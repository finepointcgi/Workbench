using System.Globalization;
using System.Text;

namespace Workbench.Application.BankFiles;

/// <summary>Who originates the file: our bank (the ODFI) and the identification it assigned us.</summary>
public sealed record NachaOrigin(
    string ImmediateDestination,
    string ImmediateDestinationName,
    string ImmediateOrigin,
    string ImmediateOriginName,
    string CompanyIdentification,
    string CompanyName,
    string EntryDescription,
    string OriginatingDfi);

/// <summary>One credit to a vendor (CCD), with its optional CCD+ remittance addenda (80 characters at most).</summary>
public sealed record NachaCredit(string RoutingNumber, string AccountNumber, bool IsSavings, decimal Amount, string IdentificationNumber, string ReceiverName, string? Addenda);

/// <summary>The offsetting debit to our own account, for banks that want a balanced file.</summary>
public sealed record NachaOffset(string RoutingNumber, string AccountNumber, bool IsSavings);

public sealed record NachaBatch(DateOnly EffectiveDate, IReadOnlyList<NachaCredit> Credits, int BatchNumber = 1, DateOnly? DescriptiveDate = null);

/// <summary>The file and what it adds up to; <see cref="TraceNumbers"/> is one per credit, in order.</summary>
public sealed record NachaFile(string Content, IReadOnlyList<string> TraceNumbers, int EntryAddendaCount, long EntryHash, decimal TotalDebit, decimal TotalCredit, int BlockCount);

/// <summary>
/// Writes NACHA ACH files: 94-character records, blocked by 10 and padded with lines of 9s. One CCD batch (service class 220
/// credits only, or 200 with an offsetting debit) with an 05 addenda per entry that has remittance text (CCD+). The entry hash
/// is the sum of the eight-digit receiving DFI numbers, keeping its rightmost ten digits. Text is upper case ASCII.
/// </summary>
public static class NachaWriter
{
    public const int RecordLength = 94;
    public const int BlockingFactor = 10;

    /// <param name="traceStart">The first entry's seven-digit trace sequence; entries count up from it.</param>
    public static NachaFile Write(NachaOrigin origin, NachaBatch batch, DateTime createdAt, char fileIdModifier, int traceStart, NachaOffset? offset = null)
    {
        Validate(origin, batch, fileIdModifier, traceStart);
        var records = new List<string>();
        records.Add(Record(
            "1", "01",
            " " + Digits(origin.ImmediateDestination, 9, "The immediate destination"),
            Fixed(origin.ImmediateOrigin, 10, right: true),
            createdAt.ToString("yyMMdd", CultureInfo.InvariantCulture),
            createdAt.ToString("HHmm", CultureInfo.InvariantCulture),
            fileIdModifier.ToString(),
            "094", "10", "1",
            Fixed(origin.ImmediateDestinationName, 23),
            Fixed(origin.ImmediateOriginName, 23),
            Fixed("", 8)));

        var serviceClass = offset is null ? "220" : "200";
        var odfi = Digits(origin.OriginatingDfi, 8, "The originating DFI");
        var batchNumber = batch.BatchNumber.ToString("0000000", CultureInfo.InvariantCulture);
        records.Add(Record(
            "5", serviceClass,
            Fixed(origin.CompanyName, 16),
            Fixed("", 20),
            Fixed(origin.CompanyIdentification, 10),
            "CCD",
            Fixed(origin.EntryDescription, 10),
            batch.DescriptiveDate is { } d ? d.ToString("yyMMdd", CultureInfo.InvariantCulture) : Fixed("", 6),
            batch.EffectiveDate.ToString("yyMMdd", CultureInfo.InvariantCulture),
            Fixed("", 3),
            "1",
            odfi,
            batchNumber));

        var traces = new List<string>();
        long hash = 0;
        long creditCents = 0;
        long debitCents = 0;
        var entryAddenda = 0;
        var sequence = traceStart;

        void Entry(string transactionCode, string routing, string account, long cents, string id, string name, string? addenda)
        {
            var rdfi = routing[..8];
            hash += long.Parse(rdfi, CultureInfo.InvariantCulture);
            var trace = odfi + sequence.ToString("0000000", CultureInfo.InvariantCulture);
            sequence++;
            records.Add(Record(
                "6", transactionCode,
                rdfi, routing[8..],
                Fixed(account, 17),
                cents.ToString("0000000000", CultureInfo.InvariantCulture),
                Fixed(id, 15),
                Fixed(name, 22),
                Fixed("", 2),
                addenda is null ? "0" : "1",
                trace));
            entryAddenda++;
            if (addenda is not null)
            {
                records.Add(Record("7", "05", Fixed(addenda, 80), "0001", trace[8..]));
                entryAddenda++;
            }

            traces.Add(trace);
        }

        foreach (var c in batch.Credits)
        {
            var cents = Cents(c.Amount);
            creditCents += cents;
            Entry(c.IsSavings ? "32" : "22", Digits(c.RoutingNumber, 9, "A routing number"), AccountDigits(c.AccountNumber), cents, c.IdentificationNumber, c.ReceiverName, Clean(c.Addenda));
        }

        var creditTraces = traces.ToList();
        if (offset is not null)
        {
            debitCents = creditCents;
            Entry(offset.IsSavings ? "37" : "27", Digits(offset.RoutingNumber, 9, "Our routing number"), AccountDigits(offset.AccountNumber), debitCents, "OFFSET", origin.CompanyName, null);
        }

        hash %= 10_000_000_000L;
        var hashText = hash.ToString("0000000000", CultureInfo.InvariantCulture);
        records.Add(Record(
            "8", serviceClass,
            entryAddenda.ToString("000000", CultureInfo.InvariantCulture),
            hashText,
            debitCents.ToString("000000000000", CultureInfo.InvariantCulture),
            creditCents.ToString("000000000000", CultureInfo.InvariantCulture),
            Fixed(origin.CompanyIdentification, 10),
            Fixed("", 19),
            Fixed("", 6),
            odfi,
            batchNumber));

        // The file control counts itself, so the block count includes it.
        var blocks = (records.Count + 1 + BlockingFactor - 1) / BlockingFactor;
        records.Add(Record(
            "9",
            "000001",
            blocks.ToString("000000", CultureInfo.InvariantCulture),
            entryAddenda.ToString("00000000", CultureInfo.InvariantCulture),
            hashText,
            debitCents.ToString("000000000000", CultureInfo.InvariantCulture),
            creditCents.ToString("000000000000", CultureInfo.InvariantCulture),
            Fixed("", 39)));
        while (records.Count % BlockingFactor != 0)
        {
            records.Add(new string('9', RecordLength));
        }

        var content = new StringBuilder();
        foreach (var r in records)
        {
            content.Append(r).Append("\r\n");
        }

        return new NachaFile(content.ToString(), creditTraces, entryAddenda, hash, debitCents / 100m, creditCents / 100m, blocks);
    }

