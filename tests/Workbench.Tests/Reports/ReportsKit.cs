using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Notifications;
using Workbench.Contracts.Reports;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Reports;

/// <summary>
/// <see cref="SeededHost"/> + <see cref="ProjectAccountingFixture"/> with a fixed clock (2026-10-20 15:00 UTC, so the
/// fixture's week of Oct 12 has ended), a recording email sender and the reports hosting (run-as-owner) registered.
/// Hours on 2026-10-14: analyst@ 8 h on 1001.01 (cost rate $87.50), viewer@ 4 h and outsider@ 2 h on 2002.01 (no rates).
/// </summary>
internal sealed class ReportsKit : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    public static readonly ReportParameters Day = new(ProjectAccountingFixture.Day, ProjectAccountingFixture.Day);

    private ReportsKit(SeededHost seeded, ProjectAccountingFixture data, RecordingEmailSender email)
    {
        Seeded = seeded;
        Data = data;
        Email = email;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Data { get; }
    public RecordingEmailSender Email { get; }

    public IStandardReportService Standard => Seeded.Host.Get<IStandardReportService>();
    public IReportWriterService Writer => Seeded.Host.Get<IReportWriterService>();
    public ISavedReportService Saved => Seeded.Host.Get<ISavedReportService>();

    public static async Task<ReportsKit> CreateAsync()
    {
        var email = new RecordingEmailSender();
        var seeded = await SeededHost.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedClock(Now));
            services.AddSingleton<IEmailSender>(email);
            Workbench.Application.Reports.ReportsRegistration.AddWorkbenchReportsHosting(services);
        });
        var data = await ProjectAccountingFixture.SeedAsync(seeded);
        return new ReportsKit(seeded, data, email);
    }

    public ReportsKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal static class ReportResultExtensions
{
    public static int Col(this ReportResultDto r, string key) => r.Columns.Select((c, i) => (c.Key, i)).Single(x => x.Key == key).i;

    public static decimal Num(this ReportRowDto row, ReportResultDto r, string key) =>
        decimal.Parse(row.Values[r.Col(key)] ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    public static string? Text(this ReportRowDto row, ReportResultDto r, string key) => row.Values[r.Col(key)];

    public static ReportRowDto Total(this ReportResultDto r) => r.Rows.Single(x => x.Kind == ReportRowKind.Total);

    public static IEnumerable<ReportRowDto> Details(this ReportResultDto r) => r.Rows.Where(x => x.Kind == ReportRowKind.Detail);
}
