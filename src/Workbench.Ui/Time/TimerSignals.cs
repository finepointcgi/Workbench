namespace Workbench.Ui.Time;

/// <summary>
/// Tells every open page of a person (other tabs and devices served by this server) that their timer started or stopped,
/// so the app bar and timer cards refresh at once. Pages on other servers catch up on their next poll.
/// </summary>
public static class TimerSignals
{
    public static event Action<string>? Changed;

    public static void Raise(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            Changed?.Invoke(email.Trim().ToLowerInvariant());
        }
    }

    public static bool IsFor(string changed, string? email) =>
        string.Equals(changed, email?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Elapsed time as h:mm:ss (or h:mm without seconds).</summary>
    public static string Elapsed(DateTime startedAtUtc, DateTime nowUtc, bool seconds = true)
    {
        var span = nowUtc > startedAtUtc ? nowUtc - startedAtUtc : TimeSpan.Zero;
        var hours = (int)span.TotalHours;
        return seconds ? $"{hours}:{span.Minutes:00}:{span.Seconds:00}" : $"{hours}:{span.Minutes:00}";
    }
}
