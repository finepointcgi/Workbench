using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Collaboration;

internal static partial class CollaborationFormat
{
    /// <summary>Distinct emails found in the sheet's Person columns, for @mention suggestions.</summary>
    public static IReadOnlyList<string> People(SheetDetail sheet, IEnumerable<ItemDto> items)
    {
        var personColumns = sheet.Columns.Where(c => c.Type == ColumnKind.Person).Select(c => c.Id.ToString()).ToList();
        return items
            .SelectMany(i => personColumns.Select(key => i.Values.GetValueOrDefault(key)))
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Contains('@'))
            .Select(v => v!.Trim().ToLowerInvariant())
            .Distinct()
            .Order()
            .ToList();
    }

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        return span.TotalSeconds switch
        {
            < 45 => "just now",
            < 90 => "a minute ago",
            _ when span.TotalMinutes < 60 => $"{(int)span.TotalMinutes} minutes ago",
            _ when span.TotalHours < 2 => "an hour ago",
            _ when span.TotalHours < 24 => $"{(int)span.TotalHours} hours ago",
            _ when span.TotalDays < 7 => $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago",
            _ => utc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture),
        };
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string Initials(string email)
    {
        var name = email.Split('@')[0];
        var parts = name.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name[..Math.Min(2, name.Length)]).ToUpperInvariant();
    }

    public static string ActivityIcon(string kind) => kind switch
    {
        ActivityKinds.ItemCreated => Icons.Material.Outlined.AddBox,
        ActivityKinds.ItemDeleted => Icons.Material.Outlined.Delete,
        ActivityKinds.ItemMoved => Icons.Material.Outlined.OpenWith,
        ActivityKinds.ItemUpdated => Icons.Material.Outlined.Edit,
        ActivityKinds.CommentAdded or ActivityKinds.CommentEdited or ActivityKinds.CommentDeleted => Icons.Material.Outlined.ChatBubbleOutline,
        ActivityKinds.AttachmentAdded or ActivityKinds.AttachmentDeleted => Icons.Material.Outlined.AttachFile,
        _ when kind.StartsWith("column.", StringComparison.Ordinal) => Icons.Material.Outlined.ViewColumn,
        _ => Icons.Material.Outlined.History,
    };

    /// <summary>Splits a comment body into plain text and <c>@email</c> mention runs.</summary>
    public static IEnumerable<(string Text, bool IsMention)> Segments(string body)
    {
        var last = 0;
        foreach (Match match in MentionPattern().Matches(body))
        {
            if (match.Index > last)
            {
                yield return (body[last..match.Index], false);
            }

            yield return (match.Value, true);
            last = match.Index + match.Length;
        }

        if (last < body.Length)
        {
            yield return (body[last..], false);
        }
    }

    [GeneratedRegex(@"(?<![\w.@])@[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex MentionPattern();
}
