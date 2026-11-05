using Microsoft.EntityFrameworkCore;
using Workbench.Application.Leave;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for leave: a PTO policy on LV.PTO (per pay period, tiers at 0/3/5 years, 240 h cap, 80 h
/// carryover, 90-day waiting period, no negative balances), a Holiday policy on LV.HOL, US federal holidays for this and
/// next year, opening PTO balances, and one approved request (Dana Lee, a Friday two weeks out, approved by pm@).
/// Runs once, after <see cref="PayrollSeed"/>.
/// </summary>
internal static class LeaveSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<LeavePolicy>().AnyAsync(cancellationToken))
        {
            return;
        }

        var wbs = await db.Set<WbsElement>().Where(w => w.Code == "LV.PTO" || w.Code == "LV.HOL").ToDictionaryAsync(w => w.Code, cancellationToken);
        if (wbs.Count < 2)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var pto = new LeavePolicy
        {
            Code = "PTO",
            Name = "Paid time off",
            Type = LeaveType.Pto,
            WbsElementId = wbs["LV.PTO"].Id,
            Method = AccrualMethod.PerPayPeriod,
            MaxBalance = 240,
            CarryoverLimit = 80,
            WaitingPeriodDays = 90,
            AllowNegativeBalance = false,
            UpdatedAtUtc = now,
        };
        pto.Tiers.AddRange(
        [
            new LeaveAccrualTier { PolicyId = pto.Id, MinYearsOfService = 0, Rate = 4.62m },
            new LeaveAccrualTier { PolicyId = pto.Id, MinYearsOfService = 3, Rate = 6.16m },
            new LeaveAccrualTier { PolicyId = pto.Id, MinYearsOfService = 5, Rate = 7.70m, MaxBalance = 280 },
        ]);
        var holiday = new LeavePolicy
        {
            Code = "HOL",
            Name = "Holiday",
            Type = LeaveType.Holiday,
            WbsElementId = wbs["LV.HOL"].Id,
            Method = AccrualMethod.None,
            AllowNegativeBalance = true,
            UpdatedAtUtc = now,
        };
        db.AddRange(pto, holiday);

        foreach (var year in new[] { today.Year, today.Year + 1 })
        {
            await HolidayService.AddFederalAsync(db, year, cancellationToken);
        }

        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var opening = new DateOnly(today.Year, 1, 1);
        foreach (var (email, hours) in new[] { ("pm@example.com", 64m), ("analyst@example.com", 40m), ("dev@workbench.local", 56m), ("viewer@example.com", 24m) })
        {
            if (employees.TryGetValue(email, out var e))
            {
                db.Add(new LeaveLedgerEntry
                {
                    EmployeeId = e.Id,
                    PolicyId = pto.Id,
                    Date = opening,
                    Kind = LeaveLedgerKind.Adjustment,
                    Hours = hours,
                    SourceKey = $"opening:{today.Year}",
                    Reason = "Opening balance carried over from the previous system",
                    CreatedBy = Actor,
                    CreatedAtUtc = now,
                });
            }
        }

        if (employees.TryGetValue("dev@workbench.local", out var dana))
        {
            var friday = today.AddDays(14 + ((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7);
            var request = new LeaveRequest
            {
                EmployeeId = dana.Id,
                PolicyId = pto.Id,
                StartDate = friday,
                EndDate = friday,
                Hours = 8,
                Comment = "Long weekend",
                Status = LeaveRequestStatus.Approved,
                CreatedAtUtc = now.AddDays(-3),
                DecidedBy = "pm@example.com",
                DecidedAtUtc = now.AddDays(-2),
            };
            request.Days.Add(new LeaveRequestDay { RequestId = request.Id, Date = friday, Hours = 8 });
            db.Add(request);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
