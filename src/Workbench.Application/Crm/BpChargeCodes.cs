using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Domain.Crm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Crm;

/// <summary>
/// Bid-and-proposal charge codes. B&amp;P is an indirect cost (FAR 31.205-18), so time spent pursuing an opportunity is
/// charged to its own leaf under the organization's indirect B&amp;P element, never to a direct project. The code is
/// created when the opportunity reaches Capture, closed when the pursuit ends and reopened if it's reopened.
/// </summary>
public static class BpChargeCodes
{
    public const string GeneralSuffix = "GEN";

    /// <summary>
    /// The B&amp;P parent: the configured element, else an indirect element of an active project coded <c>*.BP</c> or named
    /// "Bid and proposal". Null when there's none.
    /// </summary>
    public static async Task<WbsElement?> FindParentAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<CrmSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.BpParentWbsElementId is { } configured)
        {
            return await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == configured, ct);
        }

        var candidates = await (from w in db.Set<WbsElement>()
                                join p in db.Set<Project>() on w.ProjectId equals p.Id
                                where w.Kind == ChargeKind.Indirect && p.Status == ProjectStatus.Active
                                select w).ToListAsync(ct);
        return candidates
            .Where(w => w.Code.EndsWith(".BP", StringComparison.OrdinalIgnoreCase) || w.Code.Equals("BP", StringComparison.OrdinalIgnoreCase)
                        || w.Name.Contains("bid and proposal", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("B&P", StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Code.Length).ThenBy(w => w.Code, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Creates the opportunity's B&amp;P leaf (tracked; saved with the caller's changes) unless it has one. If the parent was
    /// itself a charge code, a "general" leaf is added beside it so general B&amp;P still has somewhere to go. Returns null and
    /// a reason when there's no B&amp;P element to put it under.
    /// </summary>
    public static async Task<(WbsElement? Element, string? Problem)> EnsureAsync(IWorkbenchDbContext db, Opportunity opportunity, AuditRecorder audit, CancellationToken ct)
    {
        if (opportunity.BpWbsElementId is { } existing)
        {
            return (await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == existing, ct), null);
        }

        var parent = await FindParentAsync(db, ct);
        if (parent is null)
        {
            return (null, "No bid-and-proposal charge code is set up, so no B&P charge code was created for this opportunity. "
                + "Finance can choose one under Business development › Settings.");
        }

        var siblings = await db.Set<WbsElement>().Where(w => w.ParentId == parent.Id).ToListAsync(ct);
        var order = siblings.Count == 0 ? 0 : siblings.Max(s => s.Order) + 1;
        if (siblings.Count == 0 && parent.IsChargeable)
        {
            var general = new WbsElement
            {
                ProjectId = parent.ProjectId, ParentId = parent.Id, Code = $"{parent.Code}.{GeneralSuffix}", Name = Trim($"{parent.Name} (general)", 200),
                Kind = ChargeKind.Indirect, IsChargeable = true, IsOpen = parent.IsOpen, RequiresAssignment = parent.RequiresAssignment, Order = order++,
            };
            db.Set<WbsElement>().Add(general);
            audit.Record(db, nameof(WbsElement), general.Id, "Created", after: Snapshot(general), reason: "General B&P, kept chargeable once opportunity B&P codes go under it");
        }

        var suffix = opportunity.Number.StartsWith("OPP-", StringComparison.OrdinalIgnoreCase) ? opportunity.Number[4..] : opportunity.Number;
        var code = $"{parent.Code}.{suffix}";
        for (var n = 2; await db.Set<WbsElement>().AnyAsync(w => w.Code == code, ct); n++)
        {
            code = $"{parent.Code}.{suffix}-{n}";
        }

        var element = new WbsElement
        {
            ProjectId = parent.ProjectId, ParentId = parent.Id, Code = Trim(code, 64), Name = Trim($"B&P {opportunity.Number} {opportunity.Name}", 200),
            Kind = ChargeKind.Indirect, IsChargeable = true, IsOpen = true, RequiresAssignment = false, Order = order,
        };
        db.Set<WbsElement>().Add(element);
        opportunity.BpWbsElementId = element.Id;
        audit.Record(db, nameof(WbsElement), element.Id, "Created", after: Snapshot(element), reason: $"B&P charge code for opportunity {opportunity.Number}");
        return (element, null);
    }

    /// <summary>Opens or closes the opportunity's B&amp;P code (closed once the pursuit ends).</summary>
    public static async Task SetOpenAsync(IWorkbenchDbContext db, Opportunity opportunity, bool open, AuditRecorder audit, CancellationToken ct)
    {
        if (opportunity.BpWbsElementId is not { } id || await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == id, ct) is not { } element || element.IsOpen == open)
        {
            return;
        }

        var before = Snapshot(element);
        element.IsOpen = open;
        audit.Record(db, nameof(WbsElement), element.Id, open ? "Reopened" : "Closed", before, Snapshot(element),
            open ? $"Opportunity {opportunity.Number} reopened" : $"Opportunity {opportunity.Number} is {opportunity.Stage}");
    }

    private static object Snapshot(WbsElement w) => new { w.Code, w.Name, w.Kind, w.IsChargeable, w.IsOpen, w.ParentId, w.ProjectId };

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
