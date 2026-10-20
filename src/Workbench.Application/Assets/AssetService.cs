using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Purchasing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Assets;

/// <summary>
/// The asset register: manual entry, capitalization from posted AP bill lines (with a reclass entry when the line was
/// expensed), transfers with history, and disposals with a gain/loss entry. Every change is audited.
/// </summary>
public sealed class AssetService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    GeneralLedger ledger,
    IOptions<AssetOptions> options,
    TimeProvider clock) : IAssetService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<AssetSummaryDto>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await AssetQueries.ListAsync(db, query, cancellationToken);
    }

    public async Task<AssetDto> GetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, assetId, cancellationToken);
    }

    public Task<AssetSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AssetSettingsDto(options.Value.CapitalizationThreshold, options.Value.GainLossAccountNumber, options.Value.CashAccountNumber));

    public async Task<AssetDto> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await AssetSupport.EnsureSetupAsync(db, UtcNow, cancellationToken);
        var asset = new FixedAsset { TagNumber = "", Description = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, asset, request, isNew: true, cancellationToken);
        db.Set<FixedAsset>().Add(asset);
        audit.Record(db, AssetSupport.AssetEntity, asset.Id, "Created", after: Snapshot(asset));
        await db.SaveChangesAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, asset.Id, cancellationToken);
    }

    public async Task<AssetDto> UpdateAsync(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await AssetSupport.FindAsync(db, assetId, cancellationToken);
        if (asset.Status != AssetStatus.InService)
        {
            throw new InvalidOperationException($"{asset.TagNumber} is {asset.Status} and can't be changed.");
        }

        var before = Snapshot(asset);
        await ApplyAsync(db, asset, request, isNew: false, cancellationToken);
        audit.Record(db, AssetSupport.AssetEntity, asset.Id, "Updated", before, Snapshot(asset));
        await db.SaveChangesAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, asset.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<CapitalizableBillLineDto>> ListCapitalizableBillLinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var threshold = await LowestThresholdAsync(db, cancellationToken);
        var capitalized = (await db.Set<FixedAsset>().AsNoTracking().Where(a => a.SourceBillLineId != null).Select(a => a.SourceBillLineId!.Value).ToListAsync(cancellationToken)).ToHashSet();
        var rows = await db.Set<ApBillLine>().AsNoTracking()
            .Where(l => l.Kind == ApBillLineKind.Account)
            .Join(db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted), l => l.BillId, b => b.Id, (l, b) => new { l, b })
            .ToListAsync(cancellationToken);
        rows = rows.Where(r => r.l.Amount >= threshold && !capitalized.Contains(r.l.Id)).OrderByDescending(r => r.b.InvoiceDate).ThenBy(r => r.b.Number).ThenBy(r => r.l.LineNumber).ToList();
        var accountIds = rows.Select(r => r.l.AccountId!.Value).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var vendorIds = rows.Select(r => r.b.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        var orgIds = rows.Where(r => r.l.OrgUnitId != null).Select(r => r.l.OrgUnitId!.Value).Distinct().ToList();
        var orgs = await db.Set<OrgUnit>().AsNoTracking().Where(o => orgIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Code, cancellationToken);
        return rows.Select(r => new CapitalizableBillLineDto(r.l.Id, r.b.Id, r.b.Number, vendors.GetValueOrDefault(r.b.VendorId) ?? "", r.b.InvoiceDate, r.l.LineNumber,
            r.l.Description, r.l.Amount, accounts[r.l.AccountId!.Value].Number, accounts[r.l.AccountId!.Value].Name,
            r.l.OrgUnitId is { } o ? orgs.GetValueOrDefault(o) : null)).ToList();
    }

    public async Task<AssetDto> CapitalizeAsync(CapitalizeBillLineRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await AssetSupport.EnsureSetupAsync(db, UtcNow, cancellationToken);
        var line = await db.Set<ApBillLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.BillLineId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Bill line", request.BillLineId);
        var bill = await db.Set<ApBill>().AsNoTracking().FirstAsync(b => b.Id == line.BillId, cancellationToken);
        if (line.Kind != ApBillLineKind.Account)
        {
            throw new ArgumentException("Only GL-account bill lines can be capitalized; project costs belong to the project.");
        }

        if (bill.Status != ApBillStatus.Posted)
        {
            throw new ArgumentException($"Bill {bill.Number} is {bill.Status}; capitalize lines once the bill is posted.");
        }

        var category = await db.Set<AssetCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Asset category", request.CategoryId);
        var threshold = category.CapitalizationThreshold ?? options.Value.CapitalizationThreshold;
        if (line.Amount < threshold)
        {
            throw new ArgumentException($"Line {line.LineNumber} is {line.Amount:N2}, under the {threshold:N2} capitalization threshold for {category.Code}.");
        }

        if (await db.Set<FixedAsset>().AnyAsync(a => a.SourceBillLineId == line.Id, cancellationToken))
        {
            throw new InvalidOperationException($"Line {line.LineNumber} of {bill.Number} is already capitalized.");
        }

        var asset = new FixedAsset { TagNumber = "", Description = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow, SourceBillLineId = line.Id };
        await ApplyAsync(db, asset, new SaveAssetRequest(
            request.Description ?? line.Description, request.CategoryId, bill.InvoiceDate, line.Amount, request.SalvageValue, request.UsefulLifeYears, request.Method,
            SerialNumber: request.SerialNumber, Location: request.Location, CustodianEmployeeId: request.CustodianEmployeeId,
            OrgUnitId: line.OrgUnitId, ProjectId: request.ProjectId, WbsElementId: request.WbsElementId, VendorId: bill.VendorId), isNew: true, cancellationToken);
        db.Set<FixedAsset>().Add(asset);

        // The bill debited the line's account; when that isn't the asset account, move the cost there.
        if (line.AccountId != asset.AssetAccountId)
        {
            var date = await DateInOpenPeriodAsync(db, bill.InvoiceDate, cancellationToken);
            var entry = NewEntry(date, JournalSource.Asset, asset.Id, $"Capitalize {bill.Number} line {line.LineNumber} as asset {asset.TagNumber}");
            entry.CompanyId = asset.CompanyId;
            AddLine(entry, asset.AssetAccountId, asset.Cost, 0, $"{asset.TagNumber} {asset.Description}", line.OrgUnitId);
            AddLine(entry, line.AccountId!.Value, 0, asset.Cost, $"Reclass to asset {asset.TagNumber}", line.OrgUnitId);
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, cancellationToken);
            asset.CapitalizationEntryId = entry.Id;
        }

        audit.Record(db, AssetSupport.AssetEntity, asset.Id, "Capitalized", after: new { Bill = bill.Number, line.LineNumber, asset.Cost, asset.CapitalizationEntryId });
        await db.SaveChangesAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, asset.Id, cancellationToken);
    }

    public async Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await AssetSupport.FindAsync(db, assetId, cancellationToken);
        if (asset.Status != AssetStatus.InService)
        {
            throw new InvalidOperationException($"{asset.TagNumber} is {asset.Status}.");
        }

        if (request.TransferOut && !asset.IsGovernmentProperty)
        {
            throw new ArgumentException("Only government property is transferred out; dispose of contractor-owned assets instead.");
        }

        if (request.TransferredOn < asset.AcquisitionDate)
        {
            throw new ArgumentException("The transfer date is before the acquisition date.");
        }

        var location = PurchasingSupport.Optional(request.Location, "Location", 200);
        await EnsureExistsAsync<Employee>(db, request.CustodianEmployeeId, "Custodian", cancellationToken);
        await EnsureExistsAsync<OrgUnit>(db, request.OrgUnitId, "Organization", cancellationToken);
        await EnsureExistsAsync<Project>(db, request.ProjectId, "Project", cancellationToken);
        await EnsureExistsAsync<Contract>(db, request.ContractId, "Contract", cancellationToken);
        var toCompany = request.CompanyId ?? asset.CompanyId;
        if (toCompany != asset.CompanyId)
        {
            var company = await db.Set<Company>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == toCompany, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Company", toCompany);
            if (!company.IsActive)
            {
                throw new ArgumentException($"Company {company.Code} is inactive.");
            }
        }

        var transfer = new AssetTransfer
        {
            AssetId = asset.Id,
            TransferredOn = request.TransferredOn,
            FromLocation = asset.Location,
            ToLocation = location,
            FromCustodianEmployeeId = asset.CustodianEmployeeId,
            ToCustodianEmployeeId = request.CustodianEmployeeId,
            FromOrgUnitId = asset.OrgUnitId,
            ToOrgUnitId = request.OrgUnitId,
            FromProjectId = asset.ProjectId,
            ToProjectId = request.ProjectId,
            FromContractId = asset.ContractId,
            ToContractId = request.ContractId ?? asset.ContractId,
            FromCompanyId = asset.CompanyId,
            ToCompanyId = toCompany,
            Reason = PurchasingSupport.Optional(request.Reason, "Reason", 1000),
            TransferredBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        if (transfer.FromLocation == transfer.ToLocation && transfer.FromCustodianEmployeeId == transfer.ToCustodianEmployeeId
            && transfer.FromOrgUnitId == transfer.ToOrgUnitId && transfer.FromProjectId == transfer.ToProjectId
            && transfer.FromContractId == transfer.ToContractId && transfer.FromCompanyId == transfer.ToCompanyId && !request.TransferOut)
        {
            throw new ArgumentException("The transfer doesn't change the location, custodian, organization, project, contract or company.");
        }

        var companyChanged = transfer.FromCompanyId != transfer.ToCompanyId;
        if (!asset.IsGovernmentProperty && (companyChanged || transfer.FromOrgUnitId != transfer.ToOrgUnitId))
        {
            transfer.JournalEntryId = await PostTransferAsync(db, asset, transfer, companyChanged, cancellationToken);
        }

        if (asset.ProjectId != request.ProjectId)
        {
            asset.WbsElementId = null;
        }

        asset.Location = location;
        asset.CustodianEmployeeId = request.CustodianEmployeeId;
        asset.OrgUnitId = request.OrgUnitId;
        asset.ProjectId = request.ProjectId;
        asset.ContractId = transfer.ToContractId;
        asset.CompanyId = toCompany;
        if (request.TransferOut)
        {
            asset.Status = AssetStatus.Transferred;
            asset.DisposedOn = request.TransferredOn;
            asset.DisposalReason = transfer.Reason;
        }

        db.Set<AssetTransfer>().Add(transfer);
        audit.Record(db, AssetSupport.AssetEntity, asset.Id, request.TransferOut ? "TransferredOut" : "Transferred",
            new { transfer.FromLocation, transfer.FromCustodianEmployeeId, transfer.FromOrgUnitId, transfer.FromProjectId, transfer.FromContractId, transfer.FromCompanyId },
            new { transfer.ToLocation, transfer.ToCustodianEmployeeId, transfer.ToOrgUnitId, transfer.ToProjectId, transfer.ToContractId, transfer.ToCompanyId, transfer.JournalEntryId }, transfer.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, asset.Id, cancellationToken);
    }

    /// <summary>
    /// Moves cost and accumulated depreciation from the old organization (and company) to the new one in one entry dated the
    /// transfer date. Across companies the ledger adds the intercompany due to/due from for the net book value, so each
    /// company balances. Depreciation must be posted through the month before a company move, so each company keeps its own months.
    /// </summary>
    private async Task<Guid?> PostTransferAsync(IWorkbenchDbContext db, FixedAsset asset, AssetTransfer t, bool companyChanged, CancellationToken ct)
    {
        if (asset.Cost == 0)
        {
            return null;
        }

        if (companyChanged)
        {
            var expected = AssetSupport.ExpectedThrough(asset, t.TransferredOn.AddMonths(-1));
            if (asset.AccumulatedDepreciation < expected)
            {
                throw new ArgumentException($"Run depreciation through the month before {t.TransferredOn:yyyy-MM-dd} first: {asset.AccumulatedDepreciation:N2} posted of {expected:N2} scheduled.");
            }
        }

        var period = await GeneralLedger.OpenPeriodForAsync(db, t.TransferredOn, ct);
        var entry = NewEntry(t.TransferredOn, JournalSource.Asset, asset.Id, $"Transfer asset {asset.TagNumber}: {asset.Description}");
        entry.CompanyId = asset.CompanyId;
        entry.FiscalPeriodId = period.Id;
        var memo = $"{asset.TagNumber} transfer";
        void Line(Guid account, decimal debit, decimal credit, Guid? org, Guid company)
        {
            AddLine(entry, account, debit, credit, memo, org);
            entry.Lines[^1].CompanyId = company;
        }

        Line(asset.AssetAccountId, 0, asset.Cost, t.FromOrgUnitId, t.FromCompanyId!.Value);
        if (asset.AccumulatedDepreciation > 0)
        {
            Line(asset.AccumulatedDepreciationAccountId, asset.AccumulatedDepreciation, 0, t.FromOrgUnitId, t.FromCompanyId!.Value);
        }

        Line(asset.AssetAccountId, asset.Cost, 0, t.ToOrgUnitId, t.ToCompanyId!.Value);
        if (asset.AccumulatedDepreciation > 0)
        {
            Line(asset.AccumulatedDepreciationAccountId, 0, asset.AccumulatedDepreciation, t.ToOrgUnitId, t.ToCompanyId!.Value);
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        return entry.Id;
    }

    public async Task<IReadOnlyList<AssetTransferRowDto>> ListTransfersAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<AssetTransfer>().AsNoTracking();
        if (from is { } f)
        {
            q = q.Where(t => t.TransferredOn >= f);
        }

        if (to is { } e)
        {
            q = q.Where(t => t.TransferredOn <= e);
        }

        var transfers = (await q.ToListAsync(cancellationToken)).OrderByDescending(t => t.TransferredOn).ThenByDescending(t => t.CreatedAtUtc).ToList();
        var assetIds = transfers.Select(t => t.AssetId).Distinct().ToList();
        var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToListAsync(cancellationToken);
        var names = await AssetQueries.Names.LoadAsync(db, assets, cancellationToken, transfers);
        var byId = assets.ToDictionary(a => a.Id);
        return transfers.Select(t => new AssetTransferRowDto(t.AssetId, byId[t.AssetId].TagNumber, byId[t.AssetId].Description, AssetQueries.Transfer(t, names))).ToList();
    }

    public async Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        var proceeds = PurchasingSupport.Money(request!.Proceeds, "Proceeds", allowZero: true);
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentException("Unknown disposal kind.");
        }

        var kind = (AssetDisposalKind)(int)request.Kind;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await AssetSupport.EnsureSetupAsync(db, UtcNow, cancellationToken);
        var asset = await AssetSupport.FindAsync(db, assetId, cancellationToken);
        if (asset.Status != AssetStatus.InService)
        {
            throw new InvalidOperationException($"{asset.TagNumber} is already {asset.Status}.");
        }

        if (request.DisposedOn < asset.AcquisitionDate)
        {
            throw new ArgumentException("The disposal date is before the acquisition date.");
        }

        if (kind == AssetDisposalKind.TradeIn && proceeds == 0)
        {
            throw new ArgumentException("A trade-in needs the allowance the vendor gave for it.");
        }

        var bookGain = proceeds - asset.NetBookValue;
        if (!asset.IsGovernmentProperty)
        {
            // Depreciation must be current through the month before disposal, so the gain or loss is right.
            var expected = AssetSupport.ExpectedThrough(asset, request.DisposedOn.AddMonths(-1));
            if (asset.AccumulatedDepreciation < expected)
            {
                throw new ArgumentException($"Run depreciation through the month before {request.DisposedOn:yyyy-MM-dd} first: {asset.AccumulatedDepreciation:N2} posted of {expected:N2} scheduled.");
            }

            var period = await GeneralLedger.OpenPeriodForAsync(db, request.DisposedOn, cancellationToken);
            var gainLoss = await AssetSupport.AccountByNumberAsync(db, options.Value.GainLossAccountNumber, "gain/loss on disposal", cancellationToken);
            var proceedsAccount = kind == AssetDisposalKind.TradeIn
                ? request.ProceedsAccountId is { } chosen
                    ? (await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == chosen && a.IsActive, cancellationToken) ?? throw WorkbenchNotFoundException.For("Account", chosen)).Id
                    : asset.AssetAccountId
                : (await AssetSupport.AccountByNumberAsync(db, options.Value.CashAccountNumber, "cash", cancellationToken)).Id;
            var verb = kind switch { AssetDisposalKind.Scrap => "Scrap", AssetDisposalKind.TradeIn => "Trade in", _ => "Dispose of" };
            var entry = NewEntry(request.DisposedOn, JournalSource.Asset, asset.Id, $"{verb} asset {asset.TagNumber}: {asset.Description}");
            entry.CompanyId = asset.CompanyId;
            entry.FiscalPeriodId = period.Id;
            var memo = $"{asset.TagNumber} disposal";
            if (asset.AccumulatedDepreciation > 0)
            {
                AddLine(entry, asset.AccumulatedDepreciationAccountId, asset.AccumulatedDepreciation, 0, memo, asset.OrgUnitId);
            }

            if (proceeds > 0)
            {
                AddLine(entry, proceedsAccount, proceeds, 0, kind == AssetDisposalKind.TradeIn ? $"{memo}: trade-in allowance" : $"{memo}: proceeds", asset.OrgUnitId);
            }

            if (bookGain < 0)
            {
                AddLine(entry, gainLoss.Id, -bookGain, 0, $"{memo}: loss", asset.OrgUnitId);
            }

            AddLine(entry, asset.AssetAccountId, 0, asset.Cost, $"{memo}: cost", asset.OrgUnitId);
            if (bookGain > 0)
            {
                AddLine(entry, gainLoss.Id, 0, bookGain, $"{memo}: gain", asset.OrgUnitId);
            }

            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, cancellationToken);
            asset.DisposalEntryId = entry.Id;
        }

        // Each tax book: its disposal-year depreciation, adjusted basis and gain or loss (reporting only, nothing posts).
        var startMonth = await AssetSupport.TaxYearStartMonthAsync(db, asset.CompanyId, cancellationToken);
        var taxGains = new Dictionary<string, decimal>();
        foreach (var book in await db.Set<AssetTaxBook>().Where(b => b.AssetId == asset.Id).ToListAsync(cancellationToken))
        {
            var schedule = TaxDepreciation.Schedule(AssetSupport.Terms(asset, book, startMonth), request.DisposedOn);
            var year = TaxDepreciation.TaxYearOf(request.DisposedOn, startMonth);
            book.DisposalDepreciation = schedule.For(year)?.Total ?? 0;
            book.DisposalAdjustedBasis = schedule.BasisAfter(year);
            book.DisposalGainLoss = proceeds - book.DisposalAdjustedBasis;
            book.UpdatedBy = audit.Actor;
            book.UpdatedAtUtc = UtcNow;
            taxGains[book.Book.ToString()] = book.DisposalGainLoss.Value;
        }

        asset.Status = AssetStatus.Disposed;
        asset.DisposedOn = request.DisposedOn;
        asset.DisposalProceeds = proceeds;
        asset.DisposalReason = reason;
        asset.DisposalKind = kind;
        audit.Record(db, AssetSupport.AssetEntity, asset.Id, "Disposed", new { Status = nameof(AssetStatus.InService), asset.Cost, asset.AccumulatedDepreciation },
            new { Status = nameof(AssetStatus.Disposed), Kind = kind.ToString(), Proceeds = proceeds, GainLoss = bookGain, TaxGainLoss = taxGains, asset.DisposalEntryId }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await AssetQueries.DetailAsync(db, asset.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await AssetSupport.EnsureSetupAsync(db, UtcNow, cancellationToken);
        return await AssetQueries.CategoriesAsync(db, cancellationToken);
    }

    public async Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var category = categoryId is { } id
            ? await db.Set<AssetCategory>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Asset category", id)
            : new AssetCategory { Code = "", Name = "" };
        var before = categoryId is null ? null : CategorySnapshot(category);
        var wasActive = category.IsActive;
        category.Code = PurchasingSupport.Required(request.Code, "Code", 32).ToUpperInvariant();
        category.Name = PurchasingSupport.Required(request.Name, "Name", 200);
        if (await db.Set<AssetCategory>().AnyAsync(c => c.Code == category.Code && c.Id != category.Id, cancellationToken))
        {
            throw new ArgumentException($"There's already a category {category.Code}.");
        }

        if (request.CapitalizationThreshold is < 0)
        {
            throw new ArgumentException("The capitalization threshold can't be negative.");
        }

        if (request.AssetAccountId == request.AccumulatedDepreciationAccountId)
        {
            throw new ArgumentException("The asset and accumulated depreciation accounts must be different.");
        }

        category.DefaultMethod = request.DefaultMethod.ToDomain();
        category.Convention = Enum.IsDefined(request.Convention) ? request.Convention.ToDomain() : throw new ArgumentException("Unknown depreciation convention.");
        category.DefaultLifeYears = Life(request.DefaultLifeYears);
        category.MacrsRecoveryYears = Macrs(request.MacrsRecoveryYears);
        category.CapitalizationThreshold = request.CapitalizationThreshold is { } t ? decimal.Round(t, 2) : null;
        category.AssetAccountId = (await AccountAsync(db, request.AssetAccountId, AccountType.Asset, "asset", cancellationToken)).Id;
        category.AccumulatedDepreciationAccountId = (await AccountAsync(db, request.AccumulatedDepreciationAccountId, AccountType.Asset, "accumulated depreciation", cancellationToken)).Id;
        category.ExpenseAccountId = (await AccountAsync(db, request.ExpenseAccountId, AccountType.Expense, "depreciation expense", cancellationToken)).Id;
        category.IsActive = request.IsActive;
        if (categoryId is null)
        {
            db.Set<AssetCategory>().Add(category);
        }

        var action = categoryId is null ? "Created" : wasActive == category.IsActive ? "Updated" : category.IsActive ? "Reactivated" : "Deactivated";
        audit.Record(db, AssetSupport.CategoryEntity, category.Id, action, before, CategorySnapshot(category));
        await db.SaveChangesAsync(cancellationToken);
        return (await AssetQueries.CategoriesAsync(db, cancellationToken)).First(c => c.Id == category.Id);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var category = await db.Set<AssetCategory>().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Asset category", categoryId);
        var used = await db.Set<FixedAsset>().CountAsync(a => a.CategoryId == categoryId, cancellationToken);
        if (used > 0)
        {
            throw new InvalidOperationException($"{category.Code} is used by {used} asset{(used == 1 ? "" : "s")}; deactivate it instead.");
        }

        audit.Record(db, AssetSupport.CategoryEntity, category.Id, "Deleted", CategorySnapshot(category));
        db.Set<AssetCategory>().Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static object CategorySnapshot(AssetCategory c) => new
    {
        c.Code,
        c.Name,
        DefaultMethod = c.DefaultMethod.ToString(),
        Convention = c.Convention.ToString(),
        c.DefaultLifeYears,
        c.MacrsRecoveryYears,
        c.CapitalizationThreshold,
        c.AssetAccountId,
        c.AccumulatedDepreciationAccountId,
        c.ExpenseAccountId,
        c.IsActive,
    };

    /// <summary>The lowest threshold any active category capitalizes at (bill lines below it can't be capitalized anywhere).</summary>
    private async Task<decimal> LowestThresholdAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var thresholds = await db.Set<AssetCategory>().AsNoTracking().Where(c => c.IsActive).Select(c => c.CapitalizationThreshold).ToListAsync(ct);
        return thresholds.Select(t => t ?? options.Value.CapitalizationThreshold).DefaultIfEmpty(options.Value.CapitalizationThreshold).Min();
    }

    // ---- Helpers ----

    private async Task ApplyAsync(IWorkbenchDbContext db, FixedAsset asset, SaveAssetRequest r, bool isNew, CancellationToken ct)
    {
        var category = await db.Set<AssetCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId, ct) ?? throw WorkbenchNotFoundException.For("Asset category", r.CategoryId);
        if (!category.IsActive && (isNew || category.Id != asset.CategoryId))
        {
            throw new ArgumentException($"Category {category.Code} is inactive.");
        }

        var depreciated = !isNew && await db.Set<AssetDepreciation>().AnyAsync(d => d.AssetId == asset.Id, ct);
        asset.Description = PurchasingSupport.Required(r.Description, "Description", 500);
        asset.SerialNumber = PurchasingSupport.Optional(r.SerialNumber, "Serial number", 100);
        asset.Location = PurchasingSupport.Optional(r.Location, "Location", 200);
        await EnsureExistsAsync<Employee>(db, r.CustodianEmployeeId, "Custodian", ct);
        await EnsureExistsAsync<OrgUnit>(db, r.OrgUnitId, "Organization", ct);
        asset.CustodianEmployeeId = r.CustodianEmployeeId;
        asset.OrgUnitId = r.OrgUnitId;
        if (r.WbsElementId is { } wid)
        {
            var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wid);
            if (r.ProjectId is { } pid && pid != wbs.ProjectId)
            {
                throw new ArgumentException("The WBS element isn't on that project.");
            }

            asset.ProjectId = wbs.ProjectId;
            asset.WbsElementId = wbs.Id;
        }
        else
        {
            await EnsureExistsAsync<Project>(db, r.ProjectId, "Project", ct);
            asset.ProjectId = r.ProjectId;
            asset.WbsElementId = null;
        }

        asset.IsGovernmentFurnished = r.IsGovernmentFurnished;
        asset.IsContractorAcquired = r.IsContractorAcquired && !r.IsGovernmentFurnished;
        asset.GovernmentPropertyNumber = PurchasingSupport.Optional(r.GovernmentPropertyNumber, "Government property number", 64);
        if (asset.IsGovernmentProperty)
        {
            asset.ContractId = r.ContractId is { } cid && await db.Set<Contract>().AnyAsync(c => c.Id == cid, ct)
                ? cid
                : throw new ArgumentException("Government property (GFP or contractor-acquired) needs the contract it's accountable to (FAR 52.245-1).");
        }
        else
        {
            asset.ContractId = r.ContractId;
            if (r.ContractId is { } cid && !await db.Set<Contract>().AnyAsync(c => c.Id == cid, ct))
            {
                throw WorkbenchNotFoundException.For("Contract", cid);
            }
        }

        if (r.VendorId is { } vid && !await db.Set<Vendor>().AnyAsync(v => v.Id == vid, ct))
        {
            throw WorkbenchNotFoundException.For("Vendor", vid);
        }

        asset.VendorId ??= r.VendorId;
        var tag = PurchasingSupport.Optional(r.TagNumber, "Tag number", 32);
        if (isNew)
        {
            var tags = await db.Set<FixedAsset>().Select(a => a.TagNumber).ToListAsync(ct);
            asset.TagNumber = tag ?? NextTag(tags);
        }
        else if (tag is not null)
        {
            asset.TagNumber = tag;
        }

        if (await db.Set<FixedAsset>().AnyAsync(a => a.TagNumber == asset.TagNumber && a.Id != asset.Id, ct))
        {
            throw new ArgumentException($"Tag {asset.TagNumber} is already used.");
        }

        // Financial terms are fixed once depreciation has been posted.
        var cost = PurchasingSupport.Money(r.Cost, "Cost");
        var salvage = PurchasingSupport.Money(r.SalvageValue, "Salvage value", allowZero: true);
        var life = Life(r.UsefulLifeYears ?? category.DefaultLifeYears);
        var method = r.Method?.ToDomain() ?? category.DefaultMethod;
        var convention = r.Convention?.ToDomain() ?? (isNew ? category.Convention : asset.Convention);
        if (!Enum.IsDefined(convention))
        {
            throw new ArgumentException("Unknown depreciation convention.");
        }

        // Opening balances (assets brought over from another system). On an update, no date keeps what's there.
        var openingDate = r.OpeningBalanceDate ?? (isNew ? null : asset.OpeningBalanceDate);
        var opening = r.OpeningBalanceDate is null && !isNew ? asset.OpeningAccumulatedDepreciation : PurchasingSupport.Money(r.OpeningAccumulatedDepreciation, "Accumulated depreciation", allowZero: true);
        var inService = r.InServiceDate ?? r.AcquisitionDate;
        var assetAccount = r.AssetAccountId ?? category.AssetAccountId;
        var accumulated = r.AccumulatedDepreciationAccountId ?? category.AccumulatedDepreciationAccountId;
        var expense = r.ExpenseAccountId ?? category.ExpenseAccountId;
        if (depreciated && (cost != asset.Cost || salvage != asset.SalvageValue || life != asset.UsefulLifeYears || method != asset.Method || convention != asset.Convention || inService != asset.InServiceDate
            || openingDate != asset.OpeningBalanceDate || opening != asset.OpeningAccumulatedDepreciation
            || assetAccount != asset.AssetAccountId || accumulated != asset.AccumulatedDepreciationAccountId || expense != asset.ExpenseAccountId
            || category.Id != asset.CategoryId || r.AcquisitionDate != asset.AcquisitionDate))
        {
            throw new ArgumentException($"{asset.TagNumber} has posted depreciation: its cost, life, method, dates, category, accounts and opening balance can't change.");
        }

        if (salvage >= cost)
        {
            throw new ArgumentException("The salvage value must be less than the cost.");
        }

        if (inService < r.AcquisitionDate)
        {
            throw new ArgumentException("The in-service date can't be before the acquisition date.");
        }

        if (!isNew && asset.SourceBillLineId is not null && cost != asset.Cost)
        {
            throw new ArgumentException("A capitalized asset's cost comes from its bill line.");
        }

        if (openingDate is { } cutover)
        {
            if (cutover.AddDays(1).Day != 1)
            {
                throw new ArgumentException($"Opening balances are as of a month end (for example {new DateOnly(cutover.Year, cutover.Month, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}), not {cutover:yyyy-MM-dd}.");
            }

            if (opening > 0 && (r.IsGovernmentFurnished || r.IsContractorAcquired))
            {
                throw new ArgumentException("Government property isn't depreciated, so it has no accumulated depreciation.");
            }
        }
        else if (opening != 0)
        {
            throw new ArgumentException("Accumulated depreciation needs the cut-over date it's as of.");
        }

        asset.CategoryId = category.Id;
        asset.AcquisitionDate = r.AcquisitionDate;
        asset.InServiceDate = inService;
        asset.Cost = cost;
        asset.SalvageValue = salvage;
        asset.UsefulLifeYears = life;
        asset.Method = method;
        asset.Convention = convention;
        asset.OpeningBalanceDate = openingDate;
        asset.OpeningAccumulatedDepreciation = opening;
        if (!depreciated)
        {
            asset.AccumulatedDepreciation = opening;
        }

        // Validates the opening balance against the life (throws when the life ran out with something left).
        _ = AssetSupport.Schedule(asset);
        asset.MacrsRecoveryYears = Macrs(r.MacrsRecoveryYears ?? category.MacrsRecoveryYears);
        asset.AssetAccountId = (await AccountAsync(db, assetAccount, AccountType.Asset, "asset", ct)).Id;
        asset.AccumulatedDepreciationAccountId = (await AccountAsync(db, accumulated, AccountType.Asset, "accumulated depreciation", ct)).Id;
        asset.ExpenseAccountId = (await AccountAsync(db, expense, AccountType.Expense, "depreciation expense", ct)).Id;
    }

    private static int Life(int years) => years is >= 1 and <= 50 ? years : throw new ArgumentException("The useful life is 1 to 50 years.");

    private static int? Macrs(int? years) =>
        years is null || DepreciationCalculator.MacrsTables.ContainsKey(years.Value) ? years : throw new ArgumentException("MACRS recovery periods are 3, 5, 7, 10, 15 or 20 years.");

    private static async Task<Account> AccountAsync(IWorkbenchDbContext db, Guid id, AccountType type, string what, CancellationToken ct)
    {
        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Account", id);
        if (!account.IsActive || account.Type != type || await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, ct))
        {
            throw new ArgumentException($"The {what} account must be an active {type.ToString().ToLowerInvariant()} detail account ({account.Number} isn't).");
        }

        return account;
    }

    private static async Task EnsureExistsAsync<T>(IWorkbenchDbContext db, Guid? id, string what, CancellationToken ct) where T : Workbench.Domain.Entity
    {
        if (id is { } value && !await db.Set<T>().AnyAsync(e => e.Id == value, ct))
        {
            throw WorkbenchNotFoundException.For(what, value);
        }
    }

    private static string NextTag(IEnumerable<string> existing)
    {
        var max = existing.Select(t => t.StartsWith("FA-", StringComparison.Ordinal) && int.TryParse(t[3..], out var i) ? i : 0).DefaultIfEmpty(0).Max();
        return $"FA-{max + 1:D5}";
    }

    /// <summary><paramref name="date"/> when its period is open, otherwise today (late capitalizations post in the current period).</summary>
    private async Task<DateOnly> DateInOpenPeriodAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken ct)
    {
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= date && p.End >= date, ct);
        return period is { Status: PeriodStatus.Open } ? date : DateOnly.FromDateTime(UtcNow);
    }

    private JournalEntry NewEntry(DateOnly date, JournalSource source, Guid sourceId, string description) => new()
    {
        Date = date,
        Source = source,
        SourceId = sourceId,
        Description = AssetSupport.Truncate(description, 500),
        CreatedBy = ledger.Actor,
        CreatedAtUtc = ledger.UtcNow,
    };

    internal static void AddLine(JournalEntry entry, Guid account, decimal debit, decimal credit, string memo, Guid? org = null, Guid? project = null, Guid? wbs = null) =>
        entry.Lines.Add(new JournalLine
        {
            JournalEntryId = entry.Id,
            LineNumber = entry.Lines.Count + 1,
            AccountId = account,
            OrgUnitId = org,
            ProjectId = project,
            WbsElementId = wbs,
            Debit = debit,
            Credit = credit,
            Memo = AssetSupport.Truncate(memo, 500),
        });

    private static object Snapshot(FixedAsset a) => new
    {
        a.TagNumber,
        a.Description,
        a.CategoryId,
        a.AcquisitionDate,
        a.InServiceDate,
        a.Cost,
        a.SalvageValue,
        a.UsefulLifeYears,
        Method = a.Method.ToString(),
        Convention = a.Convention.ToString(),
        a.OpeningBalanceDate,
        a.OpeningAccumulatedDepreciation,
        a.Location,
        a.CustodianEmployeeId,
        a.OrgUnitId,
        a.ProjectId,
        a.IsGovernmentFurnished,
        a.IsContractorAcquired,
        a.ContractId,
        a.GovernmentPropertyNumber,
    };
}
