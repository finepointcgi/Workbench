using Workbench.Contracts.Support;

namespace Workbench.Tests.Help;

/// <summary>An in-memory IHelpStateService for component tests.</summary>
internal sealed class FakeHelpState(params string[] completed) : IHelpStateService
{
    public List<string> Completed { get; } = [.. completed];

    public string? WhatsNewSeen { get; set; }

    public int Restarts { get; private set; }

    public Task<HelpStateDto> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Dto());

    public Task<HelpStateDto> CompleteTourAsync(string tourKey, CancellationToken cancellationToken = default)
    {
        Completed.Add(tourKey);
        return Task.FromResult(Dto());
    }

    public Task<HelpStateDto> RestartToursAsync(CancellationToken cancellationToken = default)
    {
        Restarts++;
        Completed.Clear();
        return Task.FromResult(Dto());
    }

    public Task<HelpStateDto> MarkWhatsNewSeenAsync(string version, CancellationToken cancellationToken = default)
    {
        WhatsNewSeen = version;
        return Task.FromResult(Dto());
    }

    private HelpStateDto Dto() => new([.. Completed], WhatsNewSeen);
}
