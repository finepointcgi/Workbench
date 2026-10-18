using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace Workbench.Ui.Help.Markdown;

/// <summary>
/// Turns a <see cref="MarkdownDocument"/> into elements and components. Every piece of text goes through
/// <c>AddContent</c> (HTML-encoded) and attributes are only ever set from allowed link targets, so nothing in an
/// article can inject markup or script. There is no <see cref="MarkupString"/> here on purpose.
/// </summary>
public static class MarkdownRenderer
{
    /// <param name="headingOffset">Added to heading levels (1 in the help panel, whose own title is the h2).</param>
    public static RenderFragment Render(MarkdownDocument document, int headingOffset = 0) => builder =>
    {
        foreach (var block in document.Blocks)
        {
            RenderBlock(builder, block, headingOffset);
        }
    };

    private static void RenderBlock(RenderTreeBuilder b, MdBlock block, int headingOffset)
    {
        switch (block)
        {
            case MdHeading heading:
                var level = Math.Clamp(heading.Level + headingOffset, 1, 6);
                b.OpenElement(0, $"h{level}");
                b.AddAttribute(1, "id", heading.Id);
                b.AddAttribute(2, "class", $"wb-md-h wb-md-h{level}");
                RenderInlines(b, heading.Content);
                b.CloseElement();
                break;

            case MdParagraph paragraph:
                b.OpenElement(10, "p");
                RenderInlines(b, paragraph.Content);
                b.CloseElement();
                break;

            case MdList list:
                b.OpenElement(20, list.Ordered ? "ol" : "ul");
                if (list.Ordered && list.Start != 1)
                {
                    b.AddAttribute(21, "start", list.Start);
                }

                foreach (var item in list.Items)
                {
                    b.OpenElement(22, "li");
                    RenderInlines(b, item);
                    b.CloseElement();
                }

                b.CloseElement();
                break;

            case MdCallout { Kind: MdCalloutKind.Quote } quote:
                b.OpenElement(30, "blockquote");
                foreach (var p in quote.Paragraphs)
                {
                    b.OpenElement(31, "p");
                    RenderInlines(b, p);
                    b.CloseElement();
                }

                b.CloseElement();
                break;

            case MdCallout callout:
                b.OpenComponent<MudAlert>(40);
                b.AddComponentParameter(41, nameof(MudAlert.Severity), callout.Kind switch
                {
                    MdCalloutKind.Warning => Severity.Warning,
                    MdCalloutKind.Tip => Severity.Success,
                    _ => Severity.Info,
                });
                b.AddComponentParameter(42, nameof(MudAlert.Variant), Variant.Text);
                b.AddComponentParameter(43, nameof(MudAlert.Class), "my-3 wb-md-callout");
                b.AddComponentParameter(44, nameof(MudAlert.ChildContent), (RenderFragment)(inner =>
                {
                    inner.OpenElement(45, "strong");
                    inner.AddContent(46, callout.Kind switch
                    {
                        MdCalloutKind.Warning => "Watch out: ",
                        MdCalloutKind.Tip => "Tip: ",
                        _ => "Note: ",
                    });
                    inner.CloseElement();
                    var first = true;
                    foreach (var p in callout.Paragraphs)
                    {
                        if (!first)
                        {
                            inner.OpenElement(47, "br");
                            inner.CloseElement();
                        }

                        RenderInlines(inner, p);
                        first = false;
                    }
                }));
                b.CloseComponent();
                break;

            case MdCode code:
                b.OpenElement(50, "pre");
                b.AddAttribute(51, "class", "wb-md-code");
                b.AddAttribute(52, "tabindex", "0");
                b.OpenElement(53, "code");
                b.AddContent(54, code.Text);
                b.CloseElement();
                b.CloseElement();
                break;

            case MdRule:
                b.OpenElement(60, "hr");
                b.CloseElement();
                break;

            case MdTable table:
                b.OpenElement(70, "div");
                b.AddAttribute(71, "class", "wb-md-table-wrap");
                b.OpenElement(72, "table");
                b.AddAttribute(73, "class", "wb-md-table");
                b.OpenElement(74, "thead");
                b.OpenElement(75, "tr");
                foreach (var cell in table.Header)
                {
                    b.OpenElement(76, "th");
                    b.AddAttribute(77, "scope", "col");
                    RenderInlines(b, cell);
                    b.CloseElement();
                }

                b.CloseElement();
                b.CloseElement();
                b.OpenElement(78, "tbody");
                foreach (var row in table.Rows)
                {
                    b.OpenElement(79, "tr");
                    foreach (var cell in row)
                    {
                        b.OpenElement(80, "td");
                        RenderInlines(b, cell);
                        b.CloseElement();
                    }

                    b.CloseElement();
                }

                b.CloseElement();
                b.CloseElement();
                b.CloseElement();
                break;
        }
    }

    private static void RenderInlines(RenderTreeBuilder b, IEnumerable<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText text:
                    b.AddContent(100, text.Text);
                    break;

                case MdStrong strong:
                    b.OpenElement(101, "strong");
                    RenderInlines(b, strong.Content);
                    b.CloseElement();
                    break;

                case MdEmphasis em:
                    b.OpenElement(102, "em");
                    RenderInlines(b, em.Content);
                    b.CloseElement();
                    break;

                case MdCodeSpan code:
                    b.OpenElement(103, "code");
                    b.AddAttribute(104, "class", "wb-md-inline-code");
                    b.AddContent(105, code.Text);
                    b.CloseElement();
                    break;

                case MdLink { Kind: MdLinkKind.Term } term:
                    b.OpenComponent<Term>(110);
                    b.AddComponentParameter(111, nameof(Term.Key), term.Target);
                    b.AddComponentParameter(112, nameof(Term.ChildContent), (RenderFragment)(inner => RenderInlines(inner, term.Content)));
                    b.CloseComponent();
                    break;

                case MdLink link:
                    b.OpenElement(120, "a");
                    b.AddAttribute(121, "href", link.Kind switch
                    {
                        MdLinkKind.Help => "help/" + link.Target,
                        _ => link.Target,
                    });
                    b.AddAttribute(122, "class", "wb-md-link");
                    if (link.Kind == MdLinkKind.External)
                    {
                        b.AddAttribute(123, "target", "_blank");
                        b.AddAttribute(124, "rel", "noopener noreferrer");
                    }

                    RenderInlines(b, link.Content);
                    if (link.Kind == MdLinkKind.External)
                    {
                        b.OpenElement(125, "span");
                        b.AddAttribute(126, "class", "wb-visually-hidden");
                        b.AddContent(127, " (opens in a new tab)");
                        b.CloseElement();
                    }

                    b.CloseElement();
                    break;
            }
        }
    }
}
