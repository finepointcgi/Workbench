using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Quality;
using Workbench.Domain.Materials;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>Quality settings, bound from config section <c>Quality</c>: the approval thresholds.</summary>
public sealed class QualityOptions
{
    public const string Section = "Quality";

    /// <summary>NCR dispositions of material worth more than this (at cost) also go to Finance.</summary>
    public decimal NcrFinanceThreshold { get; set; } = 5_000m;

    /// <summary>Engineering changes with a cost impact over this also go to Finance.</summary>
    public decimal EcoFinanceThreshold { get; set; } = 25_000m;
}

internal static class QualityRules
{
    public const string PlanEntity = nameof(InspectionPlan);
    public const string InspectionEntity = nameof(Inspection);
    public const string NcrEntity = nameof(Nonconformance);
    public const string DebitMemoEntity = nameof(VendorDebitMemo);
    public const string CarEntity = nameof(CorrectiveAction);
    public const string EcoEntity = nameof(EngineeringChange);

    /// <summary>The next "PREFIX-000123" number, counting rows added in this unit of work too.</summary>
    public static async Task<string> NextNumberAsync<T>(IWorkbenchDbContext db, System.Linq.Expressions.Expression<Func<T, string>> number, string prefix, CancellationToken ct)
        where T : class
    {
        var compiled = number.Compile();
        var saved = await db.Set<T>().Select(number).Where(n => n.StartsWith(prefix + "-")).ToListAsync(ct);
        var local = db.Set<T>().Local.Select(compiled);
        var max = saved.Concat(local).Select(n => n.StartsWith(prefix + "-", StringComparison.Ordinal) && int.TryParse(n[(prefix.Length + 1)..], out var i) ? i : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}-{max + 1:D6}";
    }

    /// <summary>
    /// The sample to inspect: every unit, a fixed sample, or the ANSI/ASQ Z1.4 general inspection level II single
    /// sampling size for the lot (never more than the lot).
    /// </summary>
    public static int SampleSize(Sampling sampling, int fixedSize, decimal lotSize)
    {
        var lot = (int)Math.Ceiling(Math.Max(0, lotSize));
        var size = sampling switch
        {
            Sampling.FixedSample => Math.Max(1, fixedSize),
            Sampling.Aql => AqlLevelTwo(lot),
            _ => lot,
        };
        return Math.Min(size, lot);
    }

    /// <summary>Z1.4 table I (general level II) and table II-A sample sizes by code letter.</summary>
    public static int AqlLevelTwo(int lot) => lot switch
    {
        <= 8 => 2,
        <= 15 => 3,
        <= 25 => 5,
        <= 50 => 8,
        <= 90 => 13,
        <= 150 => 20,
        <= 280 => 32,
        <= 500 => 50,
        <= 1_200 => 80,
        <= 3_200 => 125,
        <= 10_000 => 200,
        <= 35_000 => 315,
        <= 150_000 => 500,
        <= 500_000 => 800,
        _ => 1_250,
    };

    public static IReadOnlyList<string> Codes(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }

    public static string Location(string? warehouse, string? bin) => warehouse is null ? "" : $"{warehouse} / {bin}";

