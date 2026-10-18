using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Security;
using Workbench.Domain;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Security;

/// <summary>Records retention never deletes protected or held records, and purges only what has expired.</summary>
public sealed class RetentionTests
{
    [Fact]
    public async Task Purge_deletes_only_expired_purgeable_records_and_never_held_or_protected_ones()
    {
        await using var seeded = await CreateAsync();
        var old = DateTime.UtcNow.AddYears(-6);
        Guid heldSheet, freeSheet, heldProject;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var customer = new Customer { Code = "C1", Name = "Agency" };
            var contract = new Contract { CustomerId = customer.Id, ContractNumber = "W91-1", Name = "Old contract", Status = ContractStatus.Closed, PeriodStart = new DateOnly(2018, 1, 1), PeriodEnd = new DateOnly(2019, 12, 31) };
            var held = new Project { Code = "P-HELD", Name = "Held", ContractId = contract.Id, SheetId = seeded.SheetId };
            var free = new Project { Code = "P-FREE", Name = "Free", ContractId = contract.Id, SheetId = seeded.FinanceSheetId };
            var wbs = new WbsElement { ProjectId = free.Id, Code = "P-FREE.1", Name = "Work" };
            var employee = new Employee { EmployeeNumber = "E1", Email = Viewer, DisplayName = "Val" };
            var sheet = new Timesheet { EmployeeId = employee.Id, PeriodStart = new DateOnly(2019, 1, 7), PeriodEnd = new DateOnly(2019, 1, 13) };
            db.Set<Customer>().Add(customer);
            db.Set<Contract>().Add(contract);
            db.Set<Project>().AddRange(held, free);
            db.Set<WbsElement>().Add(wbs);
            db.Set<Employee>().Add(employee);
            db.Set<Timesheet>().Add(sheet);
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, WorkDate = new DateOnly(2019, 1, 7), Hours = 8 });
            db.Set<AuditEvent>().Add(new AuditEvent { EntityType = "Contract", EntityId = contract.Id, Action = "Created", Actor = Admin, OccurredAtUtc = old });
            db.Set<SecurityEvent>().AddRange(
                new SecurityEvent { EventType = "signin.success", Actor = Viewer, Outcome = "success", OccurredAtUtc = old },
                new SecurityEvent { EventType = "signin.success", Actor = Viewer, Outcome = "success", OccurredAtUtc = DateTime.UtcNow.AddDays(-10) });
            db.Set<Notification>().AddRange(
                new Notification { RecipientEmail = Viewer, Kind = "x", Title = "Old, held project", SheetId = seeded.SheetId, CreatedAtUtc = old },
                new Notification { RecipientEmail = Viewer, Kind = "x", Title = "Old", SheetId = seeded.FinanceSheetId, CreatedAtUtc = old },
                new Notification { RecipientEmail = Viewer, Kind = "x", Title = "New", CreatedAtUtc = DateTime.UtcNow });
            db.Set<UserInvite>().AddRange(
                new UserInvite { Email = "gone@example.com", DisplayName = "Gone", InvitedBy = Admin, CreatedAtUtc = old, ExpiresAtUtc = old.AddDays(7) },
                new UserInvite { Email = "joined@example.com", DisplayName = "Joined", InvitedBy = Admin, CreatedAtUtc = old, ExpiresAtUtc = old.AddDays(7), AcceptedAtUtc = old.AddDays(1) });
            await db.SaveChangesAsync();
            (heldSheet, freeSheet, heldProject) = (seeded.SheetId, seeded.FinanceSheetId, held.Id);
        }

        var retention = seeded.As(Admin).Host.Get<IRetentionService>();
        await retention.PlaceHoldAsync(new PlaceLegalHoldRequest("Project", heldProject, "Litigation hold, matter 2026-01"));

        // Even the shortest allowed periods never delete protected records.
        await Assert.ThrowsAsync<ArgumentException>(() => retention.SetPolicyAsync(RetentionRecordTypes.TimeRecords, 30));
        await Assert.ThrowsAsync<ArgumentException>(() => retention.SetPolicyAsync(RetentionRecordTypes.SecurityEvents, 10));
        await retention.SetPolicyAsync(RetentionRecordTypes.SecurityEvents, 90);

        var preview = await retention.PreviewAsync();
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Equal(2, await db.Set<SecurityEvent>().CountAsync(e => e.EventType == "signin.success"));
        }

        var report = await retention.RunNowAsync();
        var lines = report.Lines.ToDictionary(l => l.RecordType);

        Assert.True(preview.DryRun);
        Assert.Equal(1, preview.Lines.Single(l => l.RecordType == RetentionRecordTypes.SecurityEvents).Eligible);
        Assert.Equal(1, lines[RetentionRecordTypes.SecurityEvents].Purged);
        Assert.Equal(1, lines[RetentionRecordTypes.Notifications].Purged);
        Assert.Equal(1, lines[RetentionRecordTypes.Notifications].Held);
        Assert.Equal(1, lines[RetentionRecordTypes.ExpiredInvites].Purged);
        Assert.Equal(1, lines[RetentionRecordTypes.TimeRecords].Eligible);
        Assert.Equal(0, lines[RetentionRecordTypes.TimeRecords].Purged);
        Assert.Equal(1, lines[RetentionRecordTypes.Audit].Eligible);

        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Equal(1, await db.Set<SecurityEvent>().CountAsync(e => e.EventType == "signin.success"));
            Assert.Equal(["New", "Old, held project"], (await db.Set<Notification>().Select(n => n.Title).ToListAsync()).Order());
            Assert.Equal(["joined@example.com"], await db.Set<UserInvite>().Select(i => i.Email).ToListAsync());
            Assert.Equal(1, await db.Set<TimeEntry>().CountAsync());
            Assert.Equal(1, await db.Set<AuditEvent>().CountAsync());
            Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.RetentionRun && e.Actor == Admin));
            Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.SettingChanged && e.Target == "Retention:SecurityEvents"));
            Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.LegalHoldPlaced));
        }

        _ = (heldSheet, freeSheet);
    }

    [Fact]
    public async Task Legal_holds_block_deletion_and_cover_the_contracts_projects()
    {
        await using var seeded = await CreateAsync();
        Guid contractId, projectId;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var customer = new Customer { Code = "C1", Name = "Agency" };
            var contract = new Contract { CustomerId = customer.Id, ContractNumber = "W91-2", Name = "Contract", PeriodStart = new DateOnly(2024, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31) };
            var project = new Project { Code = "P1", Name = "Project", ContractId = contract.Id };
            db.Set<Customer>().Add(customer);
            db.Set<Contract>().Add(contract);
            db.Set<Project>().Add(project);
            await db.SaveChangesAsync();
            (contractId, projectId) = (contract.Id, project.Id);
        }

        var retention = seeded.As(Admin).Host.Get<IRetentionService>();
        Assert.False(await retention.IsOnHoldAsync("Project", projectId));
        await Assert.ThrowsAsync<ArgumentException>(() => retention.PlaceHoldAsync(new PlaceLegalHoldRequest("Contract", contractId, " ")));

        var hold = await retention.PlaceHoldAsync(new PlaceLegalHoldRequest("Contract", contractId, "DCAA audit"));
        Assert.True(await retention.IsOnHoldAsync("Contract", contractId));
        Assert.True(await retention.IsOnHoldAsync("Project", projectId));
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var blocked = await Assert.ThrowsAsync<ArgumentException>(() => RetentionService.EnsureNotHeldAsync(db, "Project", projectId, CancellationToken.None));
            Assert.Contains("legal hold", blocked.Message);
        }

        await retention.ReleaseHoldAsync(hold.Id, "Audit closed");
        Assert.False(await retention.IsOnHoldAsync("Project", projectId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<IRetentionService>().PlaceHoldAsync(new PlaceLegalHoldRequest("Contract", contractId, "x")));
    }
}