    /// <summary>The CCD+ remittance addenda for the invoices paid (ANSI X12 RMR segments), or a summary when they don't fit in 80 characters.</summary>
    public static string Remittance(IReadOnlyList<(string Invoice, decimal Amount)> paid, string runNumber)
    {
        var segments = string.Concat(paid.Select(p => $"RMR*IV*{Clean(p.Invoice)}**{p.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\\"));
        return segments.Length <= 80 ? segments : Clean($"{runNumber} PAYS {paid.Count} INVOICES; REMITTANCE ADVICE SENT BY EMAIL")!;
    }

    /// <summary>Upper case printable ASCII; NACHA allows nothing else.</summary>
    public static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var b = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch is >= ' ' and <= '~')
            {
                b.Append(char.ToUpperInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch))
            {
                b.Append(' ');
            }
        }

        return b.ToString();
    }

    private static void Validate(NachaOrigin origin, NachaBatch batch, char fileIdModifier, int traceStart)
    {
        if (batch.Credits.Count == 0)
        {
            throw new ArgumentException("There's nothing to pay in this file.");
        }

        if (!char.IsAsciiDigit(fileIdModifier) && fileIdModifier is not (>= 'A' and <= 'Z'))
        {
            throw new ArgumentException("The file ID modifier is A–Z or 0–9.");
        }

        if (traceStart < 1 || traceStart + batch.Credits.Count + 1 > 9_999_999)
        {
            throw new ArgumentException("The trace sequence is out of range.");
        }

        if (string.IsNullOrWhiteSpace(origin.ImmediateOrigin) || string.IsNullOrWhiteSpace(origin.CompanyIdentification) || string.IsNullOrWhiteSpace(origin.CompanyName))
        {
            throw new ArgumentException("Set the company identification and name in the bank account's payment file settings.");
        }

        if (batch.Credits.Any(c => c.Amount <= 0))
        {
            throw new ArgumentException("Every ACH credit must be more than zero.");
        }
    }

    private static long Cents(decimal amount)
    {
        var cents = decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        return cents <= 9_999_999_999m ? (long)cents : throw new ArgumentException("An ACH entry can't be more than $99,999,999.99.");
    }

    private static string Digits(string value, int length, string what)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length == length ? digits : throw new ArgumentException($"{what} must be {length} digits.");
    }

    private static string AccountDigits(string value)
    {
        var account = new string((value ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return account.Length is >= 1 and <= 17 ? account : throw new ArgumentException("An account number has 1 to 17 characters.");
    }

    /// <summary>Upper case, padded with spaces (left-justified unless <paramref name="right"/>) or cut to <paramref name="width"/>.</summary>
    private static string Fixed(string? value, int width, bool right = false)
    {
        var text = Clean(value) ?? "";
        if (text.Length > width)
        {
            text = text[..width];
        }

        return right ? text.PadLeft(width) : text.PadRight(width);
    }

    private static string Record(params string[] fields)
    {
        var record = string.Concat(fields);
        return record.Length == RecordLength
            ? record
            : throw new InvalidOperationException($"A NACHA record must be {RecordLength} characters; this one is {record.Length}.");
    }
}
