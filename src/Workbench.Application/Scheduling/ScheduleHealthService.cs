using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tasks;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Scheduling;

/// <summary>
/// The DCMA 14-point assessment of a plan (<see cref="ScheduleHealth"/>) loaded from the sheet: its dated detail tasks, links,
/// working calendar, constraints, % Complete column, the people and staffed hours on each task, and a baseline. Access is
/// checked by <c>PermissionCheckingScheduleHealthService</c> (Viewer).
/// </summary>
public sealed class ScheduleHealthService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IScheduleHealthService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<ScheduleHealthDto> GetAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (plan, dto) = await LoadAsync(db, sheetId, request ?? new ScheduleHealthRequest(), cancellationToken);
        return dto with { Checks = ScheduleHealth.Evaluate(plan) };
    }

    public async Task<IReadOnlyList<TaskFloatDto>> GetFloatAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (plan, _) = await LoadAsync(db, sheetId, new ScheduleHealthRequest(), cancellationToken);
        return ScheduleHealth.Analyze(plan, plan.Tasks, plan.Links).Items
            .Select(i => new TaskFloatDto(i.ItemId, i.SlackDays, i.FreeFloatDays ?? 0, i.IsCritical))
            .ToList();
    }

    public async Task<PlanFile> ExportPdfAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        var health = await GetAsync(sheetId, request, cancellationToken);
        return new PlanFile(FileName(health, "pdf"), ScheduleHealthPdf.ContentType, ScheduleHealthPdf.Write(health));
    }

    public async Task<PlanFile> ExportExcelAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default)
    {
        var health = await GetAsync(sheetId, request, cancellationToken);
        return new PlanFile(FileName(health, "xlsx"), PlanWorkbook.ContentType, Workbook(health));
    }

    private async Task<(HealthPlan Plan, ScheduleHealthDto Dto)> LoadAsync(
        IWorkbenchDbContext db, Guid sheetId, ScheduleHealthRequest request, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var state = await PlanReflow.LoadAsync(db, sheet, cancellationToken, tracked: false);
        var outline = new PlanOutline(state.Items);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var percentKey = state.PercentColumn?.Id.ToString();
        var resourced = await ResourcedAsync(db, sheetId, state.Items, columns, cancellationToken);

        // Detail tasks with dates: summaries only group them, and undated rows aren't scheduled yet.
        var leaves = outline.Ordered.Where(i => !outline.IsSummary(i.Id)).ToList();
        var tasks = leaves
            .Where(i => (i.Start ?? i.Finish) is not null)
            .Select(i => new HealthTask(
                i.Id,
                outline.TaskNumber[i.Id],
                outline.OutlineNumber[i.Id],
                i.Title,
                (i.Start ?? i.Finish)!.Value,
                (i.Finish ?? i.Start)!.Value,
                i.IsMilestone,
                percentKey is null ? null : ScheduleEngine.Percent(i.Values.GetValueOrDefault(percentKey)),
                resourced.Contains(i.Id),
                state.Constraints.GetValueOrDefault(i.Id)))
            .ToList();
        var ids = tasks.Select(t => t.Id).ToHashSet();
        var links = state.Links.Where(l => ids.Contains(l.PredecessorId) && ids.Contains(l.SuccessorId))
            .Select(l => new ScheduleLink(l.PredecessorId, l.SuccessorId, l.Type, l.LagDays))
            .ToList();

        var baseline = request.BaselineId is { } baselineId
            ? await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries).FirstOrDefaultAsync(b => b.Id == baselineId && b.SheetId == sheetId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Baseline", baselineId)
            : await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries).Where(b => b.SheetId == sheetId)
                .OrderByDescending(b => b.CapturedAtUtc).FirstOrDefaultAsync(cancellationToken);

        var statusDate = request.StatusDate ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var plan = new HealthPlan(tasks, links, state.Calendar, statusDate,
            baseline?.Entries.Where(e => ids.Contains(e.ItemId)).ToDictionary(e => e.ItemId, e => (e.Start, e.Finish)));

        var notes = new List<string>();
        if (state.PercentColumn is null)
        {
            notes.Add("The plan has no % Complete column, so every task counts as not started. Add one to record progress.");
        }

        if (leaves.Count > tasks.Count)
        {
            var undated = leaves.Count - tasks.Count;
            notes.Add($"{undated.ToString(Inv)} task{(undated == 1 ? " has" : "s have")} no dates and isn't assessed.");
        }

        if (baseline is null)
        {
            notes.Add("There's no baseline yet, so missed tasks and BEI can't be measured. Save one on the Gantt tab.");
        }

        var project = await PlanReflow.ProjectForAsync(db, sheetId, cancellationToken);
        var name = project is null ? sheet.Name : $"{project.Code} {project.Name}";
        var dto = new ScheduleHealthDto(sheetId, name, statusDate, baseline?.Id, baseline?.Name, tasks.Count, tasks.Count(t => !t.IsComplete), links.Count, [], notes);
        return (plan, dto);
    }

    /// <summary>
    /// Tasks with people or hours: assignees, a Person column value, or staffing on the charge code made from the task
    /// (a total-hours assignment or weekly hours).
    /// </summary>
    private static async Task<HashSet<Guid>> ResourcedAsync(
        IWorkbenchDbContext db, Guid sheetId, IReadOnlyList<Item> items, IReadOnlyList<Column> columns, CancellationToken cancellationToken)
    {
        var people = columns.Where(c => c.Type == ColumnType.Person).Select(c => c.Id.ToString()).ToList();
        var result = items.Where(i => people.Any(p => TaskRules.People(i.Values.GetValueOrDefault(p)).Any())).Select(i => i.Id).ToHashSet();
        result.UnionWith(await db.Set<ItemAssignee>().AsNoTracking().Where(a => a.SheetId == sheetId).Select(a => a.ItemId).ToListAsync(cancellationToken));

        var ids = items.Select(i => i.Id).ToList();
        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ItemId != null && ids.Contains(w.ItemId!.Value))
            .Select(w => new { w.Id, ItemId = w.ItemId!.Value }).ToListAsync(cancellationToken);
        if (elements.Count > 0)
        {
            var elementIds = elements.Select(e => e.Id).ToList();
            var staffed = (await db.Set<StaffingAssignment>().AsNoTracking()
                    .Where(a => a.WbsElementId != null && elementIds.Contains(a.WbsElementId!.Value) && a.TotalHours > 0)
                    .Select(a => a.WbsElementId!.Value).ToListAsync(cancellationToken))
                .Concat(await db.Set<ResourceAllocation>().AsNoTracking()
                    .Where(a => a.WbsElementId != null && elementIds.Contains(a.WbsElementId!.Value) && a.Hours > 0)
                    .Select(a => a.WbsElementId!.Value).ToListAsync(cancellationToken))
                .ToHashSet();
            result.UnionWith(elements.Where(e => staffed.Contains(e.Id)).Select(e => e.ItemId));
        }

        result.UnionWith(await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.SourceItemId != null && ids.Contains(a.SourceItemId!.Value) && a.Hours > 0)
            .Select(a => a.SourceItemId!.Value).ToListAsync(cancellationToken));
        return result;
    }

    private static string FileName(ScheduleHealthDto health, string extension)
    {
        var safe = string.Concat(health.PlanName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '-' : c)).Trim();
        return $"{(safe.Length == 0 ? "Plan" : safe)} schedule health {health.StatusDate.ToString("yyyy-MM-dd", Inv)}.{extension}";
    }

    /// <summary>A summary sheet (one row per check) and a sheet of every flagged task.</summary>
    internal static byte[] Workbook(ScheduleHealthDto health)
    {
        using var book = new XLWorkbook();
        var summary = book.Worksheets.Add("Schedule health");
        summary.Cell(1, 1).Value = $"Schedule health (DCMA 14-point): {health.PlanName}";
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(2, 1).Value = $"Status date {health.StatusDate.ToString("MMM d, yyyy", Inv)} · Baseline: {health.BaselineName ?? "none"} · "
            + $"{health.DetailTasks.ToString(Inv)} tasks ({health.IncompleteTasks.ToString(Inv)} unfinished), {health.Links.ToString(Inv)} links · "
            + $"{health.Passed.ToString(Inv)} passed, {health.Failed.ToString(Inv)} failed";
        var row = 3;
        foreach (var note in health.Notes)
        {
            summary.Cell(row++, 1).Value = note;
        }

        row++;
        string[] headers = ["#", "Check", "Result", "Threshold", "Flagged", "Of", "Percent", "Index", "Detail", "What it means"];
        for (var c = 0; c < headers.Length; c++)
        {
            summary.Cell(row, c + 1).Value = headers[c];
        }

        summary.Row(row).Style.Font.Bold = true;
        summary.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF8");
        var header = row;
        foreach (var check in health.Checks)
        {
            row++;
            summary.Cell(row, 1).Value = check.Number;
            summary.Cell(row, 2).Value = check.Name;
            summary.Cell(row, 3).Value = StatusText(check.Status);
            summary.Cell(row, 3).Style.Font.FontColor = check.Status switch
            {
                HealthCheckStatus.Pass => XLColor.FromHtml("#1B7F3B"),
                HealthCheckStatus.Fail => XLColor.FromHtml("#C62828"),
                _ => XLColor.FromHtml("#6B7280"),
            };
            summary.Cell(row, 4).Value = check.Threshold;
            if (check.Status != HealthCheckStatus.NotApplicable)
            {
                summary.Cell(row, 5).Value = check.Count;
                summary.Cell(row, 6).Value = check.Total;
            }

            if (check.Percent is { } percent)
            {
                summary.Cell(row, 7).Value = percent / 100m;
                summary.Cell(row, 7).Style.NumberFormat.Format = "0.0%";
            }

            if (check.Value is { } value)
            {
                summary.Cell(row, 8).Value = value;
                summary.Cell(row, 8).Style.NumberFormat.Format = "0.00";
            }

            summary.Cell(row, 9).Value = check.Result;
            summary.Cell(row, 10).Value = check.Explanation;
        }

        summary.Range(header, 1, row, headers.Length).SetAutoFilter();
        summary.Columns(1, 8).AdjustToContents();
        summary.Column(9).Width = 60;
        summary.Column(10).Width = 80;
        summary.SheetView.FreezeRows(header);

        var detail = book.Worksheets.Add("Flagged tasks");
        string[] detailHeaders = ["#", "Check", "Task ID", "Outline", "Task", "Why"];
        for (var c = 0; c < detailHeaders.Length; c++)
        {
            detail.Cell(1, c + 1).Value = detailHeaders[c];
        }

        detail.Row(1).Style.Font.Bold = true;
        detail.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF8");
        var r = 1;
        foreach (var check in health.Checks)
        {
            foreach (var task in check.Tasks)
            {
                r++;
                detail.Cell(r, 1).Value = check.Number;
                detail.Cell(r, 2).Value = check.Name;
                detail.Cell(r, 3).Value = task.TaskNumber;
                detail.Cell(r, 4).Value = task.OutlineNumber;
                detail.Cell(r, 5).Value = task.Title;
                detail.Cell(r, 6).Value = task.Detail;
            }
        }

        detail.Range(1, 1, Math.Max(r, 1), detailHeaders.Length).SetAutoFilter();
        detail.Columns().AdjustToContents();
        detail.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    internal static string StatusText(HealthCheckStatus status) => status switch
    {
        HealthCheckStatus.Pass => "Pass",
        HealthCheckStatus.Fail => "Fail",
        _ => "Not measured",
    };
}
