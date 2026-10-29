using System.Globalization;
using System.Text;

namespace Workbench.Application.Calendar;

/// <summary>One all-day entry in a calendar feed. <see cref="Uid"/> is stable, so a calendar updates an entry instead of duplicating it.</summary>
public sealed record CalendarEntry(string Uid, DateOnly Start, DateOnly EndInclusive, string Summary, string? Description = null, string? Url = null);

/// <summary>Writes an iCalendar (RFC 5545) feed of all-day entries: text escaped, lines folded at 75 bytes, CRLF line ends.</summary>
public static class IcsWriter
{
    public const string ContentType = "text/calendar; charset=utf-8";

    public static string Write(string calendarName, IEnumerable<CalendarEntry> entries, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//Workbench//Calendar feed//EN");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, "X-WR-CALNAME:" + Escape(calendarName));
        Line(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line(sb, "X-PUBLISHED-TTL:PT1H");
        var stamp = generatedUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in entries)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, "UID:" + e.Uid);
            Line(sb, "DTSTAMP:" + stamp);
            Line(sb, "DTSTART;VALUE=DATE:" + e.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(sb, "DTEND;VALUE=DATE:" + e.EndInclusive.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)); // exclusive
            Line(sb, "SUMMARY:" + Escape(e.Summary));
            if (!string.IsNullOrWhiteSpace(e.Description))
            {
                Line(sb, "DESCRIPTION:" + Escape(e.Description));
            }

            if (e.Url is not null)
            {
                Line(sb, "URL:" + e.Url);
            }

            Line(sb, "TRANSP:TRANSPARENT"); // all-day reminders don't make you look busy
            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    public static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").ReplaceLineEndings("\\n");

    private static void Line(StringBuilder sb, string line)
    {
        // Fold at 75 octets without splitting a UTF-8 character; continuation lines start with one space.
        var bytes = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > limit)
            {
                sb.Append("\r\n ");
                bytes = 0;
                limit = 74;
            }

            sb.Append(rune.ToString());
            bytes += size;
        }

        sb.Append("\r\n");
    }
}
