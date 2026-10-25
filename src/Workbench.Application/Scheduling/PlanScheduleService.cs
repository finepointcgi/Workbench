using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Collaboration;
using Workbench.Application.Tasks;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.Leave;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Scheduling;

/// <summary>
/// The MS Project-style plan editor's server side: outline, durations and predecessors, indent/outdent with date rules, and
/// Excel export and import. The rules themselves are in <see cref="ScheduleEngine"/>; permission checks are in
/// <c>PermissionCheckingPlanScheduleService</c>. Removed tasks go through <see cref="IItemService"/> (comments, attachments,
/// activity) and imported assignments through <see cref="ITaskAssignmentService"/> (notifications) when it's registered.
/// </summary>
public sealed class PlanScheduleService(
    IWorkbenchDbContextFactory dbFactory,
    IItemService items,
    TimeProvider clock,
    ActivityRecorder? activity = null,
    ITaskAssignmentService? tasks = null,
    Automations.SheetAutomations? automations = null) : IPlanScheduleService
{
    public const string PlanImportedKind = "plan.imported";

    private const string NotesColumnName = "Notes";

    public async Task<PlanScheduleDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken);
        var outline = new PlanOutline(state.Items);
        var predecessors = ScheduleEngine.PredecessorText(outline, state.Links);
        var percentKey = state.PercentColumn?.Id.ToString();
        var list = outline.Ordered.Select(item =>
        {
            var summary = outline.IsSummary(item.Id);
            int? duration = item.Start is { } s && item.Finish is { } f ? state.Calendar.Duration(s, f, item.IsMilestone && !summary) : null;
            var percent = percentKey is null ? null : ScheduleEngine.Percent(item.Values.GetValueOrDefault(percentKey));
            return new PlanTaskDto(item.Id, outline.TaskNumber[item.Id], outline.OutlineNumber[item.Id], outline.Level[item.Id], summary,
                duration, predecessors.GetValueOrDefault(item.Id) ?? "", percent, state.Constraints.GetValueOrDefault(item.Id));
        }).ToList();
        var conflicts = state.Sheet.SchedulingEnabled ? ScheduleEngine.Conflicts(state.Items, state.Links, state.Calendar, state.Constraints) : [];
        return new PlanScheduleDto(sheetId, list, state.Calendar.Holidays.Order().ToList(), conflicts,
            Enum.GetValues<DayOfWeek>().Where(d => (state.Calendar.WorkDays & (1 << (int)d)) != 0).ToList(),
            state.Calendar.ExtraWorkingDays.Order().ToList());
    }

    public async Task<IReadOnlyList<PlanConflictDto>> SetConstraintAsync(Guid sheetId, Guid itemId, SetPlanConstraintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentException($"Unknown constraint '{request.Kind}'.");
        }

        if (request.Kind == PlanConstraintKind.AsLateAsPossible)
        {
            throw new ArgumentException("As Late As Possible isn't supported. Use Finish No Later Than for a deadline.");
        }

        var clear = request.Kind == PlanConstraintKind.AsSoonAsPossible;
        if (!clear && request.Date is null)
        {
            throw new ArgumentException($"{PlanConstraints.Label(request.Kind)} needs a date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);
        var item = Find(state, itemId);
        if (!clear && state.Items.Any(i => i.ParentId == itemId))
        {
            throw new ArgumentException($"\"{item.Title}\" is a summary task: its dates come from its subtasks, so constrain a subtask instead.");
        }

        var row = await db.Set<TaskConstraint>().FirstOrDefaultAsync(c => c.SheetId == sheetId && c.ItemId == itemId, cancellationToken);
        string summary;
        if (clear)
        {
            if (row is not null)
            {
                db.Set<TaskConstraint>().Remove(row);
            }

            state.Constraints.Remove(itemId);
            summary = $"Removed the date constraint on \"{item.Title}\"";
        }
        else
        {
            var constraint = new PlanConstraintDto(request.Kind, request.Date!.Value);
            if (row is null)
            {
                db.Set<TaskConstraint>().Add(new TaskConstraint { SheetId = sheetId, ItemId = itemId, Type = (TaskConstraintType)(int)request.Kind, Date = constraint.Date });
            }
            else
            {
                row.Type = (TaskConstraintType)(int)request.Kind;
                row.Date = constraint.Date;
            }

            state.Constraints[itemId] = constraint;
            summary = $"Set \"{item.Title}\" to {constraint.Label} {constraint.Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}";
        }

        await SaveAsync(db, state, cancellationToken);
        await RecordAsync(sheetId, itemId, ActivityKinds.ItemUpdated, summary, SheetChangeKind.ItemUpdated);
        return state.Sheet.SchedulingEnabled ? ScheduleEngine.Conflicts(state.Items, state.Links, state.Calendar, state.Constraints) : [];
    }

    public async Task<PlanCalendarDto> GetCalendarAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        return await CalendarDtoAsync(db, sheetId, cancellationToken);
    }

    public async Task<PlanCalendarDto> SetCalendarAsync(Guid sheetId, SetPlanCalendarRequest? request, CancellationToken cancellationToken = default)
    {
        var mask = 0;
        List<PlanCalendarDayDto> exceptions = [];
        if (request is not null)
        {
            foreach (var day in request.WorkWeek ?? [])
            {
                if (!Enum.IsDefined(day))
                {
                    throw new ArgumentException($"Unknown day '{day}'.");
                }

                mask |= 1 << (int)day;
            }

            if (mask == 0)
            {
                throw new ArgumentException("Choose at least one working day of the week.");
            }

            exceptions = (request.Exceptions ?? []).ToList();
            var duplicate = exceptions.GroupBy(e => e.Date).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException($"{duplicate.Key:MM/dd/yyyy} is listed twice.");
            }

            if (exceptions.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.Name) || e.Name.Trim().Length > 200) is { } bad)
            {
                throw new ArgumentException($"Give {bad.Date:MM/dd/yyyy} a name (up to 200 characters), like \"Site shutdown\".");
            }

            if (exceptions.Count > 1000)
            {
                throw new ArgumentException("A calendar has at most 1,000 exception days.");
            }
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);

        // Durations stay what they are in working days on the old calendar; finish dates move to fit the new one.
        var outline = new PlanOutline(state.Items);
        var durations = state.Items
            .Where(i => !outline.IsSummary(i.Id) && i.Start is not null && i.Finish is not null)
            .ToDictionary(i => i.Id, i => state.Calendar.Duration(i.Start!.Value, i.Finish!.Value, i.IsMilestone));

        var existing = await db.Set<PlanCalendar>().Include(c => c.Exceptions).FirstOrDefaultAsync(c => c.SheetId == sheetId, cancellationToken);
        if (existing is not null)
        {
            db.Set<PlanCalendarException>().RemoveRange(existing.Exceptions);
            db.Set<PlanCalendar>().Remove(existing);
        }

        if (request is not null)
        {
            var calendar = new PlanCalendar { SheetId = sheetId, WorkDays = mask, UseCompanyHolidays = request.UseCompanyHolidays };
            calendar.Exceptions.AddRange(exceptions.Select(e => new PlanCalendarException
            {
                CalendarId = calendar.Id, Date = e.Date, Name = e.Name.Trim(), IsWorking = e.IsWorking,
            }));
            db.Set<PlanCalendar>().Add(calendar);
        }

        await db.SaveChangesAsync(cancellationToken);
        var updated = await PlanReflow.CalendarAsync(db, sheetId, cancellationToken);
        if (state.Sheet.SchedulingEnabled)
        {
            foreach (var item in state.Items.Where(i => durations.ContainsKey(i.Id)))
            {
                var start = updated.OnOrAfter(item.Start!.Value);
                var finish = item.IsMilestone ? start : updated.Finish(start, durations[item.Id]);
                if (item.Start != start || item.Finish != finish)
                {
                    item.Start = start;
                    item.Finish = finish;
                }
            }
        }

        await SaveAsync(db, state with { Calendar = updated }, cancellationToken);
        await RecordAsync(sheetId, null, ActivityKinds.ItemUpdated,
            request is null ? "The plan now uses the company calendar" : "Changed the plan's working calendar", SheetChangeKind.ItemUpdated);
        return await CalendarDtoAsync(db, sheetId, cancellationToken);
    }

    private static async Task<PlanCalendarDto> CalendarDtoAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var company = (await db.Set<Holiday>().AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(h => h.Date).Select(h => new PlanCalendarDayDto(h.Date, h.Name)).ToList();
        var own = await db.Set<PlanCalendar>().AsNoTracking().Include(c => c.Exceptions).FirstOrDefaultAsync(c => c.SheetId == sheetId, cancellationToken);
        return own is null
            ? new PlanCalendarDto(false, PlanCalendarDto.MondayToFriday, true, [], company)
            : new PlanCalendarDto(true,
                Enum.GetValues<DayOfWeek>().Where(d => (own.WorkDays & (1 << (int)d)) != 0).ToList(),
                own.UseCompanyHolidays,
                own.Exceptions.OrderBy(e => e.Date).Select(e => new PlanCalendarDayDto(e.Date, e.Name, e.IsWorking)).ToList(),
                company);
    }

    private static async Task EnsureSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }
    }

    public async Task IndentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);
        var item = Find(state, itemId);
        if (!ScheduleEngine.Indent(state.Items, item, state.Sheet.SchedulingEnabled, state.Calendar))
        {
            throw new ArgumentException($"\"{item.Title}\" has no task above it at the same level to indent under.");
        }

        var parent = state.Items.First(i => i.Id == item.ParentId);
        await SaveAsync(db, state, cancellationToken);
        await RecordAsync(sheetId, itemId, ActivityKinds.ItemMoved, $"Indented \"{item.Title}\" under \"{parent.Title}\"", SheetChangeKind.ItemMoved);
    }

    public async Task OutdentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);
        var item = Find(state, itemId);
        if (!ScheduleEngine.Outdent(state.Items, item))
        {
            throw new ArgumentException($"\"{item.Title}\" is already at the top level.");
        }

        await SaveAsync(db, state, cancellationToken);
        await RecordAsync(sheetId, itemId, ActivityKinds.ItemMoved, $"Outdented \"{item.Title}\"", SheetChangeKind.ItemMoved);
    }

    public async Task SetDurationAsync(Guid sheetId, Guid itemId, int days, CancellationToken cancellationToken = default)
    {
        if (days is < 0 or > 3650)
        {
            throw new ArgumentException("Duration is between 0 (a milestone) and 3650 working days.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);
        var item = Find(state, itemId);
        if (state.Items.Any(i => i.ParentId == itemId))
        {
            throw new ArgumentException($"\"{item.Title}\" is a summary task: its duration comes from its subtasks.");
        }

        var start = item.Start ?? item.Finish ?? state.Calendar.OnOrAfter(DateOnly.FromDateTime(clock.GetLocalNow().DateTime));
        item.IsMilestone = days == 0;
        item.Start = start;
        item.Finish = state.Calendar.Finish(start, days);
        await SaveAsync(db, state, cancellationToken);
        await RecordAsync(sheetId, itemId, ActivityKinds.ItemUpdated,
            days == 0 ? $"Made \"{item.Title}\" a milestone" : $"Set \"{item.Title}\" to {days} day{(days == 1 ? "" : "s")}", SheetChangeKind.ItemUpdated);
    }

    public async Task SetPredecessorsAsync(Guid sheetId, Guid itemId, string? predecessors, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken, tracked: true);
        var item = Find(state, itemId);
        var outline = new PlanOutline(state.Items);
        var references = ScheduleEngine.ParsePredecessors(predecessors, out var error);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        var wanted = new List<PlanLink>();
        foreach (var reference in references)
        {
            if (reference.TaskNumber > outline.Ordered.Count)
            {
                throw new ArgumentException($"There's no task {reference.TaskNumber}.");
            }

            var predecessor = outline.Ordered[reference.TaskNumber - 1];
            if (predecessor.Id == itemId)
            {
                throw new ArgumentException("A task can't be its own predecessor.");
            }

            if (outline.IsAncestor(predecessor.Id, itemId) || outline.IsAncestor(itemId, predecessor.Id))
            {
                throw new ArgumentException($"Task {reference.TaskNumber} is this task's summary or subtask; they can't be linked.");
            }

            wanted.Add(new PlanLink(predecessor.Id, itemId, reference.Type, reference.LagDays));
        }

        var others = state.Links.Where(l => l.SuccessorId != itemId).Select(l => (l.PredecessorId, l.SuccessorId)).ToList();
        foreach (var link in wanted)
        {
            if (DependencyService.CreatesCycle(others, link.PredecessorId, link.SuccessorId))
            {
                throw new ArgumentException($"Task {outline.TaskNumber[link.PredecessorId]} already waits on this task; linking them would make a loop.");
            }

            others.Add((link.PredecessorId, link.SuccessorId));
        }

        var rows = await db.Set<Dependency>().Where(d => d.SheetId == sheetId && d.SuccessorId == itemId).ToListAsync(cancellationToken);
        ReplaceLinks(db, sheetId, rows, wanted);
        state.Links.RemoveAll(l => l.SuccessorId == itemId);
        state.Links.AddRange(wanted);
        await SaveAsync(db, state, cancellationToken);
        var text = string.Join(", ", wanted.Select(l => ScheduleEngine.FormatPredecessor(outline.TaskNumber[l.PredecessorId], l.Type, l.LagDays)));
        await RecordAsync(sheetId, itemId, ActivityKinds.ItemUpdated,
            $"Updated \"{item.Title}\": Predecessors: {(text.Length == 0 ? "(none)" : text)}", SheetChangeKind.ItemUpdated);
    }

    public async Task<PlanFile> ExportAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken);
        var (rows, _) = await RowsAsync(db, state, cancellationToken);
        return new PlanFile($"{FileSafe(state.Sheet.Name)} plan.xlsx", PlanWorkbook.ContentType, PlanWorkbook.Write(state.Sheet.Name, rows));
    }

    public async Task<PlanFile> ExportMsProjectAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken);
        var (rows, emails) = await RowsAsync(db, state, cancellationToken);
        var baselines = new List<MsProjectBaseline>();
        foreach (var baseline in await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries).Where(b => b.SheetId == sheetId)
                     .OrderByDescending(b => b.CapturedAtUtc).Take(11).ToListAsync(cancellationToken))
        {
            baselines.Add(new MsProjectBaseline(baseline.Name, baseline.Entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => (g.First().Start, g.First().Finish))));
        }

        var content = MsProjectXml.Write(state.Sheet.Name, rows, state.Calendar, emails, baselines, clock.GetUtcNow().UtcDateTime);
        return new PlanFile($"{FileSafe(state.Sheet.Name)} plan.xml", MsProjectXml.ContentType, content);
    }

    public async Task<PlanFile> ExportGanttPdfAsync(Guid sheetId, GanttPdfRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is { } from && request.To is { } to && to < from)
        {
            throw new ArgumentException("The end of the date range is before its start.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await LoadAsync(db, sheetId, cancellationToken);
        var (rows, _) = await RowsAsync(db, state, cancellationToken);

        // The critical path as the Gantt shows it: dated tasks without subtasks.
        var outline = new PlanOutline(state.Items);
        var tasks = state.Items.Where(i => !outline.IsSummary(i.Id) && (i.Start ?? i.Finish) is not null)
            .Select(i => new ScheduleTask(i.Id, (i.Start ?? i.Finish)!.Value, (i.Finish ?? i.Start)!.Value, i.IsMilestone)).ToList();
        var analysis = CriticalPathCalculator.Analyze(tasks,
            state.Links.Select(l => new ScheduleLink(l.PredecessorId, l.SuccessorId, l.Type, l.LagDays)).ToList(), state.Calendar, state.Constraints);

        Dictionary<Guid, (DateOnly? Start, DateOnly? Finish)>? baseline = null;
        string? baselineName = null;
        if (request.BaselineId is { } baselineId)
        {
            var saved = await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries)
                .FirstOrDefaultAsync(b => b.Id == baselineId && b.SheetId == sheetId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Baseline", baselineId);
            baselineName = saved.Name;
            var entries = saved.Entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.First());
            baseline = BaselineService.Span(outline, id => entries.TryGetValue(id, out var e) ? (e.Start, e.Finish) : (null, null));
        }

        var conflicts = state.Sheet.SchedulingEnabled ? ScheduleEngine.Conflicts(state.Items, state.Links, state.Calendar, state.Constraints) : [];
        var chart = new GanttPdfChart(
            state.Sheet.Name, rows, analysis.CriticalPath.ToHashSet(), conflicts.Select(c => c.ItemId).ToHashSet(), baseline, baselineName,
            state.Calendar, DateOnly.FromDateTime(clock.GetLocalNow().DateTime));
        return new PlanFile($"{FileSafe(state.Sheet.Name)} Gantt.pdf", GanttPdf.ContentType, GanttPdf.Write(chart, request));
    }

    /// <summary>The plan as export rows in outline order, and the email of each person named (for MS Project's resources).</summary>
    private static async Task<(List<PlanSheetRow> Rows, Dictionary<string, string?> Emails)> RowsAsync(IWorkbenchDbContext db, State state, CancellationToken cancellationToken)
    {
        var outline = new PlanOutline(state.Items);
        var predecessors = ScheduleEngine.PredecessorText(outline, state.Links);
        var assignees = await AssigneesAsync(db, state.Sheet.Id, cancellationToken);
        var emails = assignees.Values.SelectMany(a => a).Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email))
            .ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        var percentKey = state.PercentColumn?.Id.ToString();
        var notesKey = state.NotesColumn?.Id.ToString();

        var rows = outline.Ordered.Select(item =>
        {
            var summary = outline.IsSummary(item.Id);
            int? duration = item.Start is { } s && item.Finish is { } f ? state.Calendar.Duration(s, f, item.IsMilestone && !summary) : null;
            var people = assignees.GetValueOrDefault(item.Id) ?? [];
            return new PlanSheetRow(
                outline.TaskNumber[item.Id], outline.OutlineNumber[item.Id], outline.Level[item.Id], item.CostpointWbsId, item.Title, duration,
                item.Start, item.Finish, predecessors.GetValueOrDefault(item.Id),
                percentKey is null ? null : ScheduleEngine.Percent(item.Values.GetValueOrDefault(percentKey)),
                people.Count == 0 ? null : string.Join("; ", people.Select(e => names.GetValueOrDefault(e) ?? e)),
                notesKey is null ? null : item.Values.GetValueOrDefault(notesKey),
                item.Id, summary, state.Constraints.GetValueOrDefault(item.Id));
        }).ToList();

        var byName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in emails)
        {
            byName.TryAdd(names.GetValueOrDefault(email) ?? email, email);
        }

        return (rows, byName);
    }

    public Task<PlanFile> TemplateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlanFile("Plan template.xlsx", PlanWorkbook.ContentType, PlanWorkbook.Write("(new plan)", [])));

    public async Task<PlanImportPreview> PreviewImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await BuildImportAsync(db, sheetId, request, cancellationToken)).Import.Preview;
    }

    public async Task<PlanImportPreview> ApplyImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        PlanImport import;
        PlanSnapshot snapshot;
        Automations.SheetRowsSnapshot? rowsBefore;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            (import, snapshot) = await BuildImportAsync(db, sheetId, request, cancellationToken);
            var errors = import.Preview.Issues.Count(i => i.IsError);
            if (errors > 0)
            {
                throw new ArgumentException($"The file has {errors} error{(errors == 1 ? "" : "s")}. Fix {(errors == 1 ? "it" : "them")} and import again; nothing was changed.");
            }

            rowsBefore = automations is null ? null : await automations.CaptureAsync(sheetId, cancellationToken);
            await ApplyAsync(db, sheetId, import, snapshot, MsProjectXml.IsXml(request.FileName), cancellationToken);
        }

        // Removed tasks go through the item service, which also removes their comments and attachments. Only the topmost:
        // deleting a task deletes its subtasks, and every task in the file has already moved to its new place.
        var removed = import.Removed.ToHashSet();
        var parentOf = snapshot.Items.ToDictionary(i => i.Id, i => i.ParentId);
        foreach (var id in import.Removed.Where(id => parentOf[id] is not { } p || !removed.Contains(p)))
        {
            await items.DeleteAsync(sheetId, id, cancellationToken);
        }

        if (tasks is not null)
        {
            foreach (var task in import.Tasks.Where(t => !SamePeople(snapshot.Assignees.GetValueOrDefault(t.ItemId), t.Assignees)))
            {
                await tasks.SetAssigneesAsync(sheetId, task.ItemId, new SetAssigneesRequest(task.Assignees), cancellationToken);
            }
        }

        // Sheet automations run on the rows the file added or changed, as one batch (one notification per automation and person).
        if (rowsBefore is not null)
        {
            await automations!.RunBatchAsync(rowsBefore, $"the Excel import ({request.FileName})", cancellationToken);
        }

        var preview = import.Preview;
        await RecordAsync(sheetId, null, PlanImportedKind,
            $"Imported the plan from {request.FileName}: {preview.Count(PlanImportChangeKind.Added)} added, {preview.Count(PlanImportChangeKind.Changed)} changed, {preview.Count(PlanImportChangeKind.Removed)} removed",
            SheetChangeKind.ItemUpdated);
        return preview;
    }

    private async Task<(PlanImport Import, PlanSnapshot Snapshot)> BuildImportAsync(
        IWorkbenchDbContext db, Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null || request.Content.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        var state = await LoadAsync(db, sheetId, cancellationToken);
        var fileName = request.FileName ?? "plan.xlsx";
        var xml = MsProjectXml.IsXml(fileName);
        var hasIdColumn = true;
        var rows = xml ? MsProjectXml.Read(fileName, request.Content) : PlanWorkbook.Read(fileName, request.Content, out hasIdColumn);
        if (xml)
        {
            // MS Project gives every task a GUID; only those Workbench wrote (an exported plan coming back) match tasks here.
            var known = state.Items.Select(i => i.Id).ToHashSet();
            rows = rows.Select(r => r.Task.UniqueId is { } uid && !known.Contains(uid) ? r with { Task = r.Task with { UniqueId = null } } : r).ToList();
        }

        var users = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive)
            .Select(u => new PlanUser(u.Email, u.DisplayName)).ToListAsync(cancellationToken);
        var snapshot = new PlanSnapshot(state.Items, state.Links, state.Calendar, state.PercentColumn, state.NotesColumn,
            await AssigneesAsync(db, sheetId, cancellationToken), users, state.Sheet.SchedulingEnabled, state.Constraints);
        return (PlanImport.Build(fileName, rows, hasIdColumn, snapshot, hasConstraints: xml, unknownResourcesAreWarnings: xml), snapshot);
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, Guid sheetId, PlanImport import, PlanSnapshot snapshot, bool setsConstraints, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, cancellationToken);
        var existing = await db.Items.Where(i => i.SheetId == sheetId).ToDictionaryAsync(i => i.Id, cancellationToken);
        var columns = await db.Columns.Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var nextOrder = columns.Count == 0 ? 0 : columns.Max(c => c.Order) + 1;

        var percent = snapshot.PercentColumn is { } p ? columns.First(c => c.Id == p.Id) : null;
        if (percent is null && import.Tasks.Any(t => t.PercentComplete is not null))
        {
            percent = new Column { SheetId = sheetId, Name = TaskRules.PercentColumnNames[0], Type = ColumnType.Number, Order = nextOrder++ };
            db.Columns.Add(percent);
        }

        var notes = snapshot.NotesColumn is { } n ? columns.First(c => c.Id == n.Id) : null;
        if (notes is null && import.Tasks.Any(t => !string.IsNullOrWhiteSpace(t.Notes)))
        {
            notes = new Column { SheetId = sheetId, Name = NotesColumnName, Type = ColumnType.Text, Order = nextOrder };
            db.Columns.Add(notes);
        }

        foreach (var task in import.Tasks)
        {
            if (!existing.TryGetValue(task.ItemId, out var item))
            {
                item = new Item { Id = task.ItemId, SheetId = sheetId, Title = task.Title };
                db.Items.Add(item);
            }

            item.ParentId = task.ParentId;
            item.Order = task.Order;
            item.Title = task.Title;
            var code = string.IsNullOrWhiteSpace(task.WbsCode) ? null : task.WbsCode;
            if (code != item.CostpointWbsId)
            {
                // A code typed in the file is kept as typed; a cleared one is numbered automatically.
                item.WbsCodeIsAuto = code is null;
                item.CostpointWbsId = code;
            }
            var values = new Dictionary<string, string?>(item.Values);
            if (task.IsSummary)
            {
                item.IsMilestone = false;
            }
            else
            {
                item.Start = task.Start;
                item.Finish = task.Finish;
                item.IsMilestone = task.IsMilestone;
                if (percent is not null)
                {
                    SetValue(values, percent.Id, task.PercentComplete?.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }

            if (notes is not null)
            {
                SetValue(values, notes.Id, string.IsNullOrWhiteSpace(task.Notes) ? null : task.Notes.Trim());
            }

            if (!SameValues(item.Values, values))
            {
                item.Values = values;
            }
        }

        // Links: the file's set replaces the sheet's (links to removed tasks go with them).
        var rows = await db.Set<Dependency>().Where(d => d.SheetId == sheetId).ToListAsync(cancellationToken);
        ReplaceLinks(db, sheetId, rows, import.Links);

        // Constraints: an MS Project file sets every task's (none is As Soon As Possible); Excel files leave them alone.
        if (setsConstraints)
        {
            var current = await db.Set<TaskConstraint>().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
            var wanted = import.Tasks.Where(t => t.Constraint is not null).ToDictionary(t => t.ItemId, t => t.Constraint!);
            foreach (var row in current)
            {
                if (!wanted.TryGetValue(row.ItemId, out var c))
                {
                    db.Set<TaskConstraint>().Remove(row);
                }
                else
                {
                    row.Type = (TaskConstraintType)(int)c.Kind;
                    row.Date = c.Date;
                    wanted.Remove(row.ItemId);
                }
            }

            db.Set<TaskConstraint>().AddRange(wanted.Select(kv => new TaskConstraint
            {
                SheetId = sheetId, ItemId = kv.Key, Type = (TaskConstraintType)(int)kv.Value.Kind, Date = kv.Value.Date,
            }));
        }

        // Without the task service (tests, tools), assignments are written directly, without notifications.
        if (tasks is null)
        {
            var assigned = await db.Set<ItemAssignee>().Where(a => a.SheetId == sheetId).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var task in import.Tasks)
            {
                var current = assigned.Where(a => a.ItemId == task.ItemId).ToList();
                db.Set<ItemAssignee>().RemoveRange(current.Where(a => !task.Assignees.Contains(a.Email)));
                db.Set<ItemAssignee>().AddRange(task.Assignees.Where(e => current.All(a => a.Email != e))
                    .Select(e => new ItemAssignee { SheetId = sheetId, ItemId = task.ItemId, Email = e, AssignedBy = "import", AssignedAtUtc = now }));
            }
        }

        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken, clock);
    }

    private static void SetValue(Dictionary<string, string?> values, Guid columnId, string? value)
    {
        if (value is null)
        {
            values.Remove(columnId.ToString());
        }
        else
        {
            values[columnId.ToString()] = value;
        }
    }

    private static bool SameValues(IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SamePeople(IReadOnlyList<string>? before, IReadOnlyList<string> after) =>
        (before ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(after);

    private static void ReplaceLinks(IWorkbenchDbContext db, Guid sheetId, List<Dependency> current, IReadOnlyCollection<PlanLink> wanted)
    {
        static (Guid, Guid, int, int) Key(Guid p, Guid s, int type, int lag) => (p, s, type, lag);
        var keep = wanted.Select(l => Key(l.PredecessorId, l.SuccessorId, (int)l.Type, l.LagDays)).ToHashSet();
        var have = current.Select(d => Key(d.PredecessorId, d.SuccessorId, (int)d.Type, d.LagDays)).ToHashSet();
        db.Set<Dependency>().RemoveRange(current.Where(d => !keep.Contains(Key(d.PredecessorId, d.SuccessorId, (int)d.Type, d.LagDays))));
        db.Set<Dependency>().AddRange(wanted
            .Where(l => !have.Contains(Key(l.PredecessorId, l.SuccessorId, (int)l.Type, l.LagDays)))
            .Select(l => new Dependency
            {
                SheetId = sheetId,
                PredecessorId = l.PredecessorId,
                SuccessorId = l.SuccessorId,
                Type = (DependencyType)(int)l.Type,
                LagDays = l.LagDays,
            }));
    }

    /// <summary><see cref="PercentColumn"/> is the % column the caller may read; the rollup uses <see cref="RollupPercentId"/>, restricted or not.</summary>
    private sealed record State(
        Sheet Sheet, List<Item> Items, List<PlanLink> Links, WorkCalendar Calendar, Column? PercentColumn, Column? NotesColumn, Guid? RollupPercentId,
        Dictionary<Guid, PlanConstraintDto> Constraints);

    private static async Task<State> LoadAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken, bool tracked = false)
    {
        var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var plan = await PlanReflow.LoadAsync(db, sheet, cancellationToken, tracked);

        // Restricted columns aren't read or written here: not everyone who sees the plan may see them.
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId && !c.IsRestricted && c.Formula == null).ToListAsync(cancellationToken);
        var percent = TaskRules.PercentColumn(columns);
        var notes = columns.FirstOrDefault(c => c.Type == ColumnType.Text && string.Equals(c.Name, NotesColumnName, StringComparison.OrdinalIgnoreCase));
        return new State(sheet, plan.Items, plan.Links, plan.Calendar, percent, notes, plan.PercentColumn?.Id, plan.Constraints);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> AssigneesAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        (await db.Set<ItemAssignee>().AsNoTracking().Where(a => a.SheetId == sheetId).ToListAsync(cancellationToken))
            .GroupBy(a => a.ItemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(a => a.AssignedAtUtc).ThenBy(a => a.Email, StringComparer.Ordinal).Select(a => a.Email).ToList());

    private static Item Find(State state, Guid itemId) =>
        state.Items.FirstOrDefault(i => i.Id == itemId) ?? throw WorkbenchNotFoundException.For("Item", itemId);

    /// <summary>Reflows (summaries and links) and saves the restructured or re-dated items.</summary>
    private async Task SaveAsync(IWorkbenchDbContext db, State state, CancellationToken cancellationToken)
    {
        if (state.Sheet.SchedulingEnabled)
        {
            ScheduleEngine.Reflow(state.Items, state.Links, state.Calendar, state.RollupPercentId, state.Constraints);
        }

        var project = await PlanReflow.ProjectForAsync(db, state.Sheet.Id, cancellationToken);
        await PlanReflow.NumberAsync(db, project, state.Items, cancellationToken);

        // On a project's plan the rows are the WBS: indenting or outdenting moves the charge codes too.
        if (project is not null)
        {
            await PlanWbsSync.ApplyAsync(db, project, state.Items, cancellationToken);
        }

        state.Sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task RecordAsync(Guid sheetId, Guid? itemId, string kind, string summary, SheetChangeKind change) =>
        activity?.RecordAsync(sheetId, itemId, kind, summary, change) ?? Task.CompletedTask;

    private static string FileSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Plan" : safe.Length > 80 ? safe[..80] : safe;
    }
}
