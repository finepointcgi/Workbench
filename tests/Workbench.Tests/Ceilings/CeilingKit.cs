using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Ceilings;

/// <summary>
/// The real registrations (decorators included) over <see cref="ProjectAccountingFixture"/>: analyst@ may charge 1001.01
/// (8 approved hours at $87.50 on record = $700), there's a vendor ODC ($500, draft, entered by finance@) and vendor ACME.
/// </summary>
internal sealed class CeilingKit : IAsyncDisposable
{
    public static readonly DateOnly WorkDay = new(2026, 10, 6);

    private CeilingKit(SeededHost seeded, ProjectAccountingFixture fx, Guid vendorId, Guid categoryId)
    {
        Seeded = seeded;
        Fx = fx;
        VendorId = vendorId;
        CategoryId = categoryId;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public Guid VendorId { get; }
    public Guid CategoryId { get; }

    public ICeilingService Ceilings => Seeded.Host.Get<ICeilingService>();
    public ICloseoutService Closeouts => Seeded.Host.Get<ICloseoutService>();
    public ITimesheetService Timesheets => Seeded.Host.Get<ITimesheetService>();
    public IOdcService Odcs => Seeded.Host.Get<IOdcService>();
    public IExpenseReportService Expenses => Seeded.Host.Get<IExpenseReportService>();
    public IApBillService Bills => Seeded.Host.Get<IApBillService>();
    public IPurchaseOrderService Orders => Seeded.Host.Get<IPurchaseOrderService>();
    public IWorkbenchDbContextFactory Factory => Seeded.Host.Get<IWorkbenchDbContextFactory>();

    public static async Task<CeilingKit> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var category = await db.Set<LaborCategory>().FirstAsync();
        db.Set<ChargeAssignment>().Add(new ChargeAssignment { EmployeeId = fx.AnalystId, WbsElementId = fx.Wbs1 });
        var vendor = new Vendor { Code = "ACME", Name = "Acme Supply" };
        db.Set<Vendor>().Add(vendor);
        await db.SaveChangesAsync();
        return new CeilingKit(seeded, fx, vendor.Id, category.Id);
    }

    public CeilingKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public async Task<CeilingDto> CeilingAsync(SaveCeilingRequest request)
    {
        var who = Seeded.Host.User.Email;
        As(Finance);
        try
        {
            return await Ceilings.CreateAsync(request);
        }
        finally
        {
            As(who);
        }
    }

    public Task<CeilingDto> WbsHoursAsync(decimal hours, CeilingMode mode = CeilingMode.Block, bool pmMayOverride = false) =>
        CeilingAsync(new SaveCeilingRequest(CeilingScope.Wbs, WbsElementId: Fx.Wbs1, HoursCeiling: hours, Mode: mode, ProjectManagerMayOverride: pmMayOverride));

    public Task<CeilingDto> ProjectCostAsync(decimal cost, CeilingMode mode = CeilingMode.Block) =>
        CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: Fx.Project1, CostCeiling: cost, Mode: mode));

    /// <summary>analyst@ saves <paramref name="hours"/> on 1001.01 on <see cref="WorkDay"/>.</summary>
    public async Task<TimesheetDetailDto> SaveTimeAsync(decimal hours)
    {
        As(Editor);
        var sheet = await Timesheets.GetOrCreateMyTimesheetAsync(WorkDay);
        return await Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(Fx.Wbs1, WorkDay, hours)]));
    }

    /// <summary>Closes project 1001 (as Finance), waiving what the fixture leaves open.</summary>
    public async Task CloseProjectAsync()
    {
        As(Finance);
        await Closeouts.MarkPhysicallyCompleteAsync(CloseoutScope.Project, Fx.Project1, new PhysicallyCompleteRequest(new DateOnly(2026, 10, 1)));
        await Closeouts.StartClosingAsync(CloseoutScope.Project, Fx.Project1);
        await Closeouts.WaiveAsync(CloseoutScope.Project, Fx.Project1, new WaiveCloseoutItemRequest(CloseoutCheck.AllCostsPosted, "Draft ODCs are being voided"));
        await Closeouts.SaveRecordsAsync(CloseoutScope.Project, Fx.Project1, new SaveCloseoutRecordsRequest(RecordsRetentionUntil: new DateOnly(2031, 12, 31)));
        await Closeouts.CloseAsync(CloseoutScope.Project, Fx.Project1);
    }

    public async Task ExpireOverridesAsync()
    {
        await using var db = await Factory.CreateAsync();
        foreach (var o in await db.Set<CeilingOverride>().ToListAsync())
        {
            o.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        }

        await db.SaveChangesAsync();
    }

    public async Task<List<string>> NotifiedAsync(string kind)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Workbench.Domain.Notifications.Notification>().Where(n => n.Kind == kind).Select(n => n.RecipientEmail).ToListAsync();
    }

    public SaveBillRequest Bill(decimal amount, string invoice = "A-1") =>
        new(VendorId, invoice, WorkDay, [new SaveBillLineRequest(BillLineKind.ProjectCost, "Cables", amount, Fx.Wbs1, OdcCostType.Materials)]);

    public SavePurchaseOrderRequest Order(decimal amount) =>
        new(VendorId, WorkDay, [new SavePurchaseOrderLineRequest("Laptops", 1, amount, Fx.Wbs1, OdcCostType.Equipment)]);

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}
