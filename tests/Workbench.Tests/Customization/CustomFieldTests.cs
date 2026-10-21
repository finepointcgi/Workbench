using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Customization;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Reports;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using E = Workbench.Contracts.Customization.CustomizableEntityTypes;

namespace Workbench.Tests.Customization;

public sealed class CustomFieldTests
{
    private static async Task<(SeededHost Host, Guid ProjectId)> HostWithProjectAsync(Action<IServiceCollection>? configure = null)
    {
        var host = await SeededHost.CreateAsync(configure);
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var project = new Project { Code = "7001", Name = "Data Platform", Status = ProjectStatus.Active };
        db.Set<Project>().Add(project);
        await db.SaveChangesAsync();
        host.As(SeededHost.Admin);
        return (host, project.Id);
    }

    private static ICustomFieldService Fields(SeededHost host) => host.Host.Get<ICustomFieldService>();

    private static async Task<CustomFieldDefinitionDto> AddAsync(SeededHost host, string label, CustomFieldType type, string entity = E.Project, bool required = false,
        string? list = null, bool sensitive = false, string? link = null, string? defaultValue = null) =>
        await Fields(host).SaveDefinitionAsync(null, new SaveCustomFieldRequest(entity, label, type, IsRequired: required, ValueListKey: list, IsSensitive: sensitive,
            LinkEntityType: link, DefaultValue: defaultValue));

    private static SaveCustomFieldValuesRequest Values(params (string Key, string? Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public async Task Every_type_parses_and_rejects_by_its_own_rules()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        await host.Host.Get<ICustomListService>().SaveAsync(new SaveValueListRequest("phases", "Phases", null,
            [new("disc", "Discovery"), new("build", "Build"), new("old", "Retired phase", IsActive: false)]));
        await AddAsync(host, "Notes", CustomFieldType.Text);
        await AddAsync(host, "Narrative", CustomFieldType.LongText);
        await AddAsync(host, "Head count", CustomFieldType.Number);
        await AddAsync(host, "Award fee pool", CustomFieldType.Currency);
        await AddAsync(host, "Kickoff", CustomFieldType.Date);
        await AddAsync(host, "Classified", CustomFieldType.YesNo);
        await AddAsync(host, "Phase", CustomFieldType.Picklist, list: "phases");
        await AddAsync(host, "Phases touched", CustomFieldType.MultiPicklist, list: "phases");
        await AddAsync(host, "Customer lead", CustomFieldType.Person);
        await AddAsync(host, "Related project", CustomFieldType.RecordLink, link: E.Project);
        await AddAsync(host, "Portal", CustomFieldType.Url);

        var saved = await Fields(host).SaveValuesAsync(E.Project, projectId, Values(
            ("notes", "  Weekly sync  "), ("narrative", "Line one\nLine two"), ("head_count", "12.5"), ("award_fee_pool", "$1,234.567"),
            ("kickoff", "2026-11-02"), ("classified", "yes"), ("phase", "Build"), ("phases_touched", "build, disc, build"),
            ("customer_lead", "Admin@Example.com"), ("related_project", projectId.ToString()), ("portal", "https://portal.example.gov/x")));
        string? V(string key) => saved.Fields.Single(f => f.Key == key).Value;
        Assert.Equal("Weekly sync", V("notes"));
        Assert.Equal("12.5", V("head_count"));
        Assert.Equal("1234.57", V("award_fee_pool"));
        Assert.Equal("$1,234.57", saved.Fields.Single(f => f.Key == "award_fee_pool").Display);
        Assert.Equal("2026-11-02", V("kickoff"));
        Assert.Equal("true", V("classified"));
        Assert.Equal("build", V("phase"));
        Assert.Equal("Build", saved.Fields.Single(f => f.Key == "phase").Display);
        Assert.Equal("disc, build", V("phases_touched")); // de-duplicated, in list order
        Assert.Equal("admin@example.com", V("customer_lead"));
        Assert.Equal("Admin", saved.Fields.Single(f => f.Key == "customer_lead").Display);
        Assert.Equal("7001 · Data Platform", saved.Fields.Single(f => f.Key == "related_project").Display);

        async Task RejectsAsync(string key, string value, string why)
        {
            var errors = await Fields(host).ValidateAsync(E.Project, projectId, Values((key, value)));
            Assert.Contains(errors, e => e.Contains(why, StringComparison.OrdinalIgnoreCase));
            await Assert.ThrowsAsync<ArgumentException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values((key, value))));
        }

