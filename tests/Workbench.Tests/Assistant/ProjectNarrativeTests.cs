using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Assistant;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Assistant;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Evm;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assistant;

public sealed class ProjectNarrativeTests
{
    /// <summary>EvmKit's project with a PMB, progress and costs (as in EvmServiceTests), October closed.</summary>
    private static async Task<EvmKit> EvmAsync()
    {
        var kit = await EvmKit.CreateAsync();
        var pmb = await kit.Evm.CreateBaselineAsync(kit.Project.Id, new CreateEvmBaselineRequest(ControlAccountLevel: 2, ManagementReserve: 3_000));
        Guid Wp(string code) => pmb.WorkPackages.Single(w => w.WbsCode == $"1001.01.{code}").Id;
        await kit.Evm.UpdateWorkPackageAsync(Wp("A"), new UpdateWorkPackageRequest(EvTechnique.ZeroHundred));
        await kit.Evm.UpdateWorkPackageAsync(Wp("D"), new UpdateWorkPackageRequest(EvTechnique.LevelOfEffort));
        await kit.Evm.SetThresholdsAsync(kit.Project.Id, new SetEvmThresholdsRequest(10, 1_000));
        await kit.SetPlanAsync("A", status: "Done");
        await kit.SetPlanAsync("B", status: "In progress", percent: 40);
        await kit.CostAsync("A", new(2026, 10, 20), 12_000);
        await kit.CostAsync("B", new(2026, 11, 3), 9_000);
        await kit.CostAsync("D", new(2026, 10, 15), 4_000);
        await kit.Evm.ClosePeriodAsync(kit.Project.Id, kit.Periods["2026-10"].Id);
        await kit.CostAsync("C", new(2026, 11, 10), 14_000);
        return kit;
    }

    private static NarrativeFacts Facts(EvmKit kit, EvmStatusDto nov, EvmStatusDto oct, IReadOnlyList<EvmVarianceDto> variances)
    {
        var period = kit.Periods["2026-11"];
        var project = new ProjectDto(kit.Project.Id, "1001", "Modernization", null, "W56KGU-26-C-0001", null, ProjectState.Active, null, null, kit.Project.SheetId);
        var ceiling = new CeilingStatusDto(
            new CeilingDto(Guid.NewGuid(), CeilingScope.Project, null, null, null, null, kit.Project.Id, "1001", null, null, null, null, 2_000, null, null, CeilingMode.Block, [80, 95, 100], false, null, true, "Project 1001 hours"),
            [new CeilingMeasureDto(CeilingMetric.Hours, 2_000, 2_000, 1_900, 100, 0.95m, CeilingAlertLevel.Critical)], [], CeilingAlertLevel.Critical, false);
        var clin = new ClinFundingStatusDto(Guid.NewGuid(), "0001", "Engineering", false, true, new(2026, 10, 1), new(2027, 9, 30), 500_000, 100_000, 80_000, 5_000, 15_000, 0.8m, 0.85m,
            1_000, new DateOnly(2026, 12, 15), FundingLevel.Warning);
        return new NarrativeFacts(project, new FiscalPeriodDto(period.Id, 2027, period.Name, 2, period.Start, period.End, FiscalPeriodState.Open), null, [], nov, oct, variances,
            [("Task A", new DateOnly(2026, 11, 6))], [("Design review", new DateOnly(2027, 1, 15), "plan")], [ceiling], [clin], [], []);
    }

    [Fact]
    public async Task The_narrative_is_assembled_from_earned_value_the_plan_ceilings_and_funding()
    {
        var kit = await EvmAsync();
        var nov = await kit.Evm.GetStatusAsync(kit.Project.Id, kit.Periods["2026-11"].Id);
        var oct = await kit.Evm.GetStatusAsync(kit.Project.Id, kit.Periods["2026-10"].Id);
        var variances = await kit.Evm.ListVariancesAsync(kit.Project.Id, kit.Periods["2026-11"].Id);
        Assert.NotEqual(nov.Total.Eac, oct.Total.Eac);

        var draft = NarrativeAssembler.Assemble(Facts(kit, nov, oct, variances));
        var s = draft.Sections;

        Assert.Contains($"CPI {nov.Total.Cpi:0.00}", s.Summary);
        Assert.Contains($"Cumulative cost variance is {AssistantToolbox.Money(nov.Total.Cv)}", s.CostVariance);
        Assert.Contains($"SPI {nov.Total.Spi:0.00}", s.ScheduleVariance);
        Assert.Contains(AssistantToolbox.Money(nov.Total.Eac), s.EacChanges);
        Assert.Contains($"from {AssistantToolbox.Money(oct.Total.Eac)} in 2026-10", s.EacChanges);
        Assert.Contains("Task A (finished Nov 6)", s.Accomplishments);
        Assert.Contains("Design review: Jan 15, 2027", s.UpcomingMilestones);
        Assert.Contains("Project 1001 hours: Hours 95% used (Critical, blocking)", s.Risks);
        Assert.Contains("CLIN 0001 funding: $15,000 of $100,000 remaining, projected to run out about Dec 15, 2026", s.Risks);

        var needing = variances.Where(v => v.NeedsVar).ToList();
        Assert.NotEmpty(needing);
        Assert.Equal(needing.Select(v => v.Code), draft.Variances.Select(v => v.Code));
        Assert.All(draft.Variances, v => Assert.Contains("Cause to be confirmed by the PM", v.Cause));
        Assert.All(draft.Variances, v => Assert.StartsWith("EAC ", v.Impact));
    }

