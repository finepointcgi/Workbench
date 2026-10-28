using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Ui.Setup;

namespace Workbench.Tests.Setup;

public sealed class SetupComponentTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task Getting_started_covers_the_whole_system_and_lists_every_setup_screen()
    {
        await using var ctx = NewContext();
        var checklist = new SetupChecklistDto(true, true, true, true, false, false, false,
            HasInvitedUsers: true, HasChartOfAccounts: true, HasBankAccount: false, HasPayrollProvider: true, HasApprovalWorkflows: false,
            HasIndirectRates: false, HasBillingDetails: false);
        ctx.Services.AddSingleton(Workbench.Tests.Identity.Fake<ISetupAdminService>.Create(("GetChecklistAsync", _ => checklist)));

        var page = ctx.Render<SetupHome>();

        page.WaitForAssertion(() => Assert.Equal("7 of 14 done", page.Find("[data-testid=checklist-progress]").TextContent.Trim()));
        var items = page.FindAll("[data-testid=checklist-item]").Select(i => i.TextContent).ToList();
        foreach (var step in new[] { "Users and sign-in", "Chart of accounts", "Bank accounts", "Payroll provider", "Approval workflows", "Indirect rates", "Billing details" })
        {
            Assert.Contains(items, i => i.Contains(step));
        }

        foreach (var href in new[] { "setup/organization", "setup/per-diem", "setup/approval-workflows" })
        {
            Assert.NotEmpty(page.FindAll($"a[href='{href}']"));
        }
    }

    [Fact]
    public async Task Setup_names_the_one_step_to_do_next_and_says_when_everything_is_done()
    {
        await using var ctx = NewContext();
        var checklist = new SetupChecklistDto(true, true, true, true, false, false, false,
            HasInvitedUsers: true, HasChartOfAccounts: true, HasBankAccount: false, HasPayrollProvider: true, HasApprovalWorkflows: false,
            HasIndirectRates: false, HasBillingDetails: false);
        ctx.Services.AddSingleton(Workbench.Tests.Identity.Fake<ISetupAdminService>.Create(("GetChecklistAsync", _ => checklist)));

        var page = ctx.Render<SetupHome>();

        page.WaitForAssertion(() => Assert.Contains("Bank accounts", page.Find("[data-testid=setup-next]").TextContent));
        Assert.Equal("cash", page.Find("[data-testid=setup-next-go]").GetAttribute("href"));

        var allDone = new SetupChecklistDto(true, true, true, true, true, true, true,
            HasInvitedUsers: true, HasChartOfAccounts: true, HasBankAccount: true, HasPayrollProvider: true, HasApprovalWorkflows: true,
            HasIndirectRates: true, HasBillingDetails: true);
        await using var done = NewContext();
        done.Services.AddSingleton(Workbench.Tests.Identity.Fake<ISetupAdminService>.Create(("GetChecklistAsync", _ => allDone)));
        var finished = done.Render<SetupHome>();

        finished.WaitForAssertion(() => Assert.NotNull(finished.Find("[data-testid=setup-all-done]")));
        Assert.Empty(finished.FindAll("[data-testid=setup-next]"));
    }

    private static void Type(IRenderedComponent<EmployeeEditor> editor, string testId, string value) =>
        editor.Find($"input[data-testid='{testId}'], [data-testid='{testId}'] input").Input(value);

    [Fact]
    public async Task Employee_editor_requires_number_name_and_email_before_saving()
    {
        await using var ctx = NewContext();
        var saved = new List<SaveEmployeeRequest>();
        var editor = ctx.Render<EmployeeEditor>(p => p.Add(e => e.Save, r => { saved.Add(r); return Task.CompletedTask; }));

        await editor.Find("[data-testid='employee-save']").ClickAsync(new());

        editor.WaitForAssertion(() =>
        {
            Assert.Contains("Employee number is required.", editor.Markup);
            Assert.Contains("Name is required.", editor.Markup);
            Assert.Contains("Email is required.", editor.Markup);
        });
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Employee_editor_rejects_a_bad_email_and_saves_once_it_is_fixed()
    {
        await using var ctx = NewContext();
        var saved = new List<SaveEmployeeRequest>();
        var editor = ctx.Render<EmployeeEditor>(p => p.Add(e => e.Save, r => { saved.Add(r); return Task.CompletedTask; }));
        Type(editor, "employee-number", "E2001");
        Type(editor, "employee-name", "Sam Ortiz");
        Type(editor, "employee-email", "sam.example.com");

        await editor.Find("[data-testid='employee-save']").ClickAsync(new());

        editor.WaitForAssertion(() => Assert.Contains("Enter a valid email address.", editor.Markup));
        Assert.Empty(saved);

        Type(editor, "employee-email", "Sam@Example.com");
        await editor.Find("[data-testid='employee-save']").ClickAsync(new());

        editor.WaitForAssertion(() => Assert.Single(saved));
        Assert.Equal(("E2001", "Sam Ortiz", "Sam@Example.com", 40m), (saved[0].EmployeeNumber, saved[0].DisplayName, saved[0].Email, saved[0].StandardHoursPerWeek));
    }

    [Fact]
    public async Task Employee_editor_rejects_termination_before_hire_and_out_of_range_hours()
    {
        await using var ctx = NewContext();
        var saved = new List<SaveEmployeeRequest>();
        var existing = new EmployeeDto(Guid.NewGuid(), "E1", "a@example.com", "Alex", null, null, null, new DateOnly(2025, 1, 1),
            new DateOnly(2024, 12, 1), 90m, true);
        var editor = ctx.Render<EmployeeEditor>(p => p
            .Add(e => e.Employee, existing)
            .Add(e => e.Save, r => { saved.Add(r); return Task.CompletedTask; }));

        await editor.Find("[data-testid='employee-save']").ClickAsync(new());

        editor.WaitForAssertion(() =>
        {
            Assert.Contains("Termination date can't be before the hire date.", editor.Markup);
            Assert.Contains("Standard hours must be more than 0 and at most 80.", editor.Markup);
        });
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Employee_editor_shows_the_service_error()
    {
        await using var ctx = NewContext();
        var existing = new EmployeeDto(Guid.NewGuid(), "E1", "a@example.com", "Alex", null, null, null, new DateOnly(2025, 1, 1), null, 40m, true);
        var editor = ctx.Render<EmployeeEditor>(p => p
            .Add(e => e.Employee, existing)
            .Add(e => e.Save, _ => throw new ArgumentException("Another employee already uses that employee number or email.")));

        await editor.Find("[data-testid='employee-save']").ClickAsync(new());

        editor.WaitForAssertion(() => Assert.Contains("already uses that employee number", editor.Find("[data-testid='employee-error']").TextContent));
    }

    [Fact]
    public async Task Adding_a_child_wbs_element_suggests_the_next_code_under_the_parent()
    {
        await using var ctx = NewContext();
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parent = new WbsElementDto(Guid.NewGuid(), Guid.NewGuid(), null, "1001.01", "Program", ChargeCodeKind.Indirect, false, true, false, null, 0);

        var parameters = new DialogParameters<WbsElementDialog> { { d => d.Parent, parent }, { d => d.ProjectCode, "1001" } };
        var reference = await provider.InvokeAsync(() => dialogs.ShowAsync<WbsElementDialog>("Add child", parameters));

        // The next number under the parent is suggested in full.
        var code = provider.Find("input[data-testid='wbs-code'], [data-testid='wbs-code'] input");
        Assert.Equal("1001.01.01", code.GetAttribute("value"));
        provider.Find("input[data-testid='wbs-name'], [data-testid='wbs-name'] input").Input("Design");

        // A bare prefix isn't a valid code: it needs the child's own number.
        provider.Find("input[data-testid='wbs-code'], [data-testid='wbs-code'] input").Input("1001.01.");
        await provider.Find("[data-testid='wbs-submit']").ClickAsync(new());
        provider.WaitForAssertion(() => Assert.Contains("Code must start with", provider.Markup));

        provider.Find("input[data-testid='wbs-code'], [data-testid='wbs-code'] input").Input("1001.01.01");
        await provider.Find("[data-testid='wbs-submit']").ClickAsync(new());

        var result = await reference.Result;
        var request = Assert.IsType<SaveWbsElementRequest>(result!.Data);
        Assert.Equal((parent.Id, "1001.01.01", "Design", ChargeCodeKind.Indirect, false), (request.ParentId!.Value, request.Code, request.Name, request.Kind, request.RequiresAssignment));
    }

    [Fact]
    public async Task A_top_level_wbs_element_suggests_the_next_code_under_the_project()
    {
        await using var ctx = NewContext();
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters<WbsElementDialog> { { d => d.ProjectCode, "2002" } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<WbsElementDialog>("Add", parameters));

        Assert.Equal("2002.01", provider.Find("input[data-testid='wbs-code'], [data-testid='wbs-code'] input").GetAttribute("value"));
    }

    [Fact]
    public void Audit_diff_lines_up_fields_and_flags_changes()
    {
        var fields = AuditDiff.Compare("""{"name":"Design","isOpen":true,"itemId":null}""", """{"name":"Design 2","isOpen":true,"itemId":"abc"}""");

        Assert.Equal(["name", "isOpen", "itemId"], fields.Select(f => f.Field));
        Assert.Equal([true, false, true], fields.Select(f => f.Changed));
        Assert.Equal(("Design", "Design 2"), (fields[0].Before, fields[0].After));
        Assert.Equal(["value"], AuditDiff.Compare(null, "42").Select(f => f.Field));
    }
}