        await RejectsAsync("notes", "two\nlines", "single line");
        await RejectsAsync("notes", new string('x', 501), "at most 500");
        await RejectsAsync("head_count", "twelve", "isn't a number");
        await RejectsAsync("kickoff", "next tuesday", "isn't a date");
        await RejectsAsync("classified", "maybe", "yes or no");
        await RejectsAsync("phase", "Launch", "isn't one of the choices");
        await RejectsAsync("phase", "old", "no longer offered");
        await RejectsAsync("phases_touched", "build, nope", "isn't one of the choices");
        await RejectsAsync("customer_lead", "nobody@example.com", "isn't an active Workbench user");
        await RejectsAsync("customer_lead", "not an email", "isn't a person");
        await RejectsAsync("related_project", Guid.NewGuid().ToString(), "doesn't exist");
        await RejectsAsync("portal", "javascript:alert(1)", "web address");
        await RejectsAsync("missing_field", "x", "no custom field");

        // Blank clears.
        var cleared = await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("notes", ""), ("phase", null)));
        Assert.Null(cleared.Fields.Single(f => f.Key == "notes").Value);
        Assert.Null(cleared.Fields.Single(f => f.Key == "phase").Value);
    }

    [Fact]
    public async Task Required_fields_block_the_save_and_defaults_fill_new_records()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        await AddAsync(host, "Program office", CustomFieldType.Text, required: true);
        await AddAsync(host, "Billable", CustomFieldType.YesNo, defaultValue: "yes");

        var errors = await Fields(host).ValidateAsync(E.Project, projectId, Values(("billable", "no")));
        Assert.Contains("Program office is required.", errors);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("billable", "no"))));
        Assert.Contains("Program office is required.", ex.Message);
        Assert.Empty((await Fields(host).GetValuesAsync(E.Project, projectId)).Fields.Where(f => f.Value is not null));

        var fresh = await Fields(host).GetValuesAsync(E.Project, null);
        Assert.Equal("true", fresh.Fields.Single(f => f.Key == "billable").Value);
        Assert.Contains("Program office is required.", await Fields(host).ValidateAsync(E.Project, null, Values()));
        Assert.Empty(await Fields(host).ValidateAsync(E.Project, null, Values(("program_office", "PEO EIS"))));

        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("program_office", "PEO EIS")));
        await Assert.ThrowsAsync<ArgumentException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("program_office", " "))));
    }

    [Fact]
    public async Task Values_are_audited_searchable_filterable_exported_and_copied()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        Guid otherId;
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var other = new Project { Code = "7002", Name = "Mobile Apps" };
            db.Set<Project>().Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        }

        await AddAsync(host, "Program office", CustomFieldType.Text);
        await AddAsync(host, "Head count", CustomFieldType.Number);
        await AddAsync(host, "Go live", CustomFieldType.Date);
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("program_office", "PEO Enterprise Systems"), ("head_count", "12"), ("go_live", "2027-01-15")));
        await Fields(host).SaveValuesAsync(E.Project, otherId, Values(("program_office", "DHS CIO"), ("head_count", "3")));
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("head_count", "14")));

        // Audit: on the project's own history, before and after.
        var audit = await host.Host.Get<IAuditLogService>().ListAsync("Project", projectId);
        Assert.Equal(2, audit.Count(a => a.Action == "Custom fields changed"));
        var last = audit.First(a => a.Action == "Custom fields changed");
        Assert.Contains("\"head_count\":\"12\"", last.BeforeJson);
        Assert.Contains("\"head_count\":\"14\"", last.AfterJson);

        // Search and filter.
        var hits = await Fields(host).SearchAsync("enterprise");
        var hit = Assert.Single(hits);
        Assert.Equal((projectId, "Program office", "projects/" + projectId), (hit.RecordId, hit.FieldLabel, hit.Link));
        Assert.Equal([projectId], await Fields(host).FilterAsync(E.Project, [new("head_count", CustomFieldOperator.AtLeast, "10")]));
        Assert.Equal([otherId], await Fields(host).FilterAsync(E.Project, [new("program_office", CustomFieldOperator.Equals, "dhs cio")]));
        Assert.Equal([otherId], await Fields(host).FilterAsync(E.Project, [new("go_live", CustomFieldOperator.IsBlank)]));
        Assert.Equal([projectId], await Fields(host).FilterAsync(E.Project, [new("go_live", CustomFieldOperator.AtMost, "2027-02-01"), new("program_office", CustomFieldOperator.Contains, "peo")]));
        Assert.Empty(await Fields(host).FilterAsync(E.Project, [new("head_count", CustomFieldOperator.AtLeast, "10"), new("program_office", CustomFieldOperator.Equals, "DHS CIO")]));

        // CSV export.
        var csv = Encoding.UTF8.GetString((await Fields(host).ExportCsvAsync(E.Project)).Content);
        Assert.Contains("Id,Project,Program office,Head count,Go live", csv);
        Assert.Contains($"{projectId},7001 · Data Platform,PEO Enterprise Systems,14,2027-01-15", csv);

        // Copy (cloning) onto a third project.
        Guid copyId;
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var copy = new Project { Code = "7003", Name = "Data Platform (copy)" };
            db.Set<Project>().Add(copy);
            await db.SaveChangesAsync();
            copyId = copy.Id;
        }

        await Fields(host).CopyValuesAsync(E.Project, projectId, copyId);
        Assert.Equal("14", (await Fields(host).GetValuesAsync(E.Project, copyId)).Fields.Single(f => f.Key == "head_count").Value);
    }

    [Fact]
    public async Task Retired_fields_disappear_from_records_but_keep_their_values_and_history()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        var field = await AddAsync(host, "Legacy code", CustomFieldType.Text, required: true);
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("legacy_code", "LC-1")));
        await Fields(host).SetActiveAsync(field.Id, false);

        Assert.Empty((await Fields(host).GetValuesAsync(E.Project, projectId)).Fields);
        await Assert.ThrowsAsync<ArgumentException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("legacy_code", "LC-2"))));
        var definition = Assert.Single(await Fields(host).ListDefinitionsAsync(E.Project, includeInactive: true));
        Assert.Equal((false, 1), (definition.IsActive, definition.ValueCount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fields(host).SaveDefinitionAsync(field.Id,
            new SaveCustomFieldRequest(E.Project, "Legacy code", CustomFieldType.Number, IsActive: false)));

        await Fields(host).SetActiveAsync(field.Id, true);
        Assert.Equal("LC-1", (await Fields(host).GetValuesAsync(E.Project, projectId)).Fields.Single().Value);
    }

    [Fact]
    public async Task Read_only_after_approval_locks_the_value_once_the_record_is_approved()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        await Fields(host).SaveDefinitionAsync(null, new SaveCustomFieldRequest(E.Project, "Charter number", CustomFieldType.Text, ReadOnlyAfterApproval: true));
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("charter_number", "CH-1")));
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            (await db.Set<Project>().SingleAsync(p => p.Id == projectId)).Status = ProjectStatus.Closed; // Projects lock when closed.
            await db.SaveChangesAsync();
        }

        var locked = await Fields(host).GetValuesAsync(E.Project, projectId);
        Assert.True(locked.IsLocked);
        Assert.True(locked.Fields.Single().IsReadOnly);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("charter_number", "CH-2"))));
        Assert.Contains("can't change once", ex.Message);
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("charter_number", "CH-1"))); // Unchanged is fine.
    }

    [Fact]
    public async Task Sensitive_fields_are_masked_through_the_policy_seam_and_refuse_writes()
    {
        var policy = new SwitchablePolicy();
        var (seeded, projectId) = await HostWithProjectAsync(s => s.Replace(ServiceDescriptor.Scoped<ICustomFieldAccessPolicy>(_ => policy)));
        await using var host = seeded;
        var badge = await AddAsync(host, "Badge number", CustomFieldType.Text, sensitive: true);
        await AddAsync(host, "Room", CustomFieldType.Text);
        Assert.Equal("field.project.badge_number", badge.PermissionKey);
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("badge_number", "B-1"), ("room", "4A")));

        policy.Allowed = false;
        var masked = await Fields(host).GetValuesAsync(E.Project, projectId);
        var field = masked.Fields.Single(f => f.Key == "badge_number");
        Assert.Equal((true, true, null, CustomFieldValues.Masked), (field.IsMasked, field.IsReadOnly, field.Value, field.Display));
        Assert.Equal("4A", masked.Fields.Single(f => f.Key == "room").Value);
        Assert.Equal(CustomFieldValues.Masked, (await Fields(host).GetTableAsync(E.Project, [projectId])).Values[projectId]["badge_number"]);
        Assert.DoesNotContain("B-1", Encoding.UTF8.GetString((await Fields(host).ExportCsvAsync(E.Project)).Content));
        Assert.Empty(await Fields(host).SearchAsync("B-1"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Fields(host).FilterAsync(E.Project, [new("badge_number", CustomFieldOperator.Equals, "B-1")]));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("badge_number", "B-2"))));
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("room", "5B"))); // Other fields still save.
        Assert.Null(await host.Host.Get<IEntityFieldCatalog>().GetValueAsync(E.Project, projectId, "cf.badge_number"));

        policy.Allowed = true;
        Assert.Equal("B-1", (await Fields(host).GetValuesAsync(E.Project, projectId)).Fields.Single(f => f.Key == "badge_number").Value);
        var audit = await host.Host.Get<IAuditLogService>().ListAsync("Project", projectId);
        Assert.DoesNotContain(audit, a => (a.AfterJson ?? "").Contains("B-1", StringComparison.Ordinal)); // Never in the audit trail.
    }

    [Fact]
    public async Task The_default_policy_shows_sensitive_values_to_finance_and_admins_only()
    {
        var (seeded, _) = await HostWithProjectAsync();
        await using var host = seeded;
        var policy = host.Host.Get<ICustomFieldAccessPolicy>();
        Assert.IsType<FinanceAdminCustomFieldAccessPolicy>(policy);
        Assert.True(await policy.CanSeeSensitiveAsync(E.Project, "field.project.x"));
        host.As(SeededHost.Finance);
        Assert.True(await policy.CanSeeSensitiveAsync(E.Project, "field.project.x"));
        host.As(SeededHost.Viewer);
        Assert.False(await policy.CanSeeSensitiveAsync(E.Project, "field.project.x"));
    }

    [Fact]
    public async Task Only_admins_define_fields_and_managers_change_values()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        await AddAsync(host, "Program office", CustomFieldType.Text);
        host.As(SeededHost.Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => AddAsync(host, "Another", CustomFieldType.Text));
        Assert.Single(await Fields(host).ListDefinitionsAsync(E.Project));
        await Fields(host).SaveValuesAsync(E.Project, projectId, Values(("program_office", "PEO EIS"))); // Finance manages projects.
        host.As(SeededHost.Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Fields(host).ExportCsvAsync(E.Project));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Fields(host).SaveValuesAsync(E.Project, projectId, Values(("program_office", "X"))));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Fields(host).GetValuesAsync(E.Project, Guid.NewGuid()));
    }

    [Fact]
    public async Task The_entity_field_catalog_lists_built_in_and_custom_fields_and_sets_custom_values()
    {
        var (seeded, projectId) = await HostWithProjectAsync();
        await using var host = seeded;
        await AddAsync(host, "Head count", CustomFieldType.Number, required: true);
        var catalog = host.Host.Get<IEntityFieldCatalog>();
        Assert.Contains(E.Project, catalog.EntityTypes);
        Assert.Equal(13, catalog.EntityTypes.Count);

        var fields = await catalog.GetFieldsAsync(E.Project);
        var status = fields.Single(f => f.Key == "status");
        Assert.Equal(("Picklist", false), (status.FieldType, status.IsCustom));
        Assert.Contains("OnHold", status.Options!);
        var custom = fields.Single(f => f.Key == "cf.head_count");
        Assert.Equal(("Number", true, true), (custom.FieldType, custom.IsCustom, custom.IsRequired));

        await catalog.SetValueAsync(E.Project, projectId, "cf.head_count", 7m);
        Assert.Equal(7m, await catalog.GetValueAsync(E.Project, projectId, "cf.head_count"));
        Assert.Equal("Active", await catalog.GetValueAsync(E.Project, projectId, "status"));
        Assert.Equal("7001", await catalog.GetValueAsync(E.Project, projectId, "code"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SetValueAsync(E.Project, projectId, "status", "Closed"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetFieldsAsync("spaceship"));
    }

    [Fact]
    public async Task The_report_writer_offers_custom_fields_as_columns()
    {
        var (seeded, _) = await HostWithProjectAsync();
        await using var host = seeded;
        Guid employeeId;
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var employee = new Employee { EmployeeNumber = "E1130", Email = "casey@example.com", DisplayName = "Casey Ward", HireDate = new DateOnly(2024, 1, 2) };
            db.Set<Employee>().Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
        }

        await AddAsync(host, "Clearance sponsor", CustomFieldType.Text, entity: E.Employee);
        await Fields(host).SaveValuesAsync(E.Employee, employeeId, Values(("clearance_sponsor", "DHS")));
        var writer = host.Host.Get<IReportWriterService>();
        var source = (await writer.ListSourcesAsync()).Single(s => s.Key == ReportSourceKeys.Employees);
        Assert.Contains(source.Fields, f => f.Key == "cf.clearance_sponsor" && f.Label == "Clearance sponsor");

        var result = await writer.RunAsync(new ReportDefinition(ReportSourceKeys.Employees, [new("employee_number"), new("cf.clearance_sponsor")],
            [new ReportFilterSpec("cf.clearance_sponsor", ReportFilterOperator.Equals, "DHS")]));
        var row = Assert.Single(result.Rows, r => r.Kind == ReportRowKind.Detail);
        Assert.Equal(["E1130", "DHS"], row.Values);
        var csv = Encoding.UTF8.GetString((await writer.ExportAsync(new ReportDefinition(ReportSourceKeys.Employees, [new("cf.clearance_sponsor")]), ReportFileFormat.Csv)).Content);
        Assert.Contains("Clearance sponsor", csv);
        Assert.Contains("DHS", csv);
    }

    private sealed class SwitchablePolicy : ICustomFieldAccessPolicy
    {
        public bool Allowed { get; set; } = true;

        public Task<bool> CanSeeSensitiveAsync(string entityType, string permissionKey, CancellationToken cancellationToken = default) => Task.FromResult(Allowed);
    }
}
