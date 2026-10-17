using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Sheets;

public sealed class SheetService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : ISheetService
{
    public const string DefaultWorkspaceName = "General";

    public async Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Sheets
            .AsNoTracking()
            .OrderByDescending(s => s.UpdatedAtUtc)
            .Select(s => new SheetSummary(
                s.Id,
                s.Name,
                s.Description,
                s.Workspace!.Name,
                s.Items.Count,
                s.SchedulingEnabled,
                s.CostpointProjectId,
                s.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var summary = await db.Sheets
            .AsNoTracking()
            .Where(s => s.Id == sheetId)
            .Select(s => new SheetSummary(
                s.Id,
                s.Name,
                s.Description,
                s.Workspace!.Name,
                s.Items.Count,
                s.SchedulingEnabled,
                s.CostpointProjectId,
                s.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var columns = await db.Columns
            .AsNoTracking()
            .Where(c => c.SheetId == sheetId)
            .OrderBy(c => c.Order)
            .ToListAsync(cancellationToken);
        return new SheetDetail(summary, columns.Select(c => c.ToDto()).ToList());
    }

    public async Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var workspace = await db.Workspaces.OrderBy(w => w.Name).FirstOrDefaultAsync(cancellationToken)
            ?? db.Workspaces.Add(new Workspace { Name = DefaultWorkspaceName }).Entity;

        var now = clock.GetUtcNow().UtcDateTime;
        var sheet = new Sheet
        {
            WorkspaceId = workspace.Id,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            SchedulingEnabled = request.SchedulingEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Sheets.Add(sheet);
        await db.SaveChangesAsync(cancellationToken);

        return new SheetSummary(sheet.Id, sheet.Name, sheet.Description, workspace.Name, 0,
            sheet.SchedulingEnabled, sheet.CostpointProjectId, sheet.UpdatedAtUtc);
    }
}
