using Workbench.Application.Imports.Costpoint;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.Unanet;

// Unanet importers. Each one is the matching importer for a Costpoint export with Unanet's column headings in front of
// the aliases, so the import logic, validation, dry run, audit trail and rollback are exactly the same code. Where Unanet
// shapes the data differently (a project and its tasks in one file, FLSA status words, contract numbers that are
// optional), the rows are rewritten first by Canonical.Rewrite and then handed to that same logic.

/// <summary>Unanet organizations → org units. Parents are imported before their children whatever the codes look like.</summary>
public sealed class UnanetOrgUnitImporter : OrgUnitImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Organizations";
    public override string Description => "Unanet organizations become org units. A parent organization becomes the parent org unit.";
    public override string? SampleFile => "samples/import/unanet/organizations.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new OrgUnitImporter().Fields,
    [
        new Tweak("OrgId", ["Organization Code", "Org Code", "Organization ID", "Organization"], Label: "Organization code", Description: "Unanet organization code, e.g. ENG."),
        new Tweak("Name", ["Organization Name", "Org Name", "Name"], Label: "Organization name"),
        new Tweak("ParentOrgId", ["Parent Organization", "Parent Organization Code", "Parent Org Code", "Parent"], Label: "Parent organization",
            Description: "Code of the parent organization (it can be listed after its children)."),
    ]);

    protected override Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Get("OrgId") is { } id)
            {
                parentOf.TryAdd(id, row.Get("ParentOrgId"));
            }
        }

        int Depth(string? id)
        {
            var depth = 0;
            while (id is not null && parentOf.TryGetValue(id, out var parent) && parent is not null && depth < 50)
            {
                id = parent;
                depth++;
            }

            return depth;
        }

        return base.ProcessAsync(context, rows.OrderBy(r => Depth(r.Get("OrgId"))).ToList(), cancellationToken);
    }
}

/// <summary>Unanet labor categories → labor categories. A file with only names uses the name as the code.</summary>
public sealed class UnanetLaborCategoryImporter : LaborCategoryImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Labor categories";
    public override string Description => "Unanet labor categories, used for budgets, bill rates and what people charge as.";
    public override string? SampleFile => "samples/import/unanet/labor-categories.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new LaborCategoryImporter().Fields,
    [
        new Tweak("Code", ["Labor Category Code", "LCAT Code", "Labor Category", "LCAT", "Code"], Label: "Labor category", Description: "Labor category code, or its name when Unanet has no separate code."),
        new Tweak("Name", ["Labor Category Name", "LCAT Name", "Description", "Name"], Required: false, Label: "Description", Description: "Defaults to the code."),
        new Tweak("Active", ["Active", "Status"]),
    ]);

    protected override Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var converted = Canonical.Rewrite(context, rows, ["Code", "Name", "Active"], r =>
            Canonical.One(new Dictionary<string, string?>
            {
                ["Code"] = r.Get("Code"),
                ["Name"] = r.Get("Name") ?? r.Get("Code"),
                ["Active"] = r.Get("Active") is { } a ? UnanetTerms.Open(a) : null,
            }));
        return base.ProcessAsync(context, converted, cancellationToken);
    }
}

