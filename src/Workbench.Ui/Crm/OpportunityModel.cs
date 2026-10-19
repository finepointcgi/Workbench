using Workbench.Contracts.Crm;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Ui.Crm;

/// <summary>The editable copy of an opportunity's details behind the form.</summary>
public sealed class OpportunityModel
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? SolicitationNumber { get; set; }
    public Guid? AgencyAccountId { get; set; }
    public string? AgencyName { get; set; }
    public string? NaicsCode { get; set; }
    public SetAside SetAside { get; set; }
    public string? ContractVehicle { get; set; }
    public ContractKind? ContractType { get; set; }
    public OpportunityKind Kind { get; set; }
    public decimal EstimatedValue { get; set; }
    public DateTime? EstimatedAwardDate { get; set; }
    public int? PeriodOfPerformanceMonths { get; set; }
    public DateTime? RfiDueOn { get; set; }
    public DateTime? RfpReleaseOn { get; set; }
    public DateTime? ProposalDueOn { get; set; }
    public string? PlaceOfPerformance { get; set; }
    public Guid? IncumbentAccountId { get; set; }
    public string? IncumbentName { get; set; }
    public string? CaptureManagerEmail { get; set; }
    public string? ProposalManagerEmail { get; set; }
    public int Pwin { get; set; } = 10;
    public int Pgo { get; set; } = 50;
    public List<SaveTeammate> Teammates { get; set; } = [];
    public List<SaveCompetitor> Competitors { get; set; } = [];
    public string? SourceUrl { get; set; }

    public static OpportunityModel From(OpportunityDto o)
    {
        var r = CrmFormat.ToRequest(o);
        return new OpportunityModel
        {
            Name = r.Name, Description = r.Description, SolicitationNumber = r.SolicitationNumber, AgencyAccountId = r.AgencyAccountId, AgencyName = r.AgencyName,
            NaicsCode = r.NaicsCode, SetAside = r.SetAside, ContractVehicle = r.ContractVehicle, ContractType = r.ContractType, Kind = r.Kind,
            EstimatedValue = r.EstimatedValue, EstimatedAwardDate = CrmFormat.ToDateTime(r.EstimatedAwardDate), PeriodOfPerformanceMonths = r.PeriodOfPerformanceMonths,
            RfiDueOn = CrmFormat.ToDateTime(r.RfiDueOn), RfpReleaseOn = CrmFormat.ToDateTime(r.RfpReleaseOn), ProposalDueOn = CrmFormat.ToDateTime(r.ProposalDueOn),
            PlaceOfPerformance = r.PlaceOfPerformance, IncumbentAccountId = r.IncumbentAccountId, IncumbentName = r.IncumbentName,
            CaptureManagerEmail = r.CaptureManagerEmail, ProposalManagerEmail = r.ProposalManagerEmail, Pwin = r.Pwin, Pgo = r.Pgo,
            Teammates = r.Teammates?.ToList() ?? [], Competitors = r.Competitors?.ToList() ?? [], SourceUrl = r.SourceUrl,
        };
    }

    public SaveOpportunityRequest ToRequest() => new(
        Name, Description, SolicitationNumber, AgencyAccountId, AgencyAccountId is null ? AgencyName : null, NaicsCode, SetAside, ContractVehicle, ContractType, Kind,
        EstimatedValue, CrmFormat.ToDateOnly(EstimatedAwardDate), PeriodOfPerformanceMonths, CrmFormat.ToDateOnly(RfiDueOn), CrmFormat.ToDateOnly(RfpReleaseOn),
        CrmFormat.ToDateOnly(ProposalDueOn), PlaceOfPerformance, IncumbentAccountId, IncumbentAccountId is null ? IncumbentName : null,
        string.IsNullOrWhiteSpace(CaptureManagerEmail) ? null : CaptureManagerEmail, string.IsNullOrWhiteSpace(ProposalManagerEmail) ? null : ProposalManagerEmail,
        Pwin, Pgo, Teammates, Competitors, SourceUrl);

    public decimal Weighted => Math.Round(EstimatedValue * Math.Clamp(Pwin, 0, 100) / 100m * Math.Clamp(Pgo, 0, 100) / 100m, 2);
}
