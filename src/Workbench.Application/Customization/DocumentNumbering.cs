using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Companies;
using Workbench.Domain.Customization;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;
using Workbench.Domain.Travel;

namespace Workbench.Application.Customization;

/// <summary>A numbered document type: its key, name, built-in pattern and where its numbers are stored.</summary>
public sealed record DocumentNumberType(string Key, string Name, string DefaultPattern, Func<IWorkbenchDbContext, IQueryable<string>> Numbers, Func<IWorkbenchDbContext, int?> MaxLength)
{
    public static DocumentNumberType For<T>(string key, string name, string pattern, Expression<Func<T, string>> number)
        where T : class
    {
        var property = ((MemberExpression)number.Body).Member.Name;
        return new(key, name, pattern, db => db.Set<T>().AsNoTracking().Select(number), db => db.Set<T>().EntityType.FindProperty(property)?.GetMaxLength());
    }
}

/// <summary>The document types with administrator-configurable numbers. Their default patterns are today's formats.</summary>
public static class DocumentNumberTypes
{
    public const string PurchaseOrder = "purchase-order";
    public const string Requisition = "requisition";
    public const string Receipt = "receipt";
    public const string Rfq = "rfq";
    public const string ApBill = "ap-bill";
    public const string PaymentRun = "payment-run";
    public const string ShipNotice = "ship-notice";
    public const string Invoice = "invoice";
    public const string SalesOrder = "sales-order";
    public const string PickList = "pick-list";
    public const string Shipment = "shipment";
    public const string Nonconformance = "ncr";
    public const string CorrectiveAction = "car";
    public const string EngineeringChange = "eco";
    public const string Inspection = "inspection";
    public const string DebitMemo = "debit-memo";
    public const string TravelAuthorization = "travel-authorization";
    public const string Opportunity = "opportunity";

    public static IReadOnlyList<DocumentNumberType> All { get; } =
    [
        DocumentNumberType.For<PurchaseOrder>(PurchaseOrder, "Purchase orders", "PO-{seq:6}", p => p.Number),
        DocumentNumberType.For<PurchaseRequisition>(Requisition, "Purchase requisitions", "REQ-{seq:6}", r => r.Number),
        DocumentNumberType.For<PurchaseReceipt>(Receipt, "Purchase receipts", "RCV-{seq:6}", r => r.Number),
        DocumentNumberType.For<Rfq>(Rfq, "Requests for quote", "RFQ-{seq:6}", r => r.Number),
        DocumentNumberType.For<ApBill>(ApBill, "AP bills (vouchers)", "V-{seq:6}", b => b.Number),
        DocumentNumberType.For<ApPaymentRun>(PaymentRun, "AP payment runs", "PR-{seq:6}", r => r.Number),
        DocumentNumberType.For<ShipNotice>(ShipNotice, "Advance ship notices", "ASN-{seq:6}", s => s.Number),
        DocumentNumberType.For<Invoice>(Invoice, "Invoices", "{contract}-{seq:4}", i => i.Number),
        DocumentNumberType.For<SalesOrder>(SalesOrder, "Sales orders", "SO-{seq:6}", s => s.Number),
        DocumentNumberType.For<PickList>(PickList, "Pick lists", "PK-{seq:6}", p => p.Number),
        DocumentNumberType.For<Shipment>(Shipment, "Shipments", "SH-{seq:6}", s => s.Number),
        DocumentNumberType.For<Nonconformance>(Nonconformance, "Nonconformance reports (NCRs)", "NCR-{seq:6}", n => n.Number),
        DocumentNumberType.For<CorrectiveAction>(CorrectiveAction, "Corrective action requests", "CAR-{seq:6}", c => c.Number),
        DocumentNumberType.For<EngineeringChange>(EngineeringChange, "Engineering change orders", "ECO-{seq:6}", e => e.Number),
        DocumentNumberType.For<Inspection>(Inspection, "Inspections", "INS-{seq:6}", i => i.Number),
        DocumentNumberType.For<VendorDebitMemo>(DebitMemo, "Vendor debit memos", "DM-{seq:6}", m => m.Number),
        DocumentNumberType.For<TravelAuthorization>(TravelAuthorization, "Travel authorizations", "TA-{seq:6}", t => t.Number),
        DocumentNumberType.For<Domain.Crm.Opportunity>(Opportunity, "Opportunities", "OPP-{seq:4}", o => o.Number),
    ];

