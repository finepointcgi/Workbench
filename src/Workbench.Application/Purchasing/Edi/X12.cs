using System.Globalization;
using System.Text;

namespace Workbench.Application.Purchasing.Edi;

/// <summary>A file that isn't valid X12 (envelope, counts or control numbers).</summary>
public sealed class X12FormatException(string message) : Exception(message);

/// <summary>One segment: its id and elements (element 1 is <c>Elements[1]</c>; <c>Elements[0]</c> is the id).</summary>
public sealed class X12Segment(IReadOnlyList<string> elements)
{
    public IReadOnlyList<string> Elements { get; } = elements;

    public string Id => Elements[0];

    /// <summary>Element <paramref name="index"/> (1-based, as in the X12 references: BEG03 is <c>this[3]</c>), or "" when absent.</summary>
    public string this[int index] => index < Elements.Count ? Elements[index] : "";

    public static X12Segment Of(params string?[] elements)
    {
        // Trailing empty elements are dropped, as the standard asks.
        var list = elements.Select(e => e ?? "").ToList();
        while (list.Count > 1 && list[^1].Length == 0)
        {
            list.RemoveAt(list.Count - 1);
        }

        return new X12Segment(list);
    }
}

/// <summary>A transaction set (ST … SE) within a functional group.</summary>
public sealed class X12TransactionSet(string id, string controlNumber, IReadOnlyList<X12Segment> segments)
{
    /// <summary>"850", "855", "856", "810".</summary>
    public string Id { get; } = id;

    public string ControlNumber { get; } = controlNumber;

    /// <summary>The segments between ST and SE.</summary>
    public IReadOnlyList<X12Segment> Segments { get; } = segments;

    public X12Segment? First(string id) => Segments.FirstOrDefault(s => s.Id == id);
}

public sealed class X12FunctionalGroup(string functionalId, string senderCode, string receiverCode, string controlNumber, IReadOnlyList<X12TransactionSet> sets)
{
    public string FunctionalId { get; } = functionalId;
    public string SenderCode { get; } = senderCode;
    public string ReceiverCode { get; } = receiverCode;
    public string ControlNumber { get; } = controlNumber;
    public IReadOnlyList<X12TransactionSet> Sets { get; } = sets;
}

/// <summary>One interchange (ISA … IEA): the envelope fields Workbench uses and its groups.</summary>
public sealed class X12Interchange
{
    public required string SenderQualifier { get; init; }
    public required string SenderId { get; init; }
    public required string ReceiverQualifier { get; init; }
    public required string ReceiverId { get; init; }
    public required string ControlNumber { get; init; }
    public required DateTime Timestamp { get; init; }

    /// <summary>"P" production or "T" test.</summary>
    public string UsageIndicator { get; init; } = "P";

    public required IReadOnlyList<X12FunctionalGroup> Groups { get; init; }

    public IEnumerable<X12TransactionSet> Sets => Groups.SelectMany(g => g.Sets);
}

/// <summary>
/// Reads and writes ANSI X12 (version 004010). Delimiters come from the ISA segment: the element separator is the
/// character after "ISA", the component separator ISA16 and the segment terminator the character after it. Line breaks
/// between segments are ignored. Envelope counts and control numbers are checked (ISA13 = IEA02, GS06 = GE02, ST02 = SE02,
/// SE01 = the segment count, GE01 and IEA01 the set and group counts).
/// </summary>
public static class X12
{
    public const string Version = "00401";
    public const string GroupVersion = "004010";

