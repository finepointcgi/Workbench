using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Reports;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Reports;

/// <summary>Who may see a field. <see cref="ProjectFinancials"/> values are also blanked per row the user can't read.</summary>
public enum FieldRestriction
{
    None,

    /// <summary>Finance and administrators only (cost rates, labor cost).</summary>
    Finance,

    /// <summary>Finance, administrators and the manager of the row's project or contract (contract values).</summary>
    ProjectFinancials,
}

public sealed record SourceField(string Key, string Label, ReportColumnType Type, FieldRestriction Restriction = FieldRestriction.None, string? Description = null);

/// <summary>Rows loaded from a source: values line up with <see cref="ReportSource.Fields"/>.</summary>
public sealed record SourceRows(IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>
/// A curated report-writer data source. Each one loads its rows already limited to what the user may see (the same rules
/// as the module's own permission decorators) and blanks values of restricted fields the user can't read. Users pick
/// fields and filters by key; there is no way to send SQL or an expression.
/// </summary>
public abstract class ReportSource
{
    /// <summary>Rows a report may hold; more are cut off and the result says so.</summary>
    public const int RowLimit = 50_000;

    public abstract string Key { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>The field the report's date range filters (in the database), or null when the range doesn't apply.</summary>
    public virtual string? DateField => null;

    public abstract IReadOnlyList<SourceField> Fields { get; }

    /// <summary>Why the user can't use this source at all, or null when they can.</summary>
    public virtual string? Unavailable(ReportAccessContext access) => null;

    public bool CanSee(SourceField field, ReportAccessContext access) => field.Restriction switch
    {
        FieldRestriction.Finance => access.IsFinance,
        FieldRestriction.ProjectFinancials => access.IsFinance || access.ManagesAnyProject,
        _ => true,
    };

    public abstract Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct);

    protected static SourceRows Rows<T>(List<T> loaded, Func<T, object?[]> map) =>
        new(loaded.Take(RowLimit).Select(map).ToList(), loaded.Count > RowLimit);

    protected static string? Lookup<TKey>(IReadOnlyDictionary<TKey, string> names, TKey? key)
        where TKey : struct => key is { } k && names.TryGetValue(k, out var n) ? n : null;

    public static IReadOnlyList<ReportSource> All { get; } =
        [new GlLineSource(), new TimeEntrySource(), new OdcSource(), new InvoiceSource(), new ApBillSource(), new ProjectWbsSource(), new EmployeeSource(), new Planning.GlBudgetReportSource()];

    public static ReportSource? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}

/// <summary>Posted (and reversed) journal lines with their account, org, project and WBS. Finance and Admin only, like the GL.</summary>
public sealed class GlLineSource : ReportSource
{
    public override string Key => ReportSourceKeys.GlLines;
    public override string Name => "General ledger lines";
    public override string Description => "Journal entry lines (posted and reversed entries) by account × org × project × WBS.";
    public override string? DateField => "date";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("entry", "Entry", ReportColumnType.Text),
        new("date", "Date", ReportColumnType.Date),
        new("period", "Fiscal period", ReportColumnType.Text),
        new("source", "Source", ReportColumnType.Text),
        new("status", "Status", ReportColumnType.Text),
        new("description", "Entry description", ReportColumnType.Text),
        new("account", "Account", ReportColumnType.Text),
        new("account_name", "Account name", ReportColumnType.Text),
        new("account_type", "Account type", ReportColumnType.Text),
        new("org", "Org", ReportColumnType.Text),
        new("project", "Project", ReportColumnType.Text),
        new("wbs", "WBS", ReportColumnType.Text),
        new("debit", "Debit", ReportColumnType.Money),
        new("credit", "Credit", ReportColumnType.Money),
        new("net", "Net (debit − credit)", ReportColumnType.Money),
        new("memo", "Memo", ReportColumnType.Text),
    ];

    public override string? Unavailable(ReportAccessContext access) =>
        access.IsFinance ? null : "The general ledger is for Finance and administrators.";

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var query = from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                    where e.Status != JournalStatus.Draft
                    select new { l, e };
        if (from is { } f)
        {
            query = query.Where(x => x.e.Date >= f);
        }

        if (to is { } t)
        {
            query = query.Where(x => x.e.Date <= t);
        }

        var loaded = await query.OrderBy(x => x.e.Date).ThenBy(x => x.e.Sequence).ThenBy(x => x.l.LineNumber).Take(RowLimit + 1)
            .Select(x => new { x.e.Number, x.e.Date, x.e.FiscalPeriodId, x.e.Source, x.e.Status, x.e.Description, x.l.AccountId, x.l.OrgUnitId, x.l.ProjectId, x.l.WbsElementId, x.l.Debit, x.l.Credit, x.l.Memo })
            .ToListAsync(ct);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => new { a.Number, a.Name, a.Type }, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct);
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var wbsIds = loaded.Where(x => x.WbsElementId != null).Select(x => x.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return Rows(loaded, x =>
        {
            var account = accounts.GetValueOrDefault(x.AccountId);
            return [x.Number, x.Date, periods.GetValueOrDefault(x.FiscalPeriodId), x.Source, x.Status, x.Description, account?.Number, account?.Name, account?.Type,
                Lookup(orgs, x.OrgUnitId), Lookup(projects, x.ProjectId), Lookup(wbs, x.WbsElementId), x.Debit, x.Credit, x.Debit - x.Credit, x.Memo];
        });
    }
}

