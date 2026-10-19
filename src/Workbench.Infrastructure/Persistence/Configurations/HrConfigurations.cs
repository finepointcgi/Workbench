using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Files;
using Workbench.Domain.Hr;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeHrProfileConfiguration : IEntityTypeConfiguration<EmployeeHrProfile>
{
    public void Configure(EntityTypeBuilder<EmployeeHrProfile> b)
    {
        b.Property(p => p.JobTitle).HasMaxLength(200);
        b.Property(p => p.EmploymentType).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.WorkLocation).HasMaxLength(200);
        b.Property(p => p.WorkState).HasMaxLength(2);
        b.Property(p => p.PreferredName).HasMaxLength(200);
        b.Property(p => p.PersonalEmail).HasMaxLength(320);
        b.Property(p => p.PersonalPhone).HasMaxLength(40);
        b.Property(p => p.AddressLine1).HasMaxLength(200);
        b.Property(p => p.AddressLine2).HasMaxLength(200);
        b.Property(p => p.City).HasMaxLength(100);
        b.Property(p => p.State).HasMaxLength(2);
        b.Property(p => p.PostalCode).HasMaxLength(10);
        b.Property(p => p.TerminationReason).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.EmployeeId).IsUnique();
        b.HasIndex(p => p.PositionId);
        b.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Position>().WithMany().HasForeignKey(p => p.PositionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeEeoRecordConfiguration : IEntityTypeConfiguration<EmployeeEeoRecord>
{
    public void Configure(EntityTypeBuilder<EmployeeEeoRecord> b)
    {
        b.Property(e => e.Gender).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Race).HasConversion<string>().HasMaxLength(40);
        b.Property(e => e.JobCategory).HasConversion<string>().HasMaxLength(40);
        b.Property(e => e.Disability).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.UpdatedBy).HasMaxLength(320);
        b.Ignore(e => e.IsProtectedVeteran);
        b.HasIndex(e => e.EmployeeId).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmergencyContactConfiguration : IEntityTypeConfiguration<EmergencyContact>
{
    public void Configure(EntityTypeBuilder<EmergencyContact> b)
    {
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Relationship).HasMaxLength(100);
        b.Property(c => c.Phone).HasMaxLength(40);
        b.Property(c => c.Email).HasMaxLength(320);
        b.HasIndex(c => c.EmployeeId);
        b.HasOne<Employee>().WithMany().HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeCredentialConfiguration : IEntityTypeConfiguration<EmployeeCredential>
{
    public void Configure(EntityTypeBuilder<EmployeeCredential> b)
    {
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Issuer).HasMaxLength(200);
        b.Property(c => c.Identifier).HasMaxLength(100);
        b.Property(c => c.ClearanceLevel).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Notes).HasMaxLength(1000);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.EmployeeId);
        b.HasIndex(c => c.ExpiresOn);
        b.HasOne<Employee>().WithMany().HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeDocumentConfiguration : IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<EmployeeDocument> b)
    {
        b.Property(d => d.Title).HasMaxLength(200);
        b.Property(d => d.Category).HasMaxLength(100);
        b.Property(d => d.UploadedBy).HasMaxLength(320);
        b.HasIndex(d => d.EmployeeId);
        b.HasOne<Employee>().WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StoredFile>().WithMany().HasForeignKey(d => d.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HrChangeRequestConfiguration : IEntityTypeConfiguration<HrChangeRequest>
{
    public void Configure(EntityTypeBuilder<HrChangeRequest> b)
    {
        b.Property(r => r.RequestedBy).HasMaxLength(320);
        b.Property(r => r.ChangesJson).HasMaxLength(8000);
        b.Property(r => r.Summary).HasMaxLength(500);
        b.Property(r => r.Comment).HasMaxLength(1000);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.DecidedBy).HasMaxLength(320);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);
        b.HasIndex(r => new { r.EmployeeId, r.Status });
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> b)
    {
        b.Property(p => p.Code).HasMaxLength(32);
        b.Property(p => p.Title).HasMaxLength(200);
        b.Property(p => p.EeoJobCategory).HasConversion<string>().HasMaxLength(40);
        b.Property(p => p.EmploymentType).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.SalaryMin).HasPrecision(18, 2);
        b.Property(p => p.SalaryMax).HasPrecision(18, 2);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(p => p.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(p => p.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Position>().WithMany().HasForeignKey(p => p.ReportsToPositionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompensationChangeConfiguration : IEntityTypeConfiguration<CompensationChange>
{
    public void Configure(EntityTypeBuilder<CompensationChange> b)
    {
        b.Property(c => c.PayType).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.AnnualSalary).HasPrecision(18, 2);
        b.Property(c => c.HourlyRate).HasPrecision(18, 4);
        b.Property(c => c.PreviousAnnualized).HasPrecision(18, 2);
        b.Property(c => c.Reason).HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.Note).HasMaxLength(500);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.RequestedBy).HasMaxLength(320);
        b.Property(c => c.DecidedBy).HasMaxLength(320);
        b.Property(c => c.DecisionComment).HasMaxLength(1000);
        b.Ignore(c => c.Annualized);
        b.HasIndex(c => new { c.EmployeeId, c.Status });
        b.HasIndex(c => c.CycleId);
        b.HasOne<Employee>().WithMany().HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MeritCycle>().WithMany().HasForeignKey(c => c.CycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmployeePayRate>().WithMany().HasForeignKey(c => c.PayRateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MeritCycleConfiguration : IEntityTypeConfiguration<MeritCycle>
{
    public void Configure(EntityTypeBuilder<MeritCycle> b)
    {
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.BudgetPercent).HasPrecision(9, 4);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
    }
}

internal sealed class BenefitPlanConfiguration : IEntityTypeConfiguration<BenefitPlan>
{
    public void Configure(EntityTypeBuilder<BenefitPlan> b)
    {
        b.Property(p => p.Code).HasMaxLength(32);
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.Type).HasConversion<string>().HasMaxLength(24);
        b.Property(p => p.Carrier).HasMaxLength(200);
        b.Property(p => p.DeductionCode).HasMaxLength(32);
        b.Property(p => p.AnnualElectionLimit).HasPrecision(18, 2);
        b.Property(p => p.EmployerMatchRate).HasPrecision(9, 4);
        b.Property(p => p.EmployerMatchCapPercent).HasPrecision(9, 4);
        b.Property(p => p.EmployerMonthlyContribution).HasPrecision(18, 2);
        b.Property(p => p.Description).HasMaxLength(1000);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.Ignore(p => p.IsContribution);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasMany(p => p.Tiers).WithOne().HasForeignKey(t => t.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(p => p.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BenefitTierConfiguration : IEntityTypeConfiguration<BenefitTier>
{
    public void Configure(EntityTypeBuilder<BenefitTier> b)
    {
        b.Property(t => t.Coverage).HasConversion<string>().HasMaxLength(24);
        b.Property(t => t.EmployeeMonthlyCost).HasPrecision(18, 2);
        b.Property(t => t.EmployerMonthlyCost).HasPrecision(18, 2);
        b.HasIndex(t => new { t.PlanId, t.Coverage }).IsUnique();
    }
}

internal sealed class OpenEnrollmentWindowConfiguration : IEntityTypeConfiguration<OpenEnrollmentWindow>
{
    public void Configure(EntityTypeBuilder<OpenEnrollmentWindow> b)
    {
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.CreatedBy).HasMaxLength(320);
    }
}

internal sealed class BenefitDependentConfiguration : IEntityTypeConfiguration<BenefitDependent>
{
    public void Configure(EntityTypeBuilder<BenefitDependent> b)
    {
        b.Property(d => d.FirstName).HasMaxLength(100);
        b.Property(d => d.LastName).HasMaxLength(100);
        b.Property(d => d.Relationship).HasConversion<string>().HasMaxLength(24);
        b.HasIndex(d => d.EmployeeId);
        b.HasOne<Employee>().WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BenefitElectionConfiguration : IEntityTypeConfiguration<BenefitElection>
{
    public void Configure(EntityTypeBuilder<BenefitElection> b)
    {
        b.Property(e => e.PlanType).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.Coverage).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.ContributionAmount).HasPrecision(18, 2);
        b.Property(e => e.Source).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.ElectedBy).HasMaxLength(320);
        b.HasIndex(e => new { e.EmployeeId, e.PlanType });
        b.HasMany(e => e.Dependents).WithOne().HasForeignKey(d => d.ElectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BenefitPlan>().WithMany().HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BenefitTier>().WithMany().HasForeignKey(e => e.TierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OpenEnrollmentWindow>().WithMany().HasForeignKey(e => e.WindowId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LifeEvent>().WithMany().HasForeignKey(e => e.LifeEventId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BenefitElectionDependentConfiguration : IEntityTypeConfiguration<BenefitElectionDependent>
{
    public void Configure(EntityTypeBuilder<BenefitElectionDependent> b)
    {
        b.HasIndex(d => new { d.ElectionId, d.DependentId }).IsUnique();
        b.HasOne<BenefitDependent>().WithMany().HasForeignKey(d => d.DependentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LifeEventConfiguration : IEntityTypeConfiguration<LifeEvent>
{
    public void Configure(EntityTypeBuilder<LifeEvent> b)
    {
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.Note).HasMaxLength(1000);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.ReportedBy).HasMaxLength(320);
        b.Property(e => e.DecidedBy).HasMaxLength(320);
        b.HasIndex(e => new { e.EmployeeId, e.Status });
        b.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BenefitDeductionExportConfiguration : IEntityTypeConfiguration<BenefitDeductionExport>
{
    public void Configure(EntityTypeBuilder<BenefitDeductionExport> b)
    {
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.Content).HasMaxLength(1_000_000);
        b.Property(x => x.EmployeeTotal).HasPrecision(18, 2);
        b.Property(x => x.EmployerTotal).HasPrecision(18, 2);
        b.Property(x => x.ExportedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.PayGroupId, x.PayPeriodId });
        b.HasOne<PayGroup>().WithMany().HasForeignKey(x => x.PayGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PayPeriod>().WithMany().HasForeignKey(x => x.PayPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChecklistTemplateConfiguration : IEntityTypeConfiguration<ChecklistTemplate>
{
    public void Configure(EntityTypeBuilder<ChecklistTemplate> b)
    {
        b.Property(t => t.Name).HasMaxLength(200);
        b.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        b.HasMany(t => t.Tasks).WithOne().HasForeignKey(t => t.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChecklistTemplateTaskConfiguration : IEntityTypeConfiguration<ChecklistTemplateTask>
{
    public void Configure(EntityTypeBuilder<ChecklistTemplateTask> b)
    {
        b.Property(t => t.Title).HasMaxLength(200);
        b.Property(t => t.Owner).HasConversion<string>().HasMaxLength(16);
    }
}

internal sealed class EmployeeChecklistConfiguration : IEntityTypeConfiguration<EmployeeChecklist>
{
    public void Configure(EntityTypeBuilder<EmployeeChecklist> b)
    {
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.TerminationReason).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.LeavePayoutHours).HasPrecision(9, 2);
        b.HasIndex(c => new { c.EmployeeId, c.Kind });
        b.HasMany(c => c.Tasks).WithOne().HasForeignKey(t => t.ChecklistId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ChecklistTemplate>().WithMany().HasForeignKey(c => c.TemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChecklistTaskConfiguration : IEntityTypeConfiguration<ChecklistTask>
{
    public void Configure(EntityTypeBuilder<ChecklistTask> b)
    {
        b.Property(t => t.Title).HasMaxLength(200);
        b.Property(t => t.Owner).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.AssigneeEmail).HasMaxLength(320);
        b.Property(t => t.Action).HasConversion<string>().HasMaxLength(24);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.CompletedBy).HasMaxLength(320);
        b.Property(t => t.Note).HasMaxLength(1000);
        b.HasIndex(t => new { t.Status, t.DueDate });
        b.HasIndex(t => t.AssigneeEmail);
        b.HasOne<FixedAsset>().WithMany().HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}
