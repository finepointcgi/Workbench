using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Crm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CrmAccountConfiguration : IEntityTypeConfiguration<CrmAccount>
{
    public void Configure(EntityTypeBuilder<CrmAccount> b)
    {
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.Acronym).HasMaxLength(32);
        b.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Uei).HasMaxLength(16);
        b.Property(a => a.CageCode).HasMaxLength(8);
        b.Property(a => a.Website).HasMaxLength(300);
        b.Property(a => a.Phone).HasMaxLength(64);
        b.Property(a => a.Address).HasMaxLength(500);
        b.Property(a => a.Notes).HasMaxLength(4000);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.HasIndex(a => a.Name);
        b.HasIndex(a => a.ParentId);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmContactConfiguration : IEntityTypeConfiguration<CrmContact>
{
    public void Configure(EntityTypeBuilder<CrmContact> b)
    {
        b.Property(c => c.FirstName).HasMaxLength(100);
        b.Property(c => c.LastName).HasMaxLength(100);
        b.Property(c => c.Title).HasMaxLength(200);
        b.Property(c => c.Role).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Email).HasMaxLength(320);
        b.Property(c => c.Phone).HasMaxLength(64);
        b.Property(c => c.Notes).HasMaxLength(2000);
        b.HasIndex(c => c.AccountId);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmActivityConfiguration : IEntityTypeConfiguration<CrmActivity>
{
    public void Configure(EntityTypeBuilder<CrmActivity> b)
    {
        b.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Subject).HasMaxLength(300);
        b.Property(a => a.Body).HasMaxLength(4000);
        b.Property(a => a.AssignedTo).HasMaxLength(320);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.HasIndex(a => a.AccountId);
        b.HasIndex(a => a.ContactId);
        b.HasIndex(a => a.OpportunityId);
        b.HasIndex(a => new { a.AssignedTo, a.IsDone });
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CrmContact>().WithMany().HasForeignKey(a => a.ContactId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(a => a.OpportunityId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> b)
    {
        b.Property(o => o.Number).HasMaxLength(32);
        b.Property(o => o.Name).HasMaxLength(300);
        b.Property(o => o.Description).HasMaxLength(4000);
        b.Property(o => o.SolicitationNumber).HasMaxLength(100);
        b.Property(o => o.SolicitationKey).HasMaxLength(100);
        b.Property(o => o.AgencyName).HasMaxLength(300);
        b.Property(o => o.NaicsCode).HasMaxLength(16);
        b.Property(o => o.SetAside).HasConversion<string>().HasMaxLength(48);
        b.Property(o => o.ContractVehicle).HasMaxLength(200);
        b.Property(o => o.ContractType).HasConversion<string>().HasMaxLength(32);
        b.Property(o => o.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.EstimatedValue).Money();
        b.Property(o => o.PlaceOfPerformance).HasMaxLength(300);
        b.Property(o => o.IncumbentName).HasMaxLength(200);
        b.Property(o => o.CaptureManagerEmail).HasMaxLength(320);
        b.Property(o => o.ProposalManagerEmail).HasMaxLength(320);
        b.Property(o => o.Stage).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.SourceUrl).HasMaxLength(500);
        b.Property(o => o.SamNoticeId).HasMaxLength(64);
        b.Property(o => o.SourceId).HasMaxLength(100);
        b.HasIndex(o => new { o.Source, o.SourceId });
        b.Property(o => o.OutcomeReason).HasConversion<string>().HasMaxLength(32);
        b.Property(o => o.OutcomeNotes).HasMaxLength(2000);
        b.Property(o => o.AwardedValue).HasPrecision(18, 2);
        b.Property(o => o.AwardedTo).HasMaxLength(200);
        b.Property(o => o.CreatedBy).HasMaxLength(320);
        b.HasIndex(o => o.Number).IsUnique();
        b.HasIndex(o => o.SolicitationKey);
        b.HasIndex(o => o.Stage);
        b.HasIndex(o => o.CaptureManagerEmail);
        b.HasIndex(o => o.ProposalManagerEmail);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(o => o.AgencyAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(o => o.IncumbentAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(o => o.BpWbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany().HasForeignKey(o => o.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityTeammateConfiguration : IEntityTypeConfiguration<OpportunityTeammate>
{
    public void Configure(EntityTypeBuilder<OpportunityTeammate> b)
    {
        b.Property(t => t.Name).HasMaxLength(200);
        b.Property(t => t.Role).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.WorksharePercent).HasPrecision(5, 2);
        b.Property(t => t.Notes).HasMaxLength(500);
        b.HasIndex(t => t.OpportunityId);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(t => t.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityCompetitorConfiguration : IEntityTypeConfiguration<OpportunityCompetitor>
{
    public void Configure(EntityTypeBuilder<OpportunityCompetitor> b)
    {
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Notes).HasMaxLength(1000);
        b.HasIndex(c => c.OpportunityId);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(c => c.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CrmAccount>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityStageChangeConfiguration : IEntityTypeConfiguration<OpportunityStageChange>
{
    public void Configure(EntityTypeBuilder<OpportunityStageChange> b)
    {
        b.Property(c => c.From).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.To).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.ChangedBy).HasMaxLength(320);
        b.Property(c => c.Note).HasMaxLength(1000);
        b.HasIndex(c => c.OpportunityId);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(c => c.OpportunityId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GateDefinitionConfiguration : IEntityTypeConfiguration<GateDefinition>
{
    public void Configure(EntityTypeBuilder<GateDefinition> b)
    {
        b.Property(g => g.Name).HasMaxLength(200);
        b.Property(g => g.ToStage).HasConversion<string>().HasMaxLength(16);
        b.Property(g => g.Description).HasMaxLength(1000);
        b.Property(g => g.UpdatedBy).HasMaxLength(320);
        b.HasMany(g => g.Criteria).WithOne().HasForeignKey(c => c.GateDefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(g => g.ToStage);
    }
}

internal sealed class GateCriterionConfiguration : IEntityTypeConfiguration<GateCriterion>
{
    public void Configure(EntityTypeBuilder<GateCriterion> b)
    {
        b.Property(c => c.Text).HasMaxLength(300);
        b.Property(c => c.Guidance).HasMaxLength(500);
    }
}

internal sealed class OpportunityGateReviewConfiguration : IEntityTypeConfiguration<OpportunityGateReview>
{
    public void Configure(EntityTypeBuilder<OpportunityGateReview> b)
    {
        b.Property(r => r.GateName).HasMaxLength(200);
        b.Property(r => r.ToStage).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Recommendation).HasConversion<string>().HasMaxLength(8);
        b.Property(r => r.ScorePercent).HasPrecision(5, 2);
        b.Property(r => r.Notes).HasMaxLength(2000);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.SubmittedBy).HasMaxLength(320);
        b.Property(r => r.DecidedBy).HasMaxLength(320);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);
        b.HasIndex(r => new { r.OpportunityId, r.Status });
        b.HasOne<Opportunity>().WithMany().HasForeignKey(r => r.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GateDefinition>().WithMany().HasForeignKey(r => r.GateDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Scores).WithOne().HasForeignKey(s => s.ReviewId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityGateScoreConfiguration : IEntityTypeConfiguration<OpportunityGateScore>
{
    public void Configure(EntityTypeBuilder<OpportunityGateScore> b)
    {
        b.Property(s => s.Text).HasMaxLength(300);
        b.Property(s => s.Comment).HasMaxLength(500);
    }
}

internal sealed class SavedLeadSearchConfiguration : IEntityTypeConfiguration<SavedLeadSearch>
{
    public void Configure(EntityTypeBuilder<SavedLeadSearch> b)
    {
        b.Property(s => s.Name).HasMaxLength(200);
        b.Property(s => s.NaicsCodes).HasMaxLength(500);
        b.Property(s => s.Agencies).HasMaxLength(500);
        b.Property(s => s.SetAsides).HasMaxLength(300);
        b.Property(s => s.Keywords).HasMaxLength(500);
        b.Property(s => s.OwnerEmail).HasMaxLength(320);
    }
}

internal sealed class OpportunityLeadConfiguration : IEntityTypeConfiguration<OpportunityLead>
{
    public void Configure(EntityTypeBuilder<OpportunityLead> b)
    {
        b.Property(l => l.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.SourceId).HasMaxLength(100);
        b.Property(l => l.SolicitationNumber).HasMaxLength(100);
        b.Property(l => l.SolicitationKey).HasMaxLength(100);
        b.Property(l => l.Title).HasMaxLength(500);
        b.Property(l => l.Agency).HasMaxLength(300);
        b.Property(l => l.Office).HasMaxLength(300);
        b.Property(l => l.NaicsCode).HasMaxLength(16);
        b.Property(l => l.SetAside).HasConversion<string>().HasMaxLength(48);
        b.Property(l => l.NoticeType).HasMaxLength(64);
        b.Property(l => l.PlaceOfPerformance).HasMaxLength(300);
        b.Property(l => l.Url).HasMaxLength(500);
        b.Property(l => l.Description).HasMaxLength(4000);
        b.Property(l => l.EstimatedValue).HasPrecision(18, 2);
        b.Property(l => l.FitReason).HasMaxLength(300);
        b.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.ReviewedBy).HasMaxLength(320);
        b.HasIndex(l => l.SolicitationKey);
        b.HasIndex(l => l.SourceId);
        b.HasIndex(l => l.Status);
        b.HasOne<SavedLeadSearch>().WithMany().HasForeignKey(l => l.SavedSearchId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(l => l.OpportunityId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmSettingsConfiguration : IEntityTypeConfiguration<CrmSettings>
{
    public void Configure(EntityTypeBuilder<CrmSettings> b)
    {
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.Property(s => s.CompanyProfile).HasMaxLength(8000);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(s => s.BpParentWbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProposalSectionConfiguration : IEntityTypeConfiguration<ProposalSection>
{
    public void Configure(EntityTypeBuilder<ProposalSection> b)
    {
        b.Property(s => s.Key).HasMaxLength(32);
        b.Property(s => s.Title).HasMaxLength(200);
        b.Property(s => s.Content).HasMaxLength(30000);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => new { s.OpportunityId, s.Key }).IsUnique();
        b.HasOne<Opportunity>().WithMany().HasForeignKey(s => s.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityPlanItemConfiguration : IEntityTypeConfiguration<OpportunityPlanItem>
{
    public void Configure(EntityTypeBuilder<OpportunityPlanItem> b)
    {
        b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Text).HasMaxLength(500);
        b.Property(i => i.Detail).HasMaxLength(2000);
        b.Property(i => i.CreatedBy).HasMaxLength(320);
        b.HasIndex(i => i.OpportunityId);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(i => i.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityRequirementConfiguration : IEntityTypeConfiguration<OpportunityRequirement>
{
    public void Configure(EntityTypeBuilder<OpportunityRequirement> b)
    {
        b.Property(r => r.Reference).HasMaxLength(64);
        b.Property(r => r.Text).HasMaxLength(1200);
        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ProposalSectionKey).HasMaxLength(32);
        b.Property(r => r.Notes).HasMaxLength(2000);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => r.OpportunityId);
        b.HasOne<Opportunity>().WithMany().HasForeignKey(r => r.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityMilestoneConfiguration : IEntityTypeConfiguration<OpportunityMilestone>
{
    public void Configure(EntityTypeBuilder<OpportunityMilestone> b)
    {
        b.Property(m => m.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.Name).HasMaxLength(200);
        b.Property(m => m.OwnerEmail).HasMaxLength(320);
        b.Property(m => m.Notes).HasMaxLength(1000);
        b.Property(m => m.CreatedBy).HasMaxLength(320);
        b.HasIndex(m => m.OpportunityId);
        b.HasIndex(m => new { m.IsDone, m.DueOn });
        b.HasOne<Opportunity>().WithMany().HasForeignKey(m => m.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityPwinAssessmentConfiguration : IEntityTypeConfiguration<OpportunityPwinAssessment>
{
    public void Configure(EntityTypeBuilder<OpportunityPwinAssessment> b)
    {
        b.Property(a => a.Notes).HasMaxLength(2000);
        b.Property(a => a.AssessedBy).HasMaxLength(320);
        b.HasIndex(a => a.OpportunityId).IsUnique();
        b.HasOne<Opportunity>().WithMany().HasForeignKey(a => a.OpportunityId).OnDelete(DeleteBehavior.Cascade);
    }
}