/// <summary>
/// Time entries. Rows follow the hours-report rule (Timekeeper/Finance/Admin everyone, supervisors their direct reports,
/// otherwise only your own); cost rate and labor cost are Finance/Admin fields.
/// </summary>
public sealed class TimeEntrySource : ReportSource
{
    public override string Key => ReportSourceKeys.TimeEntries;
    public override string Name => "Time entries";
    public override string Description => "Hours by employee, work date and charge code, with timesheet status.";
    public override string? DateField => "work_date";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("employee_number", "Employee #", ReportColumnType.Text),
        new("employee", "Employee", ReportColumnType.Text),
        new("org", "Org", ReportColumnType.Text),
        new("work_date", "Work date", ReportColumnType.Date),
        new("period", "Fiscal period", ReportColumnType.Text),
        new("timesheet_start", "Timesheet start", ReportColumnType.Date),
        new("status", "Timesheet status", ReportColumnType.Text),
        new("project", "Project", ReportColumnType.Text),
        new("project_name", "Project name", ReportColumnType.Text),
        new("wbs", "WBS", ReportColumnType.Text),
        new("wbs_name", "WBS name", ReportColumnType.Text),
        new("charge_kind", "Charge kind", ReportColumnType.Text),
        new("labor_category", "Labor category", ReportColumnType.Text),
        new("hours", "Hours", ReportColumnType.Hours),
        new("comment", "Comment", ReportColumnType.Text),
        new("entered_on", "Entered on", ReportColumnType.Date),
        new("cost_rate", "Cost rate", ReportColumnType.Money, FieldRestriction.Finance, "The employee's hourly cost on the work date."),
        new("labor_cost", "Labor cost", ReportColumnType.Money, FieldRestriction.Finance, "Hours × the cost rate on the work date."),
    ];

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var query = from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>() on e.TimesheetId equals t.Id
                    select new { e, t };
        if (access.VisibleEmployees is { } visible)
        {
            var ids = visible.ToList();
            query = query.Where(x => ids.Contains(x.t.EmployeeId));
        }

        if (from is { } f)
        {
            query = query.Where(x => x.e.WorkDate >= f);
        }

        if (to is { } end)
        {
            query = query.Where(x => x.e.WorkDate <= end);
        }

        var loaded = await query.OrderBy(x => x.e.WorkDate).ThenBy(x => x.e.Id).Take(RowLimit + 1)
            .Select(x => new { x.e.Id, x.e.TimesheetId, x.t.EmployeeId, x.e.WorkDate, x.t.PeriodStart, x.t.Status, x.e.WbsElementId, x.e.LaborCategoryId, x.e.Hours, x.e.Comment, x.e.EnteredAtUtc })
            .ToListAsync(ct);
        var lookups = await TimeLookups.LoadAsync(db, loaded.Select(x => x.EmployeeId), loaded.Select(x => x.WbsElementId), access.IsFinance, ct, loaded.Select(x => x.TimesheetId));
        return Rows(loaded, x =>
        {
            var emp = lookups.Employees.GetValueOrDefault(x.EmployeeId);
            var w = lookups.Wbs.GetValueOrDefault(x.WbsElementId);
            var p = w is null ? null : lookups.Projects.GetValueOrDefault(w.ProjectId);
            var rate = access.IsFinance ? lookups.RateOf(x.Id, x.EmployeeId, x.WorkDate) : null;
            var cost = access.IsFinance ? lookups.CostOf(x.Id, x.EmployeeId, x.WorkDate, x.Hours) : null;
            return [emp?.EmployeeNumber, emp?.DisplayName, emp?.OrgUnitId is { } o ? lookups.Orgs.GetValueOrDefault(o) : null, x.WorkDate, lookups.Period(x.WorkDate), x.PeriodStart, x.Status,
                p?.Code, p?.Name, w?.Code, w?.Name, w?.Kind, lookups.Categories.GetValueOrDefault(x.LaborCategoryId), x.Hours, x.Comment, x.EnteredAtUtc,
                rate, cost];
        });
    }
}

