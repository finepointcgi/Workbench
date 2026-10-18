using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// Which employee record a sign-in belongs to: an administrator's explicit link, none when they unlinked it, and
/// otherwise the employee with the same email (the default).
/// </summary>
public static class EmployeeLinks
{
    public static async Task<Employee?> FindForUserAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken)
    {
        email = AccessControl.Normalize(email);
        if (email.Length == 0)
        {
            return null;
        }

        var link = await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.EmployeeId, u.EmployeeUnlinked })
            .FirstOrDefaultAsync(cancellationToken);
        if (link?.EmployeeUnlinked == true)
        {
            return null;
        }

        if (link?.EmployeeId is { } employeeId)
        {
            return await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        }

        return await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == email, cancellationToken);
    }
}
