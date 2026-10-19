using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Support;
using Workbench.Domain.Support;

namespace Workbench.Application.Support;

/// <summary>
/// Remembers, per user on the server, which welcome tours are done and the last "What's new" entry seen. Everything
/// is about the signed-in user only; nobody can read or change another person's state.
/// </summary>
public sealed class HelpStateService(IWorkbenchDbContextFactory dbFactory, ICurrentUser currentUser, TimeProvider clock) : IHelpStateService
{
    private static readonly HelpStateDto Empty = new([], null);

    public async Task<HelpStateDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        if (me is null)
        {
            return Empty;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await db.Set<UserHelpState>().AsNoTracking().FirstOrDefaultAsync(s => s.UserEmail == me, cancellationToken);
        return state is null ? Empty : ToDto(state);
    }

    public Task<HelpStateDto> CompleteTourAsync(string tourKey, CancellationToken cancellationToken = default)
    {
        var key = CleanKey(tourKey, nameof(tourKey));
        return ChangeAsync(state =>
        {
            var tours = Split(state.CompletedTours);
            if (!tours.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                tours.Add(key);
            }

            state.CompletedTours = string.Join(',', tours);
        }, cancellationToken);
    }

    public Task<HelpStateDto> RestartToursAsync(CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state.CompletedTours = "", cancellationToken);

    public Task<HelpStateDto> MarkWhatsNewSeenAsync(string version, CancellationToken cancellationToken = default)
    {
        var value = (version ?? "").Trim();
        if (value.Length == 0 || value.Length > 32)
        {
            throw new ArgumentException("A version is 1 to 32 characters.", nameof(version));
        }

        return ChangeAsync(state => state.WhatsNewSeen = value, cancellationToken);
    }

    private async Task<HelpStateDto> ChangeAsync(Action<UserHelpState> change, CancellationToken cancellationToken)
    {
        var me = Me() ?? throw new UnauthorizedAccessException("Sign in first.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var state = await db.Set<UserHelpState>().FirstOrDefaultAsync(s => s.UserEmail == me, cancellationToken);
        if (state is null)
        {
            state = new UserHelpState { UserEmail = me };
            db.Set<UserHelpState>().Add(state);
        }

        change(state);
        state.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(state);
    }

    private string? Me() => currentUser.Email is { Length: > 0 } email ? email.Trim().ToLowerInvariant() : null;

    private static string CleanKey(string? key, string name)
    {
        var value = (key ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0 || value.Length > SupportRules.TourKeyMaxLength || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("A tour key is a short word of letters, digits and hyphens.", name);
        }

        return value;
    }

    private static List<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static HelpStateDto ToDto(UserHelpState s) => new(Split(s.CompletedTours), s.WhatsNewSeen);
}
