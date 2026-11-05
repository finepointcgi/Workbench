using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>
/// Costpoint projects (Project Master / project export) → projects and their WBS. A top-level id (<c>2001</c>) is the
/// project; every deeper id (<c>2001.01</c>, <c>2001.01.02</c>) is a WBS element under the element of its dotted
/// prefix. Matched on project code and WBS code.
/// </summary>
public class ProjectImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Projects;
    public override string Title => "Projects and WBS";
    public override string Description => "Projects with their full WBS tree (Costpoint project id levels such as 2001.01.02), charge flags, open/closed and direct vs. indirect.";
    public override string? SampleFile => "samples/import/costpoint/projects.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("ProjectId", "Project ID", true, "Costpoint project id at any level: 2001 is the project, 2001.01.02 a WBS element.", "PROJ_ID", "Project", "Project ID", "Project Code", "Project Number", "WBS", "WBS Code"),
        new("Name", "Name", true, "", "PROJ_NAME", "PROJ_DESC", "Project Name", "Name", "Description", "Title"),
        new("Level", "Level", false, "Costpoint level number; used only as a check against the id.", "LVL_NO", "Level", "Level No", "PROJ_LVL_NO"),
        new("Active", "Active", false, "Y = open/active, N = closed. Defaults to Y.", "ACTIVE_FL", "Active", "Status", "Open", "S_PROJ_STATUS_CD"),
        new("AllowCharges", "Allow charges", false, "Y when time and costs may be charged. Defaults to Y on the lowest level.", "ALLOW_CHARGES_FL", "ALLOW_CHRG_FL", "Charge Allowed", "Chargeable", "Allow Charges", "Charge Flag"),
        new("Indirect", "Direct / indirect", false, "Direct, Indirect or Leave (also Y/N for an indirect flag). Defaults to the project's.", "PROJ_TYPE_DC", "PROJ_TYPE", "INDIRECT_FL", "Project Type", "Direct/Indirect", "Charge Type", "Type"),
        new("RequiresAssignment", "Restrict to assigned", false, "Y when only assigned employees may charge. Defaults to Y for direct, N for indirect and leave.", "LIMIT_TO_EMPL_FL", "Restrict Employees", "Requires Assignment", "Employee Restrictions"),
        new("ContractNumber", "Contract", false, "Contract number of an existing contract (project level).", "PRIME_CONTR_ID", "CONTRACT_ID", "Contract", "Contract Number"),
        new("Clin", "CLIN", false, "CLIN or SLIN this WBS element charges to.", "CLIN_ID", "CLIN", "SLIN", "Line Item"),
        new("Start", "Start", false, "Project start (project level).", "PROJ_START_DT", "START_DT", "POP_START_DT", "Start Date", "Start"),
        new("End", "End", false, "Project end (project level).", "PROJ_END_DT", "END_DT", "POP_END_DT", "End Date", "Finish", "End"),
        new("Manager", "Project manager", false, "Employee id (or email) of the project manager.", "PROJ_MGR_EMPL_ID", "PM_EMPL_ID", "PROJ_MGR_ID", "Project Manager", "PM", "Manager"),
    ];

    internal static ChargeCodeKind? ParseCharge(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        null or "" or "D" or "DIRECT" or "N" or "DIR" or "CONTRACT" => ChargeCodeKind.Direct,
        "Y" or "I" or "IND" or "INDIRECT" or "OH" or "OVERHEAD" or "G&A" or "GA" or "B&P" or "BP" or "IR&D" or "IRAD" or "FRINGE" => ChargeCodeKind.Indirect,
        "L" or "LV" or "LEAVE" or "PTO" or "HOL" or "HOLIDAY" => ChargeCodeKind.Leave,
        _ => null,
    };

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var projectsService = Service<IProjectService>(context);
        var clinService = Service<IClinService>(context);
        var projects = (await projectsService.ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var contracts = (await Service<IContractService>(context).ListContractsAsync(cancellationToken)).ToDictionary(c => c.ContractNumber, StringComparer.OrdinalIgnoreCase);
        var employees = await Service<IPeopleService>(context).ListEmployeesAsync(includeInactive: true, cancellationToken);
        var projectIds = projects.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var projectContract = projects.Values.ToDictionary(p => p.Code, p => p.ContractId, StringComparer.OrdinalIgnoreCase);
        var projectKind = new Dictionary<string, ChargeCodeKind>(StringComparer.OrdinalIgnoreCase);

        // Every WBS element of the projects in the file, keyed by code (codes are unique across the organization).
        var wbs = new Dictionary<string, WbsElementDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in rows.Select(r => r.Get("ProjectId")?.Split('.')[0]).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (projects.TryGetValue(code, out var p))
            {
                foreach (var w in await projectsService.ListWbsAsync(p.Id, cancellationToken))
                {
                    wbs[w.Code] = w;
                }
            }
        }

        var wbsIds = wbs.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var allCodes = rows.Select(r => r.Get("ProjectId")).OfType<string>().Concat(wbs.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clinCache = new Dictionary<Guid, IReadOnlyList<ClinDto>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.OrderBy(r => Depth(r.Get("ProjectId") ?? "")).ThenBy(r => r.Get("ProjectId"), StringComparer.Ordinal))
        {
            var code = row.Required("ProjectId");
            var name = row.Required("Name");
            var active = row.Bool("Active") ?? true;
            var chargeText = row.Get("Indirect");
            var charge = ParseCharge(chargeText);
            if (charge is null)
            {
                row.Error("Indirect", $"'{chargeText}' isn't Direct, Indirect or Leave.");
            }

            if (code is not null && !seen.Add(code))
            {
                row.Error("ProjectId", $"{code} appears more than once in the file.");
            }

            if (code is not null && row.Decimal("Level") is { } level && level != Depth(code) + 1 && level != Depth(code))
            {
                row.Warning("Level", $"Level {level} doesn't match the id {code}; the id's dots decide the tree.");
            }

            if (row.HasErrors || code is null || name is null || charge is null)
            {
                continue;
            }

            if (Depth(code) == 0)
            {
                projectKind[code] = charge.Value;
                await ImportProjectAsync(context, row, code, name, active, projects, projectIds, projectContract, contracts, employees, cancellationToken);
                continue;
            }

            var projectCode = code.Split('.')[0];
            if (!projectIds.TryGetValue(projectCode, out var projectId))
            {
                row.Error("ProjectId", $"Project {projectCode} isn't in the file or in Workbench.");
                continue;
            }

            var parentCode = ParentCode(code)!;
            Guid? parentId = null;
            if (!string.Equals(parentCode, projectCode, StringComparison.OrdinalIgnoreCase))
            {
                if (!wbsIds.TryGetValue(parentCode, out var pid))
                {
                    row.Error("ProjectId", $"{code}'s parent {parentCode} isn't in the file or in Workbench.");
                    continue;
                }

                parentId = pid;
            }

            var kind = chargeText is null && projectKind.TryGetValue(projectCode, out var inherited) ? inherited
                : chargeText is null && wbs.Values.FirstOrDefault(w => w.ProjectId == projectId) is { } sibling ? sibling.Kind
                : charge.Value;
            var isLeaf = !allCodes.Any(c => c.StartsWith(code + ".", StringComparison.OrdinalIgnoreCase));
            var chargeable = row.Bool("AllowCharges") ?? isLeaf;
            var restricted = row.Bool("RequiresAssignment") ?? kind == ChargeCodeKind.Direct;
            var request = new SaveWbsElementRequest(parentId, code, name, kind, chargeable, active, restricted);

            Guid? clinId = null;
            if (row.Get("Clin") is { } clinNumber)
            {
                if (projectContract.GetValueOrDefault(projectCode) is not { } contractId)
                {
                    row.Error("Clin", $"Project {projectCode} has no contract, so {code} can't map to CLIN {clinNumber}.");
                    continue;
                }

                if (!context.Run.CreatedIds.Contains(contractId))
                {
                    if (!clinCache.TryGetValue(contractId, out var list))
                    {
                        clinCache[contractId] = list = await clinService.ListClinsAsync(contractId, cancellationToken);
                    }

                    if (list.FirstOrDefault(c => string.Equals(c.Number, clinNumber, StringComparison.OrdinalIgnoreCase)) is not { } clin)
                    {
                        row.Error("Clin", $"CLIN {clinNumber} isn't on project {projectCode}'s contract; import CLINs first.");
                        continue;
                    }

                    clinId = clin.Id;
                }
            }

            if (wbs.TryGetValue(code, out var element))
            {
                var changed = Changed(("Name", element.Name, name), ("Parent", element.ParentId, parentId), ("Kind", element.Kind, kind),
                    ("Chargeable", element.IsChargeable, chargeable), ("Open", element.IsOpen, active), ("Restricted", element.RequiresAssignment, restricted));
                var clinChanged = row.Get("Clin") is not null && await CurrentClinAsync(context, element.Id, cancellationToken) != clinId;
                if (clinChanged)
                {
                    changed.Add("CLIN");
                }

                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "WBS element", code, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(WbsElement), element.Id, "WBS element", code, UpdateSummary(changed), async () =>
                {
                    await projectsService.UpdateWbsElementAsync(element.Id, request, cancellationToken);
                    if (clinChanged)
                    {
                        await clinService.SetWbsClinAsync(element.Id, clinId, cancellationToken);
                    }
                }, cancellationToken);
                continue;
            }

            var summary = $"{name} · {kind}{(chargeable ? ", chargeable" : "")}{(active ? "" : ", closed")}{(row.Get("Clin") is { } c ? $" · CLIN {c}" : "")}";
            var created = await context.Run.CreateAsync(row.FileName, row.Number, typeof(WbsElement), "WBS element", code, summary, async () =>
            {
                var dto = await projectsService.CreateWbsElementAsync(projectId, request, cancellationToken);
                if (clinId is { } clin)
                {
                    await clinService.SetWbsClinAsync(dto.Id, clin, cancellationToken);
                }

                return dto.Id;
            }, cancellationToken);
            if (created is { } id)
            {
                wbsIds[code] = id;
            }
        }
    }

    private static async Task<Guid?> CurrentClinAsync(ImportContext context, Guid elementId, CancellationToken cancellationToken)
    {
        await using var db = await context.Services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(cancellationToken);
        return await db.Set<WbsElement>().Where(w => w.Id == elementId).Select(w => w.ClinId).FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task ImportProjectAsync(ImportContext context, MappedRow row, string code, string name, bool active,
        Dictionary<string, ProjectDto> projects, Dictionary<string, Guid> projectIds, Dictionary<string, Guid?> projectContract,
        Dictionary<string, ContractDto> contracts, IReadOnlyList<EmployeeDto> employees, CancellationToken cancellationToken)
    {
        var service = Service<IProjectService>(context);
        var start = row.Date("Start");
        var end = row.Date("End");
        Guid? contractId = null;
        if (row.Get("ContractNumber") is { } number)
        {
            if (contracts.TryGetValue(number, out var contract))
            {
                contractId = contract.Id;
            }
            else
            {
                row.Error("ContractNumber", $"Contract {number} isn't in Workbench; import contracts first.");
            }
        }

        Guid? managerId = null;
        if (row.Get("Manager") is { } manager)
        {
            var employee = employees.FirstOrDefault(e => string.Equals(e.EmployeeNumber, manager, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Email, manager, StringComparison.OrdinalIgnoreCase));
            if (employee is null)
            {
                row.Warning("Manager", $"Project manager '{manager}' isn't an employee in Workbench; the project is imported without one.");
            }

            managerId = employee?.Id;
        }

        if (row.HasErrors)
        {
            return;
        }

        if (projects.TryGetValue(code, out var project))
        {
            var status = active ? (project.Status == ProjectState.Closed ? ProjectState.Active : project.Status) : ProjectState.Closed;
            var request = new SaveProjectRequest(code, name, contractId ?? project.ContractId, managerId ?? project.ProjectManagerId, status, start ?? project.Start, end ?? project.Finish);
            projectContract[code] = request.ContractId;
            var changed = Changed(("Name", project.Name, name), ("Contract", project.ContractId, request.ContractId), ("Manager", project.ProjectManagerId, request.ProjectManagerId),
                ("Status", project.Status, status), ("Start", project.Start, request.Start), ("End", project.Finish, request.Finish));
            if (changed.Count == 0)
            {
                context.Run.Skip(row.FileName, row.Number, "Project", code, "Unchanged");
                return;
            }

            await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Project), project.Id, "Project", code, UpdateSummary(changed),
                () => service.UpdateProjectAsync(project.Id, request, cancellationToken), cancellationToken);
            return;
        }

        var create = new SaveProjectRequest(code, name, contractId, managerId, active ? ProjectState.Active : ProjectState.Closed, start, end);
        var id = await context.Run.CreateAsync(row.FileName, row.Number, typeof(Project), "Project", code, name,
            async () => (await service.CreateProjectAsync(create, cancellationToken)).Id, cancellationToken);
        if (id is { } created)
        {
            projectIds[code] = created;
            projectContract[code] = contractId;
        }
    }
}