/// <summary>Unanet people → employees with their organization, labor category, supervisor and current cost rate.</summary>
public sealed class UnanetEmployeeImporter : EmployeeImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "People";
    public override string Description => "Unanet people become employees, with their organization, labor category, supervisor and current hourly cost rate. Email is required: it links the person to their sign-in.";
    public override string? SampleFile => "samples/import/unanet/people.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new EmployeeImporter().Fields,
    [
        new Tweak("EmployeeNumber", ["Employee ID", "Employee Number", "Person ID", "Username", "User Name", "Login"], Label: "Employee ID", Description: "Unanet employee id (or username when the site has no employee ids)."),
        new Tweak("Name", ["Person Name", "Full Name", "Display Name", "Name"]),
        new Tweak("FirstName", ["First Name", "First"]),
        new Tweak("LastName", ["Last Name", "Last"]),
        new Tweak("Email", ["Email", "Email Address", "E-mail", "Work Email"]),
        new Tweak("OrgId", ["Organization", "Organization Code", "Org Code", "Home Organization"], Label: "Organization", Description: "Code of an organization already imported."),
        new Tweak("LaborCategory", ["Labor Category", "Labor Category Code", "Default Labor Category", "LCAT"], Label: "Labor category"),
        new Tweak("LaborCategoryName", ["Labor Category Name", "LCAT Name"]),
        new Tweak("HireDate", ["Hire Date", "Original Hire Date", "Date Hired", "Start Date"]),
        new Tweak("TerminationDate", ["Termination Date", "Term Date", "Last Day", "Separation Date", "End Date"]),
        new Tweak("Supervisor", ["Supervisor", "Supervisor ID", "Manager", "Manager ID", "Reports To"], Description: "The supervisor's employee id (in the file or already in Workbench)."),
        new Tweak("StandardHours", ["Hours Per Week", "Standard Hours", "Weekly Hours", "Std Hours"]),
        new Tweak("Exempt", ["FLSA Status", "Exempt Status", "Exempt", "Overtime Exempt"], Description: "Exempt/Non-exempt (or Y/N). Defaults to exempt."),
        new Tweak("HourlyCost", ["Labor Cost Rate", "Cost Rate", "Hourly Cost Rate", "Hourly Cost", "Hourly Rate"], Label: "Hourly cost rate"),
        new Tweak("RateEffective", ["Cost Rate Effective Date", "Rate Effective Date", "Rate Start", "Effective Date"]),
    ]);

    protected override Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var names = Fields.Select(f => f.Name).ToList();
        var converted = Canonical.Rewrite(context, rows, names, r =>
        {
            var values = Canonical.Copy(r, names);
            if (r.Get("Exempt") is { } flsa)
            {
                // "Non-exempt" must not read as "exempt": the FLSA words, then plain Y/N.
                var text = flsa.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
                values["Exempt"] = text switch
                {
                    "nonexempt" or "hourly" or "n" or "no" or "false" => "N",
                    "exempt" or "salaried" or "salary" or "y" or "yes" or "true" => "Y",
                    _ => flsa,
                };
            }

            return Canonical.One(values);
        });
        return base.ProcessAsync(context, converted, cancellationToken);
    }
}

/// <summary>Unanet cost rate history → effective-dated cost rates.</summary>
public sealed class UnanetCostRateImporter : CostRateImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Cost rate history";
    public override string Description => "Hourly cost rates by effective date. Rates are append-only: an existing date with a different amount is an error.";
    public override string? SampleFile => "samples/import/unanet/cost-rates.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new CostRateImporter().Fields,
    [
        new Tweak("EmployeeNumber", ["Employee ID", "Employee Number", "Person ID", "Username", "User Name"]),
        new Tweak("EffectiveDate", ["Effective Date", "Rate Effective Date", "Effective From", "Start Date"]),
        new Tweak("HourlyCost", ["Labor Cost Rate", "Cost Rate", "Hourly Cost Rate", "Hourly Cost", "Hourly Rate", "Rate"]),
        new Tweak("Reason", ["Reason", "Comments", "Note"]),
    ]);
}

/// <summary>Unanet customers → customers.</summary>
public sealed class UnanetCustomerImporter : CustomerImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Customers";
    public override string Description => "Unanet customers become customers (agencies and primes you hold contracts with).";
    public override string? SampleFile => "samples/import/unanet/customers.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new CustomerImporter().Fields,
    [
        new Tweak("Code", ["Customer Code", "Customer ID", "Customer Number", "Customer"], Label: "Customer code"),
        new Tweak("Name", ["Customer Name", "Name"]),
        new Tweak("Category", ["Customer Type", "Customer Category", "Category", "Agency Type"]),
    ]);
}

