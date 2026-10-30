using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Jira;
using Workbench.Application.Notifications;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Domain;
using Workbench.Domain.Integrations.Jira;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations.Jira;

/// <summary>
/// A seeded Workbench (project 2001 Radar with a plan that has a Status column, an Estimate column and one task, "Existing task";
/// two people) connected to a <see cref="FakeJira"/>. Admin is signed in.
/// </summary>
internal sealed class JiraKit : IAsyncDisposable
{
    public const string SiteUrl = "https://acme.atlassian.net";
    public const string Jordan = "jlee@example.com";
    public const string Sam = "sam.park@example.com";

    private JiraKit(IntegrationHost host, FakeJira jira) => (Host, Jira) = (host, jira);

    public IntegrationHost Host { get; }
    public FakeJira Jira { get; }
    public Guid ProjectId { get; private set; }
    public Guid SheetId { get; private set; }
    public Guid StatusColumn { get; private set; }
    public Guid EstimateColumn { get; private set; }
    public Guid ExistingTask { get; private set; }

    public IJiraProjectSyncService Sync => Host.Get<IJiraProjectSyncService>();
    public IJiraConnectionService Connection => Host.Get<IJiraConnectionService>();
    public JiraSyncEngine Engine => Host.Get<JiraSyncEngine>();
    public SettableClock Clock => Host.Clock;

