using Workbench.Contracts;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// Automation rules: viewing rules and runs needs Viewer on the sheet; creating, changing, enabling or deleting them needs
/// Editor. A rule that reads or writes a restricted column (in its trigger, conditions, actions or a <c>{Column}</c>
/// placeholder) is for those who can see restricted columns: to everyone else it, and its runs, don't exist.
/// </summary>
public sealed class PermissionCheckingRuleService(IRuleService inner, AccessControl access, RestrictedColumnAccess restricted) : IRuleService
{
    public async Task<IReadOnlyList<RuleDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var rules = await inner.ListAsync(sheetId, cancellationToken);
        var touches = await RestrictedTouchAsync(sheetId, cancellationToken);
        return touches is null ? rules : rules.Where(r => !touches(r.Trigger, r.Conditions, r.Actions)).ToList();
    }

    public async Task<RuleDto> GetAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var rule = await inner.GetAsync(sheetId, ruleId, cancellationToken);
        await RequireVisibleAsync(sheetId, rule, cancellationToken);
        return rule;
    }

    public async Task<RuleDto> CreateAsync(Guid sheetId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireAllowedAsync(sheetId, request, cancellationToken);
        return await inner.CreateAsync(sheetId, request, cancellationToken);
    }

    public async Task<RuleDto> UpdateAsync(Guid sheetId, Guid ruleId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireVisibleAsync(sheetId, await inner.GetAsync(sheetId, ruleId, cancellationToken), cancellationToken);
        await RequireAllowedAsync(sheetId, request, cancellationToken);
        return await inner.UpdateAsync(sheetId, ruleId, request, cancellationToken);
    }

    public async Task<RuleDto> SetEnabledAsync(Guid sheetId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireVisibleAsync(sheetId, await inner.GetAsync(sheetId, ruleId, cancellationToken), cancellationToken);
        return await inner.SetEnabledAsync(sheetId, ruleId, enabled, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireVisibleAsync(sheetId, await inner.GetAsync(sheetId, ruleId, cancellationToken), cancellationToken);
        await inner.DeleteAsync(sheetId, ruleId, cancellationToken);
    }

    public async Task<IReadOnlyList<RuleRunDto>> ListRunsAsync(Guid sheetId, Guid? ruleId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var runs = await inner.ListRunsAsync(sheetId, ruleId, limit, cancellationToken);
        var touches = await RestrictedTouchAsync(sheetId, cancellationToken);
        if (touches is null)
        {
            return runs;
        }

        var hidden = (await inner.ListAsync(sheetId, cancellationToken)).Where(r => touches(r.Trigger, r.Conditions, r.Actions)).Select(r => r.Id).ToHashSet();
        return runs.Where(r => !hidden.Contains(r.RuleId)).ToList();
    }

    /// <summary>A dry run changes nothing, but it's part of editing a rule, so it needs Editor too.</summary>
    public async Task<RuleTestResult> TestAsync(Guid sheetId, TestRuleRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await RequireAllowedAsync(sheetId, request.Rule, cancellationToken);
        return await inner.TestAsync(sheetId, request, cancellationToken);
    }

    private async Task RequireVisibleAsync(Guid sheetId, RuleDto rule, CancellationToken cancellationToken)
    {
        if (await RestrictedTouchAsync(sheetId, cancellationToken) is { } touches && touches(rule.Trigger, rule.Conditions, rule.Actions))
        {
            throw WorkbenchNotFoundException.For("Rule", rule.Id);
        }
    }

    private async Task RequireAllowedAsync(Guid sheetId, SaveRuleRequest rule, CancellationToken cancellationToken)
    {
        if (await RestrictedTouchAsync(sheetId, cancellationToken) is { } touches && touches(rule.Trigger, rule.Conditions, rule.Actions))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the workspace's owners can use restricted columns in rules.");
        }
    }

    /// <summary>Null when the user sees every column; otherwise a test for "this rule touches a column hidden from them".</summary>
    private async Task<Func<RuleTriggerDto, IReadOnlyList<RuleConditionDto>, IReadOnlyList<RuleActionDto>, bool>?> RestrictedTouchAsync(
        Guid sheetId, CancellationToken cancellationToken)
    {
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        if (hidden.Count == 0)
        {
            return null;
        }

        var ids = hidden.Select(h => h.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = (await restricted.ColumnsAsync(sheetId, cancellationToken)).Where(c => hidden.Contains(c.Id)).Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Field(string? field) => field is not null && ids.Contains(field);
        bool Template(string? text) => Automation.RuleLogic.PlaceholderNames(text).Any(names.Contains);
        return (trigger, conditions, actions) =>
            Field(trigger.Field)
            || conditions.Any(c => Field(c.Field))
            || actions.Any(a => Field(a.Field) || (a.PersonFields ?? []).Any(Field) || Template(a.Value) || Template(a.Message));
    }
}

