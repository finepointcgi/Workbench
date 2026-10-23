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

    public async Task<BaselineComparison> CompareAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        var detail = await GetAsync(sheetId, baselineId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var calendar = await PlanReflow.CalendarAsync(db, sheetId, cancellationToken);
        var outline = new PlanOutline(items);
        var entries = detail.Entries.ToDictionary(e => e.ItemId);
        var spans = Span(outline, id => entries.TryGetValue(id, out var e) ? (e.Start, e.Finish) : (null, null));

        var rows = outline.Ordered.Select(item =>
        {
            var (bs, bf) = spans[item.Id];
            DateOnly? start = item.Start ?? item.Finish, finish = item.Finish ?? item.Start;
            var milestone = item.IsMilestone && !outline.IsSummary(item.Id);
            int? duration = start is { } s && finish is { } f ? calendar.Duration(s, f, milestone) : null;
            int? baselineDuration = bs is { } s0 && bf is { } f0 ? calendar.Duration(s0, f0, milestone && s0 == f0) : null;
            return new BaselineVarianceDto(item.Id, start, finish, bs, bf,
                start is { } a && bs is { } b ? calendar.Between(b, a) : null,
                finish is { } c && bf is { } d ? calendar.Between(d, c) : null,
                duration - baselineDuration);
        }).ToList();

        var planFinish = rows.Select(r => r.Finish).OfType<DateOnly>().DefaultIfEmpty().Max();
        var baselineFinish = rows.Select(r => r.BaselineFinish).OfType<DateOnly>().DefaultIfEmpty().Max();
        int? finishVariance = planFinish == default || baselineFinish == default ? null : calendar.Between(baselineFinish, planFinish);
        return new BaselineComparison(detail.Summary, rows, finishVariance);
    }

    public async Task DeleteAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var baseline = await db.Set<Baseline>().Include(b => b.Entries)
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Baseline", baselineId);
        db.Set<BaselineEntry>().RemoveRange(baseline.Entries);
        db.Set<Baseline>().Remove(baseline);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Baseline dates per item in the outline: a task's own, or for a summary the span of its subtasks' (as the Gantt draws
    /// them), falling back to its own when no subtask was in the baseline.
    /// </summary>
    public static Dictionary<Guid, (DateOnly? Start, DateOnly? Finish)> Span(PlanOutline outline, Func<Guid, (DateOnly? Start, DateOnly? Finish)> dates)
    {
        var result = new Dictionary<Guid, (DateOnly? Start, DateOnly? Finish)>();
        foreach (var item in outline.Ordered.Reverse())
        {
            var spans = outline.ChildrenOf(item.Id).Select(k => result[k.Id]).Where(s => s.Start is not null).ToList();
            if (spans.Count > 0)
            {
                result[item.Id] = (spans.Min(s => s.Start), spans.Max(s => s.Finish));
                continue;
            }

            var (start, finish) = dates(item.Id);
            result[item.Id] = (start ?? finish, finish ?? start);
        }

        return result;
    }

    private static async Task EnsureSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }
    }
}
