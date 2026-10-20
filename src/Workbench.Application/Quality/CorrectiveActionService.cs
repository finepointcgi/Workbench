using Microsoft.EntityFrameworkCore;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Quality;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>
/// Corrective action requests (internal, or supplier corrective actions when a vendor is named): root cause, action,
/// owner and due date, implemented then verified. Also the supplier quality view: inspections, rejects, NCRs, returns and
/// open corrective actions per vendor.
/// </summary>
public sealed class CorrectiveActionService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, INotificationPublisher notifications, TimeProvider clock)
    : ICorrectiveActionService
{
    public const string NotificationKind = "quality.car";

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<CarSummaryDto>> ListAsync(CarQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new CarQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var status = query.Status?.ToDomain();
        var rows = await db.Set<CorrectiveAction>().AsNoTracking()
            .Where(c => (status == null || c.Status == status) && (query.VendorId == null || c.VendorId == query.VendorId)
                && (!query.OpenOnly || c.Status == CorrectiveActionStatus.Open || c.Status == CorrectiveActionStatus.Implemented))
            .ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows.OrderBy(c => c.Status).ThenBy(c => c.DueDate).ThenBy(c => c.Number, StringComparer.Ordinal).ToList(), Today, cancellationToken);
    }

    public async Task<CarDto> GetAsync(Guid carId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, carId, cancellationToken);
    }

    public async Task<CarDto> SaveAsync(Guid? carId, SaveCarRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var car = carId is { } id
            ? await db.Set<CorrectiveAction>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Corrective action", id)
            : new CorrectiveAction { Number = "", Title = "", Problem = "", OwnerEmail = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        if (car.Status is CorrectiveActionStatus.Closed or CorrectiveActionStatus.Cancelled)
        {
            throw new InvalidOperationException($"{car.Number} is {car.Status.ToString().ToLowerInvariant()}.");
        }

        var before = carId is null ? null : Snapshot(car);
        var owner = PurchasingSupport.Normalize(request.OwnerEmail);
        if (owner.Length == 0 || owner.Length > 320 || !owner.Contains('@'))
        {
            throw new ArgumentException("Choose who owns the corrective action.");
        }

        if (request.NonconformanceId is { } nid)
        {
            var ncr = await db.Set<Nonconformance>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == nid, cancellationToken) ?? throw WorkbenchNotFoundException.For("NCR", nid);
            car.VendorId = request.VendorId ?? ncr.VendorId;
            car.PartId = request.PartId ?? ncr.PartId;
        }
        else
        {
            car.VendorId = request.VendorId;
            car.PartId = request.PartId;
        }

        if (car.VendorId is { } vid && !await db.Set<Vendor>().AnyAsync(v => v.Id == vid, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Vendor", vid);
        }

        if (car.PartId is { } pid && !await db.Set<Part>().AnyAsync(p => p.Id == pid, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Part", pid);
        }

        car.NonconformanceId = request.NonconformanceId;
        car.Title = PurchasingSupport.Required(request.Title, "Title", 200);
        car.Problem = PurchasingSupport.Required(request.Problem, "Problem", 2000);
        car.RootCauseCategory = request.RootCauseCategory.ToDomain();
        car.RootCause = PurchasingSupport.Optional(request.RootCause, "Root cause", 2000);
        car.Action = PurchasingSupport.Optional(request.Action, "Corrective action", 2000);
        car.PreventiveAction = PurchasingSupport.Optional(request.PreventiveAction, "Preventive action", 2000);
        var newOwner = car.OwnerEmail != owner;
        car.OwnerEmail = owner;
        car.DueDate = request.DueDate;
        car.UpdatedAtUtc = UtcNow;
        if (carId is null)
        {
            car.Number = await QualityRules.NextNumberAsync<CorrectiveAction>(db, c => c.Number, "CAR", cancellationToken);
            db.Set<CorrectiveAction>().Add(car);
        }

        audit.Record(db, QualityRules.CarEntity, car.Id, carId is null ? "Created" : "Updated", before, Snapshot(car));
        await db.SaveChangesAsync(cancellationToken);
        if (newOwner && owner != audit.Actor)
        {
            await notifications.PublishAsync(owner, NotificationKind, $"Corrective action assigned to you: {car.Number} {car.Title}", $"Due {car.DueDate:MMM d, yyyy}",
                link: Link(car.Id));
        }

        return await DetailAsync(db, car.Id, cancellationToken);
    }

    public async Task<CarDto> MarkImplementedAsync(Guid carId, CompleteCarRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var car = await LoadAsync(db, carId, cancellationToken);
        if (car.Status != CorrectiveActionStatus.Open)
        {
            throw new InvalidOperationException($"{car.Number} is {car.Status.ToString().ToLowerInvariant()}.");
        }

        if (car.RootCause is null || car.Action is null)
        {
            throw new ArgumentException("Record the root cause and the corrective action before marking it done.");
        }

        car.Status = CorrectiveActionStatus.Implemented;
        car.ImplementedOn = request.ImplementedOn;
        car.VerificationNote = PurchasingSupport.Optional(request.Note, "Note", 1000);
        car.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.CarEntity, car.Id, "Implemented", new { Status = nameof(CorrectiveActionStatus.Open) }, new { car.ImplementedOn });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, car.Id, cancellationToken);
    }

    public async Task<CarDto> VerifyAsync(Guid carId, VerifyCarRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var car = await LoadAsync(db, carId, cancellationToken);
        if (car.Status != CorrectiveActionStatus.Implemented)
        {
            throw new InvalidOperationException($"{car.Number} isn't waiting for verification.");
        }

        var note = PurchasingSupport.Optional(request.Note, "Note", 1000);
        if (!request.Effective && note is null)
        {
            throw new ArgumentException("Say why the action wasn't effective.");
        }

        car.Status = request.Effective ? CorrectiveActionStatus.Closed : CorrectiveActionStatus.Open;
        car.VerifiedBy = audit.Actor;
        car.VerificationNote = note;
        car.ClosedAtUtc = request.Effective ? UtcNow : null;
        car.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.CarEntity, car.Id, request.Effective ? "Closed" : "Reopened", new { Status = nameof(CorrectiveActionStatus.Implemented) },
            new { Status = car.Status.ToString() }, note);
        await db.SaveChangesAsync(cancellationToken);
        if (!request.Effective && car.OwnerEmail != audit.Actor)
        {
            await notifications.PublishAsync(car.OwnerEmail, NotificationKind, $"Corrective action reopened: {car.Number} {car.Title}", note, link: Link(car.Id));
        }

        return await DetailAsync(db, car.Id, cancellationToken);
    }

    public async Task<CarDto> CancelAsync(Guid carId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var car = await LoadAsync(db, carId, cancellationToken);
        if (car.Status is CorrectiveActionStatus.Closed or CorrectiveActionStatus.Cancelled)
        {
            throw new InvalidOperationException($"{car.Number} is {car.Status.ToString().ToLowerInvariant()}.");
        }

        var before = car.Status.ToString();
        car.Status = CorrectiveActionStatus.Cancelled;
        car.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.CarEntity, car.Id, "Cancelled", new { Status = before }, new { Status = car.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, car.Id, cancellationToken);
    }

    public async Task<SupplierQualityDto> SupplierQualityAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var end = to ?? Today;
        var start = from ?? end.AddMonths(-12).AddDays(1);
        if (start > end)
        {
            throw new ArgumentException("The start date is after the end date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var startUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtc = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var inspections = await db.Set<Inspection>().AsNoTracking()
            .Where(i => i.VendorId != null && i.Kind == InspectionKind.Receiving && i.Status == InspectionStatus.Completed && i.InspectedOn >= start && i.InspectedOn <= end)
            .Select(i => new { VendorId = i.VendorId!.Value, i.Quantity, i.QuantityAccepted, i.QuantityRejected }).ToListAsync(cancellationToken);
        // NCRs count on the day of the inspection that rejected the material (else the day they were written).
        var ncrs = (await db.Set<Nonconformance>().AsNoTracking().Where(n => n.VendorId != null)
            .GroupJoin(db.Set<Inspection>().AsNoTracking(), n => n.InspectionId, i => (Guid?)i.Id, (n, i) => new { n, i })
            .SelectMany(x => x.i.DefaultIfEmpty(), (x, i) => new { VendorId = x.n.VendorId!.Value, x.n.Status, x.n.CreatedAtUtc, InspectedOn = i == null ? null : i.InspectedOn })
            .ToListAsync(cancellationToken))
            .Where(n => n.InspectedOn is { } d ? d >= start && d <= end : n.CreatedAtUtc >= startUtc && n.CreatedAtUtc < endUtc).ToList();
        var returns = await db.Set<VendorDebitMemo>().AsNoTracking().Where(m => m.Status != DebitMemoStatus.Cancelled && m.Date >= start && m.Date <= end)
            .Select(m => new { m.VendorId, m.Amount }).ToListAsync(cancellationToken);
        var cars = await db.Set<CorrectiveAction>().AsNoTracking()
            .Where(c => c.VendorId != null && (c.Status == CorrectiveActionStatus.Open || c.Status == CorrectiveActionStatus.Implemented))
            .Select(c => c.VendorId!.Value).ToListAsync(cancellationToken);
        var vendorIds = inspections.Select(i => i.VendorId).Concat(ncrs.Select(n => n.VendorId)).Concat(returns.Select(r => r.VendorId)).Concat(cars).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var rows = vendorIds.Where(vendors.ContainsKey).Select(v =>
        {
            var mine = inspections.Where(i => i.VendorId == v).ToList();
            var inspected = mine.Sum(i => i.Quantity);
            var rejected = mine.Sum(i => i.QuantityRejected);
            return new SupplierQualityRowDto(v, vendors[v].Code, vendors[v].Name, mine.Count, inspected, mine.Sum(i => i.QuantityAccepted), rejected,
                inspected == 0 ? 0 : Math.Round(rejected / inspected * 100m, 1), ncrs.Count(n => n.VendorId == v),
                ncrs.Count(n => n.VendorId == v && n.Status is NonconformanceStatus.Open or NonconformanceStatus.PendingApproval),
                returns.Where(r => r.VendorId == v).Sum(r => r.Amount), cars.Count(c => c == v));
        }).OrderByDescending(r => r.RejectRatePercent).ThenByDescending(r => r.Ncrs).ThenBy(r => r.VendorCode, StringComparer.Ordinal).ToList();
        return new SupplierQualityDto(start, end, rows);
    }

    public static string Link(Guid carId) => $"materials/quality/cars/{carId}";

    private static async Task<CorrectiveAction> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<CorrectiveAction>().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Corrective action", id);

    private static object Snapshot(CorrectiveAction c) =>
        new { c.Title, c.OwnerEmail, c.DueDate, Status = c.Status.ToString(), RootCause = c.RootCauseCategory.ToString(), c.VendorId, c.NonconformanceId };

    internal static async Task<List<CarSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<CorrectiveAction> rows, DateOnly today, CancellationToken ct)
    {
        var vendorIds = rows.Where(c => c.VendorId != null).Select(c => c.VendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var partIds = rows.Where(c => c.PartId != null).Select(c => c.PartId!.Value).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        var ncrIds = rows.Where(c => c.NonconformanceId != null).Select(c => c.NonconformanceId!.Value).Distinct().ToList();
        var ncrs = await db.Set<Nonconformance>().AsNoTracking().Where(n => ncrIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct);
        return rows.Select(c => new CarSummaryDto(c.Id, c.Number, c.Title, c.Status.ToContract(), c.VendorId is { } v ? vendors.GetValueOrDefault(v) : null,
            c.PartId is { } p ? parts.GetValueOrDefault(p) : null, c.NonconformanceId is { } n ? ncrs.GetValueOrDefault(n) : null, c.OwnerEmail, c.DueDate,
            c.Status == CorrectiveActionStatus.Open && c.DueDate < today)).ToList();
    }

    private async Task<CarDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var c = await db.Set<CorrectiveAction>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Corrective action", id);
        var summary = (await SummariesAsync(db, [c], Today, ct))[0];
        return new CarDto(summary, c.NonconformanceId, c.VendorId, c.PartId, c.Problem, c.RootCauseCategory.ToContract(), c.RootCause, c.Action, c.PreventiveAction,
            c.ImplementedOn, c.VerifiedBy, c.VerificationNote, c.ClosedAtUtc, c.CreatedBy, c.CreatedAtUtc,
            await QualityFileService.ListAsync(db, QualityFileOwner.CorrectiveAction, c.Id, ct));
    }
}
