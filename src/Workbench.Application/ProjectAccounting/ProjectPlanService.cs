using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Financials;
using Workbench.Application.MyTodo;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.ProjectAccounting;

/// <summary>
/// A project's plan terms and its approval by Finance. The plan itself is the project's WBS with its people
/// (<see cref="StaffingAssignment"/>s and their weekly <see cref="ResourceAllocation"/>s); approving it writes those hours
/// into the budget through <see cref="IBudgetService"/>: the first approval is the locked Baseline, and every approval is
/// the Current (working) budget, so Finance always sees the working plan against the baseline. Permission checks (and the
/// Can* flags) are in the identity decorator.
/// </summary>
public sealed class ProjectPlanService(
    IWorkbenchDbContextFactory dbFactory,
    IBudgetService budgets,
    AuditRecorder audit,
    TimeProvider clock,
    INotificationPublisher? notifications = null) : IProjectPlanService
{
    public const string NotificationKind = "project-plan";

    public async Task<ProjectPlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ToDtoAsync(db, await ProjectAsync(db, projectId, cancellationToken), cancellationToken);
    }

    public async Task<ProjectTermsDto> TermsFromContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ProjectTerms.FromContractAsync(db, contractId, cancellationToken);
    }

    public async Task<ProjectPlanDto> SaveTermsAsync(Guid projectId, SaveProjectTermsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await ProjectAsync(db, projectId, cancellationToken);
        var loaded = await ProjectTerms.LoadAsync(db, project, cancellationToken);
        var plan = await PlanRowAsync(db, project.Id, cancellationToken);
        var (ceiling, funded, pricing, fee) = ProjectTerms.Overrides(request, loaded.Derived);
        var before = new { plan.CeilingValue, plan.FundedValue, plan.Pricing, plan.FeeRate };
        (plan.CeilingValue, plan.FundedValue, plan.Pricing, plan.FeeRate) = (ceiling, funded, pricing, fee);
        Touch(plan);
        audit.Record(db, nameof(ProjectPlan), plan.Id, "TermsSet", before, new { plan.CeilingValue, plan.FundedValue, plan.Pricing, plan.FeeRate });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, project, cancellationToken);
    }

    public async Task<ProjectPlanDto> SaveCategoriesAsync(Guid projectId, IReadOnlyList<Guid> laborCategoryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(laborCategoryIds);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await ProjectAsync(db, projectId, cancellationToken);
        var wanted = laborCategoryIds.Distinct().ToList();
        var known = await db.Set<LaborCategory>().AsNoTracking().Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            throw new ArgumentException("Unknown labor category.");
        }

        var current = await db.Set<ProjectLaborCategory>().Where(c => c.ProjectId == project.Id).ToListAsync(cancellationToken);
        var removed = current.Where(c => !wanted.Contains(c.LaborCategoryId)).ToList();
        var added = wanted.Where(id => current.All(c => c.LaborCategoryId != id)).ToList();
        if (removed.Count == 0 && added.Count == 0)
        {
            return await ToDtoAsync(db, project, cancellationToken);
        }

        db.Set<ProjectLaborCategory>().RemoveRange(removed);
        var now = clock.GetUtcNow().UtcDateTime;
        db.Set<ProjectLaborCategory>().AddRange(added.Select(id => new ProjectLaborCategory { ProjectId = project.Id, LaborCategoryId = id, CreatedBy = audit.Actor, CreatedAtUtc = now }));
        audit.Record(db, nameof(Project), project.Id, "LaborCategoriesSet", new { Categories = current.Select(c => c.LaborCategoryId) }, new { Categories = wanted });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, project, cancellationToken);
    }

    public async Task<ProjectPlanDto> SubmitAsync(Guid projectId, string? note = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await ProjectAsync(db, projectId, cancellationToken);
        var plan = await PlanRowAsync(db, project.Id, cancellationToken);
        if (project.Status == ProjectStatus.Planning && await MissingSetupAsync(db, project, cancellationToken) is { Count: > 0 } missing)
        {
            throw new ArgumentException($"Finance hasn't finished setting up {project.Code} yet: they still need to add {string.Join(" and ", missing)}. You can plan, but not submit, until then.");
        }

        if (plan.Status == ProjectPlanStatus.Submitted)
        {
            throw new ArgumentException("The plan is already with Finance.");
        }

        var lines = await PlanLinesAsync(db, project.Id, cancellationToken);
        if (lines.Count == 0)
        {
            throw new ArgumentException("Assign people and their hours before submitting the plan.");
        }

        if (plan.Status == ProjectPlanStatus.Approved && !await ChangedSinceApprovalAsync(db, project.Id, lines, cancellationToken))
        {
            throw new ArgumentException("Nothing has changed since Finance approved the plan.");
        }

        var before = plan.Status;
        plan.Status = ProjectPlanStatus.Submitted;
        plan.SubmittedBy = audit.Actor;
        plan.SubmittedAtUtc = clock.GetUtcNow().UtcDateTime;
        plan.SubmitNote = Clean(note);
        Touch(plan);
        audit.Record(db, nameof(ProjectPlan), plan.Id, "Submitted", new { Status = before }, new { plan.Status, Note = plan.SubmitNote });
        await db.SaveChangesAsync(cancellationToken);

        if (notifications is not null)
        {
            var finance = await ApproverRoles.MembersAsync(db, ApprovalOrgRole.Finance.ToString(), cancellationToken);
            await notifications.PublishAsync(finance.Where(e => !string.Equals(e, audit.Actor, StringComparison.OrdinalIgnoreCase)), NotificationKind,
                $"Review the plan for {project.Code} {project.Name}",
                $"{await NameAsync(db, audit.Actor, cancellationToken)} submitted it for approval." + (plan.SubmitNote is { } n ? $" \"{n}\"" : ""),
                Link(project.Id));
        }

        return await ToDtoAsync(db, project, cancellationToken);
    }

    public async Task<ProjectPlanDto> ApproveAsync(Guid projectId, string? comment = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await ProjectAsync(db, projectId, cancellationToken);
        var plan = await ReviewableAsync(db, project, cancellationToken);
        var lines = await PlanLinesAsync(db, project.Id, cancellationToken);
        if (lines.Count == 0)
        {
            throw new ArgumentException("The plan has no hours to approve. Return it to the project manager.");
        }

        // The budget: the first approval sets the Baseline (locked); every approval is the Current working budget.
        var requests = lines.Select(l => new SaveBudgetLineRequest(l.WbsElementId, BudgetCostKind.Labor, l.LaborCategoryId, null, null, l.Hours)).ToList();
        var versions = await budgets.ListVersionsAsync(project.Id, cancellationToken);
        var date = clock.GetUtcNow().UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var baseline = versions.FirstOrDefault(v => v.Kind == BudgetVersionKind.Baseline);
        if (baseline is null)
        {
            baseline = (await budgets.CreateVersionAsync(project.Id,
                new CreateBudgetVersionRequest("Approved plan", BudgetVersionKind.Baseline, Description: $"The project manager's plan, approved {date}."), cancellationToken)).Version;
        }

        if (!baseline.IsLocked)
        {
            await budgets.SaveLinesAsync(baseline.Id, requests, cancellationToken);
            await budgets.LockBaselineAsync(baseline.Id, $"Plan approved {date}", cancellationToken);
        }

        var current = versions.FirstOrDefault(v => v.Kind == BudgetVersionKind.Current)
            ?? (await budgets.CreateVersionAsync(project.Id,
                new CreateBudgetVersionRequest("Working plan", BudgetVersionKind.Current, Description: "The approved plan, with changes Finance has approved since the baseline."), cancellationToken)).Version;
        await budgets.SaveLinesAsync(current.Id, requests, cancellationToken);

        var opened = false;
        if (await db.Set<Project>().FirstOrDefaultAsync(p => p.Id == project.Id, cancellationToken) is { Status: ProjectStatus.Planning } planning)
        {
            planning.Status = ProjectStatus.Active;
            opened = true;
            audit.Record(db, nameof(Project), project.Id, "Activated", new { Status = nameof(ProjectStatus.Planning) }, new { Status = nameof(ProjectStatus.Active), Why = "Plan approved" });
        }

        plan.Status = ProjectPlanStatus.Approved;
        plan.Approvals++;
        plan.ReviewedBy = audit.Actor;
        plan.ReviewedAtUtc = clock.GetUtcNow().UtcDateTime;
        plan.ReviewComment = Clean(comment);
        Touch(plan);
        audit.Record(db, nameof(ProjectPlan), plan.Id, "Approved", null,
            new { plan.Approvals, Hours = lines.Sum(l => l.Hours), BaselineVersionId = baseline.Id, CurrentVersionId = current.Id, plan.ReviewComment });
        await db.SaveChangesAsync(cancellationToken);
        await NotifyManagerAsync(db, project, plan.Approvals == 1 ? $"Finance approved the plan for {project.Code}" : $"Finance approved the changes to {project.Code}'s plan",
            plan.Approvals == 1 ? "It's the project's baseline budget now" + (opened ? ", and the project is Active: time can be charged to it." : ".") + " Changes from here are compared with it." : "They're in the working budget; the baseline stays as first approved.",
            plan.ReviewComment, cancellationToken);
        return await ToDtoAsync(db, await ProjectAsync(db, projectId, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> ReturnAsync(Guid projectId, string comment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Say what needs to change, so the project manager knows.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await ProjectAsync(db, projectId, cancellationToken);
        var plan = await ReviewableAsync(db, project, cancellationToken);
        plan.Status = ProjectPlanStatus.Returned;
        plan.ReviewedBy = audit.Actor;
        plan.ReviewedAtUtc = clock.GetUtcNow().UtcDateTime;
        plan.ReviewComment = Clean(comment);
        Touch(plan);
        audit.Record(db, nameof(ProjectPlan), plan.Id, "Returned", null, new { plan.ReviewComment });
        await db.SaveChangesAsync(cancellationToken);
        await NotifyManagerAsync(db, project, $"Finance returned the plan for {project.Code}", "Make the changes and submit it again.", plan.ReviewComment, cancellationToken);
        return await ToDtoAsync(db, project, cancellationToken);
    }

    /// <summary>A new project handed to its manager: a plan row (so it shows on their to-do list) and a notification.</summary>
    internal async Task HandOffAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var project = await ProjectAsync(db, projectId, ct);
        await PlanRowAsync(db, project.Id, ct);

        // They plan in the project's plan sheet, so they need to edit it: Editor in its workspace if they aren't already.
        var email = project.ProjectManagerId is { } pm ? await db.Set<Employee>().AsNoTracking().Where(e => e.Id == pm).Select(e => e.Email).FirstOrDefaultAsync(ct) : null;
        if (email is not null && project.SheetId is { } sheetId
            && await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => (Guid?)s.WorkspaceId).FirstOrDefaultAsync(ct) is { } workspaceId)
        {
            var normalized = email.Trim().ToLowerInvariant();
            var member = await db.Set<Domain.Identity.WorkspaceMember>().FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserEmail == normalized, ct);
            if (member is null)
            {
                db.Set<Domain.Identity.WorkspaceMember>().Add(new Domain.Identity.WorkspaceMember { WorkspaceId = workspaceId, UserEmail = normalized, Role = Domain.Identity.WorkspaceRole.Editor });
                audit.Record(db, nameof(Project), project.Id, "PlanSharedWithManager", null, new { Workspace = workspaceId, Email = normalized, Role = "Editor" });
            }
            else if (member.Role is Domain.Identity.WorkspaceRole.Viewer or Domain.Identity.WorkspaceRole.Commenter)
            {
                member.Role = Domain.Identity.WorkspaceRole.Editor;
                audit.Record(db, nameof(Project), project.Id, "PlanSharedWithManager", null, new { Workspace = workspaceId, Email = normalized, Role = "Editor" });
            }
        }

        await db.SaveChangesAsync(ct);
        var terms = (await ProjectTerms.LoadAsync(db, project, ct)).Terms;
        await NotifyManagerAsync(db, project, $"Plan project {project.Code} {project.Name}",
            $"{audit.Actor} set it up" + (terms.ContractNumber is { } c ? $" from contract {c}" : "")
            + (terms.Ceiling is { } ceiling ? $" with a {Money(ceiling)} ceiling" : "")
            + ". Build its WBS, assign people and submit the plan for approval.", null, ct);
    }

    // ---- The plan's hours ----

    /// <summary>One budget line's worth of the plan: hours on a WBS leaf in a labor category, and their direct cost.</summary>
    internal sealed record PlanLine(Guid WbsElementId, Guid? LaborCategoryId, decimal Hours, decimal DirectCost);

    /// <summary>
    /// The plan's hours by WBS leaf and labor category, from every person's weekly hours on the project. Labor budget goes
    /// on leaves, so hours on a summary element (or the whole project) count on its first leaf.
    /// </summary>
    internal static async Task<IReadOnlyList<PlanLine>> PlanLinesAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.ProjectId == projectId && a.Hours > 0).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(ct);
        var parents = wbs.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        Guid? FirstLeaf(Guid? id)
        {
            var under = wbs.Where(w => w.ParentId == id).OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal).ToList();
            foreach (var w in under)
            {
                if (!parents.Contains(w.Id))
                {
                    return w.Id;
                }

                if (FirstLeaf(w.Id) is { } leaf)
                {
                    return leaf;
                }
            }

            return null;
        }

        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var defaults = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DefaultLaborCategoryId, ct);
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        return rows
            .Select(r => (Leaf: r.WbsElementId is { } w && !parents.Contains(w) ? w : FirstLeaf(r.WbsElementId), Category: r.LaborCategoryId ?? defaults.GetValueOrDefault(r.EmployeeId), Row: r))
            .Where(x => x.Leaf is not null)
            .GroupBy(x => (Leaf: x.Leaf!.Value, x.Category))
            .Select(g => new PlanLine(g.Key.Leaf, g.Key.Category, g.Sum(x => x.Row.Hours),
                Financials.Money.Round(g.Sum(x => x.Row.Hours * (rates.Find(x.Row.EmployeeId, x.Row.WeekStart) ?? 0)))))
            .ToList();
    }

    internal static async Task<bool> ChangedSinceApprovalAsync(IWorkbenchDbContext db, Guid projectId, IReadOnlyList<PlanLine> lines, CancellationToken ct)
    {
        var current = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == projectId && v.Kind == BudgetKind.Current)
            .OrderByDescending(v => v.CreatedAtUtc).Select(v => v.Id).FirstOrDefaultAsync(ct);
        var approved = await db.Set<BudgetLine>().AsNoTracking().Where(l => l.BudgetVersionId == current && l.CostType == BudgetCostType.Labor).ToListAsync(ct);
        var was = approved.GroupBy(l => (l.WbsElementId, l.LaborCategoryId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Hours));
        var now = lines.ToDictionary(l => (l.WbsElementId, l.LaborCategoryId), l => l.Hours);
        return was.Keys.Union(now.Keys).Any(k => Math.Abs(was.GetValueOrDefault(k) - now.GetValueOrDefault(k)) > 0.005m);
    }

    // ---- Reading ----

    private async Task<ProjectPlanDto> ToDtoAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var loaded = await ProjectTerms.LoadAsync(db, project, ct);
        var plan = loaded.Plan;
        var manager = project.ProjectManagerId is { } pm ? await db.Set<Employee>().AsNoTracking().Where(e => e.Id == pm).Select(e => e.DisplayName).FirstOrDefaultAsync(ct) : null;
        var baseline = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == project.Id && v.Kind == BudgetKind.Baseline && v.IsLocked)
            .OrderByDescending(v => v.CreatedAtUtc).FirstOrDefaultAsync(ct);
        IReadOnlyList<PlanChangeLineDto> since = [];
        var changed = false;
        if (baseline is not null)
        {
            var lines = await PlanLinesAsync(db, project.Id, ct);
            var baseLines = await db.Set<BudgetLine>().AsNoTracking().Where(l => l.BudgetVersionId == baseline.Id && l.CostType == BudgetCostType.Labor).ToListAsync(ct);
            var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => $"{c.Code} {c.Name}", ct);
            since = baseLines.Select(l => l.LaborCategoryId).Concat(lines.Select(l => l.LaborCategoryId)).Distinct()
                .Select(c => new PlanChangeLineDto(c, c is { } id && categories.TryGetValue(id, out var label) ? label : "No labor category",
                    baseLines.Where(l => l.LaborCategoryId == c).Sum(l => l.Hours), lines.Where(l => l.LaborCategoryId == c).Sum(l => l.Hours),
                    baseLines.Where(l => l.LaborCategoryId == c).Sum(l => l.Amount), lines.Where(l => l.LaborCategoryId == c).Sum(l => l.DirectCost)))
                .OrderBy(l => l.LaborCategory, StringComparer.OrdinalIgnoreCase)
                .ToList();
            changed = plan?.Status == ProjectPlanStatus.Approved && await ChangedSinceApprovalAsync(db, project.Id, lines, ct);
        }

        return new ProjectPlanDto(project.Id, project.Code, project.Name, manager, loaded.Terms,
            (PlanApprovalStatus)(int)(plan?.Status ?? ProjectPlanStatus.Draft),
            plan?.SubmittedBy, plan?.SubmittedAtUtc, plan?.SubmitNote, plan?.ReviewedBy, plan?.ReviewedAtUtc, plan?.ReviewComment, plan?.Approvals ?? 0,
            baseline?.Id, changed, since, CanEditTerms: false, CanSubmit: false, CanReview: false,
            SubmittedByName: await NameAsync(db, plan?.SubmittedBy, ct), ReviewedByName: await NameAsync(db, plan?.ReviewedBy, ct),
            ProjectStatus: (ProjectState)(int)project.Status, Steps: await StepsAsync(db, project, loaded.Terms, plan, ct),
            Categories: await CategoriesAsync(db, project, ct), SetupDone: project.Status != ProjectStatus.Planning || (await MissingSetupAsync(db, project, ct)).Count == 0);
    }

    /// <summary>What Finance still has to add before the manager can submit: funding (contract projects) and labor categories.</summary>
    private static async Task<List<string>> MissingSetupAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var missing = new List<string>();
        if (project.ContractId is not null && (await ProjectTerms.LoadAsync(db, project, ct)).Terms.Funded is not > 0)
        {
            missing.Add("funding");
        }

        if (!await db.Set<ProjectLaborCategory>().AnyAsync(c => c.ProjectId == project.Id, ct))
        {
            missing.Add("labor categories");
        }

        return missing;
    }

    private static async Task<IReadOnlyList<ProjectCategoryDto>> CategoriesAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var picked = (await db.Set<ProjectLaborCategory>().AsNoTracking().Where(c => c.ProjectId == project.Id).Select(c => c.LaborCategoryId).ToListAsync(ct)).ToHashSet();
        var onContract = new HashSet<Guid>();
        if (project.ContractId is { } contractId)
        {
            onContract.UnionWith(await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contractId).Select(r => r.LaborCategoryId).ToListAsync(ct));
            onContract.UnionWith(await db.Set<Domain.Ceilings.Ceiling>().AsNoTracking().Where(c => c.ContractId == contractId && c.LaborCategoryId != null).Select(c => c.LaborCategoryId!.Value).ToListAsync(ct));
        }

        return (await db.Set<LaborCategory>().AsNoTracking().Where(c => c.IsActive || picked.Contains(c.Id)).OrderBy(c => c.Code).ToListAsync(ct))
            .Select(c => new ProjectCategoryDto(c.Id, c.Code, c.Name, picked.Contains(c.Id), onContract.Contains(c.Id))).ToList();
    }

    private static async Task<IReadOnlyList<ProjectStepDto>> StepsAsync(IWorkbenchDbContext db, Project project, ProjectTermsDto terms, ProjectPlan? plan, CancellationToken ct)
    {
        var categories = await db.Set<ProjectLaborCategory>().CountAsync(c => c.ProjectId == project.Id, ct);
        var hasPlan = plan is { Status: ProjectPlanStatus.Submitted or ProjectPlanStatus.Approved };
        var started = hasPlan || await db.Set<ResourceAllocation>().AnyAsync(a => a.ProjectId == project.Id && a.Hours > 0, ct);
        var funded = project.ContractId is null || terms.Funded is > 0;
        var approved = plan is { Approvals: > 0 };
        var steps = new List<(string Key, string Label, bool Done, string Detail)>
        {
            ("funding", "Funding", funded, funded ? (terms.Funded is { } f ? $"{Money(f)} funded" : "No contract funding needed") : "Finance adds it on a CLIN"),
            ("categories", "Labor categories", categories > 0, categories > 0 ? $"{categories} to plan with" : "Finance picks the ones this project can use"),
            ("manager", "Project manager", project.ProjectManagerId is not null, project.ProjectManagerId is not null ? "Assigned" : "Finance assigns one"),
            ("plan", "Plan", hasPlan, hasPlan ? "Submitted" : started ? "The project manager is building it, then submits" : "The project manager builds the WBS and people"),
            ("signoff", "Sign-off", approved, approved ? "Finance approved it: the baseline" : "Finance approves or returns it"),
            ("executing", "Executing", project.Status == ProjectStatus.Active, project.Status == ProjectStatus.Active ? "Time can be charged" : "Opens when the plan is approved"),
        };
        var current = steps.FindIndex(s => !s.Done);
        return steps.Select((s, i) => new ProjectStepDto(s.Key, s.Label, s.Done, i == current, s.Detail)).ToList();
    }

    /// <summary>A person's name for a stored email (who submitted or reviewed); the email when there's no such user.</summary>
    internal static async Task<string?> NameAsync(IWorkbenchDbContext db, string? email, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(email)
            ? email
            : await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == email).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) is { Length: > 0 } name ? name : email;

    private static async Task<Project> ProjectAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw WorkbenchNotFoundException.For("Project", projectId);

    /// <summary>The project's plan row, added (tracked, unsaved) when it has none.</summary>
    private async Task<ProjectPlan> PlanRowAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        if (await db.Set<ProjectPlan>().FirstOrDefaultAsync(p => p.ProjectId == projectId, ct) is { } plan)
        {
            return plan;
        }

        plan = new ProjectPlan { ProjectId = projectId, CreatedBy = audit.Actor, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.Set<ProjectPlan>().Add(plan);
        return plan;
    }

    private async Task<ProjectPlan> ReviewableAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var plan = await db.Set<ProjectPlan>().FirstOrDefaultAsync(p => p.ProjectId == project.Id, ct);
        if (plan is not { Status: ProjectPlanStatus.Submitted })
        {
            throw new ArgumentException($"The plan for {project.Code} isn't waiting for approval.");
        }

        if (string.Equals(plan.SubmittedBy, audit.Actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("You submitted this plan, so someone else in Finance reviews it.");
        }

        return plan;
    }

    private async Task NotifyManagerAsync(IWorkbenchDbContext db, Project project, string title, string body, string? comment, CancellationToken ct)
    {
        if (notifications is null || project.ProjectManagerId is not { } pm)
        {
            return;
        }

        var email = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == pm).Select(e => e.Email).FirstOrDefaultAsync(ct);
        if (email is not null && !string.Equals(email, audit.Actor, StringComparison.OrdinalIgnoreCase))
        {
            await notifications.PublishAsync(email, NotificationKind, title, comment is null ? body : $"{body} \"{comment}\"", Link(project.Id));
        }
    }

    private void Touch(ProjectPlan plan)
    {
        plan.UpdatedBy = audit.Actor;
        plan.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
    }

    private static string Link(Guid projectId) => $"projects/{projectId}/plan";

    private static string Money(decimal amount) => "$" + amount.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 2000)];
}

