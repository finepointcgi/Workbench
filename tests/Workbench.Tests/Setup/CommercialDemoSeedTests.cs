using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests.Setup;

/// <summary>The commercial demo dataset (<c>Demo:Dataset=Commercial</c>): seeds once, is consistent, and the dataset name is validated.</summary>
public sealed class CommercialDemoSeedTests
{
    private static ServiceProvider Host(string? dataset)
    {
        var services = new ServiceCollection();
                var connection = TestDb.NewConnectionString(DatabaseProvider.Sqlite);
        services.AddDbContextFactory<WorkbenchDbContext>(o => o.Use(DatabaseProvider.Sqlite, connection));
        services.AddSingleton<IWorkbenchDbContextFactory>(sp => new WorkbenchDbContextFactory(sp.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>()));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(dataset is null ? [] : [new("Demo:Dataset", dataset)]).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_commercial_dataset_is_a_consistent_professional_services_firm_and_seeds_only_once()
    {
        await using var host = Host("Commercial");
        await host.SeedSampleDataAsync();
        await host.SeedSampleDataAsync(); // Idempotent.

        await using var db = await host.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync();
        Assert.Equal(["6001", "6002", "6003", "IND"], (await db.Set<Project>().Select(p => p.Code).ToListAsync()).Order());
        Assert.Equal(3, await db.Set<Contract>().CountAsync());
        Assert.DoesNotContain(await db.Set<Contract>().Select(c => c.ContractNumber).ToListAsync(), n => n.StartsWith("W56"));
        Assert.Equal(3, await db.Set<Customer>().CountAsync(c => c.Category == "Commercial"));

        // Every person has four weeks of timesheets whose hours add up to their standard week; the pending ones are drafts.
        var employees = await db.Set<Employee>().ToListAsync();
        var sheets = await db.Set<Timesheet>().ToListAsync();
        var entries = await db.Set<TimeEntry>().ToListAsync();
        Assert.Equal(employees.Count * 4, sheets.Count);
        foreach (var sheet in sheets)
        {
            var person = employees.Single(e => e.Id == sheet.EmployeeId);
            Assert.Equal(person.StandardHoursPerWeek, entries.Where(e => e.TimesheetId == sheet.Id).Sum(e => e.Hours));
        }

        Assert.Equal(2, sheets.Count(s => s.Status == TimesheetStatus.Draft));

        // Every time entry charges a leaf the person may charge (indirect codes need no assignment).
        var wbs = (await db.Set<WbsElement>().ToListAsync()).ToDictionary(w => w.Id);
        var assignments = (await db.Set<ChargeAssignment>().ToListAsync()).Select(a => (a.EmployeeId, a.WbsElementId)).ToHashSet();
        foreach (var entry in entries)
        {
            var element = wbs[entry.WbsElementId];
            Assert.True(element.IsChargeable, element.Code);
            var owner = sheets.Single(s => s.Id == entry.TimesheetId).EmployeeId;
            Assert.True(!element.RequiresAssignment || assignments.Contains((owner, element.Id)), $"{element.Code} charged without an assignment");
        }

        // Invoices: a part-paid T&M invoice, a paid retainer month, a sent fixed-fee milestone and a draft.
        var invoices = await db.Set<Invoice>().OrderBy(i => i.Number).ToListAsync();
        Assert.Equal([InvoiceStatus.PartiallyPaid, InvoiceStatus.Draft, InvoiceStatus.Sent, InvoiceStatus.Paid], invoices.Select(i => i.Status));
        Assert.All(invoices, i => Assert.True(i.Total > 0));
    }

    [Fact]
    public void The_government_data_is_the_default_and_a_typo_is_refused()
    {
        var typo = new ConfigurationBuilder().AddInMemoryCollection([new("Demo:Dataset", "Comercial")]).Build();
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateDataset(typo));
        Assert.False(DemoMode.IsCommercial(new ConfigurationBuilder().Build()));
        Assert.True(DemoMode.IsCommercial(new ConfigurationBuilder().AddInMemoryCollection([new("Demo:Dataset", "commercial")]).Build()));
    }
}
