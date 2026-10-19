using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.Approvals;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

/// <summary>
/// The in-app compliance checklists. The DCAA SF 1408 one mirrors <c>docs/compliance/dcaa/sf1408.md</c>: each criterion
/// names the feature and report that answer it, runs a live check of this organization's data where a check means
/// something, and keeps the reviewer's answers (append-only; the latest is current).
/// </summary>
public sealed class ComplianceChecklistService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    SecurityEventLog securityLog,
    TimeProvider clock) : IComplianceChecklistService
{
    private delegate Task<(bool? Passed, string Detail)> LiveCheck(IWorkbenchDbContext db, DateTime nowUtc, CancellationToken ct);

    private sealed record Item(string Key, string Section, string Reference, string Criterion, string Feature, string Evidence, string? Link, bool Cpa, string? CpaNote, LiveCheck? Check);

    private const string Sf1408 = "SF 1408 criteria";
    private const string Dfars = "DFARS 252.242-7006 and DCAA expectations";

    private static readonly Item[] DcaaItems =
    [
        new("direct-indirect", Sf1408, "SF 1408 item 1; DFARS 252.242-7006(c)(2)",
            "Proper segregation of direct costs from indirect costs.",
            "Chart of accounts cost classes (direct labor, direct ODC, fringe, overhead, G&A, unallowable); every WBS charge code is Direct, Indirect or Leave; posting rules send each kind of cost to its account.",
            "Chart of accounts; trial balance by cost class; Projects and WBS (charge kinds).", "accounting/accounts", false, null,
            async (db, _, ct) =>
            {
                var direct = await db.Set<Account>().CountAsync(a => a.IsActive && (a.CostClass == CostClassification.DirectLabor || a.CostClass == CostClassification.DirectOdc), ct);
                var indirect = await db.Set<Account>().CountAsync(a => a.IsActive && (a.CostClass == CostClassification.Fringe || a.CostClass == CostClassification.Overhead
                    || a.CostClass == CostClassification.GeneralAndAdministrative || a.CostClass == CostClassification.IndirectLabor), ct);
                var indirectCodes = await db.Set<WbsElement>().CountAsync(w => w.Kind == ChargeKind.Indirect, ct);
                return (direct > 0 && indirect > 0 && indirectCodes > 0, $"{direct} direct and {indirect} indirect expense accounts; {indirectCodes} indirect charge codes");
            }),
        new("job-costing", Sf1408, "SF 1408 item 2; DFARS 252.242-7006(c)(3)",
            "Identification and accumulation of direct costs by contract.",
            "Contracts → projects → WBS charge codes. Labor (approved hours × effective cost rate), ODCs, AP bills and subcontracts post to the project and WBS they were charged to.",
            "Project status report; project financials; labor distribution.", $"reports/standard/{StandardReportKeys.ProjectStatus}", false, null,
            async (db, _, ct) =>
            {
                var onContracts = await db.Set<Project>().CountAsync(p => p.ContractId != null, ct);
                return (onContracts > 0, $"{onContracts} project(s) linked to a contract");
            }),
        new("allocation", Sf1408, "SF 1408 item 3; DFARS 252.242-7006(c)(4)",
            "A logical and consistent method for allocating indirect costs to intermediate and final cost objectives.",
            "Indirect rate pools and bases with the fringe → overhead → G&A cascade, provisional and target rates, allocation runs that post applied burden through the GL.",
            "Indirect rate variance report; indirect rates page.", "accounting/indirect-rates", true,
            "Review the pool and base structure, the cascade, consistency with your disclosure (CAS 401/402/418 and 410 if CAS-covered, DFARS 252.242-7006(c)(18)) and how variances are settled.",
            async (db, _, ct) =>
            {
                var rates = await db.Set<IndirectRate>().CountAsync(ct);
                return (rates > 0, $"{rates} indirect rates on record");
            }),
        new("gl-control", Sf1408, "SF 1408 item 4; DFARS 252.242-7006(c)(5), (c)(6)",
            "Accumulation of costs under general ledger control.",
            "Every subledger (time, ODCs, expenses, AP, payroll, billing, depreciation, allocations) posts to the general ledger through one journal service, idempotently per source document; posted entries are immutable and corrected by reversal.",
            "Trial balance; journal; project cost to GL reconciliation.", "accounting/reports", false, null,
            async (db, _, ct) =>
            {
                var posted = await db.Set<JournalEntry>().CountAsync(j => j.Status == JournalStatus.Posted, ct);
                return (posted > 0, $"{posted} posted journal entries");
            }),
        new("timekeeping", Sf1408, "SF 1408 item 5; DFARS 252.242-7006(c)(9)",
            "A timekeeping system that identifies employees' labor by intermediate or final cost objectives.",
            "Daily time entry against open, chargeable leaf charge codes only; employee certification on submit; supervisor approval (no self-approval); every change after submission kept as a revision with a reason; missing-time reminders; closed periods locked.",
            "Timesheet compliance report; timesheet history; audit log.", $"reports/standard/{StandardReportKeys.TimesheetCompliance}", true,
            "Confirm the written timekeeping policy, employee training and acknowledgements, and that floor checks / employee interviews would match the system.",
            async (db, _, ct) =>
            {
                var cadence = await db.Set<TimesheetPeriodRule>().AnyAsync(ct);
                var approved = await db.Set<Timesheet>().CountAsync(t => t.Status == TimesheetStatus.Approved, ct);
                return (approved > 0, $"Timesheet period: {(cadence ? "set by the organization" : "the default (weekly)")}; {approved} approved timesheets");
            }),
        new("labor-distribution", Sf1408, "SF 1408 item 6; DFARS 252.242-7006(c)(10)",
            "A labor distribution system that charges direct and indirect labor to the appropriate cost objectives.",
            "Labor cost is always computed (approved hours × the cost rate effective on the work date) to the charged WBS, direct or indirect; payroll registers reconcile to it.",
            "Labor distribution report; payroll reconciliation.", $"reports/standard/{StandardReportKeys.LaborDistribution}", false, null,
            async (db, _, ct) =>
            {
                var entries = await db.Set<TimeEntry>().CountAsync(ct);
                return (entries > 0, $"{entries} time entries");
            }),
        new("interim-costs", Sf1408, "SF 1408 item 7; DFARS 252.242-7006(c)(11)",
            "Interim (at least monthly) determination of costs charged to a contract through routine posting of books of account.",
            "Monthly fiscal periods; the period close checklist (time and ODCs posted, accruals, depreciation, bank reconciliation, allocations); closing locks the period against new time, ODCs and journal entries.",
            "Period close page; monthly financial statements; project status by period.", "accounting/close", false, null,
            async (db, _, ct) =>
            {
                var periods = await db.Set<FiscalPeriod>().AsNoTracking().Select(p => new { p.Start, p.End, p.Status }).ToListAsync(ct);
                var monthly = periods.Count > 0 && periods.All(p => p.End.DayNumber - p.Start.DayNumber <= 31);
                var closed = periods.Count(p => p.Status == PeriodStatus.Closed);
                return (monthly && closed > 0, $"{periods.Count} fiscal periods ({(monthly ? "monthly or shorter" : "some longer than a month")}); {closed} closed");
            }),
        new("unallowable", Sf1408, "SF 1408 item 8; DFARS 252.242-7006(c)(12); FAR 31.201-6",
            "Exclusion from costs charged to government contracts of amounts which are not allowable under FAR Part 31 or the contract.",
            "Unallowable flag with a required reason on ODCs and expense lines; unallowable accounts; unallowable cost is never burdened or billed; incurred cost schedules exclude it.",
            "Incurred cost (ICE) schedules; unallowable account ledger.", "accounting/incurred-cost", true,
            "Review the expressly unallowable cost policy, the mapping of GL accounts to FAR 31.205 cost principles, and the penalty exposure (FAR 42.709) before the first incurred cost submission.",
            async (db, _, ct) =>
            {
                var accounts = await db.Set<Account>().CountAsync(a => a.IsActive && a.CostClass == CostClassification.Unallowable, ct);
                var flagged = await db.Set<OdcEntry>().CountAsync(o => o.IsUnallowable, ct);
                return (accounts > 0, $"{accounts} unallowable accounts; {flagged} ODCs flagged unallowable");
            }),
        new("clin-units", Sf1408, "SF 1408 item 9; DFARS 252.242-7006(c)(13)",
            "Identification of costs by contract line item and by units (as if each were a separate contract), if required by the contract.",
            "CLINs and SLINs with WBS mapped to them; cost, funding and billing by CLIN.", "Contract funding and ceiling status report.",
            $"reports/standard/{StandardReportKeys.FundingStatus}", false, null,
            async (db, _, ct) =>
            {
                var clins = await db.Set<Clin>().CountAsync(ct);
                return (clins > 0 ? true : null, clins > 0 ? $"{clins} CLINs" : "No CLINs set up (mark Not applicable if your contracts don't need them)");
            }),
        new("preproduction", Sf1408, "SF 1408 item 10; DFARS 252.242-7006(c)(14)",
            "Segregation of preproduction costs from production costs (if applicable).",
            "No dedicated feature: use separate projects or WBS for preproduction work.", "Project cost reports by WBS.", "setup/projects", true,
            "Usually not applicable to services contractors; confirm and record why.", null),
        new("limitation-progress", Sf1408, "SF 1408 item 11; DFARS 252.242-7006(c)(15); FAR 52.232-20/22, 52.216-16, 52.232-16",
            "Financial information required by Limitation of Cost / Funds or Limitation on Payments clauses, and to support progress payment requests.",
            "Funded value per CLIN through funding actions; Limitation of Funds notices at 75% and 90%; ceilings with warn/block; open commitments. Progress payment requests (SF 1443) are not generated.",
            "Contract funding and ceiling status report; funding notices.", $"reports/standard/{StandardReportKeys.FundingStatus}", true,
            "If any contract has progress payments, the SF 1443 must be prepared outside Workbench from its reports; review that procedure.",
            async (db, _, ct) =>
            {
                var funded = await db.Set<Clin>().CountAsync(c => c.FundedValue > 0, ct);
                return (funded > 0 ? true : null, $"{funded} funded CLINs");
            }),
        new("pricing-data", Sf1408, "SF 1408 item 12; DFARS 252.242-7006(c)(17)",
            "The system and records produce adequate, reliable data for pricing follow-on acquisitions.",
            "Actuals by labor category, employee and WBS; effective-dated rate history; indirect rate history; earned value; report writer exports.",
            "Labor distribution; indirect rate variance; report writer.", "reports", true,
            "Confirm estimating uses these actuals (and your estimating system policy, if DFARS 252.215-7002 applies).", null),
        new("full-operation", Sf1408, "SF 1408 item 13",
            "Is the accounting system currently in full operation? (If not, which parts are operating, set up, planned or missing.)",
            "Every module above runs in Workbench; the live check looks for recent posting activity.", "Journal; period close.", "accounting/journal", true,
            "A pre-award survey asks for a system in operation. Record which modules are live and since when.",
            async (db, now, ct) =>
            {
                var since = now.AddDays(-45);
                var recent = await db.Set<JournalEntry>().CountAsync(j => j.Status == JournalStatus.Posted && j.PostedAtUtc >= since, ct);
                return (recent > 0, $"{recent} entries posted in the last 45 days");
            }),

        new("billing", Dfars, "DFARS 252.242-7006(c)(16); FAR 52.216-7",
            "Billings come from the books: cost-type invoices at provisional billing rates, nothing unallowable, nothing billed twice.",
            "Invoices generated from approved time, ODCs and milestones; each source billed once until the invoice is voided; provisional rates and rate/fee caps; invoices post to the GL and AR.",
            "Invoices; billing accrual; AR aging.", "billing", true,
            "Review provisional billing rates and the cumulative-to-date billing method, and whether direct-billing (DCAA) approval is needed.",
            async (db, _, ct) =>
            {
                var billed = await db.Set<Invoice>().CountAsync(i => i.Status == InvoiceStatus.Approved || i.Status == InvoiceStatus.Sent
                    || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid, ct);
                return (billed > 0 ? true : null, $"{billed} approved or later invoices");
            }),
        new("internal-control", Dfars, "DFARS 252.242-7006(c)(1), (c)(7), (c)(8)",
            "A sound internal control environment: approvals, segregation of duties, management review and an audit trail.",
            "Approval workflows (no self-approval), organization roles, quarterly access reviews, the append-only audit log of rates, setup, budgets and approvals, and the security event log.",
            "Audit log; access reviews; security events.", "setup/audit", true,
            "Assess segregation of duties for a small staff (who enters, approves, posts and pays) and the management review cadence.",
            async (db, _, ct) =>
            {
                var workflows = await db.Set<ApprovalWorkflow>().CountAsync(ct);
                var audit = await db.Set<AuditEvent>().CountAsync(ct);
                return (audit > 0, $"{workflows} approval workflows configured; {audit} audit events");
            }),
        new("policies", Dfars, "DFARS 252.242-7006(c)(1)",
            "Written accounting policies and procedures (accounting manual, timekeeping, unallowable costs, indirect rate structure, billing, period close).",
            "Not a software feature. docs/compliance/dcaa/sf1408.md lists what to write and which Workbench screens each procedure points to.",
            "Your accounting manual and policies.", null, true,
            "A GovCon CPA should draft or review these; DCAA asks for them first.", null),
    ];

    public async Task<ChecklistDto> GetAsync(string checklistKey, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        var items = ItemsOf(checklistKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var answers = await LatestAnswersAsync(db, checklistKey, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var result = new List<ChecklistItemDto>();
        foreach (var item in items)
        {
            result.Add(await ToDtoAsync(db, item, answers.GetValueOrDefault(item.Key), now, cancellationToken));
        }

        return new ChecklistDto(ComplianceChecklists.DcaaSf1408, "DCAA accounting system readiness (SF 1408)",
            "A self-assessment against the pre-award survey of a prospective contractor's accounting system (SF 1408) and the DFARS 252.242-7006 criteria. It doesn't replace a CPA's review or a DCAA audit.",
            "docs/compliance/dcaa/sf1408.md", result);
    }

    public async Task<ChecklistItemDto> SaveAnswerAsync(string checklistKey, string itemKey, SaveChecklistAnswerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await access.RequireAdminAsync(cancellationToken);
        var item = ItemsOf(checklistKey).FirstOrDefault(i => i.Key == itemKey) ?? throw new WorkbenchNotFoundException($"There is no checklist item '{itemKey}'.");
        if (!Enum.IsDefined(request.Status))
        {
            throw new ArgumentException("Choose a status.", nameof(request));
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is { Length: > 4000 })
        {
            throw new ArgumentException("Notes are too long (at most 4000 characters).", nameof(request));
        }

        if (request.Status is ChecklistStatus.Partial or ChecklistStatus.DoesNotMeet or ChecklistStatus.NotApplicable && notes is null)
        {
            throw new ArgumentException("Say why in the notes.", nameof(request));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = new ComplianceChecklistEntry
        {
            ChecklistKey = checklistKey, ItemKey = itemKey, Status = request.Status.ToString(), Notes = notes, ReviewedBy = access.Email, ReviewedAtUtc = now,
        };
        db.Set<ComplianceChecklistEntry>().Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ComplianceChecklistAnswered, $"{checklistKey}/{itemKey}", detail: request.Status.ToString(), cancellationToken: cancellationToken);
        return await ToDtoAsync(db, item, entry, now, cancellationToken);
    }

    private static Item[] ItemsOf(string checklistKey) =>
        checklistKey == ComplianceChecklists.DcaaSf1408 ? DcaaItems : throw new WorkbenchNotFoundException($"There is no checklist '{checklistKey}'.");

    private static async Task<Dictionary<string, ComplianceChecklistEntry>> LatestAnswersAsync(IWorkbenchDbContext db, string checklistKey, CancellationToken ct)
    {
        var rows = await db.Set<ComplianceChecklistEntry>().AsNoTracking().Where(e => e.ChecklistKey == checklistKey).ToListAsync(ct);
        return rows.GroupBy(r => r.ItemKey).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReviewedAtUtc).ThenByDescending(r => r.Id).First());
    }

    private static async Task<ChecklistItemDto> ToDtoAsync(IWorkbenchDbContext db, Item item, ComplianceChecklistEntry? answer, DateTime now, CancellationToken ct)
    {
        bool? passed = null;
        string? detail = null;
        if (item.Check is not null)
        {
            (passed, detail) = await item.Check(db, now, ct);
        }

        var status = answer is not null && Enum.TryParse<ChecklistStatus>(answer.Status, out var s) ? s : ChecklistStatus.NotReviewed;
        return new ChecklistItemDto(item.Key, item.Section, item.Reference, item.Criterion, item.Feature, item.Evidence, item.Link, item.Cpa, item.CpaNote,
            passed, detail, status, answer?.Notes, answer?.ReviewedBy, answer?.ReviewedAtUtc);
    }
}
