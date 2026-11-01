using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Automation;
using Workbench.Application.Collaboration;
using Workbench.Application.Notifications;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Notifications;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Automation;

/// <summary>The real application and automation registrations over an in-memory database, a manual clock and a recording mailer.</summary>
internal sealed class AutomationHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public AutomationHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Db);
        services.AddSingleton<IBlobStore>(new MemoryBlobStore());
        services.AddSingleton<ICurrentUser>(User);
        services.AddWorkbenchApplication();
        services.UseBusinessEdition();
        services.AddWorkbenchAutomationHosting();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IEmailSender>(Emails);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
    }

    public IWorkbenchDbContextFactory Db { get; } = TestDb.CreateFactory();
    public TestUser User { get; } = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
    public RecordingEmailSender Emails { get; } = new();

    public ISheetService Sheets => Get<ISheetService>();
    public IItemService Items => Get<IItemService>();
    public IColumnService Columns => Get<IColumnService>();
    public ICommentService Comments => Get<ICommentService>();
    public IActivityService Activity => Get<IActivityService>();
    public IRuleService Rules => Get<IRuleService>();
    public INotificationService Notifications => Get<INotificationService>();
    public Workbench.Application.Automations.SheetAutomationRuntime Engine => Get<Workbench.Application.Automations.SheetAutomationRuntime>();
    public DigestSender Digests => Get<DigestSender>();

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public async Task<Guid> NewSheetAsync() => (await Sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;

    public async Task<ColumnDto> StatusColumnAsync(Guid sheetId) =>
        await Columns.CreateAsync(sheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Blocked", "Done"]));

    public async Task<ColumnDto> PersonColumnAsync(Guid sheetId, string name = "Owner") =>
        await Columns.CreateAsync(sheetId, new CreateColumnRequest(name, ColumnKind.Person));

    public Task<RuleDto> RuleAsync(Guid sheetId, RuleTriggerDto trigger, params RuleActionDto[] actions) =>
        Rules.CreateAsync(sheetId, new SaveRuleRequest("Rule", true, trigger, [], actions));

    /// <summary>Replaces the item's values with <paramref name="values"/> merged over its current ones.</summary>
    public async Task<ItemDto> SetAsync(Guid sheetId, ItemDto item, params (ColumnDto Column, string? Value)[] values)
    {
        var current = (await Items.ListAsync(sheetId)).First(i => i.Id == item.Id);
        var merged = new Dictionary<string, string?>(current.Values);
        foreach (var (column, value) in values)
        {
            merged[column.Id.ToString()] = value;
        }

        return await Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(current.Title, current.Start, current.Finish, current.CostpointWbsId, merged));
    }

    /// <summary>Gives <paramref name="email"/> a role in the sheet's workspace (the sheet's creator is its Owner).</summary>
    public async Task AddMemberAsync(Guid sheetId, string email, Workbench.Contracts.Identity.WorkspaceRole role)
    {
        await using var db = await Db.CreateAsync();
        var workspaceId = await db.Sheets.Where(s => s.Id == sheetId).Select(s => s.WorkspaceId).SingleAsync();
        db.Set<Workbench.Domain.Identity.WorkspaceMember>().Add(new Workbench.Domain.Identity.WorkspaceMember { WorkspaceId = workspaceId, UserEmail = email, Role = (Workbench.Domain.Identity.WorkspaceRole)(int)role });
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<RuleRunDto>> RunsAsync(Guid sheetId) => await Rules.ListRunsAsync(sheetId, limit: 500);

    public async Task<List<Notification>> AllNotificationsAsync()
    {
        await using var db = await Db.CreateAsync();
        return await db.Set<Notification>().AsNoTracking().OrderBy(n => n.CreatedAtUtc).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