/// <summary>
/// Unanet projects → contracts. A Unanet project holds the customer agreement and the place time is charged; this step
/// creates the contract half. Internal projects (overhead, bid and proposal, leave...) have no contract and are left out;
/// task rows are ignored here (they become WBS elements in the projects step). The same Unanet projects export is used for
/// this step and the next.
/// </summary>
public sealed class UnanetContractImporter : ContractImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Projects → contracts";
    public override string Description => "Each customer project in the Unanet projects export becomes a contract (customer, type, period of performance, value, funding). Internal projects have no contract. Use the same export for the next step, which creates the projects and WBS.";
    public override string? SampleFile => "samples/import/unanet/projects.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new ContractImporter().Fields,
    [
        new Tweak("ContractNumber", ["Contract Number", "Contract No", "Contract ID", "Contract", "PO Number"], Required: false, Description: "Unanet contract number. Blank = the project code is used."),
        new Tweak("Name", ["Contract Name", "Contract Title", "Project Name", "Project Title", "Title"], Label: "Contract name", Description: "Defaults to the contract number."),
        new Tweak("Customer", ["Customer Code", "Customer ID", "Customer"], Label: "Customer code"),
        new Tweak("Type", ["Project Type", "Contract Type", "Billing Type", "Pricing Type", "Type"], Label: "Project / contract type", Description: "T&M, FFP, Cost plus, Labor hour... Overhead and other internal types are skipped."),
        new Tweak("Status", ["Project Status", "Contract Status", "Status"]),
        new Tweak("Start", ["Start Date", "Project Start", "Contract Start", "POP Start", "Period of Performance Start"]),
        new Tweak("End", ["End Date", "Project End", "Contract End", "POP End", "Period of Performance End"]),
        new Tweak("Ceiling", ["Contract Value", "Total Value", "Contract Ceiling", "Project Value", "Ceiling"]),
        new Tweak("Funded", ["Funded Amount", "Funded", "Contract Funding", "Funding"]),
    ], null,
        UnanetFields.Field("ProjectCode", "Project code", "Used as the contract number when the project has none.", "Project Code", "Project", "Project ID", "Project Number"),
        UnanetFields.Field("Task", "Task code", "Rows with a task are tasks, not projects, and are left to the projects step.", "Task Code", "Task", "Task ID", "Task Number"));

    protected override Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var names = new[] { "ContractNumber", "Name", "Customer", "Type", "Status", "IsPrime", "Start", "End", "Ceiling", "Funded" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var converted = Canonical.Rewrite(context, rows, names, r =>
        {
            if (r.Get("Task") is not null)
            {
                return Canonical.None;
            }

            var number = r.Get("ContractNumber") ?? r.Get("ProjectCode");
            if (number is null)
            {
                r.Error("ContractNumber", "Map the Contract Number column, or the Project Code column.");
                return Canonical.None;
            }

            if (UnanetTerms.Charge(r.Get("Type")) != "Direct")
            {
                context.Run.Skip(r.FileName, r.Number, "Contract", number, "Internal project: no contract (it is imported as an indirect project)");
                return Canonical.None;
            }

            if (!seen.Add(number))
            {
                context.Run.Skip(r.FileName, r.Number, "Contract", number, "Another project in the file already creates this contract");
                return Canonical.None;
            }

            var values = Canonical.Copy(r, names);
            values["ContractNumber"] = number;
            values["Name"] = r.Get("Name") ?? number;
            values["Status"] = UnanetTerms.ContractStatus(r.Get("Status"));
            return Canonical.One(values);
        });
        return base.ProcessAsync(context, converted, cancellationToken);
    }
}