    public static async Task<JiraKit> CreateAsync(bool connect = true, Action<IServiceCollection>? configure = null)
    {
        SettableClock? clock = null;
        var fake = new FakeJira(() => clock!.Now.UtcDateTime);
        var host = await IntegrationHost.CreateAsync(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IJiraHandlerFactory>(fake));
            services.Replace(ServiceDescriptor.Singleton(new NotificationOptions { PublicBaseUrl = "https://wb.example.com" }));
            configure?.Invoke(services);
        });
        clock = host.Clock;
        var kit = new JiraKit(host, fake);
        await kit.SeedAsync();
        if (connect)
        {
            var saved = await kit.Connection.SaveAsync(new SaveJiraConnectionRequest(SiteUrl, FakeJira.Email, FakeJira.Token));
            Assert.True(saved.LastTestOk, saved.LastTestMessage);
        }

        return kit;
    }

    private async Task SeedAsync()
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var sheet = new Sheet { WorkspaceId = Host.Seeded.ProgramOfficeId, Name = "2001 Radar", SchedulingEnabled = true };
        db.Sheets.Add(sheet);
        var status = new Column { SheetId = sheet.Id, Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Done"] };
        var estimate = new Column { SheetId = sheet.Id, Name = "Estimate (hours)", Type = ColumnType.Number, Order = 1 };
        db.Columns.AddRange(status, estimate);
        (StatusColumn, EstimateColumn) = (status.Id, estimate.Id);
        var existing = new Item
        {
            SheetId = sheet.Id, Title = "Existing task", CostpointWbsId = "2001.01", Order = 0, Start = new DateOnly(2026, 11, 2), Finish = new DateOnly(2026, 11, 6),
            CreatedAtUtc = Clock.Now.UtcDateTime.AddDays(-7), ModifiedAtUtc = Clock.Now.UtcDateTime.AddDays(-7),
        };
        db.Items.Add(existing);
        ExistingTask = existing.Id;
        var project = new Project { Code = "2001", Name = "Radar", SheetId = sheet.Id };
        db.Set<Project>().Add(project);
        db.Set<Employee>().Add(new Employee { EmployeeNumber = "E1", Email = Jordan, DisplayName = "Jordan Lee", HireDate = new DateOnly(2020, 1, 1) });
        db.Set<Employee>().Add(new Employee { EmployeeNumber = "E2", Email = Sam, DisplayName = "Sam Park", HireDate = new DateOnly(2020, 1, 1) });
        await db.SaveChangesAsync();
        ProjectId = project.Id;
        SheetId = sheet.Id;
    }

    public async Task<JiraProjectSyncDto> LinkAsync(string? epic = null, string? jql = null, JiraDateWinnerChoice winner = JiraDateWinnerChoice.Workbench, bool creates = false)
    {
        await Sync.LinkAsync(ProjectId, new LinkJiraProjectRequest(FakeJira.ProjectKey, epic, jql, winner, creates));
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
        return await db.Set<JiraProjectLink>().Where(l => l.ProjectId == ProjectId).Select(l => l.Id).SingleAsync();
    }

    public async Task<List<Item>> ItemsAsync()
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Items.AsNoTracking().Where(i => i.SheetId == SheetId).OrderBy(i => i.Order).ToListAsync();
    }

    public async Task<Item> ItemAsync(string title) => (await ItemsAsync()).Single(i => i.Title == title);

    public async Task<JiraItemLink> ItemLinkAsync(Guid itemId)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<JiraItemLink>().AsNoTracking().SingleAsync(l => l.ItemId == itemId);
    }

    public async Task<List<string>> AssigneesAsync(Guid itemId)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<ItemAssignee>().AsNoTracking().Where(a => a.ItemId == itemId).Select(a => a.Email).OrderBy(e => e).ToListAsync();
    }

    public string? Status(Item item) => item.Values.GetValueOrDefault(StatusColumn.ToString());

    /// <summary>Changes a task in Workbench directly (as an edit would) and raises its change, then runs the outbox.</summary>
    public async Task EditAsync(Guid itemId, Action<Item> change)
    {
        await using (var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var item = await db.Items.SingleAsync(i => i.Id == itemId);
            change(item);
            item.Values = new Dictionary<string, string?>(item.Values);
            item.ModifiedAtUtc = Clock.Now.UtcDateTime;
            item.ModifiedBy = SeededHost.Owner;
            await db.SaveChangesAsync();
        }

        await Host.Get<JiraChangeListener>().OnChangeAsync(new Contracts.Collaboration.SheetChange(SheetId, Contracts.Collaboration.SheetChangeKind.ItemUpdated, itemId, null, SeededHost.Owner, Clock.Now.UtcDateTime));
        await DispatchAsync();
    }

    /// <summary>Adds a task as the plan's Add row would, and raises its change.</summary>
    public async Task<Guid> AddTaskAsync(string title, DateOnly? finish = null)
    {
        Guid id;
        await using (var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var order = await db.Items.Where(i => i.SheetId == SheetId).MaxAsync(i => i.Order) + 1;
            var item = new Item { SheetId = SheetId, Title = title, Order = order, Finish = finish, Start = finish, CreatedAtUtc = Clock.Now.UtcDateTime, CreatedBy = SeededHost.Owner, ModifiedAtUtc = Clock.Now.UtcDateTime };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            id = item.Id;
        }

        await Host.Get<JiraChangeListener>().OnChangeAsync(new Contracts.Collaboration.SheetChange(SheetId, Contracts.Collaboration.SheetChangeKind.ItemCreated, id, null, SeededHost.Owner, Clock.Now.UtcDateTime));
        await DispatchAsync();
        return id;
    }

    public async Task<JiraWebhookOutcome> HookAsync(FakeJiraIssue issue, string eventName = "jira:issue_updated", string? identifier = null, bool sign = true, string? badSecret = null)
    {
        var secret = (await Sync.GetWebhookSetupAsync(ProjectId)).Secret;
        var body = System.Text.Json.JsonSerializer.Serialize(new { webhookEvent = eventName, issue = new { id = issue.Id, key = issue.Key, fields = new { project = new { key = issue.ProjectKey } } } });
        var signature = sign ? JiraWebhookReceiver.Sign(badSecret ?? secret, body) : null;
        return await Host.Get<JiraWebhookReceiver>().ReceiveAsync(await LinkIdAsync(), signature, null, identifier ?? Guid.NewGuid().ToString(), body, CancellationToken.None);
    }

    public async Task<IReadOnlyList<string>> ActivityAsync(Guid itemId)
    {
        await using var db = await Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<Domain.Collaboration.ActivityEvent>().AsNoTracking().Where(a => a.ItemId == itemId).OrderBy(a => a.OccurredAtUtc).Select(a => a.Summary).ToListAsync();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
