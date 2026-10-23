using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>
/// Reports that combine rows from several sheets, like Smartsheet reports. Every run is live and as the viewer: sheets they
/// can't see are left out (and counted), columns hidden from them on a sheet are blank, and each row links back to its item.
/// </summary>
public sealed class SheetReportService(
    IWorkbenchDbContextFactory dbFactory, TimeProvider clock, AccessControl access, SheetCalculator calculator) : ISheetReportService
{
    public const int MaxSheets = 50;
    public const int MaxColumns = 40;
    public const int MaxRows = 5_000;

    /// <summary>Report columns that aren't sheet columns.</summary>
    public const string SheetColumn = "Sheet";

    public async Task<IReadOnlyList<SheetReportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var me = access.Email;
        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reports = await db.Set<SheetReport>().AsNoTracking()
            .Where(r => r.OwnerEmail == me || (r.WorkspaceId != null && (visible == null || visible.Contains(r.WorkspaceId.Value))))
            .ToListAsync(cancellationToken);
        var workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);
        var admin = await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken);
        return reports.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => ToDto(r, workspaces, admin)).ToList();
    }

    public async Task<SheetReportDto?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindVisibleAsync(db, reportId, cancellationToken);
        return report is null ? null : ToDto(report, await WorkspaceNamesAsync(db, cancellationToken), await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken));
    }

    public async Task<SheetReportDto> CreateAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await ValidateAsync(request, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var report = new SheetReport { Name = request.Name.Trim(), OwnerEmail = access.Email, CreatedAtUtc = now };
        Apply(report, request, now);
        db.Set<SheetReport>().Add(report);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(report, await WorkspaceNamesAsync(db, cancellationToken), await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken));
    }

    public async Task<SheetReportDto> UpdateAsync(Guid reportId, SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await RequireEditableAsync(db, reportId, cancellationToken);
        await ValidateAsync(request, cancellationToken);
        Apply(report, request, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(report, await WorkspaceNamesAsync(db, cancellationToken), await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken));
    }

    public async Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await RequireEditableAsync(db, reportId, cancellationToken);
        db.Set<SheetReport>().Remove(report);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SheetReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindVisibleAsync(db, reportId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Report", reportId);
        var dto = ToDto(report, await WorkspaceNamesAsync(db, cancellationToken), await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken));
        return await RunAsync(db, dto, cancellationToken);
    }

    public async Task<SheetReportResultDto> PreviewAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await ValidateAsync(request, cancellationToken, checkSheets: false);
        var dto = new SheetReportDto(Guid.Empty, request.Name?.Trim() ?? "", access.Email, false, null, null, request.SheetIds.Distinct().ToList(),
            Clean(request.Columns), (request.Filters ?? []).ToList(), request.MatchAll, request.SortBy, request.SortDescending, true, clock.GetUtcNow().UtcDateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RunAsync(db, dto, cancellationToken);
    }

    private async Task<SheetReportResultDto> RunAsync(IWorkbenchDbContext db, SheetReportDto report, CancellationToken cancellationToken)
    {
        var sheets = new List<ComputedSheet>();
        var hiddenSheets = 0;
        foreach (var sheetId in report.SheetIds)
        {
            if (await calculator.ReadVisibleAsync(db, sheetId, cancellationToken) is { } sheet)
            {
                sheets.Add(sheet);
            }
            else
            {
                hiddenSheets++;
            }
        }

        var columns = report.Columns.Select(name => new SheetReportColumnDto(name, TypeOf(sheets, name))).ToList();
        var linkTitles = new Dictionary<Guid, ComputedSheet?>();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var rows = new List<(SheetReportRowDto Row, string? Sort, ColumnKind SortType)>();
        var truncated = false;
        var sortType = report.SortBy is { } sortBy ? TypeOf(sheets, sortBy) : ColumnKind.Text;
        foreach (var sheet in sheets)
        {
            var fields = Fields(sheet);
            foreach (var item in sheet.Outline())
            {
                string? Value(string name) => ValueOf(sheet, fields, item, name);
                var matches = report.Filters.Select(f => Matches(f, Value(f.Column), FieldType(sheet, fields, f.Column), today)).ToList();
                if (matches.Count > 0 && (report.MatchAll ? !matches.All(m => m) : !matches.Any(m => m)))
                {
                    continue;
                }

                if (rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }

                var cells = new List<string?>(report.Columns.Count);
                foreach (var name in report.Columns)
                {
                    var value = Value(name);
                    if (fields.TryGetValue(name, out var column) && column?.Type == ColumnType.LinkedRow && !string.IsNullOrEmpty(value))
                    {
                        value = await LinkTitlesAsync(db, column, value, linkTitles, cancellationToken);
                    }

                    cells.Add(value);
                }

                rows.Add((new SheetReportRowDto(sheet.Id, sheet.Name, item.Id, item.Title, cells), report.SortBy is null ? null : Value(report.SortBy), sortType));
            }
        }

        IEnumerable<(SheetReportRowDto Row, string? Sort, ColumnKind SortType)> ordered = rows;
        if (report.SortBy is not null)
        {
            var comparer = Comparer<(SheetReportRowDto Row, string? Sort, ColumnKind SortType)>.Create((a, b) => Compare(a.Sort, b.Sort, sortType));
            ordered = report.SortDescending ? rows.OrderByDescending(r => r, comparer) : rows.OrderBy(r => r, comparer);
        }

        return new SheetReportResultDto(report, columns, ordered.Select(r => r.Row).ToList(), hiddenSheets, truncated);
    }

    /// <summary>Report column names → the sheet's column with that name (first by order); built-ins map to null.</summary>
    private static Dictionary<string, Column?> Fields(ComputedSheet sheet)
    {
        var fields = new Dictionary<string, Column?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in sheet.Columns)
        {
            fields.TryAdd(column.Name.Trim(), column);
        }

        foreach (var builtIn in new[] { "Title", "Start", "Finish", SheetColumn })
        {
            fields.TryAdd(builtIn, null);
        }

        return fields;
    }

    private static string? ValueOf(ComputedSheet sheet, Dictionary<string, Column?> fields, Item item, string name)
    {
        if (!fields.TryGetValue(name.Trim(), out var column))
        {
            return null;
        }

        if (column is not null)
        {
            return sheet.ValueOf(item, column.Id.ToString());
        }

        return name.Trim().ToUpperInvariant() switch
        {
            "SHEET" => sheet.Name,
            var builtIn => sheet.ValueOf(item, builtIn.ToLowerInvariant()),
        };
    }

    private static ColumnKind FieldType(ComputedSheet sheet, Dictionary<string, Column?> fields, string name) =>
        fields.TryGetValue(name.Trim(), out var column) && column is not null
            ? sheet.TypeOf(column.Id.ToString()).ToContract()
            : name.Trim().ToUpperInvariant() is "START" or "FINISH" ? ColumnKind.Date : ColumnKind.Text;

    private static ColumnKind TypeOf(IReadOnlyList<ComputedSheet> sheets, string name)
    {
        foreach (var sheet in sheets)
        {
            var fields = Fields(sheet);
            if (fields.TryGetValue(name.Trim(), out var column) && column is not null)
            {
                return sheet.TypeOf(column.Id.ToString()) is ColumnType.LinkedRow ? ColumnKind.Text : sheet.TypeOf(column.Id.ToString()).ToContract();
            }
        }

        return name.Trim().ToUpperInvariant() is "START" or "FINISH" ? ColumnKind.Date : ColumnKind.Text;
    }

    /// <summary>A link cell as the titles of the rows it links to, those the user can see.</summary>
    private async Task<string?> LinkTitlesAsync(IWorkbenchDbContext db, Column column, string value, Dictionary<Guid, ComputedSheet?> targets, CancellationToken cancellationToken)
    {
        if (SheetLinks.TargetOf(column) is not { } targetId)
        {
            return null;
        }

        if (!targets.TryGetValue(targetId, out var target))
        {
            target = targets[targetId] = await calculator.ReadVisibleAsync(db, targetId, cancellationToken);
        }

        var titles = target is null ? [] : SheetLinks.Parse(value).Select(id => target.Find(id)?.Title).Where(t => t is not null).ToList();
        return titles.Count == 0 ? null : string.Join(", ", titles);
    }

    /// <summary>
    /// Whether a value passes a filter. Numbers and dates compare as such; date filters accept <c>today</c>, <c>today+7</c>
    /// and <c>today-30</c> so a saved report keeps meaning "this week".
    /// </summary>
    public static bool Matches(SheetReportFilterDto filter, string? value, ColumnKind type, DateOnly today)
    {
        var target = filter.Value?.Trim() ?? "";
        switch (filter.Operator)
        {
            case SheetReportOperator.IsBlank:
                return string.IsNullOrWhiteSpace(value);
            case SheetReportOperator.IsNotBlank:
                return !string.IsNullOrWhiteSpace(value);
            case SheetReportOperator.Contains:
                return value is not null && value.Contains(target, StringComparison.OrdinalIgnoreCase);
            case SheetReportOperator.DoesNotContain:
                return value is null || !value.Contains(target, StringComparison.OrdinalIgnoreCase);
            case SheetReportOperator.Is:
                return Equal(value, target, type, today);
            case SheetReportOperator.IsNot:
                return !Equal(value, target, type, today);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (TryDate(value, today, out var date) && TryDate(target, today, out var against))
        {
            return filter.Operator switch
            {
                SheetReportOperator.GreaterThan => date > against,
                SheetReportOperator.LessThan => date < against,
                SheetReportOperator.OnOrAfter => date >= against,
                SheetReportOperator.OnOrBefore => date <= against,
                _ => false,
            };
        }

        if (TryNumber(value, out var number) && TryNumber(target, out var limit))
        {
            return filter.Operator switch
            {
                SheetReportOperator.GreaterThan => number > limit,
                SheetReportOperator.LessThan => number < limit,
                SheetReportOperator.OnOrAfter => number >= limit,
                SheetReportOperator.OnOrBefore => number <= limit,
                _ => false,
            };
        }

        return false;
    }

    private static bool Equal(string? value, string target, ColumnKind type, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return target.Length == 0;
        }

        if (type == ColumnKind.Date && TryDate(value, today, out var a) && TryDate(target, today, out var b))
        {
            return a == b;
        }

        if (type is ColumnKind.Number or ColumnKind.Currency && TryNumber(value, out var x) && TryNumber(target, out var y))
        {
            return x == y;
        }

        if (type == ColumnKind.Checkbox)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) == target.ToLowerInvariant() is "true" or "yes" or "checked" or "1";
        }

        return string.Equals(value.Trim(), target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(string text, out decimal number) =>
        decimal.TryParse(text.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    private static bool TryDate(string text, DateOnly today, out DateOnly date)
    {
        var t = text.Trim().ToLowerInvariant().Replace(" ", "");
        if (t.StartsWith("today", StringComparison.Ordinal))
        {
            var rest = t[5..];
            if (rest.Length == 0)
            {
                date = today;
                return true;
            }

            if (int.TryParse(rest, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days))
            {
                date = today.AddDays(days);
                return true;
            }
        }

        return DateOnly.TryParseExact(text.Trim(), ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static int Compare(string? a, string? b, ColumnKind type)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            // Blanks last, whichever the direction is in the common case of ascending sorts.
            return string.IsNullOrWhiteSpace(a) == string.IsNullOrWhiteSpace(b) ? 0 : string.IsNullOrWhiteSpace(a) ? 1 : -1;
        }

        if (type is ColumnKind.Number or ColumnKind.Currency && TryNumber(a, out var x) && TryNumber(b, out var y))
        {
            return x.CompareTo(y);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ValidateAsync(SaveSheetReportRequest request, CancellationToken cancellationToken, bool checkSheets = true)
    {
        if (checkSheets && string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Give the report a name.");
        }

        if (request.Name?.Trim().Length > 200)
        {
            throw new ArgumentException("Keep the report's name under 200 characters.");
        }

        var sheets = request.SheetIds?.Distinct().ToList() ?? [];
        if (sheets.Count > MaxSheets)
        {
            throw new ArgumentException($"A report can combine up to {MaxSheets} sheets.");
        }

        if (Clean(request.Columns).Count > MaxColumns)
        {
            throw new ArgumentException($"A report can show up to {MaxColumns} columns.");
        }

        if (checkSheets)
        {
            foreach (var sheetId in sheets)
            {
                if (!await calculator.CanSeeSheetAsync(sheetId, cancellationToken))
                {
                    throw WorkbenchNotFoundException.For("Sheet", sheetId);
                }
            }
        }

        if (request.SharedWithWorkspaceId is { } workspaceId && await access.GetWorkspaceRoleAsync(workspaceId, cancellationToken) is null)
        {
            throw new ArgumentException("You can only share a report with a workspace you belong to.");
        }
    }

    private static List<string> Clean(IReadOnlyList<string>? columns) =>
        (columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Apply(SheetReport report, SaveSheetReportRequest request, DateTime now)
    {
        report.Name = request.Name.Trim();
        report.SheetIds = request.SheetIds.Distinct().ToList();
        report.Columns = Clean(request.Columns);
        report.Filters = (request.Filters ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Column))
            .Select(f => new SheetReportFilter { Column = f.Column.Trim(), Operator = f.Operator.ToString(), Value = f.Value?.Trim() })
            .ToList();
        report.MatchAll = request.MatchAll;
        report.SortBy = string.IsNullOrWhiteSpace(request.SortBy) ? null : request.SortBy.Trim();
        report.SortDescending = request.SortDescending;
        report.WorkspaceId = request.SharedWithWorkspaceId;
        report.UpdatedAtUtc = now;
    }

    private async Task<SheetReport?> FindVisibleAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken cancellationToken)
    {
        access.RequireSignedIn();
        var report = await db.Set<SheetReport>().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return null;
        }

        if (report.OwnerEmail == access.Email || await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken))
        {
            return report;
        }

        return report.WorkspaceId is { } workspaceId && await access.GetWorkspaceRoleAsync(workspaceId, cancellationToken) is not null ? report : null;
    }

    private async Task<SheetReport> RequireEditableAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken cancellationToken)
    {
        var report = await FindVisibleAsync(db, reportId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Report", reportId);
        if (report.OwnerEmail != access.Email && !await access.HasAsync(Permission.AdminAllWorkspaces, cancellationToken))
        {
            throw await access.DenyAsync("Only the person who made this report can change it.", cancellationToken);
        }

        return report;
    }

    private static async Task<Dictionary<Guid, string>> WorkspaceNamesAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);

    private SheetReportDto ToDto(SheetReport r, IReadOnlyDictionary<Guid, string> workspaces, bool admin) =>
        new(r.Id, r.Name, r.OwnerEmail, r.WorkspaceId is not null, r.WorkspaceId, r.WorkspaceId is { } w ? workspaces.GetValueOrDefault(w) : null,
            r.SheetIds.ToList(), r.Columns.ToList(),
            r.Filters.Select(f => new SheetReportFilterDto(f.Column, Enum.TryParse<SheetReportOperator>(f.Operator, out var op) ? op : SheetReportOperator.Is, f.Value)).ToList(),
            r.MatchAll, r.SortBy, r.SortDescending, r.OwnerEmail == access.Email || admin, r.UpdatedAtUtc);
}