/// <summary>Lookups shared by the time-based sources and standard reports.</summary>
internal sealed record TimeLookups(
    Dictionary<Guid, Employee> Employees,
    Dictionary<Guid, WbsElement> Wbs,
    Dictionary<Guid, Project> Projects,
    Dictionary<Guid, string> Categories,
    Dictionary<Guid, string> Orgs,
    List<FiscalPeriod> Periods,
    EffectiveRateTable<Guid> CostRates,
    TotalTime.LaborCostBook Book)
{
    public string? Period(DateOnly date) => Periods.FirstOrDefault(p => p.Start <= date && p.End >= date)?.Name;

    /// <summary>The entry's cost rate: the effective rate when total time accounting spread a salary over its hours, else the hourly rate on the date.</summary>
    public decimal? RateOf(Guid entryId, Guid employeeId, DateOnly date) => Book.Contains(entryId) ? Book.Rate(entryId) : CostRates.Find(employeeId, date);

    /// <summary>The entry's labor cost by the labor cost method (null without a cost rate).</summary>
    public decimal? CostOf(Guid entryId, Guid employeeId, DateOnly date, decimal hours) =>
        Book.Contains(entryId)
            ? Book.MissingRate(entryId) ? null : Book.Cost(entryId)
            : CostRates.Find(employeeId, date) is { } r ? ReportValues.Cents(r * hours) : null;

    public static async Task<TimeLookups> LoadAsync(
        IWorkbenchDbContext db, IEnumerable<Guid> employeeIds, IEnumerable<Guid> wbsIds, bool withCostRates, CancellationToken ct, IEnumerable<Guid>? timesheetIds = null)
    {
        var empIds = employeeIds.Distinct().ToList();
        var wIds = wbsIds.Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectIds = wbs.Values.Select(w => w.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().OrderBy(p => p.Start).ToListAsync(ct);
        var rates = new EffectiveRateTable<Guid>();
        if (withCostRates)
        {
            foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => empIds.Contains(r.EmployeeId)).ToListAsync(ct))
            {
                rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
            }
        }

        var book = withCostRates && timesheetIds is not null ? await TotalTime.LaborCostBook.LoadAsync(db, timesheetIds, ct) : TotalTime.LaborCostBook.Empty;
        return new TimeLookups(employees, wbs, projects, categories, orgs, periods, rates, book);
    }
}

