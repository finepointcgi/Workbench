using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automation;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Identity;
using Workbench.Domain.Automations;

namespace Workbench.Application.Automations;

/// <summary>
/// Checks the "manage automations" and "run automation" permissions (<see cref="IAutomationPermissions"/>). Sheet automations
/// are also open to the sheet's people: Viewers see them and their runs, Editors create and change them (as sheet rules always
/// were). A sheet automation that reads or writes a restricted column (trigger, conditions, actions, or a <c>{Column}</c>
/// placeholder) is for those who can see restricted columns: to everyone else it, and its runs, don't exist.
/// </summary>
public sealed class PermissionCheckingAutomationService(IAutomationService inner, IAutomationPermissions permissions, AccessControl access,
    IWorkbenchDbContextFactory dbFactory, RestrictedColumnAccess restricted) : IAutomationService
{
    private async Task ManageAsync(CancellationToken ct)
    {
        if (!await permissions.CanManageAsync(ct))
        {
            await access.RequireRoleAsync(_ => false, "Only administrators can manage automations.", ct);
        }
    }

    /// <summary>The sheet's people at <paramref name="minimum"/> or above, or anyone who manages automations.</summary>
    private async Task SheetAsync(Guid sheetId, WorkspaceRole minimum, CancellationToken ct)
    {
        if (!await permissions.CanManageAsync(ct))
        {
            await access.RequireSheetRoleAsync(sheetId, minimum, ct);
        }
    }

    private async Task<Guid?> SheetOfAsync(Guid automationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<AutomationRecord>().AsNoTracking().Where(a => a.Id == automationId).Select(a => a.SheetId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Checks access to an existing automation: its sheet's role (and restricted columns), or managing automations.</summary>
    private async Task ForAsync(Guid automationId, WorkspaceRole minimum, CancellationToken ct)
    {
        if (await SheetOfAsync(automationId, ct) is not { } sheetId)
        {
            await ManageAsync(ct);
            return;
        }

        await SheetAsync(sheetId, minimum, ct);
        if (await TouchesAsync(sheetId, ct) is { } touches && touches((await inner.GetAsync(automationId, ct)).Definition))
        {
            throw WorkbenchNotFoundException.For("Automation", automationId);
        }
    }

    private async Task SavingAsync(AutomationDefinition? definition, CancellationToken ct)
    {
        if (definition?.Trigger?.SheetId is not { } sheetId)
        {
            await ManageAsync(ct);
            return;
        }

        await SheetAsync(sheetId, WorkspaceRole.Editor, ct);
        if (await TouchesAsync(sheetId, ct) is { } touches && touches(definition))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the workspace's owners can use restricted columns in automations.");
        }
    }

    /// <summary>Null when the user sees every column of the sheet; otherwise a test for "this automation touches a column hidden from them".</summary>
    private async Task<Func<AutomationDefinition, bool>?> TouchesAsync(Guid sheetId, CancellationToken ct)
    {
        var hidden = await restricted.HiddenColumnsAsync(sheetId, ct);
        if (hidden.Count == 0)
        {
            return null;
        }

        var ids = hidden.Select(h => h.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var db = await dbFactory.CreateAsync(ct);
        var names = (await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct)).Where(c => hidden.Contains(c.Id)).Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Field(string? field) => field is not null && ids.Contains(field);
        bool Template(string? text) => RuleLogic.PlaceholderNames(text).Any(names.Contains);
        bool Group(AutomationConditionGroup? g) => g is not null && ((g.Conditions ?? []).Any(c => Field(c.FieldKey)) || (g.Groups ?? []).Any(Group));
        return d => Field(d.Trigger.FieldKey) || Group(d.Conditions)
            || d.Actions.Any(a => Field(a.FieldKey) || (a.Recipients ?? []).Any(r => r.Kind == RecipientKind.Field && Field(r.Value))
                || Template(a.Value) || Template(a.Title) || Template(a.Message));
    }

    public async Task<IReadOnlyList<AutomationSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await ManageAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationSummaryDto>> ListForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await SheetAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var list = await inner.ListForSheetAsync(sheetId, cancellationToken);
        if (await TouchesAsync(sheetId, cancellationToken) is not { } touches)
        {
            return list;
        }

        var visible = new List<AutomationSummaryDto>();
        foreach (var summary in list)
        {
            if (!touches((await inner.GetAsync(summary.Id, cancellationToken)).Definition))
            {
                visible.Add(summary);
            }
        }

        return visible;
    }

    public async Task<AutomationDto> GetAsync(Guid automationId, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetAsync(automationId, cancellationToken);
    }

    public async Task<AutomationDefinition> GetVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetVersionAsync(automationId, version, cancellationToken);
    }

    public async Task<AutomationDto> CreateAsync(SaveAutomationRequest request, CancellationToken cancellationToken = default)
    {
        await SavingAsync(request?.Definition, cancellationToken);
        return await inner.CreateAsync(request!, cancellationToken);
    }

    public async Task<AutomationDto> UpdateAsync(Guid automationId, SaveAutomationRequest request, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Editor, cancellationToken);
        await SavingAsync(request?.Definition, cancellationToken);
        return await inner.UpdateAsync(automationId, request!, cancellationToken);
    }

    public async Task<AutomationDto> SetEnabledAsync(Guid automationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Editor, cancellationToken);
        return await inner.SetEnabledAsync(automationId, enabled, cancellationToken);
    }

    public async Task<AutomationDto> RestoreVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Editor, cancellationToken);
        return await inner.RestoreVersionAsync(automationId, version, cancellationToken);
    }

    public async Task DeleteAsync(Guid automationId, CancellationToken cancellationToken = default)
    {
        await ForAsync(automationId, WorkspaceRole.Editor, cancellationToken);
        await inner.DeleteAsync(automationId, cancellationToken);
    }

    /// <summary>A dry run changes nothing, but it's part of editing an automation, so it needs what editing needs.</summary>
    public async Task<AutomationRunDto> DryRunAsync(AutomationDryRunRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Definition is not null)
        {
            await SavingAsync(request.Definition, cancellationToken);
        }
        else if (request?.AutomationId is { } id)
        {
            await ForAsync(id, WorkspaceRole.Editor, cancellationToken);
        }
        else
        {
            await ManageAsync(cancellationToken);
        }

        return await inner.DryRunAsync(request!, cancellationToken);
    }

    public async Task<AutomationRunDto> RunNowAsync(Guid automationId, AutomationRunNowRequest request, CancellationToken cancellationToken = default)
    {
        if (!await permissions.CanRunAsync(automationId, cancellationToken))
        {
            await access.RequireRoleAsync(_ => false, "You don't have permission to run this automation.", cancellationToken);
        }

        return await inner.RunNowAsync(automationId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationRunDto>> ListRunsAsync(AutomationRunQuery query, CancellationToken cancellationToken = default)
    {
        if (query?.AutomationId is { } id)
        {
            await ForAsync(id, WorkspaceRole.Viewer, cancellationToken);
        }
        else
        {
            await ManageAsync(cancellationToken);
        }

        return await inner.ListRunsAsync(query!, cancellationToken);
    }

    public async Task<AutomationRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        Guid? automationId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            automationId = await db.Set<AutomationRun>().AsNoTracking().Where(r => r.Id == runId).Select(r => (Guid?)r.AutomationId).FirstOrDefaultAsync(cancellationToken);
        }

        if (automationId is { } id && id != Guid.Empty)
        {
            await ForAsync(id, WorkspaceRole.Viewer, cancellationToken);
        }
        else
        {
            await ManageAsync(cancellationToken);
        }

        return await inner.GetRunAsync(runId, cancellationToken);
    }

    public async Task<AutomationCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        await ManageAsync(cancellationToken);
        return await inner.GetCatalogAsync(cancellationToken);
    }

    public async Task<SheetAutomationCatalogDto> GetSheetCatalogAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await SheetAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var catalog = await inner.GetSheetCatalogAsync(sheetId, cancellationToken);
        var hidden = (await restricted.HiddenColumnsAsync(sheetId, cancellationToken)).Select(h => h.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return hidden.Count == 0 ? catalog : catalog with { Fields = catalog.Fields.Where(f => !hidden.Contains(f.Key)).ToList() };
    }

    public async Task<IReadOnlyList<AutomationOptionDto>> ListRecordsAsync(string entityType, string? search = null, CancellationToken cancellationToken = default)
    {
        await ManageAsync(cancellationToken);
        return await inner.ListRecordsAsync(entityType, search, cancellationToken);
    }

    public IReadOnlyList<AutomationTemplateDto> ListTemplates() => inner.ListTemplates();

    public async Task<SaveAutomationRequest> BuildRecipeAsync(string key, Guid? sheetId, IReadOnlyDictionary<string, string?> blanks, CancellationToken cancellationToken = default)
    {
        if (sheetId is { } id)
        {
            await SheetAsync(id, WorkspaceRole.Viewer, cancellationToken);
        }

        return await inner.BuildRecipeAsync(key, sheetId, blanks, cancellationToken);
    }

    public async Task<AutomationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await ManageAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<AutomationSettingsDto> SaveSettingsAsync(SaveAutomationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await ManageAsync(cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }
}
