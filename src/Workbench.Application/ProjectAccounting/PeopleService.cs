using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public sealed class PeopleService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, ICurrentUser currentUser, TimeProvider clock) : IPeopleService
{
    public async Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var query = db.Set<Employee>().AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(e => e.TerminationDate == null || e.TerminationDate >= today);
        }

        return (await query.OrderBy(e => e.DisplayName).ToListAsync(cancellationToken)).Select(e => e.ToDto()).ToList();
    }

    public async Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken))?.ToDto();
    }

    public async Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await Identity.EmployeeLinks.FindForUserAsync(db, currentUser.Email, cancellationToken))?.ToDto();
    }

    public async Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = new Employee { EmployeeNumber = "", Email = "", DisplayName = "" };
        await ApplyAsync(db, employee, request, cancellationToken);
        db.Set<Employee>().Add(employee);
        audit.Record(db, nameof(Employee), employee.Id, "Created", after: employee.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return employee.ToDto();
    }

    public async Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        var before = employee.ToDto();
        await ApplyAsync(db, employee, request, cancellationToken);
        audit.Record(db, nameof(Employee), employee.Id, "Updated", before, employee.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return employee.ToDto();
    }

    public async Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<OrgUnit>().AsNoTracking().OrderBy(o => o.Code).ToListAsync(cancellationToken)).Select(o => o.ToDto()).ToList();
    }

    public async Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var code = request.Code.Required("Code");
        if (await db.Set<OrgUnit>().AnyAsync(o => o.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Org unit code '{code}' is already used.");
        }

        var unit = new OrgUnit { Code = code, Name = request.Name.Required("Name"), ParentId = request.ParentId };
        db.Set<OrgUnit>().Add(unit);
        audit.Record(db, nameof(OrgUnit), unit.Id, "Created", after: unit.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return unit.ToDto();
    }

    public async Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<LaborCategory>().AsNoTracking().OrderBy(l => l.Code).ToListAsync(cancellationToken)).Select(l => l.ToDto()).ToList();
    }

    public async Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var category = new LaborCategory { Code = "", Name = "" };
        await ApplyAsync(db, category, request, cancellationToken);
        db.Set<LaborCategory>().Add(category);
        audit.Record(db, nameof(LaborCategory), category.Id, "Created", after: category.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return category.ToDto();
    }

    public async Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var category = await db.Set<LaborCategory>().FirstOrDefaultAsync(l => l.Id == laborCategoryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Labor category", laborCategoryId);
        var before = category.ToDto();
        await ApplyAsync(db, category, request, cancellationToken);
        audit.Record(db, nameof(LaborCategory), category.Id, "Updated", before, category.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return category.ToDto();
    }

    public async Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rates = await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == employeeId).ToListAsync(cancellationToken);
        return rates.OrderByDescending(r => r.EffectiveFrom).Select(r => r.ToDto()).ToList();
    }

    public async Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.HourlyCost <= 0)
        {
            throw new ArgumentException("Hourly cost must be greater than zero.");
        }

        var reason = request.Reason.Required("Reason");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == employeeId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Employee", employeeId);
        }

        if (await db.Set<EmployeeCostRate>().AnyAsync(r => r.EmployeeId == employeeId && r.EffectiveFrom == request.EffectiveFrom, cancellationToken))
        {
            throw new ArgumentException($"A cost rate effective {request.EffectiveFrom:yyyy-MM-dd} already exists.");
        }

        var rate = new EmployeeCostRate { EmployeeId = employeeId, EffectiveFrom = request.EffectiveFrom, HourlyCost = request.HourlyCost };
        db.Set<EmployeeCostRate>().Add(rate);
        audit.Record(db, nameof(EmployeeCostRate), rate.Id, "Created", after: rate.ToDto(), reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return rate.ToDto();
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, Employee employee, SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        var number = request.EmployeeNumber.Required("Employee number");
        var email = request.Email.Required("Email").ToLowerInvariant();
        if (await db.Set<Employee>().AnyAsync(e => e.Id != employee.Id && (e.EmployeeNumber == number || e.Email == email), cancellationToken))
        {
            throw new ArgumentException("Another employee already uses that employee number or email.");
        }

        if (request.SupervisorId == employee.Id)
        {
            throw new ArgumentException("An employee can't be their own supervisor.");
        }

        if (request.TerminationDate is { } end && end < request.HireDate)
        {
            throw new ArgumentException("Termination date can't be before hire date.");
        }

        if (request.StandardHoursPerWeek is <= 0 or > 80)
        {
            throw new ArgumentException("Standard hours per week must be between 0 and 80.");
        }

        employee.EmployeeNumber = number;
        employee.Email = email;
        employee.DisplayName = request.DisplayName.Required("Display name");
        employee.OrgUnitId = request.OrgUnitId;
        employee.SupervisorId = request.SupervisorId;
        employee.DefaultLaborCategoryId = request.DefaultLaborCategoryId;
        employee.HireDate = request.HireDate;
        employee.TerminationDate = request.TerminationDate;
        employee.StandardHoursPerWeek = request.StandardHoursPerWeek;
        employee.IsExempt = request.IsExempt;
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, LaborCategory category, SaveLaborCategoryRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code.Required("Code");
        if (await db.Set<LaborCategory>().AnyAsync(l => l.Id != category.Id && l.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Labor category code '{code}' is already used.");
        }

        category.Code = code;
        category.Name = request.Name.Required("Name");
        category.IsActive = request.IsActive;
    }
}
