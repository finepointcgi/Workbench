using Microsoft.EntityFrameworkCore;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

public static class HrTodoCategories
{
    public const string Hr = "hr";
}

/// <summary>Onboarding and offboarding tasks waiting on the user (assigned to them, or to a role they hold).</summary>
public sealed class ChecklistTodoProvider : IMyTodoProvider
{
    public string Name => "Onboarding tasks";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IOnboardingService>() is not { } onboarding)
        {
            return [];
        }

        return (await onboarding.ListMyTasksAsync(cancellationToken))
            .Select(x => new MyTodoItem(
                $"hr-task:{x.Task.Id}", HrTodoCategories.Hr,
                $"{(x.Kind == ChecklistKind.Onboarding ? "Onboarding" : "Offboarding")} {x.EmployeeName}: {x.Task.Title}",
                $"{ChecklistLabels.Label(x.Task.Owner)} task · " + (x.Task.DueDate < context.Today ? $"was due {x.Task.DueDate:ddd MMM d}" : $"due {x.Task.DueDate:ddd MMM d}"),
                context.UrgencyFor(x.Task.DueDate), x.Task.DueDate, $"hr/onboarding/{x.Task.ChecklistId}", "Open checklist", Priority: 1))
            .ToList();
    }
}

/// <summary>
/// Clearances and certifications expiring within 60 days: the user's own (one item each), their reports' (for supervisors)
/// and, for HR, one summary item for everyone else.
/// </summary>
public sealed class CredentialExpiryTodoProvider : IMyTodoProvider
{
    public const int WithinDays = 60;

    public string Name => "Clearances and certifications";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IHrPersonnelService>() is not { } personnel)
        {
            return [];
        }

        var expiring = await personnel.ListExpiringCredentialsAsync(WithinDays, cancellationToken);
        var me = context.User.EmployeeId;
        var items = new List<MyTodoItem>();
        foreach (var c in expiring.Where(c => c.EmployeeId == me))
        {
            items.Add(Item(context, c, $"Your {Describe(c)} {Expires(c, context.Today)}", "Renew it and send HR the new dates", "hr/me"));
        }

        var others = expiring.Where(c => c.EmployeeId != me).ToList();
        if (context.User.Can(Permission.HrRecordsManage))
        {
            if (others.Count > 0)
            {
                var first = others.Min(c => c.ExpiresOn)!.Value;
                items.Add(new MyTodoItem("hr-expiring", HrTodoCategories.Hr, $"{others.Count} clearance(s) and certification(s) expire within {WithinDays} days",
                    $"The first {(first < context.Today ? "expired" : "expires")} {first:MMM d}", context.UrgencyFor(first), first, "hr/reports?tab=expirations", "Review"));
            }
        }
        else
        {
            items.AddRange(others.Select(c => Item(context, c, $"{c.EmployeeName}'s {Describe(c)} {Expires(c, context.Today)}", "Plan for the renewal or reinvestigation",
                $"hr/employees/{c.EmployeeId}")));
        }

        return items;
    }

    private static MyTodoItem Item(MyTodoContext context, CredentialDto c, string title, string reason, string link) =>
        new($"hr-credential:{c.Id}", HrTodoCategories.Hr, title, reason, context.UrgencyFor(c.ExpiresOn), c.ExpiresOn, link, "Open");

    private static string Describe(CredentialDto c) =>
        c.Kind == CredentialKind.SecurityClearance && c.ClearanceLevel is { } level ? $"{HrLabels.Label(level)} clearance" : c.Name;

    private static string Expires(CredentialDto c, DateOnly today) =>
        c.ExpiresOn < today ? $"expired {c.ExpiresOn:MMM d}" : $"expires {c.ExpiresOn:MMM d}";
}

/// <summary>
/// Benefits enrollment: open enrollment closing (until the user has made or waived every medical/dental/vision choice),
/// a new hire's 30 days, and a verified life event's window. HR also sees life events waiting to be verified.
/// </summary>
public sealed class BenefitsTodoProvider : IMyTodoProvider
{
    public string Name => "Benefits";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IBenefitService>() is not { } benefits)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        if (context.User.EmployeeId is not null)
        {
            var mine = await benefits.GetMineAsync(cancellationToken);
            foreach (var o in mine.Opportunities)
            {
                if (o.Source == ElectionSource.OpenEnrollment)
                {
                    var core = new[] { BenefitPlanType.Medical, BenefitPlanType.Dental, BenefitPlanType.Vision }
                        .Where(t => mine.AvailablePlans.Any(p => p.Type == t)).ToList();
                    if (core.All(mine.ElectedThisWindow.Contains))
                    {
                        continue;
                    }

                    items.Add(new MyTodoItem($"hr-oe:{o.WindowId}", HrTodoCategories.Hr, $"Open enrollment closes {o.EndsOn:MMM d}",
                        $"Choose or waive your benefits for coverage from {o.CoverageStart:MMM d, yyyy}", context.UrgencyFor(o.EndsOn), o.EndsOn, "hr/benefits/me", "Enroll"));
                }
                else if (o.Source == ElectionSource.NewHire && mine.Elections.Count == 0)
                {
                    items.Add(new MyTodoItem("hr-newhire", HrTodoCategories.Hr, "Enroll in benefits", $"New hire enrollment ends {o.EndsOn:MMM d}",
                        context.UrgencyFor(o.EndsOn), o.EndsOn, "hr/benefits/me", "Enroll"));
                }
                else if (o.Source == ElectionSource.LifeEvent)
                {
                    items.Add(new MyTodoItem($"hr-le:{o.LifeEventId}", HrTodoCategories.Hr, $"Update your benefits for {o.Label}",
                        $"Changes are open until {o.EndsOn:MMM d}", context.UrgencyFor(o.EndsOn), o.EndsOn, "hr/benefits/me", "Change benefits"));
                }
            }
        }

        if (context.User.Can(Permission.HrBenefitsManage))
        {
            var pending = (await benefits.ListPendingLifeEventsAsync(cancellationToken)).Where(e => e.EmployeeId != context.User.EmployeeId).ToList();
            if (pending.Count > 0)
            {
                var due = DateOnly.FromDateTime(pending.Min(e => e.ReportedAtUtc)).AddDays(3);
                items.Add(new MyTodoItem("hr-le-verify", HrTodoCategories.Hr, pending.Count == 1 ? "Verify 1 life event" : $"Verify {pending.Count} life events", "Employees can change benefits once you verify them",
                    context.UrgencyFor(due), due, "hr/benefits?tab=life-events", "Review"));
            }
        }

        return items;
    }
}

/// <summary>Personnel change requests waiting for HR (they're also in the approvals inbox; this links straight to them).</summary>
public sealed class HrRequestTodoProvider : IMyTodoProvider
{
    public string Name => "Personnel requests";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IHrPersonnelService>() is not { } personnel)
        {
            return [];
        }

        var requests = await personnel.ListChangeRequestsAsync(pendingOnly: true, cancellationToken);
        var mine = requests.Where(r => r.EmployeeId == context.User.EmployeeId).ToList();
        var items = mine.Select(r => new MyTodoItem($"hr-req:{r.Id}", HrTodoCategories.Hr, "Your record change is waiting for HR", r.Summary,
            TodoUrgency.Later, null, "hr/me", "View")).ToList();
        return items;
    }
}
