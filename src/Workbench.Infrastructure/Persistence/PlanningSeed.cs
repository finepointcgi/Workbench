using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Planning;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development seed for planning, entered through the real services: the current fiscal year's adopted Original
/// budget (OPS and ENG, spread evenly), the next year's planning cycle copied from it (OPS owned by Pat, drafting; ENG
/// owned by Alex, submitted and waiting on Finance), and two recurring journals: office rent (posted monthly) and a
/// depreciation-style accrual split between the units (drafts, reversed the next period). Runs once, last.
/// </summary>
internal static class PlanningSeed
{
    private const string Finance = "pm@example.com";
    private const string EngOwner = "analyst@example.com";

    // Monthly amounts per account and org unit: budgeted fringe ≈ 31%, overhead ≈ 19%, G&A ≈ 12%.
    private static readonly (string Account, string Org, decimal Monthly)[] Plan =
    [
        ("4010", "ENG", 72_000m), ("4010", "OPS", 16_000m),
        ("5010", "ENG", 30_000m), ("5010", "OPS", 10_000m),
        ("5140", "ENG", 8_000m),
        ("6010", "ENG", 3_300m), ("6010", "OPS", 1_100m),
        ("6020", "ENG", 2_700m), ("6020", "OPS", 900m),
        ("6030", "ENG", 3_300m), ("6030", "OPS", 1_100m),
        ("6110", "ENG", 4_000m), ("6120", "OPS", 4_500m), ("6130", "ENG", 600m), ("6140", "OPS", 540m), ("6140", "ENG", 360m),
        ("6210", "OPS", 4_500m), ("6220", "OPS", 2_000m), ("6230", "OPS", 2_000m),
    ];

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<GlBudgetVersion>().AnyAsync(cancellationToken) || await db.Set<RecurringJournalTemplate>().AnyAsync(cancellationToken)
            || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var years = await db.Set<FiscalYear>().AsNoTracking().OrderBy(y => y.Start).ToListAsync(cancellationToken);
        var current = years.FirstOrDefault(y => y.Start <= today && today <= y.End);
        var next = current is null ? null : years.FirstOrDefault(y => y.Year == current.Year + 1);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Number, a => a.Id, cancellationToken);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Code, o => o.Id, cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Email, e => e.Id, cancellationToken);
        if (current is null || next is null || !orgs.ContainsKey("OPS") || !orgs.ContainsKey("ENG")
            || !employees.ContainsKey(Finance) || !employees.ContainsKey(EngOwner) || Plan.Any(p => !accounts.ContainsKey(p.Account)))
        {
            return;
        }

        var user = new SeedUser { Email = Finance };
        var kit = new Kit(services, user);

        // This year's adopted budget.
        var original = await kit.Budgets.CreateVersionAsync(new CreateBudgetVersionRequest(current.Year, BudgetVersionKind.Original, $"FY{current.Year} Original",
            "The budget adopted for the year."), cancellationToken);
        foreach (var (account, org, monthly) in Plan)
        {
            await kit.Budgets.SpreadAsync(original.Id, new SpreadBudgetRequest(accounts[account], orgs[org], monthly * 12, SpreadMethod.Evenly), cancellationToken);
        }

        await kit.Budgets.AdoptAsync(original.Id, cancellationToken);

        // Next year's planning cycle, starting from this year's budget.
        var cycle = await kit.Cycles.OpenCycleAsync(new OpenPlanningCycleRequest(next.Year, "Annual plan",
            [new CycleAssignmentInput(orgs["OPS"], employees[Finance]), new CycleAssignmentInput(orgs["ENG"], employees[EngOwner])],
            "Start from this year's budget. Plan for the new task order in ENG and a 3% raise in labor from January.",
            today.AddDays(30), $"FY{next.Year} Original", original.Id), cancellationToken);

        // Alex fills in ENG (more revenue from a new task order) and submits it: it waits on Finance.
        user.Email = EngOwner;
        var eng = cycle.Submissions.First(s => s.OrgUnitCode == "ENG");
        await kit.Cycles.SpreadSubmissionAsync(eng.Id, new SpreadSubmissionRequest(accounts["4010"], 960_000m, SpreadMethod.Evenly), cancellationToken);
        await kit.Cycles.SpreadSubmissionAsync(eng.Id, new SpreadSubmissionRequest(accounts["5010"], 396_000m, SpreadMethod.Profile,
            [8, 8, 8, 8.4m, 8.4m, 8.4m, 8.4m, 8.4m, 8.5m, 8.5m, 8.5m, 8.5m]), cancellationToken);
        await kit.Cycles.SubmitAsync(eng.Id, cancellationToken);
        user.Email = Finance;

        // Recurring journals: rent from the start of the year (posted), and an accrual drafted at each month end and reversed.
        await kit.Recurring.CreateTemplateAsync(new SaveRecurringTemplateRequest("Office rent", "Office rent", RecurrenceKind.Monthly, current.Start,
            [new RecurringLineInput(accounts["6120"], LineSide.Debit, 4_500m, OrgUnitId: orgs["OPS"], Memo: "Headquarters lease"),
             new RecurringLineInput(accounts["1010"], LineSide.Credit, 4_500m)],
            RecurringDay.FirstDay, PostAutomatically: true), cancellationToken);
        await kit.Recurring.CreateTemplateAsync(new SaveRecurringTemplateRequest("Equipment depreciation accrual", "Accrued equipment depreciation", RecurrenceKind.Monthly, current.Start,
            [new RecurringLineInput(accounts["6140"], LineSide.Debit, Percent: 60, OrgUnitId: orgs["OPS"]),
             new RecurringLineInput(accounts["6140"], LineSide.Debit, Percent: 40, OrgUnitId: orgs["ENG"]),
             new RecurringLineInput(accounts["2200"], LineSide.Credit, Percent: 100)],
            RecurringDay.LastDay, Occurrences: 12, AmountMode: RecurringAmounts.Percentage, BaseAmount: 900m, AutoReverse: true), cancellationToken);
        await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest(), cancellationToken);
    }

    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            Budgets = new GlBudgetService(factory, audit, clock);
            Cycles = new PlanningCycleService(factory, audit, new AccessControl(factory, user), new NoNotifications(), clock, new ApprovalEngine(clock));
            Recurring = new RecurringJournalService(factory, new GeneralLedger(audit, clock), audit, clock);
        }

        public GlBudgetService Budgets { get; }
        public PlanningCycleService Cycles { get; }
        public RecurringJournalService Recurring { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
