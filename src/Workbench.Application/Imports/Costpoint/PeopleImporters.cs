using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>Costpoint organizations (Organizations screen / ORG export) → org units, matched on the org id.</summary>
public class OrgUnitImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.OrgUnits;
    public override string Title => "Organizations";
    public override string Description => "Costpoint organizations become org units. Dotted org ids (1.01.10) nest under their parent.";
    public override string? SampleFile => "samples/import/costpoint/organizations.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("OrgId", "Org ID", true, "Costpoint org id, e.g. 1.01.10.", "ORG_ID", "Org", "Organization", "Organization ID", "Org Code"),
        new("Name", "Org name", true, "Display name.", "ORG_NAME", "Organization Name", "ORG_DESC", "Description", "Name"),
        new("ParentOrgId", "Parent org", false, "Parent org id. Defaults to the dotted prefix when that org exists.", "PARENT_ORG_ID", "Parent Org", "Parent"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var people = Service<IPeopleService>(context);
        var setup = Service<ISetupAdminService>(context);
        var existing = (await people.ListOrgUnitsAsync(cancellationToken)).ToDictionary(o => o.Code, StringComparer.OrdinalIgnoreCase);
        var ids = existing.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var inFile = rows.Select(r => r.Get("OrgId")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Parents before children.
        foreach (var row in rows.OrderBy(r => Depth(r.Get("OrgId") ?? "")))
        {
            var code = row.Required("OrgId");
            var name = row.Required("Name");
            if (row.HasErrors || code is null || name is null)
            {
                continue;
            }

            var parentCode = row.Get("ParentOrgId") ?? ParentCode(code);
            Guid? parentId = null;
            if (parentCode is not null)
            {
                if (ids.TryGetValue(parentCode, out var pid))
                {
                    parentId = pid;
                }
                else if (row.IsMapped("ParentOrgId") && row.Get("ParentOrgId") is not null && !inFile.Contains(parentCode))
                {
                    row.Error("ParentOrgId", $"Parent org '{parentCode}' isn't in the file or in Workbench.");
                    continue;
                }
            }

            if (existing.TryGetValue(code, out var unit))
            {
                var changed = Changed(("Name", unit.Name, name), ("Parent", unit.ParentId, parentId));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Org unit", code, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(OrgUnit), unit.Id, "Org unit", code, UpdateSummary(changed),
                    () => setup.UpdateOrgUnitAsync(unit.Id, new CreateOrgUnitRequest(code, name, parentId), cancellationToken), cancellationToken);
                continue;
            }

            var id = await context.Run.CreateAsync(row.FileName, row.Number, typeof(OrgUnit), "Org unit", code, name,
                async () => (await people.CreateOrgUnitAsync(new CreateOrgUnitRequest(code, name, parentId), cancellationToken)).Id, cancellationToken);
            if (id is { } created)
            {
                ids[code] = created;
            }
        }
    }
}

