using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Domain.Compliance;

namespace Workbench.Application.Compliance;

public sealed class CmmcService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ICmmcService
{
    public async Task<CmmcAssessmentDto> GetAssessmentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = (await db.Set<CmmcPractice>().AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(r => r.PracticeId, StringComparer.Ordinal);
        var poam = await db.Set<CmmcPoamItem>().AsNoTracking().ToListAsync(cancellationToken);
        var today = Today();

        var practices = Nist171.Requirements.Select(r => ToDto(r, rows.GetValueOrDefault(r.Id), poam.Count(p => p.PracticeId == r.Id && p.Status == CmmcPoamStatus.Open))).ToList();
        var deductions = practices.Sum(p => p.Deduction);
        var unassessedWeight = practices.Where(p => p.Status == CmmcStatusDto.NotAssessed).Sum(p => p.Weight);
        var notImplemented = practices.Where(p => p.Status == CmmcStatusDto.NotImplemented).ToList();

        var families = practices.GroupBy(p => p.Family).Select(g => new CmmcFamilySummaryDto(
            g.Key, g.Count(), g.Count(p => p.Status == CmmcStatusDto.Implemented), g.Count(p => p.Status == CmmcStatusDto.NotImplemented),
            g.Count(p => p.Status == CmmcStatusDto.NotApplicable), g.Count(p => p.Status == CmmcStatusDto.NotAssessed))).ToList();

        var items = poam.OrderBy(p => p.Status).ThenBy(p => p.DueOn ?? DateOnly.MaxValue).ThenBy(p => p.PracticeId, StringComparer.Ordinal)
            .Select(p => ToDto(p, today)).ToList();

        return new CmmcAssessmentDto(
            Nist171.PerfectScore - deductions,
            Nist171.PerfectScore - deductions - unassessedWeight,
            practices.Count(p => p.Status == CmmcStatusDto.Implemented),
            notImplemented.Count,
            practices.Count(p => p.Status == CmmcStatusDto.NotApplicable),
            practices.Count(p => p.Status == CmmcStatusDto.NotAssessed),
            notImplemented.Count(p => p.OpenPoamItems == 0),
            items.Count(i => i.Status == CmmcPoamStatusDto.Open),
            items.Count(i => i.Overdue),
            families, practices, items);
    }

    public async Task<CmmcPracticeDto> SavePracticeAsync(string practiceId, SaveCmmcPracticeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requirement = Nist171.Find(practiceId ?? "") ?? throw new WorkbenchNotFoundException($"{practiceId} isn't a NIST SP 800-171 requirement.");
        if (request.PartialCredit && (!requirement.AllowsPartialCredit || request.Status != CmmcStatusDto.NotImplemented))
        {
            throw new ArgumentException("Only 3.5.3 and 3.13.11 can be partly met, and only while they are marked Not implemented.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CmmcPractice>().FirstOrDefaultAsync(p => p.PracticeId == requirement.Id, cancellationToken);
        var before = row is null ? null : new { row.Status, row.PartialCredit };
        if (row is null)
        {
            row = new CmmcPractice { PracticeId = requirement.Id };
            db.Set<CmmcPractice>().Add(row);
        }

        row.Status = (CmmcStatus)(int)request.Status;
        row.PartialCredit = request.PartialCredit;
        row.Implementation = Clean(request.Implementation, 2000);
        row.Evidence = Clean(request.Evidence, 2000);
        row.OwnerEmail = Clean(request.OwnerEmail, 320)?.ToLowerInvariant();
        row.AssessedOn = request.AssessedOn;
        row.UpdatedBy = audit.Actor;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(CmmcPractice), row.Id, "Assessed", before, new { row.PracticeId, row.Status, row.PartialCredit });
        await db.SaveChangesAsync(cancellationToken);
        var open = await db.Set<CmmcPoamItem>().CountAsync(p => p.PracticeId == requirement.Id && p.Status == CmmcPoamStatus.Open, cancellationToken);
        return ToDto(requirement, row, open);
    }

    public async Task<CmmcPoamItemDto> AddPoamItemAsync(SaveCmmcPoamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = new CmmcPoamItem { CreatedBy = audit.Actor, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        Apply(item, request);
        db.Set<CmmcPoamItem>().Add(item);
        audit.Record(db, nameof(CmmcPoamItem), item.Id, "Created", null, new { item.PracticeId, item.Milestone, item.DueOn });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item, Today());
    }

    public async Task<CmmcPoamItemDto> UpdatePoamItemAsync(Guid itemId, SaveCmmcPoamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await LoadAsync(db, itemId, cancellationToken);
        var before = new { item.Milestone, item.Status, item.DueOn };
        Apply(item, request);
        item.UpdatedBy = audit.Actor;
        item.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(CmmcPoamItem), item.Id, "Updated", before, new { item.Milestone, item.Status, item.DueOn });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item, Today());
    }

    public async Task DeletePoamItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await LoadAsync(db, itemId, cancellationToken);
        audit.Record(db, nameof(CmmcPoamItem), item.Id, "Deleted", new { item.PracticeId, item.Milestone }, null);
        db.Set<CmmcPoamItem>().Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<CmmcPoamItem> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<CmmcPoamItem>().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new WorkbenchNotFoundException("POA&M item not found.");

    private void Apply(CmmcPoamItem item, SaveCmmcPoamRequest r)
    {
        if (Nist171.Find(r.PracticeId ?? "") is not { } requirement)
        {
            throw new ArgumentException($"{r.PracticeId} isn't a NIST SP 800-171 requirement.");
        }

        var milestone = (r.Milestone ?? "").Trim();
        if (milestone.Length == 0)
        {
            throw new ArgumentException("Say what will be done.");
        }

        var done = r.Status == CmmcPoamStatusDto.Done;
        item.PracticeId = requirement.Id;
        item.Milestone = milestone.Length <= 500 ? milestone : milestone[..500];
        item.OwnerEmail = Clean(r.OwnerEmail, 320)?.ToLowerInvariant();
        item.DueOn = r.DueOn;
        item.Notes = Clean(r.Notes, 2000);
        item.Status = (CmmcPoamStatus)(int)r.Status;
        item.CompletedOn = done ? item.CompletedOn ?? Today() : null;
    }

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }

    private static CmmcPracticeDto ToDto(Nist171Requirement r, CmmcPractice? row, int openPoam)
    {
        var status = row is null ? CmmcStatusDto.NotAssessed : (CmmcStatusDto)(int)row.Status;
        var partial = row?.PartialCredit == true && r.AllowsPartialCredit;
        var deduction = status == CmmcStatusDto.NotImplemented ? (partial ? Nist171.PartialDeduction : r.Weight) : 0;
        return new CmmcPracticeDto(r.Id, r.Family, r.Statement, r.Weight, r.IsBasic, r.AllowsPartialCredit, status, partial, deduction,
            row?.Implementation, row?.Evidence, row?.OwnerEmail, row?.AssessedOn, openPoam, row?.UpdatedBy, row?.UpdatedAtUtc);
    }

    private static CmmcPoamItemDto ToDto(CmmcPoamItem p, DateOnly today) => new(
        p.Id, p.PracticeId, Nist171.Find(p.PracticeId)?.Statement ?? "", p.Milestone, p.OwnerEmail, p.DueOn,
        p.Status == CmmcPoamStatus.Open && p.DueOn is { } due && due < today, (CmmcPoamStatusDto)(int)p.Status, p.CompletedOn, p.Notes);
}
