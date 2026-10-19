using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain.Notifications;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Tasks;

/// <summary>
/// Task assignment over the real registrations (<see cref="SeededHost"/>: pm@ Owner, analyst@ Editor, reviewer@ Commenter,
/// viewer@ Viewer of the Program Office "Schedule" sheet, whose item is "Kickoff").
/// </summary>
public sealed class TaskAssignmentTests
{
    private sealed record Setup(SeededHost Seeded, RecordingEmailSender Emails, ColumnDto Status, ColumnDto Percent, ColumnDto Reviewer, ColumnDto Notes);

    private static async Task<Setup> CreateAsync()
    {
        var emails = new RecordingEmailSender();
        var seeded = await SeededHost.CreateAsync(s => s.AddSingleton<IEmailSender>(emails));
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<Domain.Identity.AppUser>().AddRange(
                new Domain.Identity.AppUser { Email = Editor, DisplayName = "Alex Rivera" },
                new Domain.Identity.AppUser { Email = Commenter, DisplayName = "Rae Viewer" });
            await db.SaveChangesAsync();
        }

        var columns = seeded.As(Owner).Host.Columns;
        var status = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Not started", "In progress", "Done"]));
        var percent = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("% Complete", ColumnKind.Number));
        var reviewer = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Reviewer", ColumnKind.Person));
        var notes = await columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        return new Setup(seeded, emails, status, percent, reviewer, notes);
    }

    private static ITaskAssignmentService Tasks(SeededHost seeded, string email) => seeded.As(email).Host.Get<ITaskAssignmentService>();

    private static async Task<List<Notification>> NotificationsAsync(SeededHost seeded, string kind)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<Notification>().Where(n => n.Kind == kind).ToListAsync();
    }

    private static async Task<ItemDto> ItemAsync(SeededHost seeded) =>
        (await seeded.As(Owner).Host.Items.ListAsync(seeded.SheetId)).Single(i => i.Id == seeded.ItemId);

    [Fact]
    public async Task Editors_assign_people_with_a_due_date_and_they_are_notified_and_it_is_in_activity()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var due = new DateOnly(2026, 10, 23);

        var assignees = await Tasks(setup.Seeded, Editor).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer, "  ANALYST@example.com "], due));

        Assert.Equal(["Alex Rivera", "Val"], assignees.Select(a => a.DisplayName));
        Assert.All(assignees, a => Assert.Equal(Editor, a.AssignedBy));
        Assert.Equal(due, (await ItemAsync(setup.Seeded)).Finish);

        // The viewer is told; the analyst assigned themselves, so isn't.
        var note = Assert.Single(await NotificationsAsync(setup.Seeded, NotificationKinds.Assignment));
        Assert.Equal(Viewer, note.RecipientEmail);
        Assert.Equal("Alex Rivera assigned you \"Kickoff\"", note.Title);
        Assert.Contains("due Fri, Oct 23", note.Body);
        Assert.Equal(setup.Seeded.ItemId, note.ItemId);

        var activity = await setup.Seeded.As(Viewer).Host.Activity.ListAsync(setup.Seeded.SheetId, setup.Seeded.ItemId);
        Assert.Contains(activity, a => a.Kind == TaskActivityKinds.ItemAssigned && a.Summary.Contains("Assigned Alex Rivera, Val"));

        var sheet = await Tasks(setup.Seeded, Viewer).GetSheetAsync(setup.Seeded.SheetId);
        Assert.False(sheet.CanAssign);
        Assert.True(sheet.IsAssignedTo(setup.Seeded.ItemId, Viewer));
        Assert.Equal(setup.Status.Id, sheet.StatusColumn!.Id);
        Assert.Equal(setup.Percent.Id, sheet.PercentColumn!.Id);
        Assert.True((await Tasks(setup.Seeded, Owner).GetSheetAsync(setup.Seeded.SheetId)).CanAssign);
    }

    [Fact]
    public async Task Unassigning_and_reassigning_only_notifies_people_newly_assigned()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var tasks = Tasks(setup.Seeded, Owner);

        await tasks.SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer]));
        await tasks.SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer, Editor]));
        var cleared = await tasks.SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([]));

        Assert.Empty(cleared);
        Assert.Equal([Editor, Viewer], (await NotificationsAsync(setup.Seeded, NotificationKinds.Assignment)).Select(n => n.RecipientEmail).Order());
        var activity = await setup.Seeded.Host.Activity.ListAsync(setup.Seeded.SheetId, setup.Seeded.ItemId);
        Assert.Contains(activity, a => a.Summary.StartsWith("Unassigned Alex Rivera, Val") || a.Summary.StartsWith("Unassigned Val, Alex Rivera"));
    }

    [Fact]
    public async Task People_who_want_email_right_away_get_one()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        await setup.Seeded.As(Viewer).Host.Get<INotificationService>().SetPreferenceAsync(new NotificationPreferenceDto(Contracts.Notifications.NotificationDelivery.ImmediateEmail));

        await Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer, Editor]));

        var email = Assert.Single(setup.Emails.Sent); // the analyst keeps the daily digest
        Assert.Equal(Viewer, email.To);
        Assert.Contains("Kickoff", email.Subject);
    }

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Commenter)]
    public async Task Viewers_and_commenters_cant_assign(string email)
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            Tasks(setup.Seeded, email).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([email])));
    }

    [Fact]
    public async Task People_outside_the_workspace_dont_see_assignees_and_only_directory_users_can_be_assigned()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Tasks(setup.Seeded, Outsider).GetSheetAsync(setup.Seeded.SheetId));
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest(["stranger@example.com"])));
        Assert.Contains("stranger@example.com", error.Message);
    }

    [Fact]
    public async Task An_assigned_viewer_updates_the_status_and_percent_of_their_own_task_only()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        await Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer]));

        var updated = await Tasks(setup.Seeded, Viewer).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("in progress", 40));

        Assert.Equal("In progress", updated.Values[setup.Status.Id.ToString()]);
        Assert.Equal("40", updated.Values[setup.Percent.Id.ToString()]);
        var activity = await setup.Seeded.Host.Activity.ListAsync(setup.Seeded.SheetId, setup.Seeded.ItemId);
        Assert.Contains(activity, a => a.Kind == ActivityKinds.ItemUpdated && a.Actor == Viewer);

        // Anything else needs edit access, through the item service too.
        var item = await ItemAsync(setup.Seeded);
        var items = setup.Seeded.As(Viewer).Host.Items;
        var values = new Dictionary<string, string?>(item.Values);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            items.UpdateAsync(setup.Seeded.SheetId, item.Id, new UpdateItemRequest("Renamed", item.Start, item.Finish, item.CostpointWbsId, values)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            items.UpdateAsync(setup.Seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId,
                new Dictionary<string, string?>(values) { [setup.Notes.Id.ToString()] = "sneaky" })));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            items.UpdateAsync(setup.Seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, new DateOnly(2027, 1, 1), item.CostpointWbsId, values)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => items.DeleteAsync(setup.Seeded.SheetId, item.Id));

        // A status-only change through the item service is the same narrow permission.
        var done = await items.UpdateAsync(setup.Seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId,
            new Dictionary<string, string?>(values) { [setup.Status.Id.ToString()] = "Done" }));
        Assert.Equal("Done", done.Values[setup.Status.Id.ToString()]);
    }

    [Fact]
    public async Task Viewers_who_arent_assigned_cant_update_the_status_and_bad_values_are_refused()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            Tasks(setup.Seeded, Viewer).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("Done")));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            Tasks(setup.Seeded, Outsider).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("Done")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Tasks(setup.Seeded, Editor).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("Shipped")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Tasks(setup.Seeded, Editor).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest(null, 120)));

        // Editors may update any item's status.
        var updated = await Tasks(setup.Seeded, Editor).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("Done", 100));
        Assert.Equal("100", updated.Values[setup.Percent.Id.ToString()]);
    }

    [Fact]
    public async Task Someone_named_in_a_person_column_counts_as_an_assignee_too()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var item = await ItemAsync(setup.Seeded);
        await setup.Seeded.As(Owner).Host.Items.UpdateAsync(setup.Seeded.SheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId,
            new Dictionary<string, string?>(item.Values) { [setup.Reviewer.Id.ToString()] = Commenter }));

        var updated = await Tasks(setup.Seeded, Commenter).UpdateStatusAsync(setup.Seeded.SheetId, item.Id, new UpdateTaskStatusRequest("In progress"));

        Assert.Equal("In progress", updated.Values[setup.Status.Id.ToString()]);
    }

    [Fact]
    public async Task My_tasks_my_work_and_the_home_to_do_list_show_assigned_items()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var due = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        await Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer], due));

        var mine = Assert.Single(await Tasks(setup.Seeded, Viewer).ListMineAsync());
        Assert.Equal("Kickoff", mine.Title);
        Assert.Equal("Schedule", mine.SheetName);
        Assert.Equal(due, mine.Due);
        Assert.Empty(await Tasks(setup.Seeded, Editor).ListMineAsync());

        // My Work: the sheet has no Person value for the viewer, only the Assignees field.
        var work = await setup.Seeded.As(Viewer).Host.Get<IMyWorkService>().GetAsync();
        Assert.Contains(work.Items, i => i.Item.Id == setup.Seeded.ItemId);

        var todo = await setup.Seeded.As(Viewer).Host.Get<IMyTodoService>().GetAsync();
        var entry = Assert.Single(todo.Items, i => i.Title == "Assigned to you: Kickoff");
        Assert.Contains("Schedule · due " + due.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture), entry.Reason);
        Assert.Equal($"sheets/{setup.Seeded.SheetId}?item={setup.Seeded.ItemId}", entry.Link);
        Assert.DoesNotContain(todo.Items, i => i.Title == "Kickoff"); // not listed twice by the sheet-items provider

        // Done tasks leave the list; losing access to the sheet hides them.
        await Tasks(setup.Seeded, Viewer).UpdateStatusAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new UpdateTaskStatusRequest("Done"));
        Assert.Empty(await Tasks(setup.Seeded, Viewer).ListMineAsync());
        Assert.Single(await Tasks(setup.Seeded, Viewer).ListMineAsync(includeDone: true));
        await setup.Seeded.As(Owner).Host.Get<Contracts.Identity.IWorkspaceMembershipService>().RemoveMemberAsync(setup.Seeded.ProgramOfficeId, Viewer);
        Assert.Empty(await Tasks(setup.Seeded, Viewer).ListMineAsync(includeDone: true));
    }

    [Fact]
    public async Task Deleted_items_drop_their_assignees()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        await Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer]));

        await setup.Seeded.As(Owner).Host.Items.DeleteAsync(setup.Seeded.SheetId, setup.Seeded.ItemId);

        Assert.Empty((await Tasks(setup.Seeded, Owner).GetSheetAsync(setup.Seeded.SheetId)).Assignees);
        Assert.Empty(await Tasks(setup.Seeded, Viewer).ListMineAsync());
    }

    [Fact]
    public async Task A_projects_workload_counts_open_tasks_per_person_for_finance_and_its_manager()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var fixture = await ProjectAccountingFixture.SeedAsync(setup.Seeded); // 1001's plan is the Schedule sheet, managed by pm@
        var overdue = DateOnly.FromDateTime(DateTime.Today).AddDays(-3);
        await Tasks(setup.Seeded, Owner).SetAssigneesAsync(setup.Seeded.SheetId, setup.Seeded.ItemId, new SetAssigneesRequest([Viewer, Editor], overdue));
        var second = await setup.Seeded.As(Owner).Host.Items.CreateAsync(setup.Seeded.SheetId, new CreateItemRequest("Design",
            Values: new Dictionary<string, string?> { [setup.Reviewer.Id.ToString()] = Viewer }));
        var done = await setup.Seeded.Host.Items.CreateAsync(setup.Seeded.SheetId, new CreateItemRequest("Closed out",
            Values: new Dictionary<string, string?> { [setup.Status.Id.ToString()] = "Done", [setup.Reviewer.Id.ToString()] = Viewer }));

        var workload = await Tasks(setup.Seeded, Finance).GetProjectWorkloadAsync(fixture.Project1);

        var val = workload.People.First();
        Assert.Equal(Viewer, val.Email);
        Assert.Equal(2, val.Open); // Kickoff (Assignees) and Design (Person column); the done one isn't counted
        Assert.Equal(1, val.Overdue);
        Assert.Equal(["Kickoff", "Design"], val.Tasks.Select(t => t.Title));
        Assert.Equal(1, workload.People.Single(p => p.Email == Editor).Open);
        Assert.DoesNotContain(val.Tasks, t => t.ItemId == done.Id);
        Assert.Contains(val.Tasks, t => t.ItemId == second.Id);

        Assert.NotNull(await Tasks(setup.Seeded, Owner).GetProjectWorkloadAsync(fixture.Project1)); // the manager
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Tasks(setup.Seeded, Editor).GetProjectWorkloadAsync(fixture.Project1));
    }

    [Fact]
    public async Task Project_managers_assign_on_their_projects_because_they_own_its_workspace()
    {
        var setup = await CreateAsync();
        await using var _ = setup.Seeded;
        var fixture = await ProjectAccountingFixture.SeedAsync(setup.Seeded);
        await ProjectManagerRoleTests.GrantAsync(setup.Seeded, Editor, Contracts.Identity.OrgRoles.ProjectManager);
        await using (var db = await setup.Seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var p2 = await db.Set<Domain.ProjectAccounting.Project>().FirstAsync(p => p.Id == fixture.Project2);
            p2.SheetId = setup.Seeded.FinanceSheetId;
            await db.SaveChangesAsync();
        }

        await setup.Seeded.As(Finance).Host.Get<Contracts.ProjectAccounting.IProjectService>().UpdateProjectAsync(fixture.Project2,
            new Contracts.ProjectAccounting.SaveProjectRequest("2002", "Unmanaged", fixture.Contract2, fixture.AnalystId, Contracts.ProjectAccounting.ProjectState.Active, null, null));
        var item = await setup.Seeded.As(Editor).Host.Items.CreateAsync(setup.Seeded.FinanceSheetId, new CreateItemRequest("Close the books"));
        var assigned = await Tasks(setup.Seeded, Editor).SetAssigneesAsync(setup.Seeded.FinanceSheetId, item.Id, new SetAssigneesRequest([Viewer]));

        Assert.Single(assigned);
    }
}