/// <summary>Costpoint project labor categories (PLCs) → labor categories, matched on the PLC code.</summary>
public class LaborCategoryImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.LaborCategories;
    public override string Title => "Labor categories (PLCs)";
    public override string Description => "Project labor categories, used for budgets and bill rates.";
    public override string? SampleFile => "samples/import/costpoint/labor-categories.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Code", "PLC", true, "Labor category code.", "BILL_LAB_CAT_CD", "LAB_CAT_CD", "PLC", "PLC Code", "GENL_LAB_CAT_CD", "Labor Category", "LCAT", "Labor Category Code"),
        new("Name", "Description", true, "Labor category name.", "BILL_LAB_CAT_DESC", "LAB_CAT_DESC", "PLC Description", "GENL_LAB_CAT_DESC", "Description", "Name", "Labor Category Name"),
        new("Active", "Active", false, "Y/N. Defaults to Y.", "ACTIVE_FL", "Active Flag", "Status"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var people = Service<IPeopleService>(context);
        var existing = (await people.ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = row.Required("Code");
            var name = row.Required("Name");
            var active = row.Bool("Active") ?? true;
            if (row.HasErrors || code is null || name is null)
            {
                continue;
            }

            if (!seen.Add(code))
            {
                row.Error("Code", $"PLC {code} appears more than once in the file.");
                continue;
            }

            var request = new SaveLaborCategoryRequest(code, name, active);
            if (existing.TryGetValue(code, out var category))
            {
                var changed = Changed(("Name", category.Name, name), ("Active", category.IsActive, active));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Labor category", code, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(LaborCategory), category.Id, "Labor category", code, UpdateSummary(changed),
                    () => people.UpdateLaborCategoryAsync(category.Id, request, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(LaborCategory), "Labor category", code, name,
                async () => (await people.CreateLaborCategoryAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }
}

/// <summary>
/// Costpoint employees (Employee Basic Info / employee export) → employees, matched on the employee id. A PLC column
/// sets the default labor category (creating missing PLCs), and an hourly rate column adds an effective-dated cost rate.
/// </summary>
public class EmployeeImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Employees;
    public override string Title => "Employees";
    public override string Description => "Employees with their home org, labor category (PLC), supervisor and current hourly cost rate.";
    public override string? SampleFile => "samples/import/costpoint/employees.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("EmployeeNumber", "Employee ID", true, "Natural key: the Costpoint employee id.", "EMPL_ID", "Employee ID", "Emp ID", "Employee Number", "Employee", "Empl Id"),
        new("Name", "Name", false, "Full name; \"Last, First\" is turned around. Or map first and last name.", "EMPL_NAME", "LAST_FIRST_NAME", "Employee Name", "Full Name", "Name"),
        new("FirstName", "First name", false, "", "FIRST_NAME", "First", "Given Name"),
        new("LastName", "Last name", false, "", "LAST_NAME", "Last", "Surname", "Family Name"),
        new("Email", "Email", true, "Work email; links the employee to their sign-in.", "EMAIL_ID", "EMAIL", "Email Address", "Work Email", "E-mail"),
        new("OrgId", "Home org", false, "Org id of an existing org unit.", "ORG_ID", "Home Org", "Org", "Organization"),
        new("LaborCategory", "PLC", false, "Default labor category code; created when missing.", "BILL_LAB_CAT_CD", "GENL_LAB_CAT_CD", "PLC", "LAB_CAT_CD", "Labor Category"),
        new("LaborCategoryName", "PLC description", false, "Name for a PLC created by the import.", "BILL_LAB_CAT_DESC", "GENL_LAB_CAT_DESC", "PLC Description", "Labor Category Name"),
        new("HireDate", "Hire date", true, "", "ORIG_HIRE_DT", "HIRE_DT", "ADJ_HIRE_DT", "Hire Date", "Start Date"),
        new("TerminationDate", "Termination date", false, "", "TERM_DT", "Termination Date", "End Date"),
        new("Supervisor", "Supervisor", false, "Supervisor's employee id (in the file or in Workbench).", "SUPERVISOR_EMPL_ID", "MGR_EMPL_ID", "Supervisor", "Manager", "Manager ID", "Supervisor ID"),
        new("StandardHours", "Standard hours/week", false, "Defaults to 40.", "STD_EST_HRS", "STD_HRS", "Hours Per Week", "Standard Hours"),
        new("Exempt", "Exempt", false, "Y/N. Defaults to Y.", "EXMPT_FL", "EXEMPT_FL", "Exempt", "FLSA Exempt"),
        new("HourlyCost", "Hourly cost", false, "Hourly cost rate; added as an effective-dated rate.", "HRLY_AMT", "Hourly Rate", "Cost Rate", "Hourly Cost", "LAB_COST_AMT"),
        new("RateEffective", "Rate effective", false, "Effective date of the hourly cost; defaults to the hire date.", "EFFECT_DT", "Effective Date", "Rate Effective Date", "Rate Start"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var people = Service<IPeopleService>(context);
        var employees = (await people.ListEmployeesAsync(includeInactive: true, cancellationToken)).ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var ids = employees.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var orgs = (await people.ListOrgUnitsAsync(cancellationToken)).ToDictionary(o => o.Code, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var categories = (await people.ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var inFile = rows.Select(r => r.Get("EmployeeNumber")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supervisors = new List<(MappedRow Row, string Number, string Supervisor)>();

        foreach (var row in rows)
        {
            var number = row.Required("EmployeeNumber");
            var email = row.Required("Email")?.ToLowerInvariant();
            var name = row.Get("Name") is { } full ? PersonName(full) : $"{row.Get("FirstName")} {row.Get("LastName")}".Trim();
            if (name.Length == 0)
            {
                row.Error("Name", "Map a name column, or first and last name.");
            }

            var hired = row.Date("HireDate");
            if (hired is null && row.Get("HireDate") is null)
            {
                row.Error("HireDate", "Hire date is required.");
            }

            var terminated = row.Date("TerminationDate");
            var hours = row.Decimal("StandardHours") ?? 40m;
            var exempt = row.Bool("Exempt") ?? true;
            var cost = row.Decimal("HourlyCost");
            var effective = row.Date("RateEffective");
            if (email is not null && !email.Contains('@'))
            {
                row.Error("Email", $"Email '{email}' isn't an email address.");
            }

            if (cost is <= 0)
            {
                row.Error("HourlyCost", "Hourly cost must be greater than zero.");
            }

            if (number is not null && !seen.Add(number))
            {
                row.Error("EmployeeNumber", $"Employee {number} appears more than once in the file.");
            }

            Guid? orgId = null;
            if (row.Get("OrgId") is { } orgCode)
            {
                if (orgs.TryGetValue(orgCode, out var o))
                {
                    orgId = o;
                }
                else
                {
                    row.Warning("OrgId", $"Org '{orgCode}' isn't in Workbench; import organizations first. The employee is imported without a home org.");
                }
            }

            if (row.Get("Supervisor") is { } sup && !ids.ContainsKey(sup) && !inFile.Contains(sup))
            {
                row.Error("Supervisor", $"Supervisor '{sup}' isn't in the file or in Workbench.");
            }

            if (row.HasErrors || number is null || email is null || hired is null)
            {
                continue;
            }

            // Default labor category, creating the PLC when the file names one Workbench doesn't have yet.
            Guid? categoryId = null;
            if (row.Get("LaborCategory") is { } plc)
            {
                if (!categories.TryGetValue(plc, out var c))
                {
                    var plcName = row.Get("LaborCategoryName") ?? plc;
                    var createdPlc = await context.Run.CreateAsync(row.FileName, row.Number, typeof(LaborCategory), "Labor category", plc, plcName,
                        async () => (await people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest(plc, plcName), cancellationToken)).Id, cancellationToken);
                    if (createdPlc is not { } newId)
                    {
                        continue;
                    }

                    categories[plc] = c = newId;
                }

                categoryId = c;
            }

            // Supervisors are set in a second pass, once everyone in the file exists.
            var existingSupervisor = employees.TryGetValue(number, out var current) ? current.SupervisorId : null;
            var request = new SaveEmployeeRequest(number, email, name, orgId, existingSupervisor, categoryId ?? current?.DefaultLaborCategoryId,
                hired.Value, terminated, hours, exempt);
            Guid? employeeId;
            if (current is not null)
            {
                employeeId = current.Id;
                var changed = Changed(("Email", current.Email, email), ("Name", current.DisplayName, name), ("Org", current.OrgUnitId, orgId),
                    ("PLC", current.DefaultLaborCategoryId, request.DefaultLaborCategoryId), ("Hire date", current.HireDate, hired),
                    ("Termination date", current.TerminationDate, terminated), ("Standard hours", current.StandardHoursPerWeek, hours),
                    ("Exempt", current.IsExempt, exempt));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Employee", number, "Unchanged");
                }
                else if (!await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Employee), current.Id, "Employee", number, UpdateSummary(changed),
                    () => people.UpdateEmployeeAsync(current.Id, request, cancellationToken), cancellationToken))
                {
                    continue;
                }
            }
            else
            {
                employeeId = await context.Run.CreateAsync(row.FileName, row.Number, typeof(Employee), "Employee", number, $"{name} <{email}>",
                    async () => (await people.CreateEmployeeAsync(request, cancellationToken)).Id, cancellationToken);
                if (employeeId is null)
                {
                    continue;
                }

                ids[number] = employeeId.Value;
            }

            if (row.Get("Supervisor") is { } supervisor)
            {
                supervisors.Add((row, number, supervisor));
            }

            if (cost is { } hourly)
            {
                await CostRates.AddAsync(context, people, row, number, employeeId.Value, current is null, effective ?? hired.Value, hourly, cancellationToken);
            }
        }

        foreach (var (row, number, supervisorNumber) in supervisors)
        {
            if (!ids.TryGetValue(supervisorNumber, out var supervisorId) || !ids.TryGetValue(number, out var employeeId))
            {
                continue;
            }

            if (employees.TryGetValue(number, out var before) && before.SupervisorId == supervisorId)
            {
                continue;
            }

            await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Employee), employeeId, "Supervisor", number, $"Supervisor {supervisorNumber}",
                async () =>
                {
                    var employee = await people.GetEmployeeAsync(employeeId, cancellationToken) ?? throw new ArgumentException($"Employee {number} wasn't saved.");
                    await people.UpdateEmployeeAsync(employeeId, new SaveEmployeeRequest(employee.EmployeeNumber, employee.Email, employee.DisplayName,
                        employee.OrgUnitId, supervisorId, employee.DefaultLaborCategoryId, employee.HireDate, employee.TerminationDate,
                        employee.StandardHoursPerWeek, employee.IsExempt), cancellationToken);
                }, cancellationToken);
        }
    }
}

