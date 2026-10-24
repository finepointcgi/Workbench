namespace Workbench.Contracts.ProjectAccounting;

// Starting a project in one step: a code and a name, optionally a contract, and where its plan comes from. The project,
// its WBS and its plan sheet are created together, so the person lands straight in the plan to add elements.

public enum ProjectStartFrom
{
    /// <summary>An empty plan: add elements in the plan grid.</summary>
    Blank,

    /// <summary>A built-in starter outline (<see cref="ProjectTemplateDto"/>).</summary>
    Template,

    /// <summary>Another project's WBS and plan: names, outline, durations and links, moved to the new start.</summary>
    CopyProject,

    /// <summary>
    /// The contract (<see cref="StartProjectRequest.ContractId"/>): its period of performance sets the dates, its CLINs become
    /// the top WBS elements, and its labor-category hour ceilings fill a Current budget where they exist.
    /// </summary>
    Contract,

    /// <summary>A plan workbook (<see cref="StartProjectRequest.Import"/>), as the plan's Excel import reads it.</summary>
    ImportExcel,
}

/// <summary>A starter outline. <paramref name="Outline"/> lists element names, indented two spaces per level.</summary>
public sealed record ProjectTemplateDto(string Key, string Name, string Description, IReadOnlyList<string> Outline);

/// <summary>
/// Starts a project; its manager is <paramref name="ProjectManagerId"/>, else its creator, and is asked to plan it when
/// that's someone else. <paramref name="Terms"/> sets its ceiling, funding, type and fee (Finance). <paramref name="Start"/> defaults to the contract's period of performance
/// (or today without one). <paramref name="TemplateKey"/> goes with <see cref="ProjectStartFrom.Template"/>,
/// <paramref name="SourceProjectId"/> with <see cref="ProjectStartFrom.CopyProject"/>, <paramref name="Import"/> with
/// <see cref="ProjectStartFrom.ImportExcel"/>.
/// </summary>
public sealed record StartProjectRequest(
    string Code,
    string Name,
    Guid? ContractId = null,
    ProjectStartFrom From = ProjectStartFrom.Blank,
    string? TemplateKey = null,
    Guid? SourceProjectId = null,
    DateOnly? Start = null,
    Scheduling.PlanImportRequest? Import = null,
    Guid? ProjectManagerId = null,
    SaveProjectTermsRequest? Terms = null);

public interface IProjectStartService
{
    /// <summary>The built-in starter outlines.</summary>
    Task<IReadOnlyList<ProjectTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks a workbook for <see cref="ProjectStartFrom.ImportExcel"/> as an import into an empty plan would. Changes nothing.</summary>
    Task<Scheduling.PlanImportPreview> CheckImportAsync(Scheduling.PlanImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>The next free numeric project code (after the highest one in use), as a suggestion for a new project.</summary>
    Task<string> SuggestCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the project (as <see cref="IProjectService.CreateProjectAsync"/>, with its permission rules), its WBS from
    /// the template or source project, and its plan sheet. Returns the project with its <c>SheetId</c>.
    /// </summary>
    Task<ProjectDto> StartAsync(StartProjectRequest request, CancellationToken cancellationToken = default);
}
