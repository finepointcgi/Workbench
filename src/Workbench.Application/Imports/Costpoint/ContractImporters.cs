using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ModificationChangeKind = Workbench.Contracts.Contracts.ModificationChangeKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>Costpoint customers → customers, matched on the customer id.</summary>
public sealed class CustomerImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Customers;
    public override string Title => "Customers";
    public override string Description => "Agencies and primes you hold contracts with.";
    public override string? SampleFile => "samples/import/costpoint/customers.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Code", "Customer ID", true, "Natural key.", "CUST_ID", "Customer ID", "Customer", "Customer Code", "CUST_CD"),
        new("Name", "Customer name", true, "", "CUST_NAME", "CUST_LONG_NAME", "Customer Name", "Name"),
        new("Category", "Category", false, "For example DoD, Civilian, Commercial or Prime contractor.", "CUST_TYPE", "CUST_TYPE_CD", "Category", "Customer Type", "Agency Type"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var contracts = Service<IContractService>(context);
        var setup = Service<ISetupAdminService>(context);
        var existing = (await contracts.ListCustomersAsync(cancellationToken)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = row.Required("Code");
            var name = row.Required("Name");
            var category = row.Get("Category");
            if (code is not null && !seen.Add(code))
            {
                row.Error("Code", $"Customer {code} appears more than once in the file.");
            }

            if (row.HasErrors || code is null || name is null)
            {
                continue;
            }

            var request = new SaveCustomerRequest(code, name, category);
            if (existing.TryGetValue(code, out var customer))
            {
                var changed = Changed(("Name", customer.Name, name), ("Category", customer.Category, category));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Customer", code, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Customer), customer.Id, "Customer", code, UpdateSummary(changed),
                    () => setup.UpdateCustomerAsync(customer.Id, request, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(Customer), "Customer", code, name,
                async () => (await contracts.CreateCustomerAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }
}

/// <summary>Costpoint contracts (Contract Management or the prime contract on Project Setup) → contracts, matched on the contract number.</summary>
public sealed class ContractImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Contracts;
    public override string Title => "Contracts";
    public override string Description => "Contract headers: customer, type, period of performance, value and funding. Contracts with CLINs take their totals from the CLINs.";
    public override string? SampleFile => "samples/import/costpoint/contracts.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("ContractNumber", "Contract number", true, "Natural key.", "PRIME_CONTR_ID", "CONTRACT_ID", "CONTR_ID", "Contract", "Contract No", "Contract Number", "Contract ID"),
        new("Name", "Contract name", true, "", "CONTR_NAME", "CONTRACT_NAME", "CONTR_DESC", "Contract Name", "Description", "Title"),
        new("Customer", "Customer ID", true, "Customer id of an existing customer.", "CUST_ID", "Customer", "Customer ID"),
        new("Type", "Contract type", true, "FFP, T&M, LH, CPFF, CPAF or CPIF.", "CONTR_TYPE", "CONTR_TYPE_CD", "PROJ_TYPE", "Contract Type", "Type"),
        new("Status", "Status", false, "Active, Pending or Closed (A/P/C, Y/N). Defaults to Active.", "STATUS", "S_CONTR_STATUS_CD", "ACTIVE_FL", "Status"),
        new("IsPrime", "Prime", false, "Y when you're the prime. Defaults to Y.", "PRIME_FL", "PRIME_CONTR_FL", "Prime", "Is Prime"),
        new("Start", "Period start", true, "", "POP_START_DT", "PERF_START_DT", "START_DT", "Start Date", "Period Start", "POP Start"),
        new("End", "Period end", true, "", "POP_END_DT", "PERF_END_DT", "END_DT", "End Date", "Period End", "POP End"),
        new("Ceiling", "Total value", false, "Ceiling / total contract value.", "TOT_CONTR_VALUE", "CONTR_VALUE_AMT", "CEILING_AMT", "TOT_VALUE_AMT", "Total Value", "Ceiling", "Contract Value"),
        new("Funded", "Funded", false, "Funded (obligated) to date.", "TOT_FUNDED_AMT", "FUNDED_AMT", "Funded", "Funded Value", "Funded Amount"),
    ];

    internal static ContractKind? ParseKind(string text) => text.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "FFP" or "FP" or "FIRMFIXEDPRICE" or "FIXEDPRICE" => ContractKind.FirmFixedPrice,
        "T&M" or "TM" or "TANDM" or "TIMEANDMATERIALS" or "TIME&MATERIALS" => ContractKind.TimeAndMaterials,
        "LH" or "LABORHOUR" => ContractKind.LaborHour,
        "CPFF" or "CP" or "CR" or "COSTPLUS" or "COSTPLUSFIXEDFEE" or "COSTREIMBURSABLE" or "CNF" or "COST" or "COSTNOFEE" => ContractKind.CostPlusFixedFee,
        "CPAF" or "COSTPLUSAWARDFEE" => ContractKind.CostPlusAwardFee,
        "CPIF" or "COSTPLUSINCENTIVEFEE" => ContractKind.CostPlusIncentiveFee,
        _ => null,
    };

    internal static ContractState? ParseState(string text) => text.Trim().ToUpperInvariant() switch
    {
        "A" or "ACTIVE" or "Y" or "OPEN" => ContractState.Active,
        "P" or "PENDING" or "AWARDED" => ContractState.Pending,
        "C" or "CLOSED" or "N" or "I" or "INACTIVE" or "COMPLETE" => ContractState.Closed,
        _ => null,
    };

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var contracts = Service<IContractService>(context);
        var existing = (await contracts.ListContractsAsync(cancellationToken)).ToDictionary(c => c.ContractNumber, StringComparer.OrdinalIgnoreCase);
        var customers = (await contracts.ListCustomersAsync(cancellationToken)).ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var number = row.Required("ContractNumber");
            var name = row.Required("Name");
            var customerCode = row.Required("Customer");
            var typeText = row.Required("Type");
            var start = row.Date("Start");
            var end = row.Date("End");
            var ceiling = row.Decimal("Ceiling") ?? 0m;
            var funded = row.Decimal("Funded") ?? 0m;
            var isPrime = row.Bool("IsPrime") ?? true;
            var kind = typeText is null ? null : ParseKind(typeText);
            var state = row.Get("Status") is { } s ? ParseState(s) : ContractState.Active;
            if (typeText is not null && kind is null)
            {
                row.Error("Type", $"Contract type '{typeText}' isn't one of FFP, T&M, LH, CPFF, CPAF, CPIF.");
            }

            if (state is null)
            {
                row.Error("Status", $"Status '{row.Get("Status")}' isn't Active, Pending or Closed.");
            }

            if (start is null && row.Get("Start") is null)
            {
                row.Error("Start", "Period start is required.");
            }

            if (end is null && row.Get("End") is null)
            {
                row.Error("End", "Period end is required.");
            }

            if (customerCode is not null && !customers.ContainsKey(customerCode))
            {
                row.Error("Customer", $"Customer '{customerCode}' isn't in Workbench; import customers first.");
            }

            if (funded > ceiling && ceiling > 0)
            {
                row.Error("Funded", "Funded value can't exceed the total value.");
            }

            if (number is not null && !seen.Add(number))
            {
                row.Error("ContractNumber", $"Contract {number} appears more than once in the file.");
            }

            if (row.HasErrors || number is null || name is null || customerCode is null || kind is null || state is null || start is null || end is null)
            {
                continue;
            }

            var request = new SaveContractRequest(customers[customerCode], number, name, kind.Value, state.Value, isPrime, start.Value, end.Value, ceiling, funded);
            if (existing.TryGetValue(number, out var contract))
            {
                // A contract with CLINs takes its value and funding from them (funding changes come through the CLIN import).
                if ((await Service<IClinService>(context).ListClinsAsync(contract.Id, cancellationToken)).Count > 0)
                {
                    ceiling = contract.CeilingValue;
                    funded = contract.FundedValue;
                    request = request with { CeilingValue = ceiling, FundedValue = funded };
                }

                var changed = Changed(("Name", contract.Name, name), ("Customer", contract.CustomerId, request.CustomerId), ("Type", contract.Type, kind),
                    ("Status", contract.Status, state), ("Prime", contract.IsPrime, isPrime), ("Start", contract.PeriodStart, start), ("End", contract.PeriodEnd, end),
                    ("Value", contract.CeilingValue, ceiling), ("Funded", contract.FundedValue, funded));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Contract", number, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Contract), contract.Id, "Contract", number, UpdateSummary(changed),
                    () => contracts.UpdateContractAsync(contract.Id, request, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(Contract), "Contract", number, name,
                async () => (await contracts.CreateContractAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }
}

/// <summary>
/// Costpoint CLINs (Contract Management / Line Items) → CLINs and SLINs, matched on contract + CLIN number. The funded
/// column is the cumulative funded amount: the difference from Workbench's funded value is obligated (or de-obligated)
/// through one executed modification per contract, so funding history stays immutable.
/// </summary>
public sealed class ClinImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Clins;
    public override string Title => "CLINs and funding";
    public override string Description => "Contract line items (and SLINs such as 0001AA) with value and cumulative funding. Funding changes are executed as one modification per contract.";
    public override string? SampleFile => "samples/import/costpoint/clins.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("ContractNumber", "Contract number", true, "", "PRIME_CONTR_ID", "CONTRACT_ID", "CONTR_ID", "Contract", "Contract No", "Contract Number"),
        new("ClinNumber", "CLIN", true, "CLIN (0001) or SLIN (0001AA).", "CLIN_ID", "CLIN", "CLIN_NO", "CLIN Number", "Line Item", "Item No", "SLIN"),
        new("Description", "Description", true, "", "CLIN_DESC", "CLIN_NAME", "Description", "Line Item Description", "Title"),
        new("Pricing", "Pricing", false, "FFP, T&M, LH, CPFF, CPAF, CPIF or CNF. Defaults to the contract's type.", "CLIN_TYPE", "CLIN_TYPE_CD", "Pricing", "Contract Type", "Type"),
        new("Start", "Period start", false, "Defaults to the contract's.", "POP_START_DT", "START_DT", "Start Date", "Period Start"),
        new("End", "Period end", false, "Defaults to the contract's.", "POP_END_DT", "END_DT", "End Date", "Period End"),
        new("Ceiling", "Value", true, "CLIN ceiling / total value.", "CLIN_VALUE_AMT", "CLIN_VALUE", "CEILING_AMT", "TOT_VALUE_AMT", "Value", "Ceiling", "Total Value", "Amount"),
        new("Funded", "Funded to date", false, "Cumulative funded amount.", "FUNDED_AMT", "TOT_FUNDED_AMT", "CLIN_FUNDED_AMT", "Funded", "Funded Value", "Funded Amount"),
        new("FeeRate", "Fee %", false, "Fee as a percent (7) or fraction (0.07), for cost-plus CLINs.", "FEE_RT", "FEE_PCT", "FEE_RATE", "Fee %", "Fee Rate", "Fee"),
        new("IsOption", "Option", false, "Y for an option CLIN.", "OPTION_FL", "Option", "Is Option"),
        new("IsExercised", "Exercised", false, "Y when an option has been exercised.", "EXERCISED_FL", "Exercised", "Option Exercised"),
        new("FundingDate", "Funding date", false, "Effective date of the funding change; defaults to today.", "FUNDING_DT", "FUND_DT", "Funding Date", "Effective Date"),
    ];

    private static ClinPricingKind? ParsePricing(string text) =>
        text.Trim().ToUpperInvariant().Replace(" ", "") is "CNF" or "COSTNOFEE" or "COST" ? ClinPricingKind.CostNoFee
        : ContractImporter.ParseKind(text) is { } k ? (ClinPricingKind)(int)k : null;

    private sealed record Planned(MappedRow Row, string Number, Guid ClinId, bool IsNew, decimal Ceiling, decimal Funded, bool Exercise, decimal CurrentFunded, decimal CurrentCeiling, bool CeilingByMod);

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var contractsService = Service<IContractService>(context);
        var clins = Service<IClinService>(context);
        var contracts = (await contractsService.ListContractsAsync(cancellationToken)).ToDictionary(c => c.ContractNumber, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(context.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        foreach (var group in rows.GroupBy(r => r.Get("ContractNumber") ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0)
            {
                foreach (var row in group)
                {
                    row.Required("ContractNumber");
                }

                continue;
            }

            if (!contracts.TryGetValue(group.Key, out var contract))
            {
                foreach (var row in group)
                {
                    row.Error("ContractNumber", $"Contract {group.Key} isn't in Workbench; import contracts first.");
                }

                continue;
            }

            var existing = (await clins.ListClinsAsync(contract.Id, cancellationToken)).ToDictionary(c => c.Number, StringComparer.OrdinalIgnoreCase);
            var planned = new List<Planned>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // CLINs before their SLINs.
            foreach (var row in group.OrderBy(r => r.Get("ClinNumber") ?? "", StringComparer.Ordinal))
            {
                var number = row.Required("ClinNumber")?.ToUpperInvariant();
                var description = row.Required("Description");
                var ceiling = row.Decimal("Ceiling");
                var funded = row.Decimal("Funded");
                var fee = row.Decimal("FeeRate");
                var isOption = row.Bool("IsOption") ?? false;
                var exercised = row.Bool("IsExercised") ?? false;
                var start = row.Date("Start") ?? contract.PeriodStart;
                var end = row.Date("End") ?? contract.PeriodEnd;
                var pricing = row.Get("Pricing") is { } p ? ParsePricing(p) : (ClinPricingKind)(int)contract.Type;
                if (pricing is null)
                {
                    row.Error("Pricing", $"Pricing '{row.Get("Pricing")}' isn't one of FFP, T&M, LH, CPFF, CPAF, CPIF, CNF.");
                }

                if (ceiling is null && row.Get("Ceiling") is null)
                {
                    row.Error("Ceiling", "Value is required.");
                }

                if (fee is > 1)
                {
                    fee /= 100m;
                }

                if (funded is < 0)
                {
                    row.Error("Funded", "Funded to date can't be negative.");
                }

                if (funded > ceiling)
                {
                    row.Error("Funded", $"CLIN {number} is funded beyond its value.");
                }

                if (number is not null && !seen.Add(number))
                {
                    row.Error("ClinNumber", $"CLIN {number} appears more than once for contract {group.Key}.");
                }

                if (row.HasErrors || number is null || description is null || ceiling is null || pricing is null)
                {
                    continue;
                }

                var key = $"{group.Key} {number}";
                var request = new SaveClinRequest(number, description, pricing.Value, start, end, ceiling.Value, fee, isOption);
                if (existing.TryGetValue(number, out var clin))
                {
                    var ceilingByMod = clin.FundedValue != 0 && clin.CeilingValue != ceiling;
                    var changed = Changed(("Description", clin.Description, description), ("Pricing", clin.Pricing, pricing), ("Start", clin.PeriodStart, start),
                        ("End", clin.PeriodEnd, end), ("Fee", clin.FeeRate, fee), ("Value", clin.CeilingValue, ceilingByMod ? clin.CeilingValue : ceiling));
                    if (changed.Count > 0)
                    {
                        var editable = request with { CeilingValue = ceilingByMod ? clin.CeilingValue : ceiling.Value, IsOption = clin.IsOption };
                        if (!await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Clin), clin.Id, "CLIN", key, UpdateSummary(changed),
                            () => context.Run.TrackAsync(ContractTotals(contract), () => clins.UpdateClinAsync(clin.Id, editable, cancellationToken), cancellationToken),
                            cancellationToken))
                        {
                            continue;
                        }
                    }

                    var exercise = clin.IsOption && !clin.IsExercised && exercised;
                    if (changed.Count == 0 && !ceilingByMod && !exercise && (funded ?? clin.FundedValue) == clin.FundedValue)
                    {
                        context.Run.Skip(row.FileName, row.Number, "CLIN", key, "Unchanged");
                        continue;
                    }

                    if (!clin.HasSlins)
                    {
                        planned.Add(new Planned(row, number, clin.Id, false, ceiling.Value, funded ?? clin.FundedValue, exercise, clin.FundedValue, clin.CeilingValue, ceilingByMod));
                    }

                    continue;
                }

                var id = await context.Run.CreateAsync(row.FileName, row.Number, typeof(Clin), "CLIN", key, $"{description} · value {ceiling:N2}",
                    async () => (await context.Run.TrackAsync(ContractTotals(contract), () => clins.CreateClinAsync(contract.Id, request, cancellationToken), cancellationToken)).Id,
                    cancellationToken);
                if (id is { } newId)
                {
                    planned.Add(new Planned(row, number, newId, true, ceiling.Value, funded ?? 0m, isOption && exercised, 0m, ceiling.Value, false));
                }
            }

            await FundAsync(context, contract, planned, today, cancellationToken);
        }
    }

    /// <summary>CLIN changes re-total the contract, so its state before them is kept for rollback.</summary>
    private static IReadOnlyList<(Type, Guid, string)> ContractTotals(ContractDto contract) => [(typeof(Contract), contract.Id, $"Contract {contract.ContractNumber}")];

    /// <summary>One executed modification per contract: exercise options, change funded CLINs' values, obligate the funding difference.</summary>
    private static async Task FundAsync(ImportContext context, ContractDto contract, List<Planned> planned, DateOnly today, CancellationToken cancellationToken)
    {
        var changes = new List<ModificationChangeDto>();
        foreach (var p in planned)
        {
            if (p.Exercise)
            {
                changes.Add(new ModificationChangeDto(ModificationChangeKind.ExerciseOption, p.Number, Note: "Exercised in Costpoint"));
            }

            if (p.CeilingByMod)
            {
                changes.Add(new ModificationChangeDto(ModificationChangeKind.ChangeClinValue, p.Number, p.Ceiling - p.CurrentCeiling, Note: "Value from Costpoint"));
            }

            if (p.Funded != p.CurrentFunded)
            {
                changes.Add(new ModificationChangeDto(ModificationChangeKind.Fund, p.Number, p.Funded - p.CurrentFunded, Note: "Funding from Costpoint"));
            }
        }

        if (changes.Count == 0)
        {
            return;
        }

        var effective = planned.Select(p => p.Row.Date("FundingDate")).OfType<DateOnly>().DefaultIfEmpty(today).Max();
        var number = $"IMP-{context.Job.Id.ToString("N")[^6..].ToUpperInvariant()}";
        var summary = string.Join("; ", changes.Select(c => c.Kind switch
        {
            ModificationChangeKind.Fund => $"fund {c.ClinNumber} {c.Amount:+#,0.00;-#,0.00}",
            ModificationChangeKind.ExerciseOption => $"exercise {c.ClinNumber}",
            _ => $"value {c.ClinNumber} {c.Amount:+#,0.00;-#,0.00}",
        }));
        var first = planned[0].Row;
        var label = $"{contract.ContractNumber} {number}";
        if (!context.Run.Applying)
        {
            context.Run.PlanUpdate(first.FileName, first.Number, "Funding modification", label, summary);
            return;
        }

        var modifications = Service<IContractModificationService>(context);
        var dbFactory = context.Services.GetRequiredService<IWorkbenchDbContextFactory>();
        var tracked = new List<(Type, Guid, string)> { (typeof(Contract), contract.Id, $"Contract {contract.ContractNumber}") };
        tracked.AddRange(planned.Where(p => !p.IsNew).Select(p => (typeof(Clin), p.ClinId, $"CLIN {contract.ContractNumber} {p.Number}")));
        try
        {
            await context.Run.TrackAsync(tracked, async () =>
            {
                var draft = await modifications.CreateDraftAsync(contract.Id,
                    new SaveModificationRequest(number, effective, $"Funding and values imported from Costpoint (import job {context.Job.Id.ToString()[..8]})", changes),
                    cancellationToken);
                try
                {
                    await modifications.ExecuteAsync(draft.Id, cancellationToken);
                }
                catch (ArgumentException)
                {
                    await modifications.DeleteDraftAsync(draft.Id, cancellationToken);
                    throw;
                }

                await context.Run.RecordCreatedAsync(typeof(ContractModification), draft.Id, $"Modification {label}", cancellationToken);
                await using var db = await dbFactory.CreateAsync(cancellationToken);
                foreach (var change in await db.Set<ContractModificationChange>().AsNoTracking().Where(c => c.ModificationId == draft.Id).Select(c => c.Id).ToListAsync(cancellationToken))
                {
                    await context.Run.RecordCreatedAsync(typeof(ContractModificationChange), change, $"Modification change {label}", cancellationToken);
                }

                foreach (var action in await db.Set<FundingAction>().AsNoTracking().Where(f => f.ModificationId == draft.Id).Select(f => f.Id).ToListAsync(cancellationToken))
                {
                    await context.Run.RecordCreatedAsync(typeof(FundingAction), action, $"Funding action {label}", cancellationToken);
                }

                return true;
            }, cancellationToken);
            context.Run.PlanUpdate(first.FileName, first.Number, "Funding modification", label, summary);
        }
        catch (Exception ex) when (ImportRun.IsRowError(ex))
        {
            foreach (var p in planned)
            {
                p.Row.Error("Funded", $"Modification {number} wasn't executed: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Costpoint project labor category bill rates (Project Labor Categories / PLC rates) → contract bill rates, matched on
/// contract + PLC + start date. A project id resolves to its project's contract.
/// </summary>
public sealed class BillRateImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.BillRates;
    public override string Title => "Bill rates (project PLC rates)";
    public override string Description => "Hourly bill rates per labor category, effective-dated. Rows name a project (its contract gets the rate) or a contract.";
    public override string? SampleFile => "samples/import/costpoint/bill-rates.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("ProjectId", "Project ID", false, "Project (any WBS level); the rate goes to its contract.", "PROJ_ID", "Project", "Project ID", "Project Code"),
        new("ContractNumber", "Contract number", false, "Use instead of a project id.", "PRIME_CONTR_ID", "CONTRACT_ID", "Contract", "Contract Number"),
        new("LaborCategory", "PLC", true, "Labor category code.", "BILL_LAB_CAT_CD", "LAB_CAT_CD", "PLC", "Labor Category"),
        new("Rate", "Bill rate", true, "Hourly bill rate.", "BILL_RT_AMT", "BILL_RATE", "BILL_RT", "Bill Rate", "Rate", "Hourly Bill Rate", "Hourly Rate"),
        new("Start", "Effective date", true, "", "START_DT", "EFFECT_DT", "Effective Date", "Start Date", "Rate Start"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var contractsService = Service<IContractService>(context);
        var people = Service<IPeopleService>(context);
        var projects = (await Service<IProjectService>(context).ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var contracts = (await contractsService.ListContractsAsync(cancellationToken)).ToDictionary(c => c.ContractNumber, StringComparer.OrdinalIgnoreCase);
        var categories = (await people.ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var rates = new Dictionary<Guid, IReadOnlyList<BillRateDto>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var plc = row.Required("LaborCategory");
            var rate = row.Decimal("Rate");
            var start = row.Date("Start");
            if (row.Get("Rate") is null)
            {
                row.Error("Rate", "Bill rate is required.");
            }
            else if (rate is <= 0)
            {
                row.Error("Rate", "Bill rate must be greater than zero.");
            }

            if (row.Get("Start") is null)
            {
                row.Error("Start", "Effective date is required.");
            }

            if (plc is not null && !categories.ContainsKey(plc))
            {
                row.Error("LaborCategory", $"PLC {plc} isn't in Workbench; import labor categories first.");
            }

            ContractDto? contract = null;
            if (row.Get("ContractNumber") is { } contractNumber)
            {
                if (!contracts.TryGetValue(contractNumber, out contract))
                {
                    row.Error("ContractNumber", $"Contract {contractNumber} isn't in Workbench.");
                }
            }
            else if (row.Get("ProjectId") is { } projectId)
            {
                var code = projectId.Split('.')[0];
                if (!projects.TryGetValue(code, out var project))
                {
                    row.Error("ProjectId", $"Project {code} isn't in Workbench; import projects first.");
                }
                else if (project.ContractNumber is null || !contracts.TryGetValue(project.ContractNumber, out contract))
                {
                    row.Error("ProjectId", $"Project {code} has no contract to hold bill rates.");
                }
            }
            else
            {
                row.Error("ProjectId", "Map a project id or a contract number.");
            }

            if (row.HasErrors || contract is null || plc is null || rate is null || start is null)
            {
                continue;
            }

            var key = $"{contract.ContractNumber} {plc} {start:yyyy-MM-dd}";
            if (!seen.Add(key))
            {
                row.Error("Start", $"{plc} has more than one rate effective {start:yyyy-MM-dd} for {contract.ContractNumber}.");
                continue;
            }

            if (!rates.TryGetValue(contract.Id, out var list))
            {
                rates[contract.Id] = list = await contractsService.ListBillRatesAsync(contract.Id, cancellationToken);
            }

            var categoryId = categories[plc];
            if (list.FirstOrDefault(r => r.LaborCategoryId == categoryId && r.EffectiveFrom == start) is { } same)
            {
                if (same.HourlyBillRate == rate)
                {
                    context.Run.Skip(row.FileName, row.Number, "Bill rate", key, "Unchanged");
                }
                else
                {
                    row.Error("Rate", $"{contract.ContractNumber} already has a different {plc} rate effective {start:yyyy-MM-dd}; add the new rate with a later effective date.");
                }

                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(ContractLaborRate), "Bill rate", key, $"{rate:0.00}/h",
                async () => (await contractsService.AddBillRateAsync(contract.Id, new AddBillRateRequest(categoryId, start.Value, rate.Value), cancellationToken)).Id,
                cancellationToken);
        }
    }
}
