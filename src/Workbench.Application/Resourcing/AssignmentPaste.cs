using System.Globalization;
using System.Text;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Resourcing;

/// <summary>One pasted row as read: person, task, hours, from, to (blank dates use the task's or project's).</summary>
public sealed record AssignmentPasteLine(int Line, string Person, string Task, decimal? Hours, DateOnly? From, DateOnly? To, IReadOnlyList<string> Errors);

/// <summary>
/// Reads rows copied from Excel (tab-separated) or typed as CSV: person, task, hours, from, to. A header row is skipped.
/// People match by email, employee number or name; tasks by WBS code (with or without the project's code in front) or
/// name, and a blank task, "project" or "whole project" is the whole project.
/// </summary>
public static class AssignmentPaste
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "M/d/yy", "MM/dd/yyyy", "MMM d, yyyy", "MMM d yyyy", "d MMM yyyy", "d-MMM-yy", "d-MMM-yyyy"];

    private static readonly string[] WholeProject = ["", "project", "whole project", "all", "(whole project)"];

    public static IReadOnlyList<AssignmentPasteLine> Parse(string text)
    {
        var result = new List<AssignmentPasteLine>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var first = true;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var fields = Split(lines[i]);
            string Field(int n) => n < fields.Count ? fields[n].Trim() : "";
            var errors = new List<string>();
            var hoursText = Field(2);
            var hours = ParseHours(hoursText);
            if (first && hours is null && hoursText.Any(char.IsLetter))
            {
                first = false;
                continue; // a header row: Person, Task, Hours, From, To
            }

            first = false;
            if (Field(0).Length == 0)
            {
                errors.Add("Name the person.");
            }

            if (hours is null)
            {
                errors.Add(hoursText.Length == 0 ? "Enter the hours." : $"\"{hoursText}\" isn't a number of hours.");
            }

            var from = ParseDate(Field(3), "start", errors);
            var to = ParseDate(Field(4), "end", errors);
            result.Add(new AssignmentPasteLine(i + 1, Field(0), Field(1), hours, from, to, errors));
        }

        return result;
    }

    public static Employee? FindPerson(IReadOnlyList<Employee> people, string text, List<string> errors)
    {
        var key = text.Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var matches = people.Where(p => string.Equals(p.Email, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.EmployeeNumber, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            // "Rivera, Alex" as well as "Alex Rivera".
            var name = key.Contains(',', StringComparison.Ordinal) ? string.Join(' ', key.Split(',', 2).Reverse().Select(s => s.Trim())) : key;
            matches = people.Where(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var active = matches.Where(p => p.TerminationDate is null).ToList();
        if (active.Count == 1 || (active.Count == 0 && matches.Count == 1))
        {
            return active.Count == 1 ? active[0] : matches[0];
        }

        errors.Add(matches.Count == 0 ? $"Nobody called \"{key}\". Use their name, email or employee number." : $"More than one person is called \"{key}\". Use their email.");
        return null;
    }

    /// <summary>The task, or null for the whole project (or when it isn't found, which adds an error).</summary>
    public static WbsElement? FindTask(IReadOnlyList<WbsElement> wbs, Project project, string text, List<string> errors)
    {
        var key = text.Trim();
        if (WholeProject.Contains(key, StringComparer.OrdinalIgnoreCase) || string.Equals(key, project.Code, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var byCode = wbs.FirstOrDefault(w => string.Equals(w.Code, key, StringComparison.OrdinalIgnoreCase))
            ?? wbs.FirstOrDefault(w => string.Equals(w.Code, $"{project.Code}.{key}", StringComparison.OrdinalIgnoreCase));
        if (byCode is not null)
        {
            return byCode;
        }

        // "1001.01.02 Build" as copied from Workbench, or just "Build".
        var named = wbs.Where(w => string.Equals(w.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals($"{w.Code} {w.Name}", key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1)
        {
            return named[0];
        }

        errors.Add(named.Count == 0 ? $"Project {project.Code} has no task \"{key}\". Use its WBS code or name, or leave it blank for the whole project."
            : $"More than one task is called \"{key}\". Use its WBS code.");
        return null;
    }

    /// <summary>The target project's task matching one on another project: the same code under this project's code, the same code, or the same name.</summary>
    public static WbsElement? MatchTask(WbsElement source, string sourceProjectCode, string targetProjectCode, IReadOnlyList<WbsElement> target)
    {
        if (source.Code.StartsWith(sourceProjectCode, StringComparison.OrdinalIgnoreCase))
        {
            var renamed = targetProjectCode + source.Code[sourceProjectCode.Length..];
            if (target.FirstOrDefault(w => string.Equals(w.Code, renamed, StringComparison.OrdinalIgnoreCase)) is { } byCode)
            {
                return byCode;
            }
        }

        if (target.FirstOrDefault(w => string.Equals(w.Code, source.Code, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            return same;
        }

        var named = target.Where(w => string.Equals(w.Name.Trim(), source.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static decimal? ParseHours(string text)
    {
        var cleaned = text.Trim().TrimEnd('h', 'H').Replace("hours", "", StringComparison.OrdinalIgnoreCase).Replace("hrs", "", StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) ? h : null;
    }

    private static DateOnly? ParseDate(string text, string which, List<string> errors)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return date;
        }

        // A date cell pasted as Excel's serial number.
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) && serial is > 30000 and < 80000)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        errors.Add($"\"{text}\" isn't a {which} date. Use a date like 2026-11-02 or 11/2/2026.");
        return null;
    }

    /// <summary>Tab-separated when the line has tabs (Excel), else comma-separated with quotes.</summary>
    private static List<string> Split(string line)
    {
        if (line.Contains('\t', StringComparison.Ordinal))
        {
            return line.Split('\t').Select(f => f.Trim().Trim('"')).ToList();
        }

        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