/// <summary>Unanet contract lines (CLINs/SLINs), where the site tracks them.</summary>
public sealed class UnanetClinImporter : ClinImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Contract lines (CLINs)";
    public override string Description => "CLINs and SLINs with value and cumulative funding, for contracts that track them. Optional.";
    public override string? SampleFile => "samples/import/unanet/clins.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new ClinImporter().Fields,
    [
        new Tweak("ContractNumber", ["Contract Number", "Contract No", "Contract ID", "Contract"]),
        new Tweak("ClinNumber", ["CLIN", "CLIN Number", "Contract Line", "Line Item", "SLIN"]),
        new Tweak("Description", ["CLIN Description", "Contract Line Description", "Line Description", "Description"]),
        new Tweak("Pricing", ["Pricing Type", "CLIN Type", "Billing Type", "Pricing", "Type"]),
        new Tweak("Ceiling", ["CLIN Value", "Line Value", "Contract Line Value", "Value", "Ceiling", "Amount"]),
        new Tweak("Funded", ["Funded Amount", "CLIN Funded", "Funded", "Funding"]),
    ]);
}

/// <summary>
/// Unanet projects and tasks → projects and their WBS tree. One file can hold project rows, task rows, or both (a report
/// that repeats the project on every task row). A task becomes <c>PROJECT.TASK</c>, nested under its parent task when
/// the file names one. A project with no tasks gets one general charge code (<c>PROJECT.00</c>), because Workbench
/// charges time to WBS elements.
/// </summary>
public sealed class UnanetProjectImporter : ProjectImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Projects and tasks";
    public override string Description => "Each Unanet project becomes a project linked to its contract, and each task becomes a WBS element under it. Internal projects (overhead, bid and proposal, leave) become indirect or leave charge codes.";
    public override string? SampleFile => "samples/import/unanet/projects.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new ProjectImporter().Fields,
    [
        new Tweak("ProjectId", ["Project Code", "Project ID", "Project Number", "Project"], Label: "Project code"),
        new Tweak("Name", ["Project Name", "Project Title", "Name"], Required: false, Label: "Project name"),
        new Tweak("Active", ["Project Status", "Status", "Active"], Description: "Active/Open or Closed. Defaults to open."),
        new Tweak("AllowCharges", ["Allow Time Entry", "Charge Allowed", "Chargeable", "Allow Charges"]),
        new Tweak("Indirect", ["Charge Type", "Direct/Indirect"], Description: "Direct, Indirect or Leave. Defaults from the project type.", Only: true),
        new Tweak("RequiresAssignment", ["Restrict to Assigned", "Assigned Only", "Requires Assignment", "Assignment Required"]),
        new Tweak("ContractNumber", ["Contract Number", "Contract No", "Contract ID", "Contract"], Description: "The contract made in the previous step. Blank = the project code."),
        new Tweak("Clin", ["CLIN", "Contract Line", "SLIN"]),
        new Tweak("Start", ["Start Date", "Project Start", "Task Start"]),
        new Tweak("End", ["End Date", "Project End", "Task End"]),
        new Tweak("Manager", ["Project Manager", "PM", "Manager"], Description: "Employee id or email of the project manager."),
    ], ["Level"],
        UnanetFields.Field("TaskCode", "Task code", "Blank on project rows. A task row becomes a WBS element under its project.", "Task Code", "Task", "Task ID", "Task Number"),
        UnanetFields.Field("TaskName", "Task name", "", "Task Name", "Task Title", "Task Description"),
        UnanetFields.Field("ParentTask", "Parent task", "Task code of the task this one sits under. Blank = directly under the project.", "Parent Task", "Parent Task Code"),
        UnanetFields.Field("Type", "Project type", "T&M, FFP, Cost plus... Overhead/bid and proposal projects become indirect, leave/PTO become leave.", "Project Type", "Contract Type", "Billing Type", "Type"));

    private static readonly string[] Canon =
        ["ProjectId", "Name", "Active", "AllowCharges", "Indirect", "RequiresAssignment", "ContractNumber", "Clin", "Start", "End", "Manager"];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var projectService = Service<IProjectService>(context);
        var existing = (await projectService.ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var hasWbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in existing.Values.Where(p => rows.Any(r => string.Equals(r.Get("ProjectId"), p.Code, StringComparison.OrdinalIgnoreCase))))
        {
            if ((await projectService.ListWbsAsync(project.Id, cancellationToken)).Count > 0)
            {
                hasWbs.Add(project.Code);
            }
        }

        var tasksOf = rows.Where(r => r.Get("TaskCode") is not null && r.Get("ProjectId") is not null)
            .ToLookup(r => r.Get("ProjectId")!, StringComparer.OrdinalIgnoreCase);
        var taskKeys = tasksOf.SelectMany(g => g.Select(r => Key(g.Key, r.Get("TaskCode")!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in tasksOf)
        {
            foreach (var r in g)
            {
                parentOf.TryAdd(Key(g.Key, r.Get("TaskCode")!), r.Get("ParentTask"));
            }
        }

        var projectRows = rows.Where(r => r.Get("TaskCode") is null && r.Get("ProjectId") is not null)
            .Select(r => r.Get("ProjectId")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usesType = rows.Count > 0 && (rows[0].IsMapped("Type") || rows[0].IsMapped("ContractNumber"));

        string TaskPath(string project, string task, int depth = 0)
        {
            task = task.StartsWith(project + ".", StringComparison.OrdinalIgnoreCase) ? task[(project.Length + 1)..] : task;
            return parentOf.GetValueOrDefault(Key(project, task)) is { } parent && depth < 20 && taskKeys.Contains(Key(project, parent))
                ? TaskPath(project, parent, depth + 1) + "." + task
                : task;
        }

        Dictionary<string, string?> ProjectValues(MappedRow r, string project)
        {
            var type = r.Get("Type");
            var charge = r.Get("Indirect") ?? UnanetTerms.Charge(type);
            var values = Canonical.Copy(r, Canon);
            values["ProjectId"] = project;
            values["Name"] = r.Get("Name") ?? project;
            values["Active"] = r.Get("Active") is { } status ? UnanetTerms.Open(status) : null;
            values["Indirect"] = charge;
            values["AllowCharges"] = "N";
            values["Clin"] = null;
            values["ContractNumber"] = UnanetTerms.Charge(type) == "Direct" && usesType ? r.Get("ContractNumber") ?? project : null;
            return values;
        }

        var converted = Canonical.Rewrite(context, rows, Canon, r =>
        {
            if (r.Get("ProjectId") is not { } project)
            {
                r.Error("ProjectId", "Project code is required.");
                return Canonical.None;
            }

            var output = new List<IReadOnlyDictionary<string, string?>>();
            if (r.Get("TaskCode") is not { } task)
            {
                if (r.Get("Name") is null)
                {
                    r.Error("Name", "Project name is required.");
                    return Canonical.None;
                }

                if (emitted.Add(project))
                {
                    output.Add(ProjectValues(r, project));
                    if (!taskKeys.Any(k => k.StartsWith(project + "\u0001", StringComparison.OrdinalIgnoreCase)) && !hasWbs.Contains(project))
                    {
                        output.Add(General(r, project));
                    }
                }
                else
                {
                    context.Run.Skip(r.FileName, r.Number, "Project", project, "The project is already listed on an earlier row");
                }

                return output;
            }

            if (r.Get("ParentTask") is { } parent && !taskKeys.Contains(Key(project, parent)))
            {
                r.Error("ParentTask", $"Parent task {parent} isn't in the file. List it too (it may already be imported, but its row is needed to place {task}).");
                return Canonical.None;
            }

            if (!projectRows.Contains(project) && !existing.ContainsKey(project) && r.Get("Name") is not null && emitted.Add(project))
            {
                // A report that repeats the project on every task row: the first one makes the project.
                output.Add(ProjectValues(r, project));
            }

            var values = Canonical.Copy(r, Canon);
            values["ProjectId"] = UnanetTerms.Wbs(project, TaskPath(project, task));
            values["Name"] = r.Get("TaskName") ?? task;
            values["Active"] = r.Get("Active") is { } active ? UnanetTerms.Open(active) : null;
            values["Indirect"] = null;
            values["ContractNumber"] = null;
            values["Start"] = null;
            values["End"] = null;
            values["Manager"] = null;
            output.Add(values);
            return output;
        });
        await base.ProcessAsync(context, converted, cancellationToken);
    }

    private static Dictionary<string, string?> General(MappedRow r, string project) => new()
    {
        ["ProjectId"] = UnanetTerms.Wbs(project, UnanetTerms.GeneralTask),
        ["Name"] = "General",
        ["AllowCharges"] = "Y",
    };

    private static string Key(string project, string task) => project + "\u0001" + task;
}

/// <summary>Unanet project bill rates → bill rates per labor category and contract.</summary>
public sealed class UnanetBillRateImporter : BillRateImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Bill rates";
    public override string Description => "Hourly bill rates per labor category, effective-dated. Rows name a project (its contract gets the rate) or a contract. Optional.";
    public override string? SampleFile => "samples/import/unanet/bill-rates.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new BillRateImporter().Fields,
    [
        new Tweak("ProjectId", ["Project Code", "Project ID", "Project"]),
        new Tweak("ContractNumber", ["Contract Number", "Contract No", "Contract"]),
        new Tweak("LaborCategory", ["Labor Category", "Labor Category Code", "LCAT"]),
        new Tweak("Rate", ["Bill Rate", "Billing Rate", "Hourly Bill Rate", "Rate"]),
        new Tweak("Start", ["Effective Date", "Start Date", "Rate Start"]),
    ]);
}