    public static DocumentNumberType? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    public static DocumentNumberType Get(string key) => Find(key) ?? throw new ArgumentException($"There is no numbered document type '{key}'.");
}

/// <summary>What a number is for: the document's company, date (for year tokens) and, for invoices, the contract prefix.</summary>
public sealed record NumberingContext(Guid? CompanyId = null, DateOnly? Date = null, string? Contract = null);

public sealed record AllocatedNumber(string Number, long Sequence);

/// <summary>A parsed numbering pattern: literal text and tokens, exactly one {seq:N}.</summary>
public sealed partial class NumberPattern
{
    public static readonly IReadOnlyList<string> Tokens = ["{YYYY}", "{YY}", "{MM}", "{FY}", "{company}", "{contract}", "{seq:6}"];

    private NumberPattern(string text, IReadOnlyList<(string? Literal, string? Token, int Width)> parts) => (Text, Parts) = (text, parts);

    public string Text { get; }
    public IReadOnlyList<(string? Literal, string? Token, int Width)> Parts { get; }

    public bool Has(string token) => Parts.Any(p => p.Token == token);
    public bool HasYear => Has("YYYY") || Has("YY") || Has("FY");

    public static NumberPattern Parse(string? pattern)
    {
        var text = pattern?.Trim() ?? "";
        if (text.Length is 0 or > 60)
        {
            throw new ArgumentException("A pattern is 1–60 characters, for example PO-{YYYY}-{seq:6}.");
        }

        var parts = new List<(string?, string?, int)>();
        var last = 0;
        foreach (Match m in TokenRegex().Matches(text))
        {
            if (m.Index > last)
            {
                parts.Add((Literal(text[last..m.Index]), null, 0));
            }

            var name = m.Groups["name"].Value;
            var width = m.Groups["width"].Success ? int.Parse(m.Groups["width"].Value, CultureInfo.InvariantCulture) : 0;
            if (name == "seq" && width is < 1 or > 12)
            {
                throw new ArgumentException("{seq:N} pads to 1–12 digits.");
            }

            parts.Add((null, name, width));
            last = m.Index + m.Length;
        }

        if (last < text.Length)
        {
            parts.Add((Literal(text[last..]), null, 0));
        }

        var count = parts.Count(p => p.Item2 == "seq");
        if (count != 1)
        {
            throw new ArgumentException("A pattern needs exactly one sequence token, such as {seq:6}.");
        }

        return new NumberPattern(text, parts);
    }

    public string Render(IReadOnlyDictionary<string, string> values, long sequence)
    {
        var sb = new StringBuilder();
        foreach (var (literal, token, width) in Parts)
        {
            sb.Append(literal ?? (token == "seq" ? sequence.ToString(new string('0', width), CultureInfo.InvariantCulture) : values[token!]));
        }

        return sb.ToString();
    }

    /// <summary>Everything before the sequence (for a prefix query) and a regex matching rendered numbers, capturing the sequence.</summary>
    public (string Prefix, Regex Matcher) Matcher(IReadOnlyDictionary<string, string> values)
    {
        var prefix = new StringBuilder();
        var regex = new StringBuilder("^");
        var beforeSeq = true;
        foreach (var (literal, token, _) in Parts)
        {
            if (token == "seq")
            {
                regex.Append("(?<seq>[0-9]+)");
                beforeSeq = false;
                continue;
            }

            var text = literal ?? values[token!];
            regex.Append(Regex.Escape(text));
            if (beforeSeq)
            {
                prefix.Append(text);
            }
        }

        return (prefix.ToString(), new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant));
    }

    private static string Literal(string text)
    {
        if (text.Contains('{') || text.Contains('}'))
        {
            throw new ArgumentException($"Unknown token in '{text}'. Use {string.Join(" ", Tokens)}.");
        }

        if (!text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or '#'))
        {
            throw new ArgumentException("Patterns may use letters, digits and - _ / . # around the tokens.");
        }

        return text;
    }

    [GeneratedRegex(@"\{(?<name>YYYY|YY|MM|FY|company|contract|seq)(?::(?<width>\d{1,2}))?\}")]
    private static partial Regex TokenRegex();
}