    [Fact]
    public async Task The_models_rewrite_is_merged_but_only_for_known_control_accounts()
    {
        var kit = await EvmAsync();
        var nov = await kit.Evm.GetStatusAsync(kit.Project.Id, kit.Periods["2026-11"].Id);
        var variances = await kit.Evm.ListVariancesAsync(kit.Project.Id, kit.Periods["2026-11"].Id);
        var draft = NarrativeAssembler.Assemble(Facts(kit, nov, nov, variances));
        var code = draft.Variances[0].Code;

        Assert.True(NarrativeAssembler.TryMerge(draft, $$"""{"summary":"Better summary.","costVariance":"","variances":[{"code":"{{code}}","cause":"Rework on A.","impact":"x","correctiveAction":"y"},{"code":"9.9","cause":"injected"}]}""", out var merged));
        Assert.Equal("Better summary.", merged.Sections.Summary);
        Assert.Equal(draft.Sections.CostVariance, merged.Sections.CostVariance);
        Assert.Equal("Rework on A.", merged.Variances[0].Cause);
        Assert.Equal(draft.Variances.Count, merged.Variances.Count);
        Assert.DoesNotContain(merged.Variances, v => v.Cause == "injected");
        Assert.False(NarrativeAssembler.TryMerge(draft, "not json", out _));
    }

    [Fact]
    public async Task The_saved_narrative_is_the_format_5_program_narrative()
    {
        var kit = await EvmAsync();
        var nov = kit.Periods["2026-11"];
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<ProjectNarrative>().Add(new ProjectNarrative
            {
                ProjectId = kit.Project.Id, FiscalPeriodId = nov.Id, PeriodName = nov.Name, Summary = "Program on track despite rework.", Risks = "CLIN 0001 funding.",
                DraftSource = "assistant", CreatedBy = "pm@example.com", UpdatedBy = "pm@example.com", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var file = await kit.Exports.ExportFormat5Async(kit.Project.Id, nov.Id);
        using var book = new XLWorkbook(new MemoryStream(file.Content));
        var text = string.Join("\n", book.Worksheet("Summary").CellsUsed().Select(c => c.GetString()));
        Assert.Contains("Program narrative", text);
        Assert.Contains("Program on track despite rework.", text);
        Assert.Contains("CLIN 0001 funding.", text);
    }

    [Fact]
    public async Task The_pm_drafts_edits_and_saves_and_others_are_refused()
    {
        await using var kit = await AssistantKit.CreateAsync();
        Guid periodId;
        await using (var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            periodId = (await db.Set<FiscalPeriod>().SingleAsync()).Id;
        }

        kit.As(Editor);
        var narratives = kit.Get<IProjectNarrativeService>();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => narratives.DraftAsync(kit.Data.Project1, periodId));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => narratives.SaveAsync(kit.Data.Project1, new SaveProjectNarrativeRequest(periodId, NarrativeSectionsDto.Empty)));

        kit.As(Owner);
        Assert.Null(await narratives.GetAsync(kit.Data.Project1, periodId));
        var draft = await narratives.DraftAsync(kit.Data.Project1, periodId);
        Assert.Equal("template", draft.DraftSource);
        Assert.False(draft.IsSaved);
        Assert.Contains("1001 Modernization, 2026-10", draft.Sections.Summary);
        Assert.Contains("no earned value baseline", draft.Sections.ScheduleVariance);
        Assert.Contains(draft.Facts, f => f.Label == "Actual cost" && f.Href == $"projects/{kit.Data.Project1}/financials");
        Assert.Null(await narratives.GetAsync(kit.Data.Project1, periodId)); // drafting saves nothing

        var saved = await narratives.SaveAsync(kit.Data.Project1, new SaveProjectNarrativeRequest(periodId, draft.Sections with { Summary = "Edited by the PM." }, draft.DraftSource));
        Assert.True(saved.IsSaved);
        Assert.Equal("Edited by the PM.", saved.Sections.Summary);
        Assert.Equal(Owner, saved.UpdatedBy);
        Assert.Contains(await narratives.ListPeriodsAsync(kit.Data.Project1), p => p.FiscalPeriodId == periodId && p.HasNarrative);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            narratives.SaveAsync(kit.Data.Project1, new SaveProjectNarrativeRequest(periodId, NarrativeSectionsDto.Empty with { Summary = new string('x', 8_001) })));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => narratives.SaveAsync(kit.Data.Project1, new SaveProjectNarrativeRequest(Guid.NewGuid(), NarrativeSectionsDto.Empty)));

        await using var check = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Contains(await check.Set<AuditEvent>().ToListAsync(), e => e.EntityType == "ProjectNarrative");
    }

    [Fact]
    public async Task With_the_assistant_on_the_draft_goes_through_the_provider_and_is_audited()
    {
        await using var kit = await AssistantKit.CreateAsync();
        await kit.EnableAsync();
        Guid periodId;
        await using (var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            periodId = (await db.Set<FiscalPeriod>().SingleAsync()).Id;
        }

        var draft = await kit.Get<IProjectNarrativeService>().DraftAsync(kit.Data.Project1, periodId);

        Assert.Equal("assistant", draft.DraftSource);
        await using var check = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var interaction = Assert.Single(await check.Set<AssistantInteraction>().ToListAsync());
        Assert.Equal(AssistantInteractionKind.Narrative, interaction.Kind);
        Assert.Equal(AssistantOutcome.Answered, interaction.Outcome);
        var call = Assert.Single(await check.Set<AssistantToolCall>().ToListAsync());
        Assert.Equal("narrative_facts", call.ToolName);
        Assert.Contains($"Project:{kit.Data.Project1}", call.RecordsRead);
    }
}