/// <summary>
/// Unanet project and task budgets (hours by labor category, other costs in dollars) → a budget version per project.
/// A row names the project and (optionally) the task; the task is found however deeply it was nested.
/// </summary>
public sealed class UnanetBudgetImporter : BudgetImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Budgets";
    public override string Description => "Budget hours by task and labor category (and other costs in dollars). Replaces the lines of the chosen budget version.";
    public override string? SampleFile => "samples/import/unanet/budgets.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new BudgetImporter().Fields,
    [
        new Tweak("WbsCode", ["Project Code", "Project ID", "Project", "WBS Code"], Label: "Project code", Description: "The project (add the task code below), or a full WBS code."),
        new Tweak("LaborCategory", ["Labor Category", "Labor Category Code", "LCAT"]),
        new Tweak("Hours", ["Budget Hours", "Planned Hours", "Estimated Hours", "Hours"]),
        new Tweak("Amount", ["Budget Amount", "Other Cost Budget", "Expense Budget", "Amount"]),
        new Tweak("OdcType", ["Expense Type", "Cost Type", "Other Cost Type"]),
    ], null,
        UnanetFields.Field("TaskCode", "Task code", "The task the budget is on. Blank = the project's general charge code.", "Task Code", "Task", "Task ID"));

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var wbs = await UnanetWbs.LoadAsync(Service<IProjectService>(context), cancellationToken);
        var names = new[] { "WbsCode", "LaborCategory", "Hours", "Amount", "FiscalYear", "Period", "OdcType" };
        var resolved = new Dictionary<MappedRow, string>();
        foreach (var row in rows)
        {
            if (row.Required("WbsCode") is not { } project)
            {
                continue;
            }

            if (project.Contains('.'))
            {
                resolved[row] = project;
                continue;
            }

            var (element, problem) = await wbs.FindAsync(project, row.Get("TaskCode"), cancellationToken);
            if (element is null)
            {
                row.Error("WbsCode", problem!);
                continue;
            }

            resolved[row] = element.Code;
        }

        var converted = Canonical.Rewrite(context, rows.Where(resolved.ContainsKey), names, r =>
        {
            var values = Canonical.Copy(r, names);
            values["WbsCode"] = resolved[r];
            return Canonical.One(values);
        });
        var options = string.IsNullOrWhiteSpace(context.Options.BudgetVersionName) ? context.Options with { BudgetVersionName = "Unanet budget" } : context.Options;
        await base.ProcessAsync(context with { Options = options }, converted, cancellationToken);
    }
}

