using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Reports;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Reports;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Integrations.Connectors;

/// <summary>Which dates a file connector covers, relative to when it runs.</summary>
internal static class ExportRange
{
    public const string Key = "range";

    public static ConnectorFieldDto Field => new(Key, "Dates to include", ConnectorFieldKind.Select, true, "Counted back from when the export runs (UTC).",
        [new("previous-day", "The previous day"), new("previous-week", "The previous week (Monday to Sunday)"),
         new("previous-month", "The previous month"), new("month-to-date", "This month so far")], "previous-month");

    public static (DateOnly From, DateOnly To) Resolve(string? range, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return range switch
        {
            "previous-day" => (today.AddDays(-1), today.AddDays(-1)),
            "previous-week" => PreviousWeek(today),
            "month-to-date" => (monthStart, today),
            _ => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
        };
    }

    private static (DateOnly, DateOnly) PreviousWeek(DateOnly today)
    {
        var sinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-sinceMonday);
        return (thisMonday.AddDays(-7), thisMonday.AddDays(-1));
    }
}

/// <summary>
/// QuickBooks-style general journal export for a company (a subsidiary that keeps its own books): posted journal lines of that
/// company in the date range, as CSV or as an IIF file QuickBooks Desktop imports (one GENERAL JOURNAL transaction per entry,
/// debits positive, credits negative, the project code as the class). Accounts map by an optional "Workbench number = QuickBooks
/// account" list; otherwise "number name". The file goes to Workbench's run log and, if chosen, an SFTP server.
/// </summary>
public sealed class GlExportConnector(IWorkbenchDbContextFactory dbFactory, ISftpUploader sftp, OutboundUrlGuard guard) : IConnector
{
    public string Kind => "gl-export";
    public string Name => "General ledger export (QuickBooks)";
    public string Description => "A company's posted journal as a CSV or QuickBooks IIF file, for a subsidiary that keeps its own books.";
    public ConnectorMode Mode => ConnectorMode.Schedule;

