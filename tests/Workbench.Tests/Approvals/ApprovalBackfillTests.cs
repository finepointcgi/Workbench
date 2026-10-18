using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Time;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Time;

namespace Workbench.Tests.Approvals;

/// <summary>Documents pending from before the workflow engine (seeded, imported, upgraded) reach the inbox.</summary>
public sealed class ApprovalBackfillTests
{
    /// <summary>Dana's submitted timesheet written straight to the database, as a pre-upgrade or seeded one would be.</summary>
    private static async Task<Guid> LegacyTimesheetAsync(TimeTestHost host)
    {
        await using var db = await host.Factory.CreateAsync();
        var sheet = new Timesheet
        {
            EmployeeId = host.Dana.Id, PeriodStart = TimeTestHost.Monday, PeriodEnd = TimeTestHost.Monday.AddDays(6),
            Status = TimesheetStatus.Submitted, CertifiedAtUtc = host.Clock.UtcNow, SubmittedAtUtc = host.Clock.UtcNow, FirstSubmittedAtUtc = host.Clock.UtcNow,
            CreatedAtUtc = host.Clock.UtcNow, UpdatedAtUtc = host.Clock.UtcNow,
        };
        sheet.Entries.Add(new TimeEntry { WbsElementId = host.Direct.Id, LaborCategoryId = (await db.Set<LaborCategory>().FirstAsync()).Id, WorkDate = TimeTestHost.Monday, Hours = 8m, EnteredAtUtc = host.Clock.UtcNow, UpdatedAtUtc = host.Clock.UtcNow });
        db.Set<Timesheet>().Add(sheet);
        await db.SaveChangesAsync();
        return sheet.Id;
    }

    private static ApprovalInboxService Inbox(TimeTestHost host)
    {
        var services = new ServiceCollection().AddSingleton<ITimesheetApprovalService>(host.Approvals).BuildServiceProvider();
        var engine = new ApprovalEngine(host.Clock);
        return new ApprovalInboxService(host.Factory, engine, new AccessControl(host.Factory, host.User), [new TimesheetApprovalHandler(services)],
            new ApprovalBackfill(host.Factory, engine));
    }

    private static async Task<int> InstancesAsync(TimeTestHost host, Guid documentId)
    {
        await using var db = await host.Factory.CreateAsync();
        return await db.Set<ApprovalInstance>().CountAsync(i => i.DocumentId == documentId);
    }

    [Fact]
    public async Task A_document_submitted_without_the_engine_shows_in_the_inbox_and_is_decided_from_it()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await LegacyTimesheetAsync(host);
        var inbox = Inbox(host);

        host.SignInAs(host.Pat);
        Assert.Equal(1, await inbox.CountMineAsync());
        var item = Assert.Single(await inbox.ListMineAsync());
        Assert.Equal(id, item.DocumentId);
        Assert.Equal(1, await InstancesAsync(host, id));

        await inbox.DecideAsync(ApprovalDocumentTypes.Timesheet, id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        Assert.Equal(TimesheetState.Approved, (await host.Timesheets.GetAsync(id)).Timesheet.Status);
        Assert.Empty(await inbox.ListMineAsync());
        Assert.Equal(1, await InstancesAsync(host, id)); // Not routed again once decided.
    }

    [Fact]
    public async Task Running_the_backfill_twice_creates_no_duplicates_and_sends_nothing()
    {
        var host = await TimeTestHost.CreateAsync();
        var legacy = await LegacyTimesheetAsync(host);
        var backfill = new ApprovalBackfill(host.Factory, new ApprovalEngine(host.Clock));

        Assert.Equal(1, await backfill.RunAsync());
        Assert.Equal(0, await backfill.RunAsync());
        await Task.WhenAll(backfill.RunAsync(), backfill.RunAsync());
        Assert.Equal(1, await InstancesAsync(host, legacy));
        Assert.Empty(host.Notifier.Sent);

        // A document already routed by the engine is left alone.
        host.SignInAs(host.Sam);
        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
        await host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(host.Overhead.Id, TimeTestHost.Monday, 8m)]));
        await host.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        Assert.Equal(0, await backfill.RunAsync());
        Assert.Equal(1, await InstancesAsync(host, sheet.Timesheet.Id));
    }

    [Fact]
    public async Task Approvals_given_under_the_old_rules_carry_over()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.GrantAsync("fin@example.com", OrgRoles.Finance);
        var project = (await host.Projects.ListProjectsAsync()).Single(p => p.Code == "1001");
        var req = new PurchaseRequisition
        {
            Number = "REQ-0001", RequesterEmail = "dana@example.com", RequesterEmployeeId = host.Dana.Id, Justification = "Lab servers",
            Status = RequisitionStatus.PendingApproval, Stage = RequisitionStage.Finance, NeedsFinance = true, Total = 20_000m,
            CreatedAtUtc = host.Clock.UtcNow, SubmittedAtUtc = host.Clock.UtcNow,
        };
        req.Lines.Add(new PurchaseRequisitionLine
        {
            LineNumber = 1, Description = "Servers", Quantity = 2, UnitCost = 10_000m, Amount = 20_000m, ProjectId = project.Id,
            ApproverEmail = "pat@example.com", ApprovedBy = "pat@example.com", ApprovedAtUtc = host.Clock.UtcNow,
        });
        await using (var db = await host.Factory.CreateAsync())
        {
            db.Set<PurchaseRequisition>().Add(req);
            await db.SaveChangesAsync();
        }

        var engine = new ApprovalEngine(host.Clock);
        Assert.Equal(1, await new ApprovalBackfill(host.Factory, engine).RunAsync());
        await using var read = await host.Factory.CreateAsync();
        Assert.Empty(await engine.ActionableAsync(read, ApprovalDocumentTypes.Requisition, "pat@example.com", default));
        Assert.Single(await engine.ActionableAsync(read, ApprovalDocumentTypes.Requisition, "fin@example.com", default));
        var pm = await read.Set<ApprovalTask>().SingleAsync(t => t.StepName == "Project managers");
        Assert.Equal(ApprovalTaskStatus.Approved, pm.Status);
        Assert.Equal("pat@example.com", pm.DecidedBy);
    }
}