/// <summary>Costpoint employee salary/rate history → effective-dated cost rates, matched on employee + effective date.</summary>
public class CostRateImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.CostRates;
    public override string Title => "Employee cost rates";
    public override string Description => "Effective-dated hourly cost rates (Employee Salary Info). Rates are append-only: an existing date with a different amount is an error.";
    public override string? SampleFile => "samples/import/costpoint/cost-rates.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("EmployeeNumber", "Employee ID", true, "", "EMPL_ID", "Employee ID", "Emp ID", "Employee Number", "Employee"),
        new("EffectiveDate", "Effective date", true, "", "EFFECT_DT", "Effective Date", "START_DT", "Start Date", "Rate Effective Date"),
        new("HourlyCost", "Hourly cost", true, "", "HRLY_AMT", "Hourly Rate", "Cost Rate", "Hourly Cost", "LAB_COST_AMT", "Rate"),
        new("Reason", "Reason", false, "Why the rate changed; defaults to the import job.", "COMMENTS", "Reason", "Note", "S_SAL_CHG_RSN_CD", "Change Reason"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var people = Service<IPeopleService>(context);
        var employees = (await people.ListEmployeesAsync(includeInactive: true, cancellationToken)).ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var number = row.Required("EmployeeNumber");
            var effective = row.Date("EffectiveDate");
            var cost = row.Decimal("HourlyCost");
            if (row.Get("EffectiveDate") is null)
            {
                row.Error("EffectiveDate", "Effective date is required.");
            }

            if (row.Get("HourlyCost") is null)
            {
                row.Error("HourlyCost", "Hourly cost is required.");
            }
            else if (cost is <= 0)
            {
                row.Error("HourlyCost", "Hourly cost must be greater than zero.");
            }

            if (number is not null && !employees.ContainsKey(number))
            {
                row.Error("EmployeeNumber", $"Employee {number} isn't in Workbench; import employees first.");
            }

            if (row.HasErrors || number is null || effective is null || cost is null)
            {
                continue;
            }

            await CostRates.AddAsync(context, people, row, number, employees[number].Id, false, effective.Value, cost.Value, cancellationToken, row.Get("Reason"));
        }
    }
}

