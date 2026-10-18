using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for payroll: a biweekly Paylocity pay group (company W1234) whose periods line up with the
/// seeded weekly timesheets — the last complete period is the two weeks ending last Sunday, checks the Friday after —
/// pay periods for last, this and next year, the default earning codes, and a payroll profile for each seeded employee
/// (Paylocity ids 1001–1004, matching <c>samples/payroll/paylocity-register-sample.csv</c>). Runs once.
/// </summary>
internal static class PayrollSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<PayGroup>().AnyAsync(cancellationToken) || !await db.Set<Employee>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var lastWeek = PayrollHours.WeekStart(today).AddDays(-7);
        var group = new PayGroup
        {
            Code = "BIWEEKLY",
            Name = "Biweekly (Paylocity)",
            Frequency = PayFrequency.Biweekly,
            ProviderKey = PaylocityPayrollProvider.ProviderKey,
            CompanyCode = "W1234",
            AnchorDate = lastWeek.AddDays(-7),
            CheckDateOffsetDays = 5,
            CreatedAtUtc = now,
        };
        db.Add(group);
        foreach (var year in new[] { today.Year - 1, today.Year, today.Year + 1 })
        {
            await PayGroupService.AddPeriodsAsync(db, group, year, cancellationToken);
        }

        db.Add(new PayrollProviderSetting
        {
            ProviderKey = PaylocityPayrollProvider.ProviderKey,
            EarningCodes = EarningCodes.Format(EarningCodes.Defaults),
            UpdatedAtUtc = now,
            UpdatedBy = "seed@workbench.local",
        });

        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        (string Email, string ProviderId, PayType Type, decimal? Salary, decimal? Hourly)[] profiles =
        [
            ("pm@example.com", "1001", PayType.Salaried, 184_080m, null),
            ("analyst@example.com", "1002", PayType.Hourly, null, 50m),
            ("dev@workbench.local", "1003", PayType.Salaried, 135_200m, null),
            ("viewer@example.com", "1004", PayType.Hourly, null, 30m),
        ];
        foreach (var (email, providerId, type, salary, hourly) in profiles)
        {
            if (employees.TryGetValue(email, out var employee))
            {
                db.Add(new EmployeePayProfile
                {
                    EmployeeId = employee.Id,
                    PayGroupId = group.Id,
                    ProviderEmployeeId = providerId,
                    PayType = type,
                    AnnualSalary = salary,
                    HourlyRate = hourly,
                    EffectiveFrom = employee.HireDate,
                    UpdatedAtUtc = now,
                    UpdatedBy = "seed@workbench.local",
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
