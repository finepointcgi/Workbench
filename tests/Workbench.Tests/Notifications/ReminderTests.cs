using System.Net.Http.Json;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations;
using Workbench.Application.MyTodo;
using Workbench.Application.Notifications;
using Workbench.Application.Time;
using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Approvals;
using Workbench.Domain.Leave;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Automation;
using Workbench.Tests.Collaboration;
using Workbench.Tests.Identity;
using Workbench.Tests.Integrations;
using Workbench.Ui.Notifications;

namespace Workbench.Tests.Notifications;

/// <summary>
/// Proactive reminders: "your timesheet isn't in" and the approvers' "waiting on you" digest, over the real registrations, an
/// in-memory database, a manual clock (UTC) and a recording mailer. Weeks are Monday to Sunday, so the due date is Friday.
/// </summary>
public sealed class ReminderTests
{
    private const string Dev = "dev@example.com";
    private const string Pat = "pat@example.com";

    // Mon 2026-10-12 .. Sun 2026-10-18 is the current week in these tests.
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Thursday = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Friday = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Saturday = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tuesday = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private (Guid, Guid, Guid)? _parents;

        public Host(DateTimeOffset now, IEmailSender? mailer = null)
        {
            Db = TestDb.CreateFactory();
            Clock = new ManualClock(now);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            services.AddSingleton(Db);
            services.AddSingleton<IBlobStore>(new MemoryBlobStore());
            services.AddSingleton<ICurrentUser>(User);
            services.AddWorkbenchApplication();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(mailer ?? Emails);
            services.Configure<TimekeepingOptions>(o => o.TimeZone = "UTC");
            services.Replace(ServiceDescriptor.Singleton<IOutboundTransport>(Transport));
            services.Replace(ServiceDescriptor.Singleton<IOutboundDns>(new FakeDns()));
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _provider.CreateAsyncScope();
        }

        public IWorkbenchDbContextFactory Db { get; }
        public ManualClock Clock { get; }
        public RecordingEmailSender Emails { get; } = new();
        public FakeTransport Transport { get; } = new();
        public TestUser User { get; } = new() { Email = Dev };

        public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public async Task<int> TimesheetsAsync(DateTimeOffset? at = null)
        {
            if (at is { } now)
            {
                Clock.Now = now;
            }

            return await Get<TimesheetReminderCheck>().RunAsync();
        }

        public async Task<int> DigestAsync(DateTimeOffset? at = null)
        {
            if (at is { } now)
            {
                Clock.Now = now;
            }

            return await Get<ApprovalDigestCheck>().RunAsync();
        }

        public async Task<Employee> EmployeeAsync(string email, string name = "Dev Person")
        {
            await using var db = await Db.CreateAsync();
            var employee = new Employee { EmployeeNumber = email[..3], Email = email, DisplayName = name, HireDate = new DateOnly(2025, 1, 6) };
            db.Set<Employee>().Add(employee);
            await db.SaveChangesAsync();
            return employee;
        }

        /// <summary>A WBS element, labor category and leave policy for rows that point at them (real databases enforce the references).</summary>
        public async Task<(Guid Wbs, Guid Labor, Guid Policy)> ParentsAsync()
        {
            if (_parents is { } known)
            {
                return known;
            }

            await using var db = await Db.CreateAsync();
            var project = new Project { Code = "P1", Name = "Project" };
            db.Set<Project>().Add(project);
            var wbs = new WbsElement { ProjectId = project.Id, Code = "1.1", Name = "Work" };
            var labor = new LaborCategory { Code = "ENG", Name = "Engineer" };
            db.Set<WbsElement>().Add(wbs);
            db.Set<LaborCategory>().Add(labor);
            var policy = new LeavePolicy { Code = "PTO", Name = "Paid time off", WbsElementId = wbs.Id };
            db.Set<LeavePolicy>().Add(policy);
            await db.SaveChangesAsync();
            return (_parents = (wbs.Id, labor.Id, policy.Id)).Value;
        }

