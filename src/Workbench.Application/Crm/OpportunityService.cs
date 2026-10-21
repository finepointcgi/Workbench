using Microsoft.EntityFrameworkCore;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Application.Integrations;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

public static class CrmNotificationKinds
{
    public const string Assigned = "crm.assigned";
    public const string StageChanged = "crm.stage";
    public const string GateSubmitted = "crm.gate.submitted";
    public const string GateDecided = "crm.gate.decided";
    public const string NewLeads = "crm.leads";
    public const string HandedOff = "crm.handoff";
    public const string TaskAssigned = "crm.task";
}

/// <summary>
/// Opportunities: details, stages (with go/no-go gates), outcomes, B&amp;P charge codes and the won handoff. Who may call
/// what is checked by <c>PermissionCheckingOpportunityService</c>; lists here are already limited to what the user may see.
/// </summary>
public sealed class OpportunityService(
    IWorkbenchDbContextFactory dbFactory,
    CrmAccess access,
    AuditRecorder audit,
    INotificationPublisher notifications,
    TimeProvider clock) : IOpportunityService
{
    public const string AuditEntityType = "Opportunity";

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<OpportunityDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", id);
        return (await CrmMapping.ToDtosAsync(db, [opportunity], await access.RolesAsync(cancellationToken), access.Email, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<OpportunityDto>> ListAsync(OpportunityFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new OpportunityFilter();
        var roles = await access.RolesAsync(cancellationToken);
        var me = access.Email;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.Opportunity>().AsNoTracking();
        if (!roles.Has(Permission.PipelineView) || filter.MineOnly)
        {
            query = query.Where(o => o.CaptureManagerEmail == me || o.ProposalManagerEmail == me);
        }

        if (filter.Stages is { Count: > 0 } stages)
        {
            var domain = stages.Select(s => s.ToDomain()).ToList();
            query = query.Where(o => domain.Contains(o.Stage));
        }
        else if (filter.OpenOnly)
        {
            query = query.Where(o => CrmMapping.OpenStages.Contains(o.Stage));
        }

        if (filter.AgencyAccountId is { } agency)
        {
            // The agency and its offices.
            var accounts = await CrmAccountService.WithDescendantsAsync(db, agency, cancellationToken);
            query = query.Where(o => o.AgencyAccountId != null && accounts.Contains(o.AgencyAccountId.Value));
        }

        if (CrmMapping.Email(filter.ManagerEmail) is { } manager)
        {
            query = query.Where(o => o.CaptureManagerEmail == manager || o.ProposalManagerEmail == manager);
        }

        if (filter.Kind is { } kind)
        {
            var k = kind.ToDomain();
            query = query.Where(o => o.Kind == k);
        }

        var list = await query.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            list = list.Where(o => o.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || o.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || (o.SolicitationNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (o.AgencyName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (o.NaicsCode?.StartsWith(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        var dtos = await CrmMapping.ToDtosAsync(db, list.OrderBy(o => o.Stage).ThenBy(o => o.ProposalDueOn ?? DateOnly.MaxValue).ThenBy(o => o.Number).ToList(),
            roles, me, cancellationToken);
        if (!string.IsNullOrWhiteSpace(filter.Search) && filter.AgencyAccountId is null)
        {
            // Agency names shown come from accounts; match them too.
            var term = filter.Search.Trim();
            var extra = await AgencyMatchesAsync(db, query, term, dtos.Select(d => d.Id).ToHashSet(), roles, me, cancellationToken);
            dtos.AddRange(extra);
        }

        return dtos;
    }

    private static async Task<List<OpportunityDto>> AgencyMatchesAsync(IWorkbenchDbContext db, IQueryable<D.Opportunity> query, string term, HashSet<Guid> already,
        PermissionSet roles, string me, CancellationToken ct)
    {
        var accounts = (await db.Set<D.CrmAccount>().AsNoTracking().Select(a => new { a.Id, a.Name, a.Acronym }).ToListAsync(ct))
            .Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (a.Acronym?.Equals(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(a => a.Id).ToList();
        if (accounts.Count == 0)
        {
            return [];
        }

        var matches = (await query.Where(o => o.AgencyAccountId != null && accounts.Contains(o.AgencyAccountId.Value)).ToListAsync(ct))
            .Where(o => !already.Contains(o.Id)).ToList();
        return await CrmMapping.ToDtosAsync(db, matches, roles, me, ct);
    }

    public async Task<OpportunityDto> CreateAsync(SaveOpportunityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = UtcNow;
        var opportunity = new D.Opportunity
        {
            Number = await NextNumberAsync(db, cancellationToken),
            Name = "",
            CreatedBy = access.Email,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StageChangedAtUtc = now,
            Stage = D.OpportunityStage.Identified,
            Source = D.OpportunitySource.Manual,
        };
        await ApplyAsync(db, opportunity, request with { CaptureManagerEmail = request.CaptureManagerEmail ?? access.Email }, cancellationToken);
        db.Set<D.Opportunity>().Add(opportunity);
        audit.Record(db, AuditEntityType, opportunity.Id, "Created", after: Snapshot(opportunity));
        await db.SaveChangesAsync(cancellationToken);
        await NotifyAssignedAsync(opportunity, null, null);
        return await GetAsync(opportunity.Id, cancellationToken);
    }

    /// <summary>Adds a new opportunity (used by lead acceptance); the caller saves.</summary>
    internal static async Task<D.Opportunity> NewAsync(IWorkbenchDbContext db, string actor, DateTime now, D.OpportunitySource source, CancellationToken ct) =>
        new()
        {
            Number = await NextNumberAsync(db, ct),
            Name = "",
            CreatedBy = actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StageChangedAtUtc = now,
            Stage = D.OpportunityStage.Identified,
            Source = source,
        };

    public async Task<OpportunityDto> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        var before = Snapshot(opportunity);
        var (capture, proposal) = (opportunity.CaptureManagerEmail, opportunity.ProposalManagerEmail);
        await ApplyAsync(db, opportunity, request, cancellationToken);
        opportunity.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, opportunity.Id, "Updated", before, Snapshot(opportunity));
        if (opportunity.BpWbsElementId is { } bp && await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == bp, cancellationToken) is { } element)
        {
            // Keep the B&P code's name in step with the opportunity's.
            element.Name = CrmMapping.Clip($"B&P {opportunity.Number} {opportunity.Name}", 200)!;
        }

        await db.SaveChangesAsync(cancellationToken);
        await NotifyAssignedAsync(opportunity, capture, proposal);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Validates and copies a request onto an opportunity (tracked), replacing its teammates and competitors when given.</summary>
    internal static async Task ApplyAsync(IWorkbenchDbContext db, D.Opportunity o, SaveOpportunityRequest r, CancellationToken ct)
    {
        o.Name = CrmMapping.Required(r.Name, 300, "Name");
        o.Description = CrmMapping.Clean(r.Description, 4000, "Description");
        o.SolicitationNumber = CrmMapping.Clean(r.SolicitationNumber, 100, "Solicitation number");
        o.SolicitationKey = PipelineMath.SolicitationKey(o.SolicitationNumber);
        if (o.SolicitationKey is { } key && await db.Set<D.Opportunity>().AnyAsync(x => x.SolicitationKey == key && x.Id != o.Id, ct))
        {
            var other = await db.Set<D.Opportunity>().Where(x => x.SolicitationKey == key && x.Id != o.Id).Select(x => x.Number).FirstAsync(ct);
            throw new ArgumentException($"Solicitation {o.SolicitationNumber} is already in the pipeline as {other}.");
        }

        if (r.AgencyAccountId is { } agencyId)
        {
            var agency = await db.Set<D.CrmAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == agencyId, ct)
                ?? throw new ArgumentException("Choose an existing agency account.");
            if (agency.Kind is not (D.CrmAccountKind.Agency or D.CrmAccountKind.Office))
            {
                throw new ArgumentException($"{agency.Name} isn't a government agency or office.");
            }
        }

        if (r.IncumbentAccountId is { } incumbent && !await db.Set<D.CrmAccount>().AnyAsync(a => a.Id == incumbent, ct))
        {
            throw new ArgumentException("Choose an existing account for the incumbent.");
        }

        o.AgencyAccountId = r.AgencyAccountId;
        o.AgencyName = CrmMapping.Clean(r.AgencyName, 300, "Agency");
        o.NaicsCode = CrmMapping.Clean(r.NaicsCode, 16, "NAICS code");
        if (o.NaicsCode is { } naics && (naics.Length is < 2 or > 6 || !naics.All(char.IsDigit)))
        {
            throw new ArgumentException("A NAICS code is 2 to 6 digits.");
        }

        o.SetAside = r.SetAside.ToDomain();
        o.ContractVehicle = CrmMapping.Clean(r.ContractVehicle, 200, "Contract vehicle");
        o.ContractType = r.ContractType?.ToDomain();
        o.Kind = r.Kind.ToDomain();
        if (r.EstimatedValue < 0 || r.EstimatedValue > 1_000_000_000_000m)
        {
            throw new ArgumentException("The estimated value must be zero or more.");
        }

        o.EstimatedValue = Math.Round(r.EstimatedValue, 2);
        o.EstimatedAwardDate = r.EstimatedAwardDate;
        if (r.PeriodOfPerformanceMonths is { } months && months is < 1 or > 600)
        {
            throw new ArgumentException("The period of performance is 1 to 600 months.");
        }

        o.PeriodOfPerformanceMonths = r.PeriodOfPerformanceMonths;
        o.RfiDueOn = r.RfiDueOn;
        o.RfpReleaseOn = r.RfpReleaseOn;
        o.ProposalDueOn = r.ProposalDueOn;
        o.PlaceOfPerformance = CrmMapping.Clean(r.PlaceOfPerformance, 300, "Place of performance");
        o.IncumbentAccountId = r.IncumbentAccountId;
        o.IncumbentName = CrmMapping.Clean(r.IncumbentName, 200, "Incumbent");
        o.CaptureManagerEmail = CrmMapping.Email(r.CaptureManagerEmail);
        o.ProposalManagerEmail = CrmMapping.Email(r.ProposalManagerEmail);
        if (r.Pwin is < 0 or > 100 || r.Pgo is < 0 or > 100)
        {
            throw new ArgumentException("Pwin and Pgo are percentages from 0 to 100.");
        }

        o.Pwin = r.Pwin;
        o.Pgo = r.Pgo;
        o.SourceUrl = CrmMapping.Clean(r.SourceUrl, 500, "Source link") ?? o.SourceUrl;

        if (r.Teammates is { } teammates)
        {
            if (teammates.Any(t => t.WorksharePercent is < 0 or > 100) || teammates.Sum(t => t.WorksharePercent) > 100m)
            {
                throw new ArgumentException("Teammates' workshare is 0–100% each and at most 100% together.");
            }

            db.Set<D.OpportunityTeammate>().RemoveRange(await db.Set<D.OpportunityTeammate>().Where(t => t.OpportunityId == o.Id).ToListAsync(ct));
            foreach (var t in teammates)
            {
                db.Set<D.OpportunityTeammate>().Add(new D.OpportunityTeammate
                {
                    OpportunityId = o.Id, AccountId = t.AccountId, Name = await NameOfAsync(db, t.AccountId, t.Name, "teammate", ct),
                    Role = t.Role.ToDomain(), WorksharePercent = Math.Round(t.WorksharePercent, 2), Notes = CrmMapping.Clean(t.Notes, 500, "Teammate notes"),
                });
            }
        }

        if (r.Competitors is { } competitors)
        {
            db.Set<D.OpportunityCompetitor>().RemoveRange(await db.Set<D.OpportunityCompetitor>().Where(c => c.OpportunityId == o.Id).ToListAsync(ct));
            foreach (var c in competitors)
            {
                db.Set<D.OpportunityCompetitor>().Add(new D.OpportunityCompetitor
                {
                    OpportunityId = o.Id, AccountId = c.AccountId, Name = await NameOfAsync(db, c.AccountId, c.Name, "competitor", ct),
                    IsIncumbent = c.IsIncumbent, Notes = CrmMapping.Clean(c.Notes, 1000, "Competitor notes"),
                });
            }
        }
    }

    private static async Task<string> NameOfAsync(IWorkbenchDbContext db, Guid? accountId, string? name, string what, CancellationToken ct)
    {
        if (accountId is { } id)
        {
            return await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.Id == id).Select(a => a.Name).FirstOrDefaultAsync(ct)
                ?? throw new ArgumentException($"Choose an existing account for the {what}.");
        }

        return CrmMapping.Required(name, 200, $"The {what}'s name");
    }

    public async Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        var to = request.Stage.ToDomain();
        if (to >= D.OpportunityStage.Won)
        {
            throw new ArgumentException("Record a win, loss or no-bid as the outcome (with its reason).");
        }

        if (opportunity.Stage >= D.OpportunityStage.Won)
        {
            throw new ArgumentException($"{opportunity.Number} is {OpportunityStages.Label(opportunity.Stage.ToContract()).ToLowerInvariant()}; reopen it first.");
        }

        if (to == opportunity.Stage)
        {
            return await GetAsync(id, cancellationToken);
        }

        if (to > opportunity.Stage && await BlockingGateAsync(db, opportunity, to, cancellationToken) is { } gate)
        {
            throw new ArgumentException($"Moving to {to} needs the \"{gate.Name}\" gate: score its checklist and submit the go/no-go decision.");
        }

        await MoveAsync(db, opportunity, to, access.Email, request.Note, audit, UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyStageAsync(opportunity);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>
    /// The first active gate between the opportunity's stage and <paramref name="to"/> (inclusive) it hasn't passed (no
    /// approved "Go" review for that stage), or null.
    /// </summary>
    internal static async Task<D.GateDefinition?> BlockingGateAsync(IWorkbenchDbContext db, D.Opportunity opportunity, D.OpportunityStage to, CancellationToken ct)
    {
        await GateDefaults.EnsureAsync(db, ct);
        var from = opportunity.Stage;
        var gates = await db.Set<D.GateDefinition>().AsNoTracking().Where(g => g.IsActive).ToListAsync(ct);
        var passed = await db.Set<D.OpportunityGateReview>().AsNoTracking()
            .Where(r => r.OpportunityId == opportunity.Id && r.Status == D.GateReviewStatus.Approved && r.Recommendation == D.GateRecommendation.Go)
            .Select(r => r.ToStage).ToListAsync(ct);
        return gates.Where(g => g.ToStage > from && g.ToStage <= to && !passed.Contains(g.ToStage)).OrderBy(g => g.ToStage).FirstOrDefault();
    }

    /// <summary>Moves a tracked opportunity to an open stage: history row, audit, and the B&amp;P code from Capture on. Returns a warning, if any.</summary>
    internal static async Task<string?> MoveAsync(IWorkbenchDbContext db, D.Opportunity opportunity, D.OpportunityStage to, string actor, string? note,
        AuditRecorder audit, DateTime now, CancellationToken ct)
    {
        var from = opportunity.Stage;
        opportunity.Stage = to;
        opportunity.StageChangedAtUtc = now;
        opportunity.UpdatedAtUtc = now;
        db.Set<D.OpportunityStageChange>().Add(new D.OpportunityStageChange
        {
            OpportunityId = opportunity.Id, From = from, To = to, ChangedBy = actor, ChangedAtUtc = now, Note = CrmMapping.Clip(note, 1000),
        });
        audit.Record(db, AuditEntityType, opportunity.Id, "StageChanged", new { Stage = from.ToString() }, new { Stage = to.ToString() }, CrmMapping.Clip(note, 1000));
        if (from != to)
        {
            IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.OpportunityStageChanged, IntegrationEventPayloads.OpportunityStage(opportunity, from), actor, now);
        }
        string? warning = null;
        if (to >= D.OpportunityStage.Capture && to < D.OpportunityStage.Won)
        {
            (_, warning) = await BpChargeCodes.EnsureAsync(db, opportunity, audit, ct);
            await BpChargeCodes.SetOpenAsync(db, opportunity, true, audit, ct);
        }
        else if (to >= D.OpportunityStage.Won)
        {
            await BpChargeCodes.SetOpenAsync(db, opportunity, false, audit, ct);
        }

        return warning;
    }

    public async Task<OpportunityDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        var outcome = request.Outcome.ToDomain();
        if (outcome < D.OpportunityStage.Won)
        {
            throw new ArgumentException("The outcome is Won, Lost or No-bid.");
        }

        if (opportunity.Stage >= D.OpportunityStage.Won)
        {
            throw new ArgumentException($"{opportunity.Number} already has an outcome.");
        }

        if (outcome != D.OpportunityStage.NoBid && opportunity.Stage != D.OpportunityStage.Submitted)
        {
            throw new ArgumentException("Record a win or loss once the proposal is submitted (move it to Submitted first).");
        }

        if (request.AwardedValue is < 0)
        {
            throw new ArgumentException("The awarded value must be zero or more.");
        }

        opportunity.OutcomeReason = request.Reason.ToDomain();
        opportunity.OutcomeNotes = CrmMapping.Clean(request.Notes, 2000, "Notes");
        opportunity.DecidedOn = request.DecidedOn ?? Today;
        opportunity.AwardedValue = outcome == D.OpportunityStage.Won ? Math.Round(request.AwardedValue ?? opportunity.EstimatedValue, 2) : request.AwardedValue;
        opportunity.AwardedTo = outcome == D.OpportunityStage.Lost ? CrmMapping.Clean(request.AwardedTo, 200, "Awarded to") : null;
        if (outcome == D.OpportunityStage.Won)
        {
            opportunity.Pwin = 100;
            opportunity.Pgo = 100;
        }

        await MoveAsync(db, opportunity, outcome, access.Email, request.Notes, audit, UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyStageAsync(opportunity);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<OpportunityDto> ReopenAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        var to = request.Stage.ToDomain();
        if (opportunity.Stage < D.OpportunityStage.Won)
        {
            throw new ArgumentException($"{opportunity.Number} is still open.");
        }

        if (opportunity.ContractId is not null)
        {
            throw new ArgumentException($"{opportunity.Number} was handed off to a contract; it can't be reopened.");
        }

        if (to >= D.OpportunityStage.Won)
        {
            throw new ArgumentException("Reopen it to a pursuit stage.");
        }

        var reached = await db.Set<D.OpportunityStageChange>().Where(c => c.OpportunityId == id && CrmMapping.OpenStages.Contains(c.To))
            .Select(c => c.To).ToListAsync(cancellationToken);
        var furthest = reached.Count == 0 ? D.OpportunityStage.Identified : reached.Max();
        if (to > furthest)
        {
            throw new ArgumentException($"It only reached {furthest}; reopen it there or earlier.");
        }

        opportunity.OutcomeReason = null;
        opportunity.OutcomeNotes = null;
        opportunity.DecidedOn = null;
        opportunity.AwardedValue = null;
        opportunity.AwardedTo = null;
        await MoveAsync(db, opportunity, to, access.Email, request.Note ?? "Reopened", audit, UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<StageChangeDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<D.OpportunityStageChange>().AsNoTracking().Where(c => c.OpportunityId == id).ToListAsync(cancellationToken);
        return rows.OrderByDescending(c => c.ChangedAtUtc).Select(c => new StageChangeDto(c.From.ToContract(), c.To.ToContract(), c.ChangedBy, c.ChangedAtUtc, c.Note)).ToList();
    }

    public async Task<OpportunityDto> EnsureBpChargeCodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        if (opportunity.Stage is < D.OpportunityStage.Capture or >= D.OpportunityStage.Won)
        {
            throw new ArgumentException("B&P charge codes are for opportunities in Capture, Proposal or Submitted.");
        }

        var (_, problem) = await BpChargeCodes.EnsureAsync(db, opportunity, audit, cancellationToken);
        if (problem is not null)
        {
            throw new ArgumentException(problem);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<HandoffResultDto> HandoffAsync(Guid id, HandoffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await LoadAsync(db, id, cancellationToken);
        if (opportunity.Stage != D.OpportunityStage.Won)
        {
            throw new ArgumentException("Only a won opportunity can be handed off to a contract.");
        }

        if (opportunity.ContractId is not null)
        {
            throw new ArgumentException($"{opportunity.Number} was already handed off.");
        }

        // Customer: the one named, the agency account's, or a new one.
        var agency = opportunity.AgencyAccountId is { } agencyId ? await db.Set<D.CrmAccount>().FirstOrDefaultAsync(a => a.Id == agencyId, cancellationToken) : null;
        Customer customer;
        var createdCustomer = false;
        if ((request.CustomerId ?? agency?.CustomerId) is { } customerId)
        {
            customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
                ?? throw new ArgumentException("Choose an existing customer.");
        }
        else
        {
            var code = CrmMapping.Required(request.CustomerCode, 32, "Customer code").ToUpperInvariant();
            if (await db.Set<Customer>().AnyAsync(c => c.Code == code, cancellationToken))
            {
                throw new ArgumentException($"Customer code {code} is taken; choose that customer or another code.");
            }

            customer = new Customer { Code = code, Name = CrmMapping.Clean(request.CustomerName, 200, "Customer name") ?? agency?.Name ?? opportunity.AgencyName ?? code, Category = "Federal" };
            db.Set<Customer>().Add(customer);
            audit.Record(db, nameof(Customer), customer.Id, "Created", after: new { customer.Code, customer.Name, customer.Category }, reason: $"Won opportunity {opportunity.Number}");
            createdCustomer = true;
        }

        if (agency is not null && agency.CustomerId is null)
        {
            agency.CustomerId = customer.Id;
        }

        var number = CrmMapping.Required(request.ContractNumber, 64, "Contract number");
        if (await db.Set<Contract>().AnyAsync(c => c.ContractNumber == number, cancellationToken))
        {
            throw new ArgumentException($"Contract {number} already exists.");
        }

        var projectCode = CrmMapping.Required(request.ProjectCode, 64, "Project code");
        if (await db.Set<Project>().AnyAsync(p => p.Code == projectCode, cancellationToken))
        {
            throw new ArgumentException($"Project {projectCode} already exists.");
        }

        if (request.ProjectManagerId is { } pm && !await db.Set<Employee>().AnyAsync(e => e.Id == pm, cancellationToken))
        {
            throw new ArgumentException("Choose an existing employee as the project manager.");
        }

        var start = opportunity.EstimatedAwardDate is { } award && award >= (opportunity.DecidedOn ?? award) ? award : opportunity.DecidedOn ?? Today;
        var months = opportunity.PeriodOfPerformanceMonths ?? 12;
        var end = start.AddMonths(months).AddDays(-1);
        var value = opportunity.AwardedValue ?? opportunity.EstimatedValue;
        var type = opportunity.ContractType ?? ContractType.FirmFixedPrice;
        var isPrime = !await db.Set<D.OpportunityTeammate>().AnyAsync(t => t.OpportunityId == id && t.Role == D.TeamingRole.Prime, cancellationToken);
        var contract = new Contract
        {
            CustomerId = customer.Id, ContractNumber = number, Name = CrmMapping.Clean(request.ContractName, 200, "Contract name") ?? CrmMapping.Clip(opportunity.Name, 200)!,
            Type = type, Status = ContractStatus.Pending, IsPrime = isPrime, PeriodStart = start, PeriodEnd = end, CeilingValue = value, FundedValue = 0m,
        };
        db.Set<Contract>().Add(contract);
        audit.Record(db, nameof(Contract), contract.Id, "Created",
            after: new { contract.ContractNumber, contract.Name, Type = contract.Type.ToString(), Status = contract.Status.ToString(), contract.PeriodStart, contract.PeriodEnd, contract.CeilingValue },
            reason: $"Won opportunity {opportunity.Number}");

        var clins = (request.Clins is { Count: > 0 } given ? given : DefaultClins(type, start, months, value)).ToList();
        if (clins.Select(c => c.Number.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clins.Count)
        {
            throw new ArgumentException("CLIN numbers must be unique.");
        }

        var clinIds = new List<Guid>();
        var order = 0;
        foreach (var c in clins)
        {
            if (c.PeriodEnd < c.PeriodStart || c.CeilingValue < 0)
            {
                throw new ArgumentException($"CLIN {c.Number}: check its period and value.");
            }

            var clin = new Clin
            {
                ContractId = contract.Id, Number = CrmMapping.Required(c.Number, 16, "CLIN number"), Description = CrmMapping.Required(c.Description, 300, "CLIN description"),
                Pricing = (ClinPricing)(int)c.Pricing, IsOption = c.IsOption, IsExercised = !c.IsOption, PeriodStart = c.PeriodStart, PeriodEnd = c.PeriodEnd,
                CeilingValue = Math.Round(c.CeilingValue, 2), FundedValue = 0m, FeeRate = null, Order = order++,
            };
            db.Set<Clin>().Add(clin);
            clinIds.Add(clin.Id);
            audit.Record(db, nameof(Clin), clin.Id, "Created", after: new { clin.Number, clin.Description, Pricing = clin.Pricing.ToString(), clin.IsOption, clin.CeilingValue },
                reason: $"Placeholder from won opportunity {opportunity.Number}");
        }

        var project = new Project
        {
            Code = projectCode, Name = CrmMapping.Clean(request.ProjectName, 200, "Project name") ?? CrmMapping.Clip(opportunity.Name, 200)!,
            ContractId = contract.Id, ProjectManagerId = request.ProjectManagerId, Status = ProjectStatus.Planning, Start = start, Finish = end,
        };
        db.Set<Project>().Add(project);
        audit.Record(db, nameof(Project), project.Id, "Created", after: new { project.Code, project.Name, Status = project.Status.ToString(), project.ContractId },
            reason: $"Won opportunity {opportunity.Number}");
        IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.ProjectCreated, IntegrationEventPayloads.Project(project), audit.Actor, UtcNow);

        opportunity.CustomerId = customer.Id;
        opportunity.ContractId = contract.Id;
        opportunity.ProjectId = project.Id;
        opportunity.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, opportunity.Id, "HandedOff", after: new { CustomerId = customer.Id, ContractId = contract.Id, ProjectId = project.Id });
        await db.SaveChangesAsync(cancellationToken);

        await notifications.PublishAsync(new[] { opportunity.CaptureManagerEmail, opportunity.ProposalManagerEmail }.Where(e => e != access.Email),
            CrmNotificationKinds.HandedOff, $"{opportunity.Number} {opportunity.Name} was handed off", $"Contract {contract.ContractNumber}, project {project.Code} (planning)",
            link: $"setup/contracts/{contract.Id}");
        return new HandoffResultDto(opportunity.Id, customer.Id, contract.Id, project.Id, clinIds, createdCustomer);
    }

    /// <summary>Placeholders: a 12-month base CLIN 0001 and option CLINs 1001, 2001, … for each further year, the value split evenly.</summary>
    public static IReadOnlyList<HandoffClin> DefaultClins(ContractType type, DateOnly start, int months, decimal value)
    {
        var periods = Math.Max(1, (int)Math.Ceiling(months / 12m));
        var share = Math.Round(value / periods, 2, MidpointRounding.ToZero);
        var pricing = (ClinPricingKind)(int)type;
        var list = new List<HandoffClin>();
        for (var i = 0; i < periods; i++)
        {
            var from = start.AddMonths(12 * i);
            var to = i == periods - 1 ? start.AddMonths(months).AddDays(-1) : start.AddMonths(12 * (i + 1)).AddDays(-1);
            var amount = i == periods - 1 ? value - share * (periods - 1) : share;
            list.Add(i == 0
                ? new HandoffClin("0001", "Base period (placeholder)", pricing, amount, from, to, false)
                : new HandoffClin($"{i}001", $"Option period {i} (placeholder)", pricing, amount, from, to, true));
        }

        return list;
    }

    internal static async Task<string> NextNumberAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        return (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.Opportunity, null, DateOnly.FromDateTime(DateTime.UtcNow), ct)).Number;
    }

    private static async Task<D.Opportunity> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<D.Opportunity>().FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Opportunity", id);

    internal static object Snapshot(D.Opportunity o) => new
    {
        o.Number, o.Name, o.SolicitationNumber, o.AgencyAccountId, o.AgencyName, o.NaicsCode, SetAside = o.SetAside.ToString(), o.ContractVehicle,
        ContractType = o.ContractType?.ToString(), Kind = o.Kind.ToString(), o.EstimatedValue, o.EstimatedAwardDate, o.PeriodOfPerformanceMonths,
        o.ProposalDueOn, o.CaptureManagerEmail, o.ProposalManagerEmail, Stage = o.Stage.ToString(), o.Pwin, o.Pgo,
    };

    private async Task NotifyAssignedAsync(D.Opportunity o, string? captureBefore, string? proposalBefore)
    {
        var me = access.Email;
        if (o.CaptureManagerEmail is { } capture && capture != captureBefore && capture != me)
        {
            await notifications.PublishAsync(capture, CrmNotificationKinds.Assigned, $"You're the capture manager for {o.Number} {o.Name}", link: $"bd/opportunities/{o.Id}");
        }

        if (o.ProposalManagerEmail is { } proposal && proposal != proposalBefore && proposal != me)
        {
            await notifications.PublishAsync(proposal, CrmNotificationKinds.Assigned, $"You're the proposal manager for {o.Number} {o.Name}", link: $"bd/opportunities/{o.Id}");
        }
    }

    private Task NotifyStageAsync(D.Opportunity o) =>
        notifications.PublishAsync(new[] { o.CaptureManagerEmail, o.ProposalManagerEmail }.Where(e => e != access.Email),
            CrmNotificationKinds.StageChanged, $"{o.Number} {o.Name} is now {OpportunityStages.Label(o.Stage.ToContract())}", link: $"bd/opportunities/{o.Id}");
}