/// <summary>ODCs and expense lines. Finance/Admin see all; a project manager their projects'; everyone else only their own.</summary>
public sealed class OdcSource : ReportSource
{
    public override string Key => ReportSourceKeys.Odcs;
    public override string Name => "ODCs and expenses";
    public override string Description => "Other direct costs and expense lines by project, WBS and type.";
    public override string? DateField => "incurred_on";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("incurred_on", "Incurred on", ReportColumnType.Date),
        new("period", "Fiscal period", ReportColumnType.Text),
        new("project", "Project", ReportColumnType.Text),
        new("project_name", "Project name", ReportColumnType.Text),
        new("wbs", "WBS", ReportColumnType.Text),
        new("type", "Type", ReportColumnType.Text),
        new("status", "Status", ReportColumnType.Text),
        new("amount", "Amount", ReportColumnType.Money),
        new("vendor", "Vendor", ReportColumnType.Text),
        new("invoice_number", "Vendor invoice", ReportColumnType.Text),
        new("employee", "Employee", ReportColumnType.Text),
        new("description", "Description", ReportColumnType.Text),
        new("unallowable", "Unallowable", ReportColumnType.Boolean),
        new("submitted_by", "Submitted by", ReportColumnType.Text),
        new("approved_by", "Approved by", ReportColumnType.Text),
        new("approved_on", "Approved on", ReportColumnType.Date),
    ];

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var query = db.Set<OdcEntry>().AsNoTracking();
        if (!access.IsFinance)
        {
            var managed = (access.FinancialProjects ?? new HashSet<Guid>()).ToList();
            var me = access.EmployeeId;
            var email = access.Email;
            query = query.Where(o => managed.Contains(o.ProjectId) || (me != null && o.EmployeeId == me) || o.SubmittedBy == email);
        }

        if (from is { } f)
        {
            query = query.Where(o => o.IncurredOn >= f);
        }

        if (to is { } t)
        {
            query = query.Where(o => o.IncurredOn <= t);
        }

        var loaded = await query.OrderBy(o => o.IncurredOn).ThenBy(o => o.Id).Take(RowLimit + 1).ToListAsync(ct);
        var lookups = await TimeLookups.LoadAsync(db, loaded.Where(o => o.EmployeeId != null).Select(o => o.EmployeeId!.Value), loaded.Select(o => o.WbsElementId), false, ct);
        return Rows(loaded, o =>
        {
            var w = lookups.Wbs.GetValueOrDefault(o.WbsElementId);
            var p = w is null ? null : lookups.Projects.GetValueOrDefault(w.ProjectId);
            return [o.IncurredOn, lookups.Period(o.IncurredOn), p?.Code, p?.Name, w?.Code, o.Type, o.Status, o.Amount, o.Vendor, o.InvoiceNumber,
                o.EmployeeId is { } e ? lookups.Employees.GetValueOrDefault(e)?.DisplayName : null, o.Description, o.IsUnallowable, o.SubmittedBy, o.ApprovedBy, o.ApprovedAtUtc];
        });
    }
}

/// <summary>Invoices. Finance/Admin see all; a project manager those of contracts their projects are on (like billing).</summary>
public sealed class InvoiceSource : ReportSource
{
    public override string Key => ReportSourceKeys.Invoices;
    public override string Name => "Invoices";
    public override string Description => "Customer invoices with totals, payments and open balance.";
    public override string? DateField => "invoice_date";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("number", "Invoice", ReportColumnType.Text),
        new("type", "Type", ReportColumnType.Text),
        new("status", "Status", ReportColumnType.Text),
        new("customer", "Customer", ReportColumnType.Text),
        new("contract", "Contract", ReportColumnType.Text),
        new("invoice_date", "Invoice date", ReportColumnType.Date),
        new("due_date", "Due date", ReportColumnType.Date),
        new("period_from", "Period from", ReportColumnType.Date),
        new("period_to", "Period to", ReportColumnType.Date),
        new("total", "Total", ReportColumnType.Money),
        new("withheld", "Withheld", ReportColumnType.Money),
        new("paid", "Paid", ReportColumnType.Money),
        new("credited", "Credited", ReportColumnType.Money),
        new("balance", "Open balance", ReportColumnType.Money),
        new("days_past_due", "Days past due", ReportColumnType.Number),
    ];

    public override string? Unavailable(ReportAccessContext access) =>
        access.IsFinance || access.FinancialContracts is not { Count: 0 } ? null : "Invoices are for Finance, administrators and project managers (for their contracts).";

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var query = db.Set<Invoice>().AsNoTracking();
        if (access.FinancialContracts is { } readable)
        {
            var ids = readable.ToList();
            query = query.Where(i => ids.Contains(i.ContractId));
        }

        if (from is { } f)
        {
            query = query.Where(i => i.InvoiceDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(i => i.InvoiceDate <= t);
        }

        var loaded = await query.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Sequence).Take(RowLimit + 1).ToListAsync(ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        return Rows(loaded, i =>
        {
            var balance = i.Balance;
            return [i.Number, i.Type, i.Status, customers.GetValueOrDefault(i.CustomerId), contracts.GetValueOrDefault(i.ContractId), i.InvoiceDate, i.DueDate, i.PeriodFrom, i.PeriodTo,
                i.Total, i.Withheld, i.AmountPaid, i.AmountCredited, balance, balance > 0 && today > i.DueDate ? today.DayNumber - i.DueDate.DayNumber : 0];
        });
    }
}

