using System.Globalization;
using Workbench.Application.IndirectRates;
using Workbench.Contracts.IncurredCost;
using Workbench.Domain.Accounting;

namespace Workbench.Application.IncurredCost;

/// <summary>What the Schedule N certificate shows once the package is certified.</summary>
internal sealed record IceCertification(string Name, string Title, string CertifiedBy, DateTime CertifiedAtUtc, string Text);

internal sealed record IceBuild(
    IReadOnlyList<IceScheduleDto> Schedules,
    IReadOnlyList<IceCheckDto> Checks,
    IReadOnlyList<IceTotalDto> Totals,
    IReadOnlyList<string> Notes,
    IceInputs Inputs)
{
    public IceScheduleDto Schedule(string code) => Schedules.First(s => s.Code == code);
}

/// <summary>
/// Builds the ICE schedules from a package's frozen facts and its typed-in values. Pure: the same facts and inputs always
/// give the same schedules, so a submitted package reads the same forever and a regenerated draft can be compared.
/// <para>
/// The claim: allowable direct cost from the GL's direct accounts (by project, hence contract) and the indirect pools from
/// the accounts the pool setups resolve to, less unallowable cost (accounts classed unallowable, ODCs flagged unallowable
/// wherever they posted, and adjustments typed into B and C). Claimed rates are pool ÷ base in cascade order; each
/// contract's indirect cost is its share of the pool by base (cumulatively rounded, so the shares add up to the pool).
/// </para>
/// </summary>
internal static class IceBuilder
{
    public const string None = "none";
    public const string Unassigned = "unassigned";

    /// <summary>Schedules in the order of the DCAA ICE model (U, the unallowable costs, last).</summary>
    public static readonly string[] Order = ["Cover", "A", "B", "C", "D", "E", "F", "G", "H", "H-1", "I", "J", "K", "L", "M", "N", "O", "U"];

    private static readonly string[] CostType = ["CostPlusFixedFee", "CostPlusAwardFee", "CostPlusIncentiveFee"];
    private static readonly string[] TimeAndMaterialsTypes = ["TimeAndMaterials", "LaborHour"];

    public static bool IsFlexiblyPriced(string type) => type != "FirmFixedPrice";

    public static IceBuild Build(IceSource s, IceInputs inputs, IceCertification? certification = null) => new Context(s, inputs, certification).Run();

    public static string CertificateText(string firm, int fiscalYear, DateOnly start, DateOnly end) =>
        "This is to certify that I have reviewed this proposal to establish final indirect cost rates and, to the best of my knowledge and belief:\n\n" +
        $"1. All costs included in this proposal (FY{fiscalYear}, {Date(start)} to {Date(end)}) to establish final indirect cost rates and billing rates are " +
        "allowable in accordance with the cost principles of the Federal Acquisition Regulation (FAR) and its supplements applicable to the contracts " +
        "to which the final indirect cost rates will apply; and\n\n" +
        "2. This proposal does not include any costs which are expressly unallowable under applicable cost principles of the FAR or its supplements.\n\n" +
        $"Firm: {firm}";

    public static string PoolLabel(string pool) => pool == nameof(IndirectPool.GeneralAndAdministrative) ? "G&A" : pool;

    public static string TypeLabel(string type) => type switch
    {
        "FirmFixedPrice" => "FFP",
        "TimeAndMaterials" => "T&M",
        "LaborHour" => "LH",
        "CostPlusFixedFee" => "CPFF",
        "CostPlusAwardFee" => "CPAF",
        "CostPlusIncentiveFee" => "CPIF",
        _ => type,
    };

    private static string Date(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal v) => v.ToString("#,##0.00;(#,##0.00)", CultureInfo.GetCultureInfo("en-US"));

    private static IceColumnDto Text(string key, string header, bool editable = false) => new(key, header, IceColumnKind.Text, editable);

    private static IceColumnDto Amount(string key, string header, bool editable = false) => new(key, header, IceColumnKind.Amount, editable);

    private static IceColumnDto Rate(string key, string header) => new(key, header, IceColumnKind.Rate);

    private static IceColumnDto Hours(string key, string header) => new(key, header, IceColumnKind.Hours);

    private static IceColumnDto DateColumn(string key, string header) => new(key, header, IceColumnKind.Date);

    /// <summary>A contract (or the "no contract" / "no project" bucket) that direct and indirect cost are claimed on.</summary>
    private sealed record Bucket(string Key, string Label, string Name, string Customer, string Type, IceContract? Contract);

    private sealed class Table(string code, string title, string description, IReadOnlyList<IceColumnDto> columns)
    {
        public string Code => code;
        public List<IceRowDto> Rows { get; } = [];
        public List<string> Notes { get; } = [];

        /// <summary>Adds a row; the returned cells stay live, so they can be filled after adding.</summary>
        public Cells Add(string key, IceRowKind kind = IceRowKind.Detail)
        {
            var cells = new Cells();
            Rows.Add(new IceRowDto(key, kind, cells.Text, cells.Numbers));
            return cells;
        }

        public IceScheduleDto ToDto() => new(code, title, description, columns, Rows, Notes);
    }

    private sealed class Cells
    {
        public Dictionary<string, string?> Text { get; } = [];
        public Dictionary<string, decimal> Numbers { get; } = [];

        public Cells T(string column, string? value)
        {
            Text[column] = value;
            return this;
        }

        public Cells N(string column, decimal value)
        {
            Numbers[column] = value;
            return this;
        }

        public Cells N(string column, decimal? value) => value is { } v ? N(column, v) : this;
    }

    private sealed class Context(IceSource s, IceInputs inputs, IceCertification? certification)
    {
        private readonly List<IceScheduleDto> _schedules = [];
        private readonly List<IceCheckDto> _checks = [];
        private readonly List<IceTotalDto> _totals = [];
        private readonly List<string> _notes = [];

        // Computed along the way and shared between schedules.
        private List<Bucket> _buckets = [];
        private Dictionary<string, decimal> _labor = [];
        private Dictionary<string, Dictionary<Guid, decimal>> _odc = [];
        private Dictionary<string, decimal> _poolClaimed = [];
        private Dictionary<string, Dictionary<string, decimal>> _allocated = [];
        private Dictionary<string, decimal> _cumulative = [];
        private Dictionary<string, decimal> _fee = [];
        private decimal _adjustments;
        private readonly HashSet<string> _unallocatable = [];
        private string _firm = "";

        private void Total(string schedule, string key, string label, decimal value) => _totals.Add(new IceTotalDto(schedule, key, label, value));

        private void Check(string code, IceCheckSeverity severity, string schedule, string title, string detail, decimal? expected = null, decimal? actual = null) =>
            _checks.Add(new IceCheckDto(code, severity, schedule, title, detail, expected, actual));

        private decimal Direct(string bucket) => _labor.GetValueOrDefault(bucket) + (_odc.TryGetValue(bucket, out var o) ? o.Values.Sum() : 0);

        private decimal Indirect(string bucket) => _allocated.Values.Sum(a => a.GetValueOrDefault(bucket));

        private decimal Claimed(string bucket) => Direct(bucket) + Indirect(bucket);

