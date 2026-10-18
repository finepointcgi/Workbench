using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Approvals;

/// <summary>
/// Escalation: a pending task whose step has "remind after N business days" reminds its approvers (and their
/// delegates) every N business days while it waits, and, when the step escalates, tells the approvers' supervisors
/// too. Idempotent: a task is reminded again only N business days after the last reminder.
/// </summary>
public sealed class ApprovalReminderCheck(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine, INotificationPublisher publisher)
{
    public const string ReminderKind = "approval.reminder";
    public const string EscalationKind = "approval.escalated";

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var sent = new List<(IReadOnlyList<string> To, string Kind, string Title, string Body, string Link)>();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var now = engine.UtcNow;
            var tasks = await db.Set<ApprovalTask>()
                .Where(t => t.Status == ApprovalTaskStatus.Pending && t.RemindAfterBusinessDays != null)
                .ToListAsync(cancellationToken);
            foreach (var task in tasks)
            {
                var since = task.LastRemindedAtUtc ?? task.ActivatedAtUtc;
                if (since is null || BusinessDays.Between(since.Value, now) < task.RemindAfterBusinessDays)
                {
                    continue;
                }

                var instance = await db.Set<ApprovalInstance>().FirstAsync(i => i.Id == task.InstanceId, cancellationToken);
                if (instance.Status != ApprovalInstanceStatus.Pending)
                {
                    continue;
                }

                var waiting = BusinessDays.Between(task.ActivatedAtUtc ?? since.Value, now);
                var approvers = (await engine.EligibleAsync(db, task, cancellationToken)).ToList();
                approvers.AddRange(await engine.DelegatesOfAsync(db, approvers, instance.DocumentType, cancellationToken));
                var label = ApprovalDocumentTypes.Label(instance.DocumentType);
                sent.Add((approvers.Distinct().ToList(), ReminderKind, $"Reminder: {instance.Title} is waiting for your approval",
                    $"{label}, {task.StepName}: waiting {waiting} business day(s).", instance.Link));
                engine.AddAction(db, instance, task, "Reminded", "system", null, string.Join(", ", approvers.Distinct()));

                if (task.EscalateToSupervisor)
                {
                    var assignees = ApprovalEngine.Split(task.AssigneesCsv).ToList();
                    var supervisorIds = await db.Set<Employee>().AsNoTracking()
                        .Where(e => assignees.Contains(e.Email.ToLower()) && e.SupervisorId != null).Select(e => e.SupervisorId!.Value).ToListAsync(cancellationToken);
                    var supervisors = (await db.Set<Employee>().AsNoTracking().Where(e => supervisorIds.Contains(e.Id)).Select(e => e.Email).ToListAsync(cancellationToken))
                        .Select(ApprovalEngine.Normalize).Where(e => e != instance.SubmitterEmail).Distinct().ToList();
                    if (supervisors.Count > 0)
                    {
                        sent.Add((supervisors, EscalationKind, $"Escalated: {instance.Title} has waited {waiting} business day(s)",
                            $"{label}, {task.StepName}, waiting on {string.Join(", ", assignees)}.", instance.Link));
                        engine.AddAction(db, instance, task, "Escalated", "system", null, string.Join(", ", supervisors));
                    }
                }

                task.LastRemindedAtUtc = now;
                task.RemindersSent++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var (to, kind, title, body, link) in sent)
        {
            await publisher.PublishAsync(to, kind, title, body, link);
        }

        return sent.Count;
    }
}

/// <summary>Runs <see cref="ApprovalReminderCheck"/> hourly for every tenant.</summary>
public sealed class ApprovalReminderBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<ApprovalReminderBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<ApprovalReminderCheck>().RunAsync(ct);
                    if (sent > 0)
                    {
                        logger.LogInformation("Sent {Count} approval reminders", sent);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Approval reminder check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