/// <summary>Unanet vendors → vendors.</summary>
public sealed class UnanetVendorImporter : VendorImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Vendors";
    public override string Description => "Vendors with payment terms and 1099 flag. Needed before open AP invoices.";
    public override string? SampleFile => "samples/import/unanet/vendors.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new VendorImporter().Fields,
    [
        new Tweak("Code", ["Vendor Code", "Vendor ID", "Vendor Number", "Vendor"]),
        new Tweak("Name", ["Vendor Name", "Name"]),
        new Tweak("Active", ["Status", "Active"]),
        new Tweak("Is1099", ["1099 Vendor", "1099", "Is 1099"]),
        new Tweak("Terms", ["Payment Terms", "Terms", "Net Days"]),
    ]);
}

/// <summary>Unanet unpaid AP invoices → open AP bills (not posted: the AP balance comes with the opening balances).</summary>
public sealed class UnanetOpenApImporter : OpenApBillImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Open AP invoices";
    public override string Description => "Unpaid vendor invoices at cut-over, for their open balance. Not posted: the AP balance comes in with the opening balances. Optional (Unanet sites that run AP in Unanet).";
    public override string? SampleFile => "samples/import/unanet/open-ap.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new OpenApBillImporter().Fields,
    [
        new Tweak("Vendor", ["Vendor Code", "Vendor ID", "Vendor"]),
        new Tweak("InvoiceNumber", ["Vendor Invoice Number", "Invoice Number", "Invoice No", "Invoice #", "Vendor Invoice"]),
        new Tweak("Voucher", ["Voucher Number", "Voucher", "Document Number"]),
        new Tweak("Amount", ["Invoice Amount", "Total Amount", "Amount"]),
        new Tweak("Balance", ["Open Amount", "Amount Due", "Balance Due", "Outstanding", "Balance"]),
        new Tweak("Paid", ["Paid Amount", "Amount Paid"]),
    ]);
}