/// <summary>Vendor bills. Finance and Admin only, like the AP module.</summary>
public sealed class ApBillSource : ReportSource
{
    public override string Key => ReportSourceKeys.ApBills;
    public override string Name => "AP bills";
    public override string Description => "Vendor bills with amounts, retainage, payments and open balance.";
    public override string? DateField => "invoice_date";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("number", "Bill", ReportColumnType.Text),
        new("vendor_code", "Vendor code", ReportColumnType.Text),
        new("vendor", "Vendor", ReportColumnType.Text),
        new("invoice_number", "Vendor invoice", ReportColumnType.Text),
        new("invoice_date", "Invoice date", ReportColumnType.Date),
        new("due_date", "Due date", ReportColumnType.Date),
        new("status", "Status", ReportColumnType.Text),
        new("amount", "Amount", ReportColumnType.Money),
        new("retainage", "Retainage", ReportColumnType.Money),
        new("paid", "Paid", ReportColumnType.Money),
        new("open_balance", "Open balance", ReportColumnType.Money, Description: "Posted bills: amount less payments."),
        new("days_past_due", "Days past due", ReportColumnType.Number),
        new("description", "Description", ReportColumnType.Text),
        new("entered_by", "Entered by", ReportColumnType.Text),
    ];

    public override string? Unavailable(ReportAccessContext access) =>
        access.IsFinance ? null : "AP bills are for Finance and administrators.";

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var query = db.Set<ApBill>().AsNoTracking();
        if (from is { } f)
        {
            query = query.Where(b => b.InvoiceDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(b => b.InvoiceDate <= t);
        }

        var loaded = await query.OrderBy(b => b.InvoiceDate).ThenBy(b => b.Number).Take(RowLimit + 1).ToListAsync(ct);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => new { v.Code, v.Name }, ct);
        return Rows(loaded, b =>
        {
            var vendor = vendors.GetValueOrDefault(b.VendorId);
            var open = b.Status == ApBillStatus.Posted ? b.Amount - b.PaidAmount : 0m;
            return [b.Number, vendor?.Code, vendor?.Name, b.InvoiceNumber, b.InvoiceDate, b.DueDate, b.Status, b.Amount, b.RetainageAmount, b.PaidAmount, open,
                open > 0 && today > b.DueDate ? today.DayNumber - b.DueDate.DayNumber : 0, b.Description, b.EnteredBy];
        });
    }
}

