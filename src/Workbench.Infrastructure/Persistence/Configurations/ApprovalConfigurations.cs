using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalWorkflowConfiguration : IEntityTypeConfiguration<ApprovalWorkflow>
{
    public void Configure(EntityTypeBuilder<ApprovalWorkflow> b)
    {
        b.Property(w => w.DocumentType).HasMaxLength(64);
        b.Property(w => w.Name).HasMaxLength(200);
        b.HasIndex(w => w.DocumentType).IsUnique();
    }
}

internal sealed class ApprovalWorkflowVersionConfiguration : IEntityTypeConfiguration<ApprovalWorkflowVersion>
{
    public void Configure(EntityTypeBuilder<ApprovalWorkflowVersion> b)
    {
        b.Property(v => v.DefinitionJson).HasMaxLength(32000);
        b.Property(v => v.CreatedBy).HasMaxLength(320);
        b.Property(v => v.Note).HasMaxLength(1000);
        b.HasIndex(v => new { v.WorkflowId, v.Version }).IsUnique();
        b.HasOne<ApprovalWorkflow>().WithMany().HasForeignKey(v => v.WorkflowId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalInstanceConfiguration : IEntityTypeConfiguration<ApprovalInstance>
{
    public void Configure(EntityTypeBuilder<ApprovalInstance> b)
    {
        b.Property(i => i.DocumentType).HasMaxLength(64);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.SubmitterEmail).HasMaxLength(320);
        b.Property(i => i.Title).HasMaxLength(300);
        b.Property(i => i.Summary).HasMaxLength(500);
        b.Property(i => i.Amount).HasPrecision(18, 2);
        b.Property(i => i.Link).HasMaxLength(300);
        b.HasIndex(i => new { i.DocumentType, i.DocumentId });
        b.HasIndex(i => new { i.Status, i.DocumentType });
        b.HasOne<ApprovalWorkflowVersion>().WithMany().HasForeignKey(i => i.WorkflowVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalTaskConfiguration : IEntityTypeConfiguration<ApprovalTask>
{
    public void Configure(EntityTypeBuilder<ApprovalTask> b)
    {
        b.Property(t => t.StepName).HasMaxLength(200);
        b.Property(t => t.ScopeKey).HasMaxLength(400);
        b.Property(t => t.ScopeLabel).HasMaxLength(300);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.AssigneesCsv).HasMaxLength(4000);
        b.Property(t => t.AssigneeRole).HasMaxLength(64); // An org role name, or role:{id} for a custom role.
        b.Property(t => t.ExcludedCsv).HasMaxLength(2000);
        b.Property(t => t.DecidedBy).HasMaxLength(320);
        b.Property(t => t.OnBehalfOf).HasMaxLength(320);
        b.Property(t => t.Comment).HasMaxLength(1000);
        b.Property(t => t.SkipReason).HasMaxLength(300);
        b.HasIndex(t => new { t.InstanceId, t.Sequence });
        b.HasIndex(t => t.Status);
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(t => t.InstanceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalActionConfiguration : IEntityTypeConfiguration<ApprovalAction>
{
    public void Configure(EntityTypeBuilder<ApprovalAction> b)
    {
        b.Property(a => a.Action).HasMaxLength(32);
        b.Property(a => a.Actor).HasMaxLength(320);
        b.Property(a => a.OnBehalfOf).HasMaxLength(320);
        b.Property(a => a.Comment).HasMaxLength(1000);
        b.HasIndex(a => a.InstanceId);
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(a => a.InstanceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApprovalDelegationConfiguration : IEntityTypeConfiguration<ApprovalDelegation>
{
    public void Configure(EntityTypeBuilder<ApprovalDelegation> b)
    {
        b.Property(d => d.DelegatorEmail).HasMaxLength(320);
        b.Property(d => d.DelegateEmail).HasMaxLength(320);
        b.Property(d => d.DocumentType).HasMaxLength(64);
        b.Property(d => d.Reason).HasMaxLength(500);
        b.Property(d => d.CreatedBy).HasMaxLength(320);
        b.HasIndex(d => d.DelegateEmail);
        b.HasIndex(d => d.DelegatorEmail);
    }
}

internal sealed class OrgUnitApproverConfiguration : IEntityTypeConfiguration<OrgUnitApprover>
{
    public void Configure(EntityTypeBuilder<OrgUnitApprover> b)
    {
        b.HasIndex(a => a.OrgUnitId).IsUnique();
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(a => a.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