internal static class CostRates
{
    /// <summary>Adds an hourly cost effective <paramref name="from"/> unless the same rate is already there.</summary>
    public static async Task AddAsync(ImportContext context, IPeopleService people, MappedRow row, string number, Guid employeeId, bool newEmployee,
        DateOnly from, decimal hourly, CancellationToken cancellationToken, string? reason = null)
    {
        var key = $"{number} {from:yyyy-MM-dd}";
        IReadOnlyList<CostRateDto> rates = newEmployee ? [] : await people.ListCostRatesAsync(employeeId, cancellationToken);
        if (rates.FirstOrDefault(r => r.EffectiveFrom == from) is { } same)
        {
            if (same.HourlyCost == hourly)
            {
                context.Run.Skip(row.FileName, row.Number, "Cost rate", key, "Unchanged");
            }
            else
            {
                row.Error("HourlyCost", $"Employee {number} already has a different cost rate effective {from:yyyy-MM-dd}. Rates are append-only: add the correction with a new effective date.");
            }

            return;
        }

        await context.Run.CreateAsync(row.FileName, row.Number, typeof(EmployeeCostRate), "Cost rate", key, $"{hourly:0.00}/h from {from:yyyy-MM-dd}",
            async () => (await people.AddCostRateAsync(employeeId, new AddCostRateRequest(from, hourly, reason ?? $"Imported from {CostpointImporter.SourceName(context)} (import job {context.Job.Id.ToString()[..8]})"), cancellationToken)).Id,
            cancellationToken);
    }
}