/// <summary>
/// Allocates document numbers. Each allocation is its own short unit of work on a <see cref="NumberSequence"/> row with an
/// optimistic version check, retried on conflict, so parallel requests (on InMemory, SQLite and PostgreSQL) never get the
/// same number; a document whose save then fails leaves a gap, never a duplicate. A new sequence starts after the highest
/// number already stored in its scope, and a number taken by a record made some other way is skipped.
/// </summary>
public sealed class DocumentNumbers(IWorkbenchDbContextFactory dbFactory, TimeProvider clock)
{
    private const int MaxAttempts = 100;

    public async Task<string> NextAsync(string documentType, NumberingContext? context = null, CancellationToken cancellationToken = default) =>
        (await AllocateAsync(documentType, context, cancellationToken)).Number;

    public async Task<AllocatedNumber> AllocateAsync(string documentType, NumberingContext? context = null, CancellationToken cancellationToken = default)
    {
        var type = DocumentNumberTypes.Get(documentType);
        context ??= new NumberingContext();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var (pattern, perCompany, resetYearly) = await SchemeAsync(db, type, cancellationToken);
            var values = await TokenValuesAsync(db, pattern, context, cancellationToken, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
            var scope = ScopeKey(pattern, perCompany, resetYearly, values);
            var sequence = await db.Set<NumberSequence>().FirstOrDefaultAsync(s => s.DocumentType == type.Key && s.ScopeKey == scope, cancellationToken);
            if (sequence is null)
            {
                sequence = new NumberSequence { DocumentType = type.Key, ScopeKey = scope, NextValue = await HighestAsync(db, type, pattern, values, cancellationToken) + 1 };
                db.Add(sequence);
            }

            var value = sequence.NextValue;
            sequence.NextValue = value + 1;
            sequence.Version++;
            sequence.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Someone else allocated (version changed) or created the scope's row first: read it again.
                await Task.Delay(Random.Shared.Next(1, 10 + (attempt * 5)), cancellationToken);
                continue;
            }

            var number = pattern.Render(values, value);
            if (type.MaxLength(db) is { } max && number.Length > max)
            {
                throw new InvalidOperationException($"The numbering pattern makes {number}, longer than the {max} characters {type.Name.ToLowerInvariant()} numbers may have. Shorten it under Admin › Customization › Numbering.");
            }

            if (!await type.Numbers(db).AnyAsync(n => n == number, cancellationToken))
            {
                return new(number, value);
            }
        }

