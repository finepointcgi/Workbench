using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application.MyWork;
using Workbench.Application.Time;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

/// <summary>
/// One source of to-dos (timesheets, expenses, approvals, ...). Register more with
/// <c>services.AddScoped&lt;IMyTodoProvider, MyProvider&gt;()</c>. A provider returns only the current user's own items
/// and items waiting on them; it reads other people's data only through permission-checked services. If it throws, the
/// page still loads and <see cref="Name"/> is listed as unavailable.
/// </summary>
public interface IMyTodoProvider
{
    /// <summary>Shown to the user when this source fails ("Couldn't load: Timesheets").</summary>
    string Name { get; }

    Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What every provider needs about this request: the user, today, and lookups several providers share (loaded once).
/// </summary>
public sealed class MyTodoContext(CurrentUserInfo user, DateOnly today, IServiceProvider services, IWorkbenchDbContextFactory dbFactory)
{
    private Task<IReadOnlyList<Project>>? _managed;
    private Task<IReadOnlyList<ApprovalInboxItemDto>>? _inbox;

    public CurrentUserInfo User => user;

    public DateOnly Today => today;

    /// <summary>The Sunday ending this Monday-to-Sunday week.</summary>
    public DateOnly WeekEnd => MyWorkCalculator.WeekEnd(today);

    public IWorkbenchDbContextFactory DbFactory => dbFactory;

    /// <summary>An optional service: null when the host doesn't register the module.</summary>
    public T? Service<T>()
        where T : class => services.GetService<T>();

    public TodoUrgency UrgencyFor(DateOnly? due, TodoUrgency whenUndated = TodoUrgency.Later) => MyTodoRules.UrgencyFor(due, today, whenUndated);

    /// <summary>Open (not closed) projects the user manages, by code.</summary>
    public Task<IReadOnlyList<Project>> ManagedProjectsAsync(CancellationToken cancellationToken) => _managed ??= LoadManagedAsync(cancellationToken);

    /// <summary>The approval inbox (empty without the approvals module).</summary>
    public Task<IReadOnlyList<ApprovalInboxItemDto>> InboxAsync(CancellationToken cancellationToken) =>
        _inbox ??= Service<IApprovalInboxService>() is { } inbox ? inbox.ListMineAsync(cancellationToken) : Task.FromResult<IReadOnlyList<ApprovalInboxItemDto>>([]);

