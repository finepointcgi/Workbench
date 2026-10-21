using Workbench.Application;
using Workbench.Application.Customization;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Tests.Identity;
using E = Workbench.Contracts.Customization.CustomizableEntities;

namespace Workbench.Tests.Customization;

public sealed class FormLayoutTests
{
    /// <summary>The fields of each converted page, in the order the page was built. The built-in layout must keep them.</summary>
    public static TheoryData<string, string[]> PagesAsBuilt => new()
    {
        { E.Project, ["code", "name", "contractId", "projectManagerId", "status", "start", "finish"] },
        { E.Contract, ["contractNumber", "customerId", "name", "type", "status", "periodStart", "periodEnd", "ceilingValue", "fundedValue", "isPrime"] },
        { E.Employee, ["employeeNumber", "displayName", "email", "orgUnitId", "supervisorId", "defaultLaborCategoryId", "standardHoursPerWeek", "hireDate", "terminationDate", "isExempt"] },
        { E.Vendor, ["code", "name", "uei", "status", "paymentTermsDays", "businessSize", "socioEconomicCategories", "is1099", "form1099Box", "remitToAddress", "contactName", "contactEmail", "contactPhone"] },
        { E.Customer, ["code", "name", "category"] },
        { E.PurchaseOrder, ["vendorId", "orderDate", "shipTo", "receivers", "changeReason", "terms"] },
        { E.ApBill, ["vendorId", "invoiceNumber", "invoiceDate", "dueDate", "currencyCode", "description"] },
        { E.Invoice, ["contractId", "type", "periodFrom", "periodTo", "invoiceDate", "releaseWithhold", "notes"] },
        { E.ExpenseReport, ["title", "tripStart", "tripEnd", "purpose"] },
        { E.Part, ["number", "revision", "description", "unitOfMeasure", "source", "tracking", "costMethod", "standardCost", "preferredVendorId", "leadTimeDays", "safetyStock", "minimumOrderQuantity", "orderMultiple", "defaultBinId", "notes", "isActive"] },
        { E.Opportunity, ["name", "solicitationNumber", "description", "agencyAccountId", "agencyName", "naicsCode", "setAside", "contractVehicle", "contractType", "placeOfPerformance", "kind", "incumbentAccountId",
                          "estimatedValue", "periodOfPerformanceMonths", "estimatedAwardDate", "rfiDueOn", "rfpReleaseOn", "proposalDueOn",
                          "captureManagerEmail", "proposalManagerEmail", "pwin", "pgo", "weightedValue"] },
    };

