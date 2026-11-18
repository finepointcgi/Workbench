using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Leave;
using Workbench.Domain.Payroll;

namespace Workbench.Application.Leave;

/// <summary>Pay groups by code and name, for a leave policy's eligibility. Reads the pay calendar records core owns; they exist only where the payroll module made some.</summary>
public sealed class PayGroupChoiceService(IWorkbenchDbContextFactory dbFactory, AccessControl access) : IPayGroupChoiceService
{
    public async Task<IReadOnlyList<PayGroupChoiceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<PayGroup>().AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Code)
            .Select(g => new PayGroupChoiceDto(g.Id, g.Code, g.Name)).ToListAsync(cancellationToken);
    }
}
