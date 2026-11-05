using Microsoft.EntityFrameworkCore;
using Workbench.Application.MyTodo;
using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Approvals;

namespace Workbench.Application.Approvals;

/// <summary>
/// "Waiting on you": one short message a business day (not one per document) to each approver with something to decide, saying how
/// many approvals wait and for how long the oldest has. Nothing waiting, no message. What waits is what the approver can decide
/// (<see cref="ApprovalEngine.ActionableAsync"/>), so a delegate hears about what they stand in for and a person who has delegated
/// everything away is left alone. Skipped on weekends, holidays, approved leave and in quiet hours; once per person per day.
/// </summary>
public sealed class ApprovalDigestCheck(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine, ReminderGate gate) : IReminderCheck
{
    public string Name => "approvals digest";

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!gate.IsReminderTime)
        {
            return 0;
        }

        var waiting = new List<(string Email, int Count, DateTime Oldest)>();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var pending = await db.Set<ApprovalTask>().AsNoTracking().Where(t => t.Status == ApprovalTaskStatus.Pending).ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return 0;
            }

            var instances = await db.Set<ApprovalInstance>().AsNoTracking()
                .Where(i => i.Status == ApprovalInstanceStatus.Pending).ToDictionaryAsync(i => i.Id, cancellationToken);
            var people = new HashSet<string>();
            foreach (var task in pending.Where(t => instances.ContainsKey(t.InstanceId)))
            {
                var eligible = await engine.EligibleAsync(db, task, cancellationToken);
                people.UnionWith(eligible);
                people.UnionWith(await engine.DelegatesOfAsync(db, eligible.ToList(), instances[task.InstanceId].DocumentType, cancellationToken));
            }

            var today = engine.Today;
            var delegatedAway = (await db.Set<ApprovalDelegation>().AsNoTracking()
                    .Where(d => d.RevokedAtUtc == null && d.StartsOn <= today && d.EndsOn >= today && d.DocumentType == null)
                    .Select(d => d.DelegatorEmail).ToListAsync(cancellationToken))
                .Select(ReminderGate.Normalize).ToHashSet();

            foreach (var email in people.Select(ReminderGate.Normalize).Distinct().Where(e => !delegatedAway.Contains(e)))
            {
                var mine = await engine.ActionableAsync(db, null, email, cancellationToken);
                if (mine.Count > 0)
                {
                    var oldest = mine.Min(m => m.Tasks.Min(t => t.Task.ActivatedAtUtc ?? m.Instance.StartedAtUtc));
                    waiting.Add((email, mine.Count, oldest));
                }
            }
        }

        if (waiting.Count == 0)
        {
            return 0;
        }

        var emails = waiting.Select(w => w.Email).ToList();
        var preferences = await gate.PreferencesAsync(emails, cancellationToken);
        var away = await gate.UnavailableTodayAsync(emails, cancellationToken);
        var hour = gate.LocalHour;
        var key = gate.Today.ToString("yyyy-MM-dd");
        var sent = 0;
        foreach (var (email, count, oldest) in waiting)
        {
            var preference = preferences[email];
            if (!preference.ApprovalsEnabled || away.Contains(email) || ReminderGate.InQuietHours(preference, hour)
                || !await gate.ClaimAsync(email, "approvals", key, cancellationToken))
            {
                continue;
            }

            await gate.SendAsync(email, preference, ReminderKinds.Approvals,
                count == 1 ? "1 approval is waiting on you" : $"{count} approvals are waiting on you",
                $"The oldest has been waiting {WaitText(engine.UtcNow - oldest)}.", "approvals");
            sent++;
        }

        return sent;
    }

    /// <summary>"under an hour", "5 hours", "1 day", "3 days".</summary>
    public static string WaitText(TimeSpan wait) =>
        wait.TotalHours < 1 ? "under an hour"
        : wait.TotalHours < 24 ? MyTodoRules.Plural((int)wait.TotalHours, "hour")
        : MyTodoRules.Plural((int)wait.TotalDays, "day");
}
