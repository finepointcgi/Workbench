using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Scheduling;
using Workbench.Contracts.Scheduling;
using Workbench.Domain.Identity;
using Workbench.Domain.Tasks;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>
/// MS Project XML (.xml, as MS Project saves it): the sample files in Samples/ import through the same preview as Excel,
/// and an exported plan imports back unchanged, both into the same plan and into a new one.
/// </summary>
public sealed class MsProjectXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/project";
    private static readonly DateOnly Mon = PlanKit.Mon;

    private readonly PlanKit _kit = new();

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Scheduling", "Samples", name));

    private async Task UserAsync(string email, string name)
    {
        await using var db = await _kit.Factory.CreateAsync();
        db.Set<AppUser>().Add(new AppUser { Email = email, DisplayName = name });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ImportOfficeMoveAsync()
    {
        await UserAsync("pat@example.com", "Pat Lee");
        var sheet = await _kit.SheetAsync("Office move");
        await _kit.Plan.ApplyImportAsync(sheet, new PlanImportRequest("office-move.xml", Sample("office-move.xml")));
        return sheet;
    }

    [Fact]
    public void Reads_tasks_outline_durations_links_constraints_progress_and_resources()
    {
        var rows = MsProjectXml.Read("office-move.xml", Sample("office-move.xml"));

        Assert.Equal(11, rows.Count); // the project summary task (ID 0) isn't a row
        Assert.Equal(Enumerable.Range(1, 11), rows.Select(r => r.Row));
        Assert.Equal([1, 2, 3, 3, 3, 2, 3, 3, 3, 3, 3], rows.Select(r => r.Task.Level));
        Assert.All(rows, r => Assert.Empty(r.Errors));

        var kickoff = rows[2].Task;
        Assert.Equal(("Kickoff", 0, Mon, Mon), (kickoff.Name, kickoff.DurationDays!.Value, kickoff.Start!.Value, kickoff.Finish!.Value));
        var survey = rows[3].Task;
        Assert.Equal((3, "3"), (survey.DurationDays!.Value, survey.Predecessors!)); // FS from UID 30 = ID 3
        Assert.Equal(100, survey.PercentComplete);
        Assert.Equal("pat@example.com", survey.Resources);
        Assert.Equal("Measure every room; the landlord has the floor plans.", survey.Notes);
        Assert.Null(survey.WbsCode); // MS Project's default WBS is the outline number, not a charge code

        var furniture = rows[4].Task;
        Assert.Equal("4SS+1d", furniture.Predecessors);
        Assert.Equal("FURN-01", furniture.WbsCode);
        Assert.Equal(new PlanConstraintDto(PlanConstraintKind.StartNoEarlierThan, new DateOnly(2026, 10, 6)), furniture.Constraint);
        Assert.Equal("4FS-1d", rows[6].Task.Predecessors);
        Assert.Equal(new PlanConstraintDto(PlanConstraintKind.MustStartOn, new DateOnly(2026, 10, 16)), rows[7].Task.Constraint);
        Assert.Equal("pat@example.com; Movers Inc.", rows[7].Task.Resources);
        Assert.Equal("8FF", rows[8].Task.Predecessors);
        Assert.Equal("8SF", rows[9].Task.Predecessors);
        Assert.Equal("8FS+2d", rows[10].Task.Predecessors);
        Assert.Equal(new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, new DateOnly(2026, 10, 23)), rows[10].Task.Constraint);
        Assert.True(rows[0].Task.IsSummary);
        Assert.Equal(Guid.Parse("3F0A6C1E-7B21-4F7E-8C55-0D1E2F3A4B40"), survey.UniqueId);
    }

    [Fact]
    public async Task The_preview_shows_what_a_file_adds_and_warns_about_people_who_arent_users()
    {
        await UserAsync("pat@example.com", "Pat Lee");
        var sheet = await _kit.SheetAsync();

        var preview = await _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest("office-move.xml", Sample("office-move.xml")));

        Assert.False(preview.HasErrors);
        Assert.Equal(11, preview.Count(PlanImportChangeKind.Added));
        Assert.Equal(0, preview.Count(PlanImportChangeKind.Removed));
        Assert.Equal([7, 8], preview.Issues.Where(i => i.Message.Contains("Movers Inc.")).Select(i => i.Row));
        Assert.All(preview.Issues, i => Assert.False(i.IsError));
        Assert.DoesNotContain(preview.Issues, i => i.Message.Contains("Unique ID")); // MS Project's own GUIDs are just new tasks
        var move = preview.Changes.Single(c => c.Title == "Move day");
        Assert.Contains("Must Start On 10/16/2026", move.Details);
        Assert.Empty(await _kit.Items.ListAsync(sheet)); // nothing applied
    }

    [Fact]
    public async Task Importing_builds_the_plan_with_its_links_constraints_and_people()
    {
        var sheet = await ImportOfficeMoveAsync();

        var items = await _kit.Items.ListAsync(sheet);
        var plan = await _kit.Plan.GetAsync(sheet);
        Assert.Equal(["1", "1.1", "1.1.1", "1.1.2", "1.1.3", "1.2", "1.2.1", "1.2.2", "1.2.3", "1.2.4", "1.2.5"], plan.Tasks.Select(t => t.OutlineNumber));
        Assert.Equal(["", "", "", "3", "4SS+1d", "", "4FS-1d", "7", "8FF", "8SF", "8FS+2d"], plan.Tasks.Select(t => t.Predecessors));
        ItemDtoDates(items, "Order furniture", new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 12));
        ItemDtoDates(items, "Pack", new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8));
        ItemDtoDates(items, "Move day", new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 16));
        ItemDtoDates(items, "Set up the network", new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 16));
        ItemDtoDates(items, "Tell the post office", new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));
        ItemDtoDates(items, "Sign-off", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20));
        ItemDtoDates(items, "Office move", Mon, new DateOnly(2026, 10, 20)); // rolled up
        Assert.True(items.Single(i => i.Title == "Sign-off").IsMilestone);
        Assert.Equal(PlanConstraintKind.MustStartOn, plan.For(items.Single(i => i.Title == "Move day").Id)!.Constraint!.Kind);
        Assert.Equal(50, plan.For(items.Single(i => i.Title == "Order furniture").Id)!.PercentComplete);
        Assert.Empty(plan.AllConflicts);

        await using var db = await _kit.Factory.CreateAsync();
        var survey = items.Single(i => i.Title == "Survey the new site").Id;
        Assert.Equal(["pat@example.com"], await db.Set<ItemAssignee>().Where(a => a.ItemId == survey).Select(a => a.Email).ToListAsync());
    }

    [Fact]
    public async Task An_exported_plan_imports_back_unchanged()
    {
        var sheet = await ImportOfficeMoveAsync();
        await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Approved"));

        var file = await _kit.Plan.ExportMsProjectAsync(sheet);
        Assert.Equal("Office move plan.xml", file.FileName);
        Assert.Equal("application/xml", file.ContentType);

        var preview = await _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest(file.FileName, file.Content));
        Assert.Empty(preview.Changes);
        Assert.Equal(11, preview.Unchanged);
        Assert.Empty(preview.Issues);
    }

    [Fact]
    public async Task A_plan_without_progress_round_trips_without_inventing_any()
    {
        var sheet = await _kit.SheetAsync();
        var a = await _kit.TaskAsync(sheet, "Design", Mon, 5);
        var b = await _kit.TaskAsync(sheet, "Build", Mon, 3);
        await _kit.LinkAsync(sheet, a.Id, b.Id, DependencyKind.StartToFinish, 2);

        var file = await _kit.Plan.ExportMsProjectAsync(sheet);
        var preview = await _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest(file.FileName, file.Content));

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Issues);
        Assert.DoesNotContain("PercentComplete", System.Text.Encoding.UTF8.GetString(file.Content));
    }

    [Fact]
    public async Task An_exported_plan_imports_into_a_new_plan_with_the_same_schedule()
    {
        var sheet = await ImportOfficeMoveAsync();
        var file = await _kit.Plan.ExportMsProjectAsync(sheet);
        var copy = await _kit.SheetAsync("Copy");

        await _kit.Plan.ApplyImportAsync(copy, new PlanImportRequest(file.FileName, file.Content));

        var original = await _kit.Plan.GetAsync(sheet);
        var copied = await _kit.Plan.GetAsync(copy);
        Assert.Equal(original.Tasks.Select(t => (t.OutlineNumber, t.DurationDays, t.Predecessors, t.Constraint, t.PercentComplete)),
            copied.Tasks.Select(t => (t.OutlineNumber, t.DurationDays, t.Predecessors, t.Constraint, t.PercentComplete)));
        var a = await _kit.Items.ListAsync(sheet);
        var b = await _kit.Items.ListAsync(copy);
        Assert.Equal(a.OrderBy(i => i.Title).Select(i => (i.Title, i.Start, i.Finish, i.IsMilestone)), b.OrderBy(i => i.Title).Select(i => (i.Title, i.Start, i.Finish, i.IsMilestone)));
        Assert.All(b, i => Assert.DoesNotContain(i.Id, a.Select(x => x.Id)));
    }

    [Fact]
    public async Task The_export_is_MS_Project_XML_in_schema_order()
    {
        await _kit.HolidayAsync(new DateOnly(2026, 11, 26), "Thanksgiving");
        var sheet = await ImportOfficeMoveAsync();
        await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Approved"));
        var doc = XDocument.Load(new MemoryStream((await _kit.Plan.ExportMsProjectAsync(sheet)).Content));

        var project = doc.Root!;
        Assert.Equal(Ns + "Project", project.Name);
        Assert.Equal("Office move", project.Element(Ns + "Title")!.Value);
        Assert.Equal("480", project.Element(Ns + "MinutesPerDay")!.Value);
        AssertInOrder(project.Elements().Select(e => e.Name.LocalName),
            ["SaveVersion", "Name", "Title", "CreationDate", "ScheduleFromStart", "StartDate", "FinishDate", "CalendarUID", "DefaultStartTime",
             "DefaultFinishTime", "MinutesPerDay", "MinutesPerWeek", "DaysPerMonth", "DefaultTaskType", "DurationFormat", "Calendars", "Tasks",
             "Resources", "Assignments"]);

        var tasks = project.Element(Ns + "Tasks")!.Elements(Ns + "Task").ToList();
        Assert.Equal(12, tasks.Count); // project summary + 11
        Assert.Equal("0", tasks[0].Element(Ns + "OutlineLevel")!.Value);
        string[] taskOrder =
        [
            "UID", "GUID", "ID", "Name", "Active", "Manual", "Type", "IsNull", "WBS", "OutlineNumber", "OutlineLevel", "Start", "Finish",
            "Duration", "DurationFormat", "Estimated", "Milestone", "Summary", "PercentComplete", "ConstraintType", "ConstraintDate", "Notes",
            "PredecessorLink", "Baseline",
        ];
        foreach (var task in tasks)
        {
            AssertInOrder(task.Elements().Select(e => e.Name.LocalName), taskOrder);
        }

        XElement Task(string name) => tasks.Single(t => t.Element(Ns + "Name")!.Value == name);
        var furniture = Task("Order furniture");
        Assert.Equal("PT40H0M0S", furniture.Element(Ns + "Duration")!.Value);
        Assert.Equal("4", furniture.Element(Ns + "ConstraintType")!.Value);
        var link = furniture.Element(Ns + "PredecessorLink")!;
        Assert.Equal(("3", "4800"), (link.Element(Ns + "Type")!.Value, link.Element(Ns + "LinkLag")!.Value)); // SS, 1 day
        Assert.Equal(Task("Survey the new site").Element(Ns + "UID")!.Value, link.Element(Ns + "PredecessorUID")!.Value);
        Assert.Equal("0", Task("Set up the network").Element(Ns + "PredecessorLink")!.Element(Ns + "Type")!.Value); // FF
        Assert.Equal("2", Task("Tell the post office").Element(Ns + "PredecessorLink")!.Element(Ns + "Type")!.Value); // SF
        Assert.Equal("-4800", Task("Pack").Element(Ns + "PredecessorLink")!.Element(Ns + "LinkLag")!.Value);
        Assert.Equal("1", Task("Sign-off").Element(Ns + "Milestone")!.Value);
        Assert.Equal("FURN-01", furniture.Element(Ns + "WBS")!.Value);
        var baseline = furniture.Element(Ns + "Baseline")!;
        Assert.Equal(("0", "2026-10-06T08:00:00", "2026-10-12T17:00:00"),
            (baseline.Element(Ns + "Number")!.Value, baseline.Element(Ns + "Start")!.Value, baseline.Element(Ns + "Finish")!.Value));

        // The calendar: Monday to Friday and the company holiday as an exception.
        var weekDays = project.Descendants(Ns + "WeekDay").ToList();
        Assert.Equal(["0", "1", "1", "1", "1", "1", "0"], weekDays.Take(7).Select(w => w.Element(Ns + "DayWorking")!.Value));
        var holiday = weekDays.Single(w => w.Element(Ns + "DayType")!.Value == "0");
        Assert.Equal("2026-11-26T00:00:00", holiday.Element(Ns + "TimePeriod")!.Element(Ns + "FromDate")!.Value);

        // People: Pat by name and email, assigned to the two tasks.
        var pat = project.Element(Ns + "Resources")!.Elements(Ns + "Resource").Single();
        Assert.Equal(("Pat Lee", "pat@example.com"), (pat.Element(Ns + "Name")!.Value, pat.Element(Ns + "EmailAddress")!.Value));
        Assert.Equal(2, project.Element(Ns + "Assignments")!.Elements().Count());
    }

    [Fact]
    public async Task A_file_with_problems_is_explained_row_by_row_and_not_imported()
    {
        var sheet = await _kit.SheetAsync();
        var request = new PlanImportRequest("problems.xml", Sample("problems.xml"));

        var preview = await _kit.Plan.PreviewImportAsync(sheet, request);

        string Issue(int row) => string.Join(" | ", preview.Issues.Where(i => i.Row == row).Select(i => (i.IsError ? "error: " : "warning: ") + i.Message));
        Assert.Equal("warning: The lag on its link to task 1 is 7 elapsed days, read as 5 working days.", Issue(2));
        Assert.Contains("percentage", Issue(3));
        Assert.Contains("As Late As Possible isn't supported", Issue(4));
        Assert.Contains("rounded to 1 working day", Issue(5));
        Assert.Contains("UID 99", Issue(5));
        Assert.DoesNotContain(preview.Issues, i => i.Row == 6); // MS Project's blank rows are skipped
        Assert.StartsWith("error: Outline level 3", Issue(7));
        Assert.Equal("error: Finish No Later Than has no constraint date.", Issue(8));
        Assert.True(preview.HasErrors);
        await Assert.ThrowsAsync<ArgumentException>(() => _kit.Plan.ApplyImportAsync(sheet, request));
        Assert.Empty(await _kit.Items.ListAsync(sheet));
    }

    [Fact]
    public async Task Files_that_arent_MS_Project_XML_are_refused_with_a_hint()
    {
        var sheet = await _kit.SheetAsync();

        var notXml = await Assert.ThrowsAsync<ArgumentException>(() =>
            _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest("plan.xml", "this is an .mpp, honest"u8.ToArray())));
        Assert.Contains("Save As", notXml.Message);
        var otherXml = await Assert.ThrowsAsync<ArgumentException>(() =>
            _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest("plan.xml", "<?xml version=\"1.0\"?><Workbook/>"u8.ToArray())));
        Assert.Contains(".mpp", otherXml.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => _kit.Plan.PreviewImportAsync(sheet, new PlanImportRequest("plan.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE Project [<!ENTITY x \"y\">]><Project>&x;</Project>"u8.ToArray())));
    }

    [Fact]
    public async Task Viewers_can_export_MS_Project_XML()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var file = await seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().ExportMsProjectAsync(seeded.SheetId);
        Assert.EndsWith(".xml", file.FileName);
        Assert.Equal(Ns + "Project", XDocument.Load(new MemoryStream(file.Content)).Root!.Name);
    }

    private static void ItemDtoDates(IReadOnlyList<Workbench.Contracts.Sheets.ItemDto> items, string title, DateOnly start, DateOnly finish)
    {
        var item = items.Single(i => i.Title == title);
        Assert.Equal((title, start, finish), (item.Title, item.Start!.Value, item.Finish!.Value));
    }

    /// <summary>Every element appears in <paramref name="order"/>, in that order (an element may be missing, not out of place).</summary>
    private static void AssertInOrder(IEnumerable<string> names, IReadOnlyList<string> order)
    {
        var last = -1;
        foreach (var name in names)
        {
            var at = order.ToList().IndexOf(name);
            Assert.True(at >= 0, $"Unexpected element {name}");
            Assert.True(at >= last, $"{name} is out of the schema's order");
            last = at;
        }
    }
}