/// <summary>
/// Projects and their WBS: the projects the user may see (like the project list); contract ceiling and funded value
/// are shown only to Finance, administrators and the managers of a project on that contract.
/// </summary>
public sealed class ProjectWbsSource : ReportSource
{
    public override string Key => ReportSourceKeys.ProjectsWbs;
    public override string Name => "Projects and WBS";
    public override string Description => "Every WBS element with its project, contract and project manager.";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("project", "Project", ReportColumnType.Text),
        new("project_name", "Project name", ReportColumnType.Text),
        new("project_status", "Project status", ReportColumnType.Text),
        new("start", "Start", ReportColumnType.Date),
        new("finish", "Finish", ReportColumnType.Date),
        new("project_manager", "Project manager", ReportColumnType.Text),
        new("contract", "Contract", ReportColumnType.Text),
        new("customer", "Customer", ReportColumnType.Text),
        new("contract_type", "Contract type", ReportColumnType.Text),
        new("wbs", "WBS", ReportColumnType.Text),
        new("wbs_name", "WBS name", ReportColumnType.Text),
        new("charge_kind", "Charge kind", ReportColumnType.Text),
        new("leaf", "Leaf", ReportColumnType.Boolean),
        new("chargeable", "Chargeable", ReportColumnType.Boolean),
        new("open", "Open", ReportColumnType.Boolean),
        new("clin", "CLIN", ReportColumnType.Text),
        new("contract_ceiling", "Contract ceiling", ReportColumnType.Money, FieldRestriction.ProjectFinancials),
        new("contract_funded", "Contract funded", ReportColumnType.Money, FieldRestriction.ProjectFinancials),
    ];

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var projects = (await db.Set<Project>().AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct)).Where(p => access.CanSeeProject(p.Id)).ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().ToListAsync(ct);
        var parents = wbs.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        var contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var clins = await db.Set<Clin>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var byProject = wbs.ToLookup(w => w.ProjectId);
        var loaded = new List<(Project P, WbsElement? W)>();
        foreach (var p in projects)
        {
            var elements = byProject[p.Id].OrderBy(w => w.Code, StringComparer.Ordinal).ToList();
            loaded.AddRange(elements.Count == 0 ? [(p, null)] : elements.Select(w => (p, (WbsElement?)w)));
        }

        return Rows(loaded, x =>
        {
            var contract = x.P.ContractId is { } c ? contracts.GetValueOrDefault(c) : null;
            var values = contract is not null && access.CanReadContract(contract.Id);
            return [x.P.Code, x.P.Name, x.P.Status, x.P.Start, x.P.Finish, x.P.ProjectManagerId is { } pm ? employees.GetValueOrDefault(pm) : null,
                contract?.ContractNumber, contract is null ? null : customers.GetValueOrDefault(contract.CustomerId), contract?.Type,
                x.W?.Code, x.W?.Name, x.W?.Kind, x.W is null ? null : !parents.Contains(x.W.Id), x.W?.IsChargeable, x.W?.IsOpen,
                x.W?.ClinId is { } clin ? clins.GetValueOrDefault(clin) : null,
                values ? contract!.CeilingValue : null, values ? contract!.FundedValue : null];
        });
    }
}

/// <summary>The employee directory (open to everyone signed in, like the people list); the current cost rate is Finance/Admin.</summary>
public sealed class EmployeeSource : ReportSource
{
    public override string Key => ReportSourceKeys.Employees;
    public override string Name => "Employees";
    public override string Description => "Employees with org, supervisor, labor category and employment dates.";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("employee_number", "Employee #", ReportColumnType.Text),
        new("employee", "Employee", ReportColumnType.Text),
        new("email", "Email", ReportColumnType.Text),
        new("org", "Org", ReportColumnType.Text),
        new("supervisor", "Supervisor", ReportColumnType.Text),
        new("labor_category", "Labor category", ReportColumnType.Text),
        new("hire_date", "Hire date", ReportColumnType.Date),
        new("termination_date", "Termination date", ReportColumnType.Date),
        new("standard_hours", "Standard hours / week", ReportColumnType.Hours),
        new("exempt", "Exempt", ReportColumnType.Boolean),
        new("active", "Active", ReportColumnType.Boolean),
        new("cost_rate", "Current cost rate", ReportColumnType.Money, FieldRestriction.Finance),
    ];

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var loaded = await db.Set<Employee>().AsNoTracking().OrderBy(e => e.EmployeeNumber).Take(RowLimit + 1).ToListAsync(ct);
        var lookups = await TimeLookups.LoadAsync(db, loaded.Select(e => e.Id), [], access.IsFinance, ct);
        var names = loaded.ToDictionary(e => e.Id, e => e.DisplayName);
        return Rows(loaded, e =>
        [
            e.EmployeeNumber, e.DisplayName, e.Email, e.OrgUnitId is { } o ? lookups.Orgs.GetValueOrDefault(o) : null,
            e.SupervisorId is { } s ? names.GetValueOrDefault(s) : null,
            e.DefaultLaborCategoryId is { } c ? lookups.Categories.GetValueOrDefault(c) : null,
            e.HireDate, e.TerminationDate, e.StandardHoursPerWeek, e.IsExempt, e.IsActiveOn(today),
            access.IsFinance ? lookups.CostRates.Find(e.Id, today) : null,
        ]);
    }
}
