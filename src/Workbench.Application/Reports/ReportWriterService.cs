using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Reports;

namespace Workbench.Application.Reports;

/// <summary>Runs report-writer definitions over the curated <see cref="ReportSource"/>s with the caller's permissions.</summary>
public sealed class ReportWriterService(IWorkbenchDbContextFactory dbFactory, ReportAccess access, TimekeepingClock clock) : IReportWriterService
{
    public async Task<IReadOnlyList<ReportDataSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        return ReportSource.All
            .Where(s => s.Unavailable(context) is null)
            .Select(s => new ReportDataSourceDto(s.Key, s.Name, s.Description, s.DateField,
                s.Fields.Where(f => s.CanSee(f, context)).Select(f => new ReportFieldDto(f.Key, f.Label, f.Type, f.Restriction != FieldRestriction.None, f.Description)).ToList()))
            .ToList();
    }

    public async Task<ReportResultDto> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var source = ReportSource.Find(definition.Source ?? "") ?? throw new ArgumentException($"Unknown data source '{definition.Source}'.");
        if (definition.Columns is null || definition.Columns.Count == 0)
        {
            throw new ArgumentException("Choose at least one column.");
        }

        var context = await access.GetAsync(cancellationToken);
        if (source.Unavailable(context) is { } reason)
        {
            throw new WorkbenchForbiddenException(reason);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = clock.Today;
        var (from, to) = source.DateField is null
            ? (null, null)
            : await ReportDates.ResolveAsync(db, definition.Range, definition.From, definition.To, today, cancellationToken);
        var rows = await source.LoadAsync(db, context, from, to, today, cancellationToken);
        var visible = source.Fields.Where(f => source.CanSee(f, context)).ToList();
        var subtitle = source.DateField is null ? source.Name : $"{source.Name} · {ReportDates.Describe(from, to)}";
        return ReportEngine.Run(source, visible, rows, definition, subtitle, clock.UtcNow);
    }

    public async Task<ReportFileDto> ExportAsync(ReportDefinition definition, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        ReportFiles.Render(await RunAsync(definition, cancellationToken), format);
}
