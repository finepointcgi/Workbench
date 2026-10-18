using Workbench.Ui.Dashboards;

namespace Workbench.Tests.Dashboards;

public sealed class SafeMarkdownTests
{
    [Fact]
    public void Parses_headings_paragraphs_lists_and_code()
    {
        var blocks = SafeMarkdown.Parse("# Title\nSome **bold** and *it* and `x`.\nSame paragraph.\n\n- one\n- two\n1. first\n```\n<b>raw</b>\n```");

        var heading = Assert.IsType<MdHeading>(blocks[0]);
        Assert.Equal((1, "Title"), (heading.Level, Assert.Single(heading.Inlines).Text));
        var paragraph = Assert.IsType<MdParagraph>(blocks[1]);
        Assert.Equal(
            [MdInlineKind.Text, MdInlineKind.Bold, MdInlineKind.Text, MdInlineKind.Italic, MdInlineKind.Text, MdInlineKind.Code, MdInlineKind.Text],
            paragraph.Inlines.Select(i => i.Kind));
        Assert.EndsWith("Same paragraph.", paragraph.Inlines[^1].Text);
        Assert.Equal(2, Assert.IsType<MdList>(blocks[2]).Items.Count);
        Assert.True(Assert.IsType<MdList>(blocks[3]).Ordered);
        Assert.Equal("<b>raw</b>", Assert.IsType<MdCode>(blocks[4]).Text);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:pm@example.com", true)]
    [InlineData("sheets/123?item=4", true)]
    [InlineData("/dashboards", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("//evil.example.com", false)]
    public void Only_allows_safe_link_targets(string href, bool safe) => Assert.Equal(safe, SafeMarkdown.IsSafeHref(href));

    [Fact]
    public void Unsafe_links_become_plain_text()
    {
        var inlines = SafeMarkdown.ParseInline("[click](javascript:alert(1)) [ok](https://example.com)");

        Assert.Equal(new MdInline(MdInlineKind.Text, "click"), inlines[0]);
        Assert.Contains(new MdInline(MdInlineKind.Link, "ok", "https://example.com"), inlines);
    }
}