        private IReadOnlyList<IceAccount> DirectOdcAccounts => s.Accounts.Where(a => a.Category == IceCategory.DirectOdc && s.Direct.Any(d => d.AccountId == a.Id)).ToList();

        private IEnumerable<IcePool> Pools => s.Pools.OrderBy(p => p.CascadeOrder).ThenBy(p => p.Pool, StringComparer.Ordinal);

        private static string PoolSchedule(string pool) => pool == nameof(IndirectPool.GeneralAndAdministrative) ? "B" : "C";

        public IceBuild Run()
        {
            Buckets();
            var cover = Cover();
            PoolSchedules(out var b, out var c);
            Allocate();
            var a = ScheduleA();
            var d = ScheduleD();
            var e = ScheduleE();
            var i = ScheduleI();
            var f = ScheduleF();
            var g = ScheduleG();
            var h = ScheduleH();
            var h1 = ScheduleH1();
            var j = ScheduleJ();
            var k = ScheduleK();
            var l = ScheduleL();
            var m = ScheduleM();
            var n = ScheduleN();
            var o = ScheduleO();
            var u = ScheduleU();
            _schedules.AddRange([cover, a, b, c, d, e, f, g, h, h1, i, j, k, l, m, n, o, u]);
            Completeness();
            return new IceBuild(_schedules, _checks.OrderByDescending(x => x.Severity).ToList(), _totals, _notes, inputs);
        }

        // ---- Buckets: contracts with cost, billing or a ceiling, then "no contract" and "no project" ----

        private void Buckets()
        {
            var projects = s.Projects.ToDictionary(p => p.Id);
            string KeyOf(Guid? projectId) => projectId is { } id && projects.TryGetValue(id, out var p) ? p.ContractId?.ToString() ?? None : Unassigned;

            foreach (var d in s.Direct)
            {
                var key = KeyOf(d.ProjectId);
                var account = s.Accounts.First(a => a.Id == d.AccountId);
                if (account.Category == IceCategory.DirectLabor)
                {
                    _labor[key] = _labor.GetValueOrDefault(key) + d.Amount;
                }
                else
                {
                    var odc = _odc.TryGetValue(key, out var x) ? x : _odc[key] = [];
                    odc[d.AccountId] = odc.GetValueOrDefault(d.AccountId) + d.Amount;
                }
            }

            var withCost = _labor.Keys.Concat(_odc.Keys).ToHashSet();
            foreach (var contract in s.Contracts)
            {
                var key = contract.Id.ToString();
                if (withCost.Contains(key) || s.Billing.Any(x => x.ContractId == contract.Id) || IsActiveIn(contract))
                {
                    _buckets.Add(new Bucket(key, contract.Number, contract.Name, contract.Customer, contract.Type, contract));
                }
            }

            if (withCost.Contains(None))
            {
                _buckets.Add(new Bucket(None, "No contract", "Projects without a contract", "", "", null));
            }

            if (withCost.Contains(Unassigned))
            {
                _buckets.Add(new Bucket(Unassigned, "No project", "Direct cost posted without a project", "", "", null));
            }
        }

        private bool IsActiveIn(IceContract c) => c.PeriodStart <= s.End && c.PeriodEnd >= s.Start;

        private IEnumerable<Bucket> ContractBuckets => _buckets.Where(b => b.Contract is not null);

        // ---- Cover: who is submitting ----

