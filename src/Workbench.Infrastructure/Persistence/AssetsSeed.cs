using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Assets;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for fixed assets: three contractor-owned assets placed in service at the start of the current
/// period (one per book method) with that period's depreciation posted, and a government-furnished spectrum analyzer on
/// the seeded contract for the property listing. Runs once, after the procurement seed.
/// </summary>
internal static class AssetsSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<FixedAsset>().AnyAsync(cancellationToken) || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        var ops = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        var eng = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "ENG", cancellationToken);
        var contract = await db.Set<Contract>().AsNoTracking().OrderBy(c => c.ContractNumber).FirstOrDefaultAsync(cancellationToken);
        var custodian = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == "dev@workbench.local", cancellationToken);
        if (period is null || ops is null || eng is null || contract is null)
        {
            return;
        }

        var user = new SeedUser { Email = "pm@example.com" };
        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var clock = TimeProvider.System;
        var audit = new AuditRecorder(user, clock);
        var ledger = new GeneralLedger(audit, clock);
        var assets = new AssetService(factory, audit, ledger, Options.Create(new AssetOptions()), clock);
        var depreciation = new DepreciationService(factory, audit, ledger, clock);

        var categories = (await assets.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Code);
        if (!categories.ContainsKey("COMP") || !categories.ContainsKey("FURN") || !categories.ContainsKey("LHI"))
        {
            return;
        }

        var start = period.Start;
        await assets.CreateAsync(new SaveAssetRequest("Conference room AV system", categories["FURN"].Id, start, 18_500m, 500m, 7, AssetDepreciationMethod.StraightLine,
            SerialNumber: "AV-55031", Location: "HQ conference room A", OrgUnitId: ops.Id), cancellationToken);
        await assets.CreateAsync(new SaveAssetRequest("Engineering file server", categories["COMP"].Id, start, 24_000m, 2_000m, 3, AssetDepreciationMethod.DecliningBalance,
            SerialNumber: "SRV-8812-A", Location: "HQ server room", CustodianEmployeeId: custodian?.Id, OrgUnitId: eng.Id), cancellationToken);
        await assets.CreateAsync(new SaveAssetRequest("Integration lab build-out", categories["LHI"].Id, start, 60_000m, 0, 10, AssetDepreciationMethod.SumOfYearsDigits,
            Location: "HQ suite 410", OrgUnitId: eng.Id), cancellationToken);
        await assets.CreateAsync(new SaveAssetRequest("Spectrum analyzer (government-furnished)", categories["COMP"].Id, start.AddYears(-1), 42_750m,
            SerialNumber: "N9020B-MY5544", Location: "Integration lab", CustodianEmployeeId: custodian?.Id, OrgUnitId: eng.Id,
            IsGovernmentFurnished: true, ContractId: contract.Id, GovernmentPropertyNumber: "W56KGU-GFP-0042"), cancellationToken);

        await depreciation.RunAsync(period.Id, cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }
}
