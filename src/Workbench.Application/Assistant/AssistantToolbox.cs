using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Time;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>What a tool returns: the data for the model, a one-line summary with the key numbers, pages and records read.</summary>
public sealed record AssistantToolResult(
    AssistantToolOutcome Outcome,
    object Data,
    string Summary,
    IReadOnlyList<AssistantCitation> Citations,
    IReadOnlyList<string> Records)
{
    public static AssistantToolResult Error(AssistantToolOutcome outcome, string message) =>
        new(outcome, new { error = outcome.ToString().ToLowerInvariant(), message }, message, [], []);
}

/// <summary>A tool the model may call: its name, what it's for, what the panel says while it runs, and its input schema.</summary>
public sealed record AssistantToolSpec(string Name, string Description, string Activity, JsonObject Schema)
{
    public ModelToolDefinition Definition => new(Name, Description, Schema);
}

/// <summary>
/// The assistant's fixed, read-only tools. Every one calls the same permission-checked services the pages use, resolved
/// from the caller's scope, so it runs as the signed-in user and sees exactly what they could open. There are no write
/// tools, no SQL and no free-form queries: arguments are names, codes and dates, validated here.
/// </summary>
public sealed class AssistantToolbox(IServiceProvider services, TimeProvider clock, ILogger<AssistantToolbox> logger)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<AssistantToolSpec> Specs { get; } =
    [
        Spec("list_projects", "Lists projects (code, name, contract, status). Use it to find a project's code.", "Looking up projects",
            ("query", "string", "Optional text to match in the project code or name", false)),
        Spec("get_project_financials",
            "A project's budget, actual cost, estimate at completion (EAC), variance at completion (VAC) and funding, with the WBS elements furthest over budget. Use for 'why is the project over budget' questions.",
            "Reading project financials",
            ("project", "string", "Project code (e.g. 1001) or name", true)),
        Spec("get_budget_versions", "A project's budget versions (baseline, current, forecasts) with totals.", "Reading budget versions",
            ("project", "string", "Project code or name", true)),
        Spec("get_earned_value",
            "Earned value status for a project's reporting period: BCWS, BCWP, ACWP, SV, CV, SPI, CPI, EAC, VAC per control account, and the variance analyses (cause, impact, corrective action).",
            "Reading earned value",
            ("project", "string", "Project code or name", true)),
        Spec("list_clin_funding",
            "Funding status of CLINs on the contracts the user may see: funded value, incurred, remaining, burn per day and projected exhaustion date. Use for 'which CLINs will run out of funding' questions.",
            "Reading CLIN funding",
            ("contract", "string", "Optional contract number (or its start)", false),
            ("within_days", "integer", "Only CLINs projected to run out within this many days (e.g. 92 for a quarter)", false)),
        Spec("get_ceilings", "Hours, cost and billing ceilings that apply to a project or contract, with consumption and alert level.", "Reading ceilings",
            ("project", "string", "Project code or name", false),
            ("contract", "string", "Contract number", false)),
        Spec("get_timesheet_status",
            "Who has and hasn't submitted a timesheet for a period (the user's direct reports, or everyone for timekeepers and Finance).",
            "Checking timesheet submissions",
            ("date", "string", "A date in the timesheet period, yyyy-MM-dd (default: today)", false)),
        Spec("get_timesheet_flags",
            "Timesheets with deterministic anomaly flags (weekend/holiday hours, late entries, revisions, closed or ceiling-limited codes, unusual patterns) for the period.",
            "Reading timesheet flags",
            ("from", "string", "Start date yyyy-MM-dd (default: 30 days ago)", false),
            ("to", "string", "End date yyyy-MM-dd (default: today)", false)),
        Spec("get_hours_by_charge_code", "Hours by employee and charge code for a date range (approved timesheets only unless include_unapproved).", "Reading hours",
            ("from", "string", "Start date yyyy-MM-dd", true),
            ("to", "string", "End date yyyy-MM-dd", true),
            ("project", "string", "Optional project code to filter", false),
            ("include_unapproved", "boolean", "Include submitted and draft hours", false)),
        Spec("list_pending_approvals", "Everything waiting on the user's approval (timesheets, expenses, ODCs, bills, invoices, journal entries, leave).", "Reading your approvals"),
        Spec("list_unallowable_costs", "Costs flagged unallowable (FAR 31.205) on a contract's or project's ODCs and expense lines.", "Reading unallowable costs",
            ("contract", "string", "Contract number (or its start)", false),
            ("project", "string", "Project code or name", false)),
        Spec("get_project_cost_ledger", "General ledger cost by WBS for a project (direct labor, ODCs, indirect, unallowable) and its latest ledger lines.", "Reading the ledger",
            ("project", "string", "Project code or name", true)),
        Spec("get_project_staffing", "A project's staffing: budget hours, actuals and allocations by labor category, and gaps.", "Reading staffing",
            ("project", "string", "Project code or name", true)),
        Spec("list_sheets", "Sheets the user can open (name, workspace, row count).", "Looking up sheets",
            ("query", "string", "Optional text to match in the sheet name", false)),
        Spec("read_sheet", "Rows of a sheet the user can open, with the columns they may see (restricted columns stay hidden).", "Reading a sheet",
            ("sheet", "string", "Sheet name", true),
            ("query", "string", "Optional text to match in the row title", false),
            ("max_rows", "integer", "Rows to return (default 50, at most 200)", false)),
        Spec("get_custom_fields", "A record's custom fields (administrator-defined details such as program office); sensitive ones stay masked.", "Reading custom fields",
            ("entity", "string", "Record type: project, contract, wbs-element, employee, vendor, customer, purchase-order, ap-bill, invoice, expense-report, timesheet, part or opportunity", true),
            ("record_id", "string", "The record's id (from another tool's records, such as Project:<id>)", true)),
        Spec("list_custom_field_matches", "Records whose custom field values contain some text (for example every project in a program office).", "Searching custom fields",
            ("text", "string", "Text to look for (at least 2 characters)", true),
            ("entity", "string", "Optional record type to limit the search to", false)),
    ];

    public static AssistantToolSpec? Find(string name) => Specs.FirstOrDefault(s => s.Name == name);

    /// <summary>Runs a tool. Never throws for permission, validation or missing records: those become an error result.</summary>
    public async Task<AssistantToolResult> RunAsync(string name, string inputJson, CancellationToken cancellationToken)
    {
        if (Find(name) is null)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.Invalid, $"There is no tool named '{Clip(name, 40)}'. The assistant can only read data with its listed tools.");
        }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Tool input must be a JSON object.");
            }

            var args = new Args(doc.RootElement, Find(name)!);
            return name switch
            {
                "list_projects" => await ListProjectsAsync(args, cancellationToken),
                "get_project_financials" => await ProjectFinancialsAsync(args, cancellationToken),
                "get_budget_versions" => await BudgetVersionsAsync(args, cancellationToken),
                "get_earned_value" => await EarnedValueAsync(args, cancellationToken),
                "list_clin_funding" => await ClinFundingAsync(args, cancellationToken),
                "get_ceilings" => await CeilingsAsync(args, cancellationToken),
                "get_timesheet_status" => await TimesheetStatusAsync(args, cancellationToken),
                "get_timesheet_flags" => await TimesheetFlagsAsync(args, cancellationToken),
                "get_hours_by_charge_code" => await HoursAsync(args, cancellationToken),
                "list_pending_approvals" => await ApprovalsAsync(cancellationToken),
                "list_unallowable_costs" => await UnallowableAsync(args, cancellationToken),
                "get_project_cost_ledger" => await LedgerAsync(args, cancellationToken),
                "get_project_staffing" => await StaffingAsync(args, cancellationToken),
                "list_sheets" => await ListSheetsAsync(args, cancellationToken),
                "read_sheet" => await ReadSheetAsync(args, cancellationToken),
                "get_custom_fields" => await CustomFieldsAsync(args, cancellationToken),
                "list_custom_field_matches" => await CustomFieldMatchesAsync(args, cancellationToken),
                _ => AssistantToolResult.Error(AssistantToolOutcome.Invalid, "Unknown tool."),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.Denied, $"Access denied: {ex.Message} The assistant can only read what you can open in Workbench.");
        }
        catch (WorkbenchNotFoundException ex)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.NotFound, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.Invalid, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant tool {Tool} failed", name);
            return AssistantToolResult.Error(AssistantToolOutcome.Failed, "That information couldn't be read just now.");
        }
    }

    // ---- Tools ----

    private async Task<AssistantToolResult> ListProjectsAsync(Args args, CancellationToken ct)
    {
        var query = args.Text("query");
        var projects = (await Get<IProjectService>().ListProjectsAsync(ct))
            .Where(p => query is null || p.Code.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .Take(50)
            .ToList();
        var summary = projects.Count == 0
            ? "No projects match."
            : $"{projects.Count} project{(projects.Count == 1 ? "" : "s")}: " + string.Join(", ", projects.Take(10).Select(p => $"{p.Code} {p.Name} ({p.Status})")) + (projects.Count > 10 ? ", …" : "");
        return new(AssistantToolOutcome.Ok,
            new { summary, projects = projects.Select(p => new { p.Code, p.Name, contract = p.ContractNumber, status = p.Status.ToString(), start = p.Start, finish = p.Finish }) },
            summary, [new("Projects", "projects")], projects.Select(p => $"Project:{p.Id}").ToList());
    }

    private async Task<AssistantToolResult> ProjectFinancialsAsync(Args args, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(args.Required("project"), ct);
        var financials = Get<IProjectFinancialsService>();
        var summaryDto = await financials.GetProjectSummaryAsync(project.Id, cancellationToken: ct);
        var wbs = await financials.GetWbsFinancialsAsync(project.Id, cancellationToken: ct);
        var t = summaryDto.Totals;
        var worst = wbs.Where(w => w.IsLeaf && w.Figures.BudgetAmount + w.Figures.ActualCost > 0)
            .OrderBy(w => w.Figures.VacAmount)
            .Take(8)
            .Select(w => new
            {
                wbs = w.Code, name = w.Name, budget = w.Figures.BudgetAmount, actual = w.Figures.ActualCost, eac = w.Figures.EacAmount, vac = w.Figures.VacAmount,
                percentSpent = w.Figures.PercentSpent, actualHours = w.Figures.ActualHours, budgetHours = w.Figures.BudgetHours,
            })
            .ToList();
        var over = worst.Where(w => w.vac < 0).ToList();
        var summary = $"Project {project.Code}: budget {Money(t.BudgetAmount)} ({summaryDto.BudgetVersionName ?? "no budget"}), actual cost {Money(t.ActualCost)} "
            + $"(labor {Money(t.ActualLaborCost)}, ODCs {Money(t.ActualOdcCost)}), EAC {Money(t.EacAmount)}, VAC {Money(t.VacAmount)}"
            + (t.PercentSpent is { } ps ? $", {Percent(ps)} of budget spent" : "")
            + (over.Count > 0 ? $"; furthest over: {string.Join(", ", over.Take(3).Select(w => $"{w.wbs} {w.name} (VAC {Money(w.vac)})"))}" : "; no WBS element is projected over budget")
            + (summaryDto.Funding is { } f ? $"; contract {f.ContractNumber} funded {Money(f.FundedValue)}, {Money(f.FundedRemaining)} remaining" : "")
            + $" (as of {summaryDto.AsOf:yyyy-MM-dd}).";
        return new(AssistantToolOutcome.Ok,
            new
            {
                summary,
                project = new { project.Code, project.Name, contract = summaryDto.ContractNumber, contractType = summaryDto.ContractType?.ToString() },
                asOf = summaryDto.AsOf,
                budgetVersion = summaryDto.BudgetVersionName,
                totals = new
                {
                    budget = t.BudgetAmount, budgetHours = t.BudgetHours, actualCost = t.ActualCost, actualLabor = t.ActualLaborCost, actualOdc = t.ActualOdcCost,
                    actualHours = t.ActualHours, percentSpent = t.PercentSpent, remainingBudget = t.RemainingBudget, etc = t.EtcAmount, eac = t.EacAmount, vac = t.VacAmount,
                },
                wbsFurthestOverBudget = worst,
                funding = summaryDto.Funding is { } fund
                    ? new { fund.ContractNumber, fund.FundedValue, fund.Spent, fund.FundedRemaining, fund.PercentOfFundedSpent, fund.FundingExhaustionDate, alerts = fund.Alerts.Select(a => a.Message) }
                    : null,
                notes = summaryDto.Notes,
            },
            summary,
            [new($"{project.Code} financials", $"projects/{project.Id}/financials")],
            [$"Project:{project.Id}", .. wbs.Select(w => $"WbsElement:{w.WbsElementId}").Take(40)]);
    }

    private async Task<AssistantToolResult> BudgetVersionsAsync(Args args, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(args.Required("project"), ct);
        var versions = await Get<IBudgetService>().ListVersionsAsync(project.Id, ct);
        var summary = versions.Count == 0
            ? $"Project {project.Code} has no budget versions."
            : $"Project {project.Code} budget versions: " + string.Join("; ", versions.Select(v => $"{v.Name} ({v.Kind}{(v.IsLocked ? ", locked" : "")}) {Money(v.TotalAmount)}, {v.TotalHours:N0} h"));
        return new(AssistantToolOutcome.Ok,
            new { summary, versions = versions.Select(v => new { v.Name, kind = v.Kind.ToString(), v.IsLocked, v.TotalAmount, v.TotalHours, v.LineCount, created = v.CreatedAtUtc, v.LockedAtUtc }) },
            summary, [new($"{project.Code} budgets", $"projects/{project.Id}/budgets")], [$"Project:{project.Id}", .. versions.Select(v => $"BudgetVersion:{v.Id}")]);
    }

    private async Task<AssistantToolResult> EarnedValueAsync(Args args, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(args.Required("project"), ct);
        var evm = Get<IEvmService>();
        if (await evm.GetBaselineAsync(project.Id, ct) is null)
        {
            var none = $"Project {project.Code} has no earned value baseline (PMB), so there are no EVM figures.";
            return new(AssistantToolOutcome.Ok, new { summary = none }, none, [new($"{project.Code} earned value", $"projects/{project.Id}/evm")], [$"Project:{project.Id}"]);
        }

        var status = await evm.GetStatusAsync(project.Id, cancellationToken: ct);
        var variances = await evm.ListVariancesAsync(project.Id, cancellationToken: ct);
        var t = status.Total;
        var summary = $"Project {project.Code} earned value for {status.Period.Name}{(status.IsFrozen ? " (closed)" : " (open)")}: BAC {Money(t.Bac)}, BCWS {Money(t.Bcws)}, BCWP {Money(t.Bcwp)}, ACWP {Money(t.Acwp)}, "
            + $"SV {Money(t.Sv)}, CV {Money(t.Cv)}, SPI {Index(t.Spi)}, CPI {Index(t.Cpi)}, EAC {Money(t.Eac)}, VAC {Money(t.Vac)}"
            + (variances.Count(v => v.NeedsVar) is var n and > 0 ? $"; {n} control account{(n == 1 ? "" : "s")} beyond variance thresholds" : "") + ".";
        return new(AssistantToolOutcome.Ok,
            new
            {
                summary,
                period = new { status.Period.Name, status.Period.Start, status.Period.End, state = status.Period.State.ToString(), status.IsFrozen },
                totals = Metrics(t),
                controlAccounts = status.ControlAccounts.Select(ca => new
                {
                    code = ca.Code, name = ca.Name, cumulative = Metrics(ca.Cumulative), period = new { ca.Period.Bcws, ca.Period.Bcwp, ca.Period.Acwp, ca.Period.Sv, ca.Period.Cv },
                    ca.NeedsVar, reasons = ca.VarianceReasons,
                }),
                variances = variances.Where(v => v.NeedsVar || v.Cause is not null).Select(v => new { v.Code, v.Name, v.Sv, v.Cv, v.Vac, v.Cause, v.Impact, v.CorrectiveAction, v.ManagerEac }),
                managementReserve = status.ManagementReserve,
                undistributedBudget = status.UndistributedBudget,
                costBasis = status.CostBasis,
                notes = status.Notes,
            },
            summary, [new($"{project.Code} earned value", $"projects/{project.Id}/evm")],
            [$"Project:{project.Id}", .. status.ControlAccounts.Select(c => $"WbsElement:{c.WbsElementId}")]);
    }

    private async Task<AssistantToolResult> ClinFundingAsync(Args args, CancellationToken ct)
    {
        var contractRef = args.Text("contract");
        var withinDays = args.Integer("within_days", 1, 3650);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var contracts = await ContractsAsync(contractRef, ct);
        var funding = Get<IFundingService>();
        var finance = (await Get<ICurrentUserContext>().GetAsync(ct)).Can(Permission.ProjectsFinancialsView);
        var rows = new List<object>();
        var lines = new List<string>();
        var citations = new List<AssistantCitation>();
        var records = new List<string>();
        var skipped = 0;
        foreach (var contract in contracts)
        {
            ContractFundingStatusDto status;
            try
            {
                status = await funding.GetFundingStatusAsync(contract.Id, ct);
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            records.Add($"Contract:{contract.Id}");
            var clins = status.Clins
                .Where(c => withinDays is null
                    || c.Level is FundingLevel.Exhausted
                    || (c.ProjectedExhaustion is { } d && d <= today.AddDays(withinDays.Value)))
                .ToList();
            if (clins.Count == 0)
            {
                continue;
            }

            citations.Add(new($"{contract.ContractNumber} funding", finance ? $"setup/contracts/{contract.Id}" : "reports/standard/funding-status"));
            foreach (var c in clins)
            {
                records.Add($"Clin:{c.ClinId}");
                rows.Add(new
                {
                    contract = contract.ContractNumber, clin = c.Number, c.Description, funded = c.FundedValue, incurred = c.Incurred, committed = c.Committed, remaining = c.Remaining,
                    percentIncurred = c.PercentIncurred, burnPerDay = c.BurnPerDay, projectedExhaustion = c.ProjectedExhaustion, level = c.Level.ToString(), periodEnd = c.PeriodEnd,
                });
                lines.Add($"{contract.ContractNumber} CLIN {c.Number}: {Money(c.Remaining)} of {Money(c.FundedValue)} left, burning {Money(c.BurnPerDay)}/day, "
                    + (c.ProjectedExhaustion is { } x ? $"runs out about {x:yyyy-MM-dd}" : c.Level == FundingLevel.Exhausted ? "already exhausted" : "no projected exhaustion"));
            }
        }

        var summary = lines.Count == 0
            ? $"No CLIN{(withinDays is { } w ? $" is projected to run out of funding within {w} days" : " funding found")} on the {contracts.Count - skipped} contract(s) you can see."
            : string.Join("; ", lines) + ".";
        if (skipped > 0)
        {
            summary += $" ({skipped} contract(s) skipped: you don't have access to their funding.)";
        }

        return new(AssistantToolOutcome.Ok, new { summary, asOf = today, withinDays, clins = rows }, summary,
            citations.Count > 0 ? citations : [new("Contract funding", finance ? "setup/contracts" : "reports/standard/funding-status")], records);
    }

    private async Task<AssistantToolResult> CeilingsAsync(Args args, CancellationToken ct)
    {
        var ceilings = Get<ICeilingService>();
        IReadOnlyList<CeilingStatusDto> list;
        string scope;
        AssistantCitation citation;
        var records = new List<string>();
        if (args.Text("project") is { } projectRef)
        {
            var project = await ResolveProjectAsync(projectRef, ct);
            list = (await ceilings.GetProjectStatusAsync(project.Id, ct)).Ceilings;
            scope = $"project {project.Code}";
            citation = new($"{project.Code} ceilings", $"projects/{project.Id}/ceilings");
            records.Add($"Project:{project.Id}");
        }
        else if (args.Text("contract") is { } contractRef)
        {
            var contract = (await ContractsAsync(contractRef, ct)).FirstOrDefault() ?? throw new WorkbenchNotFoundException($"No contract matches “{contractRef}”.");
            list = (await ceilings.GetContractStatusAsync(contract.Id, ct)).Ceilings;
            scope = $"contract {contract.ContractNumber}";
            citation = new($"{contract.ContractNumber} ceilings", $"setup/contracts/{contract.Id}");
            records.Add($"Contract:{contract.Id}");
        }
        else
        {
            throw new ArgumentException("Say which project or contract.");
        }

        records.AddRange(list.Select(c => $"Ceiling:{c.Ceiling.Id}"));
        var rows = list.Select(c => new
        {
            c.Ceiling.Label, mode = c.Ceiling.Mode.ToString(), level = c.Level.ToString(),
            measures = c.Measures.Select(m => new { metric = m.Metric.ToString(), m.Limit, m.EffectiveLimit, m.Consumed, m.Remaining, m.PercentConsumed, level = m.Level.ToString() }),
        }).ToList();
        var summary = list.Count == 0
            ? $"No ceilings apply to {scope}."
            : $"{list.Count} ceiling(s) on {scope}: " + string.Join("; ", list.Select(c => $"{c.Ceiling.Label} ({c.Ceiling.Mode}) " + string.Join(", ",
                c.Measures.Select(m => $"{m.Metric} {Percent(m.PercentConsumed)} used ({Amount(m.Metric, m.Consumed)} of {Amount(m.Metric, m.EffectiveLimit ?? m.Limit)})")) + $", {c.Level}")) + ".";
        return new(AssistantToolOutcome.Ok, new { summary, ceilings = rows }, summary, [citation], records);
    }

    private async Task<AssistantToolResult> TimesheetStatusAsync(Args args, CancellationToken ct)
    {
        var date = args.Date("date") ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var rows = await Get<ITimesheetSubmissionService>().GetStatusAsync(date, ct);
        var missing = rows.Where(r => r.Status is null or TimesheetState.Draft or TimesheetState.Rejected).ToList();
        var period = rows.FirstOrDefault() is { } first ? $"{first.PeriodStart:MMM d}–{first.PeriodEnd:MMM d, yyyy}" : $"the period containing {date:yyyy-MM-dd}";
        var summary = rows.Count == 0
            ? "You don't have anyone's timesheets to check (no direct reports, and you aren't a timekeeper or in Finance)."
            : missing.Count == 0
                ? $"Everyone ({rows.Count}) has submitted a timesheet for {period}."
                : $"{missing.Count} of {rows.Count} haven't submitted for {period}: " + string.Join(", ", missing.Select(r => $"{r.EmployeeName} ({(r.Status is null ? "no timesheet" : r.Status.ToString()!.ToLowerInvariant())}, {r.Hours:0.#} of {r.StandardHours:0.#} h)")) + ".";
        return new(AssistantToolOutcome.Ok,
            new { summary, period, people = rows.Select(r => new { r.EmployeeName, supervisor = r.SupervisorName, status = r.Status?.ToString() ?? "None", r.Hours, r.StandardHours, submitted = r.Status is TimesheetState.Submitted or TimesheetState.Approved }) },
            summary, rows.Count == 0 ? [] : [new("Team time", $"time/team?date={date:yyyy-MM-dd}")],
            rows.Where(r => r.TimesheetId is not null).Select(r => $"Timesheet:{r.TimesheetId}").Concat(rows.Select(r => $"Employee:{r.EmployeeId}")).ToList());
    }

    private async Task<AssistantToolResult> TimesheetFlagsAsync(Args args, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var to = args.Date("to") ?? today;
        var from = args.Date("from") ?? to.AddDays(-30);
        var flagged = await Get<ITimesheetFlagService>().ReportAsync(new TimesheetFlagReportQuery(from, to), ct);
        var summary = flagged.Count == 0
            ? $"No timesheets you review have flags between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}."
            : $"{flagged.Count} flagged timesheet(s) between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}: " + string.Join("; ", flagged.Take(10).Select(f =>
                $"{f.EmployeeName} {f.PeriodStart:MMM d} ({string.Join(", ", f.Flags.Select(x => x.Title))})")) + ". Flags are prompts to review, not findings.";
        return new(AssistantToolOutcome.Ok,
            new { summary, timesheets = flagged.Take(40).Select(f => new { f.EmployeeName, f.PeriodStart, f.PeriodEnd, status = f.Status.ToString(), f.TotalHours, flags = f.Flags.Select(x => new { x.Title, severity = x.Severity.ToString(), x.Detail }) }) },
            summary,
            [new("Timesheet flags", $"time/flags?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"), .. flagged.Take(5).Select(f => new AssistantCitation($"{f.EmployeeName}, {f.PeriodStart:MMM d}", f.Link))],
            flagged.Select(f => $"Timesheet:{f.TimesheetId}").ToList());
    }

    private async Task<AssistantToolResult> HoursAsync(Args args, CancellationToken ct)
    {
        var from = args.Date("from") ?? throw new ArgumentException("from is required (yyyy-MM-dd).");
        var to = args.Date("to") ?? throw new ArgumentException("to is required (yyyy-MM-dd).");
        if (to < from || to.DayNumber - from.DayNumber > 400)
        {
            throw new ArgumentException("Use a date range of at most 400 days, with from before to.");
        }

        var projectCode = args.Text("project") is { } p ? (await ResolveProjectAsync(p, ct)).Code : null;
        var rows = (await Get<ITimeReportService>().GetHoursByChargeCodeAsync(from, to, !args.Bool("include_unapproved"), ct))
            .Where(r => projectCode is null || r.ProjectCode == projectCode)
            .ToList();
        var total = rows.Sum(r => r.Hours);
        var summary = $"{total:N1} hours between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}{(projectCode is null ? "" : $" on project {projectCode}")} by {rows.Select(r => r.EmployeeId).Distinct().Count()} people"
            + (rows.Count > 0 ? ": " + string.Join(", ", rows.GroupBy(r => r.EmployeeName).OrderByDescending(g => g.Sum(r => r.Hours)).Take(8).Select(g => $"{g.Key} {g.Sum(r => r.Hours):N1} h")) : "") + ".";
        return new(AssistantToolOutcome.Ok, new { summary, rows = rows.Take(200).Select(r => new { r.EmployeeName, r.ProjectCode, r.WbsCode, r.Hours }) }, summary,
            [new("Team time", "time/team")], rows.Select(r => $"Employee:{r.EmployeeId}").Distinct().ToList());
    }

    private async Task<AssistantToolResult> ApprovalsAsync(CancellationToken ct)
    {
        var items = await Get<IApprovalInboxService>().ListMineAsync(ct);
        var summary = items.Count == 0
            ? "Nothing is waiting on your approval."
            : $"{items.Count} item(s) waiting on you: " + string.Join("; ", items.Take(10).Select(i =>
                $"{i.DocumentLabel} “{i.Title}” from {i.SubmitterEmail}{(i.Amount is { } a ? ApprovalDocumentTypes.AmountIsHours(i.DocumentType) ? $", {a:0.#} h" : $", {Money(a)}" : "")}, waiting {i.BusinessDaysWaiting} business day(s)")) + ".";
        return new(AssistantToolOutcome.Ok,
            new { summary, items = items.Select(i => new { type = i.DocumentLabel, i.Title, i.Summary, i.Amount, amountIsHours = ApprovalDocumentTypes.AmountIsHours(i.DocumentType), from = i.SubmitterEmail, step = i.StepName, i.BusinessDaysWaiting }) },
            summary,
            [new("Approvals", "approvals"), .. items.Take(5).Where(i => IsAppRelative(i.Link)).Select(i => new AssistantCitation(i.Title, i.Link))],
            items.Select(i => $"{i.DocumentType}:{i.DocumentId}").ToList());
    }

    private async Task<AssistantToolResult> UnallowableAsync(Args args, CancellationToken ct)
    {
        IReadOnlyList<ProjectDto> projects;
        string scope;
        var all = await Get<IProjectService>().ListProjectsAsync(ct);
        if (args.Text("contract") is { } contractRef)
        {
            var contract = (await ContractsAsync(contractRef, ct)).FirstOrDefault() ?? throw new WorkbenchNotFoundException($"No contract matches “{contractRef}”.");
            projects = all.Where(p => p.ContractId == contract.Id).ToList();
            scope = $"contract {contract.ContractNumber}";
        }
        else if (args.Text("project") is { } projectRef)
        {
            projects = [await ResolveProjectAsync(projectRef, ct)];
            scope = $"project {projects[0].Code}";
        }
        else
        {
            throw new ArgumentException("Say which contract or project.");
        }

        var odcs = Get<IOdcService>();
        var lines = new List<OdcEntryDto>();
        var citations = new List<AssistantCitation>();
        var denied = 0;
        foreach (var project in projects)
        {
            try
            {
                var list = await odcs.ListProjectOdcsAsync(project.Id, new OdcListQuery(IncludeExpenses: true), ct);
                var unallowable = list.Entries.Where(e => e.IsUnallowable).ToList();
                lines.AddRange(unallowable);
                if (unallowable.Count > 0)
                {
                    citations.Add(new($"{project.Code} costs", $"projects/{project.Id}/costs"));
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
        }

        if (denied > 0 && denied == projects.Count)
        {
            throw new WorkbenchForbiddenException($"You can't see the costs of {scope}.");
        }

        var total = lines.Sum(l => l.Amount);
        var summary = lines.Count == 0
            ? $"No unallowable costs recorded on {scope}."
            : $"{lines.Count} unallowable cost line(s) on {scope}, {Money(total)} in total: " + string.Join("; ", lines.OrderByDescending(l => l.Amount).Take(8).Select(l =>
                $"{l.IncurredOn:yyyy-MM-dd} {l.ProjectCode} {l.Type} {Money(l.Amount)} “{Clip(l.Description, 60)}”{(l.UnallowableReason is { } r ? $" ({Clip(r, 60)})" : "")}")) + ".";
        if (denied > 0)
        {
            summary += $" ({denied} project(s) skipped: you can't see their costs.)";
        }

        return new(AssistantToolOutcome.Ok,
            new { summary, total, lines = lines.Select(l => new { project = l.ProjectCode, wbs = l.WbsCode, type = l.Type.ToString(), date = l.IncurredOn, l.Amount, l.Description, l.Vendor, reason = l.UnallowableReason, status = l.Status.ToString() }) },
            summary, citations.Count > 0 ? citations : [new("Project costs", "odcs")], lines.Select(l => $"OdcEntry:{l.Id}").ToList());
    }

    private async Task<AssistantToolResult> LedgerAsync(Args args, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(args.Required("project"), ct);
        var reports = Get<IAccountingReportService>();
        var cost = await reports.GetProjectCostLedgerAsync(project.Id, cancellationToken: ct);
        var detail = await reports.GetLedgerDetailAsync(new LedgerQuery(ProjectId: project.Id, Take: 40), ct);
        var summary = $"Project {project.Code} ledger cost: direct labor {Money(cost.Sum(r => r.DirectLabor))}, direct ODCs {Money(cost.Sum(r => r.DirectOdc))}, indirect {Money(cost.Sum(r => r.Indirect))}, "
            + $"unallowable {Money(cost.Sum(r => r.Unallowable))}, total {Money(cost.Sum(r => r.Total))} ({detail.Lines.Count} recent ledger lines).";
        return new(AssistantToolOutcome.Ok,
            new
            {
                summary,
                byWbs = cost.Select(r => new { r.WbsCode, r.WbsName, r.DirectLabor, r.DirectOdc, r.Indirect, r.Unallowable, r.Other, r.Total }),
                recentLines = detail.Lines.Select(l => new { l.EntryNumber, l.Date, l.PeriodName, source = l.Source.ToString(), l.Description, account = $"{l.AccountNumber} {l.AccountName}", l.WbsCode, l.Debit, l.Credit }),
            },
            summary, [new("General ledger reports", "accounting/reports")],
            [$"Project:{project.Id}", .. detail.Lines.Select(l => $"JournalEntry:{l.JournalEntryId}").Distinct()]);
    }

    private async Task<AssistantToolResult> StaffingAsync(Args args, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(args.Required("project"), ct);
        var staffing = await Get<IResourcePlanningService>().GetProjectStaffingAsync(project.Id, ct);
        var gaps = staffing.Rows.Where(r => r.Gap > 0).ToList();
        var summary = $"Project {project.Code} staffing: {staffing.RemainingBudgetHours:N0} budget hours remain and {staffing.AllocatedHours:N0} are allocated"
            + (gaps.Count > 0 ? "; unallocated: " + string.Join(", ", gaps.Select(g => $"{g.LaborCategory} {g.Gap:N0} h")) : "") + ".";
        return new(AssistantToolOutcome.Ok,
            new { summary, rows = staffing.Rows.Select(r => new { r.LaborCategory, r.BudgetHours, r.ActualHours, r.RemainingBudgetHours, r.AllocatedHours, r.Gap, status = r.Status.ToString(), r.People }), notes = staffing.Notes },
            summary, [new("Resourcing", "resourcing"), new($"{project.Code} team", $"projects/{project.Id}/team")], [$"Project:{project.Id}"]);
    }

    private async Task<AssistantToolResult> ListSheetsAsync(Args args, CancellationToken ct)
    {
        var query = args.Text("query");
        var sheets = (await Get<ISheetService>().ListAsync(ct))
            .Where(s => query is null || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        var summary = sheets.Count == 0 ? "No sheets match." : $"{sheets.Count} sheet(s): " + string.Join(", ", sheets.Take(10).Select(s => $"{s.Name} ({s.Workspace}, {s.ItemCount} rows)"));
        return new(AssistantToolOutcome.Ok, new { summary, sheets = sheets.Select(s => new { s.Name, s.Workspace, rows = s.ItemCount, updated = s.UpdatedAtUtc }) }, summary,
            [new("Sheets", "sheets")], sheets.Select(s => $"Sheet:{s.Id}").ToList());
    }

    private async Task<AssistantToolResult> ReadSheetAsync(Args args, CancellationToken ct)
    {
        var name = args.Required("sheet");
        var sheetService = Get<ISheetService>();
        var sheets = await sheetService.ListAsync(ct);
        var match = sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? sheets.SingleOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchNotFoundException($"No sheet you can open is named “{Clip(name, 60)}”.");
        var detail = await sheetService.GetAsync(match.Id, ct) ?? throw new WorkbenchNotFoundException($"No sheet you can open is named “{Clip(name, 60)}”.");

        // The permission decorators already removed restricted columns and their values for this user; only the
        // columns the sheet returns are read, and values are keyed by those columns.
        var columns = detail.Columns.OrderBy(c => c.Order).ToList();
        var items = await Get<IItemService>().ListAsync(match.Id, ct);
        var query = args.Text("query");
        var max = args.Integer("max_rows", 1, 200) ?? 50;
        var rows = items
            .Where(i => query is null || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Order)
            .Take(max)
            .Select(i => new Dictionary<string, string?>(columns
                    .Where(c => i.Values.ContainsKey(c.Id.ToString()))
                    .Select(c => new KeyValuePair<string, string?>(c.Name, Clip(i.Values[c.Id.ToString()], 500))))
                {
                    ["Row id"] = i.Id.ToString(),
                    ["Title"] = Clip(i.Title, 300),
                    ["Start"] = i.Start?.ToString("yyyy-MM-dd", Invariant),
                    ["Finish"] = i.Finish?.ToString("yyyy-MM-dd", Invariant),
                })
            .ToList();
        var summary = $"Sheet “{match.Name}”: {items.Count} rows, {rows.Count} returned, columns {string.Join(", ", columns.Select(c => c.Name))}.";
        return new(AssistantToolOutcome.Ok, new { summary, sheet = match.Name, columns = columns.Select(c => new { c.Name, type = c.Type.ToString() }), rows }, summary,
            [new(match.Name, $"sheets/{match.Id}")], [$"Sheet:{match.Id}", .. items.Take(max).Select(i => $"Item:{i.Id}")]);
    }

    // ---- Helpers ----

    private async Task<AssistantToolResult> CustomFieldsAsync(Args args, CancellationToken ct)
    {
        var entity = args.Required("entity");
        var id = Guid.TryParse(args.Required("record_id").Split(':')[^1], out var g) ? g : throw new ArgumentException("'record_id' must be a record id.");
        var record = await Get<Contracts.Customization.ICustomFieldService>().GetValuesAsync(entity, id, ct);
        var fields = record.Fields.Select(f => new { field = f.Label, key = f.Key, value = f.Display, masked = f.IsMasked }).ToList();
        var summary = fields.Count == 0 ? "No custom fields are defined for this record type."
            : string.Join("; ", fields.Select(f => $"{f.field}: {f.value ?? "(blank)"}"));
        return new(AssistantToolOutcome.Ok, new { summary, entity, recordId = id, fields }, Clip(summary, 400)!, [], [$"{entity}:{id}"]);
    }

    private async Task<AssistantToolResult> CustomFieldMatchesAsync(Args args, CancellationToken ct)
    {
        var hits = await Get<Contracts.Customization.ICustomFieldService>().SearchAsync(args.Required("text"), args.Text("entity"), 50, ct);
        var summary = hits.Count == 0 ? "No custom field values match."
            : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}: " + string.Join(", ", hits.Take(10).Select(h => $"{h.Title} ({h.FieldLabel}: {h.Display})")) + (hits.Count > 10 ? ", …" : "");
        return new(AssistantToolOutcome.Ok,
            new { summary, matches = hits.Select(h => new { type = h.EntityLabel, h.Title, field = h.FieldLabel, value = h.Display, link = h.Link }) },
            summary, hits.Where(h => h.Link is not null).Take(10).Select(h => new AssistantCitation(h.Title, h.Link!)).ToList(), hits.Select(h => $"{h.EntityType}:{h.RecordId}").ToList());
    }

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private async Task<ProjectDto> ResolveProjectAsync(string reference, CancellationToken ct)
    {
        var projects = await Get<IProjectService>().ListProjectsAsync(ct);
        var r = reference.Trim();
        if (Guid.TryParse(r, out var id) && projects.FirstOrDefault(p => p.Id == id) is { } byId)
        {
            return byId;
        }

        return projects.FirstOrDefault(p => string.Equals(p.Code, r, StringComparison.OrdinalIgnoreCase))
            ?? Single(projects.Where(p => p.Code.StartsWith(r, StringComparison.OrdinalIgnoreCase)).ToList())
            ?? Single(projects.Where(p => p.Name.Contains(r, StringComparison.OrdinalIgnoreCase)).ToList())
            ?? throw new WorkbenchNotFoundException($"No project matches “{Clip(r, 60)}”. Use list_projects to find its code.");
    }

    private async Task<IReadOnlyList<ContractDto>> ContractsAsync(string? reference, CancellationToken ct)
    {
        var contracts = await Get<IContractService>().ListContractsAsync(ct);
        if (reference is null)
        {
            return contracts.Where(c => c.Status != ContractState.Closed).ToList();
        }

        var r = reference.Trim().TrimEnd('.', '…', '*').Trim();
        if (r.Length == 0)
        {
            throw new ArgumentException("Say which contract.");
        }

        var exact = contracts.Where(c => string.Equals(c.ContractNumber, r, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        var partial = contracts.Where(c => Normalize(c.ContractNumber).StartsWith(Normalize(r), StringComparison.OrdinalIgnoreCase)
            || Normalize(c.ContractNumber).Contains(Normalize(r), StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count > 0 ? partial : throw new WorkbenchNotFoundException($"No contract matches “{Clip(r, 60)}”.");

        static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    }

    private static T? Single<T>(IReadOnlyList<T> list) where T : class => list.Count == 1 ? list[0] : null;

    private static object Metrics(EvmMetrics m) => new
    {
        bac = m.Bac, bcws = m.Bcws, bcwp = m.Bcwp, acwp = m.Acwp, sv = m.Sv, cv = m.Cv, svPercent = m.SvPercent, cvPercent = m.CvPercent, spi = m.Spi, cpi = m.Cpi,
        eacCpi = m.EacCpi, eacCpiSpi = m.EacCpiSpi, eac = m.Eac, etc = m.Etc, vac = m.Vac, tcpi = m.Tcpi, percentComplete = m.PercentComplete, percentSpent = m.PercentSpent,
    };

    internal static string Money(decimal value) =>
        value < 0 ? "-$" + (-value).ToString("N0", Invariant) : "$" + value.ToString("N0", Invariant);

    /// <summary>A fraction as a whole percent ("95%"), the same on every server culture.</summary>
    internal static string Percent(decimal fraction) => (fraction * 100).ToString("0", Invariant) + "%";

    private static string Index(decimal? value) => value is { } v ? v.ToString("0.00", Invariant) : "n/a";

    private static string Amount(CeilingMetric metric, decimal value) => metric == CeilingMetric.Hours ? $"{value:N0} h" : Money(value);

    private static bool IsAppRelative(string link) =>
        !string.IsNullOrWhiteSpace(link) && !link.Contains("//", StringComparison.Ordinal) && !link.Contains(':', StringComparison.Ordinal) && !link.StartsWith('/');

    internal static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max] + "…";

    private static AssistantToolSpec Spec(string name, string description, string activity, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
            ["additionalProperties"] = false,
        };
        return new AssistantToolSpec(name, description, activity, schema);
    }

    /// <summary>Validated access to a tool's arguments: unknown properties are refused, strings are trimmed and bounded.</summary>
    private sealed class Args
    {
        private readonly JsonElement _root;

        public Args(JsonElement root, AssistantToolSpec spec)
        {
            _root = root;
            var known = spec.Schema["properties"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!known.Contains(property.Name))
                {
                    throw new ArgumentException($"Unknown argument '{Clip(property.Name, 40)}'.");
                }
            }
        }

        public string? Text(string name)
        {
            if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            text = text?.Trim();
            if (text?.Length > 200)
            {
                throw new ArgumentException($"'{name}' is too long.");
            }

            return string.IsNullOrEmpty(text) ? null : text;
        }

        public string Required(string name) => Text(name) ?? throw new ArgumentException($"'{name}' is required.");

        public int? Integer(string name, int min, int max)
        {
            if (!_root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var n = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString() ?? "", Invariant);
            return Math.Clamp(n, min, max);
        }

        public bool Bool(string name) => _root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        public DateOnly? Date(string name) => Text(name) is { } text
            ? DateOnly.TryParseExact(text, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var d) ? d : throw new ArgumentException($"'{name}' must be a date like 2026-10-31.")
            : null;
    }
}
