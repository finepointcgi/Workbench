using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Crm;

namespace Workbench.Tests.Crm;

/// <summary>The pipeline board and the opportunity list render the pipeline with keyboard-reachable moves and named controls.</summary>
public sealed class CrmPagesTests
{
    private static readonly PipelineCardDto Card = new(Guid.NewGuid(), "OPP-0005", "Army Enterprise Cloud Migration", "PEO EIS", OpportunityStage.Capture,
        48_000_000m, 15_360_000m, 40, 80, new DateOnly(2026, 11, 2), "Blake Diaz", OpportunityKind.New, CanEdit: true, HasPendingGate: false);

    private static PipelineBoardDto Board() => new(OpportunityStages.Open.Select(s =>
    {
        var cards = s == Card.Stage ? new List<PipelineCardDto> { Card } : [];
        return new PipelineLaneDto(s, cards, cards.Sum(c => c.EstimatedValue), cards.Sum(c => c.WeightedValue), s == OpportunityStage.Capture ? "Pursuit decision (gate 1)" : null);
    }).ToList());

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IPipelineService>(new FakePipeline());
        ctx.Services.AddSingleton<IOpportunityService>(new FakeOpportunities());
        ctx.Services.AddSingleton<ICrmAccountService>(new FakeAccounts());
        ctx.Services.AddSingleton<ICurrentUserContext>(new FakeUser());
        ctx.Render<MudBlazor.MudPopoverProvider>();
        return ctx;
    }

    [Fact]
    public async Task The_board_shows_a_lane_per_stage_with_totals_and_a_named_move_menu()
    {
        await using var ctx = NewContext();
        var page = ctx.Render<PipelinePage>();

        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid=pipeline-card]")));
        Assert.Equal(5, page.FindAll(".board-lane").Count);
        var totals = System.Text.RegularExpressions.Regex.Replace(page.Find("[data-testid=pipeline-totals]").TextContent, @"\s+", " ");
        Assert.Contains("1 open opportunities · $48,000,000 total · $15,360,000 weighted", totals);
        Assert.NotNull(page.Find("[aria-label='Move OPP-0005 Army Enterprise Cloud Migration to another stage']"));
        Assert.Contains("Gate: Pursuit decision (gate 1)", page.Markup);
        A11yAssert.Check(page);
    }

    [Fact]
    public async Task The_list_filters_and_totals_the_visible_rows()
    {
        await using var ctx = NewContext();
        var page = ctx.Render<OpportunityListPage>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=opportunity-table]")));
        Assert.Contains("OPP-0005", page.Markup);
        Assert.DoesNotContain("OPP-0009", page.Markup); // Lost: not an open pursuit.
        Assert.Equal("Opportunities", page.Find("[data-testid=opportunity-table] table").GetAttribute("aria-label"));
    }

    private sealed class FakePipeline : IPipelineService
    {
        public Task<PipelineBoardDto> GetBoardAsync(bool includeOutcomes = false, CancellationToken cancellationToken = default) => Task.FromResult(Board());
        public Task<ForecastDto> GetForecastAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PipelineDemandDto> GetStaffingDemandAsync(DateOnly from, int weeks, decimal revenuePerHour = 150m, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BdHighlightsDto> GetHighlightsAsync(int windowDays = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WinLossDto> GetWinLossAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BpSpendDto>> GetBpSpendAsync(Guid? opportunityId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmSettingsDto> SetCompanyProfileAsync(string? profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmSettingsDto> SetBpParentAsync(Guid? wbsElementId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BpParentOptionDto>> ListBpParentOptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeOpportunities : IOpportunityService
    {
        private static OpportunityDto Dto(string number, OpportunityStage stage) => new(Guid.NewGuid(), number, $"Opportunity {number}", null, null, null, "PEO EIS", "541512",
            SetAside.SmallBusiness, null, null, OpportunityKind.New, 1_000_000m, null, null, null, null, null, null, null, null, "bd@example.com", "Blake Diaz", null, null,
            stage, DateTime.UtcNow, 50, 80, 400_000m, OpportunitySource.Manual, null, null, null, null, null, null, null, null, null, null, null, [], [], DateTime.UtcNow, DateTime.UtcNow);

        public Task<OpportunityDto> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OpportunityDto>> ListAsync(OpportunityFilter? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpportunityDto>>([Dto("OPP-0005", OpportunityStage.Capture), Dto("OPP-0009", OpportunityStage.Lost)]);

        public Task<OpportunityDto> CreateAsync(SaveOpportunityRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpportunityDto> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpportunityDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpportunityDto> ReopenAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StageChangeDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HandoffResultDto> HandoffAsync(Guid id, HandoffRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpportunityDto> EnsureBpChargeCodeAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAccounts : ICrmAccountService
    {
        public Task<IReadOnlyList<CrmAccountDto>> ListAccountsAsync(CrmAccountKind? kind = null, string? search = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmAccountDto>>([new(Guid.NewGuid(), "PEO Enterprise Information Systems", "PEO EIS", CrmAccountKind.Office, null, null, null, null, null, null, null, null, null, null, true, 0, 1)]);

        public Task<CrmAccountDto> GetAccountAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmAccountDto> CreateAccountAsync(SaveCrmAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmAccountDto> UpdateAccountAsync(Guid id, SaveCrmAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmAccountDto> LinkCustomerAsync(Guid id, Guid? customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CrmContactDto>> ListContactsAsync(Guid? accountId = null, string? search = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmContactDto> CreateContactAsync(SaveCrmContactRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmContactDto> UpdateContactAsync(Guid id, SaveCrmContactRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CrmActivityDto>> ListActivitiesAsync(CrmActivityFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmActivityDto> LogActivityAsync(SaveCrmActivityRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CrmActivityDto> SetTaskDoneAsync(Guid activityId, bool done, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeUser : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserInfo("bd@example.com", "Blake Diaz", OrgRoles.BusinessDevelopment, null));
    }
}
