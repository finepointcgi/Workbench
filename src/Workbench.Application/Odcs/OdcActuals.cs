using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Odcs;

/// <summary>
/// The single definition of ODC actuals for budgets and financials: approved entries (vendor ODCs and expense lines)
/// that aren't flagged unallowable. Filter further by project, WBS element or <see cref="OdcEntry.IncurredOn"/>.
/// </summary>
public static class OdcActuals
{
    public static IQueryable<OdcEntry> ApprovedAllowable(IWorkbenchDbContext db) =>
        db.Set<OdcEntry>().Where(o => o.Status == OdcStatus.Approved && !o.IsUnallowable);
}
