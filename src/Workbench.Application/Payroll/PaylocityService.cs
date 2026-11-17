using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>
/// The file-based Paylocity exchange: the mapping Finance checks before an export (company codes, cost centers, employee
/// ids), the export history with checksums, and the roster import settings. Needs no Paylocity API access.
/// </summary>
public sealed class PaylocityService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, PaylocityRosterImporter roster, TimeProvider clock) : IPaylocityService
{
    public const string CostCenterAudit = nameof(PaylocityCostCenterMapping);
    public const string RosterSettingsAudit = nameof(PaylocityRosterSetting);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<PaylocityMappingDto> GetMappingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BuildMappingAsync(db, cancellationToken);
    }

    private static async Task<PaylocityMappingDto> BuildMappingAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => g.ProviderKey == PaylocityPayrollProvider.ProviderKey).OrderBy(g => g.Code).ToListAsync(ct);
        var groupIds = groups.Select(g => g.Id).ToList();
        var profiles = await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.PayGroupId != null && groupIds.Contains(p.PayGroupId.Value)).ToListAsync(ct);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().OrderBy(o => o.Code).ToListAsync(ct);
        var maps = await db.Set<PaylocityCostCenterMapping>().AsNoTracking().ToDictionaryAsync(m => m.OrgUnitId, m => m.Code, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var active = profiles.Where(p => employees.TryGetValue(p.EmployeeId, out var e) && (e.TerminationDate is null || e.TerminationDate >= today)).ToList();
        var companies = groups.Select(g => new PaylocityCompanyRow(g.Id, g.Code, g.Name, g.CompanyCode, active.Count(p => p.PayGroupId == g.Id), g.IsActive)).ToList();

        string SentAs(OrgUnit o) => maps.GetValueOrDefault(o.Id) ?? o.Code;
        var costCenters = orgs.Select(o => new PaylocityCostCenterRow(o.Id, o.Code, o.Name, maps.GetValueOrDefault(o.Id), SentAs(o),
            active.Count(p => employees[p.EmployeeId].OrgUnitId == o.Id))).ToList();

        var dupes = active.Where(p => !string.IsNullOrWhiteSpace(p.ProviderEmployeeId)).GroupBy(p => p.ProviderEmployeeId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.EmployeeId).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codeOf = groups.ToDictionary(g => g.Id, g => g.Code);
        var rows = new List<PaylocityEmployeeRow>();
        foreach (var p in active)
        {
            var e = employees[p.EmployeeId];
            var id = p.ProviderEmployeeId?.Trim();
            var (status, note) = string.IsNullOrEmpty(id) ? ("NoId", "No Paylocity employee id: this person's hours are left out of the export.")
                : dupes.Contains(id) ? ("DuplicateId", $"Paylocity id {id} is on more than one employee.")
                : ("Ready", (string?)null);
            var cc = e.OrgUnitId is { } o && orgs.FirstOrDefault(x => x.Id == o) is { } unit ? SentAs(unit) : null;
            if (status == "Ready" && cc is null)
            {
                note = "No org unit, so no cost center is sent.";
            }

            rows.Add(new PaylocityEmployeeRow(e.Id, e.EmployeeNumber, e.DisplayName, codeOf.GetValueOrDefault(p.PayGroupId ?? Guid.Empty), id, cc, status, note));
        }

        var problems = new List<string>();
        if (groups.Count == 0)
        {
            problems.Add("No pay group uses Paylocity yet. Add one under Payroll › Pay groups.");
        }

        foreach (var g in groups.Where(g => string.IsNullOrWhiteSpace(g.CompanyCode)))
        {
            problems.Add($"Pay group {g.Code} has no Paylocity company code.");
        }

        var noId = rows.Count(r => r.Status == "NoId");
        if (noId > 0)
        {
            problems.Add($"{noId} employee(s) have no Paylocity id.");
        }

        if (dupes.Count > 0)
        {
            problems.Add($"Duplicate Paylocity id(s): {string.Join(", ", dupes.Order(StringComparer.Ordinal))}.");
        }

        return new PaylocityMappingDto(companies, costCenters, rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList(), problems);
    }

    public async Task<PaylocityMappingDto> SaveCostCentersAsync(IReadOnlyList<SaveCostCenterMapping> mappings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var existing = await db.Set<PaylocityCostCenterMapping>().ToListAsync(cancellationToken);
        var units = await db.Set<OrgUnit>().AsNoTracking().Select(o => o.Id).ToListAsync(cancellationToken);
        foreach (var m in mappings.GroupBy(m => m.OrgUnitId).Select(g => g.Last()))
        {
            if (!units.Contains(m.OrgUnitId))
            {
                throw WorkbenchNotFoundException.For("Org unit", m.OrgUnitId);
            }

            var code = m.Code?.Trim();
            if (code is { Length: > 50 } || code is not null && code.Any(char.IsControl))
            {
                throw new ArgumentException("A cost center code is at most 50 characters, with no control characters.");
            }

            var row = existing.FirstOrDefault(x => x.OrgUnitId == m.OrgUnitId);
            if (string.IsNullOrEmpty(code))
            {
                if (row is not null)
                {
                    audit.Record(db, CostCenterAudit, row.Id, "Cost center mapping removed", new { row.Code });
                    db.Set<PaylocityCostCenterMapping>().Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                row = new PaylocityCostCenterMapping { OrgUnitId = m.OrgUnitId, Code = code, UpdatedAtUtc = UtcNow, UpdatedBy = audit.Actor };
                db.Set<PaylocityCostCenterMapping>().Add(row);
                audit.Record(db, CostCenterAudit, row.Id, "Cost center mapped", after: new { m.OrgUnitId, code });
            }
            else if (row.Code != code)
            {
                audit.Record(db, CostCenterAudit, row.Id, "Cost center mapping changed", new { row.Code }, new { code });
                row.Code = code;
                row.UpdatedAtUtc = UtcNow;
                row.UpdatedBy = audit.Actor;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await BuildMappingAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<PayrollExportRecordDto>> ListExportsAsync(Guid? payGroupId = null, Guid? payPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<PayrollExportRecord>().AsNoTracking()
            .Where(x => (payGroupId == null || x.PayGroupId == payGroupId) && (payPeriodId == null || x.PayPeriodId == payPeriodId))
            .Select(x => new { x.Id, x.RunId, x.PayGroupId, x.PayPeriodId, x.Sequence, x.Method, x.FileName, x.Sha256, x.LineCount, x.EmployeeCount, x.TotalHours, x.FlaggedCount, x.IsReExport, x.Note, x.ExportedBy, x.ExportedAtUtc })
            .ToListAsync(cancellationToken);
        var groups = await db.Set<PayGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Code, cancellationToken);
        var periods = await db.Set<PayPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => (p.Start, p.End), cancellationToken);
        return rows.OrderByDescending(x => x.ExportedAtUtc).ThenByDescending(x => x.Sequence)
            .Select(x => new PayrollExportRecordDto(x.Id, x.RunId, x.PayGroupId, groups.GetValueOrDefault(x.PayGroupId, ""), periods[x.PayPeriodId].Start, periods[x.PayPeriodId].End,
                x.Sequence, x.Method, x.FileName, x.Sha256, x.LineCount, x.EmployeeCount, x.TotalHours, x.FlaggedCount, x.IsReExport, x.Note, x.ExportedBy, x.ExportedAtUtc))
            .ToList();
    }

    public async Task<PayrollExportFileDto> GetExportFileAsync(Guid exportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<PayrollExportRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Payroll export", exportId);
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Content))).ToLowerInvariant();
        if (actual != row.Sha256)
        {
            throw new InvalidOperationException("The stored export no longer matches its checksum. Don't use it; export the period again with a note.");
        }

        return new PayrollExportFileDto(row.FileName, "text/csv", row.Content, row.Sha256);
    }

    public async Task<PaylocityRosterSettingsDto> GetRosterSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<PaylocityRosterSetting>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return new PaylocityRosterSettingsDto(PaylocityRosterImporter.ParseOwned(row?.OwnedFields), row?.UpdatedAtUtc, row?.UpdatedBy);
    }

    public async Task<PaylocityRosterSettingsDto> SaveRosterSettingsAsync(SavePaylocityRosterSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unknown = request.OwnedFields.Where(k => PaylocityRosterFields.All.All(a => a.Key != k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown field(s): {string.Join(", ", unknown)}.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<PaylocityRosterSetting>().FirstOrDefaultAsync(cancellationToken);
        var before = row?.OwnedFields;
        if (row is null)
        {
            row = new PaylocityRosterSetting();
            db.Set<PaylocityRosterSetting>().Add(row);
        }

        row.OwnedFields = string.Join(';', request.OwnedFields.Distinct());
        row.UpdatedAtUtc = UtcNow;
        row.UpdatedBy = audit.Actor;
        audit.Record(db, RosterSettingsAudit, row.Id, "Workbench-owned roster fields changed", new { OwnedFields = before }, new { row.OwnedFields });
        await db.SaveChangesAsync(cancellationToken);
        return new PaylocityRosterSettingsDto(PaylocityRosterImporter.ParseOwned(row.OwnedFields), row.UpdatedAtUtc, row.UpdatedBy);
    }

    public Task<PaylocityRosterResultDto> ImportRosterAsync(PaylocityRosterRequest request, CancellationToken cancellationToken = default) =>
        roster.ImportAsync(request, cancellationToken);
}

public static class PaylocityRegistration
{
    /// <summary>The file-based Paylocity exchange (mapping, export history, roster import). Above AddWorkbenchIdentity.</summary>
    public static IServiceCollection AddWorkbenchPaylocity(this IServiceCollection services)
    {
        services.AddScoped<PaylocityRosterImporter>();
        services.AddScoped<IPaylocityService, PaylocityService>();
        return services;
    }
}
