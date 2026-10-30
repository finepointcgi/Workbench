using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.Imports;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Imports;
using Workbench.Contracts.Migration;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Imports;
using Workbench.Domain.Migration;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using DomainKind = Workbench.Domain.Imports.ImportKind;
using DomainSource = Workbench.Domain.Imports.ImportSource;
using DomainStatus = Workbench.Domain.Imports.ImportJobStatus;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportSource = Workbench.Contracts.Imports.ImportSource;
using StepState = Workbench.Domain.Migration.MigrationStepState;

namespace Workbench.Application.Migration;

/// <summary>
/// The go-live checklist: the cut-over steps in order, each with the imports it uses (status from the import job
/// history), its prerequisites and data checks (counts, tie-outs of open AR/AP to the GL opening balances). Steps can
/// also be marked done or not applicable by hand. Finance or Admin (see <c>PermissionCheckingMigrationService</c>).
/// </summary>
public sealed class MigrationService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IImporter> importers,
    IReconciliationService reconciliation,
    AuditRecorder audit,
    TimeProvider clock) : IMigrationService
{
    /// <summary>An import a step uses, named by kind so a kind another branch adds (fixed assets) lights up once merged.</summary>
    private sealed record StepImport(DomainSource Source, string Kind, string Title, bool Required = true);

    private sealed record StepDefinition(string Key, string Title, string Description, string[] Prerequisites, StepImport[] Imports, string? Link = null, string? LinkText = null, bool AnyOneImport = false);

    private static readonly StepDefinition[] Steps =
    [
        new("accounts", "Chart of accounts",
            "Costpoint accounts with their type, summary (parent) account and cost class. Everything that posts needs them.",
            [], [new(DomainSource.Costpoint, nameof(DomainKind.Accounts), "Chart of accounts")]),
        new("orgs", "Organizations",
            "Costpoint organizations become org units (dotted ids nest).",
            [], [new(DomainSource.Costpoint, nameof(DomainKind.OrgUnits), "Organizations")]),
        new("people", "Employees and rates",
            "Labor categories (PLCs), employees with home org and supervisor, and effective-dated hourly cost rates.",
            ["orgs"], [
                new(DomainSource.Costpoint, nameof(DomainKind.LaborCategories), "Labor categories", Required: false),
                new(DomainSource.Costpoint, nameof(DomainKind.Employees), "Employees"),
                new(DomainSource.Costpoint, nameof(DomainKind.CostRates), "Cost rate history", Required: false),
            ]),
        new("customers", "Customers",
            "The agencies and primes you hold contracts with.",
            [], [new(DomainSource.Costpoint, nameof(DomainKind.Customers), "Customers")]),
        new("contracts", "Contracts and CLINs",
            "Contract headers, CLINs/SLINs with value and cumulative funding, and bill rates.",
            ["customers", "people"], [
                new(DomainSource.Costpoint, nameof(DomainKind.Contracts), "Contracts"),
                new(DomainSource.Costpoint, nameof(DomainKind.Clins), "CLINs and funding", Required: false),
                new(DomainSource.Costpoint, nameof(DomainKind.BillRates), "Bill rates", Required: false),
            ]),
        new("projects", "Projects and WBS",
            "Projects with their WBS tree, charge flags and CLIN mapping, then budgets.",
            ["orgs", "people", "contracts"], [
                new(DomainSource.Costpoint, nameof(DomainKind.Projects), "Projects and WBS"),
                new(DomainSource.Costpoint, nameof(DomainKind.Budgets), "Budgets", Required: false),
            ]),
        new("indirect", "Fiscal calendar and indirect rates",
            "Costpoint's fiscal years and periods, the indirect pool structure (which accounts feed fringe, overhead and G&A, and the base of each) and the provisional, actual and forward-pricing rates. Skip what Workbench already has set up.",
            ["accounts"], [
                new(DomainSource.Costpoint, nameof(DomainKind.FiscalCalendar), "Fiscal calendar", Required: false),
                new(DomainSource.Costpoint, nameof(DomainKind.IndirectPools), "Indirect pools and bases", Required: false),
                new(DomainSource.Costpoint, nameof(DomainKind.IndirectRates), "Indirect rates", Required: false),
            ], AnyOneImport: true),
        new("open-items", "Open AP and AR",
            "Vendors, then unpaid vendor invoices and customer invoices at cut-over. They're not posted: their balances come in with the opening balances.",
            ["accounts", "orgs", "contracts", "projects"], [
                new(DomainSource.Costpoint, nameof(DomainKind.Vendors), "Vendors"),
                new(DomainSource.Costpoint, nameof(DomainKind.OpenApBills), "Open AP bills"),
                new(DomainSource.Costpoint, nameof(DomainKind.OpenArInvoices), "Open AR invoices"),
            ]),
        new("opening-balances", "GL opening balances",
            "The Costpoint trial balance at the day before cut-over by account, org and project, posted as balanced opening entries.",
            ["accounts", "orgs", "projects"], [new(DomainSource.Costpoint, nameof(DomainKind.OpeningBalances), "Opening trial balance")]),
        new("fixed-assets", "Fixed assets",
            "Assets in service with cost, accumulated depreciation and in-service dates, so depreciation continues from cut-over.",
            ["accounts", "orgs"], [new(DomainSource.Costpoint, "FixedAssets", "Fixed asset opening balances")],
            Link: "assets", LinkText: "Fixed assets"),
        new("smartsheet", "Smartsheet plans",
            "Project plan sheets from the Smartsheet API or .xlsx exports, linked to each project's WBS.",
            ["projects"], [
                new(DomainSource.SmartsheetApi, nameof(DomainKind.Sheets), "Smartsheet (API)", Required: false),
                new(DomainSource.SmartsheetExcel, nameof(DomainKind.Sheets), "Smartsheet (.xlsx)", Required: false),
            ], AnyOneImport: true),
        new("parallel", "Parallel month",
            "Run the parallel month in both systems, then reconcile each Costpoint report and sign it off.",
            ["opening-balances", "open-items"], [], Link: "setup/migration/reconcile", LinkText: "Reconcile"),
    ];

    public async Task<MigrationChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await SettingsAsync(db, cancellationToken);
        var jobs = await db.Set<ImportJob>().AsNoTracking()
            .Select(j => new { j.Id, j.Source, j.Kind, j.Status, j.CreatedAtUtc, j.CreatedBy, j.ImportedAtUtc, j.CreateCount, j.ErrorCount })
            .ToListAsync(cancellationToken);
        var manual = await db.Set<MigrationStepRecord>().AsNoTracking().ToDictionaryAsync(s => s.StepKey, cancellationToken);
        var checks = await ChecksAsync(db, settings, cancellationToken);

        var result = new List<MigrationStepDto>();
        var statusByKey = new Dictionary<string, MigrationStepStatus>();
        for (var i = 0; i < Steps.Length; i++)
        {
            var step = Steps[i];
            var imports = step.Imports.Select(si =>
            {
                var available = Enum.TryParse<DomainKind>(si.Kind, out var kind) && importers.Any(x => x.Source == si.Source && x.Kind == kind);
                var latest = available ? jobs.Where(j => j.Source == si.Source && j.Kind == kind).MaxBy(j => j.CreatedAtUtc) : null;
                return new MigrationImportDto((ImportSource)(int)si.Source, si.Kind, si.Title, si.Required, available,
                    available ? $"setup/import?source={si.Source}&kind={si.Kind}" : null,
                    latest?.Id, latest is null ? null : (ImportJobStatus)(int)latest.Status, latest?.ImportedAtUtc ?? latest?.CreatedAtUtc, latest?.CreatedBy,
                    latest?.CreateCount, latest?.ErrorCount);
            }).ToList();
            bool Imported(MigrationImportDto m) => Enum.TryParse<DomainKind>(m.Kind, out var k)
                && jobs.Any(j => j.Source == (DomainSource)(int)m.Source && j.Kind == k && j.Status == DomainStatus.Imported);

            var stepChecks = checks.GetValueOrDefault(step.Key) ?? [];
            var blockedBy = step.Prerequisites.Where(p => statusByKey.GetValueOrDefault(p) is not (MigrationStepStatus.Done or MigrationStepStatus.Skipped))
                .Select(p => Steps.First(s => s.Key == p).Title)
                .Concat(stepChecks.Where(c => c.Blocking && !c.Passed).Select(c => c.Title)).ToList();
            manual.TryGetValue(step.Key, out var record);

            MigrationStepStatus status;
            if (record?.State == StepState.Done)
            {
                status = MigrationStepStatus.Done;
            }
            else if (record?.State == StepState.Skipped)
            {
                status = MigrationStepStatus.Skipped;
            }
            else if (step.Key == "parallel")
            {
                status = await ParallelStatusAsync(settings, cancellationToken);
                status = status == MigrationStepStatus.NotStarted && blockedBy.Count > 0 ? MigrationStepStatus.Blocked : status;
            }
            else
            {
                var required = imports.Where(m => m.Required && m.Available).ToList();
                var done = step.AnyOneImport ? imports.Any(Imported) : required.Count > 0 && required.All(Imported);
                var unavailable = imports.Where(m => m.Required).All(m => !m.Available) && !step.AnyOneImport;
                status = done ? MigrationStepStatus.Done
                    : imports.Any(m => m.Required && m.LastJobStatus == ImportJobStatus.Failed) ? MigrationStepStatus.Failed
                    : blockedBy.Count > 0 ? MigrationStepStatus.Blocked
                    : imports.Any(m => m.LastJobId is not null) ? MigrationStepStatus.InProgress
                    : unavailable ? MigrationStepStatus.NotStarted
                    : MigrationStepStatus.NotStarted;
            }

            statusByKey[step.Key] = status;
            result.Add(new MigrationStepDto(step.Key, i + 1, step.Title, step.Description, status,
                step.Prerequisites.Select(p => Steps.First(s => s.Key == p).Title).ToList(), status is MigrationStepStatus.Done or MigrationStepStatus.Skipped ? [] : blockedBy,
                imports, stepChecks, record is null ? null : (Contracts.Migration.MigrationStepState)(int)record.State, record?.Note, record?.UpdatedBy, record?.UpdatedAtUtc,
                step.Link, step.LinkText));
        }

        return new MigrationChecklistDto(settings, result, checks.GetValueOrDefault("") ?? []);
    }

    private async Task<MigrationStepStatus> ParallelStatusAsync(MigrationSettingsDto settings, CancellationToken ct)
    {
        if (settings.ParallelPeriodId is not { } period)
        {
            return MigrationStepStatus.NotStarted;
        }

        var status = await reconciliation.GetStatusAsync(period, ct);
        return status.All(s => s.LatestSignedOff is not null) ? MigrationStepStatus.Done
            : status.Any(s => s.LatestRun is not null) ? MigrationStepStatus.InProgress
            : MigrationStepStatus.NotStarted;
    }

    public async Task<MigrationSettingsDto> SaveSettingsAsync(SaveMigrationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (request.ParallelPeriodId is { } periodId)
        {
            var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Fiscal period", periodId);
            if (request.CutoverDate is { } cutover && period.End < cutover)
            {
                throw new ArgumentException($"The parallel month {period.Name} ends before the cut-over date {cutover:yyyy-MM-dd}.");
            }
        }

        var settings = await db.Set<MigrationSettings>().FirstOrDefaultAsync(cancellationToken);
        var before = settings is null ? null : new { settings.CutoverDate, settings.ParallelPeriodId };
        if (settings is null)
        {
            settings = new MigrationSettings();
            db.Set<MigrationSettings>().Add(settings);
        }

        settings.CutoverDate = request.CutoverDate;
        settings.ParallelPeriodId = request.ParallelPeriodId;
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(MigrationSettings), settings.Id, "Updated", before: before, after: new { settings.CutoverDate, settings.ParallelPeriodId });
        await db.SaveChangesAsync(cancellationToken);
        return await SettingsAsync(db, cancellationToken);
    }

    public async Task<MigrationChecklistDto> SetStepAsync(string stepKey, SetMigrationStepRequest request, CancellationToken cancellationToken = default)
    {
        if (Steps.All(s => s.Key != stepKey))
        {
            throw new WorkbenchNotFoundException($"Checklist step '{stepKey}' not found.");
        }

        var note = request.Note?.Trim();
        if (request.State == Contracts.Migration.MigrationStepState.Skipped && string.IsNullOrEmpty(note))
        {
            throw new ArgumentException("Say why the step doesn't apply.");
        }

        if (note is { Length: > 1000 })
        {
            throw new ArgumentException("The note can be at most 1000 characters.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var record = await db.Set<MigrationStepRecord>().FirstOrDefaultAsync(s => s.StepKey == stepKey, cancellationToken);
            var before = record is null ? null : new { record.State, record.Note };
            if (request.State == Contracts.Migration.MigrationStepState.Pending)
            {
                if (record is not null)
                {
                    db.Set<MigrationStepRecord>().Remove(record);
                }
            }
            else
            {
                if (record is null)
                {
                    record = new MigrationStepRecord { StepKey = stepKey, UpdatedBy = audit.Actor };
                    db.Set<MigrationStepRecord>().Add(record);
                }

                record.State = (StepState)(int)request.State;
                record.Note = string.IsNullOrEmpty(note) ? null : note;
                record.UpdatedBy = audit.Actor;
                record.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            }

            audit.Record(db, nameof(MigrationStepRecord), record?.Id ?? Guid.Empty, $"Step {stepKey}", before: before, after: new { request.State, Note = note });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetChecklistAsync(cancellationToken);
    }

    private static async Task<MigrationSettingsDto> SettingsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<MigrationSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            return new MigrationSettingsDto(null, null, null, null, null);
        }

        var period = settings.ParallelPeriodId is { } id ? await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) : null;
        return new MigrationSettingsDto(settings.CutoverDate, settings.ParallelPeriodId, period?.Name, settings.UpdatedBy, settings.UpdatedAtUtc);
    }

    /// <summary>Data checks by step key ("" = overall readiness).</summary>
    private static async Task<Dictionary<string, List<MigrationCheckDto>>> ChecksAsync(IWorkbenchDbContext db, MigrationSettingsDto settings, CancellationToken ct)
    {
        var checks = new Dictionary<string, List<MigrationCheckDto>>();
        void Add(string step, MigrationCheckDto check) => (checks.TryGetValue(step, out var list) ? list : checks[step] = []).Add(check);

        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(ct);
        var openingDate = settings.OpeningBalanceDate;
        Add("", new MigrationCheckDto("Cut-over date", settings.CutoverDate is not null,
            settings.CutoverDate is { } c ? $"Workbench is the system of record from {c:yyyy-MM-dd}; opening balances are dated {openingDate:yyyy-MM-dd}." : "Set the cut-over date."));
        var openingPeriod = openingDate is { } od ? periods.FirstOrDefault(p => p.Start <= od && od <= p.End) : null;
        Add("", new MigrationCheckDto("Fiscal calendar covers the opening balance date", openingPeriod is not null,
            openingDate is null ? "Set the cut-over date first." : openingPeriod is null ? $"No fiscal period covers {openingDate:yyyy-MM-dd}; set up that fiscal year." : $"Period {openingPeriod.Name}."));
        Add("", new MigrationCheckDto("Parallel month chosen", settings.ParallelPeriodId is not null,
            settings.ParallelPeriodName is { } pn ? $"{pn} is run in both systems and reconciled." : "Choose the month you'll run in parallel with Costpoint."));

        var detailAccounts = await db.Set<Account>().AsNoTracking().CountAsync(a => a.IsActive && !db.Set<Account>().Any(c => c.ParentId == a.Id), ct);
        Add("accounts", new MigrationCheckDto("Detail accounts", detailAccounts > 0, $"{detailAccounts} active detail account(s)."));
        var orgs = await db.Set<OrgUnit>().AsNoTracking().CountAsync(ct);
        Add("orgs", new MigrationCheckDto("Org units", orgs > 0, $"{orgs} org unit(s)."));
        var employees = await db.Set<Employee>().AsNoTracking().Select(e => e.Id).ToListAsync(ct);
        var withRates = await db.Set<EmployeeCostRate>().AsNoTracking().Select(r => r.EmployeeId).Distinct().CountAsync(ct);
        Add("people", new MigrationCheckDto("Employees", employees.Count > 0, $"{employees.Count} employee(s)."));
        Add("people", new MigrationCheckDto("Every employee has a cost rate", withRates >= employees.Count,
            withRates >= employees.Count ? "Labor can be costed." : $"{employees.Count - withRates} employee(s) have no cost rate; their labor would cost 0."));
        var customers = await db.Set<Customer>().AsNoTracking().CountAsync(ct);
        Add("customers", new MigrationCheckDto("Customers", customers > 0, $"{customers} customer(s)."));
        var contracts = await db.Set<Contract>().AsNoTracking().CountAsync(ct);
        Add("contracts", new MigrationCheckDto("Contracts", contracts > 0, $"{contracts} contract(s)."));
        var projects = await db.Set<Project>().AsNoTracking().CountAsync(ct);
        Add("projects", new MigrationCheckDto("Projects", projects > 0, $"{projects} project(s)."));

        Add("opening-balances", new MigrationCheckDto("Cut-over date set", openingDate is not null,
            openingDate is { } d ? $"Opening balances are posted on {d:yyyy-MM-dd}." : "Set the cut-over date above: opening balances are dated the day before.", Blocking: true));
        if (openingDate is { } date)
        {
            Add("opening-balances", new MigrationCheckDto("Opening balance period is open", openingPeriod is { Status: PeriodStatus.Open },
                openingPeriod is null ? $"No fiscal period covers {date:yyyy-MM-dd}." : openingPeriod.Status == PeriodStatus.Open ? $"{openingPeriod.Name} is open." : $"{openingPeriod.Name} is closed; reopen it to post.",
                Blocking: true));

            // Open AR and AP are subledger detail: they must tie to the GL balances the opening entry brings in.
            var lines = from l in db.Set<JournalLine>().AsNoTracking()
                        join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                        join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                        where e.Status != JournalStatus.Draft && e.Date <= date
                        select new { a.Number, l.Debit, l.Credit };
            var gl = (await lines.ToListAsync(ct)).GroupBy(l => l.Number).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
            var ar = (await db.Set<Invoice>().AsNoTracking().Where(i => i.InvoiceDate <= date).ToListAsync(ct)).Sum(i => i.Balance);
            var glAr = gl.GetValueOrDefault(BillingLedger.ArBilled);
            Add("open-items", new MigrationCheckDto("Open AR ties to the GL", ar == glAr,
                $"Open invoices {ar:N2}; GL account {BillingLedger.ArBilled} at {date:yyyy-MM-dd} {glAr:N2}{(ar == glAr ? "." : $" (difference {glAr - ar:N2}).")}"));
            var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted && b.InvoiceDate <= date).ToListAsync(ct);
            var payablesAccount = await db.Set<PostingRule>().AsNoTracking().Where(r => r.Kind == PostingRuleKind.AccountsPayable && r.Key == "")
                .Join(db.Set<Account>(), r => r.AccountId, a => a.Id, (r, a) => a.Number).FirstOrDefaultAsync(ct) ?? "2010";
            var ap = bills.Sum(b => b.Balance);
            var glAp = -gl.GetValueOrDefault(payablesAccount);
            Add("open-items", new MigrationCheckDto("Open AP ties to the GL", ap == glAp,
                $"Open bills {ap:N2} (excluding retainage); GL account {payablesAccount} at {date:yyyy-MM-dd} {glAp:N2}{(ap == glAp ? "." : $" (difference {glAp - ap:N2}).")}"));
            var retainage = bills.Sum(b => b.RetainageAmount);
            if (retainage != 0 || gl.ContainsKey("2020"))
            {
                var glRetainage = -gl.GetValueOrDefault("2020");
                Add("open-items", new MigrationCheckDto("Retainage ties to the GL", retainage == glRetainage,
                    $"Retainage on open bills {retainage:N2}; GL account 2020 {glRetainage:N2}."));
            }

            var total = gl.Values.Sum();
            Add("opening-balances", new MigrationCheckDto("Ledger balances at the opening date", total == 0,
                total == 0 ? $"Debits equal credits through {date:yyyy-MM-dd}." : $"Out of balance by {total:N2}."));
        }

        return checks;
    }
}
