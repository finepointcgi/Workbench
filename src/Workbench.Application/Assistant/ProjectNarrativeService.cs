using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>Everything a narrative is drafted from, read through the permission-checked services as the user.</summary>
public sealed record NarrativeFacts(
    ProjectDto Project,
    FiscalPeriodDto Period,
    ProjectFinancialSummaryDto? Financials,
    IReadOnlyList<WbsFinancialRowDto> Wbs,
    EvmStatusDto? Evm,
    EvmStatusDto? PreviousEvm,
    IReadOnlyList<EvmVarianceDto> Variances,
    IReadOnlyList<(string Title, DateOnly? Finish)> Completed,
    IReadOnlyList<(string Title, DateOnly Date, string Source)> Upcoming,
    IReadOnlyList<CeilingStatusDto> Ceilings,
    IReadOnlyList<ClinFundingStatusDto> Clins,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Records);

/// <summary>
/// The monthly PM narrative. Facts are assembled deterministically (<see cref="NarrativeAssembler"/>); with the assistant
/// on, Claude rewrites the draft from the same facts (numbers kept), otherwise the template stands. Nothing is stored
/// until the PM saves; saving can copy control-account VAR text into earned value, through <see cref="IEvmService"/>.
/// </summary>
public sealed class ProjectNarrativeService(
    IServiceProvider services,
    IWorkbenchDbContextFactory dbFactory,
    AssistantRunner runner,
    AuditRecorder audit,
    TimeProvider clock) : IProjectNarrativeService
{
    public async Task<IReadOnlyList<NarrativePeriodDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await ProjectAsync(projectId, cancellationToken);
        var today = Today;
        var periods = (await Get<IFiscalCalendarService>().ListPeriodsAsync(cancellationToken: cancellationToken))
            .Where(p => p.Start <= today && p.End >= today.AddMonths(-12))
            .OrderByDescending(p => p.Start)
            .ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = await db.Set<ProjectNarrative>().AsNoTracking().Where(n => n.ProjectId == project.Id).Select(n => n.FiscalPeriodId).ToListAsync(cancellationToken);
        return periods.Select(p => new NarrativePeriodDto(p.Id, p.Name, p.Start, p.End, saved.Contains(p.Id), p.Start <= today && p.End >= today)).ToList();
    }

    public async Task<ProjectNarrativeDto?> GetAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var project = await ProjectAsync(projectId, cancellationToken);
        var period = await PeriodAsync(project.Id, fiscalPeriodId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = await db.Set<ProjectNarrative>().AsNoTracking().SingleOrDefaultAsync(n => n.ProjectId == project.Id && n.FiscalPeriodId == period.Id, cancellationToken);
        if (saved is null)
        {
            return null;
        }

        var variances = await VariancesAsync(project.Id, period.Id, cancellationToken);
        return new ProjectNarrativeDto(project.Id, project.Code, project.Name, period.Id, period.Name, Sections(saved),
            variances.Where(v => v.NeedsVar || v.Cause is not null).Select(v => new NarrativeVarianceDto(v.ControlAccountWbsId, v.Code, v.Name, v.NeedsVar, v.Cause, v.Impact, v.CorrectiveAction, v.IsComplete)).ToList(),
            [], saved.DraftSource, true, saved.UpdatedBy, saved.UpdatedAtUtc, variances.Count > 0 && variances.Any(v => !v.IsFrozen), []);
    }

    public async Task<ProjectNarrativeDto> DraftAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var facts = await GatherAsync(projectId, fiscalPeriodId, cancellationToken);
        var draft = NarrativeAssembler.Assemble(facts);
        var source = "template";
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.Narrative,
            $"Draft the {facts.Period.Name} narrative for project {facts.Project.Code}",
            AssistantPrompts.NarrativeSystem,
            $"<workbench-data source=\"narrative facts\" untrusted=\"true\">\n{JsonSerializer.Serialize(NarrativeAssembler.FactsJson(facts), AssistantPrompts.DataJson)}\n</workbench-data>\n"
                + $"<draft-json>\n{JsonSerializer.Serialize(NarrativeAssembler.DraftJson(draft), AssistantPrompts.DataJson)}\n</draft-json>",
            NarrativeAssembler.Schema(),
            $"projects/{facts.Project.Id}/narrative",
            "narrative_facts",
            facts.Records,
            cancellationToken);
        if (completion is not null && NarrativeAssembler.TryMerge(draft, completion.Text, out var improved))
        {
            draft = improved;
            source = "assistant";
        }

        var notes = facts.Notes.ToList();
        if (source == "template")
        {
            notes.Insert(0, "Drafted from the numbers by a template (the assistant is off or unavailable). Edit it before saving.");
        }

        return new ProjectNarrativeDto(facts.Project.Id, facts.Project.Code, facts.Project.Name, facts.Period.Id, facts.Period.Name,
            draft.Sections, draft.Variances, NarrativeAssembler.FactList(facts), source, false, null, null,
            facts.Evm is not null && !facts.Evm.IsFrozen && draft.Variances.Count > 0, notes);
    }

    public async Task<ProjectNarrativeDto> SaveAsync(Guid projectId, SaveProjectNarrativeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await ProjectAsync(projectId, cancellationToken);
        var period = await PeriodAsync(project.Id, request.FiscalPeriodId, cancellationToken);
        var sections = Clean(request.Sections ?? NarrativeSectionsDto.Empty);
        var source = request.DraftSource is "assistant" or "template" or "manual" ? request.DraftSource : "manual";

        // VARs first: if earned value refuses (closed period, no access), nothing is saved.
        if (request.ApplyToVariances is { Count: > 0 } apply)
        {
            var evm = Get<IEvmService>();
            var existing = (await evm.ListVariancesAsync(project.Id, period.Id, cancellationToken)).ToDictionary(v => v.ControlAccountWbsId);
            foreach (var v in apply)
            {
                if (!existing.TryGetValue(v.ControlAccountWbsId, out var current))
                {
                    throw new ArgumentException("A control account in the narrative isn't in this project's earned value baseline.");
                }

                await evm.SaveVarianceAsync(project.Id, period.Id, v.ControlAccountWbsId,
                    new SaveVarianceRequest(Trim(v.Cause, 4_000), Trim(v.Impact, 4_000), Trim(v.CorrectiveAction, 4_000), current.ManagerEac), cancellationToken);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var row = await db.Set<ProjectNarrative>().SingleOrDefaultAsync(n => n.ProjectId == project.Id && n.FiscalPeriodId == period.Id, cancellationToken);
            var before = row is null ? null : Sections(row);
            if (row is null)
            {
                row = new ProjectNarrative { ProjectId = project.Id, FiscalPeriodId = period.Id, PeriodName = period.Name, DraftSource = source, CreatedBy = runner.UserEmail, CreatedAtUtc = now, UpdatedBy = runner.UserEmail };
                db.Set<ProjectNarrative>().Add(row);
            }

            row.Summary = sections.Summary;
            row.CostVariance = sections.CostVariance;
            row.ScheduleVariance = sections.ScheduleVariance;
            row.EacChanges = sections.EacChanges;
            row.Accomplishments = sections.Accomplishments;
            row.UpcomingMilestones = sections.UpcomingMilestones;
            row.Risks = sections.Risks;
            row.DraftSource = source;
            row.UpdatedBy = runner.UserEmail;
            row.UpdatedAtUtc = now;
            audit.Record(db, "ProjectNarrative", row.Id, before is null ? "Created" : "Updated", before, sections, $"{project.Code} {period.Name} narrative ({source})");
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await GetAsync(project.Id, period.Id, cancellationToken))!;
    }

    // ---- Facts ----

    private async Task<NarrativeFacts> GatherAsync(Guid projectId, Guid? fiscalPeriodId, CancellationToken ct)
    {
        var project = await ProjectAsync(projectId, ct);
        var period = await PeriodAsync(project.Id, fiscalPeriodId, ct);
        var notes = new List<string>();
        var records = new List<string> { $"Project:{project.Id}", $"FiscalPeriod:{period.Id}" };

        var financials = Get<IProjectFinancialsService>();
        var summary = await financials.GetProjectSummaryAsync(project.Id, cancellationToken: ct);
        var wbs = await financials.GetWbsFinancialsAsync(project.Id, cancellationToken: ct);
        records.AddRange(wbs.Select(w => $"WbsElement:{w.WbsElementId}"));

        EvmStatusDto? status = null;
        EvmStatusDto? previous = null;
        IReadOnlyList<EvmVarianceDto> variances = [];
        var evm = Get<IEvmService>();
        if (await evm.GetBaselineAsync(project.Id, ct) is not null)
        {
            var periods = await evm.ListPeriodsAsync(project.Id, ct);
            if (periods.Any(p => p.FiscalPeriodId == period.Id))
            {
                status = await evm.GetStatusAsync(project.Id, period.Id, ct);
                variances = await evm.ListVariancesAsync(project.Id, period.Id, ct);
                var prior = periods.Where(p => p.End < period.Start && p.State == EvmPeriodState.Closed).OrderByDescending(p => p.End).FirstOrDefault();
                if (prior is not null)
                {
                    previous = await evm.GetStatusAsync(project.Id, prior.FiscalPeriodId, ct);
                }
            }
            else
            {
                notes.Add($"{period.Name} is outside the earned value baseline's periods, so the draft uses project financials only.");
            }
        }
        else
        {
            notes.Add("This project has no earned value baseline: cost and schedule variance come from budget and actuals only.");
        }

        var completed = new List<(string, DateOnly?)>();
        var upcoming = new List<(string, DateOnly, string)>();
        if (project.SheetId is { } sheetId)
        {
            try
            {
                var sheet = await Get<ISheetService>().GetAsync(sheetId, ct);
                var items = await Get<IItemService>().ListAsync(sheetId, ct);
                var statusColumn = sheet?.Columns.FirstOrDefault(c => string.Equals(c.Name, "Status", StringComparison.OrdinalIgnoreCase));
                var percentColumn = sheet?.Columns.FirstOrDefault(c => c.Name.Contains("complete", StringComparison.OrdinalIgnoreCase) && c.Type == ColumnKind.Number);
                bool IsDone(ItemDto item) =>
                    (statusColumn is not null && item.Values.GetValueOrDefault(statusColumn.Id.ToString()) is { } s && s.Trim().ToLowerInvariant() is "done" or "complete" or "completed")
                    || (percentColumn is not null && decimal.TryParse(item.Values.GetValueOrDefault(percentColumn.Id.ToString()), NumberStyles.Number, CultureInfo.InvariantCulture, out var pc) && pc >= 100);
                foreach (var item in items)
                {
                    var done = IsDone(item);
                    if (done && item.Finish is { } f && f >= period.Start && f <= period.End)
                    {
                        completed.Add((item.Title, item.Finish));
                    }
                    else if (!done && item.IsMilestone && (item.Finish ?? item.Start) is { } d && d > period.End && d <= period.End.AddDays(90))
                    {
                        upcoming.Add((item.Title, d, "plan"));
                    }
                }

                records.Add($"Sheet:{sheetId}");
            }
            catch (UnauthorizedAccessException)
            {
                notes.Add("You can't open the plan sheet, so accomplishments and milestones from the plan are left out.");
            }
            catch (WorkbenchNotFoundException)
            {
                notes.Add("You can't open the plan sheet, so accomplishments and milestones from the plan are left out.");
            }
        }
        else
        {
            notes.Add("The project has no plan sheet, so there are no completed tasks to list.");
        }

        if (status is not null && await evm.GetBaselineAsync(project.Id, ct) is { } baseline)
        {
            foreach (var m in baseline.WorkPackages.SelectMany(w => w.Milestones))
            {
                if (!m.IsComplete && m.PlannedDate is { } d && d > period.End && d <= period.End.AddDays(90) && upcoming.All(u => u.Item1 != m.Name))
                {
                    upcoming.Add((m.Name, d, "earned value"));
                }
            }
        }

        var ceilings = new List<CeilingStatusDto>();
        try
        {
            ceilings.AddRange((await Get<ICeilingService>().GetProjectStatusAsync(project.Id, ct)).Ceilings.Where(c => c.Level != CeilingAlertLevel.Ok));
            records.AddRange(ceilings.Select(c => $"Ceiling:{c.Ceiling.Id}"));
        }
        catch (UnauthorizedAccessException)
        {
            notes.Add("Ceilings couldn't be read with your access.");
        }

        var clins = new List<ClinFundingStatusDto>();
        try
        {
            if (await Get<IFundingService>().GetProjectFundingStatusAsync(project.Id, ct) is { } funding)
            {
                clins.AddRange(funding.Clins.Where(c => c.Level is FundingLevel.Warning or FundingLevel.Critical or FundingLevel.Exhausted
                    || (c.ProjectedExhaustion is { } x && x <= period.End.AddDays(120))));
                records.AddRange(clins.Select(c => $"Clin:{c.ClinId}"));
            }
        }
        catch (UnauthorizedAccessException)
        {
            notes.Add("CLIN funding couldn't be read with your access.");
        }

        return new NarrativeFacts(project, period, summary, wbs, status, previous, variances,
            completed.OrderBy(c => c.Item2).ToList(), upcoming.OrderBy(u => u.Item2).ToList(), ceilings, clins, notes, records.Distinct().ToList());
    }

    private async Task<IReadOnlyList<EvmVarianceDto>> VariancesAsync(Guid projectId, Guid periodId, CancellationToken ct)
    {
        var evm = Get<IEvmService>();
        if (await evm.GetBaselineAsync(projectId, ct) is null || !(await evm.ListPeriodsAsync(projectId, ct)).Any(p => p.FiscalPeriodId == periodId))
        {
            return [];
        }

        return await evm.ListVariancesAsync(projectId, periodId, ct);
    }

    private async Task<ProjectDto> ProjectAsync(Guid projectId, CancellationToken ct) =>
        await Get<IProjectService>().GetProjectAsync(projectId, ct) ?? throw WorkbenchNotFoundException.For("Project", projectId);

    /// <summary>The requested period, else the earned value reporting period, else the fiscal period containing today.</summary>
    private async Task<FiscalPeriodDto> PeriodAsync(Guid projectId, Guid? fiscalPeriodId, CancellationToken ct)
    {
        var calendar = Get<IFiscalCalendarService>();
        if (fiscalPeriodId is { } id)
        {
            return (await calendar.ListPeriodsAsync(cancellationToken: ct)).FirstOrDefault(p => p.Id == id)
                ?? throw WorkbenchNotFoundException.For("Fiscal period", id);
        }

        return await calendar.GetPeriodForDateAsync(Today, ct)
            ?? throw new ArgumentException("There's no fiscal period for today. Set up the fiscal calendar first.");
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private static NarrativeSectionsDto Sections(ProjectNarrative n) =>
        new(n.Summary, n.CostVariance, n.ScheduleVariance, n.EacChanges, n.Accomplishments, n.UpcomingMilestones, n.Risks);

    private static NarrativeSectionsDto Clean(NarrativeSectionsDto s) => new(
        Trim(s.Summary, 8_000), Trim(s.CostVariance, 8_000), Trim(s.ScheduleVariance, 8_000), Trim(s.EacChanges, 8_000),
        Trim(s.Accomplishments, 8_000), Trim(s.UpcomingMilestones, 8_000), Trim(s.Risks, 8_000));

    private static string? Trim(string? text, int max)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        return t.Length <= max ? t : throw new ArgumentException($"Each narrative section can be at most {max:N0} characters.");
    }
}