    public async Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var companies = await db.Set<Company>().AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Id, c.Code, c.LegalName }).ToListAsync(cancellationToken);
        return
        [
            new("company", "Company", ConnectorFieldKind.Select, true, "Only this company's journal lines are exported.",
                companies.Select(c => new ConnectorOptionDto(c.Id.ToString(), $"{c.Code} {c.LegalName}")).ToList()),
            new("format", "File format", ConnectorFieldKind.Select, true, null,
                [new("iif", "QuickBooks Desktop (.iif)"), new("csv", "CSV (.csv)")], "iif"),
            ExportRange.Field,
            new("accountMap", "Account names in QuickBooks", ConnectorFieldKind.Text, false,
                "Optional. One per line or separated by semicolons: 1010=Checking; 4000=Services revenue. Unlisted accounts use \"number name\"."),
            .. FileDestination.Fields,
        ];
    }

    public async Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        var companyId = Guid.TryParse(config.Required("company", "The company"), out var id) ? id : throw new ArgumentException("Choose a company.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Company>().AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            throw new ArgumentException("That company doesn't exist.");
        }

        if (config.Get("format") is not (null or "iif" or "csv"))
        {
            throw new ArgumentException("Choose IIF or CSV.");
        }

        AccountMap(config.Get("accountMap"));
        await FileDestination.ValidateAsync(config, guard, cancellationToken);
    }

    public async Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var companyId = Guid.Parse(config.Required("company", "The company"));
        var iif = (config.Get("format") ?? "iif") == "iif";
        var (from, to) = ExportRange.Resolve(config.Get(ExportRange.Key), context.UtcNow);
        var map = AccountMap(config.Get("accountMap"));

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var company = await db.Set<Company>().AsNoTracking().FirstAsync(c => c.Id == companyId, cancellationToken);
        var rows = await db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.CompanyId == companyId)
            .Join(db.Set<JournalEntry>().Where(e => e.Status != JournalStatus.Draft && e.Date >= from && e.Date <= to), l => l.JournalEntryId, e => e.Id, (l, e) => new { Line = l, Entry = e })
            .ToListAsync(cancellationToken);
        var accountIds = rows.Select(r => r.Line.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var projectIds = rows.Where(r => r.Line.ProjectId != null).Select(r => r.Line.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);

        var lines = rows
            .OrderBy(r => r.Entry.Date).ThenBy(r => r.Entry.Number, StringComparer.Ordinal).ThenBy(r => r.Line.LineNumber)
            .Select(r => new ExportLine(r.Entry.Date, r.Entry.Number, r.Entry.Description, r.Entry.Source.ToString(),
                AccountName(accounts[r.Line.AccountId], map), accounts[r.Line.AccountId].Number, r.Line.Debit, r.Line.Credit, r.Line.Memo,
                r.Line.ProjectId is { } p ? projects.GetValueOrDefault(p) : null))
            .ToList();

        var name = $"gl-{Safe(company.Code)}-{from:yyyyMMdd}-{to:yyyyMMdd}.{(iif ? "iif" : "csv")}";
        var file = new ConnectorFile(name, iif ? "text/plain" : "text/csv", Encoding.UTF8.GetBytes(iif ? Iif(lines) : Csv(lines)));
        var sent = await FileDestination.SendAsync(config, file, sftp, guard, cancellationToken);
        var entries = lines.Select(l => l.Entry).Distinct().Count();
        return new ConnectorResult(true, $"{entries} journal entries ({lines.Count} lines) for {company.Code}, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}; {sent}.", entries, file);
    }

    internal sealed record ExportLine(DateOnly Date, string Entry, string Description, string Source, string Account, string AccountNumber,
        decimal Debit, decimal Credit, string? Memo, string? ProjectCode);

    internal static string Iif(IReadOnlyList<ExportLine> lines)
    {
        var sb = new StringBuilder();
        sb.Append("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!ENDTRNS\r\n");
        foreach (var entry in lines.GroupBy(l => l.Entry))
        {
            var first = true;
            foreach (var line in entry)
            {
                sb.Append(first ? "TRNS" : "SPL").Append("\t\tGENERAL JOURNAL\t")
                    .Append(line.Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Tab(line.Account)).Append('\t').Append(Tab(line.ProjectCode)).Append('\t')
                    .Append((line.Debit - line.Credit).ToString("0.00", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Tab(line.Entry)).Append('\t').Append(Tab(line.Memo ?? line.Description)).Append("\r\n");
                first = false;
            }

            sb.Append("ENDTRNS\r\n");
        }

        return sb.ToString();
    }

    internal static string Csv(IReadOnlyList<ExportLine> lines)
    {
        var sb = new StringBuilder("Date,Entry,Source,Account number,Account,Debit,Credit,Memo,Class (project)\r\n");
        foreach (var l in lines)
        {
            sb.AppendJoin(',', [
                l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CsvCell(l.Entry), CsvCell(l.Source), CsvCell(l.AccountNumber), CsvCell(l.Account),
                l.Debit.ToString("0.00", CultureInfo.InvariantCulture), l.Credit.ToString("0.00", CultureInfo.InvariantCulture), CsvCell(l.Memo ?? l.Description), CsvCell(l.ProjectCode),
            ]).Append("\r\n");
        }

        return sb.ToString();
    }

    internal static Dictionary<string, string> AccountMap(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (text ?? "").Split(['\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
            {
                throw new ArgumentException($"\"{part}\" isn't a mapping. Write it as 1010=Checking.");
            }

            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }

        return map;
    }

    private static string AccountName(Account account, Dictionary<string, string> map) =>
        map.TryGetValue(account.Number, out var name) ? name : $"{account.Number} {account.Name}";

    // IIF is tab-separated with no quoting: tabs and line breaks inside a value become spaces.
    private static string Tab(string? value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // CSV cells are quoted when needed; a leading =, +, - or @ is neutralized (spreadsheet formula injection).
    internal static string CsvCell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0]))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    private static string Safe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}

/// <summary>
/// Exports chosen saved reports as CSV or Excel on a schedule, to the run log and optionally SFTP. Scheduled runs act as the
/// administrator who set the connector up (the report shows what they may see), and stop when that account is deactivated;
/// "Run now" runs as whoever pressed it. Each file records an <c>export.report</c> security event, as on the reports page.
/// </summary>
public sealed class ReportExportConnector(IServiceProvider services, ISftpUploader sftp, OutboundUrlGuard guard) : IConnector
{
    public string Kind => "report-export";
    public string Name => "Report export (CSV or SFTP)";
    public string Description => "Saved reports as CSV or Excel files on a schedule, kept in Workbench and optionally uploaded over SFTP.";
    public ConnectorMode Mode => ConnectorMode.Schedule;

