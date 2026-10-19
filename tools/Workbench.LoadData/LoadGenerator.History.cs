using System.Text.Json;
using Workbench.Application.Accounting;
using Workbench.Application.Financials;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.Approvals;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.LoadData;

/// <summary>
/// Five fiscal years of history, in date order so journal numbers run with time. Every week: each active employee's
/// approved timesheet (entries, daily-entry records, approval instance/task/actions, audit rows) and its labor entry,
/// exactly as <see cref="PostingEngine"/> would plan it. Every month end: vendor ODCs and expense reports (posted per
/// line like the ODC posting), overhead AP bills and their payment run, payroll and reimbursement disbursements,
/// invoices for each contract with their billed sources, and customer payments 30–60 days later. Every entry balances.
/// </summary>
internal sealed partial class LoadGenerator
{
    private sealed record Billable(Guid SourceId, BilledSourceKind Kind, Proj Project, Guid WbsId, Guid? LcatId, Guid? EmployeeId, DateOnly Date, decimal Quantity, decimal Cost, string? Description);

    private readonly Dictionary<(int Year, int Month), List<Billable>> _billable = [];
    private readonly Dictionary<(int Year, int Month), decimal> _laborAccrued = [];
    private readonly Dictionary<(int Year, int Month), decimal> _vendorOdc = [];
    private readonly Dictionary<(int Year, int Month), decimal> _reimbursable = [];
    private readonly List<Invoice> _openInvoices = [];
    private readonly Dictionary<Guid, int> _invoiceSequence = [];
    private readonly HashSet<(int, int)> _monthsDone = [];
    private readonly HashSet<DateOnly> _holidays = [];

    // The provisional cascade the seeds use (fringe 32%, overhead 18%, G&A 11%), for cost-plus billing.
    private const decimal Fringe = 0.32m, Overhead = 0.18m, Ga = 0.11m;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task HistoryAsync()
    {
        await VendorsAsync();
        Holidays();
        var rules = new Dictionary<PostingRuleKind, Dictionary<string, Guid>>();
        foreach (var (kind, key, number) in new[]
                 {
                     (PostingRuleKind.DirectLabor, "", "5010"), (PostingRuleKind.IndirectLabor, "Overhead", "6110"),
                     (PostingRuleKind.IndirectLabor, "GeneralAndAdministrative", "6210"), (PostingRuleKind.IndirectLabor, "Fringe", "6040"),
                     (PostingRuleKind.LeaveLabor, "", "6010"),
                 })
        {
            (rules.TryGetValue(kind, out var map) ? map : rules[kind] = new(StringComparer.Ordinal))[key] = _accounts[number].Id;
        }

        var poolOf = new Dictionary<string, string>(StringComparer.Ordinal) { ["OH"] = "Overhead", ["GA"] = "GeneralAndAdministrative", ["BP"] = "GeneralAndAdministrative", ["FR"] = "Fringe" };
        Guid LaborAccount(WbsElement wbs) => wbs.Kind switch
        {
            ChargeKind.Direct => rules[PostingRuleKind.DirectLabor][""],
            ChargeKind.Indirect => rules[PostingRuleKind.IndirectLabor][poolOf[wbs.Code.Split('.')[0]]],
            _ => rules[PostingRuleKind.LeaveLabor][""],
        };

        var weeks = 0;
        for (var week = Monday(HistoryStart) < HistoryStart ? Monday(HistoryStart).AddDays(7) : Monday(HistoryStart); week < LastWeek; week = week.AddDays(7))
        {
            foreach (var person in _people)
            {
                await TimesheetAsync(person, week, LaborAccount);
            }

            // Month ends that this week completed.
            var next = week.AddDays(7);
            for (var month = new DateOnly(week.Year, week.Month, 1).AddMonths(-1); month < next; month = month.AddMonths(1))
            {
                var end = month.AddMonths(1).AddDays(-1);
                if (end < next && end >= HistoryStart && _monthsDone.Add((month.Year, month.Month)))
                {
                    await MonthEndAsync(month, end);
                }
            }

            if (++weeks % 26 == 0)
            {
                Console.WriteLine($"  … {week:yyyy-MM-dd}: {_bulk.Total:N0} rows so far");
            }
        }
    }

