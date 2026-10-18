using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Cash.Statements;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Cash;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash;

public sealed class BankStatementService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IBankStatementService
{
    /// <summary>Statement files larger than this are refused.</summary>
    public const int MaxContentLength = 5 * 1024 * 1024;

    public async Task<StatementImportResultDto> ImportAsync(ImportStatementRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("The statement file is empty.");
        }

        if (request.Content.Length > MaxContentLength)
        {
            throw new ArgumentException($"Statement files can be at most {MaxContentLength / (1024 * 1024)} MB.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<BankAccount>().FirstOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Bank account", request.BankAccountId);
        if (!account.IsActive)
        {
            throw new ArgumentException($"{account.Name} is inactive; reactivate it to import statements.");
        }

        var format = request.Format?.ToDomain() ?? Detect(request.Content, request.FileName);
        var mapping = request.Mapping ?? CashSupport.Deserialize(account.CsvMappingJson);
        var parsed = format switch
        {
            StatementFormat.Csv => CsvStatementParser.Parse(request.Content,
                mapping ?? throw new ArgumentException($"Map the CSV columns for {account.Name} first (date, description and amount).")),
            StatementFormat.Ofx => OfxStatementParser.Parse(request.Content),
            _ => Bai2StatementParser.Parse(request.Content, account.AccountNumberLast4),
        };

        if (parsed.AccountNumber is { Length: >= 4 } fileAccount && !fileAccount.EndsWith(account.AccountNumberLast4, StringComparison.Ordinal))
        {
            throw new ArgumentException($"This statement is for the account ending {fileAccount[^4..]}, not {account.Name} (ending {account.AccountNumberLast4}).");
        }

        if (parsed.Transactions.Count == 0)
        {
            throw new ArgumentException("The statement has no transactions.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var import = new BankStatementImport
        {
            BankAccountId = account.Id,
            Format = format,
            FileName = string.IsNullOrWhiteSpace(request.FileName) ? $"statement.{format.ToString().ToLowerInvariant()}" : Path.GetFileName(request.FileName.Trim()),
            PeriodStart = parsed.PeriodStart,
            PeriodEnd = parsed.PeriodEnd,
            OpeningBalance = parsed.OpeningBalance,
            ClosingBalance = request.ClosingBalance ?? parsed.ClosingBalance,
            Content = request.Content,
            ImportedBy = audit.Actor,
            ImportedAtUtc = now,
        };

        // Overlapping statements: an item is a duplicate when the account already has as many items with its fingerprint
        // as this file has up to and including it (so two identical $25 fees on one day both import the first time).
        var from = parsed.Transactions.Min(t => t.Date);
        var to = parsed.Transactions.Max(t => t.Date);
        var existing = (await db.Set<BankTransaction>().AsNoTracking()
                .Where(t => t.BankAccountId == account.Id && t.Date >= from && t.Date <= to).Select(t => t.Fingerprint).ToListAsync(cancellationToken))
            .GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var added = new List<BankTransaction>();
        var duplicates = 0;
        foreach (var t in parsed.Transactions)
        {
            var fingerprint = Fingerprint(t);
            var occurrence = seen[fingerprint] = seen.GetValueOrDefault(fingerprint) + 1;
            if (occurrence <= existing.GetValueOrDefault(fingerprint))
            {
                duplicates++;
                continue;
            }

            added.Add(new BankTransaction
            {
                BankAccountId = account.Id,
                ImportId = import.Id,
                Date = t.Date,
                Amount = t.Amount,
                Type = t.Type,
                Description = Clip(t.Description, 500)!,
                CheckNumber = Clip(t.CheckNumber, 32),
                BankReference = Clip(t.Reference, 100),
                Fingerprint = fingerprint,
                CreatedAtUtc = now,
            });
        }

        import.TransactionCount = added.Count;
        import.DuplicateCount = duplicates;
        db.Set<BankStatementImport>().Add(import);
        db.Set<BankTransaction>().AddRange(added);
        if (format == StatementFormat.Csv && request.Mapping is not null)
        {
            account.CsvMappingJson = CashSupport.Serialize(request.Mapping);
        }

        audit.Record(db, nameof(BankStatementImport), import.Id, "Imported", after: new
        {
            Account = account.Name,
            Format = format.ToString(),
            import.FileName,
            import.PeriodStart,
            import.PeriodEnd,
            import.ClosingBalance,
            Imported = added.Count,
            Duplicates = duplicates,
        });
        await db.SaveChangesAsync(cancellationToken);

        var warnings = parsed.Warnings.ToList();
        if (duplicates > 0)
        {
            warnings.Insert(0, $"{duplicates} item(s) were already imported from an earlier statement and were skipped.");
        }

        if (added.Count == 0)
        {
            warnings.Insert(0, "Nothing new: every item in this statement was already imported.");
        }

        return new StatementImportResultDto(import.ToDto(), warnings);
    }

    public async Task<IReadOnlyList<StatementImportDto>> ListImportsAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var imports = await db.Set<BankStatementImport>().AsNoTracking()
            .Where(i => bankAccountId == null || i.BankAccountId == bankAccountId)
            .Select(i => new { i.Id, i.BankAccountId, i.Format, i.FileName, i.PeriodStart, i.PeriodEnd, i.OpeningBalance, i.ClosingBalance, i.TransactionCount, i.DuplicateCount, i.ImportedBy, i.ImportedAtUtc })
            .ToListAsync(cancellationToken);
        return imports.OrderByDescending(i => i.ImportedAtUtc)
            .Select(i => new StatementImportDto(i.Id, i.BankAccountId, i.Format.ToContract(), i.FileName, i.PeriodStart, i.PeriodEnd, i.OpeningBalance,
                i.ClosingBalance, i.TransactionCount, i.DuplicateCount, i.ImportedBy, i.ImportedAtUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<BankItemDto>> ListItemsAsync(Guid bankAccountId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<BankAccount>().AnyAsync(a => a.Id == bankAccountId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Bank account", bankAccountId);
        }

        var items = await db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId && (from == null || t.Date >= from) && (to == null || t.Date <= to))
            .ToListAsync(cancellationToken);
        var locked = (await db.Set<BankMatch>().AsNoTracking().Where(m => m.BankAccountId == bankAccountId && m.ReconciliationId != null)
            .Select(m => m.Id).ToListAsync(cancellationToken)).ToHashSet();
        return items.OrderBy(t => t.Date).ThenBy(t => t.CreatedAtUtc).Select(t => t.ToDto(locked)).ToList();
    }

    public async Task<BankAccountDto> SaveCsvMappingAsync(Guid bankAccountId, CsvColumnMapping mapping, CancellationToken cancellationToken = default)
    {
        // Parsing an empty file validates the mapping's shape.
        CsvStatementParser.Parse("", mapping);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<BankAccount>().FirstOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Bank account", bankAccountId);
        var before = CashSupport.Deserialize(account.CsvMappingJson);
        account.CsvMappingJson = CashSupport.Serialize(mapping);
        audit.Record(db, nameof(BankAccount), account.Id, "CsvMappingSaved", before, mapping);
        await db.SaveChangesAsync(cancellationToken);
        return (await BankAccountService.ToDtosAsync(db, [account], cancellationToken))[0];
    }

    /// <summary>The format from the file's content (OFX header, BAI2 01 record), else its extension; CSV otherwise.</summary>
    public static StatementFormat Detect(string content, string? fileName)
    {
        if (OfxStatementParser.LooksLikeOfx(content))
        {
            return StatementFormat.Ofx;
        }

        if (Bai2StatementParser.LooksLikeBai2(content))
        {
            return StatementFormat.Bai2;
        }

        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return extension switch
        {
            ".ofx" or ".qfx" => StatementFormat.Ofx,
            ".bai" or ".bai2" => StatementFormat.Bai2,
            _ => StatementFormat.Csv,
        };
    }

    /// <summary>
    /// Identifies a bank item across statements: date, amount and the bank's reference (else the check number, else the
    /// description), normalized.
    /// </summary>
    public static string Fingerprint(ParsedBankTransaction t)
    {
        var key = !string.IsNullOrWhiteSpace(t.Reference) ? "r:" + Normalize(t.Reference)
            : !string.IsNullOrWhiteSpace(t.CheckNumber) ? "c:" + Normalize(t.CheckNumber)
            : "d:" + Normalize(t.Description);
        var fingerprint = $"{t.Date:yyyyMMdd}|{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{key}";
        return fingerprint.Length <= 200 ? fingerprint : fingerprint[..200];
    }

    /// <summary>Upper-case letters and digits only (so "ACH-000123" and "ach 000123" compare equal).</summary>
    public static string Normalize(string? text)
    {
        var sb = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
