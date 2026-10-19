using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Financials;
using Workbench.Application.Leave;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>
/// The payroll run workflow: pick a pay group and period, preview approved hours (DCAA: pay comes from approved time, so
/// missing and unapproved timesheets are flagged and their hours left out), export the provider's hours file, import its
/// payroll register, reconcile register gross with labor at pay rates, and post the payroll journal. Posting is
/// idempotent per register (the register id is the journal entry's source id); voiding reverses it.
/// </summary>
public sealed class PayrollRunService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    GeneralLedger ledger,
    PayrollProviders providers,
    TimeProvider clock,
    SecurityEventLog? securityLog = null) : IPayrollRunService
{
    public const string AuditEntityType = nameof(PayrollRun);
    public const int MaxRegisterBytes = 5 * 1024 * 1024;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PayrollRunDto>> ListAsync(Guid? payGroupId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = await db.Set<PayrollRun>().AsNoTracking().Where(r => payGroupId == null || r.PayGroupId == payGroupId).ToListAsync(cancellationToken);
        var dtos = await MapAsync(db, runs, cancellationToken);
        return dtos.OrderByDescending(r => r.PeriodEnd).ThenByDescending(r => r.CreatedAtUtc).ToList();
    }

    public async Task<PayrollRunDto> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await MapAsync(db, [await LoadAsync(db, runId, cancellationToken)], cancellationToken))[0];
    }

    public async Task<PayrollRunDto> StartAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == payGroupId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Pay group", payGroupId);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payPeriodId && p.PayGroupId == group.Id, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Pay period", payPeriodId);
        if (!providers.Get(group.ProviderKey).IsSupported)
        {
            throw new NotSupportedException($"{providers.NameOf(group.ProviderKey)} payroll isn't supported yet (coming soon).");
        }

        var run = await db.Set<PayrollRun>().AsNoTracking()
            .Where(r => r.PayGroupId == group.Id && r.PayPeriodId == period.Id && r.Status != PayrollRunStatus.Voided)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            run = new PayrollRun { PayGroupId = group.Id, PayPeriodId = period.Id, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
            db.Set<PayrollRun>().Add(run);
            audit.Record(db, AuditEntityType, run.Id, "Started", after: new { group.Code, period.Start, period.End });
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await MapAsync(db, [run], cancellationToken))[0];
    }

    public async Task<HoursPreviewDto> PreviewHoursAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadAsync(db, runId, cancellationToken);
        var data = await PayrollData.LoadAsync(db, run, cancellationToken);
        return new HoursPreviewDto(run.Id, data.Period.Start, data.Period.End, data.Employees.Select(e => e.Hours).ToList());
    }

    public async Task<PayrollFileDto> ExportHoursAsync(Guid runId, ExportHoursRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<PayrollRun>().Include(r => r.ExportLines).FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Payroll run", runId);
        if (run.Status is not (PayrollRunStatus.Draft or PayrollRunStatus.Exported))
        {
            throw new ArgumentException(run.Status == PayrollRunStatus.Voided
                ? "This run is voided. Start a new run for the period."
                : "The register is already imported, so the hours can't be exported again. Void the run to start over.");
        }

        var data = await PayrollData.LoadAsync(db, run, cancellationToken);
        var flagged = data.Employees.Where(e => e.Hours.Issues.Count > 0).ToList();
        if (flagged.Count > 0 && request is not { AcknowledgeIssues: true })
        {
            throw new ArgumentException($"{flagged.Count} employee(s) have missing or unapproved time or setup problems ({string.Join(", ", flagged.Select(f => f.Hours.EmployeeName))}). "
                + "Only approved hours are exported; acknowledge this to export anyway.");
        }

        var provider = providers.Get(data.Group.ProviderKey);
        var lines = new List<(Guid EmployeeId, string Category, string Code, decimal Hours, PayrollHoursLine Line)>();
        foreach (var e in data.Employees.Where(e => e.Profile.ProviderEmployeeId is not null))
        {
            var id = e.Profile.ProviderEmployeeId!;
            void Add(string category, decimal hours)
            {
                if (hours == 0)
                {
                    return;
                }

                var code = EarningCodes.Resolve(data.EarningCodes, category).Code;
                lines.Add((e.Employee.Id, category, code, hours, new PayrollHoursLine(id, e.Employee.DisplayName, code, hours, e.CostCenter)));
            }

            if (e.PayType == PayType.Hourly || data.Settings?.ExportSalariedRegularHours == true)
            {
                Add(EarningCodes.Regular, e.Split.Regular);
            }

            Add(EarningCodes.Overtime, e.Split.Overtime);
            foreach (var (code, hours) in e.Split.Leave)
            {
                Add(code, hours);
            }
        }

        var file = await provider.ExportHoursAsync(db, new PayrollHoursExport(data.Group.CompanyCode, data.Group.Code, data.Period.Start, data.Period.End, data.Period.CheckDate,
            lines.Select(l => l.Line).ToList()), cancellationToken);
        db.Set<PayrollExportLine>().RemoveRange(run.ExportLines);
        run.ExportLines.Clear();
        foreach (var l in lines)
        {
            var line = new PayrollExportLine { RunId = run.Id, EmployeeId = l.EmployeeId, Category = l.Category, EarningCode = l.Code, Hours = l.Hours };
            run.ExportLines.Add(line);
            db.Set<PayrollExportLine>().Add(line);
        }

        var before = new { Status = run.Status.ToString() };
        run.Status = PayrollRunStatus.Exported;
        run.ExportedAtUtc = UtcNow;
        run.ExportedBy = audit.Actor;
        run.ExportFileName = file.FileName;
        run.ExportContent = file.Content;
        audit.Record(db, AuditEntityType, run.Id, "Hours exported", before,
            new { Status = run.Status.ToString(), file.FileName, Lines = lines.Count, Hours = lines.Sum(l => l.Hours), Flagged = flagged.Count },
            flagged.Count > 0 ? $"Exported with {flagged.Count} flagged employee(s)" : null);
        await db.SaveChangesAsync(cancellationToken);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(PayrollSecurityEvents.HoursExported, $"payroll run {data.Group.Code} {data.Period.End:yyyy-MM-dd}",
                detail: $"{lines.Count} lines", cancellationToken: cancellationToken);
        }

        return new PayrollFileDto(file.FileName, file.ContentType, file.Content);
    }

    public async Task<RegisterImportResultDto> ImportRegisterAsync(Guid runId, ImportRegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("The register file is empty.");
        }

        if (Encoding.UTF8.GetByteCount(request.Content) > MaxRegisterBytes)
        {
            throw new ArgumentException("The register file can be at most 5 MB.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadTrackedAsync(db, runId, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content.ReplaceLineEndings("\n").Trim())));
        if (run.RegisterId is { } currentId
            && await db.Set<PayrollRegister>().AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == currentId, cancellationToken) is { } current
            && current.ContentHash == hash && current.Status == PayrollRegisterStatus.Active)
        {
            return new RegisterImportResultDto(run.Id, current.Id, true, current.Lines.OrderBy(l => l.EmployeeName).Select(ToDto).ToList(), ["This register was already imported."]);
        }

        if (run.Status is not (PayrollRunStatus.Exported or PayrollRunStatus.RegisterImported))
        {
            throw new ArgumentException(run.Status switch
            {
                PayrollRunStatus.Draft => "Export the hours first: the register must pay the approved hours that were sent to the provider.",
                PayrollRunStatus.Posted => "This run is posted. Void it to import a corrected register.",
                _ => "This run is voided. Start a new run for the period.",
            });
        }

        var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == run.PayGroupId, cancellationToken);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstAsync(p => p.Id == run.PayPeriodId, cancellationToken);
        var parsed = await providers.Get(group.ProviderKey).ParseRegisterAsync(db, request.Content, cancellationToken);
        var warnings = parsed.Warnings.ToList();
        var profiles = await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.PayGroupId == group.Id && p.ProviderEmployeeId != null).ToListAsync(cancellationToken);
        var byProviderId = profiles.ToDictionary(p => p.ProviderEmployeeId!, p => p.EmployeeId, StringComparer.OrdinalIgnoreCase);
        var names = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);

        var register = new PayrollRegister
        {
            RunId = run.Id,
            FileName = string.IsNullOrWhiteSpace(request.FileName) ? "register.csv" : Path.GetFileName(request.FileName.Trim()),
            ContentHash = hash,
            ImportedBy = audit.Actor,
            ImportedAtUtc = UtcNow,
        };
        if (register.FileName.Length > 260)
        {
            register.FileName = register.FileName[^260..];
        }

        foreach (var row in parsed.Rows)
        {
            var expectedNet = row.GrossWages - row.EmployeeTaxes - row.Deductions;
            if (expectedNet != row.NetPay)
            {
                throw new ArgumentException($"Employee {row.ProviderEmployeeId}: net pay {row.NetPay:N2} isn't gross {row.GrossWages:N2} less employee taxes "
                    + $"{row.EmployeeTaxes:N2} and deductions {row.Deductions:N2} ({expectedNet:N2}). Check the register's columns.");
            }

            if (row.GrossWages < 0 || row.EmployerTaxes < 0 || row.EmployerBenefits < 0)
            {
                throw new ArgumentException($"Employee {row.ProviderEmployeeId}: gross wages, employer taxes and benefits can't be negative.");
            }

            Guid? employeeId = byProviderId.TryGetValue(row.ProviderEmployeeId, out var id) ? id : null;
            if (employeeId is null)
            {
                warnings.Add($"Employee {row.ProviderEmployeeId} ({row.EmployeeName}) isn't in pay group {group.Code}; their pay posts entirely to labor variance.");
            }

            if (row.CheckDate != period.CheckDate)
            {
                warnings.Add($"Employee {row.ProviderEmployeeId}: check date {row.CheckDate:yyyy-MM-dd} isn't the period's check date {period.CheckDate:yyyy-MM-dd}.");
            }

            register.Lines.Add(new PayrollRegisterLine
            {
                RegisterId = register.Id,
                EmployeeId = employeeId,
                ProviderEmployeeId = row.ProviderEmployeeId,
                EmployeeName = row.EmployeeName.Length > 0 ? row.EmployeeName : employeeId is { } e ? names.GetValueOrDefault(e, "?") : "?",
                CheckDate = row.CheckDate,
                GrossWages = row.GrossWages,
                EmployeeTaxes = row.EmployeeTaxes,
                EmployerTaxes = row.EmployerTaxes,
                Deductions = row.Deductions,
                EmployerBenefits = row.EmployerBenefits,
                NetPay = row.NetPay,
            });
        }

        var exported = (await db.Set<PayrollExportLine>().AsNoTracking().Where(l => l.RunId == run.Id).Select(l => l.EmployeeId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        foreach (var missing in exported.Where(e => register.Lines.All(l => l.EmployeeId != e)))
        {
            warnings.Add($"{names.GetValueOrDefault(missing, "?")} had hours exported but no check in the register.");
        }

        if (run.RegisterId is { } previousId && await db.Set<PayrollRegister>().FirstOrDefaultAsync(r => r.Id == previousId, cancellationToken) is { Status: PayrollRegisterStatus.Active } previous)
        {
            previous.Status = PayrollRegisterStatus.Replaced;
        }

        db.Set<PayrollRegister>().Add(register);
        var before = new { Status = run.Status.ToString(), run.RegisterId };
        run.Status = PayrollRunStatus.RegisterImported;
        run.RegisterId = register.Id;
        audit.Record(db, AuditEntityType, run.Id, "Register imported", before,
            new { Status = run.Status.ToString(), run.RegisterId, register.FileName, Checks = register.Lines.Count, Gross = register.Lines.Sum(l => l.GrossWages) });
        await db.SaveChangesAsync(cancellationToken);
        return new RegisterImportResultDto(run.Id, register.Id, false, register.Lines.OrderBy(l => l.EmployeeName).Select(ToDto).ToList(), warnings);
    }

    public async Task<IReadOnlyList<RegisterLineDto>> GetRegisterAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadAsync(db, runId, cancellationToken);
        if (run.RegisterId is not { } registerId)
        {
            return [];
        }

        return await db.Set<PayrollRegisterLine>().AsNoTracking().Where(l => l.RegisterId == registerId).OrderBy(l => l.EmployeeName)
            .Select(l => ToDto(l)).ToListAsync(cancellationToken);
    }

    public async Task<PayrollReconciliationDto> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadAsync(db, runId, cancellationToken);
        return (await ReconcileAsync(db, run, cancellationToken)).Dto;
    }

    public async Task<PayrollRunDto> PostAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadTrackedAsync(db, runId, cancellationToken);
        if (run.Status == PayrollRunStatus.Posted)
        {
            return (await MapAsync(db, [run], cancellationToken))[0];
        }

        if (run.Status != PayrollRunStatus.RegisterImported || run.RegisterId is not { } registerId)
        {
            throw new ArgumentException("Import the payroll register before posting.");
        }

        var register = await db.Set<PayrollRegister>().FirstAsync(r => r.Id == registerId, cancellationToken);

        // Idempotent per register: an entry already posted for it is reused, never posted twice.
        var existing = await db.Set<JournalEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == JournalSource.Payroll && e.SourceId == register.Id && e.Status == JournalStatus.Posted, cancellationToken);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstAsync(p => p.Id == run.PayPeriodId, cancellationToken);
        Guid entryId;
        if (existing is not null)
        {
            entryId = existing.Id;
        }
        else
        {
            var (_, rows, lines) = await ReconcileAsync(db, run, cancellationToken);
            var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == run.PayGroupId, cancellationToken);
            var entry = await PayrollLedger.BuildAsync(db, run, register, group, period, rows, lines, ledger.Actor, UtcNow, cancellationToken);

            // New payroll accounts have to exist before the ledger validates the lines against them.
            await db.SaveChangesAsync(cancellationToken);
            entry.FiscalPeriodId = (await GeneralLedger.PeriodForAsync(db, entry.Date, cancellationToken)).Id;
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, cancellationToken);
            entryId = entry.Id;
        }

        var before = new { Status = run.Status.ToString() };
        register.JournalEntryId = entryId;
        run.Status = PayrollRunStatus.Posted;
        run.JournalEntryId = entryId;
        run.PostedAtUtc = UtcNow;
        run.PostedBy = audit.Actor;
        audit.Record(db, AuditEntityType, run.Id, "Posted", before, new { Status = run.Status.ToString(), JournalEntryId = entryId, RegisterId = register.Id });
        await db.SaveChangesAsync(cancellationToken);

        // Leave accrues per pay period; running it here is idempotent (a second run adds nothing).
        await LeaveEngine.AccrueAsync(db, period, audit.Actor, UtcNow, cancellationToken);
        return (await MapAsync(db, [run], cancellationToken))[0];
    }

    public async Task<PayrollRunDto> VoidAsync(Guid runId, VoidPayrollRequest request, CancellationToken cancellationToken = default)
    {
        var reason = request?.Reason.Required("Reason") ?? throw new ArgumentException("Reason is required.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadTrackedAsync(db, runId, cancellationToken);
        if (run.Status == PayrollRunStatus.Voided)
        {
            throw new ArgumentException("This run is already voided.");
        }

        var before = new { Status = run.Status.ToString(), run.JournalEntryId };
        if (run.RegisterId is { } registerId && await db.Set<PayrollRegister>().FirstOrDefaultAsync(r => r.Id == registerId, cancellationToken) is { } register)
        {
            if (register.JournalEntryId is { } entryId)
            {
                var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstAsync(e => e.Id == entryId, cancellationToken);
                if (entry.Status == JournalStatus.Posted)
                {
                    // Reverse on the original date when its period is still open, otherwise today.
                    var date = (await GeneralLedger.PeriodForAsync(db, entry.Date, cancellationToken)).Status == PeriodStatus.Open
                        ? entry.Date
                        : DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
                    var reversal = await ledger.ReverseAsync(db, entry, date, $"Payroll run voided: {reason}", cancellationToken);
                    register.ReversalEntryId = reversal.Id;
                }
            }

            register.Status = PayrollRegisterStatus.Voided;
        }

        // Posting accrued the period's leave, so voiding a posted run takes it back (re-posting accrues it again).
        if (run.Status == PayrollRunStatus.Posted)
        {
            var period = await db.Set<PayPeriod>().AsNoTracking().FirstAsync(p => p.Id == run.PayPeriodId, cancellationToken);
            var (entries, hours) = await LeaveEngine.ReverseAccrualAsync(db, period, reason, audit.Actor, UtcNow, cancellationToken);
            if (entries > 0)
            {
                audit.Record(db, LeaveEngine.AuditEntityType, period.Id, "Accrual reversed", null,
                    new { EntriesReversed = entries, HoursReversed = hours, period.Start, period.End, RunId = run.Id }, reason);
            }
        }

        run.Status = PayrollRunStatus.Voided;
        run.VoidedAtUtc = UtcNow;
        run.VoidedBy = audit.Actor;
        run.VoidReason = reason;
        audit.Record(db, AuditEntityType, run.Id, "Voided", before, new { Status = run.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [run], cancellationToken))[0];
    }

    // ---- Reconciliation ----

    internal sealed record EmployeeTotals(Guid? EmployeeId, Guid? OrgUnitId, decimal Accrued, decimal Gross, decimal EmployerTaxes, decimal EmployerBenefits);

    private async Task<(PayrollReconciliationDto Dto, List<EmployeeTotals> Rows, List<PayrollRegisterLine> Lines)> ReconcileAsync(IWorkbenchDbContext db, PayrollRun run, CancellationToken ct)
    {
        var lines = run.RegisterId is { } registerId
            ? await db.Set<PayrollRegisterLine>().AsNoTracking().Where(l => l.RegisterId == registerId).ToListAsync(ct)
            : [];
        var data = await PayrollData.LoadAsync(db, run, ct);
        var perYear = PayCalendar.PeriodsPerYear(data.Group.Frequency);
        var rows = new List<ReconciliationRowDto>();
        var totals = new List<EmployeeTotals>();
        foreach (var e in data.Employees)
        {
            var mine = lines.Where(l => l.EmployeeId == e.Employee.Id).ToList();
            var gross = mine.Sum(l => l.GrossWages);
            var notes = new List<string>();
            if (mine.Count == 0 && lines.Count > 0)
            {
                notes.Add("No check in the register.");
            }

            // The rate in effect for the pay period (on its last day); earlier periods keep the rate they were paid at.
            var hasRate = e.Rate is not null && (e.Rate.AnnualSalary ?? e.Rate.HourlyRate) is not null;
            if (!hasRate)
            {
                notes.Add("No pay rate on file.");
            }

            foreach (var change in e.RateChanges)
            {
                notes.Add($"Pay rate changed {change:MMM d}, within the period: reconciled at the rate in effect on {data.Period.End:MMM d}.");
            }

            var x = PayrollReconciliation.Explain(new PayBasis(e.PayType, e.Rate?.AnnualSalary, e.Rate?.HourlyRate, perYear), e.Split, gross);
            if (lines.Count == 0)
            {
                x = x with { Unexplained = 0 };
            }

            rows.Add(new ReconciliationRowDto(e.Employee.Id, e.Employee.DisplayName, (PayTypeKind)(int)e.PayType, e.Split.Regular, e.Split.Overtime, e.Split.LeaveTotal,
                hasRate ? x.PayRate : null, x.WorkedAtPayRate, x.LeavePay, x.OvertimePremium, x.SalariedVariance, x.ExpectedGross, gross, x.Unexplained,
                e.AccruedCost, gross - e.AccruedCost, notes));
            // Only a paid employee's accrual is cleared; an unpaid one's stays in accrued payroll until a register pays it.
            totals.Add(new EmployeeTotals(e.Employee.Id, e.Employee.OrgUnitId, mine.Count == 0 ? 0 : e.AccruedCost, gross, mine.Sum(l => l.EmployerTaxes), mine.Sum(l => l.EmployerBenefits)));
        }

        foreach (var group in lines.Where(l => l.EmployeeId is null || data.Employees.All(e => e.Employee.Id != l.EmployeeId)).GroupBy(l => l.ProviderEmployeeId))
        {
            var gross = group.Sum(l => l.GrossWages);
            rows.Add(new ReconciliationRowDto(group.First().EmployeeId, $"{group.First().EmployeeName} ({group.Key})", null, 0, 0, 0, null, 0, 0, 0, 0, 0, gross, gross, 0, gross,
                ["Not in this pay group: the whole check is unexplained."]));
            totals.Add(new EmployeeTotals(group.First().EmployeeId, null, 0, gross, group.Sum(l => l.EmployerTaxes), group.Sum(l => l.EmployerBenefits)));
        }

        rows = rows.OrderBy(r => r.EmployeeName, StringComparer.Ordinal).ToList();
        var total = new ReconciliationRowDto(null, "Total", null, rows.Sum(r => r.WorkedHours), rows.Sum(r => r.OvertimeHours), rows.Sum(r => r.LeaveHours), null,
            rows.Sum(r => r.WorkedAtPayRate), rows.Sum(r => r.LeavePay), rows.Sum(r => r.OvertimePremium), rows.Sum(r => r.SalariedVariance), rows.Sum(r => r.ExpectedGross),
            rows.Sum(r => r.RegisterGross), rows.Sum(r => r.Unexplained), rows.Sum(r => r.AccruedLaborCost), rows.Sum(r => r.LaborVariance), []);
        var dto = new PayrollReconciliationDto(run.Id, rows, total, lines.Sum(l => l.EmployerTaxes), lines.Sum(l => l.EmployerBenefits), lines.Sum(l => l.NetPay),
            lines.Sum(l => l.EmployeeTaxes + l.Deductions));
        return (dto, totals, lines);
    }

    // ---- Mapping and loading ----

    private static RegisterLineDto ToDto(PayrollRegisterLine l) =>
        new(l.EmployeeId, l.ProviderEmployeeId, l.EmployeeName, l.CheckDate, l.GrossWages, l.EmployeeTaxes, l.EmployerTaxes, l.Deductions, l.EmployerBenefits, l.NetPay);

    private static async Task<PayrollRun> LoadAsync(IWorkbenchDbContext db, Guid runId, CancellationToken ct) =>
        await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw WorkbenchNotFoundException.For("Payroll run", runId);

    private static async Task<PayrollRun> LoadTrackedAsync(IWorkbenchDbContext db, Guid runId, CancellationToken ct) =>
        await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw WorkbenchNotFoundException.For("Payroll run", runId);

    private async Task<List<PayrollRunDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<PayrollRun> runs, CancellationToken ct)
    {
        var groupIds = runs.Select(r => r.PayGroupId).Distinct().ToList();
        var periodIds = runs.Select(r => r.PayPeriodId).Distinct().ToList();
        var registerIds = runs.Where(r => r.RegisterId != null).Select(r => r.RegisterId!.Value).ToList();
        var entryIds = runs.Where(r => r.JournalEntryId != null).Select(r => r.JournalEntryId!.Value).ToList();
        var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var periods = await db.Set<PayPeriod>().AsNoTracking().Where(p => periodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var registers = await db.Set<PayrollRegister>().AsNoTracking().Where(r => registerIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var gross = (await db.Set<PayrollRegisterLine>().AsNoTracking().Where(l => registerIds.Contains(l.RegisterId)).Select(l => new { l.RegisterId, l.GrossWages }).ToListAsync(ct))
            .GroupBy(l => l.RegisterId).ToDictionary(g => g.Key, g => g.Sum(l => l.GrossWages));
        var numbers = await db.Set<JournalEntry>().AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
        return runs.Select(r =>
        {
            var g = groups[r.PayGroupId];
            var p = periods[r.PayPeriodId];
            var reg = r.RegisterId is { } id ? registers.GetValueOrDefault(id) : null;
            return new PayrollRunDto(r.Id, g.Id, g.Code, g.ProviderKey, p.Id, p.Start, p.End, p.CheckDate, (PayrollRunState)(int)r.Status, r.CreatedBy, r.CreatedAtUtc,
                r.ExportedAtUtc, r.ExportFileName, reg?.Id, reg?.FileName, reg?.ImportedAtUtc, reg is null ? null : gross.GetValueOrDefault(reg.Id),
                r.JournalEntryId, r.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null, r.PostedAtUtc, r.VoidReason);
        }).ToList();
    }
}

/// <summary>Everything a run needs about its pay group, period and employees, loaded in one place.</summary>
internal sealed class PayrollData
{
    public required PayGroup Group { get; init; }
    public required PayPeriod Period { get; init; }
    public required PayrollProviderSetting? Settings { get; init; }
    public required IReadOnlyList<EarningCodeMapping> EarningCodes { get; init; }
    public required List<EmployeePayroll> Employees { get; init; }

    /// <summary><paramref name="Rate"/> is the pay rate in effect on the period's last day (null when none is).</summary>
    internal sealed record EmployeePayroll(Employee Employee, EmployeePayProfile Profile, EmployeePayRate? Rate, IReadOnlyList<DateOnly> RateChanges,
        string? CostCenter, HoursSplit Split, EmployeeHoursDto Hours, decimal AccruedCost)
    {
        public PayType PayType => Rate?.PayType ?? Profile.PayType;
    }

    public static async Task<PayrollData> LoadAsync(IWorkbenchDbContext db, PayrollRun run, CancellationToken ct)
    {
        var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == run.PayGroupId, ct);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstAsync(p => p.Id == run.PayPeriodId, ct);
        var settings = await db.Set<PayrollProviderSetting>().AsNoTracking().FirstOrDefaultAsync(s => s.ProviderKey == group.ProviderKey, ct);
        var mappings = Payroll.EarningCodes.Parse(settings?.EarningCodes);
        var profiles = await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.PayGroupId == group.Id).ToListAsync(ct);
        var history = await PayRates.HistoryAsync(db, profiles, ct);
        var ids = profiles.Select(p => p.EmployeeId).ToList();
        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct))
            .Where(e => e.HireDate <= period.End && (e.TerminationDate is null || e.TerminationDate >= period.Start))
            .ToDictionary(e => e.Id);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct);

        // Whole workweeks around the period, so overtime counts a week's hours from before the period start.
        var weeksFrom = PayrollHours.WeekStart(period.Start);
        var weeksTo = PayrollHours.WeekStart(period.End).AddDays(6);
        var activeIds = employees.Keys.ToList();
        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                where t.Status == TimesheetStatus.Approved && activeIds.Contains(t.EmployeeId) && e.WorkDate >= weeksFrom && e.WorkDate <= weeksTo
                select new { t.EmployeeId, e.WorkDate, w.Code, w.Kind, e.Hours, e.Id, e.TimesheetId })
            .ToListAsync(ct);
        var book = await TotalTime.LaborCostBook.LoadAsync(db, entries.Select(e => e.TimesheetId), ct);
        var sheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => activeIds.Contains(t.EmployeeId) && t.PeriodEnd >= period.Start && t.PeriodStart <= period.End)
            .Select(t => new { t.EmployeeId, t.PeriodStart, t.PeriodEnd, t.Status }).ToListAsync(ct);
        var result = new List<EmployeePayroll>();
        foreach (var profile in profiles.Where(p => employees.ContainsKey(p.EmployeeId)))
        {
            var employee = employees[profile.EmployeeId];
            var payRates = history[profile.EmployeeId];
            var rate = PayRates.InEffect(payRates, period.End);
            var payType = rate?.PayType ?? profile.PayType;
            var mine = entries.Where(e => e.EmployeeId == employee.Id).ToList();
            var split = PayrollHours.Split(!employee.IsExempt, period.Start, period.End,
                mine.Select(e => new ApprovedHours(e.WorkDate, e.Code, e.Kind == ChargeKind.Leave, e.Hours)));

            // Labor the GL accrued (credited to 2100) for these hours: hours × the cost rate on the work date, per entry, by the
            // labor cost method (an exempt employee's uncompensated hours under total time accounting).
            var accrued = mine.Where(e => e.WorkDate >= period.Start && e.WorkDate <= period.End).Sum(e => book.Cost(e.Id));

            var issues = new List<HoursIssueDto>();
            if (profile.ProviderEmployeeId is null)
            {
                issues.Add(new(HoursIssueKind.NoProviderId, "No provider employee id: their hours can't be exported."));
            }

            // DCAA: pay must come from approved time. Every working day of employment needs an approved timesheet.
            var missing = new List<DateOnly>();
            var unapproved = new SortedSet<(DateOnly, string)>();
            for (var d = period.Start; d <= period.End; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !employee.IsActiveOn(d))
                {
                    continue;
                }

                var sheet = sheets.FirstOrDefault(s => s.EmployeeId == employee.Id && s.PeriodStart <= d && s.PeriodEnd >= d);
                if (sheet is null)
                {
                    missing.Add(d);
                }
                else if (sheet.Status != TimesheetStatus.Approved)
                {
                    unapproved.Add((sheet.PeriodStart, sheet.Status.ToString().ToLowerInvariant()));
                }
            }

            if (missing.Count > 0)
            {
                issues.Add(new(HoursIssueKind.MissingTimesheet, $"No timesheet for {missing.Count} working day(s) from {missing.Min():MMM d}."));
            }

            foreach (var (start, status) in unapproved)
            {
                issues.Add(new(HoursIssueKind.UnapprovedTimesheet, $"Timesheet for the period starting {start:MMM d} is {status}, not approved: its hours aren't paid."));
            }

            var leave = new List<LeaveHoursDto>();
            foreach (var (code, hours) in split.Leave)
            {
                var (earning, mapped) = Payroll.EarningCodes.Resolve(mappings, code);
                if (!mapped)
                {
                    issues.Add(new(HoursIssueKind.NoEarningCode, $"{code} has no earning code mapping; it's exported as {earning}."));
                }

                leave.Add(new LeaveHoursDto(code, earning, hours));
            }

            var hoursDto = new EmployeeHoursDto(employee.Id, employee.DisplayName, profile.ProviderEmployeeId, employee.IsExempt, (PayTypeKind)(int)payType,
                split.Regular, split.Overtime, leave, issues);
            var changes = PayRates.ChangesWithin(payRates, period.Start, period.End).Select(r => r.EffectiveFrom).Distinct().ToList();
            result.Add(new EmployeePayroll(employee, profile, rate, changes, employee.OrgUnitId is { } o ? orgs.GetValueOrDefault(o) : null, split, hoursDto, accrued));
        }

        return new PayrollData
        {
            Group = group,
            Period = period,
            Settings = settings,
            EarningCodes = mappings,
            Employees = result.OrderBy(r => r.Employee.DisplayName, StringComparer.Ordinal).ToList(),
        };
    }
}
