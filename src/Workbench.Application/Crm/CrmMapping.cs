using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>Domain ↔ contract conversions (the enums share names and values) and the opportunity DTO loader.</summary>
internal static class CrmMapping
{
    // Stages are stored as text, so queries test membership: "Lost" < "Won" as strings, whatever the enum order says.
    public static readonly D.OpportunityStage[] OpenStages =
        [D.OpportunityStage.Identified, D.OpportunityStage.Qualified, D.OpportunityStage.Capture, D.OpportunityStage.Proposal, D.OpportunityStage.Submitted];

    public static readonly D.OpportunityStage[] OutcomeStages = [D.OpportunityStage.Won, D.OpportunityStage.Lost, D.OpportunityStage.NoBid];

    public static OpportunityStage ToContract(this D.OpportunityStage s) => (OpportunityStage)(int)s;
    public static D.OpportunityStage ToDomain(this OpportunityStage s) => (D.OpportunityStage)(int)s;
    public static SetAside ToContract(this D.SetAside s) => (SetAside)(int)s;
    public static D.SetAside ToDomain(this SetAside s) => (D.SetAside)(int)s;
    public static OpportunityKind ToContract(this D.OpportunityKind k) => (OpportunityKind)(int)k;
    public static D.OpportunityKind ToDomain(this OpportunityKind k) => (D.OpportunityKind)(int)k;
    public static OpportunitySource ToContract(this D.OpportunitySource s) => (OpportunitySource)(int)s;
    public static OutcomeReason ToContract(this D.OutcomeReason r) => (OutcomeReason)(int)r;
    public static D.OutcomeReason ToDomain(this OutcomeReason r) => (D.OutcomeReason)(int)r;
    public static TeamingRole ToContract(this D.TeamingRole r) => (TeamingRole)(int)r;
    public static D.TeamingRole ToDomain(this TeamingRole r) => (D.TeamingRole)(int)r;
    public static CrmAccountKind ToContract(this D.CrmAccountKind k) => (CrmAccountKind)(int)k;
    public static D.CrmAccountKind ToDomain(this CrmAccountKind k) => (D.CrmAccountKind)(int)k;
    public static ContactRole ToContract(this D.ContactRole r) => (ContactRole)(int)r;
    public static D.ContactRole ToDomain(this ContactRole r) => (D.ContactRole)(int)r;
    public static CrmActivityKind ToContract(this D.CrmActivityKind k) => (CrmActivityKind)(int)k;
    public static D.CrmActivityKind ToDomain(this CrmActivityKind k) => (D.CrmActivityKind)(int)k;
    public static GateRecommendation ToContract(this D.GateRecommendation r) => (GateRecommendation)(int)r;
    public static D.GateRecommendation ToDomain(this GateRecommendation r) => (D.GateRecommendation)(int)r;
    public static GateReviewStatus ToContract(this D.GateReviewStatus s) => (GateReviewStatus)(int)s;
    public static LeadStatus ToContract(this D.LeadStatus s) => (LeadStatus)(int)s;
    public static ContractKind ToContract(this ContractType t) => (ContractKind)(int)t;
    public static ContractType ToDomain(this ContractKind k) => (ContractType)(int)k;

    public static string? Clean(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= max ? value : throw new ArgumentException($"{field} can be at most {max} characters.");
    }

    public static string Required(string? value, int max, string field) =>
        Clean(value, max, field) ?? throw new ArgumentException($"{field} is required.");

    public static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    public static string? Email(string? value)
    {
        var email = Identity.AccessControl.Normalize(value);
        if (email.Length == 0)
        {
            return null;
        }

        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.Length <= 320 && !email.Any(char.IsWhiteSpace)
            ? email
            : throw new ArgumentException($"\"{value}\" isn't an email address.");
    }

    /// <summary>Opportunity DTOs with the names they show (accounts, managers, B&amp;P code) and team, loaded in a few queries.</summary>
    public static async Task<List<OpportunityDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<D.Opportunity> opportunities, PermissionSet roles, string viewer, CancellationToken ct)
    {
        if (opportunities.Count == 0)
        {
            return [];
        }

        var ids = opportunities.Select(o => o.Id).ToList();
        var accountIds = opportunities.SelectMany(o => new[] { o.AgencyAccountId, o.IncumbentAccountId }).OfType<Guid>().Distinct().ToList();
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var emails = opportunities.SelectMany(o => new[] { o.CaptureManagerEmail, o.ProposalManagerEmail }).OfType<string>().Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName, ct);
        var wbsIds = opportunities.Where(o => o.BpWbsElementId != null).Select(o => o.BpWbsElementId!.Value).ToList();
        var codes = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var teammates = (await db.Set<D.OpportunityTeammate>().AsNoTracking().Where(t => ids.Contains(t.OpportunityId)).ToListAsync(ct))
            .ToLookup(t => t.OpportunityId);
        var competitors = (await db.Set<D.OpportunityCompetitor>().AsNoTracking().Where(c => ids.Contains(c.OpportunityId)).ToListAsync(ct))
            .ToLookup(c => c.OpportunityId);

        string? Name(string? email) => email is null ? null : names.GetValueOrDefault(email) ?? email;
        return opportunities.Select(o => new OpportunityDto(
            o.Id, o.Number, o.Name, o.Description, o.SolicitationNumber, o.AgencyAccountId,
            o.AgencyAccountId is { } agency && accounts.TryGetValue(agency, out var agencyName) ? agencyName : o.AgencyName,
            o.NaicsCode, o.SetAside.ToContract(), o.ContractVehicle, o.ContractType?.ToContract(), o.Kind.ToContract(), o.EstimatedValue,
            o.EstimatedAwardDate, o.PeriodOfPerformanceMonths, o.RfiDueOn, o.RfpReleaseOn, o.ProposalDueOn, o.PlaceOfPerformance,
            o.IncumbentAccountId, o.IncumbentAccountId is { } inc && accounts.TryGetValue(inc, out var incName) ? incName : o.IncumbentName,
            o.CaptureManagerEmail, Name(o.CaptureManagerEmail), o.ProposalManagerEmail, Name(o.ProposalManagerEmail),
            o.Stage.ToContract(), o.StageChangedAtUtc, o.Pwin, o.Pgo, PipelineMath.Weighted(o.EstimatedValue, o.Pwin, o.Pgo),
            o.Source.ToContract(), o.SourceUrl, o.OutcomeReason?.ToContract(), o.OutcomeNotes, o.DecidedOn, o.AwardedValue, o.AwardedTo,
            o.BpWbsElementId, o.BpWbsElementId is { } w ? codes.GetValueOrDefault(w) : null,
            o.CustomerId, o.ContractId, o.ProjectId,
            teammates[o.Id].OrderBy(t => t.Name).Select(t => new TeammateDto(t.Id, t.AccountId, t.Name, t.Role.ToContract(), t.WorksharePercent, t.Notes)).ToList(),
            competitors[o.Id].OrderByDescending(c => c.IsIncumbent).ThenBy(c => c.Name).Select(c => new CompetitorDto(c.Id, c.AccountId, c.Name, c.IsIncumbent, c.Notes)).ToList(),
            o.CreatedAtUtc, o.UpdatedAtUtc, CrmAccess.CanEdit(o, roles, viewer))).ToList();
    }
}
