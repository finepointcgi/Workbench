namespace Workbench.Application.Reports;

/// <summary>
/// A page a license module lists under Reports (the module registers one as a singleton, in <c>ConfigureServices</c>). It opens
/// <see cref="PageLink"/> instead of running a report, and is available to people who hold <see cref="Permission"/>.
/// </summary>
public sealed record StandardReportPage(string Key, string Name, string Description, string Category, string PageLink, string Permission, string DeniedMessage);