        throw new InvalidOperationException($"Couldn't allocate a {type.Name.ToLowerInvariant()} number; try again.");
    }

    /// <summary>
    /// For code that only has the caller's unit of work (static helpers): the sequence row is changed in
    /// <paramref name="db"/> and saved with the document, so it's gapless; a concurrent allocation makes that save fail
    /// with a concurrency error (never a duplicate). Prefer <see cref="AllocateAsync"/> where the service can inject this class.
    /// </summary>
    public static async Task<AllocatedNumber> NextInUnitOfWorkAsync(IWorkbenchDbContext db, string documentType, NumberingContext? context, DateOnly today, CancellationToken ct)
    {
        var type = DocumentNumberTypes.Get(documentType);
        context ??= new NumberingContext();
        var (pattern, perCompany, resetYearly) = await SchemeAsync(db, type, ct);
        var values = await TokenValuesAsync(db, pattern, context, ct, today);
        var scope = ScopeKey(pattern, perCompany, resetYearly, values);
        var sequence = db.Set<NumberSequence>().Local.FirstOrDefault(s => s.DocumentType == type.Key && s.ScopeKey == scope)
            ?? await db.Set<NumberSequence>().FirstOrDefaultAsync(s => s.DocumentType == type.Key && s.ScopeKey == scope, ct);
        if (sequence is null)
        {
            sequence = new NumberSequence { DocumentType = type.Key, ScopeKey = scope, NextValue = await HighestAsync(db, type, pattern, values, ct) + 1 };
            db.Add(sequence);
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var value = sequence.NextValue;
            var number = pattern.Render(values, value);
            sequence.NextValue++;
            sequence.Version++;
            sequence.UpdatedAtUtc = DateTime.UtcNow;
            if (type.MaxLength(db) is { } max && number.Length > max)
            {
                throw new InvalidOperationException($"The numbering pattern makes {number}, longer than the {max} characters {type.Name.ToLowerInvariant()} numbers may have.");
            }

            if (!await type.Numbers(db).AnyAsync(n => n == number, ct))
            {
                return new(number, value);
            }
        }

        throw new InvalidOperationException($"Couldn't allocate a {type.Name.ToLowerInvariant()} number; try again.");
    }

    internal static async Task<(NumberPattern Pattern, bool PerCompany, bool ResetYearly)> SchemeAsync(IWorkbenchDbContext db, DocumentNumberType type, CancellationToken ct)
    {
        var scheme = await db.Set<NumberingScheme>().AsNoTracking().FirstOrDefaultAsync(s => s.DocumentType == type.Key, ct);
        return scheme is null ? (NumberPattern.Parse(type.DefaultPattern), false, false) : (NumberPattern.Parse(scheme.Pattern), scheme.PerCompany, scheme.ResetEachYear);
    }

    internal static async Task<IReadOnlyDictionary<string, string>> TokenValuesAsync(IWorkbenchDbContext db, NumberPattern pattern, NumberingContext context, CancellationToken ct, DateOnly? today = null)
    {
        var date = context.Date ?? today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YYYY"] = date.Year.ToString("0000", CultureInfo.InvariantCulture),
            ["YY"] = (date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
            ["MM"] = date.Month.ToString("00", CultureInfo.InvariantCulture),
        };
        if (pattern.Has("FY"))
        {
            var fy = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.Start <= date && y.End >= date).Select(y => (int?)y.Year).FirstOrDefaultAsync(ct);
            values["FY"] = (fy ?? (date.Month >= 10 ? date.Year + 1 : date.Year)).ToString("0000", CultureInfo.InvariantCulture);
        }

        var companyId = context.CompanyId ?? Company.DefaultId;
        values["company"] = await db.Set<Company>().AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Code).FirstOrDefaultAsync(ct) ?? "MAIN";
        values["companyId"] = companyId.ToString();
        if (pattern.Has("contract"))
        {
            values["contract"] = string.IsNullOrWhiteSpace(context.Contract) ? throw new ArgumentException("This number needs a contract.") : context.Contract;
        }

        return values;
    }

    internal static string ScopeKey(NumberPattern pattern, bool perCompany, bool resetYearly, IReadOnlyDictionary<string, string> values)
    {
        var parts = new List<string>();
        if (perCompany || pattern.Has("company"))
        {
            parts.Add($"company={values["companyId"]}");
        }

        if (resetYearly)
        {
            parts.Add($"year={(pattern.Has("FY") ? values["FY"] : values["YYYY"])}");
        }

        if (pattern.Has("contract"))
        {
            parts.Add($"contract={values["contract"]}");
        }

        return string.Join('|', parts);
    }

    private static async Task<long> HighestAsync(IWorkbenchDbContext db, DocumentNumberType type, NumberPattern pattern, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var (prefix, matcher) = pattern.Matcher(values);
        var numbers = await type.Numbers(db).Where(n => n.StartsWith(prefix)).ToListAsync(ct);
        return numbers.Select(n => matcher.Match(n)).Where(m => m.Success && long.TryParse(m.Groups["seq"].Value, CultureInfo.InvariantCulture, out _))
            .Select(m => long.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
    }
}

public sealed class NumberingService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : INumberingService
{
    public async Task<IReadOnlyList<NumberingSchemeDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var stored = await db.Set<NumberingScheme>().AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<NumberingSchemeDto>();
        foreach (var type in DocumentNumberTypes.All)
        {
            result.Add(await ToDtoAsync(db, type, stored.FirstOrDefault(s => s.DocumentType == type.Key), cancellationToken));
        }