    [Theory]
    [MemberData(nameof(PagesAsBuilt))]
    public async Task With_no_saved_layout_every_form_renders_as_built(string entityType, string[] fields)
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Viewer);
        var layout = await host.Host.Get<IFormLayoutService>().GetEffectiveAsync(entityType);

        Assert.True(layout.IsBuiltIn);
        Assert.Equal("Built-in", layout.Source);
        Assert.Equal(fields, layout.Sections.SelectMany(s => s.Fields).Select(f => f.Key));
        Assert.Empty(layout.HiddenKeys);
        Assert.All(layout.Sections.SelectMany(s => s.Fields), f => Assert.Equal(f.DomainRequired, f.Required)); // Nothing extra is required.
    }

    [Fact]
    public void Section_headings_are_kept_where_the_page_has_them()
    {
        Assert.Equal(["The opportunity", "Value and dates", "People and probability"], BuiltInForms.Get(E.Opportunity).Sections.Select(s => s.Title));
        Assert.Equal(["1. Contract and period"], BuiltInForms.Get(E.Invoice).Sections.Select(s => s.Title));
        Assert.Equal([""], BuiltInForms.Get(E.Project).Sections.Select(s => s.Title));
    }

    [Fact]
    public void The_most_specific_layout_applies_role_in_company_then_company_then_role_then_everyone()
    {
        var fed = Guid.NewGuid();
        var layouts = new[]
        {
            (Company: (Guid?)null, Role: (string?)null, Name: "everyone"),
            (Company: (Guid?)null, Role: (string?)"Finance", Name: "finance"),
            (Company: (Guid?)fed, Role: (string?)null, Name: "fed"),
            (Company: (Guid?)fed, Role: (string?)"Finance", Name: "fed finance"),
        }.Select(l => new Holder(l.Company, l.Role, l.Name)).ToList();

        string? Pick(Guid? company, params string[] roles) =>
            FormLayoutEngine.Pick(layouts, l => l.Company, l => l.Role, company, [.. roles, CustomizationRoles.Everyone])?.Name;

        Assert.Equal("fed finance", Pick(fed, "Finance"));
        Assert.Equal("fed", Pick(fed, "Hr"));
        Assert.Equal("finance", Pick(Guid.NewGuid(), "Finance"));
        Assert.Equal("everyone", Pick(null, "Hr"));
        Assert.Equal("finance", Pick(null, "Admin", "Finance")); // the first of the caller's roles that has a layout
        Assert.Null(FormLayoutEngine.Pick(layouts.Where(l => l.Company == fed).ToList(), l => l.Company, l => l.Role, null, ["Finance", "Employee"]));
    }

    private sealed record Holder(Guid? Company, string? Role, string Name);

    [Fact]
    public void A_saved_layout_is_brought_in_step_with_todays_fields()
    {
        var form = BuiltInForms.Get(E.Project);
        var fields = form.Fields.Select(f => BuiltInForms.ToDto(E.Project, f))
            .Append(new EntityFieldDto(E.Project, "cf_program", "Program", "Text", true, false, null, null)).ToList();
        IReadOnlyList<FormSectionLayout> saved =
        [
            new("dates", "Dates", [new("finish"), new("start"), new("gone")]),
            new("details", "Basics", [new("name"), new("code", Hidden: true), new("name")]),
        ];

        var normalized = FormLayoutEngine.Normalize(saved, BuiltInForms.Layout(form), fields);

        Assert.Equal(["dates", "details"], normalized.Select(s => s.Key));
        Assert.Equal(["finish", "start"], normalized[0].Fields.Select(f => f.Key)); // unknown "gone" dropped
        // Fields the layout forgot go back after their built-in neighbour; custom fields go at the end.
        Assert.Equal(["name", "contractId", "projectManagerId", "status", "code", "cf_program"], normalized[1].Fields.Select(f => f.Key));
        var code = normalized[1].Fields.Single(f => f.Key == "code");
        Assert.False(code.Hidden); // the domain needs it, so it can't stay hidden
        Assert.True(code.Required);
    }

    [Fact]
    public void A_layout_can_never_hide_or_relax_what_the_domain_needs()
    {
        var fields = BuiltInForms.Get(E.Contract).Fields.Select(f => BuiltInForms.ToDto(E.Contract, f)).ToList();
        FormSectionLayout Section(params FormFieldPlacement[] placements) => new("details", "", placements);

        var hideRequired = Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([Section(new FormFieldPlacement("customerId", Hidden: true))], fields));
        Assert.Contains("can't be hidden", hideRequired.Message);
        Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([Section(new FormFieldPlacement("ceilingValue", Hidden: true, Required: true))], fields));
        Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([Section(new FormFieldPlacement("isPrime", Required: true))], fields)); // a switch always has a value
        Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([Section(new FormFieldPlacement("nope"))], fields));
        Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([Section(new FormFieldPlacement("name"), new FormFieldPlacement("name"))], fields));
        Assert.Throws<ArgumentException>(() => FormLayoutEngine.Validate([], fields));
        FormLayoutEngine.Validate([Section(new FormFieldPlacement("ceilingValue", Hidden: true), new FormFieldPlacement("fundedValue", Required: true))], fields);
    }

    [Fact]
    public async Task Layouts_fall_back_per_role_and_per_company()
    {
        await using var host = await SeededHost.CreateAsync();
        var fed = await AddCompanyAsync(host, "FED");
        var layouts = host.As(SeededHost.Admin).Host.Get<IFormLayoutService>();
        await layouts.SaveAsync(Layout(E.Project, null, null, "Everyone's"));
        await layouts.SaveAsync(Layout(E.Project, null, "Finance", "Finance's"));
        await layouts.SaveAsync(Layout(E.Project, fed, "Finance", "Finance at FED"));

        async Task<string> HeadingFor(string email) =>
            (await host.As(email).Host.Get<IFormLayoutService>().GetEffectiveAsync(E.Project)).Sections[0].Title;

        Assert.Equal("Finance's", await HeadingFor(SeededHost.Finance));
        Assert.Equal("Everyone's", await HeadingFor(SeededHost.Viewer));
        Assert.Equal("Everyone's", await HeadingFor(SeededHost.Admin)); // Admin has no layout of its own

        await SelectCompanyAsync(host, SeededHost.Finance, fed);
        Assert.Equal("Finance at FED", await HeadingFor(SeededHost.Finance));
        Assert.Equal("Finance in FED", (await host.As(SeededHost.Finance).Host.Get<IFormLayoutService>().GetEffectiveAsync(E.Project)).Source);

        // The preview answers for a role without signing in as it.
        var preview = await host.As(SeededHost.Admin).Host.Get<IFormLayoutService>().PreviewAsync(new PreviewFormLayoutRequest(E.Project, fed, "Hr"));
        Assert.Equal("Everyone's", preview.Sections[0].Title);
        var draft = await layouts.PreviewAsync(new PreviewFormLayoutRequest(E.Project, null, "Hr", Layout(E.Project, null, "Hr", "Draft for HR")));
        Assert.Equal("Draft for HR", draft.Sections[0].Title);

        // Removing a layout falls back to the next one.
        var finance = (await layouts.ListAsync(E.Project)).Single(l => l.RoleKey == "Finance" && l.CompanyId is null);
        await layouts.DeleteAsync(finance.Id!.Value);
        await SelectCompanyAsync(host, SeededHost.Finance, null);
        Assert.Equal("Everyone's", await HeadingFor(SeededHost.Finance));
    }

    [Fact]
    public async Task Only_those_who_manage_layouts_change_them()
    {
        await using var host = await SeededHost.CreateAsync();
        var finance = host.As(SeededHost.Finance).Host.Get<IFormLayoutService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.SaveAsync(Layout(E.Project, null, null, "Mine")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.ListAsync(E.Project));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.PreviewAsync(new PreviewFormLayoutRequest(E.Project, null, "Hr")));
        Assert.NotEmpty((await finance.GetEffectiveAsync(E.Project)).Sections); // reading the layout is for everyone
    }

    [Fact]
    public async Task Hidden_fields_are_left_out_and_required_fields_are_checked_by_the_server()
    {
        await using var host = await SeededHost.CreateAsync();
        var admin = host.As(SeededHost.Admin).Host.Get<IFormLayoutService>();
        await admin.SaveAsync(new SaveFormLayoutRequest(E.Project, null, null,
        [
            new("details", "", [new("code"), new("name"), new("contractId", Hidden: true), new("projectManagerId"), new("status"), new("start", Required: true), new("finish")]),
        ]));

        host.As(SeededHost.Finance);
        var layout = await host.Host.Get<IFormLayoutService>().GetEffectiveAsync(E.Project);
        Assert.Equal(["contractId"], layout.HiddenKeys);
        Assert.DoesNotContain(layout.Sections[0].Fields, f => f.Key == "contractId");
        Assert.True(layout.Sections[0].Fields.Single(f => f.Key == "start").Required);

        var projects = host.Host.Get<IProjectService>();
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => projects.CreateProjectAsync(new SaveProjectRequest("9001", "Layout test", null, null, ProjectState.Active, null, null)));
        Assert.Equal("Start is required.", missing.Message);

        var created = await projects.CreateProjectAsync(new SaveProjectRequest("9001", "Layout test", null, null, ProjectState.Active, new DateOnly(2026, 10, 1), null));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.UpdateProjectAsync(created.Id, new SaveProjectRequest("9001", "Layout test", null, null, ProjectState.Active, null, null)));

        // The service's own rules still run: a layout never relaxes them.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => projects.CreateProjectAsync(new SaveProjectRequest("", "No code", null, null, ProjectState.Active, new DateOnly(2026, 10, 1), null)));
    }

    [Fact]
    public async Task Required_fields_apply_to_dialogs_that_return_a_request_too()
    {
        await using var host = await SeededHost.CreateAsync();
        await host.As(SeededHost.Admin).Host.Get<IFormLayoutService>().SaveAsync(new SaveFormLayoutRequest(E.ExpenseReport, null, CustomizationRoles.Everyone,
            [new("details", "", [new("title"), new("tripStart"), new("tripEnd"), new("purpose", Required: true)])]));
        await AddEmployeeAsync(host, SeededHost.Viewer);

        var reports = host.As(SeededHost.Viewer).Host.Get<IExpenseReportService>();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => reports.CreateReportAsync(new SaveExpenseReportRequest("Trip")));
        Assert.Equal("Business purpose is required.", error.Message);
        var report = await reports.CreateReportAsync(new SaveExpenseReportRequest("Trip", Purpose: "Site visit"));
        Assert.Equal("Site visit", report.Purpose);
    }

    [Fact]
    public async Task A_role_specific_requirement_binds_only_that_role()
    {
        await using var host = await SeededHost.CreateAsync();
        await host.As(SeededHost.Admin).Host.Get<IFormLayoutService>().SaveAsync(new SaveFormLayoutRequest(E.Customer, null, "Finance",
            [new("details", "", [new("code"), new("name"), new("category", Required: true)])]));

        var contracts = host.As(SeededHost.Finance).Host.Get<IContractService>();
        await Assert.ThrowsAsync<ArgumentException>(() => contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army")));
        await contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army", "DoD"));

        // Admin isn't Finance here, so the Finance layout doesn't apply to them.
        await host.As(SeededHost.Admin).Host.Get<IContractService>().CreateCustomerAsync(new SaveCustomerRequest("NAVY", "Navy"));
    }

    private static SaveFormLayoutRequest Layout(string entityType, Guid? company, string? role, string heading)
    {
        var form = BuiltInForms.Get(entityType);
        return new SaveFormLayoutRequest(entityType, company, role,
            [new FormSectionLayout("details", heading, form.Fields.Select(f => new FormFieldPlacement(f.Key)).ToList())]);
    }

    internal static async Task<Guid> AddCompanyAsync(SeededHost host, string code)
    {
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        if (!db.Set<Company>().Any(c => c.Id == Company.DefaultId))
        {
            db.Set<Company>().Add(new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main LLC", CreatedAtUtc = DateTime.UtcNow });
        }

        var company = new Company { Code = code, LegalName = $"{code} LLC", CreatedAtUtc = DateTime.UtcNow };
        db.Set<Company>().Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static async Task SelectCompanyAsync(SeededHost host, string email, Guid? companyId)
    {
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var user = db.Set<AppUser>().Single(u => u.Email == email);
        var selection = db.Set<UserCompanySelection>().FirstOrDefault(s => s.UserId == user.Id);
        if (selection is null)
        {
            db.Set<UserCompanySelection>().Add(new UserCompanySelection { UserId = user.Id, CompanyId = companyId, UpdatedAtUtc = DateTime.UtcNow });
        }
        else
        {
            selection.CompanyId = companyId;
        }

        await db.SaveChangesAsync();
    }

    private static async Task AddEmployeeAsync(SeededHost host, string email)
    {
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        db.Set<Workbench.Domain.ProjectAccounting.Employee>().Add(new Workbench.Domain.ProjectAccounting.Employee
        {
            EmployeeNumber = "E1150",
            Email = email,
            DisplayName = "Val Chen",
            HireDate = new DateOnly(2024, 1, 1),
        });
        await db.SaveChangesAsync();
    }
}
