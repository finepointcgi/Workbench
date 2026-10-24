using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.GitLab;
using Workbench.Application.Notifications;
using Workbench.Contracts.Integrations.GitLab;
using Workbench.Domain;
using Workbench.Domain.Integrations.GitLab;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Scheduling;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations.GitLab;

/// <summary>
/// A seeded Workbench (project 1001 Radar with a plan: Design › Requirements, Mockups; Build › Code; three people staffed)
/// connected to a <see cref="FakeGitLab"/>. Admin is signed in.
/// </summary>
internal sealed class GitLabKit : IAsyncDisposable
{
    public const string BaseUrl = "https://gitlab.example.com";
    public const string Jordan = "jlee@example.com";
    public const string Sam = "sam.park@example.com";
    public const string Riley = "riley@example.com";

    private GitLabKit(IntegrationHost host, FakeGitLab gitLab) => (Host, GitLab) = (host, gitLab);

    public IntegrationHost Host { get; }
    public FakeGitLab GitLab { get; }
    public Guid ProjectId { get; private set; }
    public Guid SheetId { get; private set; }
    public Dictionary<string, Guid> Tasks { get; } = [];
    public Guid StatusColumn { get; private set; }
    public Guid PercentColumn { get; private set; }
    public Guid NotesColumn { get; private set; }

    public IGitLabProjectSyncService Sync => Host.Get<IGitLabProjectSyncService>();
    public IGitLabConnectionService Connection => Host.Get<IGitLabConnectionService>();
    public SettableClock Clock => Host.Clock;