/// <summary>Unanet unpaid customer invoices → open AR invoices (not posted: the AR balance comes with the opening balances).</summary>
public sealed class UnanetOpenArImporter : OpenArInvoiceImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override string Title => "Open AR invoices";
    public override string Description => "Unpaid customer invoices at cut-over, with what's been paid. Not posted: the AR balance comes in with the opening balances. Optional (Unanet sites that bill from Unanet).";
    public override string? SampleFile => "samples/import/unanet/open-ar.csv";

    public override IReadOnlyList<ImportField> Fields { get; } = UnanetFields.Of(new OpenArInvoiceImporter().Fields,
    [
        new Tweak("InvoiceNumber", ["Invoice Number", "Invoice No", "Invoice #", "Invoice"]),
        new Tweak("Contract", ["Contract Number", "Contract No", "Contract"]),
        new Tweak("Project", ["Project Code", "Project ID", "Project"]),
        new Tweak("Customer", ["Customer Code", "Customer ID", "Customer"]),
        new Tweak("Amount", ["Invoice Amount", "Total Amount", "Billed Amount", "Amount"]),
        new Tweak("Balance", ["Open Amount", "Amount Due", "Balance Due", "Outstanding", "Balance"]),
        new Tweak("Paid", ["Paid Amount", "Amount Paid", "Received"]),
        new Tweak("Type", ["Invoice Type", "Billing Type", "Type"]),
    ]);
}

