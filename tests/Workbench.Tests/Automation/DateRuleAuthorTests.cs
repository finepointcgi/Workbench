using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workbench.Application;
using Workbench.Application.Automation;
using Workbench.Application.Automations;
using Workbench.Contracts.Automations;
using Workbench.Domain.Automations;
using Workbench.Application.Identity;
using Workbench.Domain;
using Workbench.Domain.Automation;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Identity;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Automation;

/// <summary>
/// The hourly date-trigger job in the real web host, where nobody is signed in: each rule acts as its author, checked
/// when it runs, and is skipped (and the reason recorded) when the author is deactivated or can no longer edit.
/// The rules are stored the old way (the Rules table) and moved into sheet automations by the data migration first, so these
/// also show migrated date rules fire as they did. Seeded workspace: pm@ Owner, analyst@ Editor, viewer@ Viewer.
/// </summary>
public sealed class DateRuleAuthorTests
{
    [Fact]
    public async Task A_date_rule_fires_on_its_date_and_acts_as_its_author()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var (scopes, job) = Started(factory);
        var (sheetId, itemId) = await DueItemAsync(scopes);
        var ruleId = await DateRuleAsync(scopes, sheetId, "Flag due", "Analyst@Example.com");

        await job.RunOnceAsync(CancellationToken.None);

