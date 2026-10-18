using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Assets;

/// <summary>Read models for the register and asset detail, shared by the asset and report services.</summary>
internal static class AssetQueries
{
    public static async Task<IReadOnlyList<AssetSummaryDto>> ListAsync(IWorkbenchDbContext db, AssetQuery query, CancellationToken ct)
    {
        var q = db.Set<FixedAsset>().AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(a => a.Status == s);
        }

        if (query.CategoryId is { } categoryId)
        {
            q = q.Where(a => a.CategoryId == categoryId);
        }

        if (query.GovernmentPropertyOnly)
        {
            q = q.Where(a => a.IsGovernmentFurnished || a.IsContractorAcquired);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a => a.TagNumber.Contains(term) || a.Description.Contains(term) || (a.SerialNumber != null && a.SerialNumber.Contains(term))
                || (a.GovernmentPropertyNumber != null && a.GovernmentPropertyNumber.Contains(term)));
        }

        var assets = (await q.ToListAsync(ct)).OrderBy(a => a.TagNumber, StringComparer.Ordinal).ToList();
        return await SummariesAsync(db, assets, ct);
    }

    public static async Task<IReadOnlyList<AssetSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<FixedAsset> assets, CancellationToken ct)
    {
        var names = await Names.LoadAsync(db, assets, ct);
        return assets.Select(a => Summary(a, names)).ToList();
    }

    private static AssetSummaryDto Summary(FixedAsset a, Names n) => new(
        a.Id, a.TagNumber, a.Description, n.Categories.GetValueOrDefault(a.CategoryId)?.Code ?? "", a.AcquisitionDate, a.Cost, a.AccumulatedDepreciation,
        a.Status == AssetStatus.InService ? a.NetBookValue : 0, a.Method.ToContract(), a.UsefulLifeYears, a.Status.ToContract(), a.Location,
        n.Employee(a.CustodianEmployeeId), n.Org(a.OrgUnitId), n.Project(a.ProjectId), a.IsGovernmentFurnished, a.IsContractorAcquired, a.GovernmentPropertyNumber);

    public static async Task<AssetDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var a = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Asset", id);
        var transfers = await db.Set<AssetTransfer>().AsNoTracking().Where(t => t.AssetId == id).OrderBy(t => t.TransferredOn).ThenBy(t => t.CreatedAtUtc).ToListAsync(ct);
        var names = await Names.LoadAsync(db, [a], ct, transfers);
        var depreciation = await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.AssetId == id)
            .Join(db.Set<FiscalPeriod>(), d => d.FiscalPeriodId, p => p.Id, (d, p) => new { d, p.Name }).ToListAsync(ct);
        var accountIds = new[] { a.AssetAccountId, a.AccumulatedDepreciationAccountId, a.ExpenseAccountId };
        var accounts = await db.Set<Account>().AsNoTracking().Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var wbs = a.WbsElementId is { } w ? await db.Set<WbsElement>().AsNoTracking().Where(x => x.Id == w).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
        var contract = a.ContractId is { } c ? await db.Set<Contract>().AsNoTracking().Where(x => x.Id == c).Select(x => x.ContractNumber).FirstOrDefaultAsync(ct) : null;
        var vendor = a.VendorId is { } v ? await db.Set<Vendor>().AsNoTracking().Where(x => x.Id == v).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var bill = a.SourceBillLineId is { } bl
            ? await db.Set<ApBillLine>().AsNoTracking().Where(l => l.Id == bl).Join(db.Set<ApBill>(), l => l.BillId, b => b.Id, (l, b) => b.Number + " line " + l.LineNumber).FirstOrDefaultAsync(ct)
            : null;
        return new AssetDto(
            Summary(a, names), a.CategoryId, a.InServiceDate, a.SalvageValue, a.MacrsRecoveryYears, a.SerialNumber, a.CustodianEmployeeId,
            a.AssetAccountId, accounts.GetValueOrDefault(a.AssetAccountId) ?? "", a.AccumulatedDepreciationAccountId, accounts.GetValueOrDefault(a.AccumulatedDepreciationAccountId) ?? "",
            a.ExpenseAccountId, accounts.GetValueOrDefault(a.ExpenseAccountId) ?? "",
            a.OrgUnitId, a.ProjectId, a.WbsElementId, wbs, a.ContractId, contract, a.VendorId, vendor, a.SourceBillLineId, bill, a.CapitalizationEntryId,
            a.DisposedOn, a.DisposalProceeds, a.DisposalReason, a.DisposalEntryId,
            depreciation.OrderBy(x => x.d.PeriodStart).Select(x => new AssetDepreciationDto(x.d.FiscalPeriodId, x.Name, x.d.PeriodStart, x.d.Amount)).ToList(),
            transfers.Select(t => new AssetTransferDto(t.TransferredOn, t.FromLocation, t.ToLocation, names.Employee(t.FromCustodianEmployeeId), names.Employee(t.ToCustodianEmployeeId),
                names.Org(t.FromOrgUnitId), names.Org(t.ToOrgUnitId), names.Project(t.FromProjectId), names.Project(t.ToProjectId), t.Reason, t.TransferredBy)).ToList(),
            a.CreatedBy, a.CreatedAtUtc);
    }

    public static async Task<IReadOnlyList<AssetCategoryDto>> CategoriesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var categories = await db.Set<AssetCategory>().AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);
        var ids = categories.SelectMany(c => new[] { c.AssetAccountId, c.AccumulatedDepreciationAccountId, c.ExpenseAccountId }).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        return categories.Select(c => new AssetCategoryDto(c.Id, c.Code, c.Name, c.DefaultMethod.ToContract(), c.DefaultLifeYears, c.MacrsRecoveryYears,
            c.AssetAccountId, accounts.GetValueOrDefault(c.AssetAccountId) ?? "", c.AccumulatedDepreciationAccountId, accounts.GetValueOrDefault(c.AccumulatedDepreciationAccountId) ?? "",
            c.ExpenseAccountId, accounts.GetValueOrDefault(c.ExpenseAccountId) ?? "", c.IsActive)).ToList();
    }

    /// <summary>Display names for an asset's references.</summary>
    internal sealed class Names
    {
        public Dictionary<Guid, AssetCategory> Categories { get; private init; } = [];
        public Dictionary<Guid, string> Employees { get; private init; } = [];
        public Dictionary<Guid, string> Orgs { get; private init; } = [];
        public Dictionary<Guid, string> Projects { get; private init; } = [];
        public Dictionary<Guid, string> Contracts { get; private init; } = [];

        public static async Task<Names> LoadAsync(IWorkbenchDbContext db, IReadOnlyList<FixedAsset> assets, CancellationToken ct, IReadOnlyList<AssetTransfer>? transfers = null)
        {
            transfers ??= [];
            var employees = assets.Select(a => a.CustodianEmployeeId).Concat(transfers.SelectMany(t => new[] { t.FromCustodianEmployeeId, t.ToCustodianEmployeeId }))
                .Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var orgs = assets.Select(a => a.OrgUnitId).Concat(transfers.SelectMany(t => new[] { t.FromOrgUnitId, t.ToOrgUnitId }))
                .Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var projects = assets.Select(a => a.ProjectId).Concat(transfers.SelectMany(t => new[] { t.FromProjectId, t.ToProjectId }))
                .Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var contracts = assets.Where(a => a.ContractId != null).Select(a => a.ContractId!.Value).Distinct().ToList();
            return new Names
            {
                Categories = await db.Set<AssetCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
                Employees = await db.Set<Employee>().AsNoTracking().Where(e => employees.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct),
                Orgs = await db.Set<OrgUnit>().AsNoTracking().Where(o => orgs.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Code, ct),
                Projects = await db.Set<Project>().AsNoTracking().Where(p => projects.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct),
                Contracts = await db.Set<Contract>().AsNoTracking().Where(c => contracts.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct),
            };
        }

        public string? Employee(Guid? id) => id is { } i ? Employees.GetValueOrDefault(i) : null;
        public string? Org(Guid? id) => id is { } i ? Orgs.GetValueOrDefault(i) : null;
        public string? Project(Guid? id) => id is { } i ? Projects.GetValueOrDefault(i) : null;
        public string? Contract(Guid? id) => id is { } i ? Contracts.GetValueOrDefault(i) : null;
    }
}
