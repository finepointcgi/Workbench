using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Leave;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

/// <summary>Leave policies. Everyone signed in reads them (they explain balances); administrators change them (audited).</summary>
public sealed class LeavePolicyService(IWorkbenchDbContextFactory dbFactory, AccessControl access, AuditRecorder audit, TimeProvider clock) : ILeavePolicyService
{
    public const string AuditEntityType = nameof(LeavePolicy);

    public async Task<IReadOnlyList<LeavePolicyDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Include(p => p.Tiers)
            .Where(p => includeInactive || p.IsActive).OrderBy(p => p.Code).ToListAsync(cancellationToken);
        var codes = await WbsCodesAsync(db, policies.Select(p => p.WbsElementId), cancellationToken);
        return policies.Select(p => p.ToDto(codes.GetValueOrDefault(p.WbsElementId, "?"))).ToList();
    }

    public async Task<IReadOnlyList<LeaveChargeCodeDto>> ListLeaveChargeCodesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<WbsElement>().AsNoTracking()
            .Where(w => w.Kind == ChargeKind.Leave && w.IsChargeable && w.IsOpen)
            .OrderBy(w => w.Code)
            .Select(w => new LeaveChargeCodeDto(w.Id, w.Code, w.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<LeavePolicyDto> CreateAsync(SaveLeavePolicyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var policy = new LeavePolicy { Code = "", Name = "" };
        await ApplyAsync(db, policy, request, cancellationToken);
        db.Set<LeavePolicy>().Add(policy);
        audit.Record(db, AuditEntityType, policy.Id, "Created", after: Snapshot(policy));
        await db.SaveChangesAsync(cancellationToken);
        return policy.ToDto((await WbsCodesAsync(db, [policy.WbsElementId], cancellationToken))[policy.WbsElementId]);
    }

    public async Task<LeavePolicyDto> UpdateAsync(Guid policyId, SaveLeavePolicyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var policy = await db.Set<LeavePolicy>().Include(p => p.Tiers).FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Leave policy", policyId);
        var before = Snapshot(policy);
        db.Set<LeaveAccrualTier>().RemoveRange(policy.Tiers);
        policy.Tiers.Clear();
        await ApplyAsync(db, policy, request, cancellationToken);
        db.Set<LeaveAccrualTier>().AddRange(policy.Tiers);
        audit.Record(db, AuditEntityType, policy.Id, "Updated", before, Snapshot(policy));
        await db.SaveChangesAsync(cancellationToken);
        return policy.ToDto((await WbsCodesAsync(db, [policy.WbsElementId], cancellationToken))[policy.WbsElementId]);
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, LeavePolicy policy, SaveLeavePolicyRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var code = r.Code.Required("Code").ToUpperInvariant();
        if (code.Length > 32)
        {
            throw new ArgumentException("The code can be at most 32 characters.");
        }

        if (await db.Set<LeavePolicy>().AnyAsync(p => p.Code == code && p.Id != policy.Id, ct))
        {
            throw new ArgumentException($"A leave policy with code {code} already exists.");
        }

        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == r.WbsElementId, ct)
            ?? throw new ArgumentException("Choose the policy's leave charge code.");
        if (wbs.Kind != ChargeKind.Leave)
        {
            throw new ArgumentException($"{wbs.Code} isn't a Leave charge code.");
        }

        if (r.MaxBalance is < 0 || r.CarryoverLimit is < 0 || r.WaitingPeriodDays < 0)
        {
            throw new ArgumentException("Caps, carryover limits and waiting periods can't be negative.");
        }

        var tiers = r.Tiers ?? [];
        if (r.Method != AccrualMethodKind.None && tiers.Count == 0)
        {
            throw new ArgumentException("An accruing policy needs at least one tier (the rate from 0 years of service).");
        }

        if (tiers.Any(t => t.MinYearsOfService < 0 || t.Rate < 0 || t.MaxBalance is < 0))
        {
            throw new ArgumentException("Tier years, rates and caps can't be negative.");
        }

        if (tiers.GroupBy(t => t.MinYearsOfService).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each tier needs a different number of years of service.");
        }

        policy.Code = code;
        policy.Name = r.Name.Required("Name");
        policy.Type = (LeaveType)(int)r.Type;
        policy.WbsElementId = wbs.Id;
        policy.Method = (AccrualMethod)(int)r.Method;
        policy.MaxBalance = r.MaxBalance;
        policy.CarryoverLimit = r.CarryoverLimit;
        policy.WaitingPeriodDays = r.WaitingPeriodDays;
        policy.AllowNegativeBalance = r.AllowNegativeBalance;
        policy.IsActive = r.IsActive;
        policy.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        foreach (var t in tiers.OrderBy(t => t.MinYearsOfService))
        {
            policy.Tiers.Add(new LeaveAccrualTier { PolicyId = policy.Id, MinYearsOfService = t.MinYearsOfService, Rate = t.Rate, MaxBalance = t.MaxBalance });
        }
    }

    private static object Snapshot(LeavePolicy p) => new
    {
        p.Code,
        p.Name,
        Type = p.Type.ToString(),
        Method = p.Method.ToString(),
        p.MaxBalance,
        p.CarryoverLimit,
        p.WaitingPeriodDays,
        p.AllowNegativeBalance,
        p.IsActive,
        Tiers = p.Tiers.OrderBy(t => t.MinYearsOfService).Select(t => new { t.MinYearsOfService, t.Rate, t.MaxBalance }).ToList(),
    };

    internal static async Task<Dictionary<Guid, string>> WbsCodesAsync(IWorkbenchDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<WbsElement>().AsNoTracking().Where(w => list.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
    }
}

/// <summary>The holiday calendar. Everyone reads it; administrators change it (audited).</summary>
public sealed class HolidayService(IWorkbenchDbContextFactory dbFactory, AccessControl access, AuditRecorder audit) : IHolidayService
{
    public const string AuditEntityType = nameof(Holiday);

    public async Task<IReadOnlyList<HolidayDto>> ListAsync(int year, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        return await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= start && h.Date <= end).OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Id, h.Date, h.Name, h.Hours)).ToListAsync(cancellationToken);
    }

    public async Task<HolidayDto> AddAsync(SaveHolidayRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Hours is < 0 or > 24)
        {
            throw new ArgumentException("Holiday hours must be between 0 and 24.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<Holiday>().AnyAsync(h => h.Date == request.Date, cancellationToken))
        {
            throw new ArgumentException($"{request.Date:yyyy-MM-dd} is already a holiday.");
        }

        var holiday = new Holiday { Date = request.Date, Name = request.Name.Required("Name"), Hours = request.Hours };
        db.Set<Holiday>().Add(holiday);
        audit.Record(db, AuditEntityType, holiday.Id, "Added", after: new { holiday.Date, holiday.Name, holiday.Hours });
        await db.SaveChangesAsync(cancellationToken);
        return new HolidayDto(holiday.Id, holiday.Date, holiday.Name, holiday.Hours);
    }

    public async Task RemoveAsync(Guid holidayId, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var holiday = await db.Set<Holiday>().FirstOrDefaultAsync(h => h.Id == holidayId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Holiday", holidayId);
        db.Set<Holiday>().Remove(holiday);
        audit.Record(db, AuditEntityType, holiday.Id, "Removed", before: new { holiday.Date, holiday.Name, holiday.Hours });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> ApplyFederalTemplateAsync(int year, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        if (year is < 2000 or > 2100)
        {
            throw new ArgumentException("Choose a year between 2000 and 2100.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var added = await AddFederalAsync(db, year, cancellationToken);
        foreach (var h in db.Set<Holiday>().Local.ToList())
        {
            audit.Record(db, AuditEntityType, h.Id, "Added", after: new { h.Date, h.Name, Template = "US federal" });
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>Adds the federal holidays of a year that aren't on the calendar yet (not saved).</summary>
    public static async Task<int> AddFederalAsync(IWorkbenchDbContext db, int year, CancellationToken ct)
    {
        var holidays = FederalHolidays.For(year);
        var dates = holidays.Select(h => h.Date).ToList();
        var existing = (await db.Set<Holiday>().AsNoTracking().Where(h => dates.Contains(h.Date)).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var (date, name) in holidays.Where(h => !existing.Contains(h.Date)))
        {
            db.Set<Holiday>().Add(new Holiday { Date = date, Name = name });
            added++;
        }

        return added;
    }
}
