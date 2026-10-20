using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Cash;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.BankFiles;
using Workbench.Contracts.Security;
using Workbench.Domain.Cash;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.BankFiles;

/// <summary>
/// Payment files for AP payment runs. ACH runs give a NACHA file (one CCD credit per vendor, CCD+ remittance addenda) from the
/// paying bank account's settings; check runs give a positive pay file (issues and voids, or voids only) in the bank's
/// layout. Files are returned to the caller to save and upload; Workbench never sends them. Each download is logged with
/// its counts, total and SHA-256, audited and recorded in the security log.
/// </summary>
public sealed class PaymentFileService(
    IWorkbenchDbContextFactory dbFactory,
    VendorBankProtector vendorProtector,
    BankAccountNumberProtector bankProtector,
    AuditRecorder audit,
    AccessControl access,
    SecurityEventLog securityLog,
    IEmailSender email,
    TimeProvider clock) : IPaymentFileService
{
    public const string SettingsEntity = nameof(BankAchSettings);
    public const string LayoutEntity = nameof(PositivePayLayout);
    private const string RunEntity = nameof(ApPaymentRun);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BankFileSettingsDto>> ListBankAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        return await SettingsDtosAsync(db, accounts, cancellationToken);
    }

    public async Task<BankFileSettingsDto> GetSettingsAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindBankAccountAsync(db, bankAccountId, cancellationToken);
        return (await SettingsDtosAsync(db, [account], cancellationToken))[0];
    }

    public async Task<BankFileSettingsDto> SaveSettingsAsync(Guid bankAccountId, SaveBankFileSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindBankAccountAsync(db, bankAccountId, cancellationToken);
        var destination = Digits(request.ImmediateDestination, 9, "The immediate destination (your bank's routing number)");
        CashSupport.NormalizeRouting(destination);
        var dfi = string.IsNullOrWhiteSpace(request.OriginatingDfi) ? destination[..8] : Digits(request.OriginatingDfi, 8, "The originating DFI");
        if (request.PositivePayLayoutId is { } layoutId && PositivePayWriter.Presets.All(p => p.Id != layoutId)
            && !await db.Set<PositivePayLayout>().AnyAsync(l => l.Id == layoutId, cancellationToken))
        {
            throw new ArgumentException("That positive pay layout doesn't exist.");
        }

        if (request.BalancedFile && (account.ProtectedAccountNumber is null || account.RoutingNumber is null))
        {
            throw new ArgumentException("A balanced file debits this account, so enter its full account number and routing number first (Cash › Bank accounts).");
        }

        var settings = await db.Set<BankAchSettings>().FirstOrDefaultAsync(s => s.BankAccountId == account.Id, cancellationToken);
        var before = settings is null ? null : Snapshot(settings);
        if (settings is null)
        {
            settings = new BankAchSettings
            {
                BankAccountId = account.Id,
                ImmediateDestination = destination,
                ImmediateDestinationName = "",
                ImmediateOrigin = "",
                ImmediateOriginName = "",
                CompanyIdentification = "",
                CompanyName = "",
                OriginatingDfi = dfi,
                UpdatedBy = access.Email,
            };
            db.Set<BankAchSettings>().Add(settings);
        }

        settings.ImmediateDestination = destination;
        settings.ImmediateDestinationName = Text(request.ImmediateDestinationName, "The bank's name", 23);
        settings.ImmediateOrigin = Text(request.ImmediateOrigin, "The immediate origin", 10);
        settings.ImmediateOriginName = Text(request.ImmediateOriginName, "Your company's name", 23);
        settings.CompanyIdentification = Text(request.CompanyIdentification, "The company identification", 10);
        settings.CompanyName = Text(request.CompanyName, "The company name", 16);
        settings.EntryDescription = Text(string.IsNullOrWhiteSpace(request.EntryDescription) ? "VENDOR PAY" : request.EntryDescription, "The entry description", 10);
        settings.OriginatingDfi = dfi;
        settings.BalancedFile = request.BalancedFile;
        settings.PositivePayLayoutId = request.PositivePayLayoutId;
        settings.UpdatedBy = access.Email;
        settings.UpdatedAtUtc = UtcNow;
        audit.Record(db, SettingsEntity, settings.Id, before is null ? "Created" : "Updated", before, Snapshot(settings));
        await db.SaveChangesAsync(cancellationToken);
        return (await SettingsDtosAsync(db, [account], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<PositivePayLayoutDto>> ListLayoutsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var own = await db.Set<PositivePayLayout>().AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).ToListAsync(cancellationToken);
        return [.. PositivePayWriter.Presets, .. own.Select(ToDto)];
    }

    public async Task<PositivePayLayoutDto> SaveLayoutAsync(Guid? layoutId, SavePositivePayLayoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() is { Length: > 0 and <= 100 } n ? n : throw new ArgumentException("A layout needs a name of up to 100 characters.");
        PositivePayWriter.Validate(request.Fields, request.Format);
        if (PositivePayWriter.Presets.Any(p => p.Id == layoutId))
        {
            throw new ArgumentException("Built-in layouts can't be changed; save a copy under a new name.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<PositivePayLayout>().AnyAsync(l => l.Name == name && l.Id != layoutId, cancellationToken) || PositivePayWriter.Presets.Any(p => p.Name == name))
        {
            throw new ArgumentException($"There's already a layout called \"{name}\".");
        }

        var layout = layoutId is { } id
            ? await db.Set<PositivePayLayout>().FirstOrDefaultAsync(l => l.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Positive pay layout", id)
            : null;
        var json = JsonSerializer.Serialize(request.Fields, PositivePayWriter.Json);
        if (json.Length > 8000)
        {
            throw new ArgumentException("The layout is too long.");
        }

        if (layout is null)
        {
            layout = new PositivePayLayout { Name = name, FieldsJson = json, UpdatedBy = access.Email };
            db.Set<PositivePayLayout>().Add(layout);
        }

        layout.Name = name;
        layout.Format = request.Format == PositivePayFileFormat.Csv ? PositivePayFormat.Csv : PositivePayFormat.FixedWidth;
        layout.FieldsJson = json;
        layout.IncludeHeader = request.IncludeHeader;
        layout.IssueCode = Code(request.IssueCode);
        layout.VoidCode = Code(request.VoidCode);
        layout.UpdatedBy = access.Email;
        layout.UpdatedAtUtc = UtcNow;
        audit.Record(db, LayoutEntity, layout.Id, layoutId is null ? "Created" : "Updated", after: new { layout.Name, Format = layout.Format.ToString(), Fields = request.Fields.Count });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(layout);
    }

    public async Task<PaymentRunFilesDto> GetRunFilesAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await FindRunAsync(db, runId, cancellationToken);
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(a => a.IsActive && a.CompanyId == run.CompanyId).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        var settings = await SettingsDtosAsync(db, accounts, cancellationToken);
        var usd = accounts.Where(a => a.Currency == "USD").Select(a => a.Id).ToHashSet();
        var preferred = settings.FirstOrDefault(s => usd.Contains(s.BankAccountId) && (run.Method == PaymentMethod.Check || s.IsConfigured)) ?? settings.FirstOrDefault();
        var problems = await ProblemsAsync(db, run, preferred, cancellationToken);
        var downloads = await DownloadsAsync(db, run.Id, cancellationToken);
        return new PaymentRunFilesDto(run.Id, settings, preferred?.BankAccountId, problems, downloads, await RemittancesAsync(db, run, cancellationToken));
    }

    public async Task<PaymentFileDto> GenerateAsync(Guid runId, GeneratePaymentFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<ApPaymentRun>().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Payment run", runId);
        var account = await FindBankAccountAsync(db, request.BankAccountId, cancellationToken);
        if (!account.IsActive)
        {
            throw new ArgumentException($"{account.Name} is inactive.");
        }

        if (account.CompanyId != run.CompanyId)
        {
            throw new ArgumentException($"{account.Name} belongs to another company than payment run {run.Number}.");
        }

        if (run.Status is not (PaymentRunStatus.Approved or PaymentRunStatus.Completed))
        {
            throw new InvalidOperationException(run.Status == PaymentRunStatus.Draft
                ? $"Payment run {run.Number} needs approval before its bank file can be made."
                : $"Payment run {run.Number} was cancelled.");
        }

        var payments = await db.Set<ApPayment>().Where(p => p.RunId == run.Id).ToListAsync(cancellationToken);
        var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var billIds = payments.Select(p => p.BillId).Distinct().ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);

        PaymentFileDto file;
        string? modifier = null;
        if (request.Kind == PaymentFileType.Nacha)
        {
            (file, modifier) = await NachaAsync(db, run, account, payments, vendors, bills, cancellationToken);
        }
        else
        {
            file = await PositivePayAsync(db, run, account, payments, vendors, request.Kind == PaymentFileType.PositivePayVoids, cancellationToken);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(file.Content)));
        var download = new PaymentFileDownload
        {
            RunId = run.Id,
            BankAccountId = account.Id,
            Kind = request.Kind.ToDomain(),
            FileName = file.FileName,
            FileIdModifier = modifier,
            EntryCount = file.EntryCount,
            TotalAmount = file.TotalAmount,
            Sha256 = hash,
            DownloadedBy = access.Email,
            DownloadedAtUtc = UtcNow,
        };
        db.Set<PaymentFileDownload>().Add(download);
        audit.Record(db, RunEntity, run.Id, "FileDownloaded", after: new { Kind = request.Kind.ToString(), file.FileName, file.EntryCount, file.TotalAmount, Bank = account.Name, Sha256 = hash });
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(BankFileSecurityEvents.PaymentFileDownloaded, $"payment run {run.Number}", SecurityOutcomes.Success,
            $"{request.Kind}: {file.EntryCount} entries, {account.Name}", cancellationToken: cancellationToken);
        return file;
    }

    public async Task<RemittanceResultDto> SendRemittanceAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await FindRunAsync(db, runId, cancellationToken);
        if (run.Status is not (PaymentRunStatus.Approved or PaymentRunStatus.Completed))
        {
            throw new InvalidOperationException($"Remittance advice goes out once payment run {run.Number} is approved.");
        }

        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.RunId == run.Id && p.Status != ApPaymentStatus.Void).ToListAsync(cancellationToken);
        var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var banks = await db.Set<VendorBankAccount>().AsNoTracking().Where(a => vendorIds.Contains(a.VendorId)).ToDictionaryAsync(a => a.VendorId, cancellationToken);
        var billIds = payments.Select(p => p.BillId).Distinct().ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var sent = 0;
        var skipped = new List<string>();
        foreach (var group in payments.GroupBy(p => p.VendorId).OrderBy(g => vendors[g.Key].Code, StringComparer.Ordinal))
        {
            var vendor = vendors[group.Key];
            var to = banks.GetValueOrDefault(vendor.Id)?.RemittanceEmail ?? vendor.ContactEmail;
            if (string.IsNullOrWhiteSpace(to) || !to.Contains('@', StringComparison.Ordinal))
            {
                skipped.Add($"{vendor.Code}: no remittance or contact email");
                continue;
            }

            var lines = group.OrderBy(p => bills[p.BillId].InvoiceNumber, StringComparer.Ordinal).ToList();
            await email.SendAsync(RemittanceEmail(run, vendor, to, lines, bills), cancellationToken);
            db.Set<RemittanceAdvice>().Add(new RemittanceAdvice
            {
                RunId = run.Id, VendorId = vendor.Id, Email = to.Trim().ToLowerInvariant(), Amount = lines.Sum(p => p.Amount), Payments = lines.Count,
                SentBy = access.Email, SentAtUtc = UtcNow,
            });
            sent++;
        }

        audit.Record(db, RunEntity, run.Id, "RemittanceSent", after: new { Sent = sent, Skipped = skipped.Count });
        await db.SaveChangesAsync(cancellationToken);
        return new RemittanceResultDto(sent, skipped);
    }

    // ---- NACHA ----

    private async Task<(PaymentFileDto File, string Modifier)> NachaAsync(
        IWorkbenchDbContext db, ApPaymentRun run, BankAccount account, List<ApPayment> payments, Dictionary<Guid, Vendor> vendors, Dictionary<Guid, ApBill> bills, CancellationToken ct)
    {
        if (run.Method != PaymentMethod.Ach)
        {
            throw new ArgumentException($"Payment run {run.Number} pays by check; NACHA files are for ACH runs.");
        }

        if (account.Currency != "USD")
        {
            throw new ArgumentException($"ACH files move US dollars only; {account.Name} is kept in {account.Currency}.");
        }

        var live = payments.Where(p => p.Status != ApPaymentStatus.Void).ToList();
        if (live.Count == 0)
        {
            throw new ArgumentException($"Payment run {run.Number} has nothing left to pay.");
        }

        if (live.FirstOrDefault(p => p.CurrencyCode is not null) is { } foreign)
        {
            throw new ArgumentException($"ACH is US dollars only: {vendors[foreign.VendorId].Name} is paid in {foreign.CurrencyCode}. Pay foreign bills by wire or check.");
        }

        var settings = await db.Set<BankAchSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.BankAccountId == account.Id, ct)
            ?? throw new ArgumentException($"Set up {account.Name}'s ACH settings (Cash › Bank accounts › Payment files) first.");
        var vendorIds = live.Select(p => p.VendorId).Distinct().ToList();
        var banks = await db.Set<VendorBankAccount>().AsNoTracking().Where(a => vendorIds.Contains(a.VendorId)).ToDictionaryAsync(a => a.VendorId, ct);
        var missing = vendorIds.Where(id => !banks.ContainsKey(id)).Select(id => vendors[id].Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"No approved bank details for {string.Join(", ", missing)}. Add them on the vendor page and have them approved.");
        }

        // One credit per vendor, in vendor code order; the addenda lists the invoices paid.
        var groups = live.GroupBy(p => p.VendorId).OrderBy(g => vendors[g.Key].Code, StringComparer.Ordinal).ToList();
        var credits = new List<NachaCredit>();
        foreach (var g in groups)
        {
            var vendor = vendors[g.Key];
            var bank = banks[g.Key];
            string routing, number;
            try
            {
                (routing, number) = (vendorProtector.UnprotectRouting(bank.ProtectedRoutingNumber), vendorProtector.UnprotectAccount(bank.ProtectedAccountNumber));
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException($"{vendor.Name}'s bank details couldn't be decrypted. Check the Data Protection keys.");
            }

            var paid = g.OrderBy(p => bills[p.BillId].InvoiceNumber, StringComparer.Ordinal).Select(p => (bills[p.BillId].InvoiceNumber, p.Amount)).ToList();
            credits.Add(new NachaCredit(routing, number, bank.Kind == BankAccountKind.Savings, g.Sum(p => p.Amount), vendor.Code, bank.AccountHolderName,
                NachaWriter.Remittance(paid, run.Number)));
        }

        NachaOffset? offset = null;
        if (settings.BalancedFile)
        {
            if (account.ProtectedAccountNumber is null || account.RoutingNumber is null)
            {
                throw new ArgumentException($"A balanced file debits {account.Name}; enter its full account and routing numbers first.");
            }

            offset = new NachaOffset(account.RoutingNumber, bankProtector.Unprotect(account.ProtectedAccountNumber), false);
        }

        var now = UtcNow;
        var today = DateOnly.FromDateTime(now);
        var dayStart = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var earlier = await db.Set<PaymentFileDownload>().CountAsync(d => d.BankAccountId == account.Id && d.Kind == PaymentFileKind.Nacha && d.DownloadedAtUtc >= dayStart, ct);
        var modifier = FileIdModifier(earlier);
        var origin = new NachaOrigin(settings.ImmediateDestination, settings.ImmediateDestinationName, settings.ImmediateOrigin, settings.ImmediateOriginName,
            settings.CompanyIdentification, settings.CompanyName, settings.EntryDescription, settings.OriginatingDfi);
        var effective = run.PaymentDate > today ? run.PaymentDate : today;
        var nacha = NachaWriter.Write(origin, new NachaBatch(effective, credits, DescriptiveDate: run.PaymentDate), now, modifier, TraceStart(run.Number), offset);

        // The trace number is each payment's ACH reference until the payments are recorded.
        if (run.Status == PaymentRunStatus.Approved)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                foreach (var p in groups[i].Where(p => p.Status == ApPaymentStatus.Scheduled))
                {
                    p.Reference = nacha.TraceNumbers[i];
                }
            }
        }

        var name = $"{run.Number}-{effective:yyyyMMdd}-{modifier}.ach";
        return (new PaymentFileDto(name, "text/plain", nacha.Content, credits.Count, nacha.TotalCredit), modifier.ToString());
    }

    /// <summary>A, B, ... Z, then 0–9: the Nth file sent to the bank from this account today.</summary>
    public static char FileIdModifier(int earlierToday) => earlierToday switch
    {
        < 26 => (char)('A' + earlierToday),
        < 36 => (char)('0' + earlierToday - 26),
        _ => throw new InvalidOperationException("That's 36 ACH files from this account today, the most NACHA allows. Try again tomorrow."),
    };

    /// <summary>Trace sequences that stay the same for a run (so a re-download keeps them) and differ between runs.</summary>
    public static int TraceStart(string runNumber)
    {
        var digits = new string(runNumber.Where(char.IsAsciiDigit).ToArray());
        var n = digits.Length == 0 ? 0 : (int)(long.Parse(digits[^Math.Min(digits.Length, 9)..], CultureInfo.InvariantCulture) % 9_000);
        return n * 1_000 + 1;
    }

    // ---- Positive pay ----

    private async Task<PaymentFileDto> PositivePayAsync(
        IWorkbenchDbContext db, ApPaymentRun run, BankAccount account, List<ApPayment> payments, Dictionary<Guid, Vendor> vendors, bool voidsOnly, CancellationToken ct)
    {
        if (run.Method != PaymentMethod.Check)
        {
            throw new ArgumentException($"Payment run {run.Number} pays by ACH; positive pay lists checks.");
        }

        if (account.ProtectedAccountNumber is null)
        {
            throw new ArgumentException($"Positive pay needs {account.Name}'s full account number. Enter it under Cash › Bank accounts.");
        }

        var checks = payments.Where(p => p.Reference is not null && p.PaidOn is not null && (!voidsOnly || p.Status == ApPaymentStatus.Void)).ToList();
        if (checks.Count == 0)
        {
            throw new ArgumentException(voidsOnly ? $"No checks in payment run {run.Number} have been voided." : $"Record the check numbers for payment run {run.Number} first.");
        }

        var settings = await db.Set<BankAchSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.BankAccountId == account.Id, ct);
        var layout = await LayoutAsync(db, settings?.PositivePayLayoutId, ct);
        var items = checks
            .OrderBy(p => p.Reference!.Length).ThenBy(p => p.Reference, StringComparer.Ordinal)
            .Select(p => new PositivePayItem(p.Reference!, p.PaidOn!.Value, p.CashAmount ?? p.Amount, vendors[p.VendorId].Name, p.Status == ApPaymentStatus.Void))
            .ToList();
        var content = PositivePayWriter.Write(layout, bankProtector.Unprotect(account.ProtectedAccountNumber), account.RoutingNumber, items);
        var extension = layout.Format == PositivePayFileFormat.Csv ? "csv" : "txt";
        var name = $"{run.Number}-positive-pay{(voidsOnly ? "-voids" : "")}-{UtcNow:yyyyMMddHHmm}.{extension}";
        return new PaymentFileDto(name, layout.Format == PositivePayFileFormat.Csv ? "text/csv" : "text/plain", content, items.Count, items.Where(i => !i.IsVoid).Sum(i => i.Amount));
    }

    private static async Task<PositivePayLayoutDto> LayoutAsync(IWorkbenchDbContext db, Guid? layoutId, CancellationToken ct)
    {
        if (layoutId is { } id)
        {
            if (PositivePayWriter.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            {
                return preset;
            }

            if (await db.Set<PositivePayLayout>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct) is { } own)
            {
                return ToDto(own);
            }
        }

        return PositivePayWriter.Presets[0];
    }

    // ---- Remittance ----

    private static EmailMessage RemittanceEmail(ApPaymentRun run, Vendor vendor, string to, IReadOnlyList<ApPayment> lines, IReadOnlyDictionary<Guid, ApBill> bills)
    {
        var method = run.Method == PaymentMethod.Ach ? "ACH (direct deposit)" : "check";
        var date = (lines.FirstOrDefault(p => p.PaidOn is not null)?.PaidOn ?? run.PaymentDate).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var total = lines.Sum(p => p.Amount);
        var subject = $"Remittance advice: payment of {total.ToString("C", CultureInfo.GetCultureInfo("en-US"))} ({run.Number})";
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Hello {vendor.Name},")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"We're paying you {total.ToString("C", CultureInfo.GetCultureInfo("en-US"))} by {method} on {date}. It covers:")
            .AppendLine();
        var html = new StringBuilder()
            .Append("<h1 style=\"font-size:20px;margin:0 0 12px\">Remittance advice</h1>")
            .Append("<p style=\"margin:0 0 12px\">Hello ").Append(WebUtility.HtmlEncode(vendor.Name)).Append(",</p>")
            .Append("<p style=\"margin:0 0 12px\">We're paying you <strong>").Append(WebUtility.HtmlEncode(total.ToString("C", CultureInfo.GetCultureInfo("en-US"))))
            .Append("</strong> by ").Append(method).Append(" on ").Append(date).Append(". It covers:</p>")
            .Append("<table style=\"border-collapse:collapse;margin:0 0 16px\"><thead><tr>")
            .Append("<th scope=\"col\" style=\"text-align:left;padding:4px 12px 4px 0\">Your invoice</th>")
            .Append("<th scope=\"col\" style=\"text-align:left;padding:4px 12px 4px 0\">Invoice date</th>")
            .Append("<th scope=\"col\" style=\"text-align:right;padding:4px 12px 4px 0\">Paid</th>")
            .Append("<th scope=\"col\" style=\"text-align:left;padding:4px 0\">Reference</th></tr></thead><tbody>");
        foreach (var p in lines)
        {
            var bill = bills[p.BillId];
            var amount = p.Amount.ToString("C", CultureInfo.GetCultureInfo("en-US"));
            text.AppendLine(CultureInfo.InvariantCulture, $"  Invoice {bill.InvoiceNumber} ({bill.InvoiceDate:yyyy-MM-dd}): {amount}{(p.Reference is null ? "" : $", reference {p.Reference}")}");
            html.Append("<tr><td style=\"padding:4px 12px 4px 0\">").Append(WebUtility.HtmlEncode(bill.InvoiceNumber))
                .Append("</td><td style=\"padding:4px 12px 4px 0\">").Append(bill.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("</td><td style=\"text-align:right;padding:4px 12px 4px 0\">").Append(WebUtility.HtmlEncode(amount))
                .Append("</td><td style=\"padding:4px 0\">").Append(WebUtility.HtmlEncode(p.Reference ?? "")).Append("</td></tr>");
        }

        text.AppendLine().AppendLine("Questions about this payment? Reply to this email.");
        html.Append("</tbody></table><p style=\"margin:0\">Questions about this payment? Reply to this email.</p>");
        return new EmailMessage(to.Trim(), subject, text.ToString(), html.ToString());
    }

    private static async Task<IReadOnlyList<RemittanceAdviceDto>> RemittancesAsync(IWorkbenchDbContext db, ApPaymentRun run, CancellationToken ct)
    {
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.RunId == run.Id && p.Status != ApPaymentStatus.Void).ToListAsync(ct);
        var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var banks = await db.Set<VendorBankAccount>().AsNoTracking().Where(a => vendorIds.Contains(a.VendorId)).ToDictionaryAsync(a => a.VendorId, ct);
        var sent = (await db.Set<RemittanceAdvice>().AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync(ct))
            .GroupBy(r => r.VendorId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.SentAtUtc).First());
        return payments.GroupBy(p => p.VendorId).OrderBy(g => vendors[g.Key].Code, StringComparer.Ordinal).Select(g =>
        {
            var v = vendors[g.Key];
            var last = sent.GetValueOrDefault(v.Id);
            return new RemittanceAdviceDto(v.Id, v.Code, v.Name, banks.GetValueOrDefault(v.Id)?.RemittanceEmail ?? v.ContactEmail, g.Sum(p => p.Amount), g.Count(), last?.SentAtUtc, last?.SentBy);
        }).ToList();
    }

    // ---- Helpers ----

    private static async Task<List<string>> ProblemsAsync(IWorkbenchDbContext db, ApPaymentRun run, BankFileSettingsDto? account, CancellationToken ct)
    {
        var problems = new List<string>();
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.RunId == run.Id && p.Status != ApPaymentStatus.Void).ToListAsync(ct);
        if (run.Status is PaymentRunStatus.Draft)
        {
            problems.Add("The run needs approval before its bank file can be made.");
        }

        if (account is null)
        {
            problems.Add("Add a bank account for this company under Cash › Bank accounts.");
            return problems;
        }

        if (run.Method == PaymentMethod.Ach)
        {
            if (!account.IsConfigured)
            {
                problems.Add($"{account.BankAccountName} has no ACH settings yet (company ID, bank routing).");
            }

            if (payments.Any(p => p.CurrencyCode is not null))
            {
                problems.Add("ACH is US dollars only; foreign currency payments can't go in a NACHA file.");
            }

            var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
            var withBank = await db.Set<VendorBankAccount>().AsNoTracking().Where(a => vendorIds.Contains(a.VendorId)).Select(a => a.VendorId).ToListAsync(ct);
            var missing = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id) && !withBank.Contains(v.Id)).Select(v => v.Name).ToListAsync(ct);
            if (missing.Count > 0)
            {
                problems.Add($"No approved bank details for {string.Join(", ", missing.OrderBy(n => n, StringComparer.Ordinal))}.");
            }
        }
        else
        {
            if (!account.HasFullAccountNumber)
            {
                problems.Add($"Positive pay needs {account.BankAccountName}'s full account number.");
            }

            if (run.Status != PaymentRunStatus.Completed)
            {
                problems.Add("Positive pay lists issued checks: record the check numbers first.");
            }
        }

        return problems;
    }

    private static async Task<IReadOnlyList<PaymentFileDownloadDto>> DownloadsAsync(IWorkbenchDbContext db, Guid runId, CancellationToken ct)
    {
        var rows = await db.Set<PaymentFileDownload>().AsNoTracking().Where(d => d.RunId == runId).ToListAsync(ct);
        var accountIds = rows.Select(r => r.BankAccountId).Distinct().ToList();
        var names = await db.Set<BankAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.OrderByDescending(r => r.DownloadedAtUtc).Select(r => new PaymentFileDownloadDto(r.Id, r.Kind.ToContract(), r.FileName, r.FileIdModifier, r.EntryCount,
            r.TotalAmount, r.Sha256, names.GetValueOrDefault(r.BankAccountId) ?? "", r.DownloadedBy, r.DownloadedAtUtc)).ToList();
    }

    private static async Task<IReadOnlyList<BankFileSettingsDto>> SettingsDtosAsync(IWorkbenchDbContext db, IReadOnlyList<BankAccount> accounts, CancellationToken ct)
    {
        var ids = accounts.Select(a => a.Id).ToList();
        var settings = await db.Set<BankAchSettings>().AsNoTracking().Where(s => ids.Contains(s.BankAccountId)).ToDictionaryAsync(s => s.BankAccountId, ct);
        var own = await db.Set<PositivePayLayout>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        string LayoutName(Guid? id) => id is { } x
            ? PositivePayWriter.Presets.FirstOrDefault(p => p.Id == x)?.Name ?? own.GetValueOrDefault(x) ?? PositivePayWriter.Presets[0].Name
            : PositivePayWriter.Presets[0].Name;
        return accounts.Select(a =>
        {
            var s = settings.GetValueOrDefault(a.Id);
            return new BankFileSettingsDto(a.Id, a.Name, a.BankName, CashSupport.Mask(a.AccountNumberLast4), a.ProtectedAccountNumber is not null, a.RoutingNumber,
                s is not null && s.CompanyIdentification.Length > 0, s?.ImmediateDestination, s?.ImmediateDestinationName, s?.ImmediateOrigin, s?.ImmediateOriginName,
                s?.CompanyIdentification, s?.CompanyName, s?.EntryDescription ?? "VENDOR PAY", s?.OriginatingDfi, s?.BalancedFile ?? false, s?.PositivePayLayoutId,
                LayoutName(s?.PositivePayLayoutId), s?.UpdatedBy, s?.UpdatedAtUtc);
        }).ToList();
    }

    private static PositivePayLayoutDto ToDto(PositivePayLayout l) =>
        new(l.Id, l.Name, l.Format == PositivePayFormat.Csv ? PositivePayFileFormat.Csv : PositivePayFileFormat.FixedWidth,
            JsonSerializer.Deserialize<List<PositivePayFieldSpec>>(l.FieldsJson, PositivePayWriter.Json) ?? [], l.IncludeHeader, l.IssueCode, l.VoidCode, false);

    private static object Snapshot(BankAchSettings s) => new
    {
        s.ImmediateDestination, s.ImmediateDestinationName, s.ImmediateOrigin, s.ImmediateOriginName, s.CompanyIdentification, s.CompanyName,
        s.EntryDescription, s.OriginatingDfi, s.BalancedFile, s.PositivePayLayoutId,
    };

    private static async Task<BankAccount> FindBankAccountAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Bank account", id);

    private static async Task<ApPaymentRun> FindRunAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<ApPaymentRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Payment run", id);

    private static string Digits(string? value, int length, string what)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length == length ? digits : throw new ArgumentException($"{what} must be {length} digits.");
    }

    private static string Text(string? value, string what, int max)
    {
        var text = NachaWriter.Clean(value?.Trim()) ?? "";
        if (text.Length == 0)
        {
            throw new ArgumentException($"{what} is required.");
        }

        return text.Length <= max ? text : throw new ArgumentException($"{what} can be at most {max} characters.");
    }

    private static string Code(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length <= 4 ? text : throw new ArgumentException("Issue and void codes are at most 4 characters.");
    }
}