        public async Task SheetAsync(Employee employee, TimesheetStatus status, DateOnly? start = null, decimal hours = 0)
        {
            await using var db = await Db.CreateAsync();
            var from = start ?? new DateOnly(2026, 10, 12);
            var sheet = new Timesheet { EmployeeId = employee.Id, PeriodStart = from, PeriodEnd = from.AddDays(6), Status = status };
            db.Set<Timesheet>().Add(sheet);
            if (hours > 0)
            {
                // The reminder only needs the hours total, which comes from the entries' work dates.
                var (wbs, labor, _) = await ParentsAsync();
                db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WorkDate = from, Hours = hours, WbsElementId = wbs, LaborCategoryId = labor });
            }

            await db.SaveChangesAsync();
        }

        public async Task SetAsync(string email, Action<ReminderPreference> change)
        {
            await using var db = await Db.CreateAsync();
            var row = new ReminderPreference { UserEmail = email };
            change(row);
            db.Set<ReminderPreference>().Add(row);
            await db.SaveChangesAsync();
        }

        public async Task<List<Notification>> NotificationsAsync(string email, string? kind = null)
        {
            await using var db = await Db.CreateAsync();
            return await db.Set<Notification>().Where(n => n.RecipientEmail == email && (kind == null || n.Kind == kind)).OrderBy(n => n.CreatedAtUtc).ToListAsync();
        }

        public async Task WaitingApprovalAsync(string approver, DateTime since, string title = "Expense report 17")
        {
            await using var db = await Db.CreateAsync();
            var workflow = await db.Set<ApprovalWorkflow>().FirstOrDefaultAsync(w => w.DocumentType == ApprovalDocumentTypes.ExpenseReport);
            if (workflow is null)
            {
                workflow = new ApprovalWorkflow { DocumentType = ApprovalDocumentTypes.ExpenseReport, Name = "Expenses" };
                db.Set<ApprovalWorkflow>().Add(workflow);
                db.Set<ApprovalWorkflowVersion>().Add(new ApprovalWorkflowVersion { WorkflowId = workflow.Id, Version = 1, DefinitionJson = "{}", CreatedBy = Pat });
                await db.SaveChangesAsync();
            }

            var version = await db.Set<ApprovalWorkflowVersion>().FirstAsync(v => v.WorkflowId == workflow.Id);
            var instance = new ApprovalInstance
            {
                DocumentType = ApprovalDocumentTypes.ExpenseReport, DocumentId = Guid.NewGuid(), WorkflowVersionId = version.Id,
                SubmitterEmail = "someone@example.com", Title = title, Link = "expenses", StartedAtUtc = since,
            };
            db.Set<ApprovalInstance>().Add(instance);
            db.Set<ApprovalTask>().Add(new ApprovalTask
            {
                InstanceId = instance.Id, StepName = "Manager", Status = ApprovalTaskStatus.Pending, AssigneesCsv = approver, ActivatedAtUtc = since,
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    // ---- Your timesheet isn't in ----

    [Fact]
    public async Task Timesheet_nudges_come_the_day_before_on_the_due_date_and_once_when_overdue_then_stop()
    {
        await using var host = new Host(Wednesday);
        await host.EmployeeAsync(Dev);

        Assert.Equal(0, await host.TimesheetsAsync());                       // Wednesday: too early
        Assert.Equal(1, await host.TimesheetsAsync(Thursday));               // the business day before the due date
        Assert.Equal(0, await host.TimesheetsAsync());                       // a restart (same day) never repeats it
        Assert.Equal(1, await host.TimesheetsAsync(Friday));                 // due
        Assert.Equal(0, await host.TimesheetsAsync(Saturday));               // never on weekends
        Assert.Equal(0, await host.TimesheetsAsync(Saturday.AddDays(1)));
        Assert.Equal(1, await host.TimesheetsAsync(Monday));                 // overdue
        Assert.Equal(0, await host.TimesheetsAsync(Tuesday));                // then it stops
        Assert.Equal(0, await host.TimesheetsAsync(Tuesday.AddDays(1)));

        var titles = (await host.NotificationsAsync(Dev, ReminderKinds.Timesheet)).Select(n => n.Title).ToList();
        Assert.Equal(["Your timesheet is due tomorrow", "Your timesheet is due today", "Your timesheet is overdue"], titles);
        var email = host.Emails.Sent.First();
        Assert.Equal(Dev, email.To);
        Assert.Contains("time?date=2026-10-12", email.TextBody);
        Assert.Contains("You haven't entered any time for Oct 12–18 yet.", email.TextBody);
    }

    [Fact]
    public async Task Submitted_approved_and_returned_timesheets_and_people_on_leave_are_left_alone_but_drafts_are_nudged()
    {
        await using var host = new Host(Thursday);
        var draft = await host.EmployeeAsync(Dev);
        var submitted = await host.EmployeeAsync("sub@example.com");
        var approved = await host.EmployeeAsync("app@example.com");
        var rejected = await host.EmployeeAsync("rej@example.com");
        var onLeave = await host.EmployeeAsync("off@example.com");
        await host.SheetAsync(draft, TimesheetStatus.Draft, hours: 12);
        await host.SheetAsync(submitted, TimesheetStatus.Submitted);
        await host.SheetAsync(approved, TimesheetStatus.Approved);
        await host.SheetAsync(rejected, TimesheetStatus.Rejected);
        var (_, _, policyId) = await host.ParentsAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            var request = new LeaveRequest { EmployeeId = onLeave.Id, PolicyId = policyId, StartDate = new DateOnly(2026, 10, 15), EndDate = new DateOnly(2026, 10, 15), Hours = 8, Status = LeaveRequestStatus.Approved };
            request.Days.Add(new LeaveRequestDay { Date = new DateOnly(2026, 10, 15), Hours = 8 });
            db.Set<LeaveRequest>().Add(request);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await host.TimesheetsAsync());

        var only = Assert.Single(await host.NotificationsAsync(Dev, ReminderKinds.Timesheet));
        Assert.Contains("12 of 40 hours entered for Oct 12–18, and it isn't submitted yet.", only.Body);
        foreach (var other in new[] { "sub@example.com", "app@example.com", "rej@example.com", "off@example.com" })
        {
            Assert.Empty(await host.NotificationsAsync(other));
        }
    }

    [Fact]
    public async Task The_reminder_and_the_home_todo_agree_on_who_still_owes_a_timesheet()
    {
        await using var host = new Host(Friday);
        var late = await host.EmployeeAsync(Dev);
        var done = await host.EmployeeAsync("done@example.com");
        await host.SheetAsync(late, TimesheetStatus.Draft);
        await host.SheetAsync(done, TimesheetStatus.Submitted);
        var provider = new TimesheetTodoProvider(host.Get<TimekeepingClock>());

        async Task<bool> HasTodoAsync(Employee employee) =>
            (await provider.GetAsync(new MyTodoContext(new CurrentUserInfo(employee.Email, employee.DisplayName, OrgRoles.Employee, employee.Id), host.Clock.Today, host.Get<IServiceProvider>(), host.Db), default))
            .Any(i => i.Key.StartsWith("timesheet:", StringComparison.Ordinal));

        Assert.Equal(1, await host.TimesheetsAsync());

        Assert.True(await HasTodoAsync(late));
        Assert.NotEmpty(await host.NotificationsAsync(late.Email, ReminderKinds.Timesheet));
        Assert.False(await HasTodoAsync(done));
        Assert.Empty(await host.NotificationsAsync(done.Email));
    }

    [Fact]
    public async Task People_choose_whether_to_be_reminded_and_where_and_quiet_hours_hold_a_reminder_back_until_they_end()
    {
        await using var host = new Host(Thursday);
        var off = await host.EmployeeAsync("off@example.com");
        var inApp = await host.EmployeeAsync("bell@example.com");
        var quiet = await host.EmployeeAsync("quiet@example.com");
        await host.SetAsync(off.Email, p => p.TimesheetEnabled = false);
        await host.SetAsync(inApp.Email, p => p.ViaEmail = false);
        await host.SetAsync(quiet.Email, p => (p.QuietStartHour, p.QuietEndHour) = (9, 12)); // not before noon

        Assert.Equal(1, await host.TimesheetsAsync());

        Assert.Empty(await host.NotificationsAsync(off.Email));
        Assert.Single(await host.NotificationsAsync(inApp.Email, ReminderKinds.Timesheet));           // the bell still shows it
        Assert.DoesNotContain(host.Emails.Sent, m => m.To == inApp.Email);                              // but no email
        Assert.Empty(await host.NotificationsAsync(quiet.Email));
        Assert.Equal(1, await host.TimesheetsAsync(Thursday.AddHours(3)));                              // quiet hours over
        Assert.Contains(host.Emails.Sent, m => m.To == quiet.Email);
    }

    [Fact]
    public async Task A_reminder_is_not_repeated_in_the_daily_digest_when_the_person_chose_no_email()
    {
        await using var host = new Host(Thursday);
        var inApp = await host.EmployeeAsync("bell@example.com");
        await host.SetAsync(inApp.Email, p => p.ViaEmail = false);

        await host.TimesheetsAsync();

        Assert.NotNull(Assert.Single(await host.NotificationsAsync(inApp.Email)).EmailedAtUtc);
    }

    [Fact]
    public async Task A_broken_mail_server_never_stops_the_reminder_run_or_other_people_getting_theirs()
    {
        var failing = new ThrowingMailer();
        await using var host = new Host(Thursday, failing);
        await host.EmployeeAsync(Dev);
        await host.EmployeeAsync("two@example.com");

        Assert.Equal(2, await host.TimesheetsAsync());

        Assert.True(failing.Attempts >= 2);
        Assert.Single(await host.NotificationsAsync(Dev, ReminderKinds.Timesheet)); // the bell has it even though email is down
        Assert.Equal(0, await host.TimesheetsAsync());                              // and it isn't retried into a flood
    }

    [Fact]
    public async Task Teams_gets_the_reminder_only_for_people_who_chose_it_and_a_down_channel_changes_nothing_else()
    {
        await using var host = new Host(Thursday);
        var chosen = await host.EmployeeAsync(Dev);
        var emailOnly = await host.EmployeeAsync("email@example.com");
        await host.SetAsync(chosen.Email, p => p.ViaTeams = true);
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<ChatChannel>().Add(new ChatChannel
            {
                Kind = ChatChannelKinds.Teams, Host = "contoso.webhook.office.com", UpdatedBy = Dev,
                WebhookUrlProtected = host.Get<Workbench.Application.Integrations.IntegrationSecrets>().ProtectChatUrl("https://contoso.webhook.office.com/webhookb2/secret-path/IncomingWebhook/abcd"),
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await host.TimesheetsAsync());
        await host.Get<ChatNotificationSender>().WaitIdleAsync();

        var posted = Assert.Single(host.Transport.Sent);
        Assert.Contains("Your timesheet is due tomorrow", posted.Body);
        Assert.Equal(2, host.Emails.Sent.Count);                                   // email stays on for both
        Assert.Single(await host.NotificationsAsync(emailOnly.Email, ReminderKinds.Timesheet));

        // Teams down on the next day's reminder: the run still succeeds and email and the bell still work.
        host.Transport.Answer(FakeTransport.Unreachable);
        Assert.Equal(2, await host.TimesheetsAsync(Friday));
        await host.Get<ChatNotificationSender>().WaitIdleAsync();
        Assert.Equal(4, host.Emails.Sent.Count);
    }

    // ---- Waiting on you ----

    [Fact]
    public async Task Approvers_get_one_short_message_a_day_with_the_count_and_the_oldest_wait_and_nothing_when_nothing_waits()
    {
        await using var host = new Host(Wednesday);
        Assert.Equal(0, await host.DigestAsync());                    // nothing waiting: no message

        var now = Wednesday.UtcDateTime;
        await host.WaitingApprovalAsync(Pat, now.AddDays(-3));
        await host.WaitingApprovalAsync(Pat, now.AddHours(-5), "Bill 9");
        await host.WaitingApprovalAsync("other@example.com", now.AddHours(-2), "Bill 10");

        Assert.Equal(2, await host.DigestAsync());
        Assert.Equal(0, await host.DigestAsync());                    // once a day, even if the job runs again

        var mine = Assert.Single(await host.NotificationsAsync(Pat, ReminderKinds.Approvals));
        Assert.Equal("2 approvals are waiting on you", mine.Title);
        Assert.Equal("The oldest has been waiting 3 days.", mine.Body);
        Assert.Equal("approvals", mine.Link);
        Assert.Equal("1 approval is waiting on you", Assert.Single(await host.NotificationsAsync("other@example.com")).Title);
        Assert.Contains(host.Emails.Sent, m => m.To == Pat && m.TextBody.Contains("approvals"));

        Assert.Equal(2, await host.DigestAsync(Thursday));            // and again the next business day
    }

    [Fact]
    public async Task Delegates_hear_about_what_they_cover_and_people_who_delegated_everything_away_are_left_alone()
    {
        await using var host = new Host(Wednesday);
        await host.WaitingApprovalAsync(Pat, Wednesday.UtcDateTime.AddDays(-1));
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<ApprovalDelegation>().Add(new ApprovalDelegation
            {
                DelegatorEmail = Pat, DelegateEmail = "deputy@example.com", StartsOn = new DateOnly(2026, 10, 12), EndsOn = new DateOnly(2026, 10, 30), CreatedBy = Pat,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await host.DigestAsync());

        Assert.Empty(await host.NotificationsAsync(Pat));
        Assert.Equal("The oldest has been waiting 1 day.", Assert.Single(await host.NotificationsAsync("deputy@example.com", ReminderKinds.Approvals)).Body);
    }

    [Fact]
    public async Task The_digest_respects_weekends_leave_quiet_hours_and_the_persons_choice()
    {
        await using var host = new Host(Saturday);
        await host.WaitingApprovalAsync(Pat, Saturday.UtcDateTime.AddDays(-2));
        Assert.Equal(0, await host.DigestAsync());                    // weekend

        await host.SetAsync(Pat, p => (p.QuietStartHour, p.QuietEndHour) = (9, 17));
        Assert.Equal(0, await host.DigestAsync(Monday));              // quiet hours at 10:00
        Assert.Equal(1, await host.DigestAsync(Monday.AddHours(8)));  // 18:00 is fine

        await using var off = new Host(Wednesday);
        await off.WaitingApprovalAsync(Pat, Wednesday.UtcDateTime.AddDays(-1));
        await off.SetAsync(Pat, p => p.ApprovalsEnabled = false);
        Assert.Equal(0, await off.DigestAsync());
    }

    // ---- Per-person controls ----

    [Fact]
    public async Task Reminder_settings_default_to_on_by_email_only_and_are_saved_and_validated_per_person()
    {
        await using var host = new Host(Wednesday);
        var settings = host.Get<IReminderSettingsService>();

        var defaults = await settings.GetMineAsync();
        Assert.Equal(new ReminderSettingsDto(true, true, true, false, null, null, false), defaults);

        var saved = await settings.SetMineAsync(new SaveReminderSettingsRequest(false, true, true, true, 18, 9));
        Assert.Equal(new ReminderSettingsDto(false, true, true, true, 18, 9, false), saved);

        host.User.Email = "other@example.com";
        Assert.True((await settings.GetMineAsync()).TimesheetEnabled);                        // someone else's choices are untouched
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetMineAsync(new SaveReminderSettingsRequest(true, true, true, false, 25, 9)));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetMineAsync(new SaveReminderSettingsRequest(true, true, true, false, 18, null)));
    }

    [Fact]
    public async Task Reminder_settings_round_trip_over_the_api_for_the_signed_in_person()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var person = web.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, "analyst@example.com");
        var client = new HttpReminderSettingsService(person);

        Assert.Equal(new ReminderSettingsDto(true, true, true, false, null, null, false), await client.GetMineAsync());
        var saved = await client.SetMineAsync(new SaveReminderSettingsRequest(true, false, true, false, 20, 7));

        Assert.Equal(new ReminderSettingsDto(true, false, true, false, 20, 7, false), saved);
        Assert.Equal(saved, await client.GetMineAsync());
        var bad = await person.PutAsJsonAsync("api/v1/me/reminders", new SaveReminderSettingsRequest(true, true, true, false, 30, 7));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public void Quiet_hours_can_span_midnight()
    {
        var overnight = new ReminderPreference { UserEmail = "a@example.com", QuietStartHour = 18, QuietEndHour = 9 };

        Assert.True(ReminderGate.InQuietHours(overnight, 22));
        Assert.True(ReminderGate.InQuietHours(overnight, 3));
        Assert.False(ReminderGate.InQuietHours(overnight, 9));
        Assert.False(ReminderGate.InQuietHours(overnight, 12));
        Assert.False(ReminderGate.InQuietHours(new ReminderPreference { UserEmail = "a@example.com" }, 3));
    }

    // ---- UI ----

    private sealed class FakeSettings : IReminderSettingsService
    {
        public ReminderSettingsDto State { get; set; } = new(true, true, true, false, null, null, true);
        public SaveReminderSettingsRequest? Last { get; private set; }

        public Task<ReminderSettingsDto> GetMineAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task<ReminderSettingsDto> SetMineAsync(SaveReminderSettingsRequest r, CancellationToken cancellationToken = default)
        {
            Last = r;
            return Task.FromResult(State = new(r.TimesheetEnabled, r.ApprovalsEnabled, r.ViaEmail, r.ViaTeams, r.QuietStartHour, r.QuietEndHour, State.TeamsAvailable));
        }
    }

    [Fact]
    public async Task The_notifications_page_card_turns_reminders_on_and_off_picks_teams_and_sets_quiet_hours()
    {
        var settings = new FakeSettings();
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IReminderSettingsService>(settings);

        var card = ctx.Render<ReminderSettingsCard>();
        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='reminder-settings']")));
        Assert.Empty(card.FindAll("[data-testid='reminder-quiet-start']"));

        card.Find("input[data-testid='reminder-timesheet'], [data-testid='reminder-timesheet'] input").Change(false);
        card.WaitForAssertion(() => Assert.False(settings.Last!.TimesheetEnabled));

        card.Find("input[data-testid='reminder-teams'], [data-testid='reminder-teams'] input").Change(true);
        card.WaitForAssertion(() => Assert.True(settings.Last!.ViaTeams));

        card.Find("input[data-testid='reminder-quiet'], [data-testid='reminder-quiet'] input").Change(true);
        card.WaitForAssertion(() => Assert.Equal((18, 9), (settings.Last!.QuietStartHour!.Value, settings.Last.QuietEndHour!.Value)));
        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='reminder-quiet-start']")));
    }

    [Fact]
    public async Task The_card_hides_the_teams_choice_until_a_channel_is_connected()
    {
        var settings = new FakeSettings { State = new(true, true, true, false, null, null, false) };
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IReminderSettingsService>(settings);

        var card = ctx.Render<ReminderSettingsCard>();

        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='reminder-email']")));
        Assert.Empty(card.FindAll("[data-testid='reminder-teams']"));
    }

    private sealed class ThrowingMailer : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("mail server is down");
        }
    }
}
