using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

public static class PayrollSecurityEvents
{
    public const string PayRateRevealed = "payroll.pay_rate_revealed";
    public const string PayRateChanged = "payroll.pay_rate_changed";
    public const string HoursExported = "export.payroll_hours";
    public const string ProviderConnected = "payroll.provider_connected";
}

/// <summary>The registered provider adapters by key.</summary>
public sealed class PayrollProviders(IEnumerable<IPayrollProvider> providers)
{
    public IReadOnlyList<IPayrollProvider> All { get; } = providers.ToList();

    public IPayrollProvider? Find(string key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public IPayrollProvider Get(string key) => Find(key) ?? throw new ArgumentException($"There's no payroll provider \"{key}\".");

    public string NameOf(string key) => Find(key)?.Name ?? key;
}

/// <summary>Earning code mappings: <c>REG</c>, <c>OT</c> and leave charge codes to the provider's earning codes.</summary>
public static class EarningCodes
{
    public const string Regular = "REG";
    public const string Overtime = "OT";

    public static readonly IReadOnlyList<EarningCodeMapping> Defaults =
    [
        new(Regular, "REG"),
        new(Overtime, "OT"),
        new("LV.PTO", "PTO"),
        new("LV.HOL", "HOL"),
        new("LV.SICK", "SICK"),
    ];

    public static IReadOnlyList<EarningCodeMapping> Parse(string? stored, IReadOnlyList<EarningCodeMapping>? defaults = null)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return defaults ?? Defaults;
        }

        return stored.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => new EarningCodeMapping(p[0].Trim(), p[1].Trim()))
            .ToList();
    }

    public static string Format(IEnumerable<EarningCodeMapping> mappings) => string.Join(';', mappings.Select(m => $"{m.Category}={m.EarningCode}"));

    /// <summary>The earning code for a category; unmapped leave codes fall back to the part after <c>LV.</c>.</summary>
    public static (string Code, bool Mapped) Resolve(IReadOnlyList<EarningCodeMapping> mappings, string category)
    {
        var mapped = mappings.FirstOrDefault(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase));
        if (mapped is not null && mapped.EarningCode.Length > 0)
        {
            return (mapped.EarningCode, true);
        }

        var fallback = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        return (fallback.ToUpperInvariant(), false);
    }
}

public sealed class PayGroupService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, PayrollProviders providers, TimeProvider clock) : IPayGroupService
{
    public const string AuditEntityType = nameof(PayGroup);

