using Bunit;
using MudBlazor.Services;
using Workbench.Ui.Help;
using Workbench.Ui.Help.Markdown;

namespace Workbench.Tests.Help;

public sealed class MarkdownSafetyTests
{
    private static Task<string> RenderAsync(string markdown) => RenderAsync(MarkdownDocument.Parse(markdown));

    private static async Task<string> RenderAsync(MarkdownDocument document)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<HelpMarkdown>(p => p.Add(x => x.Document, document));
        return cut.Markup;
    }

    [Fact]
    public async Task Html_in_an_article_is_shown_as_text_never_as_markup()
    {
        var markup = await RenderAsync("""
            # Title <script>alert('h')</script>

            Hello <script>alert(1)</script> and <img src=x onerror=alert(2)> and <a href="javascript:alert(3)">x</a>

            - <iframe src="https://evil.example"></iframe>

            | <b onclick="x()">cell</b> |
            | --- |
            | <svg onload=alert(4)> |

            > [!NOTE]
            > <style>body{display:none}</style>

            ```
            <script>alert(5)</script>
            ```
            """);

        // Only the elements the renderer itself creates (MudAlert brings its own <svg> icon), and no event handlers anywhere.
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(markup);
        Assert.Empty(document.All.Where(e => e.LocalName is "script" or "img" or "iframe" or "style" or "b" or "object" or "embed"));
        Assert.DoesNotContain(document.All.SelectMany(e => e.Attributes), a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(document.QuerySelectorAll("a")); // the <a href="javascript:..."> is text
        Assert.Contains("&lt;script&gt;", markup);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("//evil.example/x")]
    [InlineData("\\\\evil.example\\x")]
    [InlineData("java script:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("help/x\" onclick=\"alert(1)")]
    [InlineData("")]
    public async Task Unsafe_link_targets_lose_the_link_but_keep_the_words(string target)
    {
        Assert.Null(MarkdownLinks.Classify(target));

        var markup = await RenderAsync($"Click [the words]({target}) here.");
        Assert.Contains("the words", markup);
        Assert.DoesNotContain("<a", markup);
    }

    [Theory]
    [InlineData("time", MdLinkKind.App, "time")]
    [InlineData("/setup/import", MdLinkKind.App, "setup/import")]
    [InlineData("https://www.gsa.gov/travel", MdLinkKind.External, "https://www.gsa.gov/travel")]
    [InlineData("mailto:help@example.com", MdLinkKind.Mail, "mailto:help@example.com")]
    [InlineData("term:EAC", MdLinkKind.Term, "eac")]
    [InlineData("help:entering-time", MdLinkKind.Help, "entering-time")]
    public void Allowed_targets_are_classified(string target, MdLinkKind kind, string cleaned)
    {
        Assert.Equal((kind, cleaned), MarkdownLinks.Classify(target));
    }

    [Fact]
    public async Task Links_terms_and_help_links_render_as_expected()
    {
        var markup = await RenderAsync("See [the GSA](https://www.gsa.gov/), [your time](time), [an EAC](term:eac) and [correcting](help:correcting-time).");

        Assert.Contains("href=\"https://www.gsa.gov/\"", markup);
        Assert.Contains("rel=\"noopener noreferrer\"", markup);
        Assert.Contains("(opens in a new tab)", markup);
        Assert.Contains("href=\"time\"", markup);
        Assert.Contains("href=\"help/correcting-time\"", markup);
        Assert.Contains("data-term=\"eac\"", markup);
    }

    [Fact]
    public void Parses_the_subset_articles_use()
    {
        var doc = MarkdownDocument.Parse("""
            ---
            title: Example
            keywords: a b
            ---
            ## Steps

            1. First **bold** and *em* and `code`
            2. Second
               continues here

            - bullet

            | A | B |
            | - | - |
            | 1 | 2 \| 3 |

            > [!WARNING]
            > Careful.

            ---
            """);

        Assert.Equal("Example", doc.FrontMatter["title"]);
        Assert.Collection(doc.Blocks,
            b => Assert.Equal("steps", Assert.IsType<MdHeading>(b).Id),
            b =>
            {
                var list = Assert.IsType<MdList>(b);
                Assert.True(list.Ordered);
                Assert.Equal(2, list.Items.Count);
                Assert.Contains(list.Items[0], i => i is MdStrong);
                Assert.Contains(list.Items[0], i => i is MdEmphasis);
                Assert.Contains(list.Items[0], i => i is MdCodeSpan);
            },
            b => Assert.False(Assert.IsType<MdList>(b).Ordered),
            b =>
            {
                var table = Assert.IsType<MdTable>(b);
                Assert.Equal(2, table.Header.Count);
                Assert.Equal("2 | 3", Assert.IsType<MdText>(Assert.Single(table.Rows[0][1])).Text);
            },
            b => Assert.Equal(MdCalloutKind.Warning, Assert.IsType<MdCallout>(b).Kind),
            b => Assert.IsType<MdRule>(b));
    }

    [Fact]
    public async Task Every_shipped_article_is_complete_and_renders_safely()
    {
        Assert.True(HelpLibrary.Articles.Count >= 20, "the help center ships its articles as embedded resources");
        foreach (var article in HelpLibrary.Articles)
        {
            Assert.False(string.IsNullOrWhiteSpace(article.Title), article.Slug);
            Assert.False(string.IsNullOrWhiteSpace(article.Summary), article.Slug);
            Assert.Contains(article.Category, HelpLibrary.Categories);
            Assert.DoesNotContain(article.Document.Blocks, b => b is MdHeading { Level: 1 }); // the page's own title is the h1

            var markup = await RenderAsync(article.Document);
            Assert.DoesNotContain("<script", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"javascript", markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Article_links_point_at_real_articles_and_glossary_terms()
    {
        var links = HelpLibrary.Articles
            .SelectMany(a => Inlines(a.Document).OfType<MdLink>().Select(l => (a.Slug, l)))
            .Concat(HelpLibrary.WhatsNew.SelectMany(w => Inlines(w.Document).OfType<MdLink>().Select(l => ("whats-new", l))))
            .ToList();

        Assert.NotEmpty(links);
        foreach (var (slug, link) in links)
        {
            switch (link.Kind)
            {
                case MdLinkKind.Help:
                    Assert.True(HelpLibrary.Find(link.Target.Split('#')[0]) is not null, $"{slug}: help:{link.Target} is not an article");
                    break;
                case MdLinkKind.Term:
                    Assert.True(Glossary.Find(link.Target) is not null, $"{slug}: term:{link.Target} is not in the glossary");
                    break;
            }
        }
    }

    [Fact]
    public void Whats_new_lists_versions_newest_first()
    {
        Assert.NotEmpty(HelpLibrary.WhatsNew);
        Assert.Equal(HelpLibrary.WhatsNew[0].Version, HelpLibrary.LatestVersion);
        Assert.All(HelpLibrary.WhatsNew, e => Assert.InRange(e.Version.Length, 1, 32));

        var parsed = HelpLibrary.ParseWhatsNew("---\ntitle: x\n---\n## 2.0 · Today\n- new\n\n## 1.0 · Before\n- old\n");
        Assert.Equal(["2.0", "1.0"], parsed.Select(p => p.Version));
        Assert.Equal("Today", parsed[0].Date);
    }

    [Fact]
    public void Search_finds_articles_by_task_words()
    {
        Assert.Equal("correcting-time", HelpLibrary.Search("correct past day")[0].Slug);
        Assert.Contains(HelpLibrary.Search("per diem"), a => a.Slug == "expenses-per-diem");
        Assert.Contains(HelpLibrary.Search("bank reconciliation"), a => a.Slug == "bank-reconciliation");
        Assert.Empty(HelpLibrary.Search("zzzz qqqq"));
        Assert.Empty(HelpLibrary.Search("  "));
    }

    private static IEnumerable<MdInline> Inlines(MarkdownDocument document) => document.Blocks.SelectMany(b => b switch
    {
        MdHeading h => Flatten(h.Content),
        MdParagraph p => Flatten(p.Content),
        MdList l => l.Items.SelectMany(Flatten),
        MdCallout c => c.Paragraphs.SelectMany(Flatten),
        MdTable t => t.Header.Concat(t.Rows.SelectMany(r => r)).SelectMany(Flatten),
        _ => [],
    });

    private static IEnumerable<MdInline> Flatten(IEnumerable<MdInline> inlines) => inlines.SelectMany(i => i switch
    {
        MdStrong s => Flatten(s.Content).Prepend(i),
        MdEmphasis e => Flatten(e.Content).Prepend(i),
        MdLink l => Flatten(l.Content).Prepend(i),
        _ => [i],
    });
}
