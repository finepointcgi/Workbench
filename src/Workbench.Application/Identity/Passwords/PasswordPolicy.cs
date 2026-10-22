namespace Workbench.Application.Identity.Passwords;

/// <summary>
/// Built-in account settings (config <c>Authentication:Passwords</c>). Defaults follow NIST SP 800-63B: a long minimum,
/// no composition rules, a check against common passwords, and lockout after repeated failures.
/// </summary>
public sealed class PasswordOptions
{
    public const string SectionName = "Authentication:Passwords";

    /// <summary>False turns built-in accounts off: only single sign-on (and the dev or demo picker) remain.</summary>
    public bool Enabled { get; set; } = true;

    public int MinimumLength { get; set; } = 12;

    public int MaximumLength { get; set; } = 128;

    /// <summary>Wrong passwords or codes in a row before the account is locked.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a forgot-password link works.</summary>
    public TimeSpan ResetLinkLifetime { get; set; } = TimeSpan.FromHours(1);

    public int RecoveryCodeCount { get; set; } = 10;
}

/// <summary>Whether a new password is acceptable: length, and not a common or guessable one. Never logs the password.</summary>
public static class PasswordPolicy
{
    /// <summary>The problem with <paramref name="password"/>, in words for the person choosing it, or null when it's fine.</summary>
    public static string? Problem(string? password, string email, PasswordOptions options)
    {
        password ??= string.Empty;
        if (password.Length < options.MinimumLength)
        {
            return $"Use at least {options.MinimumLength} characters. A few unrelated words make a long password that's easy to remember.";
        }

        if (password.Length > options.MaximumLength)
        {
            return $"Use at most {options.MaximumLength} characters.";
        }

        if (password.Distinct().Count() < 5)
        {
            return "That password repeats too few characters. Choose something less predictable.";
        }

        var lower = password.ToLowerInvariant();
        var letters = new string(lower.Where(char.IsLetter).ToArray());
        var local = email.Split('@')[0].ToLowerInvariant();
        if ((local.Length >= 4 && lower.Contains(local, StringComparison.Ordinal)) || lower.Contains(email.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return "Don't use your email address in your password.";
        }

        if (Common.Contains(lower) || letters.Length == 0 || IsCommonWordRepeated(letters) || IsSequence(lower))
        {
            return "That password is too common and would be guessed quickly. Choose something less predictable.";
        }

        return null;
    }

    /// <summary>"Password2026!", "welcomewelcome", "Qwertyuiop12": a common word (or keyboard row) with digits and symbols around it.</summary>
    private static bool IsCommonWordRepeated(string letters)
    {
        foreach (var word in CommonWords)
        {
            if (letters.Length % word.Length == 0 && letters.Length <= word.Length * 4 && string.Concat(Enumerable.Repeat(word, letters.Length / word.Length)) == letters)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs like "123456789012" or "abcdefghijkl".</summary>
    private static bool IsSequence(string value)
    {
        var steps = 0;
        for (var i = 1; i < value.Length; i++)
        {
            var d = value[i] - value[i - 1];
            if (d is 1 or -1 or 0)
            {
                steps++;
            }
        }

        return steps >= value.Length - 2;
    }

    private static readonly string[] CommonWords =
    [
        "password", "passw", "passwd", "pass", "qwerty", "qwertyuiop", "asdf", "asdfgh", "asdfghjkl", "zxcvbn", "zxcvbnm", "qazwsx", "qwer", "abc",
        "letmein", "welcome", "admin", "administrator", "iloveyou", "monkey", "dragon", "football", "baseball", "basketball", "soccer", "hockey",
        "sunshine", "princess", "trustno", "changeme", "master", "shadow", "superman", "batman", "michael", "jennifer", "jordan", "hunter", "ranger",
        "starwars", "computer", "internet", "secret", "login", "user", "guest", "default", "test", "summer", "winter", "spring", "autumn", "fall",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december", "monday", "friday",
        "workbench", "costpoint", "deltek", "unanet", "smartsheet", "company", "government", "contract", "federal", "america", "freedom", "liberty",
        "access", "letmeinnow", "whatever", "nothing", "flower", "cookie", "chocolate", "pokemon", "matrix", "mustang", "harley", "ginger", "charlie",
        "thomas", "robert", "daniel", "ashley", "jessica", "andrew", "joshua", "loveme", "lovely", "babygirl", "angel", "killer", "pepper", "maggie",
    ];

    // Frequent long passwords from public breach corpora (12+ characters, since shorter ones fail the length rule anyway).
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "123456789012", "1234567890123", "12345678901234", "123456123456", "123123123123", "111111111111", "000000000000", "112233445566",
        "qwertyuiop12", "qwertyuiop123", "1qaz2wsx3edc", "1q2w3e4r5t6y", "q1w2e3r4t5y6", "zaq12wsxcde3", "1qazxsw23edc", "qazwsxedcrfv",
        "passwordpassword", "password1234", "password12345", "password123456", "password1234!", "p@ssw0rd1234", "p@ssword1234", "passw0rd1234",
        "iloveyou1234", "iloveyouiloveyou", "letmein12345", "welcome12345", "welcome123456", "administrator", "administrator1", "adminadmin123",
        "changeme1234", "changemenow!", "trustno1trustno1", "football1234", "baseball1234", "superman1234", "starwars1234", "princess1234",
        "sunshine1234", "monkey123456", "dragon123456", "michael12345", "jennifer1234", "abcdefghijkl", "abcd12345678", "abc123abc123",
        "asdfghjkl123", "asdfasdfasdf", "zxcvbnm12345", "qwerty123456", "qwertyqwerty", "aaaaaaaaaaaa", "abcabcabcabc", "a1b2c3d4e5f6",
        "computer1234", "internet1234", "whatever1234", "nothing12345", "secret123456", "letmeinletmein", "mypassword123", "mypassword1234",
        "thisismypassword", "correcthorsebatterystaple", "ilovemyfamily", "ilovemymom123", "summer2024!!", "summer2025!!", "summer2026!!",
        "winter2025!!", "winter2026!!", "spring2026!!", "welcome2026!", "password2026", "password2025", "password2024", "workbench123",
    };
}
