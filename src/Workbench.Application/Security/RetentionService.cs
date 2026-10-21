using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

/// <summary>Default retention periods (config section <c>Retention</c>). Admins can override them in the UI.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Security events: purged after this many days (default 1 year).</summary>
    public int SecurityEventDays { get; set; } = 365;

    /// <summary>In-app notifications: purged after this many days.</summary>
    public int NotificationDays { get; set; } = 180;

    /// <summary>Expired or revoked invites that were never accepted: purged this many days after they lapsed.</summary>
    public int ExpiredInviteDays { get; set; } = 90;

    /// <summary>Time, ODC, journal and audit records: kept at least this long after contract close (default 3 years). Never auto-deleted.</summary>
    public int ProtectedRecordDays { get; set; } = 1095;

    /// <summary>The nightly job's hour (UTC).</summary>
    public int RunHourUtc { get; set; } = 3;

    /// <summary>Set false to stop the nightly job (the report and Run now still work).</summary>
    public bool NightlyJobEnabled { get; set; } = true;
}

public static class RetentionRecordTypes
{
    public const string SecurityEvents = "SecurityEvents";
    public const string Notifications = "Notifications";
    public const string ExpiredInvites = "ExpiredInvites";
    public const string TimeRecords = "TimeRecords";
    public const string Odcs = "OdcsAndExpenses";
    public const string Journal = "JournalEntries";
    public const string Audit = "AuditRecords";
    public const string AccessReviews = "AccessReviews";
}