    private async Task<IReadOnlyList<Project>> LoadManagedAsync(CancellationToken cancellationToken)
    {
        if (user.EmployeeId is not { } me)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<Project>().AsNoTracking()
            .Where(p => p.ProjectManagerId == me && p.Status != ProjectStatus.Closed)
            .OrderBy(p => p.Code)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Pure rules: urgency from a due date, and the order of the list.</summary>
public static class MyTodoRules
{
    public static TodoUrgency UrgencyFor(DateOnly? due, DateOnly today, TodoUrgency whenUndated = TodoUrgency.Later)
    {
        if (due is not { } d)
        {
            return whenUndated;
        }

        if (d < today)
        {
            return TodoUrgency.Overdue;
        }

        return d == today ? TodoUrgency.Today : d <= MyWorkCalculator.WeekEnd(today) ? TodoUrgency.ThisWeek : TodoUrgency.Later;
    }

    /// <summary>Overdue → today → this week → later; then earliest due (undated last), higher priority, title. Duplicate keys keep the first.</summary>
    public static IReadOnlyList<MyTodoItem> Sort(IEnumerable<MyTodoItem> items) =>
        items.DistinctBy(i => i.Key)
            .OrderBy(i => i.Urgency)
            .ThenBy(i => i.Due ?? DateOnly.MaxValue)
            .ThenByDescending(i => i.Priority)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>"Oct 5–7", "Oct 30–Nov 2" or "Oct 5".</summary>
    public static string Range(DateOnly start, DateOnly end) =>
        start == end ? start.ToString("MMM d")
        : start.Month == end.Month && start.Year == end.Year ? $"{start:MMM d}–{end.Day}"
        : $"{start:MMM d}–{end:MMM d}";

    public static string Plural(int count, string one, string? many = null) => count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    public static string Hours(decimal hours) => hours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class MyTodoService(
    IEnumerable<IMyTodoProvider> providers,
    ICurrentUserContext currentUser,
    IServiceProvider services,
    IWorkbenchDbContextFactory dbFactory,
    TimekeepingClock clock,
    ILogger<MyTodoService> logger) : IMyTodoService
{
    /// <summary>How many approvals the "Waiting on you" preview shows.</summary>
    public const int PreviewSize = 5;

    public async Task<MyTodoDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var user = await currentUser.GetAsync(cancellationToken);
        var context = new MyTodoContext(user, clock.Today, services, dbFactory);
        var items = new List<MyTodoItem>();
        var unavailable = new List<string>();

        // One at a time: providers share this request's scoped services, which aren't thread-safe.
        foreach (var provider in providers)
        {
            try
            {
                items.AddRange(await provider.GetAsync(context, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "To-do source {Provider} failed for {User}", provider.Name, user.Email);
                unavailable.Add(provider.Name);
            }
        }

        // A gate decision is also in the approval inbox, which has its own to-do and card: one place for it, not two.
        var inboxIds = (await TryAsync("Approvals", async () => (await context.InboxAsync(cancellationToken)).Select(i => i.DocumentId).ToHashSet(), unavailable, user.Email, cancellationToken)) ?? [];
        items.RemoveAll(i => i.Key.StartsWith("bd:gate:", StringComparison.Ordinal) && Guid.TryParse(i.Key["bd:gate:".Length..], out var gateId) && inboxIds.Contains(gateId));

        var week = await TryAsync("This week", () => MyWeek.BuildAsync(context, cancellationToken), unavailable, user.Email, cancellationToken);
        var approvals = await TryAsync("Approvals", async () =>
        {
            var inbox = await context.InboxAsync(cancellationToken);
            return inbox.Count == 0 ? null : new ApprovalsPreviewDto(inbox.Count, ApprovalTodoProvider.Oldest(inbox).Take(PreviewSize).ToList());
        }, unavailable, user.Email, cancellationToken);

        return new MyTodoDto(await NameAsync(user, cancellationToken), context.Today, MyTodoRules.Sort(items), week, approvals, unavailable.Distinct().ToList());
    }

    /// <summary>The employee record's name ("Alex Rivera"), else the sign-in name, else the email's local part.</summary>
    private async Task<string> NameAsync(CurrentUserInfo user, CancellationToken cancellationToken)
    {
        if (user.EmployeeId is { } id)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var name = await db.Set<Employee>().Where(e => e.Id == id).Select(e => e.DisplayName).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(user.DisplayName) || user.DisplayName.Contains('@') ? user.Email.Split('@')[0] : user.DisplayName;
    }

    private async Task<T?> TryAsync<T>(string name, Func<Task<T?>> load, List<string> unavailable, string email, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "To-do section {Section} failed for {User}", name, email);
            unavailable.Add(name);
            return null;
        }
    }
}

public static class MyTodoRegistration
{
    /// <summary>The service and the built-in providers. Modules add their own <see cref="IMyTodoProvider"/> anywhere.</summary>
    public static IServiceCollection AddWorkbenchMyTodo(this IServiceCollection services)
    {
        services.AddScoped<IMyTodoService, MyTodoService>();
        services.AddScoped<IMyTodoProvider, TimesheetTodoProvider>();
        services.AddScoped<IMyTodoProvider, ExpenseTodoProvider>();
        services.AddScoped<IMyTodoProvider, LeaveTodoProvider>();
        services.AddScoped<IMyTodoProvider, ApprovalTodoProvider>();
        services.AddScoped<IMyTodoProvider, ReceivingTodoProvider>();
        services.AddScoped<IMyTodoProvider, SheetTodoProvider>();
        services.AddScoped<IMyTodoProvider, EacReviewTodoProvider>();
        services.AddScoped<IMyTodoProvider, VarianceTodoProvider>();
        services.AddScoped<IMyTodoProvider, CeilingTodoProvider>();
        services.AddScoped<IMyTodoProvider, FundingTodoProvider>();
        services.AddScoped<IMyTodoProvider, WonHandoffTodoProvider>();
        services.AddScoped<IMyTodoProvider, PeriodCloseTodoProvider>();
        services.AddScoped<IMyTodoProvider, PayrollTodoProvider>();
        services.AddScoped<IMyTodoProvider, ReadyToBillTodoProvider>();
        return services;
    }
}
