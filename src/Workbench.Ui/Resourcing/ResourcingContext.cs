using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Ui.Resourcing;

/// <summary>Lookups shared by the resourcing tabs: people, projects (and which ones the user may staff) and labor categories.</summary>
public sealed record ResourcingContext(
    IReadOnlyList<EmployeeDto> Employees,
    IReadOnlyList<ProjectDto> Projects,
    IReadOnlyList<ProjectDto> ManageableProjects,
    IReadOnlyList<LaborCategoryDto> LaborCategories);