/// <summary>
/// Dashboards: the owner sees and edits their dashboards; members of a dashboard's workspace view it. Admins do both
/// for every dashboard. A dashboard the user can't see is not found. Creating a workspace dashboard needs Editor there.
/// </summary>
public sealed class PermissionCheckingDashboardService(IDashboardService inner, AccessControl access) : IDashboardService
{
    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        var all = await inner.ListAsync(cancellationToken);
        return visible is null ? all : all.Where(d => IsOwner(d) || (d.WorkspaceId is { } w && visible.Contains(w))).ToList();
    }

    public async Task<DashboardDto> GetAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var dashboard = await inner.GetAsync(dashboardId, cancellationToken);
        await RequireViewAsync(dashboard.Summary, cancellationToken);
        return dashboard;
    }

    public async Task<DashboardSummary> CreateAsync(CreateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (request.WorkspaceId is { } workspaceId)
        {
            var role = await access.GetWorkspaceRoleAsync(workspaceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Workspace", workspaceId);
            if (role < WorkspaceRole.Editor)
            {
                throw new WorkbenchForbiddenException("You need edit access to the workspace to add a dashboard to it.");
            }
        }

        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<DashboardSummary> UpdateAsync(Guid dashboardId, UpdateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        return await inner.UpdateAsync(dashboardId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        await inner.DeleteAsync(dashboardId, cancellationToken);
    }

    public async Task<WidgetDto> AddWidgetAsync(Guid dashboardId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        return await inner.AddWidgetAsync(dashboardId, request, cancellationToken);
    }

    public async Task<WidgetDto> UpdateWidgetAsync(Guid dashboardId, Guid widgetId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        return await inner.UpdateWidgetAsync(dashboardId, widgetId, request, cancellationToken);
    }

    public async Task DeleteWidgetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        await inner.DeleteWidgetAsync(dashboardId, widgetId, cancellationToken);
    }

    public async Task<DashboardDto> ReorderWidgetsAsync(Guid dashboardId, ReorderWidgetsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(dashboardId, cancellationToken);
        return await inner.ReorderWidgetsAsync(dashboardId, request, cancellationToken);
    }

    private bool IsOwner(DashboardSummary dashboard) => string.Equals(dashboard.OwnerEmail, access.Email, StringComparison.OrdinalIgnoreCase);

    private async Task RequireViewAsync(DashboardSummary dashboard, CancellationToken cancellationToken)
    {
        if (IsOwner(dashboard) || await access.IsAdminAsync(cancellationToken))
        {
            return;
        }

        if (dashboard.WorkspaceId is not { } workspaceId || await access.GetWorkspaceRoleAsync(workspaceId, cancellationToken) is null)
        {
            throw WorkbenchNotFoundException.For("Dashboard", dashboard.Id);
        }
    }

    private async Task RequireEditAsync(Guid dashboardId, CancellationToken cancellationToken)
    {
        access.RequireSignedIn();
        var dashboard = (await inner.GetAsync(dashboardId, cancellationToken)).Summary;
        await RequireViewAsync(dashboard, cancellationToken);
        if (!IsOwner(dashboard) && !await access.IsAdminAsync(cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the dashboard's owner can change it.");
        }
    }
}
