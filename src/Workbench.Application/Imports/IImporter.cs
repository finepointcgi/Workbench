using Workbench.Contracts.Imports;
using Workbench.Domain.Imports;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports;

/// <summary>Everything an importer gets for one pass: the job, its parsed files, mapping and options.</summary>
public sealed record ImportContext(
    ImportJob Job,
    IReadOnlyList<ImportJobFile> Files,
    ImportMappingDto Mapping,
    ImportOptionsDto Options,
    ImportRun Run,
    IServiceProvider Services,
    string? ApiToken = null);

/// <summary>
/// One import kind. <see cref="RunAsync"/> is called for validation, the dry run and the import; it reports every
/// row's outcome through <see cref="ImportContext.Run"/>.
/// </summary>
public interface IImporter
{
    ImportSource Source { get; }
    ImportKind Kind { get; }
    string Title { get; }
    string Description { get; }
    IReadOnlyList<ImportField> Fields { get; }

    /// <summary>File under <c>samples/import</c> that shows the expected columns.</summary>
    string? SampleFile { get; }

    /// <summary>Headers and sample rows for the mapping screen, plus the suggested mapping.</summary>
    Task<ImportPreview> PreviewAsync(ImportJob job, IReadOnlyList<ImportJobFile> files, CancellationToken cancellationToken);

    Task RunAsync(ImportContext context, CancellationToken cancellationToken);
}

public sealed record ImportPreview(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    ImportMappingDto SuggestedMapping);
