using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Contracts.Crm;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using Workbench.Tests.Time;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (FY2027 = Oct 2026–Sep 2027) with the clock at Wed
/// 2026-10-21, two Business development users (bd@ and bd2@), the indirect project IND with a chargeable B&amp;P element
/// IND.BP, and an agency (Army) with an office (PEO EIS). The SAM.gov client is the built-in sample.
/// </summary>
internal sealed class CrmKit : IAsyncDisposable
{
    public const string Bd = "bd@example.com";
    public const string Bd2 = "bd2@example.com";
    public static readonly DateOnly Today = new(2026, 10, 21);

    private CrmKit(SeededHost seeded, ProjectAccountingFixture fx)
    {
        Seeded = seeded;
        Fx = fx;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public Guid ArmyId { get; private set; }
    public Guid PeoId { get; private set; }
    public Guid BpParentId { get; private set; }
    public Guid IndirectProjectId { get; private set; }

    public IOpportunityService Opportunities => Get<IOpportunityService>();
    public IOpportunityGateService Gates => Get<IOpportunityGateService>();
    public IOpportunityLeadService Leads => Get<IOpportunityLeadService>();
    public IPipelineService Pipeline => Get<IPipelineService>();
    public ICrmAccountService Accounts => Get<ICrmAccountService>();
    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public CrmKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public static async Task<CrmKit> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var clock = new TestClock(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));
        var seeded = await SeededHost.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        });
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new CrmKit(seeded, fx);
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<AppUser>().AddRange(
                new AppUser { Email = Bd, DisplayName = "Blake", IsBusinessDevelopment = true },
                new AppUser { Email = Bd2, DisplayName = "Bree", IsBusinessDevelopment = true });
            var indirect = new Project { Code = "IND", Name = "Indirect" };
            var root = new WbsElement { ProjectId = indirect.Id, Code = "IND", Name = "Indirect", Kind = ChargeKind.Indirect, IsChargeable = false, RequiresAssignment = false };
            var bp = new WbsElement { ProjectId = indirect.Id, ParentId = root.Id, Code = "IND.BP", Name = "Bid and proposal", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 1 };
            db.Set<Project>().Add(indirect);
            db.Set<WbsElement>().AddRange(root, bp);
            await db.SaveChangesAsync();
            kit.BpParentId = bp.Id;
            kit.IndirectProjectId = indirect.Id;
        }

        kit.As(Bd);
        kit.ArmyId = (await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Department of the Army", CrmAccountKind.Agency, "Army"))).Id;
        kit.PeoId = (await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("PEO Enterprise Information Systems", CrmAccountKind.Office, "PEO EIS", kit.ArmyId))).Id;
        return kit;
    }

    /// <summary>A new opportunity (as bd@, its capture manager unless <paramref name="capture"/> says otherwise).</summary>
    public async Task<OpportunityDto> NewAsync(string name = "Cloud migration", decimal value = 1_000_000m, int pwin = 50, int pgo = 80, string? solicitation = null,
        DateOnly? award = null, OpportunityKind kind = OpportunityKind.New, string? capture = null, string? proposal = null)
    {
        As(Bd);
        return await Opportunities.CreateAsync(new SaveOpportunityRequest(name, SolicitationNumber: solicitation, AgencyAccountId: PeoId, Kind: kind,
            EstimatedValue: value, EstimatedAwardDate: award, PeriodOfPerformanceMonths: 36, CaptureManagerEmail: capture, ProposalManagerEmail: proposal, Pwin: pwin, Pgo: pgo));
    }

    /// <summary>Scores every question of the gate into <paramref name="stage"/> with <paramref name="score"/> and submits it as <paramref name="by"/>.</summary>
    public async Task<GateReviewDto> SubmitGateAsync(Guid opportunityId, OpportunityStage stage, string by = Bd, GateRecommendation recommendation = GateRecommendation.Go, int score = 4)
    {
        As(by);
        var gate = (await Gates.ListGatesAsync()).Single(g => g.ToStage == stage);
        return await Gates.SubmitAsync(opportunityId, new SubmitGateReviewRequest(gate.Id, recommendation, gate.Criteria.Select(c => new GateScoreInput(c.Id, score)).ToList()));
    }

    /// <summary>Moves an opportunity forward through each gate (bd@ submits, bd2@ approves) and the ungated stages.</summary>
    public async Task<OpportunityDto> AdvanceAsync(Guid id, OpportunityStage target)
    {
        As(Bd);
        var current = (await Opportunities.GetAsync(id)).Stage;
        var gates = await Gates.ListGatesAsync();
        foreach (var stage in OpportunityStages.Open.Where(s => s > current && s <= target))
        {
            if (gates.Any(g => g.ToStage == stage))
            {
                var review = await SubmitGateAsync(id, stage);
                if (review.Status == GateReviewStatus.Pending)
                {
                    As(Bd2);
                    await Gates.ApproveAsync(review.Id, new DecideGateRequest("Go"));
                }
            }
            else
            {
                As(Bd);
                await Opportunities.ChangeStageAsync(id, new ChangeStageRequest(stage));
            }
        }

        As(Bd);
        return await Opportunities.GetAsync(id);
    }

    public async Task<WbsElement> WbsAsync(Guid id)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<WbsElement>().AsNoTracking().FirstAsync(w => w.Id == id);
    }

    /// <summary>Hours on a WBS element on <see cref="ProjectAccountingFixture.Day"/> for analyst@, on a timesheet in <paramref name="status"/>.</summary>
    public async Task ChargeAsync(Guid wbsId, decimal hours, TimesheetStatus status)
    {
        await using var db = await Factory.CreateAsync();
        var category = await db.Set<LaborCategory>().Select(c => c.Id).FirstAsync();
        var now = DateTime.UtcNow;
        var sheet = new Timesheet { EmployeeId = Fx.AnalystId, PeriodStart = new DateOnly(2026, 10, 5), PeriodEnd = new DateOnly(2026, 10, 11), Status = status, CreatedAtUtc = now, UpdatedAtUtc = now };
        if (status == TimesheetStatus.Draft)
        {
            sheet.PeriodStart = new DateOnly(2026, 10, 19);
            sheet.PeriodEnd = new DateOnly(2026, 10, 25);
        }

        db.Set<Timesheet>().Add(sheet);
        db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbsId, LaborCategoryId = category, WorkDate = sheet.PeriodStart.AddDays(1), Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}