        private IceScheduleDto Cover()
        {
            var t = new Table("Cover", "Contractor information", "Identifies the contractor and the fiscal year covered by this submission.",
                [Text("item", "Item"), Text("value", "Value", editable: true)]);
            var cage = s.Contracts.Select(c => c.CageCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            string? Field(string key, string label, string? fallback = null)
            {
                var value = inputs.Text("Cover", key, "value", fallback);
                t.Add(key).T("item", label).T("value", value);
                return value;
            }

            _firm = Field("contractor", "Contractor name") ?? "";
            Field("address", "Address");
            Field("cage", "CAGE code", cage);
            Field("uei", "Unique entity ID (UEI)");
            Field("dcaa", "Cognizant DCAA office");
            Field("aco", "Cognizant ACO / DCMA office");
            Field("contact", "Contact name");
            Field("contactPhone", "Contact phone");
            Field("contactEmail", "Contact email");
            t.Add("fy").T("item", "Fiscal year").T("value", $"FY{s.FiscalYear}");
            t.Add("period").T("item", "Fiscal year period").T("value", $"{Date(s.Start)} to {Date(s.End)}");
            t.Notes.Add("Amounts throughout are in U.S. dollars, from the general ledger for the fiscal year (posted entries, reversals included).");
            return t.ToDto();
        }

        // ---- B and C: the pools by account, less unallowable cost ----

        private void PoolSchedules(out IceScheduleDto b, out IceScheduleDto c)
        {
            IceColumnDto[] columns =
            [
                Text("account", "Account"), Text("name", "Description"), Amount("books", "Per books"),
                Amount("flagged", "Flagged unallowable ODCs"), Amount("adjustment", "Other unallowable / adjustments", editable: true), Amount("claimed", "Claimed"),
            ];
            var tb = new Table("B", "General and administrative expenses", "The G&A pool by account: per books, less unallowable costs (FAR 31.205), as claimed.", columns);
            var tc = new Table("C", "Overhead and fringe pools", "The fringe and overhead pools by account: per books, less unallowable costs (FAR 31.205), as claimed.", columns);
            foreach (var pool in Pools)
            {
                var t = PoolSchedule(pool.Pool) == "B" ? tb : tc;
                if (t == tc)
                {
                    t.Add($"section:{pool.Pool}", IceRowKind.Section).T("account", $"{pool.Name} pool");
                }

                decimal books = 0, flagged = 0, adjust = 0;
                foreach (var account in s.Accounts.Where(x => x.Pool == pool.Pool))
                {
                    var adjustment = inputs.Number(t.Code, account.Id.ToString(), "adjustment");
                    t.Add(account.Id.ToString()).T("account", account.Number).T("name", account.Name)
                        .N("books", account.Balance).N("flagged", account.Flagged).N("adjustment", adjustment)
                        .N("claimed", account.Balance - account.Flagged - adjustment);
                    books += account.Balance;
                    flagged += account.Flagged;
                    adjust += adjustment;
                }

                _poolClaimed[pool.Pool] = books - flagged - adjust;
                _adjustments += adjust;
                t.Add($"total:{pool.Pool}", t == tb ? IceRowKind.Total : IceRowKind.Subtotal).T("account", $"Total {pool.Name} pool")
                    .N("books", books).N("flagged", flagged).N("adjustment", adjust).N("claimed", _poolClaimed[pool.Pool]);
                Total(t.Code, $"claimed:{pool.Pool}", $"{pool.Name} pool claimed", _poolClaimed[pool.Pool]);
            }

            if (tc.Rows.Count > 0)
            {
                var subtotals = tc.Rows.Where(r => r.Kind == IceRowKind.Subtotal).ToList();
                tc.Add("total", IceRowKind.Total).T("account", "Total overhead and fringe")
                    .N("books", subtotals.Sum(r => r.Number("books"))).N("flagged", subtotals.Sum(r => r.Number("flagged")))
                    .N("adjustment", subtotals.Sum(r => r.Number("adjustment"))).N("claimed", subtotals.Sum(r => r.Number("claimed")));
            }

            foreach (var t in new[] { tb, tc })
            {
                t.Notes.Add("Type other unallowable costs found in an account (FAR 31.205), or other adjustments, in the adjustments column; they move to the unallowable schedule.");
            }

            if (s.Pools.Count == 0)
            {
                tb.Notes.Add("No indirect pools are set up (Accounting › Indirect rates).");
                tc.Notes.Add("No indirect pools are set up (Accounting › Indirect rates).");
            }

            b = tb.ToDto();
            c = tc.ToDto();
        }

        // ---- Allocation of the claimed pools to contracts ----

        private void Allocate()
        {
            var earlier = _buckets.ToDictionary(x => x.Key, _ => new Dictionary<IndirectPool, decimal>());
            foreach (var pool in Pools)
            {
                var p = Enum.Parse<IndirectPool>(pool.Pool);
                var basis = Enum.Parse<AllocationBase>(pool.Base);
                var bases = _buckets.ToDictionary(x => x.Key, x => BurdenCascade.BaseOf(basis, _labor.GetValueOrDefault(x.Key),
                    _odc.TryGetValue(x.Key, out var o) ? o.Values.Sum() : 0, earlier[x.Key]));
                var total = bases.Values.Sum();
                var claimed = _poolClaimed.GetValueOrDefault(pool.Pool);
                if (total == 0)
                {
                    _unallocatable.Add(pool.Pool); // Schedule A warns; nothing can be spread over a zero base.
                }

                var shares = new Dictionary<string, decimal>();
                decimal running = 0, given = 0;
                foreach (var bucket in _buckets)
                {
                    running += bases[bucket.Key];
                    var cumulative = total == 0 ? 0 : decimal.Round(claimed * running / total, 2, MidpointRounding.AwayFromZero);
                    shares[bucket.Key] = cumulative - given;
                    given = cumulative;
                    earlier[bucket.Key][p] = shares[bucket.Key];
                }

                _allocated[pool.Pool] = shares;
            }
        }

        // ---- A: claimed rates ----

        private IceScheduleDto ScheduleA()
        {
            var t = new Table("A", "Summary of claimed indirect rates", "Each pool's claimed cost over its allocation base, in cascade order, with the year's provisional billing rates.",
                [Text("pool", "Pool"), Text("base", "Allocation base"), Amount("poolCost", "Claimed pool"), Amount("baseAmount", "Allocation base amount"),
                 Rate("claimedRate", "Claimed rate"), Rate("provisionalRate", "Provisional rate"), Rate("variance", "Claimed − provisional"),
                 Amount("applied", "Applied by allocations"), Amount("underApplied", "Under / (over) applied")]);
            var labor = _labor.Values.Sum();
            var odc = _odc.Values.Sum(o => o.Values.Sum());
            var steps = Pools.Select(p => new PoolStep(Enum.Parse<IndirectPool>(p.Pool), Enum.Parse<AllocationBase>(p.Base))).ToList();
            var rates = BurdenCascade.ActualRates(steps, labor, odc, p => _poolClaimed.GetValueOrDefault(p.ToString())).ToDictionary(r => r.Pool.ToString());
            foreach (var pool in Pools)
            {
                var (_, baseAmount, rate) = rates[pool.Pool];
                var claimed = _poolClaimed.GetValueOrDefault(pool.Pool);
                t.Add(pool.Pool).T("pool", pool.Name).T("base", BaseLabel(pool.Base)).N("poolCost", claimed).N("baseAmount", baseAmount)
                    .N("claimedRate", rate).N("provisionalRate", pool.Provisional)
                    .N("variance", rate is { } r && pool.Provisional is { } pr ? r - pr : null)
                    .N("applied", pool.Applied).N("underApplied", claimed - pool.Applied);
                Total("A", $"rate:{pool.Pool}", $"{pool.Name} claimed rate", rate ?? 0);
                if (rate is null && claimed != 0)
                {
                    Check($"A-BASE-{pool.Pool}", IceCheckSeverity.Warning, "A", $"{pool.Name} pool has no base",
                        $"The {pool.Name} pool has {Money(claimed)} of cost but its allocation base is zero, so no rate can be claimed and nothing is allocated to contracts.");
                }
            }

            var claimedCost = labor + odc + _poolClaimed.Values.Sum();
            t.Add("direct", IceRowKind.Subtotal).T("pool", "Total direct cost claimed").N("poolCost", labor + odc);
            t.Add("total", IceRowKind.Total).T("pool", "Total allowable cost claimed (direct + pools)").N("poolCost", claimedCost);
            t.Notes.Add("Fringe is spread over direct labor; overhead over direct labor + fringe; G&A over total cost input, as set up under Accounting › Indirect rates.");
            if (Pools.Any(p => p.Actual is not null))
            {
                t.Notes.Add("Actual rates already recorded for the year: " + string.Join(", ", Pools.Where(p => p.Actual is not null).Select(p => $"{p.Name} {p.Actual:P4}")) + ".");
            }

            if (s.Pools.Count == 0)
            {
                Check("A-POOLS", IceCheckSeverity.Warning, "A", "No indirect pools", "No indirect pools are set up, so no indirect rates are claimed and pool accounts show as other expenses on Schedule G.");
            }

            Total("A", "claimedCost", "Total allowable cost claimed", claimedCost);
            return t.ToDto();
        }

        private static string BaseLabel(string basis) => basis switch
        {
            nameof(AllocationBase.DirectLabor) => "Direct labor",
            nameof(AllocationBase.DirectLaborPlusFringe) => "Direct labor + fringe",
            nameof(AllocationBase.TotalCostInput) => "Total cost input",
            nameof(AllocationBase.ValueAdded) => "Value added",
            _ => basis,
        };

        // ---- D: direct cost by contract ----

        private IceScheduleDto ScheduleD()
        {
            var t = new Table("D", "Claimed direct costs by contract", "Allowable direct cost incurred in the year on each contract (unallowable direct costs excluded).",
                [Text("contract", "Contract"), Text("customer", "Customer"), Text("type", "Type"), Amount("labor", "Direct labor"), Amount("odc", "Other direct costs"), Amount("total", "Total direct")]);
            foreach (var bucket in _buckets)
            {
                var odc = _odc.TryGetValue(bucket.Key, out var o) ? o.Values.Sum() : 0;
                t.Add(bucket.Key).T("contract", bucket.Label).T("customer", bucket.Customer).T("type", TypeLabel(bucket.Type))
                    .N("labor", _labor.GetValueOrDefault(bucket.Key)).N("odc", odc).N("total", Direct(bucket.Key));
            }

            var labor = _labor.Values.Sum();
            var totalOdc = _odc.Values.Sum(o => o.Values.Sum());
            t.Add("total", IceRowKind.Total).T("contract", "Total").N("labor", labor).N("odc", totalOdc).N("total", labor + totalOdc);
            Total("D", "direct", "Total direct cost claimed", labor + totalOdc);
            if (_labor.ContainsKey(Unassigned) || _odc.ContainsKey(Unassigned))
            {
                Check("D-UNASSIGNED", IceCheckSeverity.Warning, "D", "Direct cost without a project",
                    $"{Money(Direct(Unassigned))} was posted to direct cost accounts without a project, so it can't be claimed on a contract. Reclassify it with a journal entry.");
            }

            return t.ToDto();
        }

        // ---- E: indirect cost by contract ----

        private IceScheduleDto ScheduleE()
        {
            var columns = new List<IceColumnDto> { Text("contract", "Contract"), Text("type", "Type") };
            columns.AddRange(Pools.Select(p => Amount($"pool:{p.Pool}", p.Name)));
            columns.AddRange([Amount("indirect", "Total indirect"), Amount("direct", "Direct (Schedule D)"), Amount("claimed", "Total claimed")]);
            var t = new Table("E", "Claimed indirect costs by contract", "Each contract's share of the claimed pools, by its share of each allocation base.", columns);
            foreach (var bucket in _buckets)
            {
                var row = t.Add(bucket.Key).T("contract", bucket.Label).T("type", TypeLabel(bucket.Type));
                foreach (var pool in Pools)
                {
                    row.N($"pool:{pool.Pool}", _allocated[pool.Pool].GetValueOrDefault(bucket.Key));
                }

                row.N("indirect", Indirect(bucket.Key)).N("direct", Direct(bucket.Key)).N("claimed", Claimed(bucket.Key));
            }

            var total = t.Add("total", IceRowKind.Total).T("contract", "Total");
            foreach (var pool in Pools)
            {
                total.N($"pool:{pool.Pool}", _allocated[pool.Pool].Values.Sum());
            }

            var indirect = _buckets.Sum(x => Indirect(x.Key));
            var claimed = _buckets.Sum(x => Claimed(x.Key));
            total.N("indirect", indirect).N("direct", _buckets.Sum(x => Direct(x.Key))).N("claimed", claimed);
            Total("E", "indirect", "Total indirect cost allocated", indirect);
            Total("E", "claimed", "Total cost claimed on contracts", claimed);

            var pools = _poolClaimed.Where(kv => !_unallocatable.Contains(kv.Key)).Sum(kv => kv.Value);
            if (indirect != pools && _buckets.Count > 0)
            {
                Check("E-ALLOCATION", IceCheckSeverity.Error, "E", "Allocated indirect cost doesn't equal the pools",
                    "The indirect cost spread over contracts differs from the claimed pools (Schedules B and C).", pools, indirect);
            }

            if (_unallocatable.Count > 0)
            {
                t.Notes.Add($"Not allocated (zero base, see Schedule A): {string.Join(", ", Pools.Where(p => _unallocatable.Contains(p.Pool)).Select(p => p.Name))}.");
            }

            if (_buckets.Any(x => x.Key == None))
            {
                t.Notes.Add("\"No contract\" is direct cost on projects without a contract (for example commercial or IR&D projects); it carries its share of the pools.");
            }

            return t.ToDto();
        }

        // ---- I: cumulative allowable cost on flexibly priced contracts ----

        private IceScheduleDto ScheduleI()
        {
            var t = new Table("I", "Cumulative allowable cost by flexibly priced contract",
                "Allowable cost claimed to date on each cost-type, T&M and labor-hour contract, against its value and what has been billed.",
                [Text("contract", "Contract"), Text("type", "Type"), Amount("prior", "Prior years claimed", editable: true), Amount("current", "Current year claimed"),
                 Amount("cumulative", "Cumulative claimed"), Amount("fee", "Fee (current year)", editable: true), Amount("value", "Contract value"),
                 Amount("billedCost", "Cumulative billed cost"), Amount("billedFee", "Cumulative billed fee"), Amount("difference", "Billed over / (under) claimed")]);
            foreach (var bucket in ContractBuckets.Where(x => IsFlexiblyPriced(x.Type)))
            {
                var c = bucket.Contract!;
                var current = Claimed(bucket.Key);
                var prior = inputs.Number("I", bucket.Key, "prior", s.PriorCumulative.GetValueOrDefault(c.Id));
                var defaultFee = CostType.Contains(c.Type) && c.FeeRate is { } rate ? decimal.Round(current * rate, 2, MidpointRounding.AwayFromZero) : 0;
                var fee = inputs.Number("I", bucket.Key, "fee", defaultFee);
                var billing = s.Billing.FirstOrDefault(x => x.ContractId == c.Id);
                _cumulative[bucket.Key] = prior + current;
                _fee[bucket.Key] = fee;
                t.Add(bucket.Key).T("contract", bucket.Label).T("type", TypeLabel(c.Type)).N("prior", prior).N("current", current)
                    .N("cumulative", prior + current).N("fee", fee).N("value", c.Ceiling)
                    .N("billedCost", billing?.CumulativeCost ?? 0).N("billedFee", billing?.CumulativeFee ?? 0)
                    .N("difference", (billing?.CumulativeCost ?? 0) - (prior + current));
            }

            Totals(t, "contract", ["prior", "current", "cumulative", "fee", "value", "billedCost", "billedFee", "difference"]);
            Total("I", "cumulative", "Cumulative claimed (flexibly priced)", _cumulative.Values.Sum());
            t.Notes.Add(s.PriorPackageFound
                ? $"Prior years claimed comes from the FY{s.FiscalYear - 1} package; type over it where that isn't right."
                : $"There's no FY{s.FiscalYear - 1} package in Workbench: type each contract's cumulative claimed cost through the prior year.");
            t.Notes.Add("Fee defaults to the CLIN fee rate × current year cost on cost-plus contracts; type the fee earned where it differs (award and incentive fees).");
            return t.ToDto();
        }

        private static void Totals(Table t, string labelColumn, IReadOnlyList<string> columns)
        {
            var details = t.Rows.Where(r => r.Kind == IceRowKind.Detail).ToList();
            var total = t.Add("total", IceRowKind.Total).T(labelColumn, "Total");
            foreach (var column in columns)
            {
                total.N(column, details.Sum(r => r.Number(column)));
            }
        }

        // ---- F: ceilings ----

        private IceScheduleDto ScheduleF()
        {
            var t = new Table("F", "Contract and subcontract ceilings",
                "Ceilings on flexibly priced contracts and subcontracts, against the cost claimed to date. Type any rate or cost ceilings the contract sets.",
                [Text("contract", "Contract / subcontract"), Text("type", "Type"), Amount("ceiling", "Ceiling (value)"), Amount("funded", "Funded"),
                 Amount("cumulative", "Cumulative cost"), Amount("over", "Over ceiling"), Text("rateCeilings", "Indirect rate ceilings", editable: true),
                 Text("notes", "Notes", editable: true)]);
            t.Add("section:contracts", IceRowKind.Section).T("contract", "Prime contracts");
            decimal over = 0;
            foreach (var bucket in ContractBuckets.Where(x => IsFlexiblyPriced(x.Type)))
            {
                var c = bucket.Contract!;
                var cumulative = _cumulative.GetValueOrDefault(bucket.Key);
                var excess = c.Ceiling > 0 ? Math.Max(0, cumulative - c.Ceiling) : 0;
                over += excess;
                t.Add(bucket.Key).T("contract", bucket.Label).T("type", TypeLabel(c.Type)).N("ceiling", c.Ceiling).N("funded", c.Funded)
                    .N("cumulative", cumulative).N("over", excess)
                    .T("rateCeilings", inputs.Text("F", bucket.Key, "rateCeilings")).T("notes", inputs.Text("F", bucket.Key, "notes"));
                if (excess > 0)
                {
                    Check($"F-OVER-{c.Number}", IceCheckSeverity.Warning, "F", $"{c.Number} is over its ceiling",
                        $"Cumulative claimed cost exceeds the contract value by {Money(excess)}; cost above the ceiling isn't recoverable without a modification.", c.Ceiling, cumulative);
                }
            }

            if (s.Subcontracts.Count > 0)
            {
                t.Add("section:subcontracts", IceRowKind.Section).T("contract", "Subcontracts");
                foreach (var sub in s.Subcontracts.Where(x => x.PeriodStart <= s.End && IsFlexiblyPriced(x.Pricing)))
                {
                    var excess = sub.Ceiling > 0 ? Math.Max(0, sub.CumulativeCost - sub.Ceiling) : 0;
                    over += excess;
                    var key = $"sub:{sub.Id}";
                    t.Add(key).T("contract", $"{sub.Number} ({sub.Vendor})").T("type", TypeLabel(sub.Pricing)).N("ceiling", sub.Ceiling).N("funded", sub.Funded)
                        .N("cumulative", sub.CumulativeCost).N("over", excess)
                        .T("rateCeilings", inputs.Text("F", key, "rateCeilings")).T("notes", inputs.Text("F", key, "notes"));
                }
            }

            t.Add("total", IceRowKind.Total).T("contract", "Total over ceilings").N("over", over);
            Total("F", "over", "Cost over ceilings", over);
            return t.ToDto();
        }

        // ---- G: reconciliation of total costs to the general ledger ----

        private IceScheduleDto ScheduleG()
        {
            var t = new Table("G", "Reconciliation of total costs to the general ledger",
                "Every dollar of expense on the books for the year, accounted for as claimed direct cost, claimed pools, unallowable cost or other.",
                [Text("line", "Line"), Text("source", "Source"), Amount("amount", "Amount")]);
            var direct = _labor.Values.Sum() + _odc.Values.Sum(o => o.Values.Sum());
            var unallowable = UnallowableTotal;
            var applied = s.Accounts.Where(a => a.Category == IceCategory.Applied).Sum(a => a.Balance);
            var others = s.Accounts.Where(a => a.Category == IceCategory.Other).ToList();
            var other = others.Sum(a => a.Balance - a.Flagged);

            t.Add("gl", IceRowKind.Subtotal).T("line", "Total expenses per general ledger (trial balance)").T("source", "GL").N("amount", s.GlExpenseTotal);
            t.Add("direct").T("line", "Direct costs claimed").T("source", "Schedule D").N("amount", direct);
            foreach (var pool in Pools)
            {
                t.Add($"pool:{pool.Pool}").T("line", $"{pool.Name} pool claimed").T("source", $"Schedule {PoolSchedule(pool.Pool)}").N("amount", _poolClaimed.GetValueOrDefault(pool.Pool));
            }

            t.Add("unallowable").T("line", "Unallowable costs (FAR 31.205)").T("source", "Schedule U").N("amount", unallowable);
            t.Add("applied").T("line", "Applied indirect cost, net (allocation entries and their contra accounts)").T("source", "GL").N("amount", applied);
            t.Add("other").T("line", "Other expense accounts, not claimed" + (others.Count > 0 ? $" ({string.Join(", ", others.Select(a => a.Number))})" : ""))
                .T("source", "GL").N("amount", other);
            var accounted = direct + _poolClaimed.Values.Sum() + unallowable + applied + other;
            t.Add("accounted", IceRowKind.Subtotal).T("line", "Total accounted for").N("amount", accounted);
            var difference = s.GlExpenseTotal - accounted;
            t.Add("difference", IceRowKind.Total).T("line", "Difference (must be zero)").N("amount", difference);
            Total("G", "gl", "Total expenses per GL", s.GlExpenseTotal);
            Total("G", "difference", "Unreconciled difference", difference);

            if (difference != 0)
            {
                Check("G-GL", IceCheckSeverity.Error, "G", "Schedule G doesn't reconcile to the general ledger",
                    $"The schedules account for {Money(accounted)} but the trial balance shows {Money(s.GlExpenseTotal)} of expense.", s.GlExpenseTotal, accounted);
            }
            else
            {
                Check("G-GL", IceCheckSeverity.Info, "G", "Schedule G reconciles to the general ledger",
                    $"Total expenses per the trial balance, {Money(s.GlExpenseTotal)}, are fully accounted for.", s.GlExpenseTotal, accounted);
            }

            if (applied != 0)
            {
                Check("G-APPLIED", IceCheckSeverity.Warning, "G", "Applied indirect cost doesn't net to zero",
                    $"The applied-burden accounts net to {Money(applied)}; allocation debits and their contra credits should offset. Check for one-sided entries.");
            }

            if (other != 0)
            {
                Check("G-OTHER", IceCheckSeverity.Warning, "G", "Expense accounts outside the claim",
                    $"{Money(other)} sits in expense accounts that aren't direct, in a pool or unallowable ({string.Join(", ", others.Where(a => a.Balance != 0).Select(a => $"{a.Number} {a.Name}"))}). Classify them or add them to a pool.");
            }

            if (s.DraftJournalEntries > 0)
            {
                Check("G-DRAFTS", IceCheckSeverity.Warning, "G", "Unposted journal entries",
                    $"{s.DraftJournalEntries} draft journal entr{(s.DraftJournalEntries == 1 ? "y is" : "ies are")} dated in the year and not in the claim. Post or delete them, then regenerate.");
            }

            if (s.OpenPeriods > 0)
            {
                Check("G-OPEN", IceCheckSeverity.Info, "G", "Open periods",
                    $"{s.OpenPeriods} of the year's {s.Periods} periods are still open, so the books can still change. Close the year before certifying.");
            }

            return t.ToDto();
        }

        // ---- H and H-1: direct cost by element, labor hours ----

        private IceScheduleDto ScheduleH()
        {
            var odcAccounts = DirectOdcAccounts;
            var columns = new List<IceColumnDto> { Text("contract", "Contract"), Text("type", "Type"), Amount("labor", "Direct labor") };
            columns.AddRange(odcAccounts.Select(a => Amount($"account:{a.Number}", a.Name)));
            columns.Add(Amount("total", "Total direct"));
            var t = new Table("H", "Direct costs by contract by element", "Allowable direct cost on each contract by cost element (GL account).", columns);
            foreach (var bucket in _buckets)
            {
                var row = t.Add(bucket.Key).T("contract", bucket.Label).T("type", TypeLabel(bucket.Type)).N("labor", _labor.GetValueOrDefault(bucket.Key));
                foreach (var a in odcAccounts)
                {
                    row.N($"account:{a.Number}", _odc.TryGetValue(bucket.Key, out var o) ? o.GetValueOrDefault(a.Id) : 0);
                }

                row.N("total", Direct(bucket.Key));
            }

            Totals(t, "contract", ["labor", .. odcAccounts.Select(a => $"account:{a.Number}"), "total"]);
            Total("H", "direct", "Total direct cost by element", t.Rows[^1].Number("total"));
            return t.ToDto();
        }

        private IceScheduleDto ScheduleH1()
        {
            var t = new Table("H-1", "Direct labor hours by contract", "Approved hours on direct charge codes in the year, by contract and labor category, at cost.",
                [Text("contract", "Contract"), Text("category", "Labor category"), Hours("hours", "Hours"), Hours("uncompensated", "Uncompensated hours (included)"),
                 Amount("cost", "Labor cost")]);
            var projects = s.Projects.ToDictionary(p => p.Id);
            var byBucket = s.Hours.GroupBy(h => projects.TryGetValue(h.ProjectId, out var p) ? p.ContractId?.ToString() ?? None : None).ToDictionary(g => g.Key);
            foreach (var bucket in _buckets.Where(x => byBucket.ContainsKey(x.Key)).Concat(byBucket.Keys.Where(k => _buckets.All(x => x.Key != k))
                         .Select(k => new Bucket(k, s.Contracts.FirstOrDefault(c => c.Id.ToString() == k)?.Number ?? "No contract", "", "", "", null))))
            {
                t.Add($"section:{bucket.Key}", IceRowKind.Section).T("contract", bucket.Label);
                var rows = byBucket[bucket.Key].GroupBy(h => h.LaborCategory).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                foreach (var category in rows)
                {
                    t.Add($"{bucket.Key}:{category.Key}").T("contract", bucket.Label).T("category", category.Key)
                        .N("hours", category.Sum(h => h.Hours)).N("uncompensated", category.Sum(h => h.UncompensatedHours)).N("cost", category.Sum(h => h.Cost));
                }

                t.Add($"subtotal:{bucket.Key}", IceRowKind.Subtotal).T("contract", $"Total {bucket.Label}")
                    .N("hours", byBucket[bucket.Key].Sum(h => h.Hours)).N("uncompensated", byBucket[bucket.Key].Sum(h => h.UncompensatedHours)).N("cost", byBucket[bucket.Key].Sum(h => h.Cost));
            }

            var hours = s.Hours.Sum(h => h.Hours);
            var cost = s.Hours.Sum(h => h.Cost);
            var uncompensated = s.Hours.Sum(h => h.UncompensatedHours);
            t.Add("total", IceRowKind.Total).T("contract", "Total").N("hours", hours).N("uncompensated", uncompensated).N("cost", cost);

            // DCAA: uncompensated overtime of exempt staff is recorded (total time accounting) and disclosed with the method used to cost it.
            t.Notes.Add("Hours are all hours worked and recorded, including uncompensated overtime: hours exempt salaried employees worked beyond their "
                + $"standard week without additional pay. Direct uncompensated hours: {uncompensated:N2}"
                + (s.UncompensatedHoursAll is { } all ? $"; on all charges, direct and indirect: {all:N2}." : "."));
            if (s.LaborCostMethod is { } method)
            {
                t.Notes.Add($"Uncompensated overtime costing method: {method}. Standard records uncompensated hours at no cost; total time accounting "
                    + "(effective rate) spreads each period's salary over all hours worked.");
            }
            Total("H-1", "hours", "Direct labor hours", hours);
            var gl = _labor.Values.Sum();
            if (cost != gl)
            {
                Check("H1-LABOR", IceCheckSeverity.Warning, "H-1", "Timesheet labor doesn't match the GL",
                    "Approved direct hours at cost differ from direct labor in the general ledger: timesheets not yet posted, labor posted in another year, or manual entries to direct labor.", gl, cost);
            }

            return t.ToDto();
        }

        // ---- J: subcontracts ----

        private IceScheduleDto ScheduleJ()
        {
            var t = new Table("J", "Subcontract information", "Subcontracts active in the year: type, value and the cost incurred on them.",
                [Text("subcontract", "Subcontract"), Text("vendor", "Subcontractor"), Text("size", "Business size"), Text("prime", "Prime contract"), Text("type", "Type"),
                 DateColumn("start", "Start"), DateColumn("end", "End"), Amount("ceiling", "Value"), Amount("yearCost", "Cost this year"),
                 Amount("cumulative", "Cumulative cost"), Text("audit", "Assist audit / status", editable: true)]);
            foreach (var sub in s.Subcontracts.Where(x => x.PeriodStart <= s.End && (x.PeriodEnd >= s.Start || x.YearCost != 0)))
            {
                var key = sub.Id.ToString();
                t.Add(key).T("subcontract", sub.Number).T("vendor", sub.Vendor).T("size", sub.BusinessSize).T("prime", sub.PrimeContract).T("type", TypeLabel(sub.Pricing))
                    .T("start", Iso(sub.PeriodStart)).T("end", Iso(sub.PeriodEnd)).N("ceiling", sub.Ceiling).N("yearCost", sub.YearCost).N("cumulative", sub.CumulativeCost)
                    .T("audit", inputs.Text("J", key, "audit"));
            }

            Totals(t, "subcontract", ["ceiling", "yearCost", "cumulative"]);
            Total("J", "yearCost", "Subcontract cost this year", t.Rows[^1].Number("yearCost"));
            return t.ToDto();
        }

        // ---- K: T&M hours and amounts ----

        private IceScheduleDto ScheduleK()
        {
            var t = new Table("K", "Summary of hours and amounts on T&M contracts", "Hours worked (at cost) and hours and amounts billed on time-and-materials and labor-hour contracts.",
                [Text("contract", "Contract"), Text("category", "Labor category"), Hours("hoursWorked", "Hours worked"), Amount("cost", "Labor cost"),
                 Hours("hoursBilled", "Hours billed"), Amount("billed", "Amount billed")]);
            var projects = s.Projects.ToDictionary(p => p.Id);
            foreach (var bucket in ContractBuckets.Where(x => TimeAndMaterialsTypes.Contains(x.Type)))
            {
                var id = bucket.Contract!.Id;
                var worked = s.Hours.Where(h => projects.TryGetValue(h.ProjectId, out var p) && p.ContractId == id).ToList();
                var billed = s.TmBilled.Where(b => b.ContractId == id).ToList();
                t.Add($"section:{bucket.Key}", IceRowKind.Section).T("contract", bucket.Label);
                foreach (var category in worked.Select(h => h.LaborCategory).Concat(billed.Where(b => b.LaborCategory.Length > 0).Select(b => b.LaborCategory)).Distinct().Order(StringComparer.Ordinal))
                {
                    t.Add($"{bucket.Key}:{category}").T("contract", bucket.Label).T("category", category)
                        .N("hoursWorked", worked.Where(h => h.LaborCategory == category).Sum(h => h.Hours)).N("cost", worked.Where(h => h.LaborCategory == category).Sum(h => h.Cost))
                        .N("hoursBilled", billed.Where(b => b.LaborCategory == category).Sum(b => b.Hours)).N("billed", billed.Where(b => b.LaborCategory == category).Sum(b => b.Amount));
                }

                var materials = billed.Where(b => b.LaborCategory.Length == 0).Sum(b => b.Amount);
                if (materials != 0)
                {
                    t.Add($"{bucket.Key}:materials").T("contract", bucket.Label).T("category", "Materials and ODCs").N("billed", materials);
                }

                t.Add($"subtotal:{bucket.Key}", IceRowKind.Subtotal).T("contract", $"Total {bucket.Label}")
                    .N("hoursWorked", worked.Sum(h => h.Hours)).N("cost", worked.Sum(h => h.Cost)).N("hoursBilled", billed.Sum(b => b.Hours)).N("billed", billed.Sum(b => b.Amount));
            }

            var subtotals = t.Rows.Where(r => r.Kind == IceRowKind.Subtotal).ToList();
            t.Add("total", IceRowKind.Total).T("contract", "Total").N("hoursWorked", subtotals.Sum(r => r.Number("hoursWorked"))).N("cost", subtotals.Sum(r => r.Number("cost")))
                .N("hoursBilled", subtotals.Sum(r => r.Number("hoursBilled"))).N("billed", subtotals.Sum(r => r.Number("billed")));
            Total("K", "hoursWorked", "T&M hours worked", subtotals.Sum(r => r.Number("hoursWorked")));
            Total("K", "billed", "T&M amount billed", subtotals.Sum(r => r.Number("billed")));
            return t.ToDto();
        }

        // ---- L: billings vs claimed cost ----

        private IceScheduleDto ScheduleL()
        {
            var t = new Table("L", "Reconciliation of billings to claimed cost",
                "Invoices dated in the year (approved or later, not void) against the cost claimed on each contract.",
                [Text("contract", "Contract"), Text("type", "Type"), Amount("claimed", "Claimed cost"), Amount("fee", "Fee claimed"), Amount("billedCost", "Billed cost"),
                 Amount("billedFee", "Billed fee"), Amount("billed", "Total billed"), Amount("difference", "Billed cost over / (under) claimed")]);
            var contracts = s.Contracts.Where(c => _buckets.Any(x => x.Key == c.Id.ToString()) || s.Billing.Any(x => x.ContractId == c.Id)).ToList();
            foreach (var c in contracts)
            {
                var key = c.Id.ToString();
                var billing = s.Billing.FirstOrDefault(x => x.ContractId == c.Id);
                var claimed = Claimed(key);
                var billedCost = billing?.YearCost ?? 0;
                t.Add(key).T("contract", c.Number).T("type", TypeLabel(c.Type)).N("claimed", claimed).N("fee", _fee.GetValueOrDefault(key))
                    .N("billedCost", billedCost).N("billedFee", billing?.YearFee ?? 0).N("billed", billedCost + (billing?.YearFee ?? 0))
                    .N("difference", billedCost - claimed);
                if (CostType.Contains(c.Type) && billedCost != claimed)
                {
                    Check($"L-{c.Number}", IceCheckSeverity.Warning, "L", $"{c.Number}: billed cost doesn't equal claimed cost",
                        billedCost > claimed
                            ? $"Billed {Money(billedCost - claimed)} more than claimed: a credit (or a true-up at final rates) is due."
                            : $"Billed {Money(claimed - billedCost)} less than claimed: unbilled cost to bill once rates are settled.", claimed, billedCost);
                }
            }

            Totals(t, "contract", ["claimed", "fee", "billedCost", "billedFee", "billed", "difference"]);
            var billed = t.Rows[^1].Number("billed");
            t.Add("register", IceRowKind.Subtotal).T("contract", "Per accounts receivable (invoice register)").N("billed", s.InvoiceRegisterTotal);
            t.Add("registerDifference", IceRowKind.Subtotal).T("contract", "Difference (must be zero)").N("billed", billed - s.InvoiceRegisterTotal);
            Total("L", "billed", "Total billed", billed);
            Total("L", "claimed", "Claimed cost on contracts", t.Rows.First(r => r.Key == "total").Number("claimed"));
            if (billed != s.InvoiceRegisterTotal)
            {
                Check("L-AR", IceCheckSeverity.Error, "L", "Schedule L doesn't tie to the invoices",
                    "Total billed on Schedule L differs from the invoice register for the year.", s.InvoiceRegisterTotal, billed);
            }
            else
            {
                Check("L-AR", IceCheckSeverity.Info, "L", "Schedule L ties to the invoices", $"Total billed, {Money(billed)}, equals the invoice register for the year.", s.InvoiceRegisterTotal, billed);
            }

            t.Notes.Add("Cost-plus billings at provisional rates normally differ from claimed cost at actual rates; the difference is settled when final rates are negotiated.");
            return t.ToDto();
        }

        // ---- M: contract information ----

        private IceScheduleDto ScheduleM()
        {
            var t = new Table("M", "Contract information", "Contracts with cost or billings in the year, and the government officials responsible for them.",
                [Text("contract", "Contract"), Text("name", "Title"), Text("customer", "Customer / agency"), Text("type", "Type"), Text("prime", "Prime / sub"),
                 DateColumn("start", "Start"), DateColumn("end", "End"), Amount("value", "Value"), Amount("funded", "Funded"), Text("pm", "Program manager"),
                 Text("co", "Contracting officer", editable: true), Text("aco", "Administrative office (ACO)", editable: true),
                 Text("paying", "Paying office", editable: true), Text("dcaa", "DCAA office", editable: true)]);
            foreach (var bucket in ContractBuckets)
            {
                var c = bucket.Contract!;
                t.Add(bucket.Key).T("contract", c.Number).T("name", c.Name).T("customer", c.Customer).T("type", TypeLabel(c.Type)).T("prime", c.IsPrime ? "Prime" : "Sub")
                    .T("start", Iso(c.PeriodStart)).T("end", Iso(c.PeriodEnd)).N("value", c.Ceiling).N("funded", c.Funded).T("pm", c.ProjectManager)
                    .T("co", inputs.Text("M", bucket.Key, "co")).T("aco", inputs.Text("M", bucket.Key, "aco", c.AdminOffice))
                    .T("paying", inputs.Text("M", bucket.Key, "paying", c.PayingOffice)).T("dcaa", inputs.Text("M", bucket.Key, "dcaa"));
            }

            Totals(t, "contract", ["value", "funded"]);
            var missing = t.Rows.Where(r => r.Kind == IceRowKind.Detail && string.IsNullOrWhiteSpace(r.TextOf("co"))).Select(r => r.TextOf("contract")).ToList();
            if (missing.Count > 0)
            {
                Check("M-OFFICIALS", IceCheckSeverity.Warning, "M", "Contracting officers missing", $"Type the contracting officer for {string.Join(", ", missing)}.");
            }

            return t.ToDto();
        }

        // ---- N: certificate ----

        private IceScheduleDto ScheduleN()
        {
            var t = new Table("N", "Certificate of final indirect costs", "FAR 42.703-2: signed by an official at no lower level than a vice president or chief financial officer.",
                [Text("item", "Item"), Text("value", "Value")]);
            var firm = _firm.Length > 0 ? _firm : "(contractor name: see Contractor information)";
            t.Add("text").T("item", "Certificate").T("value", certification?.Text ?? CertificateText(firm, s.FiscalYear, s.Start, s.End));
            t.Add("firm").T("item", "Name of company").T("value", firm);
            t.Add("signature").T("item", "Signature").T("value", certification is null ? "(not certified)" : $"Certified electronically by {certification.CertifiedBy}");
            t.Add("name").T("item", "Name of certifying official").T("value", certification?.Name);
            t.Add("title").T("item", "Title").T("value", certification?.Title);
            t.Add("date").T("item", "Date of execution").T("value", certification is null ? null : Iso(DateOnly.FromDateTime(certification.CertifiedAtUtc)));
            if (_firm.Length == 0)
            {
                Check("N-FIRM", IceCheckSeverity.Warning, "Cover", "Contractor name missing", "Type the contractor's name under Contractor information; it goes on the certificate.");
            }

            return t.ToDto();
        }

        // ---- O: contract closing ----

        private IceScheduleDto ScheduleO()
        {
            var t = new Table("O", "Contract closing information", "Flexibly priced contracts that ended by the end of the year (or are closed): cost claimed and billed to date, for closeout.",
                [Text("contract", "Contract"), Text("type", "Type"), DateColumn("end", "Completed"), Amount("cumulative", "Cumulative claimed"),
                 Amount("billed", "Cumulative billed"), Amount("difference", "Billed over / (under) claimed"), Text("status", "Closing status", editable: true)]);
            foreach (var bucket in ContractBuckets.Where(x => IsFlexiblyPriced(x.Type) && (x.Contract!.PeriodEnd <= s.End || x.Contract.Status == "Closed")))
            {
                var c = bucket.Contract!;
                var billing = s.Billing.FirstOrDefault(x => x.ContractId == c.Id);
                var cumulative = _cumulative.GetValueOrDefault(bucket.Key);
                var billed = (billing?.CumulativeCost ?? 0) + (billing?.CumulativeFee ?? 0);
                t.Add(bucket.Key).T("contract", c.Number).T("type", TypeLabel(c.Type)).T("end", Iso(c.PeriodEnd)).N("cumulative", cumulative).N("billed", billed)
                    .N("difference", billed - cumulative).T("status", inputs.Text("O", bucket.Key, "status"));
            }

            Totals(t, "contract", ["cumulative", "billed", "difference"]);
            Total("O", "contracts", "Contracts ready to close", t.Rows.Count(r => r.Kind == IceRowKind.Detail));
            return t.ToDto();
        }

        // ---- U: unallowable costs ----

        private decimal UnallowableTotal =>
            s.Accounts.Where(a => a.Category == IceCategory.Unallowable).Sum(a => a.Balance) + s.Accounts.Sum(a => a.Flagged) + _adjustments;

        private IceScheduleDto ScheduleU()
        {
            var t = new Table("U", "Unallowable costs (FAR 31.205)", "Expressly unallowable and other unallowable costs excluded from the claim, and where they came from.",
                [Text("item", "Account / item"), Text("detail", "Description"), Text("reason", "Reason / FAR"), Amount("amount", "Excluded"), Amount("listed", "Flagged ODC (listing)")]);
            t.Add("section:accounts", IceRowKind.Section).T("item", "Unallowable accounts");
            foreach (var a in s.Accounts.Where(x => x.Category == IceCategory.Unallowable && x.Balance != 0))
            {
                t.Add(a.Id.ToString()).T("item", a.Number).T("detail", a.Name).N("amount", a.Balance);
            }

            if (s.Accounts.Any(a => a.Flagged != 0))
            {
                t.Add("section:flagged", IceRowKind.Section).T("item", "ODCs flagged unallowable, posted to other accounts");
                foreach (var a in s.Accounts.Where(x => x.Flagged != 0))
                {
                    t.Add($"flagged:{a.Id}").T("item", a.Number).T("detail", a.Name).N("amount", a.Flagged);
                }

                Check("U-FLAGGED", IceCheckSeverity.Warning, "U", "Unallowable ODCs posted to allowable accounts",
                    $"{Money(s.Accounts.Sum(a => a.Flagged))} of ODCs flagged unallowable posted to accounts that aren't unallowable ({string.Join(", ", s.Accounts.Where(a => a.Flagged != 0).Select(a => a.Number))}). They're excluded from the claim; fix the posting rules so they post to an unallowable account.");
            }

            var adjusted = s.Accounts.Where(a => a.Pool is not null).Select(a => (Account: a, Amount: inputs.Number(PoolSchedule(a.Pool!), a.Id.ToString(), "adjustment"))).Where(x => x.Amount != 0).ToList();
            if (adjusted.Count > 0)
            {
                t.Add("section:adjustments", IceRowKind.Section).T("item", "Adjustments typed into Schedules B and C");
                foreach (var (account, amount) in adjusted)
                {
                    t.Add($"adjustment:{account.Id}").T("item", account.Number).T("detail", account.Name).T("reason", $"Schedule {PoolSchedule(account.Pool!)}").N("amount", amount);
                }
            }

            var total = UnallowableTotal;
            t.Add("total", IceRowKind.Total).T("item", "Total unallowable costs excluded").N("amount", total);
            Total("U", "unallowable", "Unallowable costs excluded", total);

            if (s.FlaggedOdcs.Count > 0)
            {
                t.Add("section:listing", IceRowKind.Section).T("item", "ODCs flagged unallowable in the year (included above)");
                foreach (var o in s.FlaggedOdcs)
                {
                    t.Add($"odc:{o.Id}").T("item", $"{Iso(o.Date)} {o.Project}").T("reason", o.Reason)
                        .N("listed", o.Amount).T("detail", $"{o.Type}: {o.Description}" + (o.PostedTo is null ? " (not posted)" : $" → {o.PostedTo}"));
                }

                t.Add("listing", IceRowKind.Subtotal).T("item", "Total flagged ODCs").N("listed", s.FlaggedOdcs.Sum(o => o.Amount));
                if (s.FlaggedOdcs.Any(o => o.PostedTo is null))
                {
                    Check("U-UNPOSTED", IceCheckSeverity.Warning, "U", "Flagged ODCs not in the GL",
                        "Some approved ODCs flagged unallowable have no posting in the year; post the backlog (Accounting › Period close) and regenerate.");
                }
            }

            return t.ToDto();
        }

        private void Completeness()
        {
            if (string.IsNullOrWhiteSpace(inputs.Text("Cover", "dcaa", "value")))
            {
                Check("COVER-DCAA", IceCheckSeverity.Info, "Cover", "Cognizant DCAA office", "Type the cognizant DCAA office under Contractor information.");
            }

            if (!s.PriorPackageFound && _cumulative.Count > 0)
            {
                Check("I-PRIOR", IceCheckSeverity.Info, "I", "Prior years' cumulative cost",
                    $"There's no FY{s.FiscalYear - 1} package, so Schedule I starts from zero unless you type prior years' claimed cost.");
            }

            _notes.Add($"Generated from the general ledger for FY{s.FiscalYear} ({Date(s.Start)} to {Date(s.End)}).");
        }
    }
}
