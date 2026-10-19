using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Time;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Navigation;
using Workbench.Ui.Assistant;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Assistant;

public sealed class AssistantComponentTests
{
    private static readonly AssistantStatusDto On = new(true, true, null, "Built-in test responses", true, AssistantNotice.Text);

    [Fact]
    public async Task The_panel_streams_the_answer_with_sources_and_a_copy_button()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.ProjectManager));
        var assistant = new ScriptedAssistant();
        ctx.Services.AddSingleton<IAssistantService>(assistant);
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var conversation = new AssistantConversation();
        var parameters = new DialogParameters<AssistantPanel>
        {
            { p => p.Conversation, conversation },
            { p => p.PagePath, "projects/0193c0de-0000-7000-8000-000000000001/financials" },
            { p => p.Status, On },
        };
        await provider.InvokeAsync(() => dialogs.ShowAsync<AssistantPanel>("Assistant", parameters));

        provider.WaitForAssertion(() => Assert.Contains("Explain this project's variance", provider.Find("[data-testid=assistant-suggestions]").TextContent));
        provider.Find("input[data-testid=assistant-input], [data-testid=assistant-input] input").Input("Why is project 1001 over budget?");
        provider.Find("[data-testid=assistant-send]").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.Contains("EAC $120,000", provider.Find("[data-testid=assistant-answer]").TextContent);
            Assert.NotEmpty(provider.FindAll("[data-testid=assistant-copy]"));
        });
        Assert.Equal("Why is project 1001 over budget?", assistant.Asked!.Question);
        Assert.Equal("projects/0193c0de-0000-7000-8000-000000000001/financials", assistant.Asked.PagePath);
        var source = provider.Find("[data-testid=assistant-source]");
        Assert.Equal("projects/x/financials", source.GetAttribute("href"));
        Assert.NotNull(provider.Find("[data-testid=assistant-copy]"));
        Assert.Equal("false", provider.Find("[data-testid=assistant-answer]").GetAttribute("aria-busy"));
        Assert.NotNull(conversation.Id);
        A11yAssert.Check(provider);
    }

    [Fact]
    public async Task A_refusal_shows_the_reason_without_calling_the_service()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        var assistant = new ScriptedAssistant();
        ctx.Services.AddSingleton<IAssistantService>(assistant);
        var provider = ctx.Render<MudDialogProvider>();
        var parameters = new DialogParameters<AssistantPanel>
        {
            { p => p.Conversation, new AssistantConversation() },
            { p => p.Status, On with { CanAsk = false, Reason = "You've used your assistant allowance for this month." } },
            { p => p.InitialQuestion, "What's waiting on my approval?" },
        };
        await provider.InvokeAsync(() => ctx.Services.GetRequiredService<IDialogService>().ShowAsync<AssistantPanel>("Assistant", parameters));

        provider.WaitForAssertion(() => Assert.Contains("allowance", provider.Find("[data-testid=assistant-error]").TextContent));
        Assert.Null(assistant.Asked);
    }

    [Fact]
    public async Task The_palette_offers_ask_when_the_assistant_is_on()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        ctx.Services.AddSingleton<IAssistantService>(new ScriptedAssistant());
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<CommandPaletteDialog> { { d => d.Access, new NavAccess(new CurrentUserInfo("a@example.com", "A", OrgRoles.Employee, null)) } };
        IDialogReference? dialog = null;
        await provider.InvokeAsync(async () => dialog = await dialogs.ShowAsync<CommandPaletteDialog>("Search", parameters));

        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll("[data-testid='palette-result']")));
        provider.Find("input").Input("who hasn't submitted their timesheet?");
        provider.WaitForAssertion(() => Assert.StartsWith("Ask the assistant", provider.FindAll("[data-testid='palette-result']")[0].TextContent.Trim()));
        provider.Find("input").KeyDown("Enter");

        var result = await dialog!.Result;
        var item = Assert.IsType<PaletteItem>(result!.Data);
        Assert.Equal(PaletteKind.Ask, item.Kind);
        Assert.Equal("who hasn't submitted their timesheet?", item.Keywords);
        Assert.DoesNotContain("time", ctx.Services.GetRequiredService<NavigationManager>().Uri.Replace("http://localhost/", ""));
    }

    [Fact]
    public async Task The_flags_dialog_lists_each_flag_accessibly()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Finance));
        ctx.Services.AddSingleton<IAssistantService>(new ScriptedAssistant());
        ctx.Services.AddSingleton<ITimesheetFlagService>(new NoFlagService());
        var provider = ctx.Render<MudDialogProvider>();
        var flags = new TimesheetFlagsDto(Guid.NewGuid(), Guid.NewGuid(), "Alex Rivera", new(2026, 10, 19), new(2026, 10, 25), TimesheetState.Submitted, 45, "time/history/x",
        [
            new(TimesheetFlagRules.Weekend, "Weekend hours", TimesheetFlagSeverity.Info, "5 h on Sat Oct 24 (a weekend).", [new("Weekend hours", "5 h")], [new(2026, 10, 24)], "a"),
            new(TimesheetFlagRules.Holiday, "Hours on a holiday", TimesheetFlagSeverity.Warning, "8 h charged on Company day.", [], [], "b"),
        ]);
        await provider.InvokeAsync(() => ctx.Services.GetRequiredService<IDialogService>().ShowAsync<TimesheetFlagsDialog>("Review flags",
            new DialogParameters<TimesheetFlagsDialog> { { d => d.Flags, flags } }));

        provider.WaitForAssertion(() => Assert.Equal(2, provider.FindAll("[data-testid=timesheet-flag]").Count));
        Assert.Contains("Look closer", provider.Markup);
        Assert.Equal(2, provider.FindAll("[data-testid=flag-explain]").Count);
        A11yAssert.Check(provider);
    }

    [Fact]
    public void Suggestions_fit_the_page()
    {
        Assert.Contains("Explain this project's variance", AssistantSuggestions.For("projects/0193c0de-0000-7000-8000-000000000001"));
        Assert.Contains("Show unallowable costs on this project", AssistantSuggestions.For("/projects/0193c0de-0000-7000-8000-000000000001/costs?x=1"));
        Assert.Contains("Who hasn't submitted their timesheet?", AssistantSuggestions.For("time/approvals"));
        Assert.Contains("Which CLINs will run out of funding this quarter?", AssistantSuggestions.For(""));
    }

    private sealed class ScriptedAssistant : IAssistantService
    {
        public AskAssistantRequest? Asked { get; private set; }

        public Task<AssistantStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(On);

        public async IAsyncEnumerable<AssistantEvent> AskAsync(AskAssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Asked = request;
            var conversation = Guid.NewGuid();
            var interaction = Guid.NewGuid();
            yield return new AssistantEvent(AssistantEventKind.Started, null, conversation, interaction);
            await Task.Yield();
            yield return new AssistantEvent(AssistantEventKind.Tool, "Reading project financials");
            yield return new AssistantEvent(AssistantEventKind.Text, "EAC $120,000 ");
            yield return new AssistantEvent(AssistantEventKind.Text, "vs budget $100,000.");
            yield return new AssistantEvent(AssistantEventKind.Completed, null, conversation, interaction, new AssistantAnswerDto(interaction, conversation,
                "EAC $120,000 vs budget $100,000.", [new AssistantCitation("1001 financials", "projects/x/financials")], ["get_project_financials"], "Answered", AssistantTokenUsage.Zero));
        }
    }

    private sealed class NoFlagService : ITimesheetFlagService
    {
        public Task<IReadOnlyList<TimesheetFlagsDto>> ListForTimesheetsAsync(IReadOnlyCollection<Guid> timesheetIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TimesheetFlagsDto>>([]);

        public Task<TimesheetFlagsDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TimesheetFlagsDto>> ReportAsync(TimesheetFlagReportQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TimesheetFlagsDto>>([]);

        public Task<TimesheetFlagExplanationDto> ExplainAsync(Guid timesheetId, string ruleKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimesheetFlagExplanationDto(timesheetId, ruleKey, "Explained.", true));
    }
}