    public static X12Interchange Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new X12FormatException("The file is empty.");
        }

        text = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!text.StartsWith("ISA", StringComparison.Ordinal) || text.Length < 106)
        {
            throw new X12FormatException("The file doesn't start with an ISA interchange header.");
        }

        var element = text[3];
        var component = text[104];
        var terminator = text[105];
        if (element == terminator || char.IsLetterOrDigit(element) || char.IsLetterOrDigit(terminator))
        {
            throw new X12FormatException("The ISA header's delimiters can't be read (it must be exactly 106 characters).");
        }

        var segments = text.Split(terminator)
            .Select(s => s.Trim('\r', '\n', ' ', '\t'))
            .Where(s => s.Length > 0)
            .Select(s => new X12Segment(s.Split(element)))
            .ToList();
        _ = component;

        var isa = segments[0];
        if (isa.Elements.Count != 17)
        {
            throw new X12FormatException($"The ISA header has {isa.Elements.Count - 1} elements; it needs 16.");
        }

        var groups = new List<X12FunctionalGroup>();
        var i = 1;
        while (i < segments.Count && segments[i].Id == "GS")
        {
            var gs = segments[i++];
            var sets = new List<X12TransactionSet>();
            while (i < segments.Count && segments[i].Id == "ST")
            {
                var st = segments[i];
                var start = i++;
                var body = new List<X12Segment>();
                while (i < segments.Count && segments[i].Id != "SE")
                {
                    if (segments[i].Id is "ST" or "GE" or "IEA")
                    {
                        throw new X12FormatException($"Transaction set {st[2]} has no SE trailer.");
                    }

                    body.Add(segments[i++]);
                }

                if (i >= segments.Count)
                {
                    throw new X12FormatException($"Transaction set {st[2]} has no SE trailer.");
                }

                var se = segments[i++];
                if (se[2] != st[2])
                {
                    throw new X12FormatException($"Transaction set control numbers don't match (ST02 {st[2]}, SE02 {se[2]}).");
                }

                if (!int.TryParse(se[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != i - start)
                {
                    throw new X12FormatException($"Transaction set {st[2]}: SE01 says {se[1]} segments, but there are {i - start}.");
                }

                sets.Add(new X12TransactionSet(st[1], st[2], body));
            }

            if (i >= segments.Count || segments[i].Id != "GE")
            {
                throw new X12FormatException($"Functional group {gs[6]} has no GE trailer.");
            }

            var ge = segments[i++];
            if (ge[2] != gs[6])
            {
                throw new X12FormatException($"Functional group control numbers don't match (GS06 {gs[6]}, GE02 {ge[2]}).");
            }

            if (ge[1] != sets.Count.ToString(CultureInfo.InvariantCulture))
            {
                throw new X12FormatException($"Functional group {gs[6]}: GE01 says {ge[1]} transaction sets, but there are {sets.Count}.");
            }

            groups.Add(new X12FunctionalGroup(gs[1], gs[2].Trim(), gs[3].Trim(), gs[6], sets));
        }

        if (i >= segments.Count || segments[i].Id != "IEA")
        {
            throw new X12FormatException(i < segments.Count ? $"Unexpected {segments[i].Id} segment; expected GS or IEA." : "The interchange has no IEA trailer.");
        }

        var iea = segments[i];
        if (iea[2] != isa[13])
        {
            throw new X12FormatException($"Interchange control numbers don't match (ISA13 {isa[13]}, IEA02 {iea[2]}).");
        }

        if (iea[1] != groups.Count.ToString(CultureInfo.InvariantCulture))
        {
            throw new X12FormatException($"IEA01 says {iea[1]} functional groups, but there are {groups.Count}.");
        }

        return new X12Interchange
        {
            SenderQualifier = isa[5].Trim(),
            SenderId = isa[6].Trim(),
            ReceiverQualifier = isa[7].Trim(),
            ReceiverId = isa[8].Trim(),
            ControlNumber = isa[13].Trim(),
            UsageIndicator = isa[15].Trim(),
            Timestamp = DateTime.TryParseExact(isa[9] + isa[10], "yyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
                ? ts : DateTime.MinValue,
            Groups = groups,
        };
    }

    /// <summary>Writes one interchange: "*" elements, ":" components, "~" plus a line break after each segment.</summary>
    public static string Write(X12Interchange interchange)
    {
        var sb = new StringBuilder();
        void Seg(X12Segment s) => sb.Append(string.Join('*', s.Elements.Select(Clean))).Append("~\n");

        sb.Append(string.Join('*',
            "ISA", "00", Pad("", 10), "00", Pad("", 10),
            Pad(interchange.SenderQualifier, 2), Pad(interchange.SenderId, 15),
            Pad(interchange.ReceiverQualifier, 2), Pad(interchange.ReceiverId, 15),
            interchange.Timestamp.ToString("yyMMdd", CultureInfo.InvariantCulture), interchange.Timestamp.ToString("HHmm", CultureInfo.InvariantCulture),
            "U", Version, interchange.ControlNumber.PadLeft(9, '0'), "0", interchange.UsageIndicator, ":")).Append("~\n");
        foreach (var g in interchange.Groups)
        {
            Seg(X12Segment.Of("GS", g.FunctionalId, g.SenderCode, g.ReceiverCode,
                interchange.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture), interchange.Timestamp.ToString("HHmm", CultureInfo.InvariantCulture),
                g.ControlNumber, "X", GroupVersion));
            foreach (var set in g.Sets)
            {
                Seg(X12Segment.Of("ST", set.Id, set.ControlNumber));
                foreach (var s in set.Segments)
                {
                    Seg(s);
                }

                Seg(X12Segment.Of("SE", (set.Segments.Count + 2).ToString(CultureInfo.InvariantCulture), set.ControlNumber));
            }

            Seg(X12Segment.Of("GE", g.Sets.Count.ToString(CultureInfo.InvariantCulture), g.ControlNumber));
        }

        Seg(X12Segment.Of("IEA", interchange.Groups.Count.ToString(CultureInfo.InvariantCulture), interchange.ControlNumber.PadLeft(9, '0')));
        return sb.ToString();
    }

    /// <summary>An interchange holding just <paramref name="set"/> (kept in the EDI log so one set can be retried alone).</summary>
    public static string Single(X12Interchange source, X12FunctionalGroup group, X12TransactionSet set) => Write(new X12Interchange
    {
        SenderQualifier = source.SenderQualifier,
        SenderId = source.SenderId,
        ReceiverQualifier = source.ReceiverQualifier,
        ReceiverId = source.ReceiverId,
        ControlNumber = source.ControlNumber,
        Timestamp = source.Timestamp == DateTime.MinValue ? DateTime.UtcNow : source.Timestamp,
        UsageIndicator = source.UsageIndicator,
        Groups = [new X12FunctionalGroup(group.FunctionalId, group.SenderCode, group.ReceiverCode, group.ControlNumber, [set])],
    });

    // Delimiters can't appear in data: replace them so a description with "*" can't break the file.
    private static string Clean(string value) => value.Replace('*', ' ').Replace('~', ' ').Replace(':', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string Pad(string value, int width) => Clean(value).PadRight(width)[..width];

    public static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : DateOnly.TryParseExact(value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s
        : null;

    public static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;
}
