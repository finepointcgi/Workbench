namespace Workbench.Application.Reports;

/// <summary>
/// A page a license module lists under Reports (the module registers one as a singleton, in <c>ConfigureServices</c>). It opens
/// <see cref="PageLink"/> instead of running a report, and is available to people who hold <see cref="Permission"/>.
/// </summary>
public sealed record StandardReportPage(string Key, string Name, string Description, string Category, string PageLink, string Permission, string DeniedMessage);

/// <summary>
/// A standard report a license module runs (a data report, not just a page: <see cref="StandardReportPage"/> is for pages). Register it in
/// the module's <c>ConfigureServices</c>; it is listed, authorized, run, drilled into and exported like a built-in standard report (company
/// words, report files), and with the module absent it is not listed. Fill <paramref name="builder"/> from the module's permission-checked
/// services; <see cref="Unavailable"/> is asked before anything runs, and <paramref name="access"/> carries the viewer's visibility.
/// </summary>
public interface IStandardReportContribution
{
    string Key { get; }

    string Name { get; }

    string Description { get; }

    string Category { get; }

    IReadOnlyList<Contracts.Reports.ReportParameterKind> Parameters => [];

    string? PageLink => null;

    /// <summary>Why this viewer can't run the report, or null when they can.</summary>
    string? Unavailable(ReportAccessContext access);

    Task RunAsync(ReportBuilder builder, ReportAccessContext access, Contracts.Reports.ReportParameters parameters, CancellationToken cancellationToken);

    /// <summary>The detail behind a row: <paramref name="drillKey"/> is the key the module put on that row. The default has no detail.</summary>
    Task DrillAsync(ReportBuilder builder, ReportAccessContext access, Contracts.Reports.ReportParameters parameters, string drillKey, CancellationToken cancellationToken) =>
        throw new ArgumentException("This report has no detail to show.");
}