        return result;
    }

    public async Task<NumberingSchemeDto> SaveAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = DocumentNumberTypes.Get(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pattern = await ValidateAsync(db, type, request, cancellationToken);
        var scheme = await db.Set<NumberingScheme>().FirstOrDefaultAsync(s => s.DocumentType == type.Key, cancellationToken);
        var before = scheme is null ? new { pattern = type.DefaultPattern, perCompany = false, resetEachYear = false } : new { pattern = scheme.Pattern, perCompany = scheme.PerCompany, resetEachYear = scheme.ResetEachYear };
        if (scheme is null)
        {
            scheme = new NumberingScheme { DocumentType = type.Key, Pattern = pattern.Text };
            db.Add(scheme);
        }

        (scheme.Pattern, scheme.PerCompany, scheme.ResetEachYear, scheme.UpdatedAtUtc, scheme.UpdatedBy) =
            (pattern.Text, request.PerCompany, request.ResetEachYear, clock.GetUtcNow().UtcDateTime, audit.Actor);
        audit.Record(db, "NumberingScheme", scheme.Id, "Saved", before, new { pattern = scheme.Pattern, perCompany = scheme.PerCompany, resetEachYear = scheme.ResetEachYear, type = type.Key });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, type, scheme, cancellationToken);
    }

    public async Task<NumberingSchemeDto> ResetAsync(string documentType, CancellationToken cancellationToken = default)
    {
        var type = DocumentNumberTypes.Get(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<NumberingScheme>().FirstOrDefaultAsync(s => s.DocumentType == type.Key, cancellationToken) is { } scheme)
        {
            audit.Record(db, "NumberingScheme", scheme.Id, "Reset to default", new { pattern = scheme.Pattern }, new { pattern = type.DefaultPattern });
            db.Remove(scheme);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await ToDtoAsync(db, type, null, cancellationToken);
    }

    public async Task<NumberingPreviewDto> PreviewAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default)
    {
        var type = DocumentNumberTypes.Get(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        try
        {
            var pattern = await ValidateAsync(db, type, request, cancellationToken);
            return new(await ExampleAsync(db, pattern, cancellationToken), null);
        }
        catch (ArgumentException ex)
        {
            return new("", ex.Message);
        }
    }

    private async Task<NumberPattern> ValidateAsync(IWorkbenchDbContext db, DocumentNumberType type, SaveNumberingSchemeRequest request, CancellationToken ct)
    {
        var pattern = NumberPattern.Parse(request.Pattern);
        if (type.Key == DocumentNumberTypes.Invoice && !pattern.Has("contract"))
        {
            throw new ArgumentException("Invoice numbers count per contract, so the pattern needs {contract}.");
        }

        if (type.Key != DocumentNumberTypes.Invoice && pattern.Has("contract"))
        {
            throw new ArgumentException("{contract} is only for invoice numbers.");
        }

        if (request.ResetEachYear && !pattern.HasYear)
        {
            throw new ArgumentException("A sequence that restarts every year needs {YYYY}, {YY} or {FY} in the pattern, or numbers would repeat.");
        }

        if (request.PerCompany && !pattern.Has("company"))
        {
            throw new ArgumentException("A sequence per company needs {company} in the pattern, or numbers would repeat.");
        }

        var example = await ExampleAsync(db, pattern, ct);
        if (type.MaxLength(db) is { } max && example.Length > max)
        {
            throw new ArgumentException($"{example} is longer than the {max} characters these numbers may have.");
        }

        return pattern;
    }

    private async Task<string> ExampleAsync(IWorkbenchDbContext db, NumberPattern pattern, CancellationToken ct)
    {
        var codes = await db.Set<Company>().AsNoTracking().Select(c => c.Code).ToListAsync(ct);
        var values = await DocumentNumbers.TokenValuesAsync(db, pattern, new NumberingContext(Contract: "W56HZV26C0042"), ct, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        var longest = new Dictionary<string, string>(values) { ["company"] = codes.OrderByDescending(c => c.Length).FirstOrDefault() ?? values["company"] };
        return pattern.Render(longest, 123);
    }

    private async Task<NumberingSchemeDto> ToDtoAsync(IWorkbenchDbContext db, DocumentNumberType type, NumberingScheme? scheme, CancellationToken ct)
    {
        var pattern = NumberPattern.Parse(scheme?.Pattern ?? type.DefaultPattern);
        return new(type.Key, type.Name, type.DefaultPattern, pattern.Text, scheme?.PerCompany ?? false, scheme?.ResetEachYear ?? false, scheme is not null,
            await ExampleAsync(db, pattern, ct), NumberPattern.Tokens);
    }
}