/// <summary>
/// Costpoint budgets / EACs (Project Budgets, EAC export) by WBS and PLC, optionally by fiscal period → a budget
/// version per project, matched on project + version name. Rows with a PLC are labor hours (priced at Workbench's
/// planning rates); rows without one are ODC dollars.
/// </summary>
public class BudgetImporter : CostpointImporter
{
    public const string DefaultVersionName = "Costpoint budget";

    public override ImportKind Kind => ImportKind.Budgets;
    public override string Title => "Budgets and EACs";
    public override string Description => "Budget hours by WBS and PLC (and ODC dollars), whole-project or by fiscal period. Replaces the lines of the chosen budget version.";
    public override string? SampleFile => "samples/import/costpoint/budgets.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("WbsCode", "Project / WBS ID", true, "The WBS element the amount is budgeted on (Costpoint project id at the budget level).", "PROJ_ID", "WBS", "WBS Code", "WBS ID", "Project ID", "Project"),
        new("LaborCategory", "PLC", false, "Labor category; leave blank for ODC dollars.", "BILL_LAB_CAT_CD", "LAB_CAT_CD", "PLC", "Labor Category"),
        new("Hours", "Hours", false, "Budget (or EAC) hours for labor rows.", "BUD_HRS", "EAC_HRS", "BUDGET_HRS", "Hours", "Budget Hours", "EAC Hours"),
        new("Amount", "Amount", false, "Dollars for ODC rows (labor rows are priced from hours).", "BUD_AMT", "EAC_AMT", "BUDGET_AMT", "Amount", "Budget Amount", "EAC Amount", "Cost"),
        new("FiscalYear", "Fiscal year", false, "With the period number, phases the amount into a fiscal period.", "FY_CD", "Fiscal Year", "FY"),
        new("Period", "Period", false, "Period number (1–12) with the fiscal year, or a month as yyyy-MM. Blank or TOTAL = unphased.", "PD_NO", "Period", "Fiscal Period", "Month"),
        new("OdcType", "ODC type", false, "Travel, Materials, Equipment, Subcontract, Consultant or Other (ODC rows).", "ODC_TYPE", "Cost Element", "ACCT_DESC", "Expense Type", "ODC Type"),
    ];

    private sealed record Line(MappedRow Row, WbsElementDto Wbs, Guid? CategoryId, BudgetOdcType? Odc, DateOnly? Period, decimal Hours, decimal Amount);

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var projectsService = Service<IProjectService>(context);
        var budgets = Service<IBudgetService>(context);
        var projects = await projectsService.ListProjectsAsync(cancellationToken);
        var categories = (await Service<IPeopleService>(context).ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var periods = await Service<IFiscalCalendarService>(context).ListPeriodsAsync(null, cancellationToken);
        var wbsByProject = new Dictionary<Guid, IReadOnlyList<WbsElementDto>>();
        var lines = new List<Line>();

        foreach (var row in rows)
        {
            var code = row.Required("WbsCode");
            var plc = row.Get("LaborCategory");
            var hours = row.Decimal("Hours") ?? 0m;
            var amount = row.Decimal("Amount") ?? 0m;
            if (code is null)
            {
                continue;
            }

            var project = context.Options.ProjectId is { } chosen
                ? projects.FirstOrDefault(p => p.Id == chosen)
                : projects.FirstOrDefault(p => string.Equals(p.Code, code.Split('.')[0], StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                row.Error("WbsCode", $"Project for {code} isn't in Workbench; import projects first (or choose the project).");
                continue;
            }

            if (!wbsByProject.TryGetValue(project.Id, out var wbs))
            {
                wbsByProject[project.Id] = wbs = await projectsService.ListWbsAsync(project.Id, cancellationToken);
            }

            var element = wbs.FirstOrDefault(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase));
            if (element is null)
            {
                row.Error("WbsCode", $"WBS {code} isn't in project {project.Code}.");
                continue;
            }

            Guid? categoryId = null;
            if (plc is not null && !categories.TryGetValue(plc, out var c))
            {
                row.Error("LaborCategory", $"PLC {plc} isn't in Workbench; import labor categories first.");
            }
            else if (plc is not null)
            {
                categoryId = categories[plc];
                if (wbs.Any(w => w.ParentId == element.Id))
                {
                    row.Error("WbsCode", $"Labor is budgeted on lowest-level WBS elements; {code} has children.");
                }
            }

            BudgetOdcType? odc = null;
            if (plc is null)
            {
                var text = row.Get("OdcType");
                odc = text is null ? BudgetOdcType.Other : Enum.TryParse<BudgetOdcType>(text.Replace(" ", ""), true, out var parsed) ? parsed : null;
                if (odc is null)
                {
                    row.Error("OdcType", $"ODC type '{text}' isn't Travel, Materials, Equipment, Subcontract, Consultant or Other.");
                }
            }

            var period = PeriodStart(row, periods);
            if (row.HasErrors)
            {
                continue;
            }

            if (plc is not null && amount != 0 && hours == 0)
            {
                row.Error("Hours", "Labor rows need hours; Workbench prices labor from hours × planning rate.");
                continue;
            }

            if (hours == 0 && amount == 0)
            {
                context.Run.Skip(row.FileName, row.Number, "Budget line", code, "No hours or amount");
                continue;
            }

            lines.Add(new Line(row, element, categoryId, odc, period, plc is null ? 0 : hours, plc is null ? amount : 0));
        }

        foreach (var group in lines.GroupBy(l => l.Wbs.ProjectId))
        {
            var project = projects.First(p => p.Id == group.Key);
            await SaveVersionAsync(context, budgets, project, group.ToList(), cancellationToken);
        }
    }

    private static DateOnly? PeriodStart(MappedRow row, IReadOnlyList<FiscalPeriodDto> periods)
    {
        var text = row.Get("Period");
        if (text is null || text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            return null;
        }

        if (text.Length == 7 && DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", out var month))
        {
            var match = periods.FirstOrDefault(p => p.Start <= month && p.End >= month);
            if (match is null)
            {
                row.Error("Period", $"No fiscal period covers {text}; set up the fiscal calendar first.");
            }

            return match?.Start;
        }

        if (!int.TryParse(text, out var number) || row.Get("FiscalYear") is not { } fyText || !int.TryParse(fyText, out var fy))
        {
            row.Error("Period", $"Period '{text}' needs a fiscal year, or use yyyy-MM.");
            return null;
        }

        var period = periods.FirstOrDefault(p => p.FiscalYear == fy && p.Number == number);
        if (period is null)
        {
            row.Error("Period", $"Fiscal year {fy} period {number} isn't in the fiscal calendar.");
        }

        return period?.Start;
    }

    private static async Task SaveVersionAsync(ImportContext context, IBudgetService budgets, ProjectDto project, List<Line> lines, CancellationToken cancellationToken)
    {
        // Costpoint exports repeat a WBS × PLC × period when it has several accounts: add them up.
        var requests = lines
            .GroupBy(l => (l.Wbs.Id, l.CategoryId, l.Odc, l.Period))
            .Select(g => new SaveBudgetLineRequest(g.Key.Id, g.Key.CategoryId is null ? BudgetCostKind.Odc : BudgetCostKind.Labor, g.Key.CategoryId,
                g.Key.CategoryId is null ? g.Key.Odc : null, g.Key.Period, g.Sum(l => l.Hours), g.Sum(l => l.Amount)))
            .ToList();
        var name = string.IsNullOrWhiteSpace(context.Options.BudgetVersionName) ? DefaultVersionName : context.Options.BudgetVersionName.Trim();
        var kind = context.Options.BudgetVersionKind;
        IReadOnlyList<BudgetVersionDto> versions = context.Run.CreatedIds.Contains(project.Id) ? [] : await budgets.ListVersionsAsync(project.Id, cancellationToken);
        var version = versions.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (kind != BudgetVersionKind.Forecast ? versions.FirstOrDefault(v => v.Kind == kind) : null);
        var first = lines[0].Row;
        var key = $"{project.Code} {version?.Name ?? name}";
        var summary = $"{requests.Count} lines · {requests.Sum(r => r.Hours):N1} h · ODC {requests.Sum(r => r.Amount):N2}";

        if (version is null)
        {
            await context.Run.CreateAsync(first.FileName, first.Number, typeof(BudgetVersion), "Budget version", key, $"{kind} · {summary}", async () =>
            {
                var created = await budgets.CreateVersionAsync(project.Id, new CreateBudgetVersionRequest(name, kind, Description: $"Imported from {SourceName(context)} (import job {context.Job.Id.ToString()[..8]})"), cancellationToken);
                var saved = await budgets.SaveLinesAsync(created.Version.Id, requests, cancellationToken);
                foreach (var line in saved.Lines)
                {
                    await context.Run.RecordCreatedAsync(typeof(BudgetLine), line.Id, $"Budget line {line.WbsCode} {line.LaborCategoryCode ?? line.OdcType?.ToString()}", cancellationToken);
                }

                return created.Version.Id;
            }, cancellationToken);
            return;
        }

        if (version.IsLocked)
        {
            foreach (var line in lines)
            {
                line.Row.Error(null, $"Budget version {version.Name} of project {project.Code} is locked.");
            }

            return;
        }

        var current = await budgets.GetVersionAsync(version.Id, cancellationToken);
        static string Signature(Guid wbs, BudgetCostKind cost, Guid? category, BudgetOdcType? odc, DateOnly? period, decimal hours, decimal amount) =>
            // Fixed two decimals: a database can hand back 8 where the file said 8.0 (decimal keeps its scale in ToString).
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{wbs}|{cost}|{category}|{odc}|{period:yyyy-MM-dd}|{decimal.Round(hours, 2):0.00}|{(cost == BudgetCostKind.Odc ? decimal.Round(amount, 2) : 0m):0.00}");
        var existing = current?.Lines.Select(l => Signature(l.WbsElementId, l.CostType, l.LaborCategoryId, l.OdcType, l.PeriodStart, l.Hours, l.Amount)).Order().ToList() ?? [];
        var incoming = requests.Select(r => Signature(r.WbsElementId, r.CostType, r.LaborCategoryId, r.OdcType, r.PeriodStart, r.Hours, r.Amount)).Order().ToList();
        if (existing.SequenceEqual(incoming))
        {
            context.Run.Skip(first.FileName, first.Number, "Budget version", key, "Unchanged");
            return;
        }

        if (!context.Run.Applying)
        {
            context.Run.PlanUpdate(first.FileName, first.Number, "Budget version", key, $"Replaces {existing.Count} lines with {summary}");
            return;
        }

        // The old lines are replaced: keep each one's last state so a rollback can put it back.
        var dbFactory = context.Services.GetRequiredService<IWorkbenchDbContextFactory>();
        var oldLines = new List<(Guid Id, string Snapshot, string Label)>();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var dbContext = EntitySnapshots.Context(db);
            foreach (var line in await db.Set<BudgetLine>().Where(l => l.BudgetVersionId == version.Id).ToListAsync(cancellationToken))
            {
                oldLines.Add((line.Id, EntitySnapshots.Serialize(dbContext, line), $"Budget line {version.Name}"));
            }
        }

        try
        {
            var saved = await context.Run.TrackAsync([(typeof(BudgetVersion), version.Id, $"Budget version {key}")],
                () => budgets.SaveLinesAsync(version.Id, requests, cancellationToken), cancellationToken);
            foreach (var (id, snapshot, label) in oldLines)
            {
                await context.Run.RecordDeletedAsync(typeof(BudgetLine), id, label, snapshot, cancellationToken);
            }

            foreach (var line in saved.Lines)
            {
                await context.Run.RecordCreatedAsync(typeof(BudgetLine), line.Id, $"Budget line {line.WbsCode} {line.LaborCategoryCode ?? line.OdcType?.ToString()}", cancellationToken);
            }

            context.Run.PlanUpdate(first.FileName, first.Number, "Budget version", key, $"Replaced {oldLines.Count} lines with {summary}");
        }
        catch (Exception ex) when (ImportRun.IsRowError(ex))
        {
            first.Error(null, ex.Message);
        }
    }
}
