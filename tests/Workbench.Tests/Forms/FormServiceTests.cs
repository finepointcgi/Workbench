using Microsoft.EntityFrameworkCore;
using Workbench.Application.Forms;
using Workbench.Contracts;
using Workbench.Contracts.Forms;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Forms;
using Workbench.Domain.Integrations;
using Workbench.Domain.Notifications;
using Workbench.Tests.Identity;
using Workbench.Tests.SheetExtras;
using FormAudience = Workbench.Contracts.Forms.FormAudience;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;

namespace Workbench.Tests.Forms;

public sealed class FormServiceTests
{
    private const string Pm = "owner.pm@example.com";

    /// <summary>The Schedule sheet with Status (dropdown), Due (date), Hours and a restricted Budget; the form asks for title, status, due.</summary>
    private static async Task<(SeededHost Host, FormDto Form, ColumnDto Status, ColumnDto Due, ColumnDto Hours)> FormAsync(FormAudience audience = FormAudience.Workspace)
    {
        var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var status = await host.AddColumnAsync(host.SheetId, "Status", ColumnKind.Dropdown, "New", "Triaged");
        var due = await host.AddColumnAsync(host.SheetId, "Due", ColumnKind.Date);
        var hours = await host.AddColumnAsync(host.SheetId, "Hours", ColumnKind.Number);
        host.As(SeededHost.Editor);
        var form = await host.Host.Get<IFormService>().CreateAsync(host.SheetId, new SaveFormRequest("Request work",
        [
            new FormFieldDto("title", "What do you need?", "One line is fine.", true),
            new FormFieldDto(status.Id.ToString(), "Kind", null, true),
            new FormFieldDto(due.Id.ToString(), "Needed by"),
        ], Audience: audience, ConfirmationMessage: "Got it, we'll be in touch."));
        return (host, form, status, due, hours);
    }

    private static IFormService Forms(SeededHost host) => host.Host.Get<IFormService>();

    private static IPublicFormService Public(SeededHost host) => host.Host.Get<IPublicFormService>();

