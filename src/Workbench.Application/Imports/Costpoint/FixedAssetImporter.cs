using Workbench.Application.Assets;
using Workbench.Contracts.Assets;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Assets;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>
/// Costpoint fixed assets with opening balances (Fixed Assets › Asset Master export) → the asset register, matched on the
/// asset (tag) number. Each asset carries its cost, in-service date and accumulated depreciation as of the cut-over date
/// (a month end). Nothing is posted: the balances arrive with the general ledger import. Workbench depreciates from the
/// month after the cut-over, spreading what's left over the rest of the life, so the first run catches up any months
/// since the cut-over once and never repeats what Costpoint already took.
/// </summary>
public sealed class FixedAssetImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.FixedAssets;
    public override string Title => "Fixed assets (opening balances)";
    public override string Description =>
        "Assets with cost, in-service date and accumulated depreciation (or net book value) as of the cut-over month end. Nothing is posted; depreciation continues from the month after.";
    public override string? SampleFile => "samples/import/costpoint/fixed-assets.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("AssetId", "Asset number", true, "Becomes the asset tag; re-importing the same number updates the asset.", "ASSET_ID", "ASSET_NO", "Asset No", "Asset ID", "Asset Number", "Tag", "Tag Number", "ASSET_ID_ITEM"),
        new("Description", "Description", true, "", "ASSET_DESC", "ASSET_S_DESC", "Asset Description", "Description", "Name"),
        new("Category", "Category", true, "Workbench asset category code (COMP, FURN, LHI or your own).", "ASSET_CLASS_CD", "PROP_TYPE_CD", "Asset Class", "Property Type", "Category Code", "Class"),
        new("InServiceDate", "In-service date", true, "Date placed in service; depreciation started that month.", "PL_IN_SERV_DT", "IN_SERV_DT", "Placed in Service", "In Service Date", "Service Date", "Depreciation Start"),
        new("AcquisitionDate", "Acquisition date", false, "Defaults to the in-service date.", "ACQ_DT", "ACQUIRED_DT", "Acquisition Date", "Acquired", "Purchase Date"),
        new("Cost", "Acquisition cost", true, "Capitalized cost.", "ACQ_COST_AMT", "TOT_COST_AMT", "COST_AMT", "Cost", "Acquisition Cost", "Original Cost", "Book Cost"),
        new("AccumulatedDepreciation", "Accumulated depreciation", false, "Through the cut-over date. Give this or the net book value (or both, which must agree).", "ACCUM_DEPR_AMT", "TOT_ACCUM_DEPR", "Accumulated Depreciation", "Accum Depr", "Accum Depreciation", "Depreciation to Date"),
        new("NetBookValue", "Net book value", false, "Cost less accumulated depreciation at the cut-over.", "NET_BOOK_VAL", "NBV", "Net Book Value", "Book Value"),
        new("CutoverDate", "Cut-over date", true, "The month end the balances are as of, e.g. 2026-09-30.", "AS_OF_DT", "BAL_DT", "As Of", "As Of Date", "Cutover Date", "Cut-over", "Balance Date", "Through Date"),
        new("UsefulLifeYears", "Useful life (years)", false, "Defaults to the category's.", "LIFE_YRS", "USEFUL_LIFE", "Useful Life", "Life", "Life (Years)", "Useful Life Years"),
        new("UsefulLifeMonths", "Useful life (months)", false, "Alternative to years; must be whole years (36, 60...).", "LIFE_MOS", "USEFUL_LIFE_MOS", "Life (Months)", "Useful Life Months", "Months"),
        new("Method", "Depreciation method", false, "SL (straight line), DDB/DB (declining balance) or SYD. Defaults to the category's.", "DEPR_METHOD_CD", "DEPR_MTHD", "Method", "Depreciation Method"),
        new("Convention", "Convention", false, "Full month, half month (mid-month) or following month. Defaults to the category's.", "AVG_CONV_CD", "CONVENTION_CD", "Averaging Convention", "Convention"),
        new("SalvageValue", "Salvage value", false, "Defaults to 0.", "SALVAGE_AMT", "SALV_VAL", "Salvage", "Salvage Value", "Residual Value"),
        new("SerialNumber", "Serial number", false, "", "SERIAL_NO", "SERIAL_NUM", "Serial", "Serial Number"),
        new("Location", "Location", false, "", "LOCATION_CD", "LOC_DESC", "Location"),
        new("OrgId", "Org", false, "Org id of an existing org unit (depreciation expense segment).", "ORG_ID", "Org", "Organization"),
        new("ProjectId", "Project", false, "Code of an existing project (depreciation expense segment).", "PROJ_ID", "Project", "Project ID"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var assets = Service<IAssetService>(context);
        var people = Service<IPeopleService>(context);
        var categories = (await assets.ListCategoriesAsync(cancellationToken)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var existing = (await assets.ListAsync(new AssetQuery(), cancellationToken)).ToDictionary(a => a.TagNumber, StringComparer.OrdinalIgnoreCase);
        var orgs = (await people.ListOrgUnitsAsync(cancellationToken)).ToDictionary(o => o.Code, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var projects = (await Service<IProjectService>(context).ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutovers = new HashSet<DateOnly>();

        foreach (var row in rows)
        {
            if (Parse(row, categories, orgs, projects) is not { } parsed)
            {
                continue;
            }

            var (tag, request, category) = parsed;
            if (!seen.Add(tag))
            {
                row.Error("AssetId", $"Asset {tag} appears more than once in the file.");
                continue;
            }

            if (cutovers.Add(request.OpeningBalanceDate!.Value) && cutovers.Count == 2)
            {
                row.Warning("CutoverDate", $"Rows have different cut-over dates ({string.Join(", ", cutovers.Order().Select(d => d.ToString("yyyy-MM-dd")))}); usually every asset is as of the same month end.");
            }

            var summary = $"{request.Description}: cost {request.Cost:N2}, accumulated {request.OpeningAccumulatedDepreciation:N2} as of {request.OpeningBalanceDate:yyyy-MM-dd}";
            if (existing.TryGetValue(tag, out var match))
            {
                var current = await assets.GetAsync(match.Id, cancellationToken);
                var changed = Changed(
                    ("Description", current.Asset.Description, request.Description), ("Category", current.CategoryId, request.CategoryId),
                    ("Acquired", current.Asset.AcquisitionDate, request.AcquisitionDate), ("In service", current.InServiceDate, request.InServiceDate),
                    ("Cost", current.Asset.Cost, request.Cost), ("Salvage", current.SalvageValue, request.SalvageValue),
                    ("Life", current.Asset.UsefulLifeYears, request.UsefulLifeYears ?? category.DefaultLifeYears), ("Method", current.Asset.Method, request.Method ?? category.DefaultMethod),
                    ("Convention", current.Convention, request.Convention ?? current.Convention),
                    ("Cut-over date", current.OpeningBalanceDate, request.OpeningBalanceDate), ("Accumulated depreciation", current.OpeningAccumulatedDepreciation, request.OpeningAccumulatedDepreciation),
                    ("Serial number", current.SerialNumber, request.SerialNumber), ("Location", current.Asset.Location, request.Location),
                    ("Org", current.OrgUnitId, request.OrgUnitId), ("Project", current.ProjectId, request.ProjectId));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Fixed asset", tag, "Unchanged");
                    continue;
                }

                if (current.Asset.Status != AssetState.InService || current.Depreciation.Count > 0)
                {
                    row.Error("AssetId", $"{tag} is already in Workbench with posted depreciation or a disposal, so its balances can't be re-imported.");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(FixedAsset), match.Id, "Fixed asset", tag, UpdateSummary(changed),
                    () => assets.UpdateAsync(match.Id, request with { TagNumber = current.Asset.TagNumber }, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(FixedAsset), "Fixed asset", tag, summary,
                async () => (await assets.CreateAsync(request, cancellationToken)).Asset.Id, cancellationToken);
        }
    }

    /// <summary>Reads and checks one row (everything the asset service would refuse, so the dry run shows it); null on errors.</summary>
    private static (string Tag, SaveAssetRequest Request, AssetCategoryDto Category)? Parse(
        MappedRow row, Dictionary<string, AssetCategoryDto> categories, Dictionary<string, Guid> orgs, Dictionary<string, Guid> projects)
    {
        var tag = row.Required("AssetId");
        var description = row.Required("Description");
        var categoryCode = row.Required("Category");
        var inService = RequiredDate(row, "InServiceDate");
        var acquired = row.Date("AcquisitionDate") ?? inService;
        var cost = row.Get("Cost") is null ? Missing<decimal>(row, "Cost") : row.Decimal("Cost");
        var accumulated = row.Decimal("AccumulatedDepreciation");
        var netBook = row.Decimal("NetBookValue");
        var cutover = RequiredDate(row, "CutoverDate");

        var salvage = row.Decimal("SalvageValue") ?? 0;
        var lifeYears = row.Decimal("UsefulLifeYears");
        var lifeMonths = row.Decimal("UsefulLifeMonths");
        var method = ParseMethod(row);
        var convention = ParseConvention(row);
        if (row.HasErrors || tag is null || description is null || categoryCode is null || inService is null || cost is null || cutover is null)
        {
            return null;
        }

        if (tag.Length > 32)
        {
            row.Error("AssetId", $"Asset number {tag} is longer than 32 characters.");
        }

        if (!categories.TryGetValue(categoryCode, out var category))
        {
            row.Error("Category", $"Category {categoryCode} doesn't exist; add it under Fixed assets › Asset categories first.");
            return null;
        }

        if (!category.IsActive)
        {
            row.Error("Category", $"Category {category.Code} is inactive.");
        }

        if (cost <= 0)
        {
            row.Error("Cost", "The cost must be more than zero.");
        }

        if (salvage < 0 || salvage >= cost)
        {
            row.Error("SalvageValue", "The salvage value must be at least zero and less than the cost.");
        }

        if (accumulated is null && netBook is null)
        {
            row.Error("AccumulatedDepreciation", "Give the accumulated depreciation or the net book value.");
        }
        else if (accumulated is { } a && netBook is { } n && Math.Abs(cost.Value - a - n) > 0.01m)
        {
            row.Error("NetBookValue", $"Cost {cost:N2} less accumulated depreciation {a:N2} is {cost - a:N2}, not the net book value {n:N2}.");
        }

        var opening = decimal.Round(accumulated ?? cost.Value - (netBook ?? 0), 2);
        int life;
        if (lifeMonths is { } months)
        {
            if (months <= 0 || months % 12 != 0)
            {
                row.Error("UsefulLifeMonths", $"A useful life of {months} months isn't whole years; Workbench depreciates over whole years.");
            }

            life = (int)(months / 12);
        }
        else if (lifeYears is { } years)
        {
            if (years != decimal.Truncate(years))
            {
                row.Error("UsefulLifeYears", $"A useful life of {years} years isn't whole years.");
            }

            life = (int)years;
        }
        else
        {
            life = category.DefaultLifeYears;
        }

        if (life is < 1 or > 50)
        {
            row.Error(lifeMonths is null ? "UsefulLifeYears" : "UsefulLifeMonths", "The useful life is 1 to 50 years.");
        }

        if (cutover.Value.AddDays(1).Day != 1)
        {
            row.Error("CutoverDate", $"The cut-over date {cutover:yyyy-MM-dd} isn't a month end; balances are as of the close of a month.");
        }

        if (acquired > inService)
        {
            row.Error("InServiceDate", "The in-service date can't be before the acquisition date.");
        }

        var org = Lookup(row, "OrgId", "Org", orgs);
        var project = Lookup(row, "ProjectId", "Project", projects);

        if (row.HasErrors)
        {
            return null;
        }

        // The same checks the depreciation schedule makes: within cost less salvage, in service before the cut-over, life left.
        try
        {
            var schedule = DepreciationCalculator.MonthlySchedule(cost.Value, salvage, life, (method ?? category.DefaultMethod).ToDomain(), (convention ?? category.Convention).ToDomain());
            DepreciationCalculator.OpeningBalanceSchedule(schedule, DepreciationCalculator.MonthIndex(inService.Value, cutover.Value) + 1, opening, cost.Value - salvage);
        }
        catch (ArgumentException ex)
        {
            row.Error("AccumulatedDepreciation", ex.Message);
            return null;
        }

        var request = new SaveAssetRequest(
            description, category.Id, acquired!.Value, cost.Value, salvage, life, method, tag, inService,
            SerialNumber: row.Get("SerialNumber"), Location: row.Get("Location"), OrgUnitId: org, ProjectId: project,
            Convention: convention, OpeningBalanceDate: cutover, OpeningAccumulatedDepreciation: opening);
        return (tag, request, category);
    }

    private static DateOnly? RequiredDate(MappedRow row, string field) => row.Get(field) is null ? Missing<DateOnly>(row, field) : row.Date(field);

    private static T? Missing<T>(MappedRow row, string field) where T : struct
    {
        row.Required(field);
        return null;
    }

    private static Guid? Lookup(MappedRow row, string field, string what, Dictionary<string, Guid> ids)
    {
        if (row.Get(field) is not { } code)
        {
            return null;
        }

        if (ids.TryGetValue(code, out var id))
        {
            return id;
        }

        row.Error(field, $"{what} {code} isn't in Workbench.");
        return null;
    }

    private static AssetDepreciationMethod? ParseMethod(MappedRow row)
    {
        var text = row.Get("Method");
        if (text is null)
        {
            return null;
        }

        AssetDepreciationMethod? method = FieldMatcher.Normalize(text) switch
        {
            "sl" or "straightline" or "straight" or "s" => AssetDepreciationMethod.StraightLine,
            "ddb" or "db" or "200db" or "200" or "decliningbalance" or "doubledecliningbalance" or "declining" => AssetDepreciationMethod.DecliningBalance,
            "syd" or "sumofyearsdigits" or "sumoftheyearsdigits" or "sumofyears" => AssetDepreciationMethod.SumOfYearsDigits,
            _ => null,
        };
        if (method is null)
        {
            row.Error("Method", $"Depreciation method '{text}' isn't SL, DDB or SYD.");
        }

        return method;
    }

    private static AssetDepreciationConvention? ParseConvention(MappedRow row)
    {
        var text = row.Get("Convention");
        if (text is null)
        {
            return null;
        }

        AssetDepreciationConvention? convention = FieldMatcher.Normalize(text) switch
        {
            "f" or "fm" or "full" or "fullmonth" => AssetDepreciationConvention.FullMonth,
            "h" or "hm" or "mm" or "half" or "halfmonth" or "mid" or "midmonth" => AssetDepreciationConvention.MidMonth,
            "n" or "nm" or "next" or "nextmonth" or "following" or "followingmonth" => AssetDepreciationConvention.FollowingMonth,
            _ => null,
        };
        if (convention is null)
        {
            row.Error("Convention", $"Convention '{text}' isn't full month, half month or following month.");
        }

        return convention;
    }
}