    public async Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken)
    {
        var reports = await services.GetRequiredService<ISavedReportService>().ListAsync(cancellationToken);
        return
        [
            new("reports", "Saved reports", ConnectorFieldKind.MultiSelect, true, "Each report becomes its own file. Reports use their saved filters and date range.",
                reports.Select(r => new ConnectorOptionDto(r.Id.ToString(), r.Name)).ToList()),
            new("format", "File format", ConnectorFieldKind.Select, true, null, [new("csv", "CSV (.csv)"), new("xlsx", "Excel (.xlsx)")], "csv"),
            .. FileDestination.Fields,
        ];
    }

    public async Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        var ids = ReportIds(config);
        var visible = (await services.GetRequiredService<ISavedReportService>().ListAsync(cancellationToken)).Select(r => r.Id).ToHashSet();
        if (ids.FirstOrDefault(id => !visible.Contains(id)) is var missing && missing != Guid.Empty)
        {
            throw new ArgumentException("One of the chosen reports doesn't exist or isn't shared with you.");
        }

        if (config.Get("format") is not (null or "csv" or "xlsx"))
        {
            throw new ArgumentException("Choose CSV or Excel.");
        }

        await FileDestination.ValidateAsync(config, guard, cancellationToken);
    }

    public async Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
    {
        var ids = ReportIds(context.Config);
        var format = context.Config.Get("format") == "xlsx" ? ReportFileFormat.Xlsx : ReportFileFormat.Csv;
        var files = new List<ConnectorFile>();
        if (context.Trigger == ConnectorTrigger.Schedule)
        {
            await using var db = await services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(cancellationToken);
            var owner = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == context.Connector.CreatedBy, cancellationToken);
            if (owner is not { IsActive: true })
            {
                return new ConnectorResult(false, "Stopped: the account that set up this connector is deactivated. Edit and save it as an active administrator.");
            }

            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            using (ReportRunAs.Enter(scope.ServiceProvider, owner.Email, owner.DisplayName))
            {
                await ExportAllAsync(scope.ServiceProvider.GetRequiredService<ISavedReportService>(), ids, format, files, cancellationToken);
            }
        }
        else
        {
            await ExportAllAsync(services.GetRequiredService<ISavedReportService>(), ids, format, files, cancellationToken);
        }

        // One file per report; several are sent one by one and the log keeps the first (a zip would hide the names).
        var sent = new List<string>();
        foreach (var file in files)
        {
            sent.Add(await FileDestination.SendAsync(context.Config, file, sftp, guard, cancellationToken));
        }

        var stamp = context.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var kept = files.Count == 1 ? files[0] with { FileName = Stamp(files[0].FileName, stamp) } : Combine(files, stamp);
        return new ConnectorResult(true, $"{files.Count} report file{(files.Count == 1 ? "" : "s")}: {string.Join(", ", files.Select(f => f.FileName))}; {sent.FirstOrDefault() ?? "kept in Workbench"}.",
            files.Count, kept);
    }

    private static async Task ExportAllAsync(ISavedReportService reports, IReadOnlyList<Guid> ids, ReportFileFormat format, List<ConnectorFile> files, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            var file = await reports.ExportAsync(id, format, ct);
            files.Add(new ConnectorFile(file.FileName, file.ContentType, file.Content));
        }
    }

    private static IReadOnlyList<Guid> ReportIds(ConnectorConfig config)
    {
        var ids = config.Required("reports", "At least one saved report")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : throw new ArgumentException("Choose saved reports from the list."))
            .Distinct().ToList();
        return ids.Count == 0 ? throw new ArgumentException("Choose at least one saved report.") : ids;
    }

    private static string Stamp(string fileName, string stamp) =>
        $"{Path.GetFileNameWithoutExtension(fileName)}-{stamp}{Path.GetExtension(fileName)}";

    // Several CSV reports are kept as one text file with a heading per report; Excel files keep the first workbook.
    private static ConnectorFile Combine(List<ConnectorFile> files, string stamp)
    {
        if (files.Any(f => f.ContentType != "text/csv" && !f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            return files[0] with { FileName = Stamp(files[0].FileName, stamp) };
        }

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.Append("# ").Append(f.FileName).Append("\r\n").Append(Encoding.UTF8.GetString(f.Content).TrimStart('﻿')).Append("\r\n");
        }

        return new ConnectorFile($"reports-{stamp}.csv", "text/csv", Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