    private void Holidays()
    {
        for (var year = HistoryStart.Year; year <= options.Today.Year + 1; year++)
        {
            DateOnly Nth(int month, DayOfWeek day, int n) => Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(d => new DateOnly(year, month, d)).Where(d => d.DayOfWeek == day).ElementAt(n);
            DateOnly Last(int month, DayOfWeek day) => Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(d => new DateOnly(year, month, d)).Last(d => d.DayOfWeek == day);
            foreach (var d in new[]
                     {
                         new DateOnly(year, 1, 1), Nth(1, DayOfWeek.Monday, 2), Nth(2, DayOfWeek.Monday, 2), Last(5, DayOfWeek.Monday), new DateOnly(year, 6, 19),
                         new DateOnly(year, 7, 4), Nth(9, DayOfWeek.Monday, 0), Nth(10, DayOfWeek.Monday, 1), new DateOnly(year, 11, 11),
                         Nth(11, DayOfWeek.Thursday, 3), new DateOnly(year, 12, 25),
                     })
            {
                // Observed on the nearest weekday.
                _holidays.Add(d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(-1), DayOfWeek.Sunday => d.AddDays(1), _ => d });
            }
        }
    }

    private decimal CostRate(Person person, DateOnly date) => person.Rates.Last(r => r.EffectiveFrom <= date).HourlyCost;

    /// <summary>The day's charges: holidays and leave, then indirect staff's codes, then the person's direct assignments.</summary>
    private IEnumerable<(WbsElement Wbs, decimal Hours)> Day(Person person, DateOnly date, int weekIndex)
    {
        if (_holidays.Contains(date))
        {
            yield return (_indirect["LV.01"], 8m);
            yield break;
        }

        var roll = _rng.NextDouble();
        if (roll < 0.04)
        {
            yield return (_indirect["LV.02"], 8m);
            yield break;
        }

        if (roll < 0.055)
        {
            yield return (_indirect["LV.03"], 8m);
            yield break;
        }

        if (person.Indirect)
        {
            var codes = person.IndirectCodes;
            yield return (_wbs[codes[weekIndex % codes.Length]], 6m);
            yield return (_wbs[codes[(weekIndex + 1) % codes.Length]], 2m);
            yield break;
        }

        var fy = FiscalYearOf(date);
        var charges = person.Charges[fy].Select(id => _wbs[id]).Where(w => _projects.First(p => p.P.Id == w.ProjectId).P is var pr && pr.Start <= date && pr.Finish >= date).ToList();
        if (charges.Count == 0)
        {
            // On the bench between contracts: proposals.
            yield return (_indirect["BP.01"], 8m);
            yield break;
        }

        var primary = charges[weekIndex % charges.Count];
        if (roll < 0.12)
        {
            yield return (primary, 6m);
            yield return (_indirect[roll < 0.09 ? "OH.01" : "BP.03"], 2m);
        }
        else if (charges.Count > 1 && roll < 0.55)
        {
            yield return (primary, 6m);
            yield return (charges[(weekIndex + 1) % charges.Count], 2m);
        }
        else
        {
            yield return (primary, 8m);
        }
    }

    private async Task TimesheetAsync(Person person, DateOnly week, Func<WbsElement, Guid> laborAccount)
    {
        var days = Enumerable.Range(0, 5).Select(i => week.AddDays(i)).Where(person.E.IsActiveOn).ToList();
        if (days.Count == 0)
        {
            return;
        }

        var weekIndex = week.DayNumber / 7;
        var approver = person.Supervisor ?? _people.First(p => p.Email == $"itadmin@{Domain}");
        var submitted = Utc(week.AddDays(4), 21, _rng.Next(0, 59));
        var approved = Utc(week.AddDays(7), 14, _rng.Next(0, 59)).AddDays(_rng.NextDouble() < 0.15 ? 1 : 0);
        var sheet = new Timesheet
        {
            EmployeeId = person.E.Id,
            PeriodStart = week,
            PeriodEnd = week.AddDays(6),
            Status = TimesheetStatus.Approved,
            CertifiedAtUtc = submitted,
            SubmittedAtUtc = submitted,
            FirstSubmittedAtUtc = submitted,
            CertificationText = TimesheetCertification.Text,
            ApprovedById = approver.E.Id,
            ApprovedAtUtc = approved,
            ReviewedBy = approver.Email,
            CreatedAtUtc = Utc(week, 13),
            UpdatedAtUtc = approved,
        };
        await _bulk.AddAsync(sheet);

        var entries = new List<(TimeEntry Entry, WbsElement Wbs, decimal Cost)>();
        foreach (var date in days)
        {
            var entered = _rng.NextDouble() < 0.03 ? submitted.AddMinutes(-30) : Utc(date, 21, _rng.Next(0, 59));
            foreach (var (wbs, hours) in Day(person, date, weekIndex))
            {
                var entry = new TimeEntry
                {
                    TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = person.Lcat.Id, WorkDate = date, Hours = hours,
                    EnteredAtUtc = entered, UpdatedAtUtc = entered,
                };
                entries.Add((entry, wbs, Money.Round(hours * CostRate(person, date))));
                await _bulk.AddAsync(entry);
            }

            await _bulk.AddAsync(new TimeDayRecord { EmployeeId = person.E.Id, TimesheetId = sheet.Id, WorkDate = date, FirstEnteredAtUtc = entered });
        }

        // Approval: one supervisor task, as the default timesheet workflow creates it.
        var hoursTotal = entries.Sum(e => e.Entry.Hours);
        var instance = new ApprovalInstance
        {
            DocumentType = ApprovalDocumentTypes.Timesheet, DocumentId = sheet.Id, WorkflowVersionId = _timesheetWorkflowVersion,
            Status = ApprovalInstanceStatus.Approved, SubmitterEmail = person.Email,
            Title = $"{person.E.DisplayName}: timesheet {sheet.PeriodStart:MMM d}–{sheet.PeriodEnd:MMM d}", Summary = $"{hoursTotal:0.0} h", Amount = hoursTotal,
            Link = $"time/history/{sheet.Id}", CurrentSequence = 1, StartedAtUtc = submitted, CompletedAtUtc = approved,
        };
        var task = new ApprovalTask
        {
            InstanceId = instance.Id, StepIndex = 0, Sequence = 1, StepName = "Supervisor", Status = ApprovalTaskStatus.Approved,
            AssigneesCsv = approver.Email, ExcludedCsv = person.Email, ActivatedAtUtc = submitted, DecidedBy = approver.Email, DecidedAtUtc = approved,
        };
        await _bulk.AddRangeAsync(
        [
            instance, task,
            new ApprovalAction { InstanceId = instance.Id, Action = "Submitted", Actor = person.Email, OccurredAtUtc = submitted },
            new ApprovalAction { InstanceId = instance.Id, TaskId = task.Id, Action = "Approved", Actor = approver.Email, OccurredAtUtc = approved },
            new ApprovalAction { InstanceId = instance.Id, Action = "Completed", Actor = approver.Email, OccurredAtUtc = approved },
            Audit("Timesheet", sheet.Id, "Submitted", person.Email, submitted, new { Status = "Submitted", Hours = hoursTotal }),
            Audit("Timesheet", sheet.Id, "Approved", approver.Email, approved, new { Status = "Approved", Hours = hoursTotal }),
        ]);

        // Labor journal entries, one per fiscal period the week touches (PostingEngine.PlanTimesheetAsync).
        foreach (var group in entries.GroupBy(e => PeriodOf(e.Entry.WorkDate)))
        {
            var period = group.Key;
            var entry = NewEntry(Min(sheet.PeriodEnd, period.End), period, JournalSource.Timesheet, sheet.Id,
                $"Labor: {person.E.DisplayName}, week of {sheet.PeriodStart:yyyy-MM-dd}", approver.Email, approved);
            var lines = new List<JournalLine>();
            foreach (var line in group.GroupBy(c => (Account: laborAccount(c.Wbs), c.Wbs.Id)).OrderBy(g => g.First().Wbs.Code, StringComparer.Ordinal))
            {
                var cost = line.Sum(c => c.Cost);
                var wbs = line.First().Wbs;
                lines.Add(Line(entry, lines.Count + 1, line.Key.Account, cost, 0, $"{line.Sum(c => c.Entry.Hours):0.##} h {wbs.Code}", person.E.OrgUnitId, wbs));
            }

            var total = lines.Sum(l => l.Debit);
            lines.Add(Line(entry, lines.Count + 1, _accounts["2100"].Id, 0, total, $"Accrued payroll: {person.E.DisplayName}", person.E.OrgUnitId));
            await PostAsync(entry, lines, approver.Email, approved);
            var key = (period.Start.Year, period.Start.Month);
            _laborAccrued[key] = _laborAccrued.GetValueOrDefault(key) + total;
        }

        foreach (var (e, wbs, cost) in entries.Where(e => e.Wbs.Kind == ChargeKind.Direct))
        {
            var proj = _projects.First(p => p.P.Id == wbs.ProjectId);
            var key = (e.WorkDate.Year, e.WorkDate.Month);
            (_billable.TryGetValue(key, out var list) ? list : _billable[key] = []).Add(new Billable(e.Id, BilledSourceKind.TimeEntry, proj, wbs.Id, e.LaborCategoryId, person.E.Id, e.WorkDate, e.Hours, cost, null));
        }
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private JournalEntry NewEntry(DateOnly date, FiscalPeriod period, JournalSource source, Guid? sourceId, string description, string actor, DateTime at) => new()
    {
        Date = date, FiscalPeriodId = period.Id, Source = source, SourceId = sourceId, Description = description.Length > 500 ? description[..500] : description,
        CreatedBy = actor, CreatedAtUtc = at,
    };

    private static JournalLine Line(JournalEntry entry, int number, Guid account, decimal debit, decimal credit, string? memo, Guid? orgUnit = null, WbsElement? wbs = null, Guid? projectId = null) => new()
    {
        JournalEntryId = entry.Id, LineNumber = number, AccountId = account, Debit = debit, Credit = credit, Memo = memo,
        OrgUnitId = orgUnit, ProjectId = wbs?.ProjectId ?? projectId, WbsElementId = wbs?.Id,
    };

    /// <summary>Validates like <see cref="JournalEntry.EnsurePostable"/>, numbers the entry, and adds it with its lines and audit row.</summary>
    private async Task PostAsync(JournalEntry entry, List<JournalLine> lines, string actor, DateTime at)
    {
        lines.RemoveAll(l => l.Debit == 0 && l.Credit == 0);
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].LineNumber = i + 1;
            entry.Lines.Add(lines[i]);
        }

        entry.MarkPosted(++_sequence, actor, at);
        entry.Lines.Clear(); // Lines are added separately, so the bulk insert doesn't add them twice.
        await _bulk.AddAsync(entry);
        await _bulk.AddRangeAsync(lines);
        await _bulk.AddAsync(Audit(GeneralLedger.AuditEntityType, entry.Id, "Posted", actor, at, new
        {
            entry.Number, entry.Date, Source = entry.Source.ToString(), entry.SourceId, entry.SourceRevision, entry.Description,
            Debits = lines.Sum(l => l.Debit), Credits = lines.Sum(l => l.Credit), Lines = lines.Count,
        }));
    }

    private static AuditEvent Audit(string type, Guid id, string action, string actor, DateTime at, object after) => new()
    {
        EntityType = type, EntityId = id, Action = action, Actor = actor, OccurredAtUtc = at, AfterJson = JsonSerializer.Serialize(after, Json),
    };

    // ---- Month end ----

    private async Task MonthEndAsync(DateOnly month, DateOnly end)
    {
        var key = (month.Year, month.Month);
        var period = PeriodOf(month);
        var controller = _people.First(p => p.Email == $"finance@{Domain}");
        var clerk = _people.First(p => p.Indirect && p.Supervisor == controller);
        var closeAt = Utc(end.AddDays(3), 15);

        await VendorOdcsAsync(month, end, clerk);
        await ExpenseReportsAsync(month, end);
        await OverheadBillsAsync(month, end, period, clerk, controller);

        // Payroll and reimbursements paid in cash (the accruals clear).
        var payroll = _laborAccrued.GetValueOrDefault(key);
        if (payroll > 0)
        {
            var taxes = Money.Round(payroll * 0.0765m);
            var entry = NewEntry(end, period, JournalSource.Manual, null, $"Payroll disbursements {month:yyyy-MM}", controller.Email, closeAt);
            await PostAsync(entry,
            [
                Line(entry, 1, _accounts["2100"].Id, payroll, 0, "Accrued payroll cleared"),
                Line(entry, 2, _accounts["6020"].Id, taxes, 0, "Employer payroll taxes", _orgUnits[0].Id),
                Line(entry, 3, _accounts["1010"].Id, 0, payroll + taxes, "Net pay, withholdings and taxes"),
            ], controller.Email, closeAt);
        }

        foreach (var (account, amounts, memo) in new[] { ("2110", _reimbursable, "Expense reimbursements"), ("2010", _vendorOdc, "Vendor ODC invoices paid") })
        {
            if (amounts.GetValueOrDefault(key) is var amount and > 0)
            {
                var entry = NewEntry(end, period, JournalSource.Manual, null, $"{memo} {month:yyyy-MM}", controller.Email, closeAt);
                await PostAsync(entry, [Line(entry, 1, _accounts[account].Id, amount, 0, memo), Line(entry, 2, _accounts["1010"].Id, 0, amount, memo)], controller.Email, closeAt);
            }
        }

        await InvoicesAsync(month, end, controller);
        await PaymentsAsync(end, controller);
    }

    private static readonly OdcType[] VendorTypes = [OdcType.Materials, OdcType.Equipment, OdcType.Subcontract, OdcType.Consultant, OdcType.Other];

    private string OdcAccount(OdcType type) => type switch
    {
        OdcType.Travel => "5110", OdcType.Materials => "5120", OdcType.Equipment => "5130", OdcType.Subcontract => "5140", OdcType.Consultant => "5150", _ => "5190",
    };

    private async Task VendorOdcsAsync(DateOnly month, DateOnly end, Person clerk)
    {
        foreach (var proj in _projects.Where(p => p.P.Start <= end && p.P.Finish >= month))
        {
            for (var i = 0; i < _rng.Next(1, 4); i++)
            {
                var type = Pick(VendorTypes);
                var vendor = _vendors[OverheadVendors.Length + _rng.Next(OdcVendors.Length)];
                var incurred = month.AddDays(_rng.Next(0, end.Day));
                var approver = proj.Manager!;
                var submitted = Utc(incurred.AddDays(2), 15);
                var approved = submitted.AddDays(2);
                var odc = new OdcEntry
                {
                    ProjectId = proj.P.Id, WbsElementId = proj.OdcLeaf.Id, Type = type, IncurredOn = incurred, Amount = Math.Round(Between(500, type == OdcType.Subcontract ? 60_000 : 12_000), 2),
                    Description = $"{type} for {proj.P.Code}", Vendor = vendor.Name, VendorId = vendor.Id, InvoiceNumber = $"{vendor.Code}-{incurred:yyMMdd}-{i + 1}",
                    Status = OdcStatus.Approved, SubmittedBy = clerk.Email, CreatedAtUtc = submitted.AddHours(-1), SubmittedAtUtc = submitted, ApprovedBy = approver.Email, ApprovedAtUtc = approved,
                };
                await _bulk.AddAsync(odc);
                await PostOdcAsync(odc, approver.Email, approved, isExpense: false, null);
                AddBillable(proj, odc);
            }
        }
    }

    private async Task PostOdcAsync(OdcEntry odc, string actor, DateTime at, bool isExpense, Person? employee)
    {
        var period = PeriodOf(odc.IncurredOn);
        var debit = odc.IsUnallowable ? (odc.Type == OdcType.Travel ? "7020" : "7010") : OdcAccount(odc.Type);
        var credit = isExpense ? "2110" : "2010";
        var description = (isExpense ? $"Expense: {odc.Description}" : $"ODC: {odc.Vendor} {odc.InvoiceNumber} {odc.Description}".Replace("  ", " ")) + (odc.IsUnallowable ? " (unallowable)" : "");
        var entry = NewEntry(odc.IncurredOn, period, JournalSource.Odc, odc.Id, description, actor, at);
        var wbs = _wbs[odc.WbsElementId];
        await PostAsync(entry,
        [
            Line(entry, 1, _accounts[debit].Id, odc.Amount, 0, odc.IsUnallowable ? $"Unallowable: {odc.UnallowableReason}" : odc.Description, employee?.E.OrgUnitId, wbs),
            Line(entry, 2, _accounts[credit].Id, 0, odc.Amount, isExpense ? $"Reimbursement: {employee!.E.DisplayName}" : $"{odc.Vendor} {odc.InvoiceNumber}".Trim(), employee?.E.OrgUnitId),
        ], actor, at);
        var key = (period.Start.Year, period.Start.Month);
        var bucket = isExpense ? _reimbursable : _vendorOdc;
        bucket[key] = bucket.GetValueOrDefault(key) + odc.Amount;
    }

    private void AddBillable(Proj proj, OdcEntry odc)
    {
        if (odc.IsUnallowable)
        {
            return;
        }

        var key = (odc.IncurredOn.Year, odc.IncurredOn.Month);
        (_billable.TryGetValue(key, out var list) ? list : _billable[key] = []).Add(
            new Billable(odc.Id, BilledSourceKind.Odc, proj, odc.WbsElementId, null, odc.EmployeeId, odc.IncurredOn, 1, odc.Amount, odc.Description));
    }

    private async Task ExpenseReportsAsync(DateOnly month, DateOnly end)
    {
        var fy = FiscalYearOf(month);
        var travelers = _people.Where(p => !p.Indirect && p.Charges.ContainsKey(fy) && p.E.IsActiveOn(month) && p.E.IsActiveOn(end) && _rng.NextDouble() < 0.10).ToList();
        foreach (var person in travelers)
        {
            var proj = _projects.First(p => p.P.Id == _wbs[person.Charges[fy][0]].ProjectId);
            if (proj.P.Finish < end)
            {
                continue;
            }

            var tripStart = month.AddDays(_rng.Next(0, Math.Max(1, end.Day - 5)));
            var tripEnd = tripStart.AddDays(_rng.Next(1, 4));
            var approver = person.Supervisor!;
            var submitted = Utc(tripEnd.AddDays(2), 16);
            var approved = submitted.AddDays(1);
            var report = new ExpenseReport
            {
                EmployeeId = person.E.Id, Title = $"Site visit {proj.P.Code} {tripStart:MMM d}", Purpose = "Customer site support", TripStart = tripStart, TripEnd = tripEnd,
                Status = OdcStatus.Approved, CreatedBy = person.Email, CreatedAtUtc = submitted.AddHours(-2), SubmittedBy = person.Email, SubmittedAtUtc = submitted,
                ApprovedBy = approver.Email, ApprovedAtUtc = approved,
            };
            await _bulk.AddAsync(report);
            var nights = tripEnd.DayNumber - tripStart.DayNumber;
            var lines = new List<(string Description, decimal Amount, bool Unallowable)>
            {
                ("Airfare", Math.Round(Between(280, 900), 2), false),
                ($"Lodging, {nights} night(s)", Math.Round(nights * Between(110, 190), 2), false),
                ($"Meals and incidentals, {nights + 1} day(s)", Math.Round((nights + 1) * 68m, 2), false),
            };
            if (_rng.NextDouble() < 0.06)
            {
                lines.Add(("Team dinner bar tab", Math.Round(Between(40, 140), 2), true));
            }

            var total = 0m;
            foreach (var (description, amount, unallowable) in lines)
            {
                var odc = new OdcEntry
                {
                    ProjectId = proj.P.Id, WbsElementId = proj.OdcLeaf.Id, Type = unallowable ? OdcType.Other : OdcType.Travel, IncurredOn = tripStart, Amount = amount, Description = description,
                    EmployeeId = person.E.Id, ExpenseReportId = report.Id, IsUnallowable = unallowable, UnallowableReason = unallowable ? "Alcohol, FAR 31.205-51" : null,
                    Status = OdcStatus.Approved, SubmittedBy = person.Email, CreatedAtUtc = report.CreatedAtUtc, SubmittedAtUtc = submitted, ApprovedBy = approver.Email, ApprovedAtUtc = approved,
                };
                total += amount;
                await _bulk.AddAsync(odc);
                await PostOdcAsync(odc, approver.Email, approved, isExpense: true, person);
                AddBillable(proj, odc);
            }

            var instance = new ApprovalInstance
            {
                DocumentType = ApprovalDocumentTypes.ExpenseReport, DocumentId = report.Id, WorkflowVersionId = _expenseWorkflowVersion, Status = ApprovalInstanceStatus.Approved,
                SubmitterEmail = person.Email, Title = $"{person.E.DisplayName}: {report.Title}", Summary = $"{lines.Count} line(s)", Amount = total,
                Link = $"expenses/{report.Id}", CurrentSequence = 1, StartedAtUtc = submitted, CompletedAtUtc = approved,
            };
            var task = new ApprovalTask
            {
                InstanceId = instance.Id, StepIndex = 0, Sequence = 1, StepName = "Supervisor", Status = ApprovalTaskStatus.Approved, AssigneesCsv = approver.Email,
                ExcludedCsv = person.Email, ActivatedAtUtc = submitted, DecidedBy = approver.Email, DecidedAtUtc = approved,
            };
            await _bulk.AddRangeAsync(
            [
                instance, task,
                new ApprovalAction { InstanceId = instance.Id, Action = "Submitted", Actor = person.Email, OccurredAtUtc = submitted },
                new ApprovalAction { InstanceId = instance.Id, TaskId = task.Id, Action = "Approved", Actor = approver.Email, OccurredAtUtc = approved },
                new ApprovalAction { InstanceId = instance.Id, Action = "Completed", Actor = approver.Email, OccurredAtUtc = approved },
            ]);
        }
    }

    private int _billNumber;
    private int _runNumber;

    private async Task OverheadBillsAsync(DateOnly month, DateOnly end, FiscalPeriod period, Person clerk, Person controller)
    {
        var key = (month.Year, month.Month);
        var bills = new List<ApBill>();
        for (var i = 0; i < OverheadVendors.Length; i++)
        {
            var (_, name, account, monthly) = OverheadVendors[i];
            var vendor = _vendors[i];
            var amount = monthly == 0 ? Money.Round(_laborAccrued.GetValueOrDefault(key) * 0.14m) : Math.Round(monthly * Between(0.95m, 1.06m), 2);
            if (amount <= 0)
            {
                continue;
            }

            var date = month.AddDays(Math.Min(4, end.Day - 1));
            var entered = Utc(date.AddDays(1), 14);
            var bill = new ApBill
            {
                VendorId = vendor.Id, Number = $"BILL-{++_billNumber:D6}", InvoiceNumber = $"{vendor.Code}-{month:yyyyMM}", InvoiceDate = date, DueDate = date.AddDays(30),
                Description = $"{name} {month:MMMM yyyy}", Status = ApBillStatus.Posted, Amount = amount, PaidAmount = amount, EnteredBy = clerk.Email,
                CreatedAtUtc = entered, SubmittedAtUtc = entered, PostedAtUtc = entered.AddDays(1),
            };
            var line = new ApBillLine
            {
                BillId = bill.Id, LineNumber = 1, Kind = ApBillLineKind.Account, Description = bill.Description!, Amount = amount, AccountId = _accounts[account].Id,
                OrgUnitId = _orgUnits[0].Id, Status = ApLineStatus.Approved, ApproverEmail = controller.Email, DecidedBy = controller.Email, DecidedAtUtc = entered.AddDays(1),
            };
            var entry = NewEntry(date, period, JournalSource.AP, bill.Id, $"AP bill {bill.Number}: {name} {bill.InvoiceNumber}", controller.Email, entered.AddDays(1));
            bill.JournalEntryId = entry.Id;
            await PostAsync(entry, [Line(entry, 1, _accounts[account].Id, amount, 0, bill.Description, _orgUnits[0].Id), Line(entry, 2, _accounts["2010"].Id, 0, amount, name)],
                controller.Email, entered.AddDays(1));
            await _bulk.AddAsync(bill);
            await _bulk.AddAsync(line);
            bills.Add(bill);
        }

        if (bills.Count == 0)
        {
            return;
        }

        // One completed ACH payment run at month end pays them all.
        var paidOn = end;
        var run = new ApPaymentRun
        {
            Number = $"PAY-{++_runNumber:D5}", PaymentDate = paidOn, Method = Workbench.Domain.Purchasing.PaymentMethod.Ach, Status = PaymentRunStatus.Completed,
            CreatedBy = clerk.Email, CreatedAtUtc = Utc(paidOn.AddDays(-2), 14), ApprovedBy = controller.Email, ApprovedAtUtc = Utc(paidOn.AddDays(-1), 14), CompletedAtUtc = Utc(paidOn, 18),
        };
        await _bulk.AddAsync(run);
        foreach (var bill in bills)
        {
            var payment = new ApPayment
            {
                RunId = run.Id, BillId = bill.Id, VendorId = bill.VendorId, Amount = bill.Amount, Method = run.Method, Status = ApPaymentStatus.Paid,
                Reference = $"ACH{run.Number[4..]}{bill.Number[^4..]}", PaidOn = paidOn, PaidBy = controller.Email,
            };
            var entry = NewEntry(paidOn, period, JournalSource.AP, payment.Id, $"Payment {payment.Reference}: bill {bill.Number}", controller.Email, run.CompletedAtUtc!.Value);
            payment.JournalEntryId = entry.Id;
            await PostAsync(entry, [Line(entry, 1, _accounts["2010"].Id, bill.Amount, 0, bill.Number), Line(entry, 2, _accounts["1010"].Id, 0, bill.Amount, payment.Reference)],
                controller.Email, run.CompletedAtUtc!.Value);
            await _bulk.AddAsync(payment);
        }
    }

    // ---- Billing ----

    private async Task InvoicesAsync(DateOnly month, DateOnly end, Person controller)
    {
        var items = _billable.GetValueOrDefault((month.Year, month.Month)) ?? [];
        _billable.Remove((month.Year, month.Month));
        var invoiceDate = end.AddDays(5);
        if (invoiceDate >= LastWeek)
        {
            return; // Not billed yet: the current period's billing is left for the load test.
        }

        var fy = FiscalYearOf(month);
        foreach (var ctr in _contracts)
        {
            var mine = items.Where(b => b.Project.Contract == ctr).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            var type = ctr.C.Type;
            // Fixed price bills a milestone at each quarter end (the quarter's cost × 1.2); the rest bill monthly.
            if (type == ContractType.FirmFixedPrice)
            {
                await FixedPriceAsync(ctr, month, end, mine, controller);
                continue;
            }

            var invoice = NewInvoice(ctr, month, end, invoiceDate, type == ContractType.CostPlusFixedFee ? InvoiceType.CostPlus : InvoiceType.TimeAndMaterials, controller);
            var lines = new List<(InvoiceLine Line, List<BilledSource> Sources)>();
            foreach (var group in mine.GroupBy(b => (b.Project, b.Kind, b.LcatId)).OrderBy(g => g.Key.Project.P.Code, StringComparer.Ordinal).ThenBy(g => g.Key.Kind))
            {
                var (proj, kind, lcat) = group.Key;
                var odc = kind == BilledSourceKind.Odc;
                var rate = odc || type == ContractType.CostPlusFixedFee ? 0m : ctr.BillRates[(lcat!.Value, fy)];
                var sources = group.Select(b => new BilledSource
                {
                    Kind = b.Kind, SourceId = b.SourceId, Date = b.Date, EmployeeId = b.EmployeeId, LaborCategoryId = b.LcatId, WbsElementId = b.WbsId,
                    Quantity = b.Quantity, Rate = rate == 0 ? (odc ? b.Cost : Math.Round(b.Cost / b.Quantity, 4)) : rate, Amount = rate == 0 ? b.Cost : Money.Round(b.Quantity * rate),
                    Description = b.Description,
                }).ToList();
                var line = new InvoiceLine
                {
                    InvoiceId = invoice.Id, ClinId = odc ? ctr.OdcClin.Id : ctr.LaborClin.Id, Kind = odc ? InvoiceLineKind.Odc : InvoiceLineKind.Labor,
                    Description = odc ? $"Other direct costs, {proj.P.Code}" : $"{_lcats.First(l => l.Id == lcat).Name}, {proj.P.Code}",
                    ProjectId = proj.P.Id, LaborCategoryId = lcat, Quantity = odc ? sources.Count : sources.Sum(s => s.Quantity), Rate = odc ? 0 : rate,
                    Amount = sources.Sum(s => s.Amount),
                };
                if (!odc && rate == 0)
                {
                    line.Rate = line.Quantity == 0 ? 0 : Math.Round(line.Amount / line.Quantity, 4);
                }

                lines.Add((line, sources));
            }

            if (type == ContractType.CostPlusFixedFee)
            {
                // Burden on labor (fringe → overhead → G&A) and fee on the labor CLIN's burdened cost, per project.
                foreach (var proj in mine.Select(b => b.Project).Distinct())
                {
                    var labor = mine.Where(b => b.Project == proj && b.Kind == BilledSourceKind.TimeEntry).Sum(b => b.Cost);
                    var odcCost = mine.Where(b => b.Project == proj && b.Kind == BilledSourceKind.Odc).Sum(b => b.Cost);
                    var fringe = Money.Round(labor * Fringe);
                    var overhead = Money.Round((labor + fringe) * Overhead);
                    var ga = Money.Round((labor + fringe + overhead + odcCost) * Ga);
                    var burden = fringe + overhead + ga;
                    var fee = Money.Round((labor + burden) * ctr.LaborClin.FeeRate!.Value);
                    lines.Add((new InvoiceLine { InvoiceId = invoice.Id, ClinId = ctr.LaborClin.Id, Kind = InvoiceLineKind.Burden, Description = $"Indirect costs (provisional rates), {proj.P.Code}", ProjectId = proj.P.Id, Quantity = 1, Rate = burden, Amount = burden }, []));
                    lines.Add((new InvoiceLine { InvoiceId = invoice.Id, ClinId = ctr.LaborClin.Id, Kind = InvoiceLineKind.Fee, Description = $"Fixed fee 7%, {proj.P.Code}", ProjectId = proj.P.Id, Quantity = 1, Rate = fee, Amount = fee }, []));
                }
            }

            await SaveInvoiceAsync(ctr, invoice, lines, controller);
        }
    }

    private Invoice NewInvoice(Ctr ctr, DateOnly from, DateOnly to, DateOnly date, InvoiceType type, Person controller)
    {
        var sequence = _invoiceSequence[ctr.C.Id] = _invoiceSequence.GetValueOrDefault(ctr.C.Id) + 1;
        var created = Utc(date, 14);
        return new Invoice
        {
            ContractId = ctr.C.Id, CustomerId = ctr.C.CustomerId, Sequence = sequence, Number = $"INV-{ctr.Prefix}-{sequence:D4}", Type = type, Status = InvoiceStatus.Sent,
            PeriodFrom = from, PeriodTo = to, InvoiceDate = date, DueDate = date.AddDays(30), PaymentTermsDays = 30, CreatedBy = controller.Email, CreatedAtUtc = created,
            UpdatedAtUtc = created, ApprovedBy = controller.Email, ApprovedAtUtc = created.AddHours(2), SentBy = controller.Email, SentAtUtc = created.AddHours(3),
        };
    }

    private async Task SaveInvoiceAsync(Ctr ctr, Invoice invoice, List<(InvoiceLine Line, List<BilledSource> Sources)> lines, Person controller)
    {
        lines.RemoveAll(l => l.Line.Amount == 0);
        if (lines.Count == 0)
        {
            _invoiceSequence[ctr.C.Id]--;
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].Line.LineNumber = i + 1;
        }

        invoice.Total = lines.Sum(l => l.Line.Amount);
        var period = PeriodOf(invoice.InvoiceDate);
        var entry = NewEntry(invoice.InvoiceDate, period, JournalSource.Billing, invoice.Id, $"Invoice {invoice.Number} · {ctr.C.ContractNumber}", controller.Email, invoice.ApprovedAtUtc!.Value);
        invoice.JournalEntryId = entry.Id;
        // BillingLedger.PostInvoiceAsync: debit AR billed, credit revenue by CLIN pricing per project.
        var journal = new List<JournalLine> { Line(entry, 1, _accounts["1110"].Id, invoice.Total, 0, $"Invoice {invoice.Number}") };
        var clins = new[] { ctr.LaborClin, ctr.OdcClin };
        foreach (var g in lines.GroupBy(l => (Account: Workbench.Application.Billing.BillingLedger.RevenueAccount(clins.First(c => c.Id == l.Line.ClinId).Pricing, ctr.C.Type), l.Line.ProjectId))
                     .OrderBy(g => g.Key.Account, StringComparer.Ordinal))
        {
            journal.Add(Line(entry, journal.Count + 1, _accounts[g.Key.Account].Id, 0, g.Sum(l => l.Line.Amount), null, projectId: g.Key.ProjectId));
        }

        await PostAsync(entry, journal, controller.Email, invoice.ApprovedAtUtc!.Value);
        await _bulk.AddAsync(invoice);
        foreach (var (line, sources) in lines)
        {
            await _bulk.AddAsync(line);
            foreach (var source in sources)
            {
                source.InvoiceId = invoice.Id;
                source.InvoiceLineId = line.Id;
                await _bulk.AddAsync(source);
            }
        }

        await _bulk.AddAsync(Audit("Invoice", invoice.Id, "Approved", controller.Email, invoice.ApprovedAtUtc!.Value, new { invoice.Number, invoice.Total }));
        _openInvoices.Add(invoice);
    }

    private readonly Dictionary<Guid, List<Billable>> _fixedPriceQuarter = [];

    private async Task FixedPriceAsync(Ctr ctr, DateOnly month, DateOnly end, List<Billable> mine, Person controller)
    {
        var quarter = (_fixedPriceQuarter.TryGetValue(ctr.C.Id, out var q) ? q : _fixedPriceQuarter[ctr.C.Id] = []);
        quarter.AddRange(mine);
        if (month.Month % 3 != 0)
        {
            return; // Quarters end in December, March, June and September.
        }

        var cost = quarter.Sum(b => b.Cost);
        quarter.Clear();
        var invoiceDate = end.AddDays(5);
        var milestone = new BillingMilestone
        {
            ContractId = ctr.C.Id, ClinId = ctr.LaborClin.Id, Description = $"Quarterly deliverable {month.AddMonths(-2):MMM}–{month:MMM yyyy}", Amount = Math.Round(cost * 1.2m, 2),
            PlannedDate = end, CompletedDate = end, CompletedBy = ctr.Projects[0].Manager?.Email,
        };
        var invoice = NewInvoice(ctr, month.AddMonths(-2), end, invoiceDate, InvoiceType.FixedPriceMilestone, controller);
        milestone.InvoiceId = invoice.Id;
        var line = new InvoiceLine
        {
            InvoiceId = invoice.Id, ClinId = ctr.LaborClin.Id, Kind = InvoiceLineKind.Milestone, Description = milestone.Description, ProjectId = ctr.Projects[0].P.Id,
            Quantity = 1, Rate = milestone.Amount, Amount = milestone.Amount,
        };
        var source = new BilledSource { Kind = BilledSourceKind.Milestone, SourceId = milestone.Id, Date = end, Quantity = 1, Rate = milestone.Amount, Amount = milestone.Amount, Description = milestone.Description };
        await SaveInvoiceAsync(ctr, invoice, [(line, [source])], controller);
        milestone.Order = _invoiceSequence[ctr.C.Id];
        await _bulk.AddAsync(milestone);
    }

    private async Task PaymentsAsync(DateOnly end, Person controller)
    {
        // Customers pay 25–60 days after the invoice date; whatever isn't due by last week stays open (the AR aging).
        foreach (var invoice in _openInvoices.ToList())
        {
            var receivedOn = invoice.InvoiceDate.AddDays(25 + (int)((uint)invoice.Id.GetHashCode() % 36));
            if (receivedOn > end || receivedOn >= LastWeek)
            {
                continue;
            }

            var customer = _customers.First(c => c.Id == invoice.CustomerId);
            var payment = new Payment
            {
                CustomerId = invoice.CustomerId, ReceivedOn = receivedOn, Amount = invoice.Total, Method = Workbench.Domain.Billing.PaymentMethod.Eft,
                Reference = $"EFT{receivedOn:yyyyMMdd}{invoice.Sequence:D3}", CreatedBy = controller.Email, CreatedAtUtc = Utc(receivedOn, 16),
            };
            var entry = NewEntry(receivedOn, PeriodOf(receivedOn), JournalSource.Billing, payment.Id, $"Cash receipt · {customer.Name} · Ref {payment.Reference}", controller.Email, payment.CreatedAtUtc);
            payment.JournalEntryId = entry.Id;
            invoice.AmountPaid = invoice.Total;
            await PostAsync(entry, [Line(entry, 1, _accounts["1010"].Id, payment.Amount, 0, $"Ref {payment.Reference}"), Line(entry, 2, _accounts["1110"].Id, 0, payment.Amount, $"Ref {payment.Reference}")],
                controller.Email, payment.CreatedAtUtc);
            await _bulk.AddAsync(payment);
            await _bulk.AddAsync(new PaymentApplication { PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = invoice.Total });
            _openInvoices.Remove(invoice);
            _paid.Add(invoice.Id);
        }
    }

    /// <summary>Invoices paid in full after they were inserted (their status is updated with the funding pass).</summary>
    private readonly HashSet<Guid> _paid = [];
}
