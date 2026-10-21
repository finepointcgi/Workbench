using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Scheduling;

public sealed class DependencyService(IWorkbenchDbContextFactory dbFactory) : IDependencyService
{
    private const int MaxLagDays = 3650;

    public async Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var dependencies = await LoadAsync(db, sheetId, cancellationToken);
        return dependencies.Select(ToDto).ToList();
    }

    public async Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PredecessorId == request.SuccessorId)
        {
            throw new ArgumentException("An item can't depend on itself.");
        }

        if (Math.Abs(request.LagDays) > MaxLagDays)
        {
            throw new ArgumentException($"Lag must be between -{MaxLagDays} and {MaxLagDays} working days.");
        }

        if (!Enum.IsDefined(request.Type))
        {
            throw new ArgumentException($"Unknown dependency type '{request.Type}'.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var ids = new[] { request.PredecessorId, request.SuccessorId };
        var items = await db.Items.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.SheetId })
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var item = items.FirstOrDefault(i => i.Id == id) ?? throw WorkbenchNotFoundException.For("Item", id);
            if (item.SheetId != sheetId)
            {
                throw new ArgumentException("Both items must be on the same sheet.");
            }
        }

        var existing = await LoadAsync(db, sheetId, cancellationToken);
        if (existing.Any(d => d.PredecessorId == request.PredecessorId && d.SuccessorId == request.SuccessorId))
        {
            throw new ArgumentException("These items are already linked.");
        }

        if (CreatesCycle(existing.Select(d => (d.PredecessorId, d.SuccessorId)), request.PredecessorId, request.SuccessorId))
        {
            throw new ArgumentException("That link would create a circular dependency.");
        }

        var dependency = new Dependency
        {
            SheetId = sheetId,
            PredecessorId = request.PredecessorId,
            SuccessorId = request.SuccessorId,
            Type = (DependencyType)(int)request.Type,
            LagDays = request.LagDays,
        };
        db.Set<Dependency>().Add(dependency);
        await db.SaveChangesAsync(cancellationToken);

        // The successor now starts no earlier than the link allows (and its successors after it).
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken);
        return ToDto(dependency);
    }

    public async Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dependency = await db.Set<Dependency>().FirstOrDefaultAsync(d => d.Id == dependencyId && d.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Dependency", dependencyId);
        db.Set<Dependency>().Remove(dependency);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureSheetAsync(db, sheetId, cancellationToken);
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var dependencies = await LoadAsync(db, sheetId, cancellationToken);

        // Summary rows span their children, so only dated leaves take part in the network.
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        var tasks = items
            .Where(i => !parents.Contains(i.Id) && (i.Start ?? i.Finish) is not null)
            .Select(i => new ScheduleTask(i.Id, (i.Start ?? i.Finish)!.Value, (i.Finish ?? i.Start)!.Value))
            .ToList();
        var links = dependencies
            .Select(d => new ScheduleLink(d.PredecessorId, d.SuccessorId, (DependencyKind)(int)d.Type, d.LagDays))
            .ToList();
        return CriticalPathCalculator.Analyze(tasks, links);
    }

    /// <summary>True when <paramref name="predecessorId"/> is already reachable from <paramref name="successorId"/>.</summary>
    public static bool CreatesCycle(IEnumerable<(Guid PredecessorId, Guid SuccessorId)> edges, Guid predecessorId, Guid successorId)
    {
        var next = edges.ToLookup(e => e.PredecessorId, e => e.SuccessorId);
        var seen = new HashSet<Guid> { successorId };
        var stack = new Stack<Guid>([successorId]);
        while (stack.TryPop(out var id))
        {
            if (id == predecessorId)
            {
                return true;
            }

            foreach (var successor in next[id])
            {
                if (seen.Add(successor))
                {
                    stack.Push(successor);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The sheet's links whose items still exist. FK cascades remove links with their items on real databases;
    /// the join keeps providers without cascades (EF in-memory) from returning dangling ids.
    /// </summary>
    private static Task<List<Dependency>> LoadAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        db.Set<Dependency>().AsNoTracking()
            .Where(d => d.SheetId == sheetId
                && db.Items.Any(i => i.Id == d.PredecessorId)
                && db.Items.Any(i => i.Id == d.SuccessorId))
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

    private static async Task EnsureSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }
    }

    private static DependencyDto ToDto(Dependency d) =>
        new(d.Id, d.PredecessorId, d.SuccessorId, (DependencyKind)(int)d.Type, d.LagDays);
}
