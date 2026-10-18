using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Approvals;

/// <summary>
/// Approval workflow definitions (Setup › Approval workflows). Every save publishes a new immutable version; documents
/// already in flight keep the version they started with. Permission checks are in <see cref="PermissionCheckingApprovalWorkflowService"/>.
/// </summary>
public sealed class ApprovalWorkflowService(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine, AuditRecorder audit, AccessControl access) : IApprovalWorkflowService
{
    public const string AuditEntityType = nameof(ApprovalWorkflow);

    public async Task<IReadOnlyList<ApprovalWorkflowSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var result = new List<ApprovalWorkflowSummaryDto>();
        foreach (var type in ApprovalDocumentTypes.All)
        {
            var (workflow, version) = await engine.CurrentAsync(db, type, access.Email, cancellationToken);
            var inFlight = await db.Set<ApprovalInstance>().CountAsync(i => i.DocumentType == type && i.Status == ApprovalInstanceStatus.Pending, cancellationToken);
            result.Add(new ApprovalWorkflowSummaryDto(type, ApprovalDocumentTypes.Label(type), workflow.Name, version.Version,
                ApprovalDefaults.Deserialize(version.DefinitionJson).Steps.Count, workflow.UpdatedAtUtc, inFlight));
        }

        await db.SaveChangesAsync(cancellationToken); // Built-in defaults created on first look.
        return result;
    }

    public async Task<ApprovalWorkflowDto> GetAsync(string documentType, CancellationToken cancellationToken = default)
    {
        RequireType(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dto = await DetailAsync(db, documentType, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<ApprovalWorkflowDefinition> GetVersionAsync(string documentType, int version, CancellationToken cancellationToken = default)
    {
        RequireType(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<ApprovalWorkflowVersion>().AsNoTracking()
            .Join(db.Set<ApprovalWorkflow>().Where(w => w.DocumentType == documentType), v => v.WorkflowId, w => w.Id, (v, w) => v)
            .FirstOrDefaultAsync(v => v.Version == version, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"Version {version} of the {ApprovalDocumentTypes.Label(documentType).ToLowerInvariant()} workflow doesn't exist.");
        return ApprovalDefaults.Deserialize(row.DefinitionJson);
    }

    public async Task<ApprovalWorkflowDto> SaveAsync(string documentType, SaveApprovalWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        RequireType(documentType);
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(request.Name) ? throw new ArgumentException("Give the workflow a name.") : request.Name.Trim();
        if (name.Length > 200)
        {
            throw new ArgumentException("The name can be at most 200 characters.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000)
        {
            throw new ArgumentException("The note can be at most 1000 characters.");
        }

        var definition = Validate(request.Definition);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await PublishAsync(db, documentType, name, definition, note, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, documentType, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> ResetToDefaultAsync(string documentType, CancellationToken cancellationToken = default)
    {
        RequireType(documentType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await PublishAsync(db, documentType, ApprovalDefaults.Name(documentType), ApprovalDefaults.For(documentType), "Reset to the built-in default", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, documentType, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgUnitApproverDto>> ListOrgUnitApproversAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var units = await db.Set<OrgUnit>().AsNoTracking().OrderBy(u => u.Code).ToListAsync(cancellationToken);
        var approvers = await db.Set<OrgUnitApprover>().AsNoTracking()
            .Join(db.Set<Employee>(), a => a.EmployeeId, e => e.Id, (a, e) => new { a.OrgUnitId, e.Id, e.DisplayName })
            .ToDictionaryAsync(x => x.OrgUnitId, cancellationToken);
        return units.Select(u => new OrgUnitApproverDto(u.Id, u.Code, u.Name, approvers.GetValueOrDefault(u.Id)?.Id, approvers.GetValueOrDefault(u.Id)?.DisplayName)).ToList();
    }

    public async Task<OrgUnitApproverDto> SetOrgUnitApproverAsync(Guid orgUnitId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var unit = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == orgUnitId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Org unit", orgUnitId);
        Employee? employee = null;
        if (employeeId is { } id)
        {
            employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Employee", id);
        }

        var row = await db.Set<OrgUnitApprover>().FirstOrDefaultAsync(a => a.OrgUnitId == orgUnitId, cancellationToken);
        var before = row?.EmployeeId;
        if (employee is null)
        {
            if (row is not null)
            {
                db.Set<OrgUnitApprover>().Remove(row);
            }
        }
        else if (row is null)
        {
            db.Set<OrgUnitApprover>().Add(new OrgUnitApprover { OrgUnitId = orgUnitId, EmployeeId = employee.Id });
        }
        else
        {
            row.EmployeeId = employee.Id;
        }

        audit.Record(db, nameof(OrgUnitApprover), orgUnitId, "ApproverChanged", new { EmployeeId = before }, new { EmployeeId = employee?.Id });
        await db.SaveChangesAsync(cancellationToken);
        return new OrgUnitApproverDto(unit.Id, unit.Code, unit.Name, employee?.Id, employee?.DisplayName);
    }

    /// <summary>Checks a definition and returns it with names trimmed and people normalized.</summary>
    public static ApprovalWorkflowDefinition Validate(ApprovalWorkflowDefinition? definition)
    {
        if (definition?.Steps is not { Count: > 0 } steps)
        {
            throw new ArgumentException("A workflow needs at least one step.");
        }

        if (steps.Count > 20)
        {
            throw new ArgumentException("A workflow can have at most 20 steps.");
        }

        var result = new List<ApprovalStepDefinition>();
        foreach (var (step, i) in steps.Select((s, i) => (s, i + 1)))
        {
            var where = $"Step {i}";
            if (string.IsNullOrWhiteSpace(step.Name) || step.Name.Trim().Length > 200)
            {
                throw new ArgumentException($"{where} needs a name of at most 200 characters.");
            }

            if (step.Sequence is < 1 or > 99)
            {
                throw new ArgumentException($"{where}: the order must be between 1 and 99.");
            }

            if (step.RemindAfterBusinessDays is < 1 or > 60)
            {
                throw new ArgumentException($"{where}: reminders are sent after 1 to 60 business days.");
            }

            if (step.EscalateToApproverSupervisor && step.RemindAfterBusinessDays is null)
            {
                throw new ArgumentException($"{where}: set the reminder days to escalate to the approver's supervisor.");
            }

            var conditions = (step.Conditions ?? []).Select(c => Condition(c, where)).ToList();
            result.Add(step with
            {
                Name = step.Name.Trim(),
                Approver = Rule(step.Approver, where, fallback: false),
                Fallback = step.Fallback is null ? null : Rule(step.Fallback, $"{where} fallback", fallback: true),
                Conditions = conditions,
            });
        }

        var validated = new ApprovalWorkflowDefinition(result.OrderBy(s => s.Sequence).ToList());
        if (ApprovalDefaults.Serialize(validated).Length > 32000)
        {
            throw new ArgumentException("The workflow is too large.");
        }

        return validated;
    }

    private static ApproverRule Rule(ApproverRule? rule, string where, bool fallback)
    {
        if (rule is null)
        {
            throw new ArgumentException($"{where} needs an approver.");
        }

        if (fallback && rule.Kind is ApproverRuleKind.ProjectManager or ApproverRuleKind.OrgUnitManager)
        {
            throw new ArgumentException($"{where}: a fallback is the supervisor, an org role or named people.");
        }

        switch (rule.Kind)
        {
            case ApproverRuleKind.OrgRole:
                return rule.Role is null ? throw new ArgumentException($"{where}: choose the org role.") : rule with { People = null };
            case ApproverRuleKind.SpecificPeople or ApproverRuleKind.AnyOf:
                var people = (rule.People ?? []).Select(ApprovalEngine.Normalize).Where(p => p.Length > 0).Distinct().ToList();
                if (people.Count == 0)
                {
                    throw new ArgumentException($"{where}: list at least one person.");
                }

                foreach (var person in people)
                {
                    if (person.Length > 320 || !MailAddress.TryCreate(person, out _))
                    {
                        throw new ArgumentException($"{where}: \"{person}\" isn't a valid email.");
                    }
                }

                return rule with { People = people, Role = null };
            default:
                return rule with { People = null, Role = null };
        }
    }

    private static ApprovalCondition Condition(ApprovalCondition c, string where) => c.Kind switch
    {
        ApprovalConditionKind.AmountOver or ApprovalConditionKind.AmountAtMost => c.Amount is { } a && a >= 0 && decimal.Round(a, 2) == a
            ? c with { Ids = null, Values = null }
            : throw new ArgumentException($"{where}: give an amount (0 or more, at most two decimals)."),
        ApprovalConditionKind.ProjectIn or ApprovalConditionKind.ContractIn or ApprovalConditionKind.OrgUnitIn => c.Ids is { Count: > 0 }
            ? c with { Amount = null, Values = null }
            : throw new ArgumentException($"{where}: choose at least one {c.Kind switch { ApprovalConditionKind.ProjectIn => "project", ApprovalConditionKind.ContractIn => "contract", _ => "org unit" }}."),
        ApprovalConditionKind.CostTypeIn => c.Values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList() is { Count: > 0 } values
            ? c with { Amount = null, Ids = null, Values = values }
            : throw new ArgumentException($"{where}: list at least one cost type."),
        _ => new ApprovalCondition(c.Kind),
    };

    private static void RequireType(string documentType)
    {
        if (!ApprovalDocumentTypes.All.Contains(documentType))
        {
            throw new WorkbenchNotFoundException($"There's no approval workflow for \"{documentType}\".");
        }
    }

    private async Task PublishAsync(IWorkbenchDbContext db, string documentType, string name, ApprovalWorkflowDefinition definition, string? note, CancellationToken ct)
    {
        var (workflow, current) = await engine.CurrentAsync(db, documentType, access.Email, ct);
        var latest = Math.Max(current.Version, await db.Set<ApprovalWorkflowVersion>().Where(v => v.WorkflowId == workflow.Id).Select(v => (int?)v.Version).MaxAsync(ct) ?? 0);
        var version = new ApprovalWorkflowVersion
        {
            WorkflowId = workflow.Id,
            Version = latest + 1,
            DefinitionJson = ApprovalDefaults.Serialize(definition),
            CreatedBy = access.Email,
            CreatedAtUtc = engine.UtcNow,
            Note = note,
        };
        db.Set<ApprovalWorkflowVersion>().Add(version);
        var before = new { workflow.Name, current.Version };
        workflow.Name = name;
        workflow.CurrentVersionId = version.Id;
        workflow.UpdatedAtUtc = engine.UtcNow;
        audit.Record(db, AuditEntityType, workflow.Id, "VersionPublished", before, new { Name = name, version.Version, Steps = definition.Steps.Count }, note);
    }

    private async Task<ApprovalWorkflowDto> DetailAsync(IWorkbenchDbContext db, string documentType, CancellationToken ct)
    {
        var (workflow, current) = await engine.CurrentAsync(db, documentType, access.Email, ct);
        var versions = (await db.Set<ApprovalWorkflowVersion>().AsNoTracking().Where(v => v.WorkflowId == workflow.Id).ToListAsync(ct))
            .Concat(db.Set<ApprovalWorkflowVersion>().Local.Where(v => v.WorkflowId == workflow.Id))
            .DistinctBy(v => v.Id).OrderByDescending(v => v.Version).ToList();
        var inFlight = (await db.Set<ApprovalInstance>().AsNoTracking()
                .Where(i => i.DocumentType == documentType && i.Status == ApprovalInstanceStatus.Pending)
                .GroupBy(i => i.WorkflowVersionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var canEdit = (await access.GetOrgRolesAsync(ct)).IsAdmin();
        return new ApprovalWorkflowDto(documentType, ApprovalDocumentTypes.Label(documentType), workflow.Name, current.Version,
            ApprovalDefaults.Deserialize(current.DefinitionJson),
            versions.Select(v => new ApprovalWorkflowVersionDto(v.Id, v.Version, v.CreatedBy, v.CreatedAtUtc, v.Note, v.Id == current.Id, inFlight.GetValueOrDefault(v.Id))).ToList(),
            canEdit);
    }
}

/// <summary>Workflows and org-unit approvers: read by Finance and Admin, changed by Admin.</summary>
public sealed class PermissionCheckingApprovalWorkflowService(IApprovalWorkflowService inner, AccessControl access) : IApprovalWorkflowService
{
    public async Task<IReadOnlyList<ApprovalWorkflowSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view approval workflows", cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> GetAsync(string documentType, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view approval workflows", cancellationToken);
        return await inner.GetAsync(documentType, cancellationToken);
    }

    public async Task<ApprovalWorkflowDefinition> GetVersionAsync(string documentType, int version, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view approval workflows", cancellationToken);
        return await inner.GetVersionAsync(documentType, version, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> SaveAsync(string documentType, SaveApprovalWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.SaveAsync(documentType, request, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> ResetToDefaultAsync(string documentType, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.ResetToDefaultAsync(documentType, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgUnitApproverDto>> ListOrgUnitApproversAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view org-unit approvers", cancellationToken);
        return await inner.ListOrgUnitApproversAsync(cancellationToken);
    }

    public async Task<OrgUnitApproverDto> SetOrgUnitApproverAsync(Guid orgUnitId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.SetOrgUnitApproverAsync(orgUnitId, employeeId, cancellationToken);
    }
}