/// <summary>The deterministic part of the narrative: the draft text from the facts, the facts list and the JSON sent to the model.</summary>
public static class NarrativeAssembler
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public sealed record Draft(NarrativeSectionsDto Sections, IReadOnlyList<NarrativeVarianceDto> Variances);

    public static Draft Assemble(NarrativeFacts f)
    {
        var p = f.Project;
        var t = f.Financials?.Totals ?? FinancialFigures.Zero;
        var e = f.Evm?.Total;

        var summary = new StringBuilder($"{p.Code} {p.Name}, {f.Period.Name} ({f.Period.Start:MMM d} – {f.Period.End:MMM d, yyyy}): ");
        summary.Append($"actual cost to date is {M(t.ActualCost)} against a budget of {M(t.BudgetAmount)}");
        summary.Append(t.PercentSpent is { } ps ? $" ({Percent(ps)} spent)" : "");
        summary.Append($"; the estimate at completion is {M(t.EacAmount)} (variance at completion {M(t.VacAmount)}).");
        if (e is not null)
        {
            summary.Append($" Earned value: CPI {I(e.Cpi)}, SPI {I(e.Spi)}, {e.PercentComplete.ToString("0.#", Invariant)}% complete.");
        }

        string cost;
        string schedule;
        if (e is not null && f.Evm is { } status)
        {
            var costCas = status.ControlAccounts.Where(c => c.VarianceReasons.Any(r => r.Contains("CV", StringComparison.OrdinalIgnoreCase) || r.Contains("VAC", StringComparison.OrdinalIgnoreCase))).ToList();
            var scheduleCas = status.ControlAccounts.Where(c => c.VarianceReasons.Any(r => r.Contains("SV", StringComparison.OrdinalIgnoreCase))).ToList();
            cost = $"Cumulative cost variance is {M(e.Cv)}{Pct(e.CvPercent)} (BCWP {M(e.Bcwp)} vs ACWP {M(e.Acwp)}, CPI {I(e.Cpi)}); this period {M(status.PeriodTotal.Cv)}."
                + (costCas.Count > 0
                    ? " Beyond thresholds: " + string.Join("; ", costCas.Select(c => $"{c.Code} {c.Name} (CV {M(c.Cumulative.Cv)}{Pct(c.Cumulative.CvPercent)})")) + ". Cause to be confirmed by the PM."
                    : " No control account is beyond the cost variance thresholds.");
            schedule = $"Cumulative schedule variance is {M(e.Sv)}{Pct(e.SvPercent)} (BCWP {M(e.Bcwp)} vs BCWS {M(e.Bcws)}, SPI {I(e.Spi)}); this period {M(status.PeriodTotal.Sv)}."
                + (scheduleCas.Count > 0
                    ? " Beyond thresholds: " + string.Join("; ", scheduleCas.Select(c => $"{c.Code} {c.Name} (SV {M(c.Cumulative.Sv)}{Pct(c.Cumulative.SvPercent)})")) + ". Cause to be confirmed by the PM."
                    : " No control account is beyond the schedule variance thresholds.");
        }
        else
        {
            var over = f.Wbs.Where(w => w.IsLeaf && w.Figures.VacAmount < 0).OrderBy(w => w.Figures.VacAmount).Take(5).ToList();
            cost = $"Actual cost is {M(t.ActualCost)} of a {M(t.BudgetAmount)} budget; remaining budget {M(t.RemainingBudget)}."
                + (over.Count > 0
                    ? " Projected over budget: " + string.Join("; ", over.Select(w => $"{w.Code} {w.Name} (EAC {M(w.Figures.EacAmount)} vs budget {M(w.Figures.BudgetAmount)})")) + ". Cause to be confirmed by the PM."
                    : " No WBS element is projected over budget.");
            schedule = "Schedule variance isn't measured: the project has no earned value baseline for this period.";
        }

        var eac = $"EAC {M(t.EacAmount)} against a budget of {M(t.BudgetAmount)} (VAC {M(t.VacAmount)}).";
        if (e is not null)
        {
            eac += $" Earned value EAC (managers' estimates) {M(e.Eac)} against BAC {M(e.Bac)}; EAC at CPI {M(e.EacCpi)}, at CPI×SPI {M(e.EacCpiSpi)}.";
            if (f.PreviousEvm is { } prev)
            {
                var delta = e.Eac - prev.Total.Eac;
                eac += delta == 0
                    ? $" Unchanged from {prev.Period.Name}."
                    : $" {(delta > 0 ? "Up" : "Down")} {M(Math.Abs(delta))} from {M(prev.Total.Eac)} in {prev.Period.Name}.";
            }
        }

        var accomplishments = f.Completed.Count == 0
            ? "No plan tasks were marked done with a finish date in this period."
            : string.Join("\n", f.Completed.Select(c => $"- {c.Title}{(c.Finish is { } d ? $" (finished {d:MMM d})" : "")}"));
        var milestones = f.Upcoming.Count == 0
            ? "No milestones are due in the next 90 days."
            : string.Join("\n", f.Upcoming.Select(u => $"- {u.Title}: {u.Date:MMM d, yyyy}"));

        var risks = new List<string>();
        risks.AddRange(f.Ceilings.Select(c => $"- {c.Ceiling.Label}: " + string.Join(", ", c.Measures.Select(m => $"{m.Metric} {Percent(m.PercentConsumed)} used")) + $" ({c.Level}{(c.Ceiling.Mode == CeilingMode.Block ? ", blocking" : "")})."));
        risks.AddRange(f.Clins.Select(c => $"- CLIN {c.Number} funding: {M(c.Remaining)} of {M(c.FundedValue)} remaining"
            + (c.ProjectedExhaustion is { } x ? $", projected to run out about {x:MMM d, yyyy}" : "") + $" ({c.Level})."));
        if (f.Financials?.Funding is { } funding)
        {
            risks.AddRange(funding.Alerts.Where(a => a.Level != FundingAlertLevel.Info).Select(a => $"- {a.Message}"));
        }

        var variances = f.Variances.Where(v => v.NeedsVar).Select(v => new NarrativeVarianceDto(
            v.ControlAccountWbsId, v.Code, v.Name, v.NeedsVar,
            v.Cause ?? $"Cause to be confirmed by the PM. Cumulative CV {M(v.Cv)}{Pct(v.CvPercent)}, SV {M(v.Sv)}{Pct(v.SvPercent)}.",
            v.Impact ?? $"EAC {M(v.Eac)} against BAC {M(v.Bac)} (VAC {M(v.Vac)}).",
            v.CorrectiveAction ?? "Corrective action to be decided by the PM.",
            v.IsComplete)).ToList();

        return new Draft(
            new NarrativeSectionsDto(summary.ToString(), cost, schedule, eac, accomplishments, milestones,
                risks.Count == 0 ? "No ceilings or CLIN funding thresholds are at risk." : string.Join("\n", risks.Distinct())),
            variances);
    }

    public static IReadOnlyList<NarrativeFactDto> FactList(NarrativeFacts f)
    {
        var p = f.Project;
        var list = new List<NarrativeFactDto>();
        if (f.Financials is { } fin)
        {
            var href = $"projects/{p.Id}/financials";
            list.Add(new("Budget", M(fin.Totals.BudgetAmount), href));
            list.Add(new("Actual cost", M(fin.Totals.ActualCost), href));
            list.Add(new("EAC (financials)", M(fin.Totals.EacAmount), href));
            list.Add(new("VAC (financials)", M(fin.Totals.VacAmount), href));
        }

        if (f.Evm is { } evm)
        {
            var href = $"projects/{p.Id}/evm";
            list.Add(new("CPI / SPI", $"{I(evm.Total.Cpi)} / {I(evm.Total.Spi)}", href));
            list.Add(new("CV / SV (cumulative)", $"{M(evm.Total.Cv)} / {M(evm.Total.Sv)}", href));
            list.Add(new("EAC (earned value)", M(evm.Total.Eac), href));
            if (f.PreviousEvm is { } prev)
            {
                list.Add(new($"EAC in {prev.Period.Name}", M(prev.Total.Eac), href));
            }
        }

        list.Add(new("Tasks completed this period", f.Completed.Count.ToString(Invariant), p.SheetId is { } s ? $"sheets/{s}" : null));
        list.Add(new("Milestones in the next 90 days", f.Upcoming.Count.ToString(Invariant), p.SheetId is { } s2 ? $"sheets/{s2}" : null));
        list.Add(new("Ceilings at warning or above", f.Ceilings.Count.ToString(Invariant), $"projects/{p.Id}/ceilings"));
        list.Add(new("CLINs with funding risk", f.Clins.Count.ToString(Invariant), $"projects/{p.Id}/financials"));
        return list;
    }

    public static object FactsJson(NarrativeFacts f) => new
    {
        project = new { f.Project.Code, f.Project.Name, contract = f.Project.ContractNumber },
        period = new { f.Period.Name, f.Period.Start, f.Period.End },
        financials = f.Financials is { } fin ? new { budget = fin.Totals.BudgetAmount, actual = fin.Totals.ActualCost, eac = fin.Totals.EacAmount, vac = fin.Totals.VacAmount, percentSpent = fin.Totals.PercentSpent } : null,
        earnedValue = f.Evm is { } e ? new
        {
            e.Total.Bac, e.Total.Bcws, e.Total.Bcwp, e.Total.Acwp, e.Total.Sv, e.Total.Cv, e.Total.Spi, e.Total.Cpi, e.Total.Eac, e.Total.Vac,
            previousEac = f.PreviousEvm?.Total.Eac,
            controlAccounts = e.ControlAccounts.Select(c => new { c.Code, c.Name, c.Cumulative.Sv, c.Cumulative.Cv, c.Cumulative.Vac, c.NeedsVar, reasons = c.VarianceReasons }),
        } : null,
        completedTasks = f.Completed.Select(c => new { title = c.Title, finished = c.Finish }),
        upcomingMilestones = f.Upcoming.Select(u => new { title = u.Title, date = u.Date }),
        ceilings = f.Ceilings.Select(c => new { c.Ceiling.Label, level = c.Level.ToString(), measures = c.Measures.Select(m => new { metric = m.Metric.ToString(), m.PercentConsumed }) }),
        clinFunding = f.Clins.Select(c => new { c.Number, c.FundedValue, c.Remaining, c.ProjectedExhaustion, level = c.Level.ToString() }),
    };

    public static object DraftJson(Draft d) => new
    {
        d.Sections.Summary,
        d.Sections.CostVariance,
        d.Sections.ScheduleVariance,
        d.Sections.EacChanges,
        d.Sections.Accomplishments,
        d.Sections.UpcomingMilestones,
        d.Sections.Risks,
        variances = d.Variances.Select(v => new { code = v.Code, cause = v.Cause, impact = v.Impact, correctiveAction = v.CorrectiveAction }),
    };

    public static JsonObject Schema()
    {
        var properties = new JsonObject();
        foreach (var name in new[] { "summary", "costVariance", "scheduleVariance", "eacChanges", "accomplishments", "upcomingMilestones", "risks" })
        {
            properties[name] = new JsonObject { ["type"] = "string" };
        }

        properties["variances"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["code"] = new JsonObject { ["type"] = "string" },
                    ["cause"] = new JsonObject { ["type"] = "string" },
                    ["impact"] = new JsonObject { ["type"] = "string" },
                    ["correctiveAction"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("code", "cause", "impact", "correctiveAction"),
                ["additionalProperties"] = false,
            },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("summary", "costVariance", "scheduleVariance", "eacChanges", "accomplishments", "upcomingMilestones", "risks", "variances"),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>Takes the model's sections where present; variances only for the draft's own control accounts.</summary>
    public static bool TryMerge(Draft draft, string json, out Draft merged)
    {
        merged = draft;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject o)
        {
            return false;
        }

        string? Text(string name, string? fallback) =>
            o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? Clip(s.Trim(), 8_000) : fallback;

        var s = draft.Sections;
        var sections = new NarrativeSectionsDto(
            Text("summary", s.Summary), Text("costVariance", s.CostVariance), Text("scheduleVariance", s.ScheduleVariance), Text("eacChanges", s.EacChanges),
            Text("accomplishments", s.Accomplishments), Text("upcomingMilestones", s.UpcomingMilestones), Text("risks", s.Risks));
        var byCode = (o["variances"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(v => v["code"] is JsonValue)
            .GroupBy(v => v["code"]!.ToString())
            .ToDictionary(g => g.Key, g => g.First());
        var variances = draft.Variances.Select(v =>
        {
            if (!byCode.TryGetValue(v.Code, out var m))
            {
                return v;
            }

            string? Field(string name, string? fallback) => m[name] is JsonValue jv && jv.TryGetValue<string>(out var x) && !string.IsNullOrWhiteSpace(x) ? Clip(x.Trim(), 4_000) : fallback;
            return v with { Cause = Field("cause", v.Cause), Impact = Field("impact", v.Impact), CorrectiveAction = Field("correctiveAction", v.CorrectiveAction) };
        }).ToList();
        merged = new Draft(sections, variances);
        return true;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    private static string M(decimal value) => AssistantToolbox.Money(value);

    private static string Percent(decimal fraction) => AssistantToolbox.Percent(fraction);

    private static string I(decimal? value) => value is { } v ? v.ToString("0.00", Invariant) : "n/a";

    private static string Pct(decimal? percent) => percent is { } p ? $" ({p.ToString("0.#", Invariant)}%)" : "";
}
