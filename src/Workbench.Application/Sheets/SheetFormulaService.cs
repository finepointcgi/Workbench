using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Formulas;
using Workbench.Domain.Sheets;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Sheets;

/// <summary>Cross-sheet references, checking formulas before saving, and the function list.</summary>
public sealed class SheetFormulaService(
    IWorkbenchDbContextFactory dbFactory, AccessControl access, RestrictedColumnAccess restricted, SheetCalculator calculator, TimeProvider clock)
    : ISheetFormulaService
{
    public async Task<IReadOnlyList<SheetReferenceDto>> ListReferencesAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var references = await db.Set<SheetReference>().AsNoTracking().Where(r => r.SheetId == sheetId).ToListAsync(cancellationToken);
        var result = new List<SheetReferenceDto>();
        foreach (var reference in references.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(await ToDtoAsync(db, reference, cancellationToken));
        }

        return result;
    }

    public async Task<SheetReferenceDto> CreateReferenceAsync(Guid sheetId, CreateSheetReferenceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200 || name.Contains('{') || name.Contains('}'))
        {
            throw new ArgumentException("Give the reference a name (up to 200 characters, without braces).");
        }

        if (request.SourceSheetId == sheetId)
        {
            throw new ArgumentException("A cross-sheet reference points at another sheet. On this sheet, use a column range like [Status]:[Status].");
        }

        if (request.Fields is not { Count: > 0 })
        {
            throw new ArgumentException("Pick at least one column of the other sheet.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var source = await calculator.ReadVisibleAsync(db, request.SourceSheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", request.SourceSheetId);
        var fields = request.Fields.Select(f => f.Trim().ToLowerInvariant()).Distinct().ToList();
        if (fields.FirstOrDefault(f => !source.HasField(f)) is { } unknown)
        {
            throw new ArgumentException($"\"{source.Name}\" has no column {unknown}.");
        }

        var names = await db.Set<SheetReference>().Where(r => r.SheetId == sheetId).Select(r => r.Name).ToListAsync(cancellationToken);
        if (names.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"This sheet already has a reference named {{{name}}}.");
        }

        var reference = new SheetReference
        {
            SheetId = sheetId,
            Name = name,
            SourceSheetId = request.SourceSheetId,
            Fields = fields,
            CreatedBy = access.Email,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<SheetReference>().Add(reference);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, reference, cancellationToken);
    }

    public async Task DeleteReferenceAsync(Guid sheetId, Guid referenceId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reference = await db.Set<SheetReference>().FirstOrDefaultAsync(r => r.Id == referenceId && r.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Reference", referenceId);
        db.Set<SheetReference>().Remove(reference);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<FormulaCheckDto> CheckAsync(Guid sheetId, string formula, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        if (string.IsNullOrWhiteSpace(formula))
        {
            return new FormulaCheckDto("Type a formula, starting with = if you like.", null, null, null);
        }

        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var columns = (await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken))
            .Where(c => !hidden.Contains(c.Id)).ToList();
        var references = await db.Set<SheetReference>().AsNoTracking().Where(r => r.SheetId == sheetId).Select(r => r.Name).ToListAsync(cancellationToken);
        if (Formula.Validate(formula.Trim(), columns.Select(c => c.Name), references) is { } problem)
        {
            return new FormulaCheckDto(problem, null, null, null);
        }

        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return new FormulaCheckDto(null, null, null, null);
        }

        foreach (var item in items.Where(_ => hidden.Count > 0))
        {
            item.Values = item.Values.Where(v => !(Guid.TryParse(v.Key, out var id) && hidden.Contains(id))).ToDictionary(v => v.Key, v => v.Value);
        }

        // Work it out as a column added at the end, without saving anything.
        var trial = new Column { SheetId = sheetId, Name = $"__check_{Guid.NewGuid():N}", Type = ColumnType.Text, Order = int.MaxValue, Formula = formula.Trim() };
        var computed = await calculator.ComputeAsync(db, sheetId, [.. columns, trial], items, cancellationToken);
        var first = computed.OrderBy(i => i.ParentId.HasValue).ThenBy(i => i.Order).First();
        var value = first.Values.GetValueOrDefault(trial.Id.ToString());
        return new FormulaCheckDto(null, first.Title, value, FormulaErrors.IsError(value) ? FormulaErrors.Explain(value) : null);
    }

    public Task<IReadOnlyList<FormulaFunctionDto>> ListFunctionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FormulaFunctionDto>>(
            FormulaFunctions.Catalog.Select(f => new FormulaFunctionDto(f.Name, f.Category, f.Syntax, f.Description)).ToList());

    private async Task<SheetReferenceDto> ToDtoAsync(IWorkbenchDbContext db, SheetReference reference, CancellationToken cancellationToken)
    {
        var source = await calculator.ReadVisibleAsync(db, reference.SourceSheetId, cancellationToken);
        return source is null
            ? new SheetReferenceDto(reference.Id, reference.Name, reference.SourceSheetId, null, reference.Fields, [], false)
            : new SheetReferenceDto(reference.Id, reference.Name, reference.SourceSheetId, source.Name, reference.Fields,
                reference.Fields.Select(f => source.NameOf(f) ?? "(deleted column)").ToList(), true);
    }
}