    public static async Task<GitLabKit> CreateAsync(bool epics = true, bool connect = true, Action<GitLabOptions>? gitlab = null, Action<IServiceCollection>? configure = null)
    {
        SettableClock? clock = null;
        var fake = new FakeGitLab(() => clock!.Now.UtcDateTime) { EpicsAvailable = epics };
        var host = await IntegrationHost.CreateAsync(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGitLabHandlerFactory>(fake));
            services.Replace(ServiceDescriptor.Singleton(new NotificationOptions { PublicBaseUrl = "https://wb.example.com" }));
            services.Configure<GitLabOptions>(o => gitlab?.Invoke(o));
            configure?.Invoke(services);
        });
        clock = host.Clock;
        var kit = new GitLabKit(host, fake);
        await kit.SeedAsync();
        if (connect)
        {
            var saved = await kit.Connection.SaveAsync(new SaveGitLabConnectionRequest(BaseUrl, FakeGitLab.Token, null));
            Assert.True(saved.LastTestOk, saved.LastTestMessage);
        }

        return kit;
    }

    private async Task SeedAsync()
    {
        GitLab.AddUser(202, "jlee", "Jordan Lee", email: Jordan);
        GitLab.AddUser(203, "sam.park", "Sam Park");
        GitLab.AddUser(204, "casey", "Casey Outside");

        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var sheet = new Sheet { WorkspaceId = Host.Seeded.ProgramOfficeId, Name = "1001 Radar", SchedulingEnabled = true };
        db.Sheets.Add(sheet);
        var status = new Column { SheetId = sheet.Id, Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Done"] };
        var percent = new Column { SheetId = sheet.Id, Name = "% Complete", Type = ColumnType.Number, Order = 1 };
        var notes = new Column { SheetId = sheet.Id, Name = "Notes", Type = ColumnType.Text, Order = 2 };
        db.Columns.AddRange(status, percent, notes);
        (StatusColumn, PercentColumn, NotesColumn) = (status.Id, percent.Id, notes.Id);

        Item Task(string title, string code, int order, Item? parent = null, DateOnly? start = null, DateOnly? finish = null)
        {
            var item = new Item
            {
                SheetId = sheet.Id, Title = title, CostpointWbsId = code, ParentId = parent?.Id, Order = order, Start = start, Finish = finish,
                CreatedAtUtc = Clock.Now.UtcDateTime.AddDays(-7), ModifiedAtUtc = Clock.Now.UtcDateTime.AddDays(-7),
            };
            db.Items.Add(item);
            Tasks[title] = item.Id;
            return item;
        }

        var design = Task("Design", "1001.01", 0, start: new DateOnly(2026, 11, 2), finish: new DateOnly(2026, 11, 13));
        var requirements = Task("Requirements", "1001.01.01", 0, design, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6));
        requirements.Values = new Dictionary<string, string?> { [notes.Id.ToString()] = "Gather the radar requirements." };
        var mockups = Task("Mockups", "1001.01.02", 1, design, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 13));
        var build = Task("Build", "1001.02", 1, start: new DateOnly(2026, 11, 16), finish: new DateOnly(2026, 11, 27));
        var code = Task("Code", "1001.02.01", 0, build, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 27));
        db.Set<Dependency>().Add(new Dependency { SheetId = sheet.Id, PredecessorId = requirements.Id, SuccessorId = mockups.Id });

        var project = new Project { Code = "1001", Name = "Radar", SheetId = sheet.Id };
        db.Set<Project>().Add(project);
        var elements = new Dictionary<string, WbsElement>();
        foreach (var item in new[] { design, requirements, mockups, build, code })
        {
            var element = new WbsElement { ProjectId = project.Id, Code = item.CostpointWbsId!, Name = item.Title, ItemId = item.Id };
            elements[item.Title] = element;
            db.Set<WbsElement>().Add(element);
        }

        Employee Person(string number, string email, string name)
        {
            var e = new Employee { EmployeeNumber = number, Email = email, DisplayName = name, HireDate = new DateOnly(2020, 1, 1) };
            db.Set<Employee>().Add(e);
            return e;
        }

        var jordan = Person("E1", Jordan, "Jordan Lee");
        var sam = Person("E2", Sam, "Sam Park");
        var riley = Person("E3", Riley, "Riley Quinn");
        void Staff(Employee e, string task, decimal hours) => db.Set<StaffingAssignment>().Add(new StaffingAssignment
        {
            EmployeeId = e.Id, ProjectId = project.Id, WbsElementId = elements[task].Id, From = new DateOnly(2026, 11, 2), To = new DateOnly(2026, 11, 27),
            TotalHours = hours, CreatedBy = SeededHost.Admin,
        });
        Staff(jordan, "Requirements", 40);
        Staff(sam, "Mockups", 24);
        Staff(riley, "Code", 16);
        await db.SaveChangesAsync();
        ProjectId = project.Id;
        SheetId = sheet.Id;
    }

    public async Task<GitLabProjectSyncDto> LinkAsync(GitLabPhaseChoice phases = GitLabPhaseChoice.Auto, int levels = 1)
    {
        await Sync.LinkAsync(ProjectId, new LinkGitLabProjectRequest(FakeGitLab.ProjectPath, phases, levels));
        await DispatchAsync();
        return await Sync.GetAsync(ProjectId);
    }

    /// <summary>Runs the outbox dispatcher until nothing is due.</summary>
    public async Task DispatchAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var result = await Host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);
            if (result.EventsFannedOut == 0 && result.Attempts == 0)
            {
                return;
            }
        }
    }

    public async Task<Guid> LinkIdAsync()
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<GitLabProjectLink>().Where(l => l.ProjectId == ProjectId).Select(l => l.Id).SingleAsync();
    }

    public async Task<Item> ItemAsync(string title)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Items.AsNoTracking().SingleAsync(i => i.Id == Tasks[title]);
    }

    public async Task<GitLabItemLink> ItemLinkAsync(string title)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<GitLabItemLink>().AsNoTracking().SingleAsync(l => l.ItemId == Tasks[title]);
    }

    public async Task<FakeIssue> IssueAsync(string title) => GitLab.Issue((await ItemLinkAsync(title)).Iid);

    /// <summary>Changes a task in Workbench directly (as an edit would) and raises its change.</summary>
    public async Task EditAsync(string title, Action<Item> change, bool raise = true)
    {
        await using (var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var item = await db.Items.SingleAsync(i => i.Id == Tasks[title]);
            change(item);
            item.Values = new Dictionary<string, string?>(item.Values);
            item.ModifiedAtUtc = Clock.Now.UtcDateTime;
            item.ModifiedBy = SeededHost.Owner;
            await db.SaveChangesAsync();
        }

        if (!raise)
        {
            return;
        }

        await Host.Get<GitLabChangeListener>().OnChangeAsync(new Contracts.Collaboration.SheetChange(SheetId, Contracts.Collaboration.SheetChangeKind.ItemUpdated,
            Tasks[title], null, SeededHost.Owner, Clock.Now.UtcDateTime));
    }

    /// <summary>GitLab calls the webhook for an issue (after a person changed it there).</summary>
    public async Task<GitLabWebhookOutcome> IssueHookAsync(FakeIssue issue, string? idempotencyKey = null, string? token = null)
    {
        var secret = token ?? (await Sync.GetWebhookSetupAsync(ProjectId)).SecretToken;
        var body = FakeGitLab.Payload("issue-hook.json", ("object_attributes.id", issue.Id), ("object_attributes.iid", issue.Iid), ("object_attributes.title", issue.Title));
        return await Host.Get<GitLabWebhookReceiver>().ReceiveAsync(await LinkIdAsync(), secret, idempotencyKey ?? Guid.NewGuid().ToString(), null, body, CancellationToken.None);
    }

    public string? Percent(Item item) => item.Values.GetValueOrDefault(PercentColumn.ToString());

    public string? Status(Item item) => item.Values.GetValueOrDefault(StatusColumn.ToString());

    public async Task<IReadOnlyList<string>> ActivityAsync(string title)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<Domain.Collaboration.ActivityEvent>().AsNoTracking().Where(a => a.ItemId == Tasks[title])
            .OrderBy(a => a.OccurredAtUtc).Select(a => a.Summary).ToListAsync();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
