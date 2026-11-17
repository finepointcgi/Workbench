using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Assistant;
using Workbench.Application.Customization;
using Workbench.Application.Migration;
using Workbench.Application.Purchasing;
using Workbench.Application.Reports;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Domain;
using Workbench.Plugins;

namespace Workbench.SampleModule;

public sealed class SampleWidget : Entity
{
    public required string Name { get; set; }
}

/// <summary>A service interface the module owns: the edition gates wrap its changes (anything not starting with a reading verb).</summary>
public interface ISampleWidgetService
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(string name, CancellationToken cancellationToken = default);
}

internal sealed class SampleWidgetService(IWorkbenchDbContextFactory dbFactory) : ISampleWidgetService
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<SampleWidget>().AsNoTracking().OrderBy(w => w.Name).Select(w => w.Name).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<SampleWidget>().Add(new SampleWidget { Name = name });
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class SampleWidgetConfiguration : IEntityTypeConfiguration<SampleWidget>
{
    public void Configure(EntityTypeBuilder<SampleWidget> b)
    {
        b.ToTable("SampleWidgets");
        b.Property(w => w.Name).HasMaxLength(100);
    }
}

/// <summary>The module's own context: the model its migrations are made from. It maps only the module's tables.</summary>
public sealed class SampleModuleDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfigurationsFromAssembly(typeof(SampleModuleDbContext).Assembly);
}

[DbContext(typeof(SampleModuleDbContext))]
[Migration("20261117000000_CreateSampleWidgets")]
internal sealed class CreateSampleWidgets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "SampleWidgets",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                Name = table.Column<string>(maxLength: 100, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_SampleWidgets", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("SampleWidgets");
}

public sealed class SampleModule : IWorkbenchModule
{
    public const string PermissionView = "sample.widgets.view";

    public string Id => ModuleKeys.GovCon;

    public string DisplayName => "Sample module";

    public string Version => "1.2.3";

    public ModuleContribution Describe() => new()
    {
        ModuleId = Id,
        ApiSegments = ["sample-widgets"],
        PermissionPrefixes = ["sample."],
        PermissionAreas = ["Sample module"],
        Permissions = [new PermissionDefinition(PermissionView, "Sample module", "See sample widgets", "The sample module's widgets.", OrgRoles.Finance)],
        NavEntries = [new ModuleNavEntry("finance", "Sample widgets", "Sample widgets", "sample-widgets", "widgets", [PermissionView])],
        ServiceInterfaces = [typeof(ISampleWidgetService)],
        ApiKeyAreas = [new ApiKeyAreaDto("sample", "Sample module", "Sample widgets.", ["sample-widgets"])],
        RoutableAssemblies = [typeof(SampleModule).Assembly],
        Rows = SampleRows.All,
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddScoped<ISampleWidgetService, SampleWidgetService>();

        // Contributions to core engines: each is absent when the module is not loaded.
        services.AddScoped<IHomeWidgetProvider, SampleHomeWidget>();
        services.AddScoped<IAssistantToolContribution, SampleAssistantTool>();
        services.AddScoped<IStandardReportContribution, SampleStandardReport>();
        services.AddScoped<IApprovalDocumentHandler, SampleApprovalHandler>();
        services.AddScoped<IReconciliationSource, SampleReconciliationSource>();
        services.AddSingleton(new MigrationStepContribution("sample-widgets", "Sample widgets", "Load the sample module's widgets.", ["people"], [], After: "people"));
        services.AddScoped<IItemReader, SampleItemReader>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api) =>
        api.MapGet("/sample-widgets", async (ISampleWidgetService widgets, CancellationToken ct) => await widgets.ListAsync(ct));

    public IReadOnlyList<Assembly> ModelAssemblies => [typeof(SampleModule).Assembly];

    public ModuleMigrations? Migrations => new(typeof(SampleModuleDbContext), "Workbench.SampleModule", "Workbench.SampleModule");
}
