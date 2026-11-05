using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Imports;
using Workbench.Domain.Imports;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>
/// Base for the Costpoint file importers: reads each uploaded CSV/.xlsx, checks the required fields are mapped and
/// hands the mapped rows to <see cref="ProcessAsync"/>.
/// </summary>
public abstract class CostpointImporter : IImporter
{
    public virtual ImportSource Source => ImportSource.Costpoint;
    public abstract ImportKind Kind { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ImportField> Fields { get; }
    public abstract string? SampleFile { get; }

    public Task<ImportPreview> PreviewAsync(ImportJob job, IReadOnlyList<ImportJobFile> files, CancellationToken cancellationToken)
    {
        var file = files.Count == 0 ? new TabularFile("", [], []) : Read(files[0]);
        var mapping = FieldMatcher.AutoMap(Fields, file.Headers);
        return Task.FromResult(new ImportPreview(file.Headers, file.Rows.Take(5).Select(r => r.Cells).ToList(), new ImportMappingDto(mapping, [])));
    }

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var rows = new List<MappedRow>();
        foreach (var stored in context.Files)
        {
            var file = Read(stored);
            var missing = Fields.Where(f => f.Required
                && (!context.Mapping.Fields.TryGetValue(f.Name, out var header) || header is null
                    || !file.Headers.Contains(header, StringComparer.OrdinalIgnoreCase))).ToList();
            foreach (var field in missing)
            {
                context.Run.Error(file.FileName, 0, field.Name, $"Map a column of {file.FileName} to {field.Label}.");
            }

            if (missing.Count == 0)
            {
                rows.AddRange(file.Rows.Select(r => new MappedRow(file, r, context.Mapping.Fields, context.Run)));
            }
        }

        context.Run.TotalRows = rows.Count;
        if (rows.Count > 0)
        {
            await ProcessAsync(context, rows, cancellationToken);
        }
    }

    protected abstract Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken);

    protected TabularFile Read(ImportJobFile file) =>
        TabularFile.Read(file.FileName, file.Content, Fields.SelectMany(f => new[] { f.Name, f.Label }.Concat(f.Aliases)));

    protected static T Service<T>(ImportContext context) where T : notnull => context.Services.GetRequiredService<T>();

    protected static string Reason(ImportContext context) => $"Imported from Costpoint (import job {context.Job.Id.ToString()[..8]})";

    /// <summary>Names of the fields whose values differ, for "update" summaries. Empty when nothing changed.</summary>
    protected static List<string> Changed(params (string Name, object? Old, object? New)[] fields) =>
        fields.Where(f => !Equals(Norm(f.Old), Norm(f.New))).Select(f => f.Name).ToList();

    protected static string UpdateSummary(IReadOnlyList<string> changed) => $"Changes {string.Join(", ", changed)}";

    /// <summary>Costpoint's dotted ids: the parent of <c>2001.01.02</c> is <c>2001.01</c>; the top level has none.</summary>
    protected static string? ParentCode(string code)
    {
        var dot = code.LastIndexOf('.');
        return dot <= 0 ? null : code[..dot];
    }

    protected static int Depth(string code) => code.Count(c => c == '.');

    /// <summary>"Last, First" → "First Last".</summary>
    protected static string PersonName(string name)
    {
        var comma = name.IndexOf(',');
        return comma > 0 ? $"{name[(comma + 1)..].Trim()} {name[..comma].Trim()}".Trim() : name.Trim();
    }

    private static object? Norm(object? value) => value switch
    {
        string s => s.Trim(),
        decimal d => decimal.Round(d, 4),
        _ => value,
    };
}
