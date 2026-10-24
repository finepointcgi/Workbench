using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Sheets;

/// <summary>
/// Conditional formatting rules, stored with the sheet in the order they apply. Anyone who can open the sheet reads them;
/// editors save them. A rule that reads or colors a restricted column the caller can't see is left out of what they get
/// (its formatting would give the hidden values away) and keeps its place when they save the list.
/// </summary>
public sealed class SheetFormatRuleService(
    IWorkbenchDbContextFactory dbFactory, TimeProvider clock, AccessControl? access = null, RestrictedColumnAccess? restricted = null)
    : ISheetFormatRuleService
{
    public const int MaxRules = 50;
    public const int MaxConditions = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SheetFormatRulesDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        if (access is not null)
        {
            await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var hidden = await HiddenAsync(db, sheetId, cancellationToken);
        var rules = await LoadAsync(db, sheetId, cancellationToken);
        var visible = rules.Where(r => !Reads(r.Definition, hidden)).Select(r => new SheetFormatRuleDto(r.Rule.Id, r.Definition)).ToList();
        return new SheetFormatRulesDto(visible, await CanEditAsync(sheetId, cancellationToken), rules.Count - visible.Count);
    }

    public async Task<SheetFormatRulesDto> SaveAsync(Guid sheetId, SaveSheetFormatRulesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (access is not null)
        {
            await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw Contracts.WorkbenchNotFoundException.For("Sheet", sheetId);
        }

        var hidden = await HiddenAsync(db, sheetId, cancellationToken);
        var columns = (await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken)).Where(c => !hidden.Contains(c.Id)).ToList();
        var sent = (request.Rules ?? []).Select(r => (r.Id, Definition: Clean(r.Definition, columns))).ToList();
        if (sent.Count > MaxRules)
        {
            throw new ArgumentException($"A sheet has at most {MaxRules} formatting rules.");
        }

        var existing = await LoadAsync(db, sheetId, cancellationToken);

        // Rules the caller can't see stay where they were.
        var ordered = sent.Select(r => (Rule: existing.FirstOrDefault(e => e.Rule.Id == r.Id && !Reads(e.Definition, hidden)).Rule, r.Definition)).ToList();
        for (var i = 0; i < existing.Count; i++)
        {
            if (Reads(existing[i].Definition, hidden))
            {
                ordered.Insert(Math.Min(i, ordered.Count), (existing[i].Rule, existing[i].Definition));
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var kept = ordered.Where(o => o.Rule is not null).Select(o => o.Rule!.Id).ToHashSet();
        db.Set<SheetFormatRule>().RemoveRange(existing.Where(e => !kept.Contains(e.Rule.Id)).Select(e => e.Rule));
        for (var i = 0; i < ordered.Count; i++)
        {
            var (rule, definition) = ordered[i];
            if (rule is null)
            {
                rule = new SheetFormatRule { SheetId = sheetId };
                db.Set<SheetFormatRule>().Add(rule);
            }

            var json = JsonSerializer.Serialize(definition, Json);
            if (rule.DefinitionJson != json || rule.Order != i)
            {
                rule.DefinitionJson = json;
                rule.Order = i;
                rule.UpdatedAtUtc = now;
                rule.UpdatedBy = access?.Email;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ListAsync(sheetId, cancellationToken);
    }

    /// <summary>Checks a rule against the sheet's columns (the ones the caller can see) and the palette.</summary>
    internal static SheetFormatRuleDefinition Clean(SheetFormatRuleDefinition? rule, IReadOnlyList<Column> columns)
    {
        if (rule is null)
        {
            throw new ArgumentException("Each rule needs a condition and a format.");
        }

        var dtos = columns.Select(c => c.ToDto()).ToList();
        var conditions = (rule.Conditions ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Field)).ToList();
        if (conditions.Count == 0 || conditions.Count > MaxConditions)
        {
            throw new ArgumentException($"A rule has 1 to {MaxConditions} conditions.");
        }

        foreach (var condition in conditions)
        {
            if (!SheetFormats.Known(condition.Field, dtos))
            {
                throw new ArgumentException("A rule's condition names a column that isn't on this sheet.");
            }

            if (!Enum.IsDefined(condition.Operator))
            {
                throw new ArgumentException("A rule's condition has an unknown comparison.");
            }

            if (SheetFilterText.TakesValue(condition.Operator) && string.IsNullOrWhiteSpace(condition.Value))
            {
                throw new ArgumentException("Fill in the value each condition compares with.");
            }
        }

        var target = string.IsNullOrWhiteSpace(rule.Target) ? null : rule.Target.Trim();
        if (target is not null && !SheetFormats.Known(target, dtos))
        {
            throw new ArgumentException("A rule formats a column that isn't on this sheet.");
        }

        var color = string.IsNullOrWhiteSpace(rule.Color) ? null : StatusColors.Find(rule.Color)?.Key
            ?? throw new ArgumentException("Pick a color from the palette.");
        if (color is null && !rule.Bold && !rule.Strikethrough)
        {
            throw new ArgumentException("Each rule needs a format: a color, bold or strikethrough.");
        }

        return new SheetFormatRuleDefinition(
            conditions.Select(c => new ListFilterCondition(c.Field.Trim(), c.Operator, SheetFilterText.TakesValue(c.Operator) ? c.Value?.Trim() : null)).ToList(),
            rule.MatchAny, target, color, rule.Bold, rule.Strikethrough, rule.Enabled);
    }

    private static bool Reads(SheetFormatRuleDefinition rule, IReadOnlySet<Guid> hidden) =>
        hidden.Count > 0 && rule.Fields.Any(f => Guid.TryParse(f, out var id) && hidden.Contains(id));

    private async Task<IReadOnlySet<Guid>> HiddenAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        restricted is null || access is null ? new HashSet<Guid>() : await restricted.HiddenColumnsAsync(sheetId, cancellationToken);

    private async Task<bool> CanEditAsync(Guid sheetId, CancellationToken cancellationToken) =>
        access is null || await access.GetSheetRoleAsync(sheetId, cancellationToken) is { } role && role >= WorkspaceRole.Editor;

    private static async Task<List<(SheetFormatRule Rule, SheetFormatRuleDefinition Definition)>> LoadAsync(
        IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken) =>
        (await db.Set<SheetFormatRule>().Where(r => r.SheetId == sheetId).OrderBy(r => r.Order).ToListAsync(cancellationToken))
            .Select(r => (r, Read(r.DefinitionJson)))
            .Where(r => r.Item2 is not null)
            .Select(r => (r.r, r.Item2!))
            .ToList();

    internal static SheetFormatRuleDefinition? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SheetFormatRuleDefinition>(json, Json) is { Conditions: not null } rule ? rule : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
