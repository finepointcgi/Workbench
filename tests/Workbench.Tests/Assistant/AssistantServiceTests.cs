using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Assistant;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Security;
using Workbench.Domain.Assistant;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assistant;

public sealed class AssistantServiceTests
{
    [Fact]
    public async Task The_assistant_is_off_by_default_and_refusals_are_logged()
    {
        await using var kit = await AssistantKit.CreateAsync();
        kit.As(Owner);

        var status = await kit.Get<IAssistantService>().GetStatusAsync();
        Assert.False(status.IsEnabled);
        Assert.False(status.CanAsk);

        var (_, answer, failure) = await kit.AskAsync("Why is project 1001 over budget?");
        Assert.Null(answer);
        Assert.Contains("turned off", failure);

        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<AssistantInteraction>().SingleAsync();
        Assert.Equal(AssistantOutcome.Disabled, row.Outcome);
        Assert.Equal(Owner, row.UserEmail);
        Assert.Empty(await db.Set<AssistantToolCall>().ToListAsync());
    }

    [Fact]
    public async Task Only_admins_turn_it_on_and_only_after_accepting_the_notice()
    {
        await using var kit = await AssistantKit.CreateAsync();
        var admin = kit.Get<IAssistantAdminService>();

        kit.As(Owner);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => admin.SaveSettingsAsync(new SaveAssistantSettingsRequest(true, "fake", null, AcceptNotice: true)));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => admin.GetSettingsAsync());

        kit.As(Admin);
        var off = await admin.GetSettingsAsync();
        Assert.False(off.IsEnabled);
        Assert.Contains(off.Connections, c => c.Name == "fake");
        await Assert.ThrowsAsync<ArgumentException>(() => admin.SaveSettingsAsync(new SaveAssistantSettingsRequest(true, "fake", null)));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.SaveSettingsAsync(new SaveAssistantSettingsRequest(true, "not-configured", null, AcceptNotice: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.SaveSettingsAsync(new SaveAssistantSettingsRequest(true, "fake", null, MonthlyTokenLimit: 0, AcceptNotice: true)));

        var on = await admin.SaveSettingsAsync(new SaveAssistantSettingsRequest(true, "fake", null, AcceptNotice: true));
        Assert.True(on.IsEnabled);
        Assert.True(on.NoticeIsCurrent);
        Assert.Equal(Admin, on.NoticeAcceptedBy);

        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Contains(await db.Set<SecurityEvent>().ToListAsync(), e => e.EventType == SecurityEventTypes.AssistantSettingsChanged && e.Detail!.Contains("notice"));

        kit.As(Owner);
        Assert.True((await kit.Get<IAssistantService>().GetStatusAsync()).CanAsk);
    }

    [Fact]
    public async Task A_pm_gets_an_answer_with_the_numbers_and_links_to_the_pages()
    {
        await using var kit = await AssistantKit.CreateAsync();
        await kit.EnableAsync();

        var (events, answer, failure) = await kit.AskAsync("Why is project 1001 over budget?");

        Assert.Null(failure);
        Assert.NotNull(answer);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.Text);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.Tool && e.Text == "Reading project financials");
        Assert.Contains("Project 1001: budget", answer!.Answer);
        Assert.Contains("actual cost $", answer.Answer);
        Assert.Contains(answer.Citations, c => c.Href == $"projects/{kit.Data.Project1}/financials");
        Assert.All(answer.Citations, c => Assert.False(c.Href.Contains("//") || c.Href.StartsWith('/')));
        Assert.Contains("get_project_financials", answer.ToolsUsed);
        Assert.True(answer.Usage.Total > 0);
    }

    [Fact]
    public async Task An_employee_asking_about_project_financials_is_refused_through_the_tool()
    {
        await using var kit = await AssistantKit.CreateAsync();
        await kit.EnableAsync(then: Editor);

        var (_, answer, _) = await kit.AskAsync("Why is project 1001 over budget?");

        Assert.NotNull(answer);
        Assert.Contains("Access denied", answer!.Answer);
        Assert.DoesNotContain("budget $", answer.Answer);
        Assert.Empty(answer.Citations);

        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var calls = await db.Set<AssistantToolCall>().Where(c => c.ToolName == "get_project_financials").ToListAsync();
        var call = Assert.Single(calls);
        Assert.Equal(AssistantToolOutcome.Denied, call.Outcome);
        Assert.True(string.IsNullOrEmpty(call.RecordsRead));
    }

    [Fact]
    public async Task Every_question_tool_call_and_record_read_is_in_the_audit_log_for_admins()
    {
        await using var kit = await AssistantKit.CreateAsync();
        await kit.EnableAsync();
        var (_, answer, _) = await kit.AskAsync("Why is project 1001 over budget?", pagePath: $"/projects/{kit.Data.Project1}/financials?tab=x");

        kit.As(Owner);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.Get<IAssistantAdminService>().ListAuditAsync(new AssistantAuditQuery()));

        kit.As(Admin);
        var audit = await kit.Get<IAssistantAdminService>().ListAuditAsync(new AssistantAuditQuery(UserEmail: Owner));
        var row = Assert.Single(audit);
        Assert.Equal(answer!.InteractionId, row.Id);
        Assert.Equal("Question", row.Kind);
        Assert.Equal("Answered", row.Outcome);
        Assert.Equal($"projects/{kit.Data.Project1}/financials", row.PagePath);
        Assert.Equal("Why is project 1001 over budget?", row.Prompt);
        Assert.Equal(answer.Answer, row.Answer);
        Assert.Equal("Fake", row.Provider);
        var financials = Assert.Single(row.ToolCalls, c => c.ToolName == "get_project_financials");
        Assert.Equal("Ok", financials.Outcome);
        Assert.Contains($"Project:{kit.Data.Project1}", financials.RecordsRead);
        Assert.Contains("\"project\":\"1001\"", financials.Arguments);

        var usage = await kit.Get<IAssistantAdminService>().GetUsageAsync();
        var mine = Assert.Single(usage.Users);
        Assert.Equal(Owner, mine.UserEmail);
        Assert.Equal(1, mine.Questions);
        Assert.Equal(answer.Usage.Total, mine.Total);
    }

    [Fact]
    public async Task Usage_limits_stop_further_questions()
    {
        await using var kit = await AssistantKit.CreateAsync();
        await kit.EnableAsync(perUserLimit: 1);

        var first = await kit.AskAsync("What's waiting on my approval?");
        Assert.NotNull(first.Answer);

        var second = await kit.AskAsync("What's waiting on my approval?");
        Assert.Null(second.Answer);
        Assert.Contains("allowance", second.Failure);

        await kit.EnableAsync(dailyLimit: 1, then: Editor);
        Assert.NotNull((await kit.AskAsync("What's waiting on my approval?")).Answer);
        Assert.Contains("daily limit", (await kit.AskAsync("What's waiting on my approval?")).Failure);
    }

    [Fact]
    public async Task Follow_up_questions_in_a_conversation_send_the_earlier_turns()
    {
        var recorder = new RecordingModelFactory();
        await using var kit = await AssistantKit.CreateAsync(s => s.AddSingleton<IAssistantModelClientFactory>(recorder));
        await kit.EnableAsync();

        var first = await kit.AskAsync("What's waiting on my approval?");
        await kit.AskAsync("And yesterday?", conversationId: first.Answer!.ConversationId);

        var last = recorder.Requests.Last();
        Assert.Contains(last.Messages, m => m.Role == ModelRole.Assistant && m.Content.OfType<ModelText>().Any(t => t.Text == first.Answer.Answer));
        Assert.Contains("What's waiting on my approval?", string.Concat(last.Messages[0].Content.OfType<ModelText>().Select(t => t.Text)));
    }

    [Fact]
    public async Task The_system_prompt_and_tools_are_a_stable_cacheable_prefix()
    {
        var recorder = new RecordingModelFactory();
        await using var kit = await AssistantKit.CreateAsync(s => s.AddSingleton<IAssistantModelClientFactory>(recorder));
        await kit.EnableAsync();
        await kit.AskAsync("Why is project 1001 over budget?");
        kit.As(Editor);
        await kit.AskAsync("Who hasn't submitted their timesheet?");

        Assert.True(recorder.Requests.Count >= 4);
        Assert.Single(recorder.Requests.Select(r => r.System).Distinct());
        Assert.Single(recorder.Requests.Select(r => string.Join(",", r.Tools.Select(t => t.Name + t.InputSchema.ToJsonString()))).Distinct());
        Assert.DoesNotContain(Owner, recorder.Requests[0].System);
        Assert.DoesNotContain("2026", recorder.Requests[0].System);
    }

    [Fact]
    public async Task Clean_path_keeps_only_an_app_relative_path()
    {
        Assert.Equal("projects/x/financials", AssistantService.CleanPath("/projects/x/financials?tab=1#top"));
        Assert.Null(AssistantService.CleanPath("https://evil.example/x"));
        Assert.Null(AssistantService.CleanPath("//evil.example/x"));
        Assert.Null(AssistantService.CleanPath("  "));
        await Task.CompletedTask;
    }

    /// <summary>The fake provider, recording every request it gets.</summary>
    internal sealed class RecordingModelFactory : IAssistantModelClientFactory
    {
        public List<ModelRequest> Requests { get; } = [];

        public IAssistantModelClient Create(string connectionName, AssistantConnectionOptions? connection) => new Client(this);

        private sealed class Client(RecordingModelFactory owner) : IAssistantModelClient
        {
            public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            {
                lock (owner.Requests)
                {
                    owner.Requests.Add(request);
                }

                return new FakeAssistantModel().StreamAsync(request, cancellationToken);
            }
        }
    }
}
