using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

public sealed class SubcontractService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ISubcontractService
{
    public async Task<IReadOnlyList<SubcontractDto>> ListAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var subs = await db.Set<Subcontract>().AsNoTracking().Where(s => vendorId == null || s.VendorId == vendorId).OrderBy(s => s.Number).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, subs, cancellationToken);
    }

    public async Task<SubcontractDetailDto> GetAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, subcontractId, cancellationToken);
    }

    public async Task<SubcontractDetailDto> CreateAsync(SaveSubcontractRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = new Subcontract { Number = "", Description = "" };
        await ApplyAsync(db, sub, request, isNew: true, cancellationToken);
        db.Set<Subcontract>().Add(sub);
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "Created", after: Snapshot(sub));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> UpdateAsync(Guid subcontractId, SaveSubcontractRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        if (sub.Status == SubcontractStatus.Closed)
        {
            throw new InvalidOperationException($"Subcontract {sub.Number} is closed.");
        }

        var before = Snapshot(sub);
        await ApplyAsync(db, sub, request, isNew: false, cancellationToken);
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "Updated", before, Snapshot(sub));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> ActivateAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        if (sub.Status != SubcontractStatus.Draft)
        {
            throw new InvalidOperationException($"Subcontract {sub.Number} is {sub.Status}; only drafts can be activated.");
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, sub.VendorId, cancellationToken);
        if (vendor.Status == VendorStatus.Inactive)
        {
            throw new ArgumentException($"Vendor {vendor.Code} is inactive.");
        }

        if (sub.CeilingValue <= 0)
        {
            throw new ArgumentException("Set a ceiling before activating the subcontract.");
        }

        sub.Status = SubcontractStatus.Active;
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "Activated", new { Status = "Draft" }, new { Status = "Active" });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> CloseAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        if (sub.Status == SubcontractStatus.Closed)
        {
            throw new InvalidOperationException($"Subcontract {sub.Number} is already closed.");
        }

        var before = sub.Status.ToString();
        sub.Status = SubcontractStatus.Closed;
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "Closed", new { Status = before }, new { Status = "Closed" });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> AddFundingAsync(Guid subcontractId, AddSubcontractFundingRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        if (sub.Status == SubcontractStatus.Closed)
        {
            throw new InvalidOperationException($"Subcontract {sub.Number} is closed.");
        }

        var number = PurchasingSupport.Required(request.Number, "Modification number", 16);
        var description = PurchasingSupport.Required(request.Description, "Description", 500);
        if (request.FundedChange == 0 && request.CeilingChange == 0)
        {
            throw new ArgumentException("A modification changes the funded value, the ceiling or both.");
        }

        if (decimal.Round(request.FundedChange, 2) != request.FundedChange || decimal.Round(request.CeilingChange, 2) != request.CeilingChange)
        {
            throw new ArgumentException("Amounts can have at most two decimal places.");
        }

        if (await db.Set<SubcontractFundingAction>().AnyAsync(f => f.SubcontractId == sub.Id && f.Number == number, cancellationToken))
        {
            throw new ArgumentException($"Modification {number} is already on this subcontract.");
        }

        var funded = sub.FundedValue + request.FundedChange;
        var ceiling = sub.CeilingValue + request.CeilingChange;
        if (funded < 0 || ceiling < 0)
        {
            throw new ArgumentException("Funding and ceiling can't go below zero.");
        }

        if (funded > ceiling)
        {
            throw new ArgumentException($"Funded value {funded:N2} would exceed the ceiling {ceiling:N2}.");
        }

        var billed = (await SubcontractTotals.LoadAsync(db, [sub.Id], cancellationToken)).GetValueOrDefault(sub.Id)?.Billed ?? 0;
        if (funded < billed)
        {
            throw new ArgumentException($"Funded value {funded:N2} would be below the {billed:N2} already billed.");
        }

        var before = new { sub.FundedValue, sub.CeilingValue };
        sub.FundedValue = funded;
        sub.CeilingValue = ceiling;
        var action = new SubcontractFundingAction
        {
            SubcontractId = sub.Id,
            Number = number,
            EffectiveDate = request.EffectiveDate,
            FundedChange = request.FundedChange,
            CeilingChange = request.CeilingChange,
            Description = description,
            CreatedBy = audit.Actor,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<SubcontractFundingAction>().Add(action);
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "FundingChanged", before, new { sub.FundedValue, sub.CeilingValue, Modification = number }, description);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> SaveFlowDownAsync(Guid subcontractId, SaveFlowDownRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        var clause = PurchasingSupport.Required(request.ClauseNumber, "Clause number", 32);
        if (!System.Text.RegularExpressions.Regex.IsMatch(clause, @"^(52|252)\.\d{3}-\d{1,4}$"))
        {
            throw new ArgumentException("Enter a FAR or DFARS clause number, for example 52.204-21 or 252.204-7012.");
        }

        var row = await db.Set<SubcontractFlowDown>().FirstOrDefaultAsync(f => f.SubcontractId == sub.Id && f.ClauseNumber == clause, cancellationToken);
        object? before = row is null ? null : new { row.IsIncluded, row.Note };
        if (row is null)
        {
            row = new SubcontractFlowDown { SubcontractId = sub.Id, ClauseNumber = clause };
            db.Set<SubcontractFlowDown>().Add(row);
        }

        row.Title = PurchasingSupport.Optional(request.Title, "Title", 300) ?? row.Title ?? PurchasingCatalog.StandardFlowDowns.FirstOrDefault(s => s.Clause == clause).Title;
        row.IsIncluded = request.IsIncluded;
        row.Note = PurchasingSupport.Optional(request.Note, "Note", 1000);
        row.UpdatedBy = audit.Actor;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "FlowDownSaved", before, new { Clause = clause, row.IsIncluded, row.Note });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> RemoveFlowDownAsync(Guid subcontractId, Guid flowDownId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        var row = await db.Set<SubcontractFlowDown>().FirstOrDefaultAsync(f => f.Id == flowDownId && f.SubcontractId == sub.Id, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Flow-down clause", flowDownId);
        db.Set<SubcontractFlowDown>().Remove(row);
        audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "FlowDownRemoved", new { Clause = row.ClauseNumber, row.IsIncluded });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    public async Task<SubcontractDetailDto> AddStandardFlowDownsAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sub = await LoadAsync(db, subcontractId, cancellationToken);
        var existing = (await db.Set<SubcontractFlowDown>().Where(f => f.SubcontractId == sub.Id).Select(f => f.ClauseNumber).ToListAsync(cancellationToken)).ToHashSet();
        var now = clock.GetUtcNow().UtcDateTime;
        var added = PurchasingCatalog.StandardFlowDowns.Where(s => !existing.Contains(s.Clause))
            .Select(s => new SubcontractFlowDown { SubcontractId = sub.Id, ClauseNumber = s.Clause, Title = s.Title, UpdatedBy = audit.Actor, UpdatedAtUtc = now })
            .ToList();
        if (added.Count > 0)
        {
            db.Set<SubcontractFlowDown>().AddRange(added);
            audit.Record(db, PurchasingSupport.SubcontractEntity, sub.Id, "FlowDownsAdded", after: new { Clauses = added.Select(a => a.ClauseNumber) });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await DetailAsync(db, sub.Id, cancellationToken);
    }

    // ---- Helpers ----

    private static async Task<Subcontract> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Subcontract>().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Subcontract", id);

    private static async Task ApplyAsync(IWorkbenchDbContext db, Subcontract sub, SaveSubcontractRequest r, bool isNew, CancellationToken ct)
    {
        var number = PurchasingSupport.Required(r.Number, "Subcontract number", 64);
        if (await db.Set<Subcontract>().AnyAsync(s => s.Number == number && s.Id != sub.Id, ct))
        {
            throw new ArgumentException($"Subcontract number {number} is already used.");
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, r.VendorId, ct);
        if (vendor.Status == VendorStatus.Inactive && (isNew || vendor.Id != sub.VendorId))
        {
            throw new ArgumentException($"Vendor {vendor.Code} is inactive.");
        }

        if (!isNew && sub.Status != SubcontractStatus.Draft && vendor.Id != sub.VendorId)
        {
            throw new ArgumentException("The vendor of an active subcontract can't change.");
        }

        if (r.PeriodEnd < r.PeriodStart)
        {
            throw new ArgumentException("The period ends before it starts.");
        }

        if (r.RetainagePercent is < 0 or > 100)
        {
            throw new ArgumentException("Retainage is a percent from 0 to 100.");
        }

        PurchasingSupport.Money(r.CeilingValue, "Ceiling", allowZero: true);
        if (!isNew && sub.Status != SubcontractStatus.Draft && r.CeilingValue != sub.CeilingValue)
        {
            throw new ArgumentException("Change the ceiling of an active subcontract with a modification.");
        }

        if (r.CeilingValue < sub.FundedValue)
        {
            throw new ArgumentException($"The ceiling can't be below the funded value {sub.FundedValue:N2}.");
        }

        if (r.PrimeContractId is { } cid && !await db.Set<Contract>().AnyAsync(c => c.Id == cid, ct))
        {
            throw WorkbenchNotFoundException.For("Contract", cid);
        }

        if (r.ProjectId is { } pid)
        {
            var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) ?? throw WorkbenchNotFoundException.For("Project", pid);
            if (r.PrimeContractId is { } prime && project.ContractId is { } pc && pc != prime)
            {
                throw new ArgumentException($"Project {project.Code} isn't on that prime contract.");
            }
        }

        sub.Number = number;
        sub.Description = PurchasingSupport.Required(r.Description, "Description", 500);
        sub.VendorId = vendor.Id;
        sub.Pricing = r.Pricing.ToDomain();
        sub.PeriodStart = r.PeriodStart;
        sub.PeriodEnd = r.PeriodEnd;
        sub.CeilingValue = r.CeilingValue;
        sub.PrimeContractId = r.PrimeContractId;
        sub.ProjectId = r.ProjectId;
        sub.RetainagePercent = r.RetainagePercent;
    }

    private static object Snapshot(Subcontract s) => new
    {
        s.Number,
        s.Description,
        s.VendorId,
        Pricing = s.Pricing.ToString(),
        Status = s.Status.ToString(),
        s.PeriodStart,
        s.PeriodEnd,
        s.CeilingValue,
        s.FundedValue,
        s.RetainagePercent,
        s.PrimeContractId,
        s.ProjectId,
    };

    private static async Task<SubcontractDetailDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var sub = await db.Set<Subcontract>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Subcontract", id);
        var dto = (await ToDtosAsync(db, [sub], ct))[0];
        var funding = (await db.Set<SubcontractFundingAction>().AsNoTracking().Where(f => f.SubcontractId == id).ToListAsync(ct))
            .OrderBy(f => f.EffectiveDate).ThenBy(f => f.CreatedAtUtc)
            .Select(f => new SubcontractFundingDto(f.Id, f.Number, f.EffectiveDate, f.FundedChange, f.CeilingChange, f.Description, f.CreatedBy, f.CreatedAtUtc))
            .ToList();
        var flowDowns = (await db.Set<SubcontractFlowDown>().AsNoTracking().Where(f => f.SubcontractId == id).ToListAsync(ct))
            .OrderBy(f => f.ClauseNumber.StartsWith("252", StringComparison.Ordinal)).ThenBy(f => f.ClauseNumber, StringComparer.Ordinal)
            .Select(f => new FlowDownDto(f.Id, f.ClauseNumber, f.Title, f.IsIncluded, f.Note)).ToList();
        var billing = (await SubcontractTotals.LoadAsync(db, [id], ct)).GetValueOrDefault(id);
        return new SubcontractDetailDto(dto, funding, flowDowns, billing?.Invoices ?? []);
    }

    internal static async Task<IReadOnlyList<SubcontractDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<Subcontract> subs, CancellationToken ct)
    {
        var vendorIds = subs.Select(s => s.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var contractIds = subs.Where(s => s.PrimeContractId != null).Select(s => s.PrimeContractId!.Value).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var projectIds = subs.Where(s => s.ProjectId != null).Select(s => s.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var ids = subs.Select(s => s.Id).ToList();
        var flowDowns = (await db.Set<SubcontractFlowDown>().AsNoTracking().Where(f => ids.Contains(f.SubcontractId)).ToListAsync(ct)).ToLookup(f => f.SubcontractId);
        var billing = await SubcontractTotals.LoadAsync(db, ids, ct);
        return subs.Select(s =>
        {
            var vendor = vendors[s.VendorId];
            var b = billing.GetValueOrDefault(s.Id);
            return new SubcontractDto(
                s.Id, s.Number, s.Description, s.VendorId, vendor.Code, vendor.Name,
                s.PrimeContractId, s.PrimeContractId is { } c ? contracts.GetValueOrDefault(c) : null,
                s.ProjectId, s.ProjectId is { } p ? projects.GetValueOrDefault(p) : null,
                s.Pricing.ToContract(), s.Status.ToContract(), s.PeriodStart, s.PeriodEnd, s.CeilingValue, s.FundedValue, s.RetainagePercent,
                b?.Billed ?? 0, b?.Retainage ?? 0, b?.Paid ?? 0,
                flowDowns[s.Id].Count(f => f.IsIncluded), flowDowns[s.Id].Count());
        }).ToList();
    }
}
