using Microsoft.EntityFrameworkCore;
using Workbench.Application.Materials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Quality;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>
/// Inspection plans and the inspection queue. Completing an inspection releases the accepted stock from inspection
/// hold to available and moves the rejected stock to nonconforming on a new NCR (in-process inspections move no stock:
/// the rejected units are still on the work order).
/// </summary>
public sealed class InspectionService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, TimeProvider clock) : IInspectionService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ---- Plans ----

    public async Task<IReadOnlyList<InspectionPlanDto>> ListPlansAsync(Guid? partId = null, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plans = await db.Set<InspectionPlan>().AsNoTracking().Include(p => p.Characteristics)
            .Where(p => (partId == null || p.PartId == partId) && (!activeOnly || p.IsActive)).ToListAsync(cancellationToken);
        return await PlansAsync(db, plans.OrderBy(p => p.Code, StringComparer.Ordinal).ToList(), cancellationToken);
    }

    public async Task<InspectionPlanDto> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await db.Set<InspectionPlan>().AsNoTracking().Include(p => p.Characteristics).FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Inspection plan", planId);
        return (await PlansAsync(db, [plan], cancellationToken))[0];
    }

    public async Task<InspectionPlanDto> SavePlanAsync(Guid? planId, SaveInspectionPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = planId is { } id
            ? await db.Set<InspectionPlan>().Include(p => p.Characteristics).FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Inspection plan", id)
            : new InspectionPlan { Code = "", Name = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        var before = planId is null ? null : Snapshot(plan);
        var code = MaterialRules.Code(request.Code, "Plan code", 64);
        if (await db.Set<InspectionPlan>().AnyAsync(p => p.Code == code && p.Id != plan.Id, cancellationToken))
        {
            throw new ArgumentException($"Inspection plan {code} already exists.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        var kind = request.Kind.ToDomain();
        if (kind != InspectionKind.Receiving && part.Source != PartSource.Make)
        {
            throw new ArgumentException($"{part.Display} is bought, not built: it can only have a receiving inspection.");
        }

        if (kind == InspectionKind.InProcess && request.OperationSequence is null)
        {
            throw new ArgumentException("Choose the routing operation the in-process inspection follows.");
        }

        if (request.IsActive && await db.Set<InspectionPlan>().AnyAsync(p => p.Id != plan.Id && p.IsActive && p.PartId == part.Id && p.Kind == kind
            && (kind != InspectionKind.InProcess || p.OperationSequence == request.OperationSequence), cancellationToken))
        {
            throw new ArgumentException($"{part.Display} already has an active {Label(kind)} plan{(kind == InspectionKind.InProcess ? $" at operation {request.OperationSequence}" : "")}; deactivate it first.");
        }

        if (request.Sampling == Sampling.FixedSample && request.SampleSize <= 0)
        {
            throw new ArgumentException("Enter the sample size.");
        }

        if (request.AcceptNumber < 0 || request.SampleSize < 0)
        {
            throw new ArgumentException("Sample size and accept number can't be negative.");
        }

        if ((request.Characteristics ?? []).Count == 0)
        {
            throw new ArgumentException("Add at least one characteristic to check.");
        }

        if (request.Characteristics!.Count > 100)
        {
            throw new ArgumentException("A plan can have at most 100 characteristics.");
        }

        plan.Code = code;
        plan.Name = PurchasingSupport.Required(request.Name, "Plan name", 200);
        plan.PartId = part.Id;
        plan.Kind = kind;
        plan.OperationSequence = kind == InspectionKind.InProcess ? request.OperationSequence : null;
        plan.Sampling = request.Sampling.ToDomain();
        plan.SampleSize = request.Sampling == Sampling.FixedSample ? request.SampleSize : 0;
        plan.AcceptNumber = request.Sampling == Sampling.Full ? 0 : request.AcceptNumber;
        plan.Aql = request.Aql is { } aql && aql > 0 ? Math.Round(aql, 3) : null;
        var documents = (request.RequiredDocuments ?? []).Select(d => d?.Trim() ?? "").Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (documents.Any(d => d.Length > 100 || d.Contains('\n')))
        {
            throw new ArgumentException("Each required document name is one line of at most 100 characters.");
        }

        plan.RequiredDocuments = documents.Count == 0 ? null : string.Join("\n", documents);
        if (plan.RequiredDocuments?.Length > 1000)
        {
            throw new ArgumentException("Too many required documents.");
        }

        plan.Instructions = PurchasingSupport.Optional(request.Instructions, "Instructions", 2000);
        plan.IsActive = request.IsActive;
        plan.UpdatedAtUtc = UtcNow;
        db.Set<InspectionCharacteristic>().RemoveRange(plan.Characteristics);
        plan.Characteristics.Clear();
        var sequence = 0;
        foreach (var c in request.Characteristics)
        {
            sequence += 10;
            var name = PurchasingSupport.Required(c.Name, $"Characteristic {sequence / 10} name", 200);
            if (c.Kind == CharacteristicType.Measured && c.LowerLimit is { } lo && c.UpperLimit is { } hi && lo > hi)
            {
                throw new ArgumentException($"{name}: the lower limit is above the upper limit.");
            }

            var measured = c.Kind == CharacteristicType.Measured;
            plan.Characteristics.Add(new InspectionCharacteristic
            {
                PlanId = plan.Id, Sequence = sequence, Name = name, Kind = c.Kind.ToDomain(), Nominal = measured ? c.Nominal : null,
                LowerLimit = measured ? c.LowerLimit : null, UpperLimit = measured ? c.UpperLimit : null,
                Unit = measured ? PurchasingSupport.Optional(c.Unit, "Unit", 16) : null, IsCritical = c.IsCritical,
            });
        }

        if (planId is null)
        {
            db.Set<InspectionPlan>().Add(plan);
        }
        else
        {
            db.Set<InspectionCharacteristic>().AddRange(plan.Characteristics);
        }

        audit.Record(db, QualityRules.PlanEntity, plan.Id, planId is null ? "Created" : "Updated", before, Snapshot(plan));
        await db.SaveChangesAsync(cancellationToken);
        return (await PlansAsync(db, [plan], cancellationToken))[0];
    }

    private static object Snapshot(InspectionPlan p) => new
    {
        p.Code, p.Name, Kind = p.Kind.ToString(), p.OperationSequence, Sampling = p.Sampling.ToString(), p.SampleSize, p.AcceptNumber, p.RequiredDocuments, p.IsActive,
        Characteristics = p.Characteristics.Count,
    };

    internal static async Task<List<InspectionPlanDto>> PlansAsync(IWorkbenchDbContext db, IReadOnlyList<InspectionPlan> plans, CancellationToken ct)
    {
        var partIds = plans.Select(p => p.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return plans.Select(p => new InspectionPlanDto(p.Id, p.Code, p.Name, p.PartId, parts[p.PartId].Display, parts[p.PartId].Description, p.Kind.ToContract(),
            p.OperationSequence, p.Sampling.ToContract(), p.SampleSize, p.AcceptNumber, p.Aql, p.RequiredDocumentList, p.Instructions, p.IsActive,
            p.Characteristics.OrderBy(c => c.Sequence).Select(Map).ToList())).ToList();
    }

    internal static CharacteristicDto Map(InspectionCharacteristic c) =>
        new(c.Id, c.Sequence, c.Name, c.Kind.ToContract(), c.Nominal, c.LowerLimit, c.UpperLimit, c.Unit, c.IsCritical);

    private static string Label(InspectionKind kind) => kind switch
    {
        InspectionKind.InProcess => "in-process inspection",
        InspectionKind.Final => "final inspection",
        _ => "receiving inspection",
    };

    // ---- Inspections ----

    public async Task<IReadOnlyList<InspectionSummaryDto>> ListAsync(InspectionQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new InspectionQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var status = query.Status?.ToDomain();
        var kind = query.Kind?.ToDomain();
        var rows = await db.Set<Inspection>().AsNoTracking()
            .Where(i => (status == null || i.Status == status) && (kind == null || i.Kind == kind) && (query.PartId == null || i.PartId == query.PartId)
                && (query.VendorId == null || i.VendorId == query.VendorId))
            .OrderByDescending(i => i.Number).Take(Math.Clamp(query.Take, 1, 1000)).ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows.OrderBy(i => i.Status).ThenBy(i => i.ReadyOn).ThenBy(i => i.Number, StringComparer.Ordinal).ToList(), cancellationToken);
    }

    public async Task<InspectionDto> GetAsync(Guid inspectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, inspectionId, cancellationToken);
    }

    public async Task<InspectionDto> CompleteAsync(Guid inspectionId, CompleteInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var inspection = await db.Set<Inspection>().Include(i => i.Items).Include(i => i.Results).FirstOrDefaultAsync(i => i.Id == inspectionId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Inspection", inspectionId);
        if (inspection.Status != InspectionStatus.Pending)
        {
            throw new InvalidOperationException($"{inspection.Number} is already {inspection.Status.ToString().ToLowerInvariant()}.");
        }

        var plan = inspection.PlanId is { } pid ? await db.Set<InspectionPlan>().AsNoTracking().Include(p => p.Characteristics).FirstAsync(p => p.Id == pid, cancellationToken) : null;
        var characteristics = plan?.Characteristics.OrderBy(c => c.Sequence).ToList() ?? [];

        // Results: one per characteristic; measured values are judged against the limits.
        var inputs = (request.Results ?? []).GroupBy(r => r.CharacteristicId).ToDictionary(g => g.Key, g => g.Last());
        var results = new List<InspectionResult>();
        foreach (var c in characteristics)
        {
            if (!inputs.TryGetValue(c.Id, out var input))
            {
                throw new ArgumentException($"Record a result for \"{c.Name}\".");
            }

            bool passed;
            decimal? value = null;
            if (c.Kind == CharacteristicKind.Measured)
            {
                value = input.MeasuredValue ?? throw new ArgumentException($"Enter the measured value for \"{c.Name}\".");
                passed = c.IsWithinLimits(value.Value);
            }
            else
            {
                passed = input.Passed ?? throw new ArgumentException($"Say whether \"{c.Name}\" passed.");
            }

            results.Add(new InspectionResult
            {
                InspectionId = inspection.Id, CharacteristicId = c.Id, Sequence = c.Sequence, Name = c.Name, MeasuredValue = value, Passed = passed,
                Note = QualityRules.Clip(input.Note, 500),
            });
        }

        // Accepted and rejected per item.
        var accepted = new Dictionary<Guid, decimal>();
        if (inspection.Items.Count == 0)
        {
            var total = request.QuantityAccepted ?? inspection.Quantity;
            accepted[Guid.Empty] = CheckAccepted(total, inspection.Quantity);
        }
        else if (request.Items is { Count: > 0 })
        {
            foreach (var d in request.Items)
            {
                var item = inspection.Items.FirstOrDefault(i => i.Id == d.ItemId) ?? throw new ArgumentException("That item isn't on this inspection.");
                accepted[item.Id] = CheckAccepted(d.QuantityAccepted, item.Quantity);
            }

            foreach (var item in inspection.Items.Where(i => !accepted.ContainsKey(i.Id)))
            {
                accepted[item.Id] = item.Quantity;
            }
        }
        else
        {
            var left = CheckAccepted(request.QuantityAccepted ?? inspection.Quantity, inspection.Quantity);
            foreach (var item in inspection.Items)
            {
                var take = Math.Min(left, item.Quantity);
                accepted[item.Id] = take;
                left -= take;
            }
        }

        var totalAccepted = accepted.Values.Sum();
        var totalRejected = inspection.Quantity - totalAccepted;
        if (request.DefectsFound < 0 || request.DefectsFound > Math.Max(inspection.SampleSize, (int)Math.Ceiling(inspection.Quantity)))
        {
            throw new ArgumentException("The number of defective units must be between zero and the sample size.");
        }

        if (totalAccepted > 0)
        {
            if (results.Any(r => !r.Passed && characteristics.First(c => c.Id == r.CharacteristicId).IsCritical))
            {
                throw new ArgumentException("A critical characteristic failed: reject the lot (the material review board decides what to do with it).");
            }

            var sampled = plan is not null && plan.Sampling != SamplingMethod.Full && inspection.SampleSize < inspection.Quantity;
            if (sampled && request.DefectsFound > plan!.AcceptNumber)
            {
                throw new ArgumentException($"{request.DefectsFound} defective in the sample is over the accept number ({plan.AcceptNumber}): reject the lot.");
            }

            var attached = await db.Set<QualityAttachment>().AsNoTracking()
                .Where(a => a.OwnerType == nameof(QualityFileOwner.Inspection) && a.OwnerId == inspection.Id).Select(a => a.Kind).ToListAsync(cancellationToken);
            var missing = plan?.RequiredDocumentList.FirstOrDefault(d => !attached.Contains(d, StringComparer.OrdinalIgnoreCase));
            if (missing is not null)
            {
                throw new ArgumentException($"Attach the {missing.ToLowerInvariant()} before accepting (or reject the lot).");
            }
        }

        string? codes = null;
        string? description = null;
        if (totalRejected > 0)
        {
            codes = await DefectCodeDefaults.ValidateAsync(db, request.DefectCodes, cancellationToken);
            description = PurchasingSupport.Required(request.NonconformanceDescription, "Describe what's wrong (for the NCR)", 2000);
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(inspection.PartId, cancellationToken);
        Nonconformance? ncr = null;
        if (totalRejected > 0)
        {
            ncr = new Nonconformance
            {
                Number = (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.Nonconformance, null, DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken)).Number,
                PartId = part.Id,
                Quantity = totalRejected,
                Source = inspection.Kind switch
                {
                    InspectionKind.InProcess => NonconformanceSource.InProcessInspection,
                    InspectionKind.Final => NonconformanceSource.FinalInspection,
                    _ => NonconformanceSource.ReceivingInspection,
                },
                InspectionId = inspection.Id,
                VendorId = inspection.VendorId,
                PurchaseOrderLineId = inspection.PurchaseOrderLineId,
                WorkOrderId = inspection.WorkOrderId,
                DefectCodes = codes!,
                Description = description!,
                CreatedBy = audit.Actor,
                CreatedAtUtc = UtcNow,
                UpdatedAtUtc = UtcNow,
            };
            db.Set<Nonconformance>().Add(ncr);
            inspection.NonconformanceId = ncr.Id;
        }

        // Release and reject held stock.
        var memo = $"{inspection.Number}";
        foreach (var item in inspection.Items)
        {
            var ok = accepted[item.Id];
            var bad = item.Quantity - ok;
            item.QuantityAccepted = ok;
            item.QuantityRejected = bad;
            if (ok > 0)
            {
                engine.ChangeStatus(part, HeldBalance(engine, part, item), ok, InventoryStockStatus.Available, request.InspectedOn, $"{memo}: passed inspection");
            }

            if (bad > 0)
            {
                var held = HeldBalance(engine, part, item);
                engine.ChangeStatus(part, held, bad, InventoryStockStatus.Nonconforming, request.InspectedOn, $"{memo}: rejected ({ncr!.Number})");
                ncr.Items.Add(new NonconformanceItem
                {
                    NonconformanceId = ncr.Id, WarehouseId = held.WarehouseId, BinId = held.BinId, Lot = held.Lot, Serial = held.Serial,
                    Ownership = held.Ownership, OwnerProjectId = held.OwnerProjectId, Quantity = bad,
                });
            }
        }

        if (ncr is not null)
        {
            var lots = ncr.Items.Select(i => i.Lot).Where(l => l.Length > 0).Distinct().ToList();
            var serials = ncr.Items.Select(i => i.Serial).Where(s => s.Length > 0).Distinct().ToList();
            ncr.Lot = lots.Count == 1 ? lots[0] : "";
            ncr.Serial = serials.Count == 1 ? serials[0] : "";
            audit.Record(db, QualityRules.NcrEntity, ncr.Id, "Opened", after: new { ncr.Number, ncr.Quantity, ncr.DefectCodes, Inspection = inspection.Number });
        }

        db.Set<InspectionResult>().RemoveRange(inspection.Results);
        db.Set<InspectionResult>().AddRange(results);
        inspection.QuantityAccepted = totalAccepted;
        inspection.QuantityRejected = totalRejected;
        inspection.DefectsFound = request.DefectsFound;
        inspection.InspectedOn = request.InspectedOn;
        inspection.InspectorEmail = audit.Actor;
        inspection.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 2000);
        inspection.Status = InspectionStatus.Completed;
        inspection.CompletedAtUtc = UtcNow;
        await engine.FlushAsync(JournalSource.Inventory, inspection.Id, request.InspectedOn, $"Inspection {inspection.Number}", cancellationToken);
        audit.Record(db, QualityRules.InspectionEntity, inspection.Id, "Completed", new { Status = nameof(InspectionStatus.Pending) },
            new { inspection.QuantityAccepted, inspection.QuantityRejected, inspection.DefectsFound, Ncr = ncr?.Number });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, inspection.Id, cancellationToken);
    }

    private static decimal CheckAccepted(decimal accepted, decimal of)
    {
        MaterialRules.Quantity(accepted, "Accepted quantity", allowZero: true);
        return accepted > of ? throw new ArgumentException($"At most {of:0.####} can be accepted.") : accepted;
    }

    /// <summary>The inspection-hold stock an item covers (where it was received, or wherever it was moved since).</summary>
    private static InventoryBalance HeldBalance(StockEngine engine, Part part, InspectionItem item)
    {
        var held = engine.Balances(part.Id).Where(b => b.Status == InventoryStockStatus.InspectionHold && b.Lot == item.Lot && b.Serial == item.Serial
            && b.Ownership == item.Ownership && b.OwnerProjectId == item.OwnerProjectId).ToList();
        return held.FirstOrDefault(b => b.BinId == item.BinId) ?? held.FirstOrDefault()
            ?? throw new InvalidOperationException($"The held stock of {part.Display}{(item.Lot.Length > 0 ? $" lot {item.Lot}" : "")}{(item.Serial.Length > 0 ? $" serial {item.Serial}" : "")} is gone; it may have been counted out.");
    }

    // ---- Defect codes ----

    public async Task<IReadOnlyList<DefectCodeDto>> ListDefectCodesAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await DefectCodeDefaults.EnsureAsync(db, cancellationToken);
        return (await db.Set<DefectCode>().AsNoTracking().Where(d => !activeOnly || d.IsActive).ToListAsync(cancellationToken))
            .OrderBy(d => d.Code, StringComparer.Ordinal).Select(d => new DefectCodeDto(d.Id, d.Code, d.Description, d.IsActive)).ToList();
    }

    public async Task<DefectCodeDto> SaveDefectCodeAsync(Guid? defectCodeId, SaveDefectCodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await DefectCodeDefaults.EnsureAsync(db, cancellationToken);
        var code = defectCodeId is { } id
            ? await db.Set<DefectCode>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Defect code", id)
            : new DefectCode { Code = "", Description = "" };
        var value = MaterialRules.Code(request.Code, "Code", 16);
        if (await db.Set<DefectCode>().AnyAsync(d => d.Code == value && d.Id != code.Id, cancellationToken))
        {
            throw new ArgumentException($"Defect code {value} already exists.");
        }

        code.Code = value;
        code.Description = PurchasingSupport.Required(request.Description, "Description", 200);
        code.IsActive = request.IsActive;
        if (defectCodeId is null)
        {
            db.Set<DefectCode>().Add(code);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DefectCodeDto(code.Id, code.Code, code.Description, code.IsActive);
    }

    // ---- Mapping ----

    internal static async Task<List<InspectionSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Inspection> rows, CancellationToken ct)
    {
        var partIds = rows.Select(i => i.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vendorIds = rows.Where(i => i.VendorId != null).Select(i => i.VendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var poIds = rows.Where(i => i.PurchaseOrderId != null).Select(i => i.PurchaseOrderId!.Value).Distinct().ToList();
        var pos = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var woIds = rows.Where(i => i.WorkOrderId != null).Select(i => i.WorkOrderId!.Value).Distinct().ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Number, ct);
        var ncrIds = rows.Where(i => i.NonconformanceId != null).Select(i => i.NonconformanceId!.Value).ToList();
        var ncrs = await db.Set<Nonconformance>().AsNoTracking().Where(n => ncrIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct);
        return rows.Select(i =>
        {
            var p = parts[i.PartId];
            var source = i.PurchaseOrderId is { } po ? $"PO {pos.GetValueOrDefault(po)}"
                : i.WorkOrderId is { } wo ? $"{wos.GetValueOrDefault(wo)}{(i.OperationSequence is { } op ? $" op {op}" : "")}" : null;
            return new InspectionSummaryDto(i.Id, i.Number, i.Kind.ToContract(), i.Status.ToContract(), p.Id, p.Display, p.Description, p.UnitOfMeasure, i.Quantity,
                i.SampleSize, i.QuantityAccepted, i.QuantityRejected, source, i.VendorId is { } v ? vendors.GetValueOrDefault(v) : null, i.ReadyOn, i.InspectedOn,
                i.InspectorEmail, i.NonconformanceId, i.NonconformanceId is { } n ? ncrs.GetValueOrDefault(n) : null);
        }).ToList();
    }

    private static async Task<InspectionDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var i = await db.Set<Inspection>().AsNoTracking().Include(x => x.Items).Include(x => x.Results).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Inspection", id);
        var summary = (await SummariesAsync(db, [i], ct))[0];
        var plan = i.PlanId is { } pid ? await db.Set<InspectionPlan>().AsNoTracking().Include(p => p.Characteristics).FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = i.Items.Where(x => x.BinId != null).Select(x => x.BinId!.Value).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var wo = i.WorkOrderId is { } w ? await db.Set<WorkOrder>().AsNoTracking().Where(x => x.Id == w).Select(x => x.Number).FirstOrDefaultAsync(ct) : null;
        return new InspectionDto(summary, i.PlanId, plan?.Name, plan?.Instructions, plan?.AcceptNumber ?? 0, i.DefectsFound, i.Notes, i.WorkOrderId, wo, i.OperationSequence,
            plan?.Characteristics.OrderBy(c => c.Sequence).Select(Map).ToList() ?? [],
            i.Items.OrderBy(x => x.Lot, StringComparer.Ordinal).ThenBy(x => x.Serial, StringComparer.Ordinal).Select(x => new InspectionItemDto(x.Id,
                x.WarehouseId is { } wid ? QualityRules.Location(warehouses.GetValueOrDefault(wid), x.BinId is { } b ? bins.GetValueOrDefault(b) : null) : null,
                x.Lot, x.Serial, x.Quantity, x.QuantityAccepted, x.QuantityRejected)).ToList(),
            i.Results.OrderBy(r => r.Sequence).Select(r => new InspectionResultDto(r.CharacteristicId, r.Sequence, r.Name, r.MeasuredValue, r.Passed, r.Note)).ToList(),
            plan?.RequiredDocumentList ?? [],
            await QualityFileService.ListAsync(db, QualityFileOwner.Inspection, i.Id, ct));
    }
}