    public static InspectionType ToContract(this InspectionKind v) => (InspectionType)(int)v;
    public static InspectionKind ToDomain(this InspectionType v) => (InspectionKind)(int)v;
    public static Sampling ToContract(this SamplingMethod v) => (Sampling)(int)v;
    public static SamplingMethod ToDomain(this Sampling v) => (SamplingMethod)(int)v;
    public static CharacteristicType ToContract(this CharacteristicKind v) => (CharacteristicType)(int)v;
    public static CharacteristicKind ToDomain(this CharacteristicType v) => (CharacteristicKind)(int)v;
    public static InspectionState ToContract(this InspectionStatus v) => (InspectionState)(int)v;
    public static InspectionStatus ToDomain(this InspectionState v) => (InspectionStatus)(int)v;
    public static NcrSource ToContract(this NonconformanceSource v) => (NcrSource)(int)v;
    public static NcrState ToContract(this NonconformanceStatus v) => (NcrState)(int)v;
    public static NonconformanceStatus ToDomain(this NcrState v) => (NonconformanceStatus)(int)v;
    public static NcrDisposition ToContract(this NonconformanceDisposition v) => (NcrDisposition)(int)v;
    public static NonconformanceDisposition ToDomain(this NcrDisposition v) => (NonconformanceDisposition)(int)v;
    public static DebitMemoState ToContract(this DebitMemoStatus v) => (DebitMemoState)(int)v;
    public static CarState ToContract(this CorrectiveActionStatus v) => (CarState)(int)v;
    public static CorrectiveActionStatus ToDomain(this CarState v) => (CorrectiveActionStatus)(int)v;
    public static RootCause ToContract(this RootCauseCategory v) => (RootCause)(int)v;
    public static RootCauseCategory ToDomain(this RootCause v) => (RootCauseCategory)(int)v;
    public static EcoState ToContract(this EngineeringChangeStatus v) => (EcoState)(int)v;
    public static EngineeringChangeStatus ToDomain(this EcoState v) => (EngineeringChangeStatus)(int)v;
    public static EcoReason ToContract(this EngineeringChangeReason v) => (EcoReason)(int)v;
    public static EngineeringChangeReason ToDomain(this EcoReason v) => (EngineeringChangeReason)(int)v;
    public static Effectivity ToContract(this EffectivityKind v) => (Effectivity)(int)v;
    public static EffectivityKind ToDomain(this Effectivity v) => (EffectivityKind)(int)v;
    public static EcoStockDisposition ToContract(this ChangeStockDisposition v) => (EcoStockDisposition)(int)v;
    public static ChangeStockDisposition ToDomain(this EcoStockDisposition v) => (ChangeStockDisposition)(int)v;
    public static EcoWorkOrderDisposition ToContract(this ChangeWorkOrderDisposition v) => (EcoWorkOrderDisposition)(int)v;
    public static ChangeWorkOrderDisposition ToDomain(this EcoWorkOrderDisposition v) => (ChangeWorkOrderDisposition)(int)v;
    public static WorkOrderReviewState ToContract(this WorkOrderReviewStatus v) => (WorkOrderReviewState)(int)v;
    public static WorkOrderState ToContract(this WorkOrderStatus v) => (WorkOrderState)(int)v;
}

/// <summary>The defect codes every organization starts with (added the first time they're used).</summary>
internal static class DefectCodeDefaults
{
    private static readonly (string Code, string Description)[] Defaults =
    [
        ("DIM", "Dimension out of tolerance"),
        ("COS", "Cosmetic damage or finish defect"),
        ("DOC", "Missing or wrong certification or documentation"),
        ("FUN", "Fails functional or electrical test"),
        ("WRG", "Wrong part, revision or quantity"),
        ("PKG", "Packaging or shipping damage"),
        ("MAT", "Material or plating nonconformance"),
        ("WKM", "Workmanship (assembly, soldering, wiring)"),
    ];

    public static async Task EnsureAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if (await db.Set<DefectCode>().AnyAsync(ct))
        {
            return;
        }

        db.Set<DefectCode>().AddRange(Defaults.Select(d => new DefectCode { Code = d.Code, Description = d.Description }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Normalizes and checks codes against the active list.</summary>
    public static async Task<string> ValidateAsync(IWorkbenchDbContext db, IReadOnlyList<string>? codes, CancellationToken ct)
    {
        var list = (codes ?? []).Select(c => (c ?? "").Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("Choose at least one defect code.");
        }

        await EnsureAsync(db, ct);
        var known = await db.Set<DefectCode>().AsNoTracking().Where(d => d.IsActive && list.Contains(d.Code)).Select(d => d.Code).ToListAsync(ct);
        var unknown = list.FirstOrDefault(c => !known.Contains(c));
        if (unknown is not null)
        {
            throw new ArgumentException($"\"{unknown}\" isn't an active defect code.");
        }

        var csv = string.Join(",", list);
        return csv.Length > 200 ? throw new ArgumentException("Too many defect codes.") : csv;
    }
}