/// <summary>
/// Records retention (A.5.33, A.8.10). Purges only expired, purgeable records not covered by a legal hold
/// (security events, notifications, lapsed invites). Protected records (time, ODCs and expenses, journal, audit,
/// access reviews) are never deleted here: the job reports how many are past their retention period so a records
/// owner can decide.
/// </summary>
public sealed class RetentionService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    SecurityEventLog securityLog,
    RetentionOptions options,
    TimeProvider clock,
    ILogger<RetentionService> logger) : IRetentionService
{
    private sealed record Definition(string RecordType, string Description, bool Protected, bool AfterContractClose, int MinimumDays, Func<RetentionOptions, int> Default);

    private static readonly Definition[] Definitions =
    [
        new(RetentionRecordTypes.SecurityEvents, "Security event log", false, false, 90, o => o.SecurityEventDays),
        new(RetentionRecordTypes.Notifications, "In-app notifications", false, false, 7, o => o.NotificationDays),
        new(RetentionRecordTypes.ExpiredInvites, "Expired or revoked invites (never accepted)", false, false, 0, o => o.ExpiredInviteDays),
        new(RetentionRecordTypes.TimeRecords, "Timesheets, time entries and revisions", true, true, 1095, o => o.ProtectedRecordDays),
        new(RetentionRecordTypes.Odcs, "ODCs and expense reports", true, true, 1095, o => o.ProtectedRecordDays),
        new(RetentionRecordTypes.Journal, "General ledger journal entries", true, true, 1095, o => o.ProtectedRecordDays),
        new(RetentionRecordTypes.Audit, "Project accounting audit records", true, false, 1095, o => o.ProtectedRecordDays),
        new(RetentionRecordTypes.AccessReviews, "Access reviews and decisions", true, false, 1095, o => o.ProtectedRecordDays),
    ];

    public async Task<IReadOnlyList<RetentionPolicyDto>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var overrides = await LoadOverridesAsync(db, cancellationToken);
        return Definitions.Select(d => ToDto(d, overrides)).ToList();
    }

    public async Task<RetentionPolicyDto> SetPolicyAsync(string recordType, int retentionDays, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        var definition = Definitions.FirstOrDefault(d => d.RecordType == recordType)
            ?? throw new ArgumentException($"Unknown record type {recordType}.", nameof(recordType));
        var minimum = Math.Max(definition.MinimumDays, definition.Protected ? definition.Default(options) : 0);
        if (retentionDays < minimum || retentionDays > 36500)
        {
            throw new ArgumentException($"{definition.Description} must be kept at least {minimum} days.", nameof(retentionDays));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<RetentionPolicy>().FirstOrDefaultAsync(p => p.RecordType == recordType, cancellationToken);
        var before = row?.RetentionDays ?? definition.Default(options);
        if (row is null)
        {
            row = new RetentionPolicy { RecordType = recordType, UpdatedBy = access.Email };
            db.Set<RetentionPolicy>().Add(row);
        }

        row.RetentionDays = retentionDays;
        row.UpdatedBy = access.Email;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.SettingChanged, $"Retention:{recordType}", detail: $"{before} → {retentionDays} days", cancellationToken: cancellationToken);
        return ToDto(definition, await LoadOverridesAsync(db, cancellationToken));
    }

    public async Task<RetentionReportDto> PreviewAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        return await RunAsync(dryRun: true, access.Email, cancellationToken);
    }

    public async Task<RetentionReportDto> RunNowAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        return await RunAsync(dryRun: false, access.Email, cancellationToken);
    }

    /// <summary>The retention run without a permission check, for the nightly job.</summary>
    public async Task<RetentionReportDto> RunAsync(bool dryRun, string actor, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var overrides = await LoadOverridesAsync(db, cancellationToken);
        int Days(string type) => overrides.TryGetValue(type, out var d) ? d : Definitions.First(x => x.RecordType == type).Default(options);

        var holds = await db.Set<LegalHold>().AsNoTracking().Where(h => h.ReleasedAtUtc == null).ToListAsync(cancellationToken);
        var heldContracts = holds.Where(h => h.EntityType == HoldTypes.Contract).Select(h => h.EntityId).ToHashSet();
        var heldProjects = holds.Where(h => h.EntityType == HoldTypes.Project).Select(h => h.EntityId).ToHashSet();
        var projects = await db.Set<Project>().AsNoTracking().Select(p => new { p.Id, p.ContractId, p.SheetId }).ToListAsync(cancellationToken);
        heldProjects.UnionWith(projects.Where(p => p.ContractId is { } c && heldContracts.Contains(c)).Select(p => p.Id));
        var heldSheets = projects.Where(p => heldProjects.Contains(p.Id) && p.SheetId is not null).Select(p => p.SheetId!.Value).ToHashSet();

        var lines = new List<RetentionReportLine>();

        // Purgeable: security events.
        var eventCutoff = now.AddDays(-Days(RetentionRecordTypes.SecurityEvents));
        var oldEvents = db.Set<SecurityEvent>().Where(e => e.OccurredAtUtc < eventCutoff);
        lines.Add(await PurgeAsync(db, RetentionRecordTypes.SecurityEvents, oldEvents, 0, dryRun, $"Older than {eventCutoff:yyyy-MM-dd}", cancellationToken));

        // Purgeable: notifications, except those about a held project's plan sheet.
        var noteCutoff = now.AddDays(-Days(RetentionRecordTypes.Notifications));
        var heldSheetList = heldSheets.ToList();
        var oldNotes = db.Set<Notification>().Where(n => n.CreatedAtUtc < noteCutoff);
        var heldNotes = heldSheetList.Count == 0 ? 0 : await oldNotes.CountAsync(n => n.SheetId != null && heldSheetList.Contains(n.SheetId.Value), cancellationToken);
        lines.Add(await PurgeAsync(db, RetentionRecordTypes.Notifications,
            oldNotes.Where(n => n.SheetId == null || !heldSheetList.Contains(n.SheetId.Value)), heldNotes, dryRun, $"Older than {noteCutoff:yyyy-MM-dd}", cancellationToken));

        // Purgeable: invites that lapsed without being accepted.
        var inviteCutoff = now.AddDays(-Days(RetentionRecordTypes.ExpiredInvites));
        var lapsed = db.Set<UserInvite>().Where(i => i.AcceptedAtUtc == null
            && ((i.RevokedAtUtc != null && i.RevokedAtUtc < inviteCutoff) || (i.RevokedAtUtc == null && i.ExpiresAtUtc < inviteCutoff)));
        lines.Add(await PurgeAsync(db, RetentionRecordTypes.ExpiredInvites, lapsed, 0, dryRun, $"Lapsed before {inviteCutoff:yyyy-MM-dd}", cancellationToken));

        // Protected: report only. Records of a contract closed more than the retention period ago are "eligible".
        var closeCutoff = today.AddDays(-Days(RetentionRecordTypes.TimeRecords));
        var closedContracts = await db.Set<Contract>().AsNoTracking()
            .Where(c => c.Status == ContractStatus.Closed && c.PeriodEnd < closeCutoff)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var eligibleProjects = projects.Where(p => p.ContractId is { } c && closedContracts.Contains(c)).Select(p => p.Id).ToList();
        var eligibleHeld = eligibleProjects.Where(heldProjects.Contains).ToList();
        var eligibleFree = eligibleProjects.Except(eligibleHeld).ToList();

        int timeEligible = await CountTimeAsync(db, eligibleFree, cancellationToken), timeHeld = await CountTimeAsync(db, eligibleHeld, cancellationToken);
        lines.Add(new(RetentionRecordTypes.TimeRecords, true, timeEligible, 0, timeHeld, $"Contracts closed before {closeCutoff:yyyy-MM-dd}; never deleted automatically"));

        var odcCutoffContracts = today.AddDays(-Days(RetentionRecordTypes.Odcs));
        int odcEligible = await db.Set<OdcEntry>().CountAsync(o => eligibleFree.Contains(o.ProjectId), cancellationToken);
        int odcHeld = await db.Set<OdcEntry>().CountAsync(o => eligibleHeld.Contains(o.ProjectId), cancellationToken);
        lines.Add(new(RetentionRecordTypes.Odcs, true, odcEligible, 0, odcHeld, $"Contracts closed before {odcCutoffContracts:yyyy-MM-dd}; never deleted automatically"));

        lines.Add(new(RetentionRecordTypes.Journal, true, 0, 0, 0, "Kept at least 3 years after contract close; never deleted automatically"));

        var auditCutoff = now.AddDays(-Days(RetentionRecordTypes.Audit));
        lines.Add(new(RetentionRecordTypes.Audit, true, await db.Set<AuditEvent>().CountAsync(a => a.OccurredAtUtc < auditCutoff, cancellationToken), 0, 0,
            $"Older than {auditCutoff:yyyy-MM-dd}; never deleted automatically"));

        var reviewCutoff = now.AddDays(-Days(RetentionRecordTypes.AccessReviews));
        lines.Add(new(RetentionRecordTypes.AccessReviews, true, await db.Set<AccessReview>().CountAsync(r => r.StartedAtUtc < reviewCutoff, cancellationToken), 0, 0,
            "Evidence for A.5.18; never deleted automatically"));

        var report = new RetentionReportDto(now, dryRun, lines);
        if (!dryRun)
        {
            var summary = string.Join("; ", lines.Select(l => l.Protected ? $"{l.RecordType}: {l.Eligible} eligible (kept)" : $"{l.RecordType}: {l.Purged} purged, {l.Held} held"));
            logger.LogInformation("Retention run by {Actor}: {Summary}", actor, summary);
            await securityLog.RecordAsync(SecurityEventTypes.RetentionRun, detail: summary, actor: actor, cancellationToken: cancellationToken);
        }

        return report;
    }

    // ---- Legal holds ----

    public async Task<IReadOnlyList<LegalHoldDto>> ListHoldsAsync(bool includeReleased = false, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var holds = db.Set<LegalHold>().AsNoTracking();
        if (!includeReleased)
        {
            holds = holds.Where(h => h.ReleasedAtUtc == null);
        }

        var rows = await holds.ToListAsync(cancellationToken);
        return rows.OrderByDescending(h => h.PlacedAtUtc).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<HoldableRecordDto>> ListHoldableAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().Select(c => new { c.Id, c.ContractNumber, c.Name }).ToListAsync(cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().Select(p => new { p.Id, p.Code, p.Name }).ToListAsync(cancellationToken);
        return contracts.OrderBy(c => c.ContractNumber).Select(c => new HoldableRecordDto(HoldTypes.Contract, c.Id, $"{c.ContractNumber} {c.Name}"))
            .Concat(projects.OrderBy(p => p.Code).Select(p => new HoldableRecordDto(HoldTypes.Project, p.Id, $"{p.Code} {p.Name}")))
            .ToList();
    }

    public async Task<LegalHoldDto> PlaceHoldAsync(PlaceLegalHoldRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw new ArgumentException("Say why the hold is placed (e.g. the matter or request).", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var label = request.EntityType switch
        {
            HoldTypes.Contract => await db.Set<Contract>().AsNoTracking().Where(c => c.Id == request.EntityId).Select(c => c.ContractNumber + " " + c.Name).FirstOrDefaultAsync(cancellationToken),
            HoldTypes.Project => await db.Set<Project>().AsNoTracking().Where(p => p.Id == request.EntityId).Select(p => p.Code + " " + p.Name).FirstOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentException("Holds apply to a Contract or a Project.", nameof(request)),
        } ?? throw WorkbenchNotFoundException.For(request.EntityType, request.EntityId);

        var hold = new LegalHold
        {
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            Label = label,
            Reason = reason.Length > 2000 ? reason[..2000] : reason,
            PlacedBy = access.Email,
            PlacedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<LegalHold>().Add(hold);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.LegalHoldPlaced, $"{hold.EntityType} {label}", detail: hold.Reason, cancellationToken: cancellationToken);
        return ToDto(hold);
    }

    public async Task<LegalHoldDto> ReleaseHoldAsync(Guid holdId, string reason, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminRetention, "manage retention and legal holds", cancellationToken);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Say why the hold is released.", nameof(reason));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var hold = await db.Set<LegalHold>().FirstOrDefaultAsync(h => h.Id == holdId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Legal hold", holdId);
        if (hold.ReleasedAtUtc is not null)
        {
            throw new ArgumentException("This hold was already released.", nameof(holdId));
        }

        hold.ReleasedAtUtc = clock.GetUtcNow().UtcDateTime;
        hold.ReleasedBy = access.Email;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.LegalHoldReleased, $"{hold.EntityType} {hold.Label}", detail: reason.Trim(), cancellationToken: cancellationToken);
        return ToDto(hold);
    }

    public async Task<bool> IsOnHoldAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await IsOnHoldAsync(db, entityType, entityId, cancellationToken);
    }

    /// <summary>
    /// For deletion paths: throws <see cref="ArgumentException"/> when the contract or project is on legal hold.
    /// </summary>
    public static async Task EnsureNotHeldAsync(IWorkbenchDbContext db, string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        if (await IsOnHoldAsync(db, entityType, entityId, cancellationToken))
        {
            throw new ArgumentException($"This {entityType.ToLowerInvariant()} is on legal hold and can't be deleted.", nameof(entityId));
        }
    }

    internal static async Task<bool> IsOnHoldAsync(IWorkbenchDbContext db, string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var active = db.Set<LegalHold>().AsNoTracking().Where(h => h.ReleasedAtUtc == null);
        if (await active.AnyAsync(h => h.EntityType == entityType && h.EntityId == entityId, cancellationToken))
        {
            return true;
        }

        if (entityType == HoldTypes.Project)
        {
            var contractId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == entityId).Select(p => p.ContractId).FirstOrDefaultAsync(cancellationToken);
            return contractId is { } c && await active.AnyAsync(h => h.EntityType == HoldTypes.Contract && h.EntityId == c, cancellationToken);
        }

        return false;
    }

    private static async Task<RetentionReportLine> PurgeAsync<T>(IWorkbenchDbContext db, string recordType, IQueryable<T> expired, int held, bool dryRun, string note, CancellationToken cancellationToken)
        where T : class
    {
        var eligible = await expired.CountAsync(cancellationToken);
        var purged = 0;
        if (!dryRun && eligible > 0)
        {
            try
            {
                purged = await expired.ExecuteDeleteAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Providers without set-based deletes (the in-memory test provider): delete tracked rows.
                var rows = await expired.ToListAsync(cancellationToken);
                db.Set<T>().RemoveRange(rows);
                await db.SaveChangesAsync(cancellationToken);
                purged = rows.Count;
            }
        }

        return new RetentionReportLine(recordType, false, eligible, purged, held, note);
    }

    private static async Task<int> CountTimeAsync(IWorkbenchDbContext db, List<Guid> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
        {
            return 0;
        }

        var wbs = db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId)).Select(w => w.Id);
        return await db.Set<TimeEntry>().CountAsync(t => wbs.Contains(t.WbsElementId), cancellationToken);
    }

    private static async Task<Dictionary<string, int>> LoadOverridesAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        await db.Set<RetentionPolicy>().AsNoTracking().ToDictionaryAsync(p => p.RecordType, p => p.RetentionDays, cancellationToken);

    private RetentionPolicyDto ToDto(Definition d, Dictionary<string, int> overrides) =>
        new(d.RecordType, d.Description, overrides.TryGetValue(d.RecordType, out var days) ? days : d.Default(options), d.Protected, d.AfterContractClose, overrides.ContainsKey(d.RecordType));

    private static LegalHoldDto ToDto(LegalHold h) =>
        new(h.Id, h.EntityType, h.EntityId, h.Label, h.Reason, h.PlacedBy, h.PlacedAtUtc, h.ReleasedBy, h.ReleasedAtUtc);
}

public static class HoldTypes
{
    public const string Contract = "Contract";
    public const string Project = "Project";
}