/// <summary>Unanet project and task assignments → charge assignments (who may charge what).</summary>
public sealed class UnanetAssignmentImporter : CostpointImporter
{
    public override ImportSource Source => ImportSource.Unanet;
    public override ImportKind Kind => ImportKind.Assignments;
    public override string Title => "Assignments";
    public override string Description => "Who is assigned to which project or task, and as which labor category. People can charge only what they are assigned to (unless the project allows everyone).";
    public override string? SampleFile => "samples/import/unanet/assignments.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("EmployeeNumber", "Employee ID", true, "The person (import people first).", "Employee ID", "Employee Number", "Person ID", "Username", "User Name", "Person"),
        new("Project", "Project code", true, "The project (import projects first).", "Project Code", "Project ID", "Project"),
        new("Task", "Task code", false, "Blank = the project's general charge code.", "Task Code", "Task", "Task ID"),
        new("LaborCategory", "Labor category", false, "Labor category the person charges as on it.", "Labor Category", "Labor Category Code", "LCAT"),
        new("From", "Assigned from", false, "", "Assignment Start", "Start Date", "From", "Begin Date"),
        new("To", "Assigned to", false, "", "Assignment End", "End Date", "To", "Finish Date"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var projects = Service<IProjectService>(context);
        var wbs = await UnanetWbs.LoadAsync(projects, cancellationToken);
        var people = Service<IPeopleService>(context);
        var employees = (await people.ListEmployeesAsync(includeInactive: true, cancellationToken)).ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var categories = (await people.ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<Guid, IReadOnlyList<ChargeAssignmentDto>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var number = row.Required("EmployeeNumber");
            var project = row.Required("Project");
            var from = row.Date("From");
            var to = row.Date("To");
            if (number is not null && !employees.ContainsKey(number))
            {
                row.Error("EmployeeNumber", $"Employee {number} isn't in Workbench; import people first.");
            }

            Guid? categoryId = null;
            if (row.Get("LaborCategory") is { } plc)
            {
                if (categories.TryGetValue(plc, out var c))
                {
                    categoryId = c;
                }
                else
                {
                    row.Error("LaborCategory", $"Labor category {plc} isn't in Workbench; import labor categories first.");
                }
            }

            if (from is not null && to is not null && to < from)
            {
                row.Error("To", "The assignment ends before it starts.");
            }

            WbsElementDto? element = null;
            if (project is not null)
            {
                var (found, problem) = await wbs.FindAsync(project, row.Get("Task"), cancellationToken);
                element = found;
                if (found is null)
                {
                    row.Error("Task", problem!);
                }
            }

            if (row.HasErrors || number is null || element is null)
            {
                continue;
            }

            var key = $"{number}|{element.Code}|{categoryId}|{from:O}|{to:O}";
            if (!seen.Add(key))
            {
                context.Run.Skip(row.FileName, row.Number, "Assignment", $"{number} on {element.Code}", "Listed twice in the file");
                continue;
            }

            if (!current.TryGetValue(element.Id, out var list))
            {
                current[element.Id] = list = context.Run.CreatedIds.Contains(element.Id) ? [] : await projects.ListAssignmentsAsync(element.Id, cancellationToken);
            }

            var employee = employees[number];
            if (list.Any(a => a.EmployeeId == employee.Id && a.LaborCategoryId == categoryId && a.From == from && a.To == to))
            {
                context.Run.Skip(row.FileName, row.Number, "Assignment", $"{number} on {element.Code}", "Already assigned");
                continue;
            }

            var label = $"{employee.DisplayName} on {element.Code}";
            var summary = $"{label}{(from is null ? "" : $" from {from:yyyy-MM-dd}")}{(to is null ? "" : $" to {to:yyyy-MM-dd}")}";
            var request = new AddChargeAssignmentRequest(employee.Id, categoryId, from, to);
            await context.Run.CreateAsync(row.FileName, row.Number, typeof(ChargeAssignment), "Assignment", $"{number} on {element.Code}", summary,
                async () => (await projects.AddAssignmentAsync(element.Id, request, cancellationToken)).Id, cancellationToken);
        }
    }
}
