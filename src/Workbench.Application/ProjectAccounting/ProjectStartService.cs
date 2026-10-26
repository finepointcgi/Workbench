using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Ceilings;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

/// <summary>
/// Starts a project in one step: the project, its WBS (blank, a starter outline, or a copy of another project's) and its
/// plan sheet. Every change goes through the permission-checked services (<see cref="IProjectService"/>,
/// <see cref="IPlanSyncService"/>, <see cref="IItemService"/>, <see cref="IDependencyService"/>), so a project manager
/// gets exactly what "New project" allowed before: a project they manage, a request on a contract, and only the source
/// projects they can see.
/// </summary>
public sealed class ProjectStartService(
    IWorkbenchDbContextFactory dbFactory,
    IProjectService projects,
    IPlanSyncService planSync,
    IItemService items,
    IDependencyService dependencies,
    ICurrentUserContext currentUser,
    IPlanScheduleService plans,
    IClinService clins,
    IBudgetService budgets,
    TimeProvider clock,
    IProjectPlanService? planTerms = null,
    ProjectPlanService? handOff = null,
    Contracts.Revenue.IRevenueSetupService? revenueSetup = null) : IProjectStartService
{
    private static readonly IReadOnlyList<ProjectTemplateDto> Templates =
    [
        new("phases", "Plan, do, close",
            "Three phases most projects share. Rename them and add your tasks under each.",
            ["Project management", "  Kickoff", "  Status reporting", "Execution", "Closeout", "  Final report"]),
        new("services", "Services contract",
            "Program management plus task areas, the way most services contracts are priced.",
            ["Program management", "Task area 1", "Task area 2", "Travel and other direct costs"]),
        new("product", "Build and deliver",
            "Requirements through delivery for a product or system.",
            ["Requirements", "Design", "Build", "Test", "Deliver", "  Training", "  Handover"]),
    ];

    public Task<IReadOnlyList<ProjectTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Templates);

    public async Task<string> SuggestCodeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var codes = await db.Set<Project>().AsNoTracking().Select(p => p.Code).ToListAsync(cancellationToken);
        var used = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = codes.Select(c => int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        next = Math.Max(next, 1001);
        while (used.Contains(next.ToString(CultureInfo.InvariantCulture)))
        {
            next++;
        }

        return next.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ProjectDto> StartAsync(StartProjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("A project needs a code and a name.");
        }

        var template = request.From == ProjectStartFrom.Template
            ? Templates.FirstOrDefault(t => t.Key == request.TemplateKey) ?? throw new ArgumentException("Pick a starter outline.")
            : null;
        if (request.From == ProjectStartFrom.Contract && request.ContractId is null)
        {
            throw new ArgumentException("Pick the contract to start from.");
        }

        if (request.From == ProjectStartFrom.ImportExcel && request.Import is not { Content.Length: > 0 })
        {
            throw new ArgumentException("Choose the Excel file to import.");
        }

        ProjectDto? source = null;
        if (request.From == ProjectStartFrom.CopyProject)
        {
            source = request.SourceProjectId is { } sourceId
                ? await projects.GetProjectAsync(sourceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", sourceId)
                : throw new ArgumentException("Pick the project to copy.");
        }

        var (start, finish, calendar) = await DatesAsync(request, cancellationToken);
        var code = request.Code.Trim();
        var me = (await currentUser.GetAsync(cancellationToken)).EmployeeId;
        var manager = request.ProjectManagerId ?? me; // Finance picks the PM; otherwise its creator manages it
        var project = await projects.CreateProjectAsync(
            new SaveProjectRequest(code, request.Name.Trim(), request.ContractId, manager,
                request.ContractId is null ? ProjectState.Active : ProjectState.Planning, start, finish), cancellationToken);

        // Source element id -> new element id, for copying the plan's dates and links after the sheet exists.
        var copied = new Dictionary<Guid, Guid>();
        if (template is not null)
        {
            await AddOutlineAsync(project, template.Outline, cancellationToken);
        }
        else if (source is not null)
        {
            copied = await CopyWbsAsync(source, project, cancellationToken);
        }
        else if (request.From == ProjectStartFrom.Contract)
        {
            await FromContractAsync(project, request.ContractId!.Value, cancellationToken);
        }

        project = await planSync.CreatePlanSheetAsync(project.Id, cancellationToken);
        if (project.SheetId is { } sheetId)
        {
            if (source is not null)
            {
                await CopyPlanAsync(source, project, sheetId, copied, start, calendar, cancellationToken);
            }
            else if (request.From == ProjectStartFrom.ImportExcel)
            {
                await plans.ApplyImportAsync(sheetId, request.Import!, cancellationToken);

                // Codes from another project's export don't belong here: clearing them numbers the tasks under this project.
                foreach (var item in (await items.ListAsync(sheetId, cancellationToken))
                    .Where(i => i.CostpointWbsId is { } c && !c.StartsWith(project.Code + ".", StringComparison.Ordinal)))
                {
                    await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, null, item.Values), cancellationToken);
                }
            }
            else if (template is not null || request.From == ProjectStartFrom.Contract)
            {
                // Every starter task starts with the project as a one-day task, ready for real durations.
                foreach (var item in (await items.ListAsync(sheetId, cancellationToken)).Where(i => i.Start is null))
                {
                    await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, start, start, item.CostpointWbsId, item.Values), cancellationToken);
                }
            }
        }

        // Finance's terms (ceiling, funding, type, fee), then the hand-off: the PM gets a to-do and a notification.
        if (request.Terms is { } terms && planTerms is not null)
        {
            await planTerms.SaveTermsAsync(project.Id, terms, cancellationToken);
        }

        await FinishAsync(project, request.LaborCategoryIds, manager, me, cancellationToken);
        return await projects.GetProjectAsync(project.Id, cancellationToken) ?? project;
    }

    /// <summary>
    /// A project that exists without a plan (one handed off from a won opportunity) is finished the way "Start a project" would
    /// have: the contract's CLINs as its first WBS rows, the plan sheet, revenue set up from the contract, the contract's labor
    /// categories, and the project manager asked to plan it. One step, so there is one way a project becomes ready to plan.
    /// </summary>
    public async Task<ProjectDto> FinishSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetProjectAsync(projectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", projectId);
        if (project.SheetId is not null)
        {
            throw new ArgumentException($"{project.Code} already has its plan.");
        }

        var me = (await currentUser.GetAsync(cancellationToken)).EmployeeId;
        if (project.ContractId is { } contractId && (await projects.ListWbsAsync(project.Id, cancellationToken)).Count == 0)
        {
            await FromContractAsync(project, contractId, cancellationToken);
        }

        project = await planSync.CreatePlanSheetAsync(project.Id, cancellationToken);
        if (project.SheetId is { } sheetId)
        {
            DateOnly start;
            await using (var db = await dbFactory.CreateAsync(cancellationToken))
            {
                var calendar = await PlanReflow.CalendarAsync(db, cancellationToken);
                start = calendar.OnOrAfter(project.Start ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime));
            }

            foreach (var item in (await items.ListAsync(sheetId, cancellationToken)).Where(i => i.Start is null))
            {
                await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, start, start, item.CostpointWbsId, item.Values), cancellationToken);
            }
        }

        await FinishAsync(project, null, project.ProjectManagerId, me, cancellationToken);
        return await projects.GetProjectAsync(project.Id, cancellationToken) ?? project;
    }

    /// <summary>What every new project gets once it exists: revenue set up from the contract, its labor categories, and its manager asked to plan it.</summary>
    private async Task FinishAsync(ProjectDto project, IReadOnlyList<Guid>? categoryIds, Guid? manager, Guid? me, CancellationToken cancellationToken)
    {
        await SetUpRevenueAsync(project, planTerms, cancellationToken);

        // The labor categories the plan may use: the ones given, else the ones the contract prices. Only Finance can set them,
        // so a project a PM requests waits for Finance to pick them before it can be submitted.
        if (planTerms is not null && project.ContractId is not null)
        {
            var categories = categoryIds ?? await ContractCategoriesAsync(project.ContractId.Value, cancellationToken);
            if (categories.Count > 0)
            {
                try
                {
                    await planTerms.SaveCategoriesAsync(project.Id, categories, cancellationToken);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchForbiddenException)
                {
                }
            }
        }

        if (handOff is not null && manager is not null && manager != me)
        {
            await handOff.HandOffAsync(project.Id, cancellationToken);
        }
    }

    public async Task<PlanImportPreview> CheckImportAsync(PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is not { Length: > 0 })
        {
            throw new ArgumentException("The file is empty.");
        }

        var name = request.FileName ?? "plan.xlsx";
        var xml = MsProjectXml.IsXml(name);
        var hasIdColumn = true;
        var rows = xml
            ? MsProjectXml.Read(name, request.Content).Select(r => r with { Task = r.Task with { UniqueId = null } }).ToList()
            : PlanWorkbook.Read(name, request.Content, out hasIdColumn);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var users = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive)
            .Select(u => new PlanUser(u.Email, u.DisplayName)).ToListAsync(cancellationToken);
        var empty = new PlanSnapshot([], [], await PlanReflow.CalendarAsync(db, cancellationToken), null, null,
            new Dictionary<Guid, IReadOnlyList<string>>(), users);
        return PlanImport.Build(name, rows, hasIdColumn, empty, hasConstraints: xml, unknownResourcesAreWarnings: xml).Preview;
    }

    /// <summary>
    /// The contract's top-level CLINs become the top WBS elements (mapped to their CLIN), and its labor-category hour
    /// ceilings become a Current budget on them. Steps the creator isn't allowed (mapping CLINs, budgeting) are skipped.
    /// </summary>
    private async Task FromContractAsync(ProjectDto project, Guid contractId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClinDto> lines;
        try
        {
            lines = await clins.ListClinsAsync(contractId, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchNotFoundException)
        {
            return;
        }

        var used = new List<string>();
        var elementFor = new Dictionary<Guid, Guid>();
        foreach (var clin in lines.Where(c => c.ParentNumber is null).OrderBy(c => c.Number, StringComparer.Ordinal))
        {
            var code = WbsCodes.Next(project.Code, used);
            used.Add(code);
            var element = await projects.CreateWbsElementAsync(project.Id,
                new SaveWbsElementRequest(null, code, $"CLIN {clin.Number} {clin.Description}".Trim()), cancellationToken);
            elementFor[clin.Id] = element.Id;
            try
            {
                await clins.SetWbsClinAsync(element.Id, clin.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        if (elementFor.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var hours = await db.Set<Ceiling>().AsNoTracking()
            .Where(c => c.ContractId == contractId && c.IsActive && c.LaborCategoryId != null && c.HoursCeiling > 0 && c.ProjectId == null && c.WbsElementId == null)
            .Select(c => new { c.ClinId, LaborCategoryId = c.LaborCategoryId!.Value, Hours = c.HoursCeiling!.Value })
            .ToListAsync(cancellationToken);
        if (hours.Count == 0)
        {
            return;
        }

        var first = elementFor.Values.First();
        var budgetLines = hours
            .GroupBy(h => (Element: h.ClinId is { } c && elementFor.TryGetValue(c, out var e) ? e : first, h.LaborCategoryId))
            .Select(g => new SaveBudgetLineRequest(g.Key.Element, BudgetCostKind.Labor, g.Key.LaborCategoryId, null, null, g.Sum(h => h.Hours)))
            .ToList();
        try
        {
            var version = await budgets.CreateVersionAsync(project.Id,
                new CreateBudgetVersionRequest("Current", BudgetVersionKind.Current, Description: "Started from the contract's labor-category hours."), cancellationToken);
            await budgets.SaveLinesAsync(version.Version.Id, budgetLines, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            // Budgeting needs Finance or the project's manager; the project is still created.
        }
    }

    /// <summary>
    /// How the project earns revenue follows its contract, so Finance doesn't have to set it up before the first month end: T&amp;M
    /// and labor-hour earn hours at the bill rate as they are worked, cost-plus earns cost plus fee, fixed price earns on milestones.
    /// Only Finance can set it (a project a PM requests is set up by Finance later), and other contract types keep the default.
    /// </summary>
    private async Task SetUpRevenueAsync(ProjectDto project, IProjectPlanService? terms, CancellationToken cancellationToken)
    {
        if (revenueSetup is null || project.ContractId is not { } contractId)
        {
            return;
        }

        ContractType type;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            type = await db.Set<Contract>().AsNoTracking().Where(c => c.Id == contractId).Select(c => c.Type).FirstOrDefaultAsync(cancellationToken);
        }

        var method = type switch
        {
            ContractType.TimeAndMaterials or ContractType.LaborHour => (Contracts.Revenue.RevenueMethodKind?)Contracts.Revenue.RevenueMethodKind.TimeAndMaterialsIncurred,
            ContractType.CostPlusFixedFee or ContractType.CostPlusAwardFee or ContractType.CostPlusIncentiveFee => Contracts.Revenue.RevenueMethodKind.CostPlusFee,
            ContractType.FirmFixedPrice => Contracts.Revenue.RevenueMethodKind.FixedPriceMilestones,
            _ => null,
        };
        if (method is null)
        {
            return;
        }

        try
        {
            var fee = method == Contracts.Revenue.RevenueMethodKind.CostPlusFee && terms is not null
                ? (await terms.TermsFromContractAsync(contractId, cancellationToken)).FeeRate
                : (decimal?)null;
            await revenueSetup.SaveSetupAsync(new Contracts.Revenue.SaveRevenueSetupRequest(project.Id, method.Value, FeeRate: fee > 0 ? fee : null), cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchForbiddenException or ArgumentException)
        {
            // Setting revenue up is Finance's; the project is still created.
        }
    }

    private async Task<IReadOnlyList<Guid>> ContractCategoriesAsync(Guid contractId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var priced = await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contractId).Select(r => r.LaborCategoryId).ToListAsync(cancellationToken);
        var capped = await db.Set<Ceiling>().AsNoTracking().Where(c => c.ContractId == contractId && c.LaborCategoryId != null).Select(c => c.LaborCategoryId!.Value).ToListAsync(cancellationToken);
        return priced.Concat(capped).Distinct().ToList();
    }

    private async Task<(DateOnly Start, DateOnly? Finish, WorkCalendar Calendar)> DatesAsync(StartProjectRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var calendar = await PlanReflow.CalendarAsync(db, cancellationToken);
        var contract = request.ContractId is { } id
            ? await db.Set<Contract>().AsNoTracking().Where(c => c.Id == id).Select(c => new { c.PeriodStart, c.PeriodEnd }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var start = request.Start ?? contract?.PeriodStart ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        return (calendar.OnOrAfter(start), contract?.PeriodEnd, calendar);
    }

    /// <summary>Creates a starter outline: two spaces of indent per level, numbered under the project code.</summary>
    private async Task AddOutlineAsync(ProjectDto project, IReadOnlyList<string> outline, CancellationToken cancellationToken)
    {
        var parents = new List<WbsElementDto>();
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in outline)
        {
            var level = (line.Length - line.TrimStart(' ').Length) / 2;
            while (parents.Count > level)
            {
                parents.RemoveAt(parents.Count - 1);
            }

            var parent = parents.LastOrDefault();
            var prefix = parent?.Code ?? project.Code;
            var siblings = used.TryGetValue(prefix, out var list) ? list : used[prefix] = [];
            var code = WbsCodes.Next(prefix, siblings);
            siblings.Add(code);
            var kind = parent?.Kind ?? (project.ContractId is null ? ChargeCodeKind.Indirect : ChargeCodeKind.Direct);
            var element = await projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(parent?.Id, code, line.Trim(), kind), cancellationToken);
            parents.Add(element);
        }
    }

    /// <summary>Copies the source's WBS under the new code: <c>1001.02.03</c> becomes <c>1002.02.03</c>.</summary>
    private async Task<Dictionary<Guid, Guid>> CopyWbsAsync(ProjectDto source, ProjectDto project, CancellationToken cancellationToken)
    {
        var wbs = await projects.ListWbsAsync(source.Id, cancellationToken);
        var map = new Dictionary<Guid, Guid>();
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        async Task CopyAsync(Guid? sourceParentId, Guid? parentId, string prefix)
        {
            foreach (var element in wbs.Where(w => w.ParentId == sourceParentId).OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal))
            {
                var siblings = used.TryGetValue(prefix, out var list) ? list : used[prefix] = [];
                var oldPrefix = sourceParentId is { } sp ? wbs.First(w => w.Id == sp).Code : source.Code;
                var tail = WbsCodes.Position(oldPrefix, element.Code);
                var code = tail is { } n && !siblings.Contains(WbsCodes.Code(prefix, n)) ? WbsCodes.Code(prefix, n) : WbsCodes.Next(prefix, siblings);
                siblings.Add(code);
                var created = await projects.CreateWbsElementAsync(project.Id,
                    new SaveWbsElementRequest(parentId, code, element.Name, element.Kind, element.IsChargeable, true, element.RequiresAssignment), cancellationToken);
                map[element.Id] = created.Id;
                await CopyAsync(element.Id, created.Id, code);
            }
        }

        await CopyAsync(null, null, project.Code);
        return map;
    }

    /// <summary>
    /// Copies the source plan onto the new one: dates moved to the new start (same working-day offsets and durations),
    /// milestones, links, and the source's tasks that aren't WBS elements. Skipped quietly when the source plan can't be read.
    /// </summary>
    private async Task CopyPlanAsync(
        ProjectDto source, ProjectDto project, Guid sheetId, Dictionary<Guid, Guid> copied, DateOnly start, WorkCalendar calendar, CancellationToken cancellationToken)
    {
        if (source.SheetId is not { } sourceSheetId)
        {
            return;
        }

        IReadOnlyList<ItemDto> sourceItems;
        IReadOnlyList<DependencyDto> sourceLinks;
        try
        {
            sourceItems = await items.ListAsync(sourceSheetId, cancellationToken);
            sourceLinks = await dependencies.ListAsync(sourceSheetId, cancellationToken);
        }
        catch (Exception ex) when (ex is WorkbenchNotFoundException or UnauthorizedAccessException)
        {
            return;
        }

        var sourceWbs = await projects.ListWbsAsync(source.Id, cancellationToken);
        var newWbs = (await projects.ListWbsAsync(project.Id, cancellationToken)).ToDictionary(w => w.Id);
        var itemMap = new Dictionary<Guid, Guid>();
        foreach (var element in sourceWbs)
        {
            if (element.ItemId is { } from && copied.TryGetValue(element.Id, out var newElementId) && newWbs[newElementId].ItemId is { } to)
            {
                itemMap[from] = to;
            }
        }

        var origin = sourceItems.Select(i => i.Start ?? i.Finish).OfType<DateOnly>().DefaultIfEmpty(start).Min();
        (DateOnly? Start, DateOnly? Finish) Moved(ItemDto item)
        {
            if ((item.Start ?? item.Finish) is not { } s)
            {
                return (null, null);
            }

            var f = item.Finish ?? s;
            var newStart = calendar.AddWorkingDays(calendar.OnOrAfter(start), calendar.Between(origin, s));
            return (newStart, item.IsMilestone ? newStart : calendar.Finish(newStart, calendar.Duration(s, f)));
        }

        var outline = new List<ItemDto>();
        void Visit(Guid? parentId)
        {
            foreach (var item in sourceItems.Where(i => i.ParentId == parentId).OrderBy(i => i.Order))
            {
                outline.Add(item);
                Visit(item.Id);
            }
        }

        Visit(null);
        var current = (await items.ListAsync(sheetId, cancellationToken)).ToDictionary(i => i.Id);
        foreach (var item in outline)
        {
            var (s, f) = Moved(item);
            if (itemMap.TryGetValue(item.Id, out var target) && current.TryGetValue(target, out var existing))
            {
                await items.UpdateAsync(sheetId, target, new UpdateItemRequest(existing.Title, s, f, existing.CostpointWbsId, existing.Values, item.IsMilestone), cancellationToken);
            }
            else if (!itemMap.ContainsKey(item.Id))
            {
                Guid? parent = item.ParentId is { } p && itemMap.TryGetValue(p, out var mapped) ? mapped : null;
                var created = await items.CreateAsync(sheetId, new CreateItemRequest(item.Title, parent, item.Order, s, f, null, item.IsMilestone), cancellationToken);
                itemMap[item.Id] = created.Id;
            }
        }

        foreach (var link in sourceLinks)
        {
            if (itemMap.TryGetValue(link.PredecessorId, out var predecessor) && itemMap.TryGetValue(link.SuccessorId, out var successor))
            {
                await dependencies.AddAsync(sheetId, new CreateDependencyRequest(predecessor, successor, link.Type, link.LagDays), cancellationToken);
            }
        }
    }
}
