using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Assistant;
using Workbench.Application.Automations;
using Workbench.Application.Customization;
using Workbench.Application.Migration;
using Workbench.Application.Purchasing;
using Workbench.Application.Reports;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Modules;
using Workbench.Contracts.Reports;
using Workbench.Domain.Assistant;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;

namespace Workbench.SampleModule;

// One example of every way a module adds to a core catalog or core engine. Each is what a moved module writes for the same seam.

/// <summary>Catalog rows (ModuleContribution.Rows): the rows core catalogs read beside their own.</summary>
internal static class SampleRows
{
    public const string Key = "sample-widget";

    public static IReadOnlyList<IModuleRow> All { get; } =
    [
        new ModuleCustomList("sample-widgets"),
        new ModuleApprovalDocumentType(Key, "Sample widget approval"),
        new ModuleSecurityEventType("sample.widget_added", "A sample widget was added."),
        new ModuleHomeWidget("sample.widget-count", "Sample widgets", "How many sample widgets there are."),
        new WebhookEventInfo("sample.widget_added", "Sample widget added", "Sample module", "A sample widget was added."),
        new BuiltInForms.FormDefinition(Key, "Sample widget", "Sample widgets › Details",
            [new("details", "", [new("name", "Name", BuiltInForms.Text, Required: true)])], [typeof(SampleSaveRequest)]),
        DocumentNumberType.For<SampleWidget>(Key, "Sample widgets", "SW-{seq:4}", w => w.Name),
        new AutomationEntity
        {
            Key = Key, Label = "Sample widget", ClrType = typeof(SampleWidget), LinkFormat = "sample-widgets/{id}",
            CanWrite = r => r.HasFlag(OrgRoles.Finance), CanReadAll = r => r.HasFlag(OrgRoles.Finance),
        },
        new NewAction(Key, "Sample widget", "New sample widget", "widgets", NavRule.Everyone, "sample-widgets"),
        new SampleReportSource(),
        new SampleListSource(),
    ];
}

public sealed record SampleSaveRequest(string Name);

internal sealed class SampleReportSource : ReportSource
{
    public override string Key => "sample-widgets";

    public override string Name => "Sample widgets";

    public override string Description => "The sample module's widgets.";

    public override IReadOnlyList<SourceField> Fields { get; } = [new("name", "Name", ReportColumnType.Text)];

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct) =>
        Rows(await db.Set<SampleWidget>().AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct), w => [w.Name]);
}

internal sealed class SampleListSource : IListSource
{
    public string ListKey => "sample-widgets";

    public string Title => "Sample widgets";

    public Task<ChartData> ChartAsync(IServiceProvider services, ListViewDefinition view, ChartKind kind, CancellationToken ct = default) =>
        Task.FromResult(new ChartData(kind, ["Widgets"], [1], Title));
}

/// <summary>Dashboard and home widget: computed with the viewer's access.</summary>
internal sealed class SampleHomeWidget(ISampleWidgetService widgets) : IHomeWidgetProvider
{
    public string Key => "sample.widget-count";

    public async Task<WidgetDataDto> ComputeAsync(WidgetDto widget, WidgetDataDto seed, CancellationToken ct) =>
        seed with { Markdown = $"{(await widgets.ListAsync(ct)).Count} sample widget(s)" };
}

/// <summary>An assistant tool: read-only, strict schema, runs as the signed-in user.</summary>
internal sealed class SampleAssistantTool : IAssistantToolContribution
{
    public AssistantToolSpec Spec { get; } = AssistantToolbox.Spec("list_sample_widgets", "Lists the sample module's widgets.", "Reading sample widgets");

    public string? McpArea => "sample";

    public async Task<AssistantToolResult> RunAsync(JsonElement input, IServiceProvider services, CancellationToken ct)
    {
        var names = await ((ISampleWidgetService)services.GetService(typeof(ISampleWidgetService))!).ListAsync(ct);
        return new AssistantToolResult(AssistantToolOutcome.Ok, new { widgets = names }, $"{names.Count} sample widget(s)", [], []);
    }
}

/// <summary>A standard report that runs data (not just a page).</summary>
internal sealed class SampleStandardReport : IStandardReportContribution
{
    public string Key => "sample-widget-list";

    public string Name => "Sample widget list";

    public string Description => "Every sample widget.";

    public string Category => "Sample module";

    public string? Unavailable(ReportAccessContext access) => access.SeesLaborCost ? null : "The sample report is for Finance.";

    public async Task RunAsync(ReportBuilder builder, ReportAccessContext access, ReportParameters parameters, CancellationToken ct)
    {
        builder.Column("name", "Name");
        foreach (var name in await new SampleWidgetCatalog().NamesAsync(ct))
        {
            builder.Row(name);
        }
    }
}

internal sealed class SampleWidgetCatalog
{
    public Task<IReadOnlyList<string>> NamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["Gadget"]);
}

/// <summary>The inbox approves and rejects the module's documents through its own service.</summary>
internal sealed class SampleApprovalHandler : IApprovalDocumentHandler
{
    public string DocumentType => SampleRows.Key;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The module's side of a parallel-run reconciliation report: core asks it, never the module's services.</summary>
internal sealed class SampleReconciliationSource : IReconciliationSource
{
    public ReconReportKind Kind => ReconReportKind.RevenueByProject;

    public Task<IReadOnlyList<WorkbenchFigure>> LoadAsync(ReconScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkbenchFigure>>([new(["SAMPLE"], "Sample", new Dictionary<string, decimal> { ["Revenue"] = 42m })]);

    public Task<ReconDetail> DetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken cancellationToken) =>
        Task.FromResult(new ReconDetail(["Item"], [["sample"]]));
}

/// <summary>How core reads items once the module owns them (the last registration wins).</summary>
internal sealed class SampleItemReader : IItemReader
{
    public static readonly Guid ItemId = new("5a5a5a5a-0000-4000-8000-000000000001");

    public Task<IReadOnlyDictionary<Guid, ItemRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ItemRef>>(ids.Contains(ItemId) ? new Dictionary<Guid, ItemRef> { [ItemId] = new(ItemId, "SAMPLE-1", "A sample part", "EA") } : new Dictionary<Guid, ItemRef>());
}
