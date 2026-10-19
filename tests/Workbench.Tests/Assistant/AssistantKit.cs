using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assistant;

/// <summary>
/// <see cref="SeededHost"/> + <see cref="ProjectAccountingFixture"/> with the fake provider offered (as in Development).
/// pm@ manages project 1001; analyst@ (an Employee) charges to it; admin@ administers.
/// </summary>
internal sealed class AssistantKit : IAsyncDisposable
{
    private AssistantKit(SeededHost seeded, ProjectAccountingFixture data)
    {
        Seeded = seeded;
        Data = data;
    }

    public SeededHost Seeded { get; }

    public ProjectAccountingFixture Data { get; }

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public static async Task<AssistantKit> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var seeded = await SeededHost.CreateAsync(services =>
        {
            services.Configure<AssistantOptions>(o => o.EnableFakeProvider = true);
            configure?.Invoke(services);
        });
        var data = await ProjectAccountingFixture.SeedAsync(seeded);
        return new AssistantKit(seeded, data);
    }

    public AssistantKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    /// <summary>Turns the assistant on with the fake connection (as admin), then switches back to <paramref name="then"/>.</summary>
    public async Task EnableAsync(string then = Owner, long? perUserLimit = null, int? dailyLimit = null)
    {
        As(Admin);
        await Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null,
            MonthlyTokenLimitPerUser: perUserLimit, DailyQuestionLimitPerUser: dailyLimit, AcceptNotice: true));
        As(then);
    }

    public async Task<(IReadOnlyList<AssistantEvent> Events, AssistantAnswerDto? Answer, string? Failure)> AskAsync(string question, string? pagePath = null, Guid? conversationId = null)
    {
        var events = new List<AssistantEvent>();
        await foreach (var e in Get<IAssistantService>().AskAsync(new AskAssistantRequest(question, conversationId, pagePath)))
        {
            events.Add(e);
        }

        return (events, events.LastOrDefault(e => e.Kind == AssistantEventKind.Completed)?.Answer, events.LastOrDefault(e => e.Kind == AssistantEventKind.Failed)?.Text);
    }

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}
