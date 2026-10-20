using Workbench.Contracts.Compliance;

namespace Workbench.Application.Compliance;

/// <summary>What in Workbench a disclosure item describes, so its answer can be compared with the live configuration.</summary>
public enum DisclosureLink
{
    None,
    LaborChargingMethod,
    UncompensatedOvertime,
    FringeBase,
    OverheadBase,
    GaBase,
    DepreciationMethods,
    CapitalizationThreshold,
    UnallowableCosts,
}

/// <summary>One item of a disclosure statement form. <see cref="IsAmount"/> items take a number as their choice.</summary>
public sealed record DisclosureItem(string Key, int Part, string Question, IReadOnlyList<DisclosureChoiceDto>? ChoiceList = null, DisclosureLink Link = DisclosureLink.None, bool IsAmount = false)
{
    public IReadOnlyList<DisclosureChoiceDto> Choices => ChoiceList ?? [];

    public bool HasChoices => Choices.Count > 0 || IsAmount;
}

/// <summary>
/// The disclosure statement forms as structured items. DS-1 follows the CASB DS-1 layout (parts I–VIII, item numbers as
/// on the form); the text is a plain-language summary of each item, so check wording against the current CASB form
/// before filing. DS-2 (educational institutions) is listed with its parts and the items that matter most.
/// </summary>
public static class DisclosureItems
{
    private static DisclosureChoiceDto C(string code, string label) => new(code, label);

    private static readonly DisclosureChoiceDto[] YesNo = [C("Yes", "Yes"), C("No", "No"), C("NotApplicable", "Not applicable")];

    private static readonly DisclosureChoiceDto[] Bases =
    [
        C("DirectLabor", "Direct labor dollars"),
        C("DirectLaborPlusFringe", "Direct labor plus fringe"),
        C("TotalCostInput", "Total cost input"),
        C("ValueAdded", "Value added (total cost input less materials and subcontracts)"),
        C("NotApplicable", "No such pool"),
    ];

    public static readonly IReadOnlyList<(int Number, string Roman, string Title)> Ds1Parts =
    [
        (1, "I", "General information"),
        (2, "II", "Direct costs"),
        (3, "III", "Direct vs. indirect costs"),
        (4, "IV", "Indirect costs"),
        (5, "V", "Depreciation and capitalization"),
        (6, "VI", "Other costs and credits"),
        (7, "VII", "Deferred compensation and insurance costs"),
        (8, "VIII", "Home office expenses"),
    ];

    public static readonly IReadOnlyList<DisclosureItem> Ds1 =
    [
        new("1.1.0", 1, "Type of business entity.", [C("Corporation", "Corporation"), C("Partnership", "Partnership"), C("SoleProprietorship", "Sole proprietorship"), C("Other", "Other (describe)")]),
        new("1.2.0", 1, "Government sales as a share of total sales, and the cost accounting standards coverage (full or modified)."),
        new("1.3.0", 1, "Type of cost system.", [C("JobOrder", "Job order"), C("Process", "Process"), C("Both", "Both")]),
        new("1.4.0", 1, "Use of standard costs.", [C("None", "No standard costs"), C("Labor", "Labor only"), C("Material", "Material only"), C("Both", "Labor and material")]),
        new("1.5.0", 1, "How the cost accounting system is integrated with the general ledger (Workbench posts labor, ODCs, AP and allocations to the ledger)."),
        new("1.6.0", 1, "Fiscal year and accounting periods."),

        new("2.1.0", 2, "Description of direct material: what is charged directly to contracts."),
        new("2.2.0", 2, "Method of charging direct material.", [C("ActualCost", "Actual cost"), C("Standard", "Standard cost"), C("Average", "Average cost"), C("Other", "Other (describe)")]),
        new("2.3.0", 2, "When direct material is charged (on receipt, on issue to the job, on vendor invoice)."),
        new("2.5.0", 2, "Description of direct labor: the employees and functions charged directly."),
        new("2.6.0", 2, "Method of charging direct labor.", [C("IndividualActual", "Individual (actual) rates"), C("Average", "Average rates"), C("Standard", "Standard rates")], DisclosureLink.LaborChargingMethod),
        new("2.7.0", 2, "Treatment of variances from standard or average rates."),

        new("3.1.0", 3, "Criteria for charging costs directly or indirectly (CAS 402: costs incurred for the same purpose in like circumstances are treated the same way)."),
        new("3.2.0(a)", 3, "Uncompensated overtime of salaried exempt employees: how hours above the standard week are costed.",
            [C("Standard", "Standard: hours above the standard week cost nothing"), C("TotalTimeEffectiveRate", "Total time accounting: salary spread over every hour worked"), C("Hybrid", "Effective rate only in periods with uncompensated hours")],
            DisclosureLink.UncompensatedOvertime),
        new("3.2.0(b)", 3, "Treatment of other specific functions or costs (for example travel, training, recruiting)."),
        new("3.3.0", 3, "Treatment of credits (rebates, refunds, returns) and whether they follow the original charge."),

        new("4.1.0(a)", 4, "Fringe benefit pool: allocation base.", Bases, DisclosureLink.FringeBase),
        new("4.1.0(b)", 4, "Overhead pool: allocation base.", Bases, DisclosureLink.OverheadBase),
        new("4.1.0(c)", 4, "Costs in each overhead pool (the expense accounts and indirect labor that form it)."),
        new("4.2.0", 4, "Service centers and how they're charged out."),
        new("4.3.0", 4, "General and administrative (G&A) pool: allocation base (CAS 410).", Bases, DisclosureLink.GaBase),
        new("4.4.0", 4, "How indirect rates are applied during the year (provisional billing rates) and adjusted at year end."),
        new("4.5.0", 4, "Independent research and development and bid and proposal costs (CAS 420): accumulation and allocation."),

        new("5.1.0", 5, "Depreciation methods for contract costing (CAS 409).",
            [C("StraightLine", "Straight line"), C("DecliningBalance", "Declining balance"), C("SumOfYearsDigits", "Sum-of-years digits"), C("Mixed", "Different methods by asset category")],
            DisclosureLink.DepreciationMethods),
        new("5.2.0", 5, "How useful lives are determined (experience, guidelines)."),
        new("5.3.0", 5, "Treatment of residual (salvage) value."),
        new("5.4.0", 5, "Treatment of gains and losses on disposal."),
        new("5.5.0(a)", 5, "Minimum dollar amount for capitalization (CAS 404).", [], DisclosureLink.CapitalizationThreshold, IsAmount: true),
        new("5.5.0(b)", 5, "Minimum service life for capitalization, and treatment of group (mass) purchases."),

        new("6.1.0", 6, "Vacation, holiday and sick pay (CAS 408): accrued when earned or charged when taken."),
        new("6.2.0", 6, "Severance pay and other employee costs."),
        new("6.3.0", 6, "Unallowable costs (CAS 405): how they're identified and kept out of claims and proposals.",
            [C("SeparateAccounts", "Recorded in separate unallowable accounts"), C("Memorandum", "Identified by memorandum records"), C("NotSegregated", "Not segregated")],
            DisclosureLink.UnallowableCosts),
        new("6.4.0", 6, "Treatment of other credits and income (for example interest, rentals)."),

        new("7.1.0", 7, "Pension plans (CAS 412 and 413): kinds of plans and how costs are measured and assigned.", YesNo),
        new("7.2.0", 7, "Deferred compensation other than pensions (CAS 415)."),
        new("7.3.0", 7, "Insurance and self-insurance (CAS 416)."),
        new("7.4.0", 7, "Employee stock ownership plans."),

        new("8.1.0", 8, "Does a home office allocate expenses to this segment (CAS 403)?", YesNo),
        new("8.2.0", 8, "Home office expense pools and allocation bases (residual expenses, service functions)."),
    ];

