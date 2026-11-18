using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Assistant;
using Workbench.Application.Automations;
using Workbench.Application.Customization;
using Workbench.Application.Migration;
using Workbench.Application.Purchasing;
using Workbench.Application.Reports;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Modules;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Security;
using Workbench.Domain.Licensing;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;
using Workbench.Web;

namespace Workbench.Tests.Modules;

/// <summary>
/// The shared seams a module uses to add to core catalogs and core engines (docs/operations/module-plugins.md): catalog rows, home widgets,
/// assistant tools, data reports, approval documents, reconciliation sources, migration steps and the core readers. The sample module (a real
/// assembly, loaded through the real loader) contributes one of each; licensed they are there, unlicensed none of them is.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class ModuleSeamTests : IDisposable
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly string SampleDll = Path.Combine(AppContext.BaseDirectory, "modules-sample", "Workbench.SampleModule.dll");
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        ModuleHost.Use(ModuleHost.Empty);
        foreach (var folder in _folders)
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private string Modules()
    {
        var root = Path.Combine(Path.GetTempPath(), $"workbench-modules-{Guid.NewGuid():N}");
        var folder = Directory.CreateDirectory(Path.Combine(root, "sample")).FullName;
        _folders.Add(root);
        File.WriteAllText(Path.Combine(folder, "module.json"), """{ "id": "govcon", "assembly": "Workbench.SampleModule.dll" }""");
        File.Copy(SampleDll, Path.Combine(folder, "Workbench.SampleModule.dll"));
        return root;
    }

    private static ModuleHostOptions Options(string folder, params string[] licensed) =>
        new(folder, licensed.Contains, Required: null, NavCatalog.BuiltInSectionKeys, NavCatalog.BuiltInHrefs, BuiltInRowKeys.All);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(params string[] modules)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme","issuedAt":"2026-10-01","expires":"2099-11-01","seats":25,"lightSeats":5,"modules":[{{string.Join(",", modules.Select(m => $"\"{m}\""))}}]}""");
        return $"WB2.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private WorkbenchWebFactory Host(string key) =>
        new("Development", testAuth: true, seededTemplate: true, settings: new Dictionary<string, string>
        {
            ["Modules:Folder"] = Modules(),
            ["License:EditionOverride"] = "",
            ["License:PublicKeyPem"] = PublicKey,
            ["Modules:LicenseKey"] = key,
        });

    // ───────────────────────────────────── Catalog rows ─────────────────────────────────────

    [Fact]
    public void Licensed_the_module_adds_a_row_to_every_catalog_and_unlicensed_none()
    {
        var before = (Lists: CustomizableLists.All.Count, Approvals: ApprovalDocumentTypes.All.Count, Security: SecurityEventTypes.All.Count, Events: WebhookEventCatalog.All.Count,
            Forms: BuiltInForms.All.Count, Numbers: DocumentNumberTypes.All.Count, Entities: AutomationEntities.All.Count, New: NewCatalog.Actions.Count,
            Sources: ReportSource.All.Count, ListSources: ListSources.All.Count);

        var unlicensed = ModuleHost.Load(Options(Modules(), ModuleIds.Billing));
        Assert.Equal(ModuleLoadStatus.NotLicensed, Assert.Single(unlicensed.Results).Status);
        Assert.DoesNotContain("sample-widgets", CustomizableLists.All);
        Assert.Null(BuiltInForms.Find("sample-widget"));
        Assert.Null(DocumentNumberTypes.Find("sample-widget"));
        Assert.Null(AutomationEntities.Find("sample-widget"));
        Assert.Null(NewCatalog.Find("sample-widget"));
        Assert.Null(ReportSource.Find("sample-widgets"));
        Assert.Null(ListSources.Find("sample-widgets"));
        Assert.False(WebhookEventCatalog.IsKnown("sample.widget_added"));
        Assert.DoesNotContain("sample-widget", ApprovalDocumentTypes.All);
        Assert.DoesNotContain("sample.widget_added", SecurityEventTypes.All);
        Assert.Equal(before.Forms, BuiltInForms.All.Count);

        var host = ModuleHost.Load(Options(Modules(), ModuleIds.GovCon));
        Assert.Equal(ModuleLoadStatus.Loaded, Assert.Single(host.Results).Status);

        Assert.Equal(before.Lists + 1, CustomizableLists.All.Count);
        Assert.Contains("sample-widgets", CustomizableLists.All);
        Assert.Equal(before.Approvals + 1, ApprovalDocumentTypes.All.Count);
        Assert.Equal("Sample widget approval", ApprovalDocumentTypes.Label("sample-widget"));
        Assert.NotEmpty(ApprovalDefaults.For("sample-widget").Steps); // A starting workflow, so the document can be routed.
        Assert.Equal(before.Security + 1, SecurityEventTypes.All.Count);
        Assert.Contains("sample.widget_added", SecurityEventTypes.All);
        Assert.Equal(before.Events + 1, WebhookEventCatalog.All.Count);
        Assert.Equal(["sample.widget_added"], WebhookEventCatalog.Normalize(["sample.widget_added"]));
        Assert.Equal(before.Forms + 1, BuiltInForms.All.Count);
        Assert.Equal("Sample widget", BuiltInForms.Get("sample-widget").Name);
        Assert.Equal(before.Numbers + 1, DocumentNumberTypes.All.Count);
        Assert.Equal("SW-{seq:4}", DocumentNumberTypes.Get("sample-widget").DefaultPattern);
        Assert.Equal(before.Entities + 1, AutomationEntities.All.Count);
        Assert.Equal("Sample widget", AutomationEntities.Find("sample-widget")!.Label);
        Assert.Equal(before.New + 1, NewCatalog.Actions.Count);
        Assert.Equal(before.Sources + 1, ReportSource.All.Count);
        Assert.Equal("Sample widgets", ReportSource.Find("sample-widgets")!.Name);
        Assert.Equal(before.ListSources + 1, ListSources.All.Count);

        // Built-in rows are untouched and keep their order; the module's come after.
        Assert.Equal(BuiltInForms.BuiltIn, BuiltInForms.All.Take(BuiltInForms.BuiltIn.Count));
    }

    [Fact]
    public void A_row_that_exists_already_or_has_no_key_is_refused()
    {
        var dup = new ModuleContribution { ModuleId = ModuleIds.GovCon, Rows = [new ModuleCustomList(CustomizableLists.Projects), new ModuleApprovalDocumentType(ApprovalDocumentTypes.Invoice, "Mine")] };
        var problems = ModuleSet.Empty.Validate(dup, builtInRowKeys: BuiltInRowKeys.All);
        Assert.Contains(problems, p => p.Contains("custom-list") && p.Contains("projects"));
        Assert.Contains(problems, p => p.Contains("approval-type") && p.Contains("invoice"));

        var blank = new ModuleContribution { ModuleId = ModuleIds.GovCon, Rows = [new ModuleCustomList(" ")] };
        Assert.Contains(ModuleSet.Empty.Validate(blank, builtInRowKeys: BuiltInRowKeys.All), p => p.Contains("no key"));

        var fresh = new ModuleContribution { ModuleId = ModuleIds.GovCon, Rows = [new ModuleCustomList("mine")] };
        Assert.Empty(ModuleSet.Empty.Validate(fresh, builtInRowKeys: BuiltInRowKeys.All));
        Assert.Contains(ModuleSet.Empty.With(fresh).Validate(fresh with { ModuleId = ModuleIds.Capture }, builtInRowKeys: BuiltInRowKeys.All), p => p.Contains("mine")); // Another module's row.
    }

    [Fact]
    public void The_built_in_row_inventory_matches_the_catalogs_it_guards()
    {
        Assert.Equal(BuiltInRowKeys.All.Count, BuiltInRowKeys.All.Distinct().Count());
        Assert.Contains("form/project", BuiltInRowKeys.All);
        Assert.Contains("document-number/invoice", BuiltInRowKeys.All);
        Assert.Contains("automation-entity/contract", BuiltInRowKeys.All);
        Assert.Contains("integration-event/invoice.paid", BuiltInRowKeys.All);
        Assert.Contains("security-event/signin.success", BuiltInRowKeys.All);
        Assert.Contains("new-action/time", BuiltInRowKeys.All);
        Assert.Contains(ModuleSet.RowKey(ReportSource.BuiltIn[0]), BuiltInRowKeys.All);
    }

    // ───────────────────────────────────── Engines, on the real host ─────────────────────────────────────

    [Fact]
    public async Task Licensed_the_module_adds_a_widget_a_tool_a_report_an_approval_handler_a_reconciliation_source_a_step_and_the_item_reader()
    {
        await using var factory = Host(Issue("govcon"));
        using var finance = factory.CreateClientAs("pm@example.com");
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Home widget: listed in the picker, computed by the module's provider.
        Assert.Contains(ModuleRegistry.Current.RowsOf<ModuleHomeWidget>(), w => w.Key == "sample.widget-count");
        var widget = new WidgetDto(Guid.NewGuid(), Guid.NewGuid(), WidgetType.Module, "Sample", 0, 6, WidgetHeight.Medium, new WidgetConfig(ModuleKey: "sample.widget-count"));
        var data = await sp.GetRequiredService<HomeWidgetData>().ComputeAsync(widget, default);
        Assert.Null(data.Error);
        Assert.Equal("0 sample widget(s)", data.Markdown);

        // Assistant tool: in the toolbox and over MCP, under its API key area, and it runs.
        var toolbox = sp.GetRequiredService<AssistantToolbox>();
        Assert.Contains(toolbox.AllSpecs, s => s.Name == "list_sample_widgets");
        Assert.DoesNotContain(AssistantToolbox.Specs, s => s.Name == "list_sample_widgets"); // The built-in set is unchanged.
        var run = await toolbox.RunAsync("list_sample_widgets", "{}", default);
        Assert.Equal(Workbench.Domain.Assistant.AssistantToolOutcome.Ok, run.Outcome);
        Assert.Equal("0 sample widget(s)", run.Summary);
        Assert.Equal(Workbench.Domain.Assistant.AssistantToolOutcome.Invalid, (await toolbox.RunAsync("list_sample_widgets", """{"extra":1}""", default)).Outcome); // Strict schema, as for built-in tools.
        Assert.Contains(sp.GetRequiredService<Contracts.Assistant.IAssistantToolHost>().Tools, t => t.Name == "list_sample_widgets" && t.Area == "sample");

        // Data report: listed, authorized by the module's own rule, run through the standard report engine.
        var listed = await (await finance.GetAsync("/api/v1/reports/standard")).Content.ReadFromJsonAsync<List<StandardReportDto>>(Json);
        var report = Assert.Single(listed!, r => r.Key == "sample-widget-list");
        Assert.True(report.Available);
        var result = await (await finance.PostAsJsonAsync("/api/v1/reports/standard/sample-widget-list/run", new ReportParameters())).Content.ReadFromJsonAsync<ReportResultDto>(Json);
        Assert.Equal("Sample widget list", result!.Title);
        Assert.Equal("Gadget", Assert.Single(result.Rows).Values[0]);

        // Approval documents: the inbox finds the module's handler.
        Assert.Contains(sp.GetServices<IApprovalDocumentHandler>(), h => h.DocumentType == "sample-widget");

        // Reconciliation: the module answers for its report, core never calls its services.
        var recon = new ReconciliationSources(sp, sp.GetRequiredService<Workbench.Application.IWorkbenchDbContextFactory>());
        var figures = await recon.LoadAsync(ReconReportKind.RevenueByProject, null!, default);
        Assert.Equal(42m, Assert.Single(figures).Values["Revenue"]);

        // Migration checklist: the step is there, after the one it names.
        var checklist = await (await finance.GetAsync("/api/v1/migration/checklist")).Content.ReadFromJsonAsync<MigrationChecklistDto>(Json);
        var keys = checklist!.Steps.Select(s => s.Key).ToList();
        Assert.Equal(keys.IndexOf("people") + 1, keys.IndexOf("sample-widgets"));

        // Core readers: the module's reader replaces the built-in one.
        var item = (await sp.GetRequiredService<IItemReader>().ReadAsync(null!, [new Guid("5a5a5a5a-0000-4000-8000-000000000001")], default)).Values.Single();
        Assert.Equal("SAMPLE-1", item.Number);
    }

    [Fact]
    public async Task Unlicensed_none_of_those_exist_and_core_keeps_its_own_answers()
    {
        await using var factory = Host(Issue("billing"));
        using var finance = factory.CreateClientAs("pm@example.com");
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        Assert.Empty(ModuleRegistry.Current.RowsOf<ModuleHomeWidget>());
        var widget = new WidgetDto(Guid.NewGuid(), Guid.NewGuid(), WidgetType.Module, "Sample", 0, 6, WidgetHeight.Medium, new WidgetConfig(ModuleKey: "sample.widget-count"));
        Assert.Contains("isn't installed", (await sp.GetRequiredService<HomeWidgetData>().ComputeAsync(widget, default)).Error);

        var toolbox = sp.GetRequiredService<AssistantToolbox>();
        Assert.Equal(AssistantToolbox.Specs.Count, toolbox.AllSpecs.Count);
        Assert.Equal(Workbench.Domain.Assistant.AssistantToolOutcome.Invalid, (await toolbox.RunAsync("list_sample_widgets", "{}", default)).Outcome);
        Assert.DoesNotContain(sp.GetRequiredService<Contracts.Assistant.IAssistantToolHost>().Tools, t => t.Name == "list_sample_widgets");

        var listed = await (await finance.GetAsync("/api/v1/reports/standard")).Content.ReadFromJsonAsync<List<StandardReportDto>>(Json);
        Assert.DoesNotContain(listed!, r => r.Key == "sample-widget-list");
        Assert.NotEqual(HttpStatusCode.OK, (await finance.PostAsJsonAsync("/api/v1/reports/standard/sample-widget-list/run", new ReportParameters())).StatusCode);

        Assert.DoesNotContain(sp.GetServices<IApprovalDocumentHandler>(), h => h.DocumentType == "sample-widget");
        Assert.Empty(sp.GetServices<IReconciliationSource>());
        var checklist = await (await finance.GetAsync("/api/v1/migration/checklist")).Content.ReadFromJsonAsync<MigrationChecklistDto>(Json);
        Assert.DoesNotContain(checklist!.Steps, s => s.Key == "sample-widgets");

        Assert.IsType<PartItemReader>(sp.GetRequiredService<IItemReader>());
        Assert.IsType<VendorTableReader>(sp.GetRequiredService<IVendorReader>());
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