/// <summary>Lets the approvals inbox decide a submitted project plan through the plan service (rejecting sends it back to the project manager with the reason).</summary>
public sealed class ProjectPlanApprovalHandler(IServiceProvider services) : Approvals.IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.ProjectPlan;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IProjectPlanService>(services).ApproveAsync(documentId, comment, cancellationToken);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IProjectPlanService>(services).ReturnAsync(documentId, reason, cancellationToken);
}

/// <summary>
/// The plan's to-dos: for a project's manager, plan a project Finance handed over, fix and resubmit a returned plan, or
/// resubmit changes since approval; for Finance, review submitted plans.
/// </summary>
public sealed class ProjectPlanTodoProvider : IMyTodoProvider
{
    public string Name => "Project plans";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        var items = new List<MyTodoItem>();
        await using var db = await context.DbFactory.CreateAsync(cancellationToken);
        var managed = await context.ManagedProjectsAsync(cancellationToken);
        var ids = managed.Select(p => p.Id).ToList();
        var plans = await db.Set<ProjectPlan>().AsNoTracking().Where(p => ids.Contains(p.ProjectId)).ToDictionaryAsync(p => p.ProjectId, cancellationToken);
        foreach (var project in managed.Where(p => plans.ContainsKey(p.Id)))
        {
            var plan = plans[project.Id];
            var link = $"projects/{project.Id}/plan";
            switch (plan.Status)
            {
                case ProjectPlanStatus.Returned:
                    items.Add(new MyTodoItem($"plan:{project.Id}:returned:{plan.ReviewedAtUtc:O}", TodoCategories.Project, $"Fix and resubmit the plan for {project.Code}",
                        $"Finance returned it: \"{plan.ReviewComment}\"", TodoUrgency.ThisWeek, null, link, "Open the plan", Priority: 8));
                    break;
                case ProjectPlanStatus.Approved when await ProjectPlanService.ChangedSinceApprovalAsync(db, project.Id, await ProjectPlanService.PlanLinesAsync(db, project.Id, cancellationToken), cancellationToken):
                    // Once approved, the plan is the budget: people or hours added since are changes to send, not a staffing gap to chase.
                    items.Add(new MyTodoItem($"plan:{project.Id}:changes", TodoCategories.Project, $"Send the changes to the plan for {project.Code}",
                        "People or hours have changed since Finance approved it. Send the changes for approval.", TodoUrgency.ThisWeek, null, link, "Review the changes", Priority: 6));
                    break;
                case ProjectPlanStatus.Draft:
                    var hours = await db.Set<ResourceAllocation>().AnyAsync(a => a.ProjectId == project.Id && a.Hours > 0, cancellationToken);
                    items.Add(hours
                        ? new MyTodoItem($"plan:{project.Id}:submit", TodoCategories.Project, $"Submit the plan for {project.Code}",
                            "When the WBS and people are right, send it to Finance to approve.", TodoUrgency.ThisWeek, null, link, "Open the plan", Priority: 6)
                        : new MyTodoItem($"plan:{project.Id}:plan", TodoCategories.Project, $"Plan {project.Code} {project.Name}",
                            "Finance set it up for you. Build the WBS, assign people and their hours, then submit it.", TodoUrgency.ThisWeek, null, link, "Plan it", Priority: 8));
                    break;
            }
        }

        // Finance's side of it, a plan waiting for review, is in the Approvals inbox with everything else Finance decides (so it's counted there once).

        return items;
    }
}