    public static readonly IReadOnlyList<(int Number, string Roman, string Title)> Ds2Parts =
    [
        (1, "I", "General information"),
        (2, "II", "Direct costs"),
        (3, "III", "Indirect costs"),
        (4, "IV", "Depreciation and use allowances"),
        (5, "V", "Other costs and credits"),
        (6, "VI", "Deferred compensation and insurance costs"),
        (7, "VII", "Central system or group expenses"),
        (8, "VIII", "Other"),
    ];

    public static readonly IReadOnlyList<DisclosureItem> Ds2 =
    [
        new("1.1.0", 1, "Type of institution and organizational structure."),
        new("2.1.0", 2, "Criteria for charging costs directly."),
        new("2.2.0", 2, "Method of charging direct salaries and wages.", [C("IndividualActual", "Individual (actual) rates"), C("Average", "Average rates"), C("Standard", "Standard rates")], DisclosureLink.LaborChargingMethod),
        new("3.1.0", 3, "Indirect cost pools and allocation bases."),
        new("4.1.0", 4, "Depreciation methods and use allowances.",
            [C("StraightLine", "Straight line"), C("DecliningBalance", "Declining balance"), C("SumOfYearsDigits", "Sum-of-years digits"), C("Mixed", "Different methods by asset category")],
            DisclosureLink.DepreciationMethods),
        new("4.2.0", 4, "Minimum dollar amount for capitalization.", [], DisclosureLink.CapitalizationThreshold, IsAmount: true),
        new("5.1.0", 5, "Unallowable costs: how they're identified and excluded.",
            [C("SeparateAccounts", "Recorded in separate unallowable accounts"), C("Memorandum", "Identified by memorandum records"), C("NotSegregated", "Not segregated")],
            DisclosureLink.UnallowableCosts),
        new("6.1.0", 6, "Pension plans and other deferred compensation."),
        new("7.1.0", 7, "Central system or group expenses allocated to this institution."),
        new("8.1.0", 8, "Other information."),
    ];

    public static IReadOnlyList<DisclosureItem> For(DisclosureFormType form) => form == DisclosureFormType.Ds2 ? Ds2 : Ds1;

    public static IReadOnlyList<(int Number, string Roman, string Title)> PartsFor(DisclosureFormType form) => form == DisclosureFormType.Ds2 ? Ds2Parts : Ds1Parts;

    public static string LinkLabel(DisclosureLink link) => link switch
    {
        DisclosureLink.LaborChargingMethod => "Labor costing (each employee's own cost rate)",
        DisclosureLink.UncompensatedOvertime => "Labor cost method (total time accounting)",
        DisclosureLink.FringeBase => "Fringe pool base (indirect rates setup)",
        DisclosureLink.OverheadBase => "Overhead pool base (indirect rates setup)",
        DisclosureLink.GaBase => "G&A pool base (indirect rates setup)",
        DisclosureLink.DepreciationMethods => "Asset category depreciation methods",
        DisclosureLink.CapitalizationThreshold => "Capitalization threshold (fixed assets)",
        DisclosureLink.UnallowableCosts => "Unallowable cost accounts (chart of accounts)",
        _ => "",
    };
}
