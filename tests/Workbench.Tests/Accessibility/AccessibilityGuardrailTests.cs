using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Ui.Accessibility;

namespace Workbench.Tests.Accessibility;

/// <summary>The guardrail itself, and the shared accessible components (charts, breadcrumbs).</summary>
public sealed class AccessibilityGuardrailTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    [Fact]
    public async Task Flags_icon_buttons_and_inputs_without_a_name()
    {
        await using var ctx = NewContext();
        var markup = ctx.Render(b =>
        {
            b.OpenComponent<MudIconButton>(0);
            b.AddAttribute(1, nameof(MudIconButton.Icon), Icons.Material.Filled.Delete);
            b.CloseComponent();
            b.AddMarkupContent(2, "<input type=\"text\" />");
        });

        var problems = A11yAssert.Find(markup.Nodes);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.StartsWith("No accessible name", p));
    }

    [Fact]
    public async Task Accepts_labelled_controls()
    {
        await using var ctx = NewContext();
        var markup = ctx.Render(b =>
        {
            b.OpenComponent<MudIconButton>(0);
            b.AddAttribute(1, nameof(MudIconButton.Icon), Icons.Material.Filled.Delete);
            b.AddAttribute(2, "aria-label", "Delete line");
            b.CloseComponent();
            b.OpenComponent<MudTextField<string>>(3);
            b.AddAttribute(4, nameof(MudTextField<string>.Label), "Title");
            b.CloseComponent();
            b.AddMarkupContent(5, "<label>Hours <input type=\"number\" /></label><button><svg aria-hidden=\"true\"></svg>Save</button>");
        });

        A11yAssert.Check(markup);
    }

    [Fact]
    public async Task Flags_nested_controls_and_unnamed_data_tables()
    {
        await using var ctx = NewContext();
        var markup = ctx.Render(b => b.AddMarkupContent(0,
            "<div role=\"button\" tabindex=\"0\" aria-label=\"Open\"><button aria-label=\"Delete\"></button></div>" +
            "<table><tr><th>Week</th></tr><tr><td>1</td></tr></table>"));

        var problems = A11yAssert.Find(markup.Nodes);

        Assert.Contains(problems, p => p.StartsWith("Interactive control nested"));
        Assert.Contains(problems, p => p.StartsWith("Data table without a caption"));
    }

    [Fact]
    public async Task Charts_are_one_named_image_with_a_data_table()
    {
        await using var ctx = NewContext();
        List<ChartSeries<double>> series =
        [
            new() { Name = "Budget", Data = new ChartData<double>([100, 200]) },
            new() { Name = "Actual", Data = new ChartData<double>([90]) },
        ];
        var chart = ctx.Render<AccessibleChart>(p => p
            .Add(c => c.Title, "Budget vs actual")
            .Add(c => c.Labels, ["Oct", "Nov"])
            .Add(c => c.Series, series)
            .Add(c => c.Format, v => v.ToString("0"))
            .Add(c => c.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, "<svg><text>chart</text></svg>"))));

        Assert.StartsWith("Budget vs actual", chart.Find("[role=img]").GetAttribute("aria-label"));
        Assert.Equal("Budget vs actual", chart.Find("caption").TextContent);
        Assert.Equal(["Period", "Budget", "Actual"], chart.FindAll("thead th").Select(th => th.TextContent));
        Assert.Equal(["Nov", "200", "—"], chart.FindAll("tbody tr")[1].Children.Select(c => c.TextContent));
        Assert.All(chart.FindAll("th"), th => Assert.NotNull(th.GetAttribute("scope")));
        A11yAssert.Check(chart);
    }

    [Fact]
    public async Task Breadcrumbs_are_a_landmark_without_dead_links()
    {
        await using var ctx = NewContext();
        var crumbs = ctx.Render<AccessibleBreadcrumbs>(p => p.Add(c => c.Items,
            [new BreadcrumbItem("Finance", null, disabled: true), new BreadcrumbItem("Bills", "purchasing/bills"), new BreadcrumbItem("Bill", null, disabled: true)]));

        Assert.Equal("Breadcrumb", crumbs.Find("nav").GetAttribute("aria-label"));
        Assert.Empty(crumbs.FindAll("a[href='#']"));
        Assert.Equal("purchasing/bills", crumbs.Find("a").GetAttribute("href"));
        Assert.Equal("Bill", crumbs.Find("[aria-current=page]").TextContent);
        A11yAssert.Check(crumbs);
    }
}