    public async Task<IReadOnlyList<PayGroupDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var groups = await db.Set<PayGroup>().AsNoTracking().OrderBy(g => g.Code).ToListAsync(cancellationToken);
        var counts = await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.PayGroupId != null)
            .GroupBy(p => p.PayGroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return groups.Select(g => ToDto(g, counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<PayGroupDto> GetAsync(Guid payGroupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = await FindAsync(db, payGroupId, cancellationToken);
        return ToDto(group, await db.Set<EmployeePayProfile>().CountAsync(p => p.PayGroupId == group.Id, cancellationToken));
    }

    public async Task<PayGroupDto> CreateAsync(SavePayGroupRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = new PayGroup { Code = "", Name = "", ProviderKey = "", CompanyCode = "", CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        await ApplyAsync(db, group, request, cancellationToken);
        db.Set<PayGroup>().Add(group);
        audit.Record(db, AuditEntityType, group.Id, "Created", after: Snapshot(group));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(group, 0);
    }

    public async Task<PayGroupDto> UpdateAsync(Guid payGroupId, SavePayGroupRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = await db.Set<PayGroup>().FirstOrDefaultAsync(g => g.Id == payGroupId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Pay group", payGroupId);
        var before = Snapshot(group);
        var calendarChanged = group.Frequency != (PayFrequency)(int)request.Frequency || group.AnchorDate != request.AnchorDate;
        if (calendarChanged && await db.Set<PayrollRun>().AnyAsync(r => r.PayGroupId == group.Id, cancellationToken))
        {
            throw new ArgumentException("This pay group has payroll runs, so its frequency and anchor date can't change. Create a new pay group instead.");
        }

        await ApplyAsync(db, group, request, cancellationToken);
        if (calendarChanged)
        {
            db.Set<PayPeriod>().RemoveRange(await db.Set<PayPeriod>().Where(p => p.PayGroupId == group.Id).ToListAsync(cancellationToken));
        }

        audit.Record(db, AuditEntityType, group.Id, "Updated", before, Snapshot(group));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(group, await db.Set<EmployeePayProfile>().CountAsync(p => p.PayGroupId == group.Id, cancellationToken));
    }

    public async Task<int> GeneratePeriodsAsync(Guid payGroupId, int year, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100)
        {
            throw new ArgumentException("Choose a year between 2000 and 2100.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = await FindAsync(db, payGroupId, cancellationToken);
        var added = await AddPeriodsAsync(db, group, year, cancellationToken);
        if (added > 0)
        {
            audit.Record(db, AuditEntityType, group.Id, "Periods generated", after: new { Year = year, Added = added });
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    /// <summary>Adds the year's periods that don't exist yet (not saved). Shared with the seed.</summary>
    public static async Task<int> AddPeriodsAsync(IWorkbenchDbContext db, PayGroup group, int year, CancellationToken ct)
    {
        var existing = (await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == group.Id).Select(p => p.Start).ToListAsync(ct)).ToHashSet();
        var periods = PayCalendar.Generate(group.Frequency, group.AnchorDate, group.CheckDateOffsetDays, year);
        var added = 0;
        for (var i = 0; i < periods.Count; i++)
        {
            if (existing.Contains(periods[i].Start))
            {
                continue;
            }

            db.Set<PayPeriod>().Add(new PayPeriod
            {
                PayGroupId = group.Id,
                Year = year,
                Number = i + 1,
                Start = periods[i].Start,
                End = periods[i].End,
                CheckDate = periods[i].CheckDate,
            });
            added++;
        }

        return added;
    }

    public async Task<IReadOnlyList<PayPeriodDto>> ListPeriodsAsync(Guid payGroupId, int? year = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindAsync(db, payGroupId, cancellationToken);
        var periods = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == payGroupId && (year == null || p.Year == year))
            .OrderBy(p => p.Start).ToListAsync(cancellationToken);
        var runs = await db.Set<PayrollRun>().AsNoTracking().Where(r => r.PayGroupId == payGroupId).ToListAsync(cancellationToken);
        return periods.Select(p =>
        {
            var run = runs.Where(r => r.PayPeriodId == p.Id).OrderBy(r => r.Status == PayrollRunStatus.Voided).ThenByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            return new PayPeriodDto(p.Id, p.PayGroupId, p.Year, p.Number, p.Start, p.End, p.CheckDate, run?.Id, run is null ? null : (PayrollRunState)(int)run.Status);
        }).ToList();
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, PayGroup group, SavePayGroupRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var code = r.Code.Required("Code").ToUpperInvariant();
        if (code.Length > 32)
        {
            throw new ArgumentException("The code can be at most 32 characters.");
        }

        if (await db.Set<PayGroup>().AnyAsync(g => g.Code == code && g.Id != group.Id, ct))
        {
            throw new ArgumentException($"A pay group with code {code} already exists.");
        }

        var provider = providers.Get(r.ProviderKey.Required("Provider"));
        if (!provider.IsSupported)
        {
            throw new ArgumentException($"{provider.Name} is coming soon; choose a supported provider.");
        }

        if (r.CheckDateOffsetDays is < 0 or > 31)
        {
            throw new ArgumentException("The check date must be 0 to 31 days after the period end.");
        }

        group.Code = code;
        group.Name = r.Name.Required("Name");
        group.Frequency = (PayFrequency)(int)r.Frequency;
        group.ProviderKey = provider.Key;
        group.CompanyCode = r.CompanyCode.Required("Company code");
        group.AnchorDate = r.AnchorDate;
        group.CheckDateOffsetDays = r.CheckDateOffsetDays;
        group.IsActive = r.IsActive;
    }

    private static async Task<PayGroup> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Pay group", id);

    private PayGroupDto ToDto(PayGroup g, int employees) => new(g.Id, g.Code, g.Name, (PayFrequencyKind)(int)g.Frequency, g.ProviderKey, providers.NameOf(g.ProviderKey),
        g.CompanyCode, g.AnchorDate, g.CheckDateOffsetDays, g.IsActive, employees);

    private static object Snapshot(PayGroup g) => new { g.Code, g.Name, Frequency = g.Frequency.ToString(), g.ProviderKey, g.CompanyCode, g.AnchorDate, g.CheckDateOffsetDays, g.IsActive };
}

/// <summary>
/// Employee payroll profiles. Pay rates are masked in every response except <see cref="RevealAsync"/>, which records a
/// security event; rate changes record one too. Audit rows say a rate changed, never what it is.
/// </summary>
public sealed class PayrollProfileService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock, SecurityEventLog? securityLog = null, LaborRateChanges? laborRates = null) : IPayrollProfileService
{
    public const string AuditEntityType = nameof(EmployeePayProfile);

    public async Task<IReadOnlyList<PayProfileDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().OrderBy(e => e.DisplayName).ToListAsync(cancellationToken);
        var profiles = await db.Set<EmployeePayProfile>().AsNoTracking().ToDictionaryAsync(p => p.EmployeeId, cancellationToken);
        var history = await PayRates.HistoryAsync(db, profiles.Values.ToList(), cancellationToken);
        var groups = await db.Set<PayGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Code, cancellationToken);
        return employees.Select(e => ToDto(e, profiles.GetValueOrDefault(e.Id), history.GetValueOrDefault(e.Id) ?? [], groups, Today, reveal: false, withHistory: false)).ToList();
    }

    public async Task<PayProfileDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LoadAsync(db, employeeId, reveal: false, cancellationToken);
    }

    public async Task<PayProfileDto> RevealAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dto = await LoadAsync(db, employeeId, reveal: true, cancellationToken);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(PayrollSecurityEvents.PayRateRevealed, $"employee {dto.EmployeeNumber}", detail: "Pay rate revealed", cancellationToken: cancellationToken);
        }

        return dto;
    }

    public async Task<PayProfileDto> SaveAsync(Guid employeeId, SavePayProfileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        if (request.PayGroupId is { } groupId && !await db.Set<PayGroup>().AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new ArgumentException("That pay group doesn't exist.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.ProviderEmployeeId) ? null : request.ProviderEmployeeId.Trim();
        if (providerId?.Length > 64)
        {
            throw new ArgumentException("The provider employee id can be at most 64 characters.");
        }

        if (providerId is not null && await db.Set<EmployeePayProfile>().AnyAsync(p => p.ProviderEmployeeId == providerId && p.EmployeeId != employeeId
                && p.PayGroupId == request.PayGroupId, cancellationToken))
        {
            throw new ArgumentException($"Provider employee id {providerId} is already used by another employee in this pay group.");
        }

        if (request.AnnualSalary is < 0 or > 10_000_000 || request.HourlyRate is < 0 or > 10_000)
        {
            throw new ArgumentException("Enter a salary up to 10,000,000 or an hourly rate up to 10,000.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > 500)
        {
            throw new ArgumentException("The reason can be at most 500 characters.");
        }

        var profile = await db.Set<EmployeePayProfile>().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken);
        var isNew = profile is null;
        profile ??= new EmployeePayProfile { EmployeeId = employeeId, EffectiveFrom = employee.HireDate };
        var before = isNew ? null : Snapshot(profile);
        var history = isNew ? [] : await PayRates.HistoryAsync(db, profile, cancellationToken);
        var payType = (PayType)(int)request.PayType;
        var now = clock.GetUtcNow().UtcDateTime;
        EmployeePayRate? added = null;
        if (request.AnnualSalary is not null || request.HourlyRate is not null || payType != profile.PayType || isNew)
        {
            var salary = payType == PayType.Salaried ? request.AnnualSalary ?? (profile.PayType == payType ? profile.AnnualSalary : null) : null;
            var hourly = payType == PayType.Hourly ? request.HourlyRate ?? (profile.PayType == payType ? profile.HourlyRate : null) : null;
            if (payType == PayType.Salaried && salary is null)
            {
                throw new ArgumentException("A salaried employee needs an annual salary.");
            }

            if (payType == PayType.Hourly && hourly is null)
            {
                throw new ArgumentException("An hourly employee needs an hourly pay rate.");
            }

            var effective = request.EffectiveFrom ?? (isNew ? employee.HireDate : Today);
            if (effective < employee.HireDate)
            {
                throw new ArgumentException($"A pay rate can't start before the hire date ({employee.HireDate:yyyy-MM-dd}).");
            }

            var inEffect = PayRates.InEffect(history, effective);
            if (isNew || inEffect is null || salary != inEffect.AnnualSalary || hourly != inEffect.HourlyRate || payType != inEffect.PayType)
            {
                // Rates are append-only: the change is a new row from its effective date. A profile saved before the
                // history existed gets its old rate stored first (from the hire date), so earlier periods keep it.
                if (!isNew && !await db.Set<EmployeePayRate>().AnyAsync(r => r.EmployeeId == employeeId, cancellationToken))
                {
                    foreach (var derived in history)
                    {
                        db.Set<EmployeePayRate>().Add(derived);
                    }
                }

                added = new EmployeePayRate
                {
                    EmployeeId = employeeId,
                    EffectiveFrom = effective,
                    PayType = payType,
                    AnnualSalary = salary,
                    HourlyRate = hourly,
                    Reason = reason ?? (isNew ? "Initial pay rate" : null),
                    CreatedBy = audit.Actor,
                    CreatedAtUtc = now,
                };
                db.Set<EmployeePayRate>().Add(added);
                PayRates.Mirror(profile, PayRates.Order([.. history, added]));
            }
        }

        profile.PayGroupId = request.PayGroupId;
        profile.ProviderEmployeeId = providerId;
        if (request.EmploymentType is { } type)
        {
            profile.EmploymentType = (EmploymentType)(int)type;
        }

        profile.UpdatedAtUtc = now;
        profile.UpdatedBy = audit.Actor;
        if (isNew)
        {
            db.Set<EmployeePayProfile>().Add(profile);
        }

        // Labor cost follows pay: the matching cost rate from the same date (a first profile only fills a gap).
        var costChanged = added is not null && await LaborRateChanges.SyncCostRateAsync(db, added, isNew, cancellationToken);
        var rateChanged = added is not null && !isNew;
        audit.Record(db, AuditEntityType, profile.Id, isNew ? "Created" : "Updated", before, Snapshot(profile), rateChanged ? "Pay rate changed" : null);
        if (rateChanged)
        {
            // Its own row, so the history of changes is searchable; never the amounts.
            audit.Record(db, AuditEntityType, profile.Id, "Pay rate changed", null,
                new { added!.EffectiveFrom, PayType = added.PayType.ToString() }, reason);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (rateChanged && securityLog is not null)
        {
            await securityLog.RecordAsync(PayrollSecurityEvents.PayRateChanged, $"employee {employee.EmployeeNumber}",
                detail: $"Pay rate changed from {added!.EffectiveFrom:yyyy-MM-dd}", cancellationToken: cancellationToken);
        }

        // A change dated before labor that's already posted re-costs that labor (adjusting entries, audited).
        IReadOnlyList<LaborAdjustmentDto>? adjustments = null;
        if (costChanged && laborRates is not null)
        {
            var outcomes = await laborRates.AdjustPostedLaborAsync(employeeId, added!.EffectiveFrom,
                $"Pay rate change effective {added.EffectiveFrom:yyyy-MM-dd}" + (reason is null ? "" : $": {reason}"), cancellationToken);
            adjustments = await LaborAdjustments.ToDtosAsync(db, outcomes, cancellationToken);
        }

        return await LoadAsync(db, employeeId, reveal: false, cancellationToken) with { LaborAdjustments = adjustments };
    }

    public async Task<IReadOnlyList<LaborAdjustmentDto>> ListLaborAdjustmentsAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LaborAdjustments.ListAsync(db, employeeId, cancellationToken);
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private async Task<PayProfileDto> LoadAsync(IWorkbenchDbContext db, Guid employeeId, bool reveal, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct) ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        var history = profile is null ? [] : await PayRates.HistoryAsync(db, profile, ct);
        var groups = await db.Set<PayGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Code, ct);
        return ToDto(employee, profile, history, groups, Today, reveal, withHistory: true);
    }

    private static PayProfileDto ToDto(Employee e, EmployeePayProfile? p, IReadOnlyList<EmployeePayRate> history, IReadOnlyDictionary<Guid, string> groups,
        DateOnly today, bool reveal, bool withHistory)
    {
        // What's in effect today (a new hire's first rate may start later).
        var current = PayRates.InEffect(history, today) ?? (history.Count > 0 ? history[0] : null);
        return new PayProfileDto(
            e.Id, e.EmployeeNumber, e.DisplayName, e.Email, e.IsExempt, p is not null, p?.PayGroupId,
            p?.PayGroupId is { } g ? groups.GetValueOrDefault(g) : null, p?.ProviderEmployeeId,
            current is not null ? (PayTypeKind)(int)current.PayType : p is null ? PayTypeKind.Hourly : (PayTypeKind)(int)p.PayType,
            current is not null && (current.AnnualSalary ?? current.HourlyRate) is not null,
            reveal ? current?.AnnualSalary : null, reveal ? current?.HourlyRate : null, current?.EffectiveFrom ?? p?.EffectiveFrom, p?.UpdatedAtUtc,
            p is null ? EmploymentTypeKind.FullTime : (EmploymentTypeKind)(int)p.EmploymentType,
            withHistory
                ? history.Reverse().Select(r => new PayRateDto(r.EffectiveFrom, (PayTypeKind)(int)r.PayType, reveal ? r.AnnualSalary : null, reveal ? r.HourlyRate : null,
                    r.Reason, r.CreatedBy, r.CreatedAtUtc, ReferenceEquals(r, current))).ToList()
                : null);
    }

    // Never the pay rate itself: audit rows are readable by more people than pay rates should be.
    private static object Snapshot(EmployeePayProfile p) => new { p.PayGroupId, p.ProviderEmployeeId, PayType = p.PayType.ToString(), EmploymentType = p.EmploymentType.ToString(), p.EffectiveFrom };
}

public sealed class PayrollProviderService(
    IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, PayrollProviders providers, TimeProvider clock,
    PayrollConnections? connections = null, SecurityEventLog? securityLog = null) : IPayrollProviderService
{
    public const string ConnectionAuditType = nameof(PayrollProviderConnection);

    public async Task<IReadOnlyList<PayrollProviderDto>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<PayrollProviderDto>();
        foreach (var p in providers.All)
        {
            var connected = p.IsSupported && await p.IsConnectedAsync(cancellationToken);
            list.Add(new PayrollProviderDto(p.Key, p.Name, p.IsSupported, p.SupportsEmployeeSync, connected,
                !p.IsSupported ? "Coming soon" : connected ? "API connected" : p.ConnectionSpec is not null ? "Not connected" : "File import",
                p.ConnectionSpec is not null, p.SupportsHoursApi, p.SupportsRegisterApi));
        }

        return list.OrderByDescending(p => p.IsSupported).ThenBy(p => p.Key == PaylocityPayrollProvider.ProviderKey ? 0 : 1).ThenBy(p => p.Name).ToList();
    }

    public async Task<PayrollConnectionDto> GetConnectionAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var (provider, spec) = Connectable(providerKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ConnectionDtoAsync(db, provider, spec, cancellationToken);
    }

    public async Task<PayrollConnectionDto> SaveConnectionAsync(string providerKey, SavePayrollConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, spec) = Connectable(providerKey);
        var store = connections ?? throw new InvalidOperationException("Payroll connections aren't available.");
        var environment = (request.Environment ?? "").Trim().ToLowerInvariant();
        if (!spec.Environments.Contains(environment))
        {
            throw new ArgumentException($"Choose the {provider.Name} environment: {string.Join(" or ", spec.Environments)}.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<PayrollProviderConnection>().FirstOrDefaultAsync(c => c.ProviderKey == provider.Key, cancellationToken);
        var values = row is null ? [] : store.Unprotect(row.ProtectedCredentials);
        foreach (var field in spec.Fields)
        {
            var given = request.Values.TryGetValue(field.Key, out var v) ? v?.Trim() : null;
            if (!string.IsNullOrEmpty(given))
            {
                if (given.Length > 20_000)
                {
                    throw new ArgumentException($"{field.Label} is too long.");
                }

                values[field.Key] = given;
            }
            else if (!field.IsSecret)
            {
                // A non-secret field left blank is cleared; a secret left blank keeps the one on file.
                values.Remove(field.Key);
            }

            if (field.Required && !values.ContainsKey(field.Key))
            {
                throw new ArgumentException($"Enter the {field.Label}.");
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var isNew = row is null;
        row ??= new PayrollProviderConnection { ProviderKey = provider.Key, ProtectedCredentials = "", ConnectedBy = audit.Actor, ConnectedAtUtc = now };
        row.Environment = environment;
        row.ProtectedCredentials = store.Protect(values);
        row.Summary = Summary(spec, values);
        row.UpdatedAtUtc = now;
        row.LastCheckedAtUtc = null;
        row.LastCheckSucceeded = null;
        row.LastCheckMessage = null;
        if (isNew)
        {
            db.Set<PayrollProviderConnection>().Add(row);
        }

        // Which fields changed, never their values.
        audit.Record(db, ConnectionAuditType, row.Id, isNew ? "Connected" : "Credentials changed", null,
            new { Provider = provider.Key, Environment = environment, Fields = request.Values.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).Order().ToList() });
        await db.SaveChangesAsync(cancellationToken);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(PayrollSecurityEvents.ProviderConnected, $"payroll provider {provider.Key}", detail: isNew ? "Connected" : "Credentials changed", cancellationToken: cancellationToken);
        }

        if (request.TestNow)
        {
            await TestConnectionAsync(provider.Key, cancellationToken);
        }

        await using var read = await dbFactory.CreateAsync(cancellationToken);
        return await ConnectionDtoAsync(read, provider, spec, cancellationToken);
    }

    public async Task<PayrollConnectionTestDto> TestConnectionAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var (provider, _) = Connectable(providerKey);
        bool ok;
        string message;
        try
        {
            message = await provider.TestConnectionAsync(cancellationToken);
            ok = true;
        }
        catch (Exception ex) when (ex is PayrollProviderApiException or HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            (ok, message) = (false, ex.Message);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<PayrollProviderConnection>().FirstOrDefaultAsync(c => c.ProviderKey == provider.Key, cancellationToken) is { } row)
        {
            row.LastCheckedAtUtc = now;
            row.LastCheckSucceeded = ok;
            row.LastCheckMessage = message.Length > 500 ? message[..500] : message;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PayrollConnectionTestDto(ok, message, now);
    }

    public async Task<PayrollConnectionDto> DisconnectAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var (provider, spec) = Connectable(providerKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<PayrollProviderConnection>().FirstOrDefaultAsync(c => c.ProviderKey == provider.Key, cancellationToken) is { } row)
        {
            db.Set<PayrollProviderConnection>().Remove(row);
            audit.Record(db, ConnectionAuditType, row.Id, "Disconnected", null, new { Provider = provider.Key });
            await db.SaveChangesAsync(cancellationToken);
            if (securityLog is not null)
            {
                await securityLog.RecordAsync(PayrollSecurityEvents.ProviderConnected, $"payroll provider {provider.Key}", detail: "Disconnected", cancellationToken: cancellationToken);
            }
        }

        return await ConnectionDtoAsync(db, provider, spec, cancellationToken);
    }

    private (IPayrollProvider Provider, PayrollConnectionSpec Spec) Connectable(string providerKey)
    {
        var provider = providers.Get(providerKey);
        return provider.ConnectionSpec is { } spec
            ? (provider, spec)
            : throw new ArgumentException($"{provider.Name} doesn't connect with customer credentials here"
                + (provider.Key == PaylocityPayrollProvider.ProviderKey ? ": its API credentials are server settings (Payroll:Paylocity:ClientId and ClientSecret)." : "."));
    }

    private async Task<PayrollConnectionDto> ConnectionDtoAsync(IWorkbenchDbContext db, IPayrollProvider provider, PayrollConnectionSpec spec, CancellationToken ct)
    {
        var row = await db.Set<PayrollProviderConnection>().AsNoTracking().FirstOrDefaultAsync(c => c.ProviderKey == provider.Key, ct);
        Dictionary<string, string> values = [];
        if (row is not null && connections is not null)
        {
            try
            {
                values = connections.Unprotect(row.ProtectedCredentials);
            }
            catch (PayrollProviderApiException)
            {
                // Unreadable on this server: shown as connected with nothing on file, so the customer re-enters it.
            }
        }

        return new PayrollConnectionDto(provider.Key, provider.Name,
            spec.Fields.Select(f => new PayrollConnectionFieldDto(f.Key, f.Label, f.IsSecret, f.Required, f.Multiline, f.Help, f.Placeholder)).ToList(),
            spec.Environments, spec.SetupHelp, spec.DocsUrl, row is not null, row?.Environment, row?.Summary,
            spec.Fields.Where(f => !f.IsSecret && values.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => values[f.Key]),
            spec.Fields.Where(f => f.IsSecret && values.ContainsKey(f.Key)).Select(f => f.Key).ToList(),
            row?.ConnectedBy, row?.ConnectedAtUtc, row?.LastCheckedAtUtc, row?.LastCheckSucceeded, row?.LastCheckMessage);
    }

    private static string? Summary(PayrollConnectionSpec spec, IReadOnlyDictionary<string, string> values)
    {
        var id = spec.Fields.FirstOrDefault(f => !f.IsSecret && values.ContainsKey(f.Key));
        if (id is null)
        {
            return null;
        }

        var value = values[id.Key];
        return $"{id.Label} {(value.Length <= 8 ? value : value[..6] + "…")}";
    }

    public async Task<PayrollProviderSettingsDto> GetSettingsAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(providerKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var setting = await db.Set<PayrollProviderSetting>().AsNoTracking().FirstOrDefaultAsync(s => s.ProviderKey == provider.Key, cancellationToken);
        return ToDto(provider, setting);
    }

    public async Task<PayrollProviderSettingsDto> SaveSettingsAsync(string providerKey, SavePayrollProviderSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = providers.Get(providerKey);
        var mappings = (request.EarningCodes ?? []).Select(m => new EarningCodeMapping(m.Category.Required("Category").ToUpperInvariant(), m.EarningCode.Required("Earning code").ToUpperInvariant())).ToList();
        if (mappings.Any(m => m.Category.Length > 64 || m.EarningCode.Length > 32 || m.Category.Contains(';') || m.Category.Contains('=') || m.EarningCode.Contains(';') || m.EarningCode.Contains('=')))
        {
            throw new ArgumentException("Categories are at most 64 characters and earning codes at most 32, without ';' or '='.");
        }

        if (mappings.GroupBy(m => m.Category).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each category can be mapped only once.");
        }

        foreach (var required in new[] { EarningCodes.Regular, EarningCodes.Overtime })
        {
            if (mappings.All(m => m.Category != required))
            {
                throw new ArgumentException($"Map the {required} category to an earning code.");
            }
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var setting = await db.Set<PayrollProviderSetting>().FirstOrDefaultAsync(s => s.ProviderKey == provider.Key, cancellationToken);
        var before = setting is null ? null : new { setting.EarningCodes, setting.ExportSalariedRegularHours };
        if (setting is null)
        {
            setting = new PayrollProviderSetting { ProviderKey = provider.Key };
            db.Set<PayrollProviderSetting>().Add(setting);
        }

        setting.EarningCodes = EarningCodes.Format(mappings);
        setting.ExportSalariedRegularHours = request.ExportSalariedRegularHours;
        setting.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        setting.UpdatedBy = audit.Actor;
        audit.Record(db, nameof(PayrollProviderSetting), setting.Id, "Updated", before, new { setting.EarningCodes, setting.ExportSalariedRegularHours });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(provider, setting);
    }

    public async Task<EmployeeSyncResultDto> SyncEmployeesAsync(Guid payGroupId, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var group = await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == payGroupId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Pay group", payGroupId);
        var provider = providers.Get(group.ProviderKey);
        if (!provider.SupportsEmployeeSync)
        {
            throw new NotSupportedException($"{provider.Name} employee sync isn't supported yet (coming soon).");
        }

        if (!dryRun && !await provider.IsConnectedAsync(cancellationToken))
        {
            throw new ArgumentException(provider.ConnectionSpec is not null
                ? $"{provider.Name} isn't connected. Connect it under Payroll › Providers first."
                : $"The {provider.Name} API isn't configured. Set Payroll:Paylocity:ClientId and ClientSecret (from a vault or environment variables).");
        }

        var profiles = await db.Set<EmployeePayProfile>().Where(p => p.PayGroupId == group.Id).ToListAsync(cancellationToken);
        var ids = profiles.Select(p => p.EmployeeId).ToList();
        var rates = await PayRates.HistoryAsync(db, profiles, cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var changes = new List<(PayrollEmployeeRecord, PayrollSyncAction)>();
        var items = new List<EmployeeSyncItemDto>();
        foreach (var profile in profiles.OrderBy(p => employees[p.EmployeeId].DisplayName))
        {
            var e = employees[profile.EmployeeId];
            var terminated = e.TerminationDate is { } t && t <= today;
            var (first, last) = SplitName(e.DisplayName);
            var rate = PayRates.InEffect(rates[profile.EmployeeId], today); // The provider pays the rate in effect now.
            var record = new PayrollEmployeeRecord(e.Id, profile.ProviderEmployeeId, e.EmployeeNumber, first, last, e.Email, e.HireDate, e.TerminationDate, e.IsExempt,
                (rate?.PayType ?? profile.PayType).ToString(), rate is null ? profile.AnnualSalary : rate.AnnualSalary, rate is null ? profile.HourlyRate : rate.HourlyRate,
                e.OrgUnitId is { } o ? orgs.GetValueOrDefault(o) : null);
            PayrollSyncAction? action = profile.ProviderEmployeeId is null
                ? terminated ? null : PayrollSyncAction.Create
                : terminated ? PayrollSyncAction.Terminate : PayrollSyncAction.Update;
            if (action is null)
            {
                items.Add(new EmployeeSyncItemDto(e.Id, e.DisplayName, null, EmployeeSyncAction.Skip, true, "Terminated and never sent to the provider."));
                continue;
            }

            changes.Add((record, action.Value));
            items.Add(new EmployeeSyncItemDto(e.Id, e.DisplayName, profile.ProviderEmployeeId, (EmployeeSyncAction)(int)action.Value, true, dryRun ? "Would " + action.Value.ToString().ToLowerInvariant() : ""));
        }

        if (dryRun || changes.Count == 0)
        {
            return new EmployeeSyncResultDto(provider.Key, dryRun, items);
        }

        IReadOnlyList<PayrollSyncOutcome> outcomes;
        try
        {
            outcomes = await provider.SyncEmployeesAsync(group.CompanyCode, changes, cancellationToken);
        }
        catch (PayrollProviderApiException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var outcome in outcomes.Where(o => o.Succeeded && o.Action == PayrollSyncAction.Create && o.ProviderEmployeeId is not null))
        {
            var profile = profiles.First(p => p.EmployeeId == outcome.EmployeeId);
            profile.ProviderEmployeeId = outcome.ProviderEmployeeId;
            profile.UpdatedAtUtc = now;
            profile.UpdatedBy = audit.Actor;
        }

        audit.Record(db, nameof(PayGroup), group.Id, "Employees synced",
            after: new { Provider = provider.Key, Succeeded = outcomes.Count(o => o.Succeeded), Failed = outcomes.Count(o => !o.Succeeded) });
        await db.SaveChangesAsync(cancellationToken);
        var result = items.Select(i => outcomes.FirstOrDefault(o => o.EmployeeId == i.EmployeeId) is { } o
            ? i with { ProviderEmployeeId = o.ProviderEmployeeId, Succeeded = o.Succeeded, Message = o.Message }
            : i).ToList();
        return new EmployeeSyncResultDto(provider.Key, false, result);
    }

    private static (string First, string Last) SplitName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        return space <= 0 ? (trimmed, trimmed) : (trimmed[..space], trimmed[(space + 1)..]);
    }

    private static PayrollProviderSettingsDto ToDto(IPayrollProvider provider, PayrollProviderSetting? s) =>
        new(provider.Key, EarningCodes.Parse(s?.EarningCodes, provider.DefaultEarningCodes), s?.ExportSalariedRegularHours ?? false, s?.UpdatedAtUtc, s?.UpdatedBy);
}
