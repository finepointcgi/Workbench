using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Hr;
using Workbench.Domain.Identity;
using Workbench.Tests.Identity;
using Workbench.Tests.Payroll;

namespace Workbench.Tests.Hr;

/// <summary>
/// HR over <see cref="PayrollKit"/> (default chart; Paylocity pay group BW; pm@ salaried 104,000 with Paylocity id 1001, analyst@
/// hourly 50 (2002), viewer@ hourly 30 (3003); analyst@ and viewer@ report to pm@; outsider@ has no supervisor and no
/// payroll profile). Adds hr@example.com with the HR role (no employee record). Calls run as finance@ until switched.
/// </summary>
internal sealed class HrKit : IAsyncDisposable
{
    public const string Hr = "hr@example.com";

    private HrKit(PayrollKit payroll) => Payroll = payroll;

    public PayrollKit Payroll { get; }

    public SeededHost Seeded => Payroll.Seeded;

    public ProjectAccountingFixture Fx => Payroll.Fx;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public IHrPersonnelService Personnel => Get<IHrPersonnelService>();
    public IPositionService Positions => Get<IPositionService>();
    public ICompensationService Compensation => Get<ICompensationService>();
    public IBenefitService Benefits => Get<IBenefitService>();
    public IBenefitDeductionService Deductions => Get<IBenefitDeductionService>();
    public IOnboardingService Onboarding => Get<IOnboardingService>();
    public IHrReportService Reports => Get<IHrReportService>();
    public IWorkbenchDbContextFactory Factory => Payroll.Factory;

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public HrKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public static async Task<HrKit> CreateAsync()
    {
        var kit = new HrKit(await PayrollKit.CreateAsync());
        await using var db = await kit.Factory.CreateAsync();
        db.Set<AppUser>().Add(new AppUser { Email = Hr, DisplayName = "Harper", IsHr = true });
        await db.SaveChangesAsync();
        return kit;
    }

    public async Task<Guid> AccountAsync(string number)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Domain.Accounting.Account>().Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
    }

    /// <summary>Plans for this year and next, created as hr@: a PPO and an HDHP (four tiers), dental, life, 401(k), FSA and HSA.</summary>
    public async Task<Plans> PlansAsync(Guid? medicalAccount = null)
    {
        As(Hr);
        var start = new DateOnly(Today.Year, 1, 1);
        var end = new DateOnly(Today.Year + 1, 12, 31);
        SaveBenefitTier[] Tiers(decimal ee, decimal er) =>
        [
            new(CoverageLevel.EmployeeOnly, ee, er), new(CoverageLevel.EmployeeSpouse, ee * 2, er * 2),
            new(CoverageLevel.EmployeeChildren, ee * 2, er * 2), new(CoverageLevel.Family, ee * 3, er * 3),
        ];
        Task<BenefitPlanDto> Save(string code, BenefitPlanType type, SaveBenefitTier[] tiers, bool hdhp = false, decimal? limit = null, decimal? match = null,
            decimal? cap = null, decimal? employerMonthly = null, bool fullTimeOnly = true, Guid? account = null) =>
            Benefits.SavePlanAsync(null, new SaveBenefitPlanRequest(code, code, type, "Carrier", start, end, code.Replace("-", ""), true, account, hdhp, fullTimeOnly,
                limit, match, cap, employerMonthly, null, true, tiers));
        return new Plans(
            await Save("MED-PPO", BenefitPlanType.Medical, Tiers(100m, 500m), account: medicalAccount),
            await Save("MED-HDHP", BenefitPlanType.Medical, Tiers(50m, 450m), hdhp: true, account: medicalAccount),
            await Save("DEN", BenefitPlanType.Dental, Tiers(10m, 20m)),
            await Save("LIFE", BenefitPlanType.Life, [new(CoverageLevel.EmployeeOnly, 0m, 20m)]),
            await Save("401K", BenefitPlanType.Retirement401k, [], limit: 75m, match: 1m, cap: 0.04m, fullTimeOnly: false),
            await Save("FSA", BenefitPlanType.Fsa, [], limit: 3_300m),
            await Save("HSA", BenefitPlanType.Hsa, [], limit: 4_300m, employerMonthly: 50m));
    }

    public sealed record Plans(BenefitPlanDto Ppo, BenefitPlanDto Hdhp, BenefitPlanDto Dental, BenefitPlanDto Life, BenefitPlanDto K401, BenefitPlanDto Fsa, BenefitPlanDto Hsa);

    /// <summary>Open enrollment open today for two weeks, coverage from next January 1 (as hr@).</summary>
    public async Task<OpenEnrollmentWindowDto> OpenWindowAsync()
    {
        As(Hr);
        return await Benefits.SaveWindowAsync(null, new SaveOpenEnrollmentWindowRequest("OE", Today, Today.AddDays(14), new DateOnly(Today.Year + 1, 1, 1)));
    }

    public async Task<int> SecurityEventsAsync(string type)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Domain.Security.SecurityEvent>().CountAsync(e => e.EventType == type);
    }

    public ValueTask DisposeAsync() => Payroll.DisposeAsync();
}
