using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Domain.Approvals;
using Workbench.Domain.Automations;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Automations;

/// <summary>
/// Admin › Automation. Every save publishes a new immutable version and writes an audit row; deleting hides the automation
/// and keeps its history. Permission checks are in <see cref="PermissionCheckingAutomationService"/>.
/// </summary>
public sealed class AutomationService(
    IWorkbenchDbContextFactory dbFactory,
    AutomationIndex index,
    AutomationExecutor executor,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IServiceProvider services,
    TimeProvider clock,
    SheetAutomationRuntime? sheetRuntime = null) : IAutomationService
{
    public const string AuditEntityType = "Automation";

    private string Me => ApprovalEngine.Normalize(currentUser.Email);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Every automation, sheet automations included (each names its sheet).</summary>
    public Task<IReadOnlyList<AutomationSummaryDto>> ListAsync(CancellationToken cancellationToken = default) => SummariesAsync(null, cancellationToken);

    public async Task<IReadOnlyList<AutomationSummaryDto>> ListForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Sheet", sheetId);
            }
        }

        return await SummariesAsync(sheetId, cancellationToken);
    }

    private async Task<IReadOnlyList<AutomationSummaryDto>> SummariesAsync(Guid? sheetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<AutomationRecord>().AsNoTracking().Where(a => a.DeletedAtUtc == null);
        if (sheetId is { } only)
        {
            query = query.Where(a => a.SheetId == only);
        }

        var rows = await (sheetId is null ? query.OrderBy(a => a.Name) : query.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Name)).ToListAsync(cancellationToken);
        var sheetIds = rows.Where(r => r.SheetId != null).Select(r => r.SheetId!.Value).Distinct().ToList();
        var sheetNames = await db.Sheets.AsNoTracking().Where(s => sheetIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var sheetColumns = (await db.Columns.AsNoTracking().Where(c => sheetIds.Contains(c.SheetId)).ToListAsync(cancellationToken))
            .GroupBy(c => c.SheetId).ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Column>)g.ToList());
        var ids = rows.Select(r => r.Id).ToList();
        var since = Now.AddDays(-7);
        var recent = await db.Set<AutomationRun>().AsNoTracking()
            .Where(r => ids.Contains(r.AutomationId) && !r.IsDryRun && r.StartedAtUtc >= since)
            .Select(r => new { r.AutomationId, r.Status, r.StartedAtUtc }).ToListAsync(cancellationToken);
        var last = await db.Set<AutomationRun>().AsNoTracking().Where(r => ids.Contains(r.AutomationId) && !r.IsDryRun)
            .GroupBy(r => r.AutomationId).Select(g => new { Id = g.Key, At = g.Max(r => r.StartedAtUtc) }).ToListAsync(cancellationToken);
        var lastStatus = new Dictionary<Guid, (DateTime At, string Status)>();
        foreach (var l in last)
        {
            var status = await db.Set<AutomationRun>().AsNoTracking().Where(r => r.AutomationId == l.Id && r.StartedAtUtc == l.At && !r.IsDryRun).Select(r => r.Status).FirstAsync(cancellationToken);
            lastStatus[l.Id] = (l.At, status);
        }

        var versions = await db.Set<AutomationVersion>().AsNoTracking().Where(v => ids.Contains(v.AutomationId))
            .Select(v => new { v.Id, v.DefinitionJson }).ToListAsync(cancellationToken);
        var failed = new[] { nameof(AutomationRunStatus.Failed), nameof(AutomationRunStatus.PartiallyFailed) };
        return rows.Select(a =>
        {
            var definition = versions.FirstOrDefault(v => v.Id == a.CurrentVersionId) is { } v ? AutomationJson.Definition(v.DefinitionJson) : null;
            var mine = recent.Where(r => r.AutomationId == a.Id).ToList();
            var summary = definition is null ? a.TriggerKind
                : a.SheetId is { } sheet ? SheetAutomationRules.Describe(definition.Trigger, sheetColumns.GetValueOrDefault(sheet) ?? [])
                : AutomationText.Trigger(definition.Trigger);
            return new AutomationSummaryDto(a.Id, a.Name, a.Description, a.IsEnabled, Enum.Parse<AutomationTriggerKind>(a.TriggerKind), a.EntityType,
                summary, a.CurrentVersion, a.UpdatedBy, a.UpdatedAtUtc,
                lastStatus.TryGetValue(a.Id, out var ls) ? ls.At : null, lastStatus.TryGetValue(a.Id, out var ls2) ? ls2.Status : null,
                mine.Count(r => r.Status != nameof(AutomationRunStatus.ConditionsNotMet)), mine.Count(r => failed.Contains(r.Status)),
                a.SheetId, a.SheetId is { } id ? sheetNames.GetValueOrDefault(id) ?? "(deleted sheet)" : null);
        }).ToList();
    }

    public async Task<AutomationDto> GetAsync(Guid automationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, await FindAsync(db, automationId, cancellationToken), cancellationToken);
    }

    public async Task<AutomationDefinition> GetVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindAsync(db, automationId, cancellationToken);
        var row = await db.Set<AutomationVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.AutomationId == automationId && v.Version == version, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"Version {version} of this automation doesn't exist.");
        return AutomationJson.Definition(row.DefinitionJson);
    }

    public async Task<AutomationDto> CreateAsync(SaveAutomationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (name, description, definition, note) = await ValidateAsync(db, request, null, cancellationToken);
        var now = Now;
        var automation = new AutomationRecord
        {
            Name = name,
            Description = description,
            IsEnabled = request.IsEnabled,
            TriggerKind = definition.Trigger.Kind.ToString(),
            TemplateKey = AutomationExecutor.Clip(request.TemplateKey, 64),
            CreatedBy = Me,
            CreatedAtUtc = now,
            UpdatedBy = Me,
            UpdatedAtUtc = now,
        };
        db.Set<AutomationRecord>().Add(automation);
        Publish(db, automation, definition, note ?? "Created");
        audit.Record(db, AuditEntityType, automation.Id, "Created", after: Snapshot(automation, definition));
        await db.SaveChangesAsync(cancellationToken);
        index.Invalidate(db);
        return await DetailAsync(db, automation, cancellationToken);
    }

    public async Task<AutomationDto> UpdateAsync(Guid automationId, SaveAutomationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var automation = await FindAsync(db, automationId, cancellationToken, tracking: true);
        var (name, description, definition, note) = await ValidateAsync(db, request, automationId, cancellationToken);
        if (definition.Trigger.SheetId != automation.SheetId)
        {
            throw new ArgumentException(automation.SheetId is null ? "An automation can't move onto a sheet; create one from the sheet's Automate button." : "A sheet automation stays on its sheet.");
        }

        var before = Snapshot(automation, await CurrentDefinitionAsync(db, automation, cancellationToken));
        automation.Name = name;
        automation.Description = description;
        automation.IsEnabled = request.IsEnabled;
        automation.UpdatedBy = Me;
        automation.UpdatedAtUtc = Now;
        Publish(db, automation, definition, note);
        audit.Record(db, AuditEntityType, automation.Id, "Updated", before, Snapshot(automation, definition), note);
        await db.SaveChangesAsync(cancellationToken);
        index.Invalidate(db);
        return await DetailAsync(db, automation, cancellationToken);
    }

    public async Task<AutomationDto> SetEnabledAsync(Guid automationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var automation = await FindAsync(db, automationId, cancellationToken, tracking: true);
        if (automation.IsEnabled != enabled)
        {
            automation.IsEnabled = enabled;
            automation.UpdatedBy = Me;
            automation.UpdatedAtUtc = Now;
            audit.Record(db, AuditEntityType, automation.Id, enabled ? "Enabled" : "Disabled", new { IsEnabled = !enabled }, new { IsEnabled = enabled });
            await db.SaveChangesAsync(cancellationToken);
            index.Invalidate(db);
        }

        return await DetailAsync(db, automation, cancellationToken);
    }

    public async Task<AutomationDto> RestoreVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var automation = await FindAsync(db, automationId, cancellationToken, tracking: true);
        var row = await db.Set<AutomationVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.AutomationId == automationId && v.Version == version, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"Version {version} of this automation doesn't exist.");
        var definition = AutomationJson.Definition(row.DefinitionJson);
        var (_, _, validated, _) = await ValidateAsync(db, new SaveAutomationRequest(row.Name, definition, automation.Description, automation.IsEnabled), automationId, cancellationToken);
        var before = Snapshot(automation, await CurrentDefinitionAsync(db, automation, cancellationToken));
        automation.Name = row.Name;
        automation.UpdatedBy = Me;
        automation.UpdatedAtUtc = Now;
        Publish(db, automation, validated, $"Restored version {version}");
        audit.Record(db, AuditEntityType, automation.Id, "Restored", before, Snapshot(automation, validated), $"Version {version}");
        await db.SaveChangesAsync(cancellationToken);
        index.Invalidate(db);
        return await DetailAsync(db, automation, cancellationToken);
    }

    public async Task DeleteAsync(Guid automationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var automation = await FindAsync(db, automationId, cancellationToken, tracking: true);
        automation.DeletedAtUtc = Now;
        automation.DeletedBy = Me;
        automation.IsEnabled = false;
        audit.Record(db, AuditEntityType, automation.Id, "Deleted", before: new { automation.Name });
        await db.SaveChangesAsync(cancellationToken);
        index.Invalidate(db);
    }

    public async Task<AutomationRunDto> DryRunAsync(AutomationDryRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AutomationDefinition definition;
        string? name = request.Name;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (request.Definition is { } draft)
            {
                (_, _, definition, _) = await ValidateAsync(db, new SaveAutomationRequest(string.IsNullOrWhiteSpace(name) ? "Draft" : name, draft), request.AutomationId, cancellationToken);
            }
            else
            {
                var automation = await FindAsync(db, request.AutomationId ?? throw new ArgumentException("Choose an automation to test."), cancellationToken);
                definition = await CurrentDefinitionAsync(db, automation, cancellationToken);
                name ??= automation.Name;
            }
        }

        if (definition.Trigger.SheetId is { } sheetId)
        {
            var runtime = sheetRuntime ?? throw new InvalidOperationException("Sheet automations aren't available here.");
            return await runtime.DryRunAsync(request.AutomationId ?? Guid.Empty, string.IsNullOrWhiteSpace(name) ? "Draft" : name!, definition, sheetId,
                request.RecordId ?? throw new ArgumentException("Choose a row to test against."), Me, cancellationToken);
        }

        var entityType = request.EntityType ?? definition.Trigger.EntityType;
        if (AutomationEntities.Find(entityType) is not null && request.RecordId is null)
        {
            throw new ArgumentException("Choose a record to test against.");
        }

        return await executor.RunAsync(new AutomationInvocation(
            request.AutomationId ?? Guid.Empty, definition.Trigger.Kind, "Dry run", entityType, request.RecordId, Me, DryRun: true,
            DefinitionOverride: request.Definition is null ? null : definition, NameOverride: name), cancellationToken);
    }

    public async Task<AutomationRunDto> RunNowAsync(Guid automationId, AutomationRunNowRequest request, CancellationToken cancellationToken = default)
    {
        string? entityType;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var automation = await FindAsync(db, automationId, cancellationToken);
            if (automation.SheetId is not null)
            {
                throw new ArgumentException("Sheet automations run when their trigger happens; use a dry run to test one on a row.");
            }

            entityType = automation.EntityType;
            if (AutomationEntities.Find(entityType) is not null && request.RecordId is null)
            {
                throw new ArgumentException("Choose the record to run it on.");
            }
        }

        return await executor.RunAsync(new AutomationInvocation(automationId, AutomationTriggerKind.Manual, $"Run by {Me}", entityType, request.RecordId, Me), cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationRunDto>> ListRunsAsync(AutomationRunQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = db.Set<AutomationRun>().AsNoTracking();
        if (query.AutomationId is { } id)
        {
            runs = runs.Where(r => r.AutomationId == id);
        }

        if (query.Status is { } status)
        {
            var text = status.ToString();
            runs = runs.Where(r => r.Status == text);
        }

        if (query.EntityType is { } type)
        {
            runs = runs.Where(r => r.EntityType == type);
        }

        if (query.RecordId is { } recordId)
        {
            runs = runs.Where(r => r.RecordId == recordId);
        }

        var rows = await runs.OrderByDescending(r => r.StartedAtUtc).Take(Math.Clamp(query.Take, 1, 500)).ToListAsync(cancellationToken);
        return rows.Select(AutomationMapping.ToDto).ToList();
    }

    public async Task<AutomationRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<AutomationRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Automation run", runId);
        return AutomationMapping.ToDto(run);
    }

    public async Task<AutomationCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var entities = new List<AutomationEntityTypeDto>();
        foreach (var entity in AutomationEntities.All)
        {
            var fields = await AutomationScope.FieldsAsync(entity, services, cancellationToken);
            entities.Add(new AutomationEntityTypeDto(entity.Key, entity.Label, fields,
                AutomationRelationships.All.Where(r => Relates(entity, r)).ToList(), entity.ApprovalDocumentType,
                fields.Where(f => f.IsCustom || (entity.Property(f.Key) is { } p && entity.Writable.Contains(p.Name))).Select(f => f.Key).ToList()));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var workflows = await db.Set<ApprovalWorkflow>().AsNoTracking().Where(w => w.DocumentType.StartsWith(ApprovalDocumentTypes.CustomPrefix))
            .OrderBy(w => w.Name).Select(w => new AutomationOptionDto(w.DocumentType, w.Name)).ToListAsync(cancellationToken);
        var connectors = await db.Set<IntegrationConnector>().AsNoTracking().Where(c => c.Kind == "teams" || c.Kind == "slack")
            .OrderBy(c => c.Name).Select(c => new AutomationOptionDto(c.Id.ToString(), c.Name + (c.IsActive ? "" : " (off)"))).ToListAsync(cancellationToken);
        var endpoints = await db.Set<WebhookEndpoint>().AsNoTracking().OrderBy(e => e.Name)
            .Select(e => new AutomationOptionDto(e.Id.ToString(), e.Name + (e.IsActive ? "" : " (off)"))).ToListAsync(cancellationToken);
        var manual = nameof(AutomationTriggerKind.Manual);
        var automations = await db.Set<AutomationRecord>().AsNoTracking().Where(a => a.DeletedAtUtc == null && a.TriggerKind == manual)
            .OrderBy(a => a.Name).Select(a => new AutomationOptionDto(a.Id.ToString(), a.Name)).ToListAsync(cancellationToken);
        var companies = await db.Set<Company>().AsNoTracking().OrderBy(c => c.Code).Select(c => new AutomationOptionDto(c.Id.ToString(), c.Code + " " + c.LegalName)).ToListAsync(cancellationToken);
        var units = await db.Set<OrgUnit>().AsNoTracking().OrderBy(u => u.Code).Select(u => new AutomationOptionDto(u.Id.ToString(), u.Code + " " + u.Name)).ToListAsync(cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.Status != ProjectStatus.Closed).OrderBy(p => p.Code)
            .Select(p => new AutomationOptionDto(p.Id.ToString(), p.Code + " " + p.Name)).Take(500).ToListAsync(cancellationToken);
        var events = WebhookEventCatalog.All.Select(e => new AutomationEventDto(e.Key, e.Label, e.Group, AutomationText.EventEntity(e.Key))).ToList();
        return new AutomationCatalogDto(entities, events, ApprovalOrgRoleName.All, workflows, connectors, endpoints, automations, companies, units, projects);
    }

    public async Task<IReadOnlyList<AutomationOptionDto>> ListRecordsAsync(string entityType, string? search = null, CancellationToken cancellationToken = default)
    {
        var entity = AutomationEntities.Require(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var method = typeof(AutomationService).GetMethod(nameof(RecentAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(entity.ClrType);
        var records = await (Task<List<object>>)method.Invoke(null, [db, cancellationToken])!;
        var term = search?.Trim();
        return records
            .Select(r => new AutomationOptionDto(((Domain.Entity)r).Id.ToString(), entity.Describe(r)))
            .Where(o => string.IsNullOrEmpty(term) || o.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(50).ToList();
    }

    private static async Task<List<object>> RecentAsync<T>(IWorkbenchDbContext db, CancellationToken ct)
        where T : Domain.Entity =>
        (await db.Set<T>().AsNoTracking().OrderByDescending(e => e.Id).Take(500).ToListAsync(ct)).Cast<object>().ToList();

    public IReadOnlyList<AutomationTemplateDto> ListTemplates() => AutomationTemplates.All;

    public async Task<SaveAutomationRequest> BuildRecipeAsync(string key, Guid? sheetId, IReadOnlyDictionary<string, string?> blanks, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Domain.Column> columns = [];
        if (sheetId is { } id)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            if (!await db.Sheets.AnyAsync(s => s.Id == id, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Sheet", id);
            }

            columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == id).OrderBy(c => c.Order).ToListAsync(cancellationToken);
        }

        return AutomationTemplates.Build(key, sheetId, columns, blanks);
    }

    public async Task<SheetAutomationCatalogDto> GetSheetCatalogAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => new { s.Id, s.Name, s.WorkspaceId }).FirstOrDefaultAsync(cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).OrderBy(c => c.Order).ToListAsync(cancellationToken);
        var fields = new List<SheetAutomationFieldDto>
        {
            new(AutomationSheets.Title, "Title", "Title", [], false),
            new(AutomationSheets.Start, "Start", "Date", [], false),
            new(AutomationSheets.Finish, "Finish", "Date", [], false),
        };
        fields.AddRange(columns.Select(c => new SheetAutomationFieldDto(c.Id.ToString(), c.Name, c.Type.ToString(), c.Options, c.Formula is not null)));
        var rows = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).OrderBy(i => i.ParentId == null ? 0 : 1).ThenBy(i => i.Order).Take(200)
            .Select(i => new AutomationOptionDto(i.Id.ToString(), i.Title)).ToListAsync(cancellationToken);

        // Sheets the signed-in user can edit (moving or copying rows needs it, now and when the automation runs).
        var targets = new List<AutomationOptionDto>();
        var others = await db.Sheets.AsNoTracking().Where(s => s.Id != sheetId).OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.WorkspaceId }).Take(500).ToListAsync(cancellationToken);
        var roles = new Dictionary<Guid, bool>();
        foreach (var other in others)
        {
            if (!roles.TryGetValue(other.WorkspaceId, out var editable))
            {
                roles[other.WorkspaceId] = editable = await SheetAutomationRuntime.RoleOfAsync(db, Me, other.WorkspaceId) is Domain.Identity.WorkspaceRole.Owner or Domain.Identity.WorkspaceRole.Editor;
            }

            if (editable)
            {
                targets.Add(new AutomationOptionDto(other.Id.ToString(), other.Name));
            }
        }

        var workflows = await db.Set<ApprovalWorkflow>().AsNoTracking().Where(w => w.DocumentType.StartsWith(ApprovalDocumentTypes.CustomPrefix))
            .OrderBy(w => w.Name).Select(w => new AutomationOptionDto(w.DocumentType, w.Name)).ToListAsync(cancellationToken);
        var connectors = await db.Set<IntegrationConnector>().AsNoTracking().Where(c => c.Kind == "teams" || c.Kind == "slack")
            .OrderBy(c => c.Name).Select(c => new AutomationOptionDto(c.Id.ToString(), c.Name + (c.IsActive ? "" : " (off)"))).ToListAsync(cancellationToken);
        var endpoints = await db.Set<WebhookEndpoint>().AsNoTracking().OrderBy(e => e.Name)
            .Select(e => new AutomationOptionDto(e.Id.ToString(), e.Name + (e.IsActive ? "" : " (off)"))).ToListAsync(cancellationToken);
        var canEdit = await SheetAutomationRuntime.RoleOfAsync(db, Me, sheet.WorkspaceId) is Domain.Identity.WorkspaceRole.Owner or Domain.Identity.WorkspaceRole.Editor
            || (services.GetService(typeof(IAutomationPermissions)) is IAutomationPermissions p && await p.CanManageAsync(cancellationToken));
        return new SheetAutomationCatalogDto(sheet.Id, sheet.Name, fields, rows, targets, workflows, connectors, endpoints, canEdit);
    }

    public async Task<AutomationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var s = await db.Set<AutomationSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new AutomationSettings();
        return new AutomationSettingsDto(s.PausedAll, s.PausedBy, s.PausedAtUtc, s.MaxChainDepth, s.MaxRunsPerRecordPerHour);
    }

    public async Task<AutomationSettingsDto> SaveSettingsAsync(SaveAutomationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxChainDepth is < 1 or > 20)
        {
            throw new ArgumentException("The chain depth limit must be between 1 and 20.");
        }

        if (request.MaxRunsPerRecordPerHour is < 1 or > 1000)
        {
            throw new ArgumentException("The runs per record per hour limit must be between 1 and 1000.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var s = await AutomationSettingsStore.GetOrCreateAsync(db, cancellationToken);
        var before = new { s.PausedAll, s.MaxChainDepth, s.MaxRunsPerRecordPerHour };
        if (s.PausedAll != request.PausedAll)
        {
            s.PausedAll = request.PausedAll;
            s.PausedBy = request.PausedAll ? Me : null;
            s.PausedAtUtc = request.PausedAll ? Now : null;
        }

        s.MaxChainDepth = request.MaxChainDepth;
        s.MaxRunsPerRecordPerHour = request.MaxRunsPerRecordPerHour;
        audit.Record(db, AuditEntityType, s.Id, request.PausedAll ? "SettingsChanged (paused)" : "SettingsChanged", before,
            new { s.PausedAll, s.MaxChainDepth, s.MaxRunsPerRecordPerHour });
        await db.SaveChangesAsync(cancellationToken);
        index.Invalidate(db);
        return new AutomationSettingsDto(s.PausedAll, s.PausedBy, s.PausedAtUtc, s.MaxChainDepth, s.MaxRunsPerRecordPerHour);
    }

    // ---- helpers ----

    private void Publish(IWorkbenchDbContext db, AutomationRecord automation, AutomationDefinition definition, string? note)
    {
        var version = new AutomationVersion
        {
            AutomationId = automation.Id,
            Version = automation.CurrentVersion + 1,
            Name = automation.Name,
            DefinitionJson = AutomationJson.Serialize(definition),
            CreatedBy = Me,
            CreatedAtUtc = Now,
            Note = note,
        };
        db.Set<AutomationVersion>().Add(version);
        automation.CurrentVersion = version.Version;
        automation.CurrentVersionId = version.Id;
        automation.TriggerKind = definition.Trigger.Kind.ToString();
        automation.EntityType = definition.Trigger.EntityType;
        automation.SheetId = definition.Trigger.SheetId;
        automation.TriggerKeys = definition.Trigger.Kind switch
        {
            AutomationTriggerKind.RecordEvent => string.Join(",", (definition.Trigger.Events ?? []).Distinct()),
            AutomationTriggerKind.FieldChanged => definition.Trigger.FieldKey,
            AutomationTriggerKind.IntegrationEvent => definition.Trigger.EventKey,
            AutomationTriggerKind.Threshold => definition.Trigger.Threshold?.ToString(),
            _ => null,
        };
        automation.HasBlockingAction = definition.Actions.Any(a => a.Kind == AutomationActionKind.Block);
    }

    private static object Snapshot(AutomationRecord a, AutomationDefinition? d) => new
    {
        a.Name,
        a.IsEnabled,
        a.CurrentVersion,
        Trigger = d is null ? null : AutomationText.Trigger(d.Trigger),
        Actions = d?.Actions.Select(x => x.Kind.ToString()).ToList(),
        RunAs = d is null ? null : d.RunAs == AutomationRunAs.ServicePrincipal ? d.ServicePrincipalEmail : "actor",
    };

    private static async Task<AutomationDefinition> CurrentDefinitionAsync(IWorkbenchDbContext db, AutomationRecord automation, CancellationToken ct) =>
        AutomationJson.Definition((await db.Set<AutomationVersion>().AsNoTracking().FirstAsync(v => v.Id == automation.CurrentVersionId, ct)).DefinitionJson);

    private static async Task<AutomationRecord> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct, bool tracking = false)
    {
        var query = tracking ? db.Set<AutomationRecord>() : db.Set<AutomationRecord>().AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAtUtc == null, ct) ?? throw WorkbenchNotFoundException.For("Automation", id);
    }

    private static async Task<AutomationDto> DetailAsync(IWorkbenchDbContext db, AutomationRecord a, CancellationToken ct)
    {
        var versions = await db.Set<AutomationVersion>().AsNoTracking().Where(v => v.AutomationId == a.Id).OrderByDescending(v => v.Version).ToListAsync(ct);
        var current = versions.First(v => v.Id == a.CurrentVersionId);
        return new AutomationDto(a.Id, a.Name, a.Description, a.IsEnabled, a.CurrentVersion, AutomationJson.Definition(current.DefinitionJson),
            versions.Select(v => new AutomationVersionDto(v.Version, v.CreatedBy, v.CreatedAtUtc, v.Note, v.Id == a.CurrentVersionId)).ToList(),
            a.CreatedBy, a.CreatedAtUtc, a.UpdatedBy, a.UpdatedAtUtc, a.TemplateKey);
    }

    private static bool Relates(AutomationEntity e, string relationship) => relationship switch
    {
        AutomationRelationships.Submitter => e.SubmitterProperty is not null || e.ApprovalDocumentType is not null,
        AutomationRelationships.Employee or AutomationRelationships.Supervisor => e.EmployeeProperty is not null,
        AutomationRelationships.ProjectManager => e.ProjectProperty is not null || e.Key == "contract",
        _ => true,
    };

    /// <summary>Checks a definition against the catalog and the saver's own access; returns it with text trimmed.</summary>
    private async Task<(string Name, string? Description, AutomationDefinition Definition, string? Note)> ValidateAsync(
        IWorkbenchDbContext db, SaveAutomationRequest request, Guid? selfId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(request.Name) ? throw new ArgumentException("Give the automation a name.") : request.Name.Trim();
        if (name.Length > 200)
        {
            throw new ArgumentException("The name can be at most 200 characters.");
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > 1000)
        {
            throw new ArgumentException("The description can be at most 1000 characters.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000)
        {
            throw new ArgumentException("The note can be at most 1000 characters.");
        }

        var d = request.Definition ?? throw new ArgumentException("Define what the automation does.");
        var trigger = d.Trigger ?? throw new ArgumentException("Choose a trigger.");
        if (trigger.SheetId is not null)
        {
            return (name, description, await SheetAutomationRules.ValidateAsync(db, d, Me, ct), note);
        }

        trigger = trigger.Kind switch
        {
            AutomationTriggerKind.Threshold => trigger.Threshold switch
            {
                ThresholdKind.ContractFundingBelow => trigger with { EntityType = "contract" },
                ThresholdKind.ProjectBudgetOver => trigger with { EntityType = "project" },
                ThresholdKind.TimesheetMissing => trigger with { EntityType = "employee" },
                ThresholdKind.ApprovalWaiting => trigger,
                _ => throw new ArgumentException("Choose what the threshold measures."),
            },
            AutomationTriggerKind.IntegrationEvent => trigger with { EntityType = AutomationText.EventEntity(trigger.EventKey ?? "") },
            _ => trigger,
        };
        var entity = AutomationEntities.Find(trigger.EntityType);
        if (trigger.EntityType is not null && entity is null)
        {
            throw new ArgumentException($"\"{trigger.EntityType}\" isn't a record type automations know.");
        }

        var fields = entity is null ? [] : await AutomationScope.FieldsAsync(entity, services, ct);
        bool HasField(string? key) => key is not null && fields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.RecordEvent:
                if (entity is null || (trigger.Events?.Count ?? 0) == 0)
                {
                    throw new ArgumentException("Choose the record type and at least one event.");
                }

                break;
            case AutomationTriggerKind.FieldChanged:
                if (entity is null || !HasField(trigger.FieldKey))
                {
                    throw new ArgumentException("Choose the record type and the field to watch.");
                }

                break;
            case AutomationTriggerKind.IntegrationEvent:
                if (trigger.EventKey is null || !WebhookEventCatalog.IsKnown(trigger.EventKey))
                {
                    throw new ArgumentException("Choose one of Workbench's events.");
                }

                break;
            case AutomationTriggerKind.Schedule:
                if (trigger.Hour is < 0 or > 23 || trigger.DayOfMonth is < 1 or > 31)
                {
                    throw new ArgumentException("The hour must be 0–23 and the day of the month 1–31.");
                }

                break;
            case AutomationTriggerKind.DateField:
                var dateField = entity?.Property(trigger.FieldKey ?? "");
                var type = dateField is null ? null : Nullable.GetUnderlyingType(dateField.PropertyType) ?? dateField.PropertyType;
                if (type != typeof(DateOnly) && type != typeof(DateTime))
                {
                    throw new ArgumentException("Choose the record type and one of its built-in date fields.");
                }

                if (trigger.OffsetDays is < -3650 or > 3650 || trigger.Hour is < 0 or > 23)
                {
                    throw new ArgumentException("The offset must be within ten years and the hour 0–23.");
                }

                break;
            case AutomationTriggerKind.Threshold:
                if (trigger.Threshold is ThresholdKind.ContractFundingBelow or ThresholdKind.ProjectBudgetOver && trigger.Percent is not (> 0 and <= 1000))
                {
                    throw new ArgumentException("Enter the threshold percentage (for example 25).");
                }

                if (trigger.Threshold is ThresholdKind.ApprovalWaiting && entity is not null && entity.ApprovalDocumentType is null)
                {
                    throw new ArgumentException($"{entity.Label} records don't go through approval workflows.");
                }

                if (trigger.Days is < 0 or > 365)
                {
                    throw new ArgumentException("The number of days must be 0–365.");
                }

                break;
        }

        ValidateGroup(d.Conditions, depth: 0);
        void ValidateGroup(AutomationConditionGroup? group, int depth)
        {
            if (group is null)
            {
                return;
            }

            if (depth > 4)
            {
                throw new ArgumentException("Condition groups can be nested at most four deep.");
            }

            foreach (var c in group.Conditions ?? [])
            {
                switch (c.Subject)
                {
                    case ConditionSubject.Field when !HasField(c.FieldKey) && !(entity is null && !string.IsNullOrWhiteSpace(c.FieldKey)):
                        throw new ArgumentException($"\"{c.FieldKey}\" isn't a field of {entity?.Label.ToLowerInvariant() ?? "this record"}.");
                    case ConditionSubject.ActorRole when (c.Values ?? []).Concat(c.Value is null ? [] : [c.Value]).Any(r => !ApprovalOrgRoleName.All.Contains(r)):
                        throw new ArgumentException("Choose roles from the list.");
                    case ConditionSubject.Company or ConditionSubject.OrgUnit or ConditionSubject.Project
                        when (c.Values ?? []).Concat(c.Value is null ? [] : [c.Value]).Any(v => !Guid.TryParse(v, out _)):
                        throw new ArgumentException("Choose companies, org units or projects from the list.");
                }
            }

            foreach (var child in group.Groups ?? [])
            {
                ValidateGroup(child, depth + 1);
            }
        }

        if (d.Actions is not { Count: > 0 } actions)
        {
            throw new ArgumentException("Add at least one action.");
        }

        if (actions.Count > 20)
        {
            throw new ArgumentException("An automation can have at most 20 actions.");
        }

        if (actions.Any(a => a.Kind == AutomationActionKind.Block))
        {
            if (trigger.Kind is not (AutomationTriggerKind.RecordEvent or AutomationTriggerKind.FieldChanged))
            {
                throw new ArgumentException("Refusing a change only works with a record event or field change trigger (it runs before the change is saved).");
            }

            if (actions.Count > 1)
            {
                throw new ArgumentException("A validation rule (refuse the change) can't have other actions: a refused change doesn't happen.");
            }

            if (string.IsNullOrWhiteSpace(actions[0].Message))
            {
                throw new ArgumentException("Say why the change is refused; people see this message.");
            }
        }

        foreach (var action in actions)
        {
            await ValidateActionAsync(db, action, entity, fields, selfId, ct);
        }

        if (d.RunAs == AutomationRunAs.ServicePrincipal)
        {
            var email = ApprovalEngine.Normalize(d.ServicePrincipalEmail);
            if (!await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Email == email && u.IsActive, ct))
            {
                throw new ArgumentException("The service user must be an active Workbench user.");
            }

            // Nobody designates a principal with access they don't have themselves.
            var mine = await Identity.RoleStore.ResolveAsync(db, Me, ct);
            var theirs = await Identity.RoleStore.ResolveAsync(db, email, ct);
            if (!mine.BuiltIn.IsAdmin() && theirs.Permissions.Except(mine.Permissions).Any())
            {
                throw new WorkbenchForbiddenException("You can't have an automation act as a user with roles you don't have.");
            }

            d = d with { ServicePrincipalEmail = email };
        }
        else
        {
            d = d with { ServicePrincipalEmail = null };
        }

        return (name, description, d with { Trigger = trigger }, note);
    }

    private static async Task ValidateActionAsync(IWorkbenchDbContext db, AutomationAction action, AutomationEntity? entity, IReadOnlyList<Contracts.Customization.EntityFieldDto> fields,
        Guid? selfId, CancellationToken ct)
    {
        if (action.Title?.Length > 300 || action.Message?.Length > 2000 || action.Value?.Length > 2000)
        {
            throw new ArgumentException("Titles can be at most 300 characters and messages 2000.");
        }

        switch (action.Kind)
        {
            case AutomationActionKind.Notify or AutomationActionKind.CreateTask:
                if ((action.Recipients?.Count ?? 0) == 0)
                {
                    throw new ArgumentException("Choose who to tell.");
                }

                foreach (var r in action.Recipients!)
                {
                    var ok = r.Kind switch
                    {
                        RecipientKind.Person => MailAddress.TryCreate(r.Value?.Trim(), out _),
                        RecipientKind.Role => Enum.TryParse<ApprovalOrgRole>(r.Value, true, out _),
                        RecipientKind.Field => fields.Any(f => string.Equals(f.Key, r.Value, StringComparison.OrdinalIgnoreCase)),
                        RecipientKind.Relationship => AutomationRelationships.All.Contains(r.Value),
                        _ => false,
                    };
                    if (!ok)
                    {
                        throw new ArgumentException($"\"{r.Value}\" isn't a valid recipient.");
                    }
                }

                if (action.DueInDays is < 0 or > 365)
                {
                    throw new ArgumentException("A to-do can be due at most a year out.");
                }

                break;
            case AutomationActionKind.SetField:
                if (entity is null || action.FieldKey is null || !fields.Any(f => string.Equals(f.Key, action.FieldKey, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException("Choose the field to set.");
                }

                var builtIn = entity.Property(action.FieldKey);
                if (builtIn is not null && !entity.Writable.Contains(builtIn.Name)
                    || entity.Computed.Any(c => string.Equals(c.Key, action.FieldKey, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"{entity.Label} › {action.FieldKey} can't be set by an automation.");
                }

                break;
            case AutomationActionKind.RequireApproval:
                if (entity is null)
                {
                    throw new ArgumentException("Requiring an approval needs a record type.");
                }

                if (action.WorkflowKey is not { } key || !ApprovalDocumentTypes.IsCustom(key)
                    || !await db.Set<ApprovalWorkflow>().AsNoTracking().AnyAsync(w => w.DocumentType == key, ct))
                {
                    throw new ArgumentException("Choose a record approval workflow (create one in Setup › Approval workflows).");
                }

                break;
            case AutomationActionKind.PostToChat:
                if (!await db.Set<IntegrationConnector>().AsNoTracking().AnyAsync(c => c.Id == action.ConnectorId && (c.Kind == "teams" || c.Kind == "slack"), ct))
                {
                    throw new ArgumentException("Choose a Teams or Slack connector (Admin › Integrations).");
                }

                break;
            case AutomationActionKind.CallWebhook:
                if (!await db.Set<WebhookEndpoint>().AsNoTracking().AnyAsync(e => e.Id == action.WebhookEndpointId, ct))
                {
                    throw new ArgumentException("Choose a webhook endpoint (Admin › Integrations).");
                }

                break;
            case AutomationActionKind.StartAutomation:
                if (action.TargetAutomationId is not { } target || target == selfId)
                {
                    throw new ArgumentException("Choose another automation to start.");
                }

                var manual = nameof(AutomationTriggerKind.Manual);
                if (!await db.Set<AutomationRecord>().AsNoTracking().AnyAsync(a => a.Id == target && a.DeletedAtUtc == null && a.TriggerKind == manual, ct))
                {
                    throw new ArgumentException("Only automations with the \"Run by hand or by another automation\" trigger can be started.");
                }

                break;
        }
    }
}

/// <summary>Plain-language descriptions of triggers, for the list and the run log.</summary>
public static class AutomationText
{
    public static string Trigger(AutomationTrigger t)
    {
        var entity = AutomationEntities.Find(t.EntityType)?.Label ?? "record";
        return t.Kind switch
        {
            AutomationTriggerKind.RecordEvent => $"{entity} {string.Join(" or ", (t.Events ?? []).Select(e => Event(e)))}",
            AutomationTriggerKind.FieldChanged => $"{entity} {t.FieldKey} changes{(t.FromValue is null ? "" : $" from {t.FromValue}")}{(t.ToValue is null ? "" : $" to {t.ToValue}")}",
            AutomationTriggerKind.IntegrationEvent => WebhookEventCatalog.All.FirstOrDefault(e => e.Key == t.EventKey)?.Label ?? t.EventKey ?? "Event",
            AutomationTriggerKind.Schedule => t.Frequency switch
            {
                ScheduleFrequency.Weekly => $"Every {t.DayOfWeek ?? DayOfWeek.Monday} at {t.Hour ?? 7}:00",
                ScheduleFrequency.Monthly => $"Monthly on day {t.DayOfMonth ?? 1} at {t.Hour ?? 7}:00",
                _ => $"Daily at {t.Hour ?? 7}:00",
            } + (t.EntityType is null ? "" : $", for each {entity.ToLowerInvariant()}"),
            AutomationTriggerKind.DateField => t.OffsetDays switch
            {
                < 0 => $"{-t.OffsetDays} days before {entity.ToLowerInvariant()} {t.FieldKey}",
                > 0 => $"{t.OffsetDays} days after {entity.ToLowerInvariant()} {t.FieldKey}",
                _ => $"On {entity.ToLowerInvariant()} {t.FieldKey}",
            },
            AutomationTriggerKind.Threshold => t.Threshold switch
            {
                ThresholdKind.ContractFundingBelow => $"Contract funding below {t.Percent:0.#}%",
                ThresholdKind.ProjectBudgetOver => $"Project budget over {t.Percent:0.#}%",
                ThresholdKind.TimesheetMissing => $"Timesheet missing {t.Days ?? 1} day(s) after the period",
                ThresholdKind.ApprovalWaiting => $"{(t.EntityType is null ? "Any document" : entity)} waiting {t.Days ?? 2} business days for approval",
                _ => "Threshold",
            },
            _ => "Run by hand or by another automation",
        };
    }

    public static string Event(RecordEventKind e) => e switch
    {
        RecordEventKind.StatusChanged => "status changes",
        _ => "is " + e.ToString().ToLowerInvariant(),
    };

    /// <summary>The record type an integration event is about (timesheet.submitted → timesheet).</summary>
    public static string? EventEntity(string key) => key.Split('.')[0] switch
    {
        "timesheet" => "timesheet",
        "invoice" => "invoice",
        "bill" => "ap-bill",
        "purchase_order" => "purchase-order",
        "project" => "project",
        "employee" => "employee",
        "opportunity" => "opportunity",
        _ => null,
    };
}
