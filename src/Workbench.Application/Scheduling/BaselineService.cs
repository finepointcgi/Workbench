using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Scheduling;

public sealed class BaselineService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IBaselineService
{
    public async Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"Baseline {now:yyyy-MM-dd}" : request.Name.Trim();
        if (name.Length > 200)
        {
            throw new ArgumentException("Baseline names are limited to 200 characters.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var items = await db.Items.AsNoTracking()
            .Where(i => i.SheetId == sheetId)
            .Select(i => new { i.Id, i.Start, i.Finish })
            .ToListAsync(cancellationToken);

        var baseline = new Baseline { SheetId = sheetId, Name = name, CapturedAtUtc = now };
        baseline.Entries.AddRange(items.Select(i => new BaselineEntry { BaselineId = baseline.Id, ItemId = i.Id, Start = i.Start, Finish = i.Finish }));
        db.Set<Baseline>().Add(baseline);
        await db.SaveChangesAsync(cancellationToken);
        return new BaselineSummary(baseline.Id, baseline.Name, baseline.CapturedAtUtc, baseline.Entries.Count);
    }

    public async Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        return await db.Set<Baseline>().AsNoTracking()
            .Where(b => b.SheetId == sheetId)
            .OrderByDescending(b => b.CapturedAtUtc)
            .Select(b => new BaselineSummary(b.Id, b.Name, b.CapturedAtUtc, b.Entries.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var baseline = await db.Set<Baseline>().AsNoTracking()
            .Include(b => b.Entries)
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Baseline", baselineId);

        // Entries go with their items through an FK cascade; the filter covers providers without one.
        var itemIds = (await db.Items.Where(i => i.SheetId == sheetId).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
        var entries = baseline.Entries
            .Where(e => itemIds.Contains(e.ItemId))
            .Select(e => new BaselineEntryDto(e.ItemId, e.Start, e.Finish))
            .ToList();
        return new BaselineDetail(new BaselineSummary(baseline.Id, baseline.Name, baseline.CapturedAtUtc, entries.Count), entries);
    }

    private static async Task EnsureSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }
    }
}