    private static SubmitFormRequest Answers(FormDto form, params (string Key, string? Value)[] answers) =>
        new(answers.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public async Task A_response_adds_a_row_even_without_edit_access_and_notifies_the_forms_creator()
    {
        var (host, form, status, due, _) = await FormAsync();
        await using var _ = host;
        host.As(SeededHost.Viewer);

        var thanks = await Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "New laptop"), (status.Id.ToString(), "new"), (due.Id.ToString(), "11/02/2026")));

        Assert.Equal("Got it, we'll be in touch.", thanks.Message);
        host.As(SeededHost.Owner);
        var row = (await host.Host.Items.ListAsync(host.SheetId)).Single(i => i.Title == "New laptop");
        Assert.Equal("New", row.Cell(status));
        Assert.Equal("2026-11-02", row.Cell(due));
        Assert.Equal(1, (await Forms(host).ListAsync(host.SheetId)).Single().ResponseCount);
        var notification = await host.DbAsync(db => db.Set<Notification>().SingleAsync(n => n.Kind == FormService.NotificationKind));
        Assert.Equal(SeededHost.Editor, notification.RecipientEmail);
        Assert.Equal(row.Id, notification.ItemId);
        var activity = await host.DbAsync(db => db.Set<ActivityEvent>().Where(a => a.ItemId == row.Id).ToListAsync());
        Assert.Contains(activity, a => a.Actor == SeededHost.Viewer);
        var outbox = await host.DbAsync(db => db.Set<IntegrationOutboxEvent>().SingleAsync(e => e.EventType == "sheet.form_submitted"));
        Assert.Equal(row.Id, outbox.SubjectId);
        Assert.DoesNotContain("New laptop", outbox.PayloadJson);
    }

    [Fact]
    public async Task The_grant_is_for_that_one_row_only()
    {
        var (host, form, status, _, _) = await FormAsync();
        await using var _ = host;
        host.As(SeededHost.Viewer);
        await Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "One"), (status.Id.ToString(), "New")));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Host.Items.CreateAsync(host.SheetId, new CreateItemRequest("Sneaky")));
        var grant = host.Host.Get<FormSubmissionGrant>();
        using (grant.Allow(host.SheetId))
        {
            Assert.True(grant.TryUse(host.SheetId));
            Assert.False(grant.TryUse(host.SheetId));
        }

        Assert.False(grant.TryUse(host.SheetId));
    }

    [Fact]
    public async Task Answers_are_checked_in_plain_words_and_only_form_fields_are_written()
    {
        var (host, form, status, due, hours) = await FormAsync();
        await using var _ = host;
        host.As(SeededHost.Viewer);

        var missing = await Assert.ThrowsAsync<ArgumentException>(() => Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "No kind"))));
        Assert.Equal("Please fill in \"Kind\".", missing.Message);
        var choice = await Assert.ThrowsAsync<ArgumentException>(() => Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "x"), (status.Id.ToString(), "Urgent"))));
        Assert.Contains("Pick one of the choices for \"Kind\"", choice.Message);
        var date = await Assert.ThrowsAsync<ArgumentException>(() => Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "x"), (status.Id.ToString(), "New"), (due.Id.ToString(), "soon"))));
        Assert.Contains("needs a date", date.Message);

        await Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "Sneaky"), (status.Id.ToString(), "New"), (hours.Id.ToString(), "999")));
        host.As(SeededHost.Owner);
        Assert.Null((await host.Host.Items.ListAsync(host.SheetId)).Single(i => i.Title == "Sneaky").Cell(hours));
    }

    [Fact]
    public async Task Computed_linked_and_restricted_columns_cannot_be_form_fields()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var budget = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Budget", ColumnKind.Currency, IsRestricted: true));
        var formula = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Twice", ColumnKind.Number, Formula: "2 * 2"));
        var other = await host.NewProgramSheetAsync("Other");
        var link = await host.AddColumnAsync(host.SheetId, "Related", ColumnKind.LinkedRow, other.ToString());
        var person = await host.AddColumnAsync(host.SheetId, "Requester", ColumnKind.Person);

        foreach (var column in new[] { budget, formula, link })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Forms(host).CreateAsync(host.SheetId, new SaveFormRequest("F", [new FormFieldDto(column.Id.ToString(), "x")])));
        }

        var internalForm = await Forms(host).CreateAsync(host.SheetId, new SaveFormRequest("F", [new FormFieldDto(person.Id.ToString(), "Your email")]));
        Assert.Equal(["title", person.Id.ToString()], internalForm.Fields.Select(f => f.Key)); // Title is always asked first

        await host.MakeAsync(SeededHost.Owner, OrgRoles.ProjectManager);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Forms(host).UpdateAsync(internalForm.Id, new SaveFormRequest("F",
            [new FormFieldDto(person.Id.ToString(), "Your email")], Audience: FormAudience.Public)));
        Assert.Contains("isn't offered on a public form", ex.Message);
    }

    [Fact]
    public async Task Viewers_cannot_build_forms_and_outsiders_cannot_fill_in_workspace_forms()
    {
        var (host, form, status, _, _) = await FormAsync();
        await using var _ = host;

        host.As(SeededHost.Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Forms(host).CreateAsync(host.SheetId, new SaveFormRequest("Mine", [])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Forms(host).DeleteAsync(form.Id));
        Assert.Null((await Forms(host).ListAsync(host.SheetId)).Single().PublicPath);

        host.As(SeededHost.Outsider);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Forms(host).GetForFillingAsync(form.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "x"), (status.Id.ToString(), "New"))));

        host.As(SeededHost.Editor);
        await Forms(host).UpdateAsync(form.Id, new SaveFormRequest(form.Title, form.Fields, Audience: FormAudience.Organization));
        host.As(SeededHost.Outsider);
        var fill = await Forms(host).GetForFillingAsync(form.Id);
        Assert.Equal(["What do you need?", "Kind", "Needed by"], fill.Fields.Select(f => f.Label));
        Assert.Equal(["New", "Triaged"], fill.Fields[1].Options);
        await Forms(host).SubmitAsync(form.Id, Answers(form, ("title", "From outside the workspace"), (status.Id.ToString(), "New")));
    }

    [Fact]
    public async Task Making_a_form_public_needs_the_workspace_owner_with_the_permission()
    {
        var (host, form, _, _, _) = await FormAsync();
        await using var _ = host;
        var makePublic = new SaveFormRequest(form.Title, form.Fields, Audience: FormAudience.Public);

        host.As(SeededHost.Editor); // Editor, not Owner
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Forms(host).UpdateAsync(form.Id, makePublic));
        host.As(SeededHost.Owner); // Owner, but only the Employee role
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Forms(host).UpdateAsync(form.Id, makePublic));
        Assert.False((await Forms(host).ListAsync(host.SheetId)).Single().CanPublish);

        await host.MakeAsync(SeededHost.Owner, OrgRoles.ProjectManager);
        var published = await Forms(host).UpdateAsync(form.Id, makePublic);

        Assert.True(published.CanPublish);
        Assert.StartsWith("f/", published.PublicPath);
    }

    private static async Task<(SeededHost Host, FormDto Form, string Token, ColumnDto Status)> PublicFormAsync()
    {
        var (host, form, status, _, _) = await FormAsync();
        await host.MakeAsync(SeededHost.Owner, OrgRoles.ProjectManager);
        host.As(SeededHost.Owner);
        var published = await Forms(host).UpdateAsync(form.Id,
            new SaveFormRequest(form.Title, form.Fields, ConfirmationMessage: form.ConfirmationMessage, Audience: FormAudience.Public));
        host.As(""); // nobody signed in
        return (host, published, published.PublicPath!["f/".Length..], status);
    }

    [Fact]
    public async Task A_public_form_shows_only_its_fields_and_adds_a_row_without_signing_in()
    {
        var (host, form, token, status) = await PublicFormAsync();
        await using var _ = host;

        var fill = await Public(host).GetAsync(token);
        Assert.NotNull(fill);
        Assert.Equal(["title", status.Id.ToString(), form.Fields[2].Key], fill.Fields.Select(f => f.Key));
        Assert.Null(await Public(host).GetAsync(token[..^1] + (token[^1] == 'a' ? 'b' : 'a')));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Host.Items.ListAsync(host.SheetId));

        await Public(host).SubmitAsync(token, Answers(form, ("title", "Broken badge reader"), (status.Id.ToString(), "New")), honeypot: null, clientAddress: "203.0.113.5");

        host.As(SeededHost.Owner);
        Assert.Contains(await host.Host.Items.ListAsync(host.SheetId), i => i.Title == "Broken badge reader");
        var submission = await host.DbAsync(db => db.Set<FormSubmission>().SingleAsync());
        Assert.True(submission.IsPublic);
        Assert.Null(submission.SubmittedBy);
        Assert.DoesNotContain("203.0.113.5", submission.ClientKey);
        var notification = await host.DbAsync(db => db.Set<Notification>().SingleAsync(n => n.Kind == FormService.NotificationKind));
        Assert.Contains("public link", notification.Body);
    }

    [Fact]
    public async Task Public_responses_are_rate_limited_per_client()
    {
        var (host, form, token, status) = await PublicFormAsync();
        await using var _ = host;

        for (var i = 0; i < FormService.PublicPerClient; i++)
        {
            await Public(host).SubmitAsync(token, Answers(form, ("title", $"Row {i}"), (status.Id.ToString(), "New")), null, "198.51.100.7");
        }

        await Assert.ThrowsAsync<FormRateLimitException>(() =>
            Public(host).SubmitAsync(token, Answers(form, ("title", "One too many"), (status.Id.ToString(), "New")), null, "198.51.100.7"));
        await Public(host).SubmitAsync(token, Answers(form, ("title", "Someone else"), (status.Id.ToString(), "New")), null, "198.51.100.8");
        host.As(SeededHost.Owner);
        Assert.DoesNotContain(await host.Host.Items.ListAsync(host.SheetId), i => i.Title == "One too many");
    }

    [Fact]
    public async Task A_filled_in_honeypot_is_thanked_and_thrown_away()
    {
        var (host, form, token, status) = await PublicFormAsync();
        await using var _ = host;

        var thanks = await Public(host).SubmitAsync(token, Answers(form, ("title", "Buy cheap watches"), (status.Id.ToString(), "New")), "http://spam.example", "192.0.2.1");

        Assert.Equal("Got it, we'll be in touch.", thanks.Message);
        host.As(SeededHost.Owner);
        Assert.DoesNotContain(await host.Host.Items.ListAsync(host.SheetId), i => i.Title == "Buy cheap watches");
        Assert.Equal("honeypot", (await host.DbAsync(db => db.Set<FormSubmission>().SingleAsync())).RejectedReason);
        Assert.Equal(0, (await Forms(host).ListAsync(host.SheetId)).Single().ResponseCount);
    }

    [Fact]
    public async Task Resetting_the_link_or_closing_the_form_stops_the_old_link()
    {
        var (host, form, token, status) = await PublicFormAsync();
        await using var _ = host;
        host.As(SeededHost.Owner);

        var reset = await Forms(host).ResetPublicLinkAsync(form.Id);
        var newToken = reset.PublicPath!["f/".Length..];
        host.As("");
        Assert.Null(await Public(host).GetAsync(token));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Public(host).SubmitAsync(token, Answers(form, ("title", "x")), null, "192.0.2.9"));
        Assert.NotNull(await Public(host).GetAsync(newToken));

        host.As(SeededHost.Owner);
        await Forms(host).UpdateAsync(form.Id, new SaveFormRequest(form.Title, form.Fields, Audience: FormAudience.Public, IsOpen: false));
        host.As("");
        Assert.False((await Public(host).GetAsync(newToken))!.IsOpen);
        await Assert.ThrowsAsync<ArgumentException>(() => Public(host).SubmitAsync(newToken, Answers(form, ("title", "x"), (status.Id.ToString(), "New")), null, "192.0.2.9"));

        host.As(SeededHost.Owner);
        await Forms(host).UpdateAsync(form.Id, new SaveFormRequest(form.Title, form.Fields, Audience: FormAudience.Workspace));
        host.As("");
        Assert.Null(await Public(host).GetAsync(newToken));
    }
}
