using Workbench.Application.Compliance;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Tests.Evm;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Compliance;

public sealed class CmmcTests
{
    private static (CmmcService Service, MutableClock Clock) Create()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero));
        return (new CmmcService(TestDb.CreateFactory(), new AuditRecorder(new EvmKit.User(), clock), clock), clock);
    }

    private static SaveCmmcPracticeRequest Mark(CmmcStatusDto status, bool partial = false) => new(status, partial);

    [Fact]
    public void The_catalogue_is_the_110_requirements_with_the_methodologys_points()
    {
        var all = Nist171.Requirements;
        Assert.Equal(110, all.Count);
        Assert.Equal(110, all.Select(r => r.Id).Distinct().Count());
        Assert.Equal([22, 3, 9, 9, 11, 3, 6, 9, 2, 6, 3, 4, 16, 7], Enumerable.Range(1, 14).Select(f => all.Count(r => r.FamilyNumber == f)));
        Assert.Equal((30, 80), (all.Count(r => r.IsBasic), all.Count(r => !r.IsBasic)));
        Assert.Equal((44, 14, 51), (all.Count(r => r.Weight == 5), all.Count(r => r.Weight == 3), all.Count(r => r.Weight == 1)));
        Assert.Equal(313, all.Sum(r => r.Weight)); // 110 down to -203, the range the methodology gives
        Assert.Equal(0, all.Single(r => r.Id == "3.12.4").Weight); // the system security plan: required, but not scored
        Assert.Equal((5, 3, 5, 1), (all.Single(r => r.Id == "3.1.1").Weight, all.Single(r => r.Id == "3.1.19").Weight, all.Single(r => r.Id == "3.5.3").Weight, all.Single(r => r.Id == "3.1.8").Weight));
        Assert.All(all, r => Assert.False(string.IsNullOrWhiteSpace(r.Statement)));
        Assert.Equal(["3.13.11", "3.5.3"], all.Where(r => r.AllowsPartialCredit).Select(r => r.Id).Order());
    }

    [Fact]
    public async Task Nothing_assessed_scores_110_and_every_failure_costs_its_points()
    {
        var (cmmc, _) = Create();
        var start = await cmmc.GetAssessmentAsync();
        Assert.Equal((110, 110 - Nist171.Requirements.Sum(r => r.Weight), 110), (start.Score, start.WorstCaseScore, start.NotAssessed));

        await cmmc.SavePracticeAsync("3.1.1", Mark(CmmcStatusDto.NotImplemented)); // 5 points
        await cmmc.SavePracticeAsync("3.1.19", Mark(CmmcStatusDto.NotImplemented)); // 3 points
        await cmmc.SavePracticeAsync("3.1.8", Mark(CmmcStatusDto.NotImplemented)); // 1 point
        await cmmc.SavePracticeAsync("3.1.2", Mark(CmmcStatusDto.Implemented));
        await cmmc.SavePracticeAsync("3.1.3", Mark(CmmcStatusDto.NotApplicable));

        var a = await cmmc.GetAssessmentAsync();
        Assert.Equal((101, 3, 1, 1, 105), (a.Score, a.NotImplemented, a.Implemented, a.NotApplicable, a.NotAssessed));
        Assert.Equal(3, a.UncoveredGaps); // none has a plan yet
        Assert.Equal((1, 3), (a.Families.Single(f => f.Family == "Access Control").Implemented, a.Families.Single(f => f.Family == "Access Control").NotImplemented));
    }

    [Fact]
    public async Task A_partly_met_mfa_or_fips_requirement_loses_3_points_and_no_other_can_be_partly_met()
    {
        var (cmmc, _) = Create();
        await cmmc.SavePracticeAsync("3.5.3", Mark(CmmcStatusDto.NotImplemented));
        Assert.Equal(105, (await cmmc.GetAssessmentAsync()).Score);
        await cmmc.SavePracticeAsync("3.5.3", Mark(CmmcStatusDto.NotImplemented, partial: true));
        Assert.Equal(107, (await cmmc.GetAssessmentAsync()).Score);

        await Assert.ThrowsAsync<ArgumentException>(() => cmmc.SavePracticeAsync("3.1.1", Mark(CmmcStatusDto.NotImplemented, partial: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => cmmc.SavePracticeAsync("3.5.3", Mark(CmmcStatusDto.Implemented, partial: true)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => cmmc.SavePracticeAsync("9.9.9", Mark(CmmcStatusDto.Implemented)));
    }

    [Fact]
    public async Task Marking_a_practice_again_updates_it_and_keeps_the_notes()
    {
        var (cmmc, _) = Create();
        await cmmc.SavePracticeAsync("3.3.1", new SaveCmmcPracticeRequest(CmmcStatusDto.NotImplemented, Implementation: "Logging is partial", Evidence: "SSP 3.3", OwnerEmail: "Sec@Example.com"));
        var done = await cmmc.SavePracticeAsync("3.3.1", new SaveCmmcPracticeRequest(CmmcStatusDto.Implemented, Implementation: "Central logging in place", OwnerEmail: "Sec@Example.com", AssessedOn: new DateOnly(2026, 11, 16)));

        Assert.Equal((CmmcStatusDto.Implemented, 0, "sec@example.com", "Central logging in place"), (done.Status, done.Deduction, done.OwnerEmail, done.Implementation));
        Assert.Equal(1, (await cmmc.GetAssessmentAsync()).Practices.Count(p => p.Status == CmmcStatusDto.Implemented));
    }

    [Fact]
    public async Task Plan_of_action_items_cover_gaps_go_overdue_and_close()
    {
        var (cmmc, clock) = Create();
        await cmmc.SavePracticeAsync("3.1.1", Mark(CmmcStatusDto.NotImplemented));
        var item = await cmmc.AddPoamItemAsync(new SaveCmmcPoamRequest("3.1.1", "Roll out access reviews", "Sec@example.com", new DateOnly(2026, 11, 1)));

        var a = await cmmc.GetAssessmentAsync();
        Assert.Equal((0, 1, 1), (a.UncoveredGaps, a.OpenPoamItems, a.OverduePoamItems)); // due Nov 1, today is Nov 16
        Assert.True(a.Poam.Single().Overdue);
        Assert.Equal(1, a.Practices.Single(p => p.PracticeId == "3.1.1").OpenPoamItems);

        var closed = await cmmc.UpdatePoamItemAsync(item.Id, new SaveCmmcPoamRequest("3.1.1", "Roll out access reviews", Status: CmmcPoamStatusDto.Done));
        Assert.Equal((CmmcPoamStatusDto.Done, new DateOnly(2026, 11, 16), false), (closed.Status, closed.CompletedOn, closed.Overdue));
        Assert.Equal((1, 0), ((await cmmc.GetAssessmentAsync()).UncoveredGaps, (await cmmc.GetAssessmentAsync()).OpenPoamItems)); // done: the gap is uncovered again

        await cmmc.DeletePoamItemAsync(item.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => cmmc.UpdatePoamItemAsync(item.Id, new SaveCmmcPoamRequest("3.1.1", "x")));
        _ = clock;
    }

    [Theory]
    [InlineData("9.9.9", "Do it")]
    [InlineData("3.1.1", "  ")]
    public async Task A_plan_item_needs_a_real_requirement_and_a_milestone(string practice, string milestone)
    {
        var (cmmc, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => cmmc.AddPoamItemAsync(new SaveCmmcPoamRequest(practice, milestone)));
    }
}

/// <summary>The real registrations: the assessment belongs to those who manage compliance (Admin).</summary>
public sealed class CmmcPermissionTests
{
    [Fact]
    public async Task Admins_keep_the_assessment()
    {
        await using var seeded = await CreateAsync();
        var cmmc = seeded.As(Admin).Host.Get<ICmmcService>();

        Assert.Equal(110, (await cmmc.GetAssessmentAsync()).Practices.Count);
        await cmmc.SavePracticeAsync("3.1.1", new SaveCmmcPracticeRequest(CmmcStatusDto.Implemented));
        var item = await cmmc.AddPoamItemAsync(new SaveCmmcPoamRequest("3.1.2", "Do it"));
        await cmmc.DeletePoamItemAsync(item.Id);
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Editor)]
    [InlineData(Viewer)]
    public async Task Nobody_else_sees_or_changes_it(string email)
    {
        await using var seeded = await CreateAsync();
        var cmmc = seeded.As(email).Host.Get<ICmmcService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => cmmc.GetAssessmentAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => cmmc.SavePracticeAsync("3.1.1", new SaveCmmcPracticeRequest(CmmcStatusDto.Implemented)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => cmmc.AddPoamItemAsync(new SaveCmmcPoamRequest("3.1.2", "Do it")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => cmmc.DeletePoamItemAsync(Guid.NewGuid()));
    }
}
