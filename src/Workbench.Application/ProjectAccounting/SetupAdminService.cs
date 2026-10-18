using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public sealed class SetupAdminService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : ISetupAdminService
{
    public async Task<OrgUnitDto> UpdateOrgUnitAsync(Guid orgUnitId, CreateOrgUnitRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var unit = await db.Set<OrgUnit>().FirstOrDefaultAsync(o => o.Id == orgUnitId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Org unit", orgUnitId);
        var code = request.Code.Required("Code");
        if (await db.Set<OrgUnit>().AnyAsync(o => o.Id != orgUnitId && o.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Org unit code '{code}' is already used.");
        }

        if (request.ParentId is { } parentId)
        {
            var parents = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.ParentId, cancellationToken);
            if (!parents.ContainsKey(parentId))
            {
                throw WorkbenchNotFoundException.For("Org unit", parentId);
            }

            for (Guid? cursor = parentId; cursor is { } id; cursor = parents.GetValueOrDefault(id))
            {
                if (id == orgUnitId)
                {
                    throw new ArgumentException("An org unit can't be placed under itself or one of its descendants.");
                }
            }
        }

        var before = unit.ToDto();
        unit.Code = code;
        unit.Name = request.Name.Required("Name");
        unit.ParentId = request.ParentId;
        audit.Record(db, nameof(OrgUnit), unit.Id, "Updated", before, unit.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return unit.ToDto();
    }

    public async Task<CustomerDto> UpdateCustomerAsync(Guid customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer", customerId);
        var code = request.Code.Required("Code");
        if (await db.Set<Customer>().AnyAsync(c => c.Id != customerId && c.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Customer code '{code}' is already used.");
        }

        var before = customer.ToDto();
        customer.Code = code;
        customer.Name = request.Name.Required("Name");
        customer.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        audit.Record(db, nameof(Customer), customer.Id, "Updated", before, customer.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return customer.ToDto();
    }

    public async Task<IReadOnlyList<AuditEventDto>> SearchAuditAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        if (query.From is { } f && query.To is { } t && t < f)
        {
            throw new ArgumentException("The end date can't be before the start date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = db.Set<AuditEvent>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            rows = rows.Where(a => a.EntityType == query.EntityType);
        }

        if (query.EntityId is { } id)
        {
            rows = rows.Where(a => a.EntityId == id);
        }

        if (query.From is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(a => a.OccurredAtUtc >= start);
        }

        if (query.To is { } to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(a => a.OccurredAtUtc < end);
        }

        var list = await rows.OrderByDescending(a => a.OccurredAtUtc).Take(Math.Clamp(query.Limit, 1, 2000)).ToListAsync(cancellationToken);
        return list.Select(a => new AuditEventDto(a.Id, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson, a.Actor, a.OccurredAtUtc, a.Reason)).ToList();
    }

    public async Task<IReadOnlyList<string>> ListAuditEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<AuditEvent>().AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
    }

    public async Task<SetupChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return new SetupChecklistDto(
            await db.Set<FiscalPeriod>().AnyAsync(p => p.Status == PeriodStatus.Open, cancellationToken),
            await db.Set<LaborCategory>().AnyAsync(cancellationToken),
            await db.Set<Employee>().AnyAsync(cancellationToken),
            await db.Set<EmployeeCostRate>().AnyAsync(cancellationToken),
            await db.Set<Customer>().AnyAsync(cancellationToken) && await db.Set<Contract>().AnyAsync(cancellationToken),
            await db.Set<WbsElement>().AnyAsync(cancellationToken),
            await db.Set<ChargeAssignment>().AnyAsync(cancellationToken),
            // Someone besides the first administrator can sign in (or has been invited).
            await db.Set<Domain.Identity.AppUser>().CountAsync(u => u.IsActive, cancellationToken) > 1
                || await db.Set<Domain.Security.UserInvite>().AnyAsync(cancellationToken),
            await db.Set<Domain.Accounting.Account>().AnyAsync(a => a.IsActive, cancellationToken),
            await db.Set<Domain.Cash.BankAccount>().AnyAsync(cancellationToken),
            await db.Set<Domain.Payroll.PayrollProviderSetting>().AnyAsync(cancellationToken),
            await db.Set<Domain.Approvals.ApprovalWorkflow>().AnyAsync(w => w.CurrentVersionId != null, cancellationToken),
            await db.Set<Domain.Accounting.IndirectRate>().AnyAsync(cancellationToken),
            await db.Set<Domain.Billing.ContractBillingSettings>().AnyAsync(cancellationToken)
                || await db.Set<Domain.Billing.CustomerBillingProfile>().AnyAsync(cancellationToken));
    }
}