        var item = await QueryAsync(scopes, db => db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId));
        Assert.Equal("Due today [due]", item.Title);
        var run = await QueryAsync(scopes, db => db.Set<AutomationRun>().AsNoTracking().SingleAsync(r => r.AutomationId == ruleId && r.RecordId == itemId));
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), run.Status);
        Assert.Equal($"Finish is {DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime):yyyy-MM-dd}", run.TriggerDetail);
        Assert.Equal("analyst@example.com", run.Principal);

        // The change is recorded as the automation, naming the rule and its author.
        var actor = await QueryAsync(scopes, db => db.Set<ActivityEvent>().AsNoTracking().Where(a => a.ItemId == itemId).Select(a => a.Actor).SingleAsync());
        Assert.Equal("Workbench Automation (rule \"Flag due\", author analyst@example.com)", actor);

        // Once per item and date.
        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rule_whose_author_cant_edit_is_skipped_and_the_reason_recorded_once_a_day()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var (scopes, job) = Started(factory);
        var (sheetId, itemId) = await DueItemAsync(scopes);
        var viewerRule = await DateRuleAsync(scopes, sheetId, "Viewer's rule", "viewer@example.com");
        var outsiderRule = await DateRuleAsync(scopes, sheetId, "Outsider's rule", "someone@elsewhere.example");

        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None));

        Assert.Equal("Due today", (await QueryAsync(scopes, db => db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId))).Title);
        var runs = await QueryAsync(scopes, db => db.Set<AutomationRun>().AsNoTracking().ToListAsync());
        foreach (var (ruleId, author) in new[] { (viewerRule, "viewer@example.com"), (outsiderRule, "someone@elsewhere.example") })
        {
            var skipped = Assert.Single(runs, r => r.AutomationId == ruleId);
            Assert.Equal(nameof(AutomationRunStatus.Skipped), skipped.Status);
            Assert.Null(skipped.RecordId);
            Assert.Equal($"The automation's author, {author}, no longer has edit access to this sheet.", skipped.Error);
        }

        Assert.Empty(await QueryAsync(scopes, db => db.Set<ActivityEvent>().AsNoTracking().Where(a => a.ItemId == itemId).ToListAsync()));
    }

    [Fact]
    public async Task A_deactivated_authors_rules_are_skipped_even_though_their_membership_remains()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var (scopes, job) = Started(factory);
        var (sheetId, itemId) = await DueItemAsync(scopes);
        var ruleId = await DateRuleAsync(scopes, sheetId, "Flag due", "analyst@example.com");
        await QueryAsync(scopes, db => db.Set<AppUser>().Where(u => u.Email == "analyst@example.com")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false)));

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Due today", (await QueryAsync(scopes, db => db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId))).Title);
        var run = await QueryAsync(scopes, db => db.Set<AutomationRun>().AsNoTracking().SingleAsync(r => r.AutomationId == ruleId));
        Assert.Equal(nameof(AutomationRunStatus.Skipped), run.Status);
        Assert.Equal("The automation's author, analyst@example.com, is deactivated.", run.Error);
    }

    [Fact]
    public async Task The_author_context_is_per_scope_grants_only_the_authors_roles_and_cant_be_entered_in_a_request()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var (scopes, _) = Started(factory);
        var (sheetId, _) = await DueItemAsync(scopes);

        await using (var viewer = RuleAuthorExecution.CreateScope(scopes, "viewer@example.com"))
        {
            var access = viewer.ServiceProvider.GetRequiredService<AccessControl>();
            Assert.Equal("viewer@example.com", access.Email);
            Assert.False(access.IsSystemReader);
            Assert.False(await access.HasAsync(Workbench.Contracts.Identity.Permission.AdminAllWorkspaces));
            Assert.Equal(Workbench.Contracts.Identity.WorkspaceRole.Viewer, await access.GetSheetRoleAsync(sheetId));
        }

        await using (var other = scopes.CreateAsyncScope())
        {
            Assert.False(other.ServiceProvider.GetRequiredService<RuleAuthorExecutionContext>().IsActive);
            Assert.False(other.ServiceProvider.GetRequiredService<AccessControl>().IsAuthenticated);
        }

        Assert.Throws<ArgumentException>(() => RuleAuthorExecution.CreateScope(scopes, "Workbench System"));

        var http = factory.Services.GetRequiredService<IHttpContextAccessor>();
        http.HttpContext = new DefaultHttpContext();
        try
        {
            Assert.Throws<InvalidOperationException>(() => RuleAuthorExecution.CreateScope(scopes, "pm@example.com"));
        }
        finally
        {
            http.HttpContext = null;
        }
    }

    private static (IServiceScopeFactory Scopes, DateTriggerBackgroundService Job) Started(WorkbenchWebFactory factory)
    {
        _ = factory.Server; // Starts the host, which creates and seeds the development database.
        var job = factory.Services.GetServices<IHostedService>().OfType<DateTriggerBackgroundService>().Single();
        return (factory.Services.GetRequiredService<IServiceScopeFactory>(), job);
    }

    /// <summary>A new item finishing today on a sheet in the seeded workspace.</summary>
    private static Task<(Guid SheetId, Guid ItemId)> DueItemAsync(IServiceScopeFactory scopes) => QueryAsync(scopes, async db =>
    {
        var sheetId = await db.Sheets.AsNoTracking().OrderBy(s => s.Name).Select(s => s.Id).FirstAsync();
        var item = new Item { SheetId = sheetId, Title = "Due today", Order = 10_000, Finish = DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime) };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return (sheetId, item.Id);
    });

    private static Task<Guid> DateRuleAsync(IServiceScopeFactory scopes, Guid sheetId, string name, string author) => QueryAsync(scopes, async db =>
    {
        var rule = new Rule
        {
            SheetId = sheetId,
            Name = name,
            CreatedBy = author,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Trigger = new RuleTrigger { Kind = RuleTriggerKind.DateReached, Field = RuleFields.Finish },
            Actions = [new RuleAction { Kind = RuleActionKind.SetValue, Field = RuleFields.Title, Value = "{Title} [due]" }],
        };
        db.Set<Rule>().Add(rule);
        await db.SaveChangesAsync();
        Assert.Equal(1, await SheetRuleMigration.MigrateAsync(db));
        return rule.Id;
    });

    private static async Task<T> QueryAsync<T>(IServiceScopeFactory scopes, Func<IWorkbenchDbContext, Task<T>> query)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        return await query(db);
    }
}
