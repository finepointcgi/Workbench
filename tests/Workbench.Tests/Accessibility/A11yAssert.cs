using System.Text;
using AngleSharp.Dom;
using Bunit;

namespace Workbench.Tests.Accessibility;

/// <summary>
/// Accessibility guardrails for bUnit tests (WCAG 2.1 AA, Section 508). Call <see cref="Check"/> on a rendered component
/// to fail when a control has no accessible name (icon buttons without aria-label, inputs without a label), when one
/// interactive control is nested in another, when a data table has no headers or no name, or when an image has no
/// alternative. It is a cheap first line; the axe-core scan in tests/a11y checks whole pages in a browser.
/// </summary>
public static class A11yAssert
{
    private const string Interactive =
        "button, a[href], input, select, textarea, [role=button], [role=link], [role=menuitem], [role=option], [role=tab], " +
        "[role=checkbox], [role=switch], [role=combobox], [role=radio], [role=slider]";

    private const string ChildrenPresentational =
        "button, [role=button], [role=link], [role=menuitem], [role=option], [role=tab], [role=checkbox], [role=switch], [role=radio]";

    /// <summary>Asserts the rendered markup has no accessibility problems this helper can find.</summary>
    public static void Check<T>(IRenderedComponent<T> component) where T : Microsoft.AspNetCore.Components.IComponent =>
        Check(component.Nodes);

    /// <summary>Asserts the nodes have no accessibility problems this helper can find.</summary>
    public static void Check(INodeList nodes)
    {
        var problems = Find(nodes);
        Assert.True(problems.Count == 0, "Accessibility problems:\n" + string.Join("\n", problems));
    }

    /// <summary>The problems found, one line each (empty when there are none).</summary>
    public static IReadOnlyList<string> Find(INodeList nodes)
    {
        var problems = new List<string>();
        var roots = nodes.OfType<IElement>().ToList();
        IEnumerable<IElement> All(string selector) =>
            roots.SelectMany(r => r.Matches(selector) ? new[] { r }.Concat(r.QuerySelectorAll(selector)) : r.QuerySelectorAll(selector)).Distinct();

        foreach (var element in All(Interactive).Where(e => !IsHidden(e)))
        {
            if (element.LocalName == "input" && element.GetAttribute("type") is "hidden")
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(Name(element)))
            {
                problems.Add($"No accessible name: {Describe(element)}");
            }

            // Controls whose content is presentational (axe nested-interactive); a grid or listbox may hold controls.
            if (element.Matches(ChildrenPresentational) && IsFocusable(element) && element.QuerySelectorAll("*").FirstOrDefault(d => IsFocusable(d) && !IsHidden(d)) is { } inner)
            {
                problems.Add($"Interactive control nested in another: {Describe(inner)} inside {Describe(element)}");
            }
        }

        foreach (var img in All("img").Where(e => !IsHidden(e) && !e.HasAttribute("alt") && string.IsNullOrWhiteSpace(e.GetAttribute("aria-label"))))
        {
            problems.Add($"Image without alt: {Describe(img)}");
        }

        foreach (var table in All("table").Where(t => !IsHidden(t) && t.GetAttribute("role") is not ("presentation" or "none")))
        {
            if (table.QuerySelector("th") is null && table.QuerySelector("[role=columnheader], [role=rowheader]") is null)
            {
                continue; // a layout table; MudBlazor uses none, and data tables need headers (below)
            }

            var named = table.QuerySelector("caption") is not null || !string.IsNullOrWhiteSpace(table.GetAttribute("aria-label"))
                || table.HasAttribute("aria-labelledby");
            if (!named)
            {
                problems.Add($"Data table without a caption or aria-label: {Describe(table)}");
            }
        }

        foreach (var grid in All("[role=grid], [role=table], [role=treegrid]").Where(g => !IsHidden(g)))
        {
            if (string.IsNullOrWhiteSpace(Name(grid)))
            {
                problems.Add($"Grid without a name: {Describe(grid)}");
            }
        }

        return problems;
    }

    /// <summary>A simplified accessible name: aria-labelledby, aria-label, a label, alt/value/title, then the text.</summary>
    public static string Name(IElement element)
    {
        if (element.GetAttribute("aria-labelledby") is { Length: > 0 } ids)
        {
            var document = element.Owner;
            var text = string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => document?.GetElementById(id) ?? FindInTree(element, id))
                .OfType<IElement>()
                .Select(Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        if (element.GetAttribute("aria-label") is { } label && !string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        if (element.LocalName is "input" or "select" or "textarea")
        {
            if (element.GetAttribute("type") is "button" or "submit" or "reset" && element.GetAttribute("value") is { Length: > 0 } value)
            {
                return value;
            }

            var id = element.GetAttribute("id");
            var root = Root(element);
            var forLabel = id is null ? null : root.QuerySelectorAll("label").FirstOrDefault(l => l.GetAttribute("for") == id);
            var wrapping = element.Closest("label");
            var text = forLabel is not null ? Text(forLabel) : wrapping is not null ? Text(wrapping) : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }

            return element.GetAttribute("title")?.Trim() ?? "";
        }

        var content = Text(element).Trim();
        return content.Length > 0 ? content : element.GetAttribute("title")?.Trim() ?? "";
    }

    private static string Text(IElement element)
    {
        var sb = new StringBuilder();
        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText t:
                        sb.Append(t.Data).Append(' ');
                        break;
                    case IElement e when e.GetAttribute("aria-hidden") == "true" || e.HasAttribute("hidden"):
                        break;
                    case IElement e when e.GetAttribute("aria-label") is { Length: > 0 } l:
                        sb.Append(l).Append(' ');
                        break;
                    case IElement e when e.LocalName == "img":
                        sb.Append(e.GetAttribute("alt")).Append(' ');
                        break;
                    case IElement e when e.LocalName is "style" or "script":
                        break;
                    case IElement e:
                        Walk(e);
                        break;
                }
            }
        }

        Walk(element);
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsHidden(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (e.GetAttribute("aria-hidden") == "true" || e.HasAttribute("hidden") || e.HasAttribute("inert"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFocusable(IElement e)
    {
        if (e.HasAttribute("disabled") || e.GetAttribute("tabindex") is { } t && int.TryParse(t, out var i) && i < 0)
        {
            return false;
        }

        return e.LocalName switch
        {
            "button" or "select" or "textarea" => true,
            "input" => e.GetAttribute("type") != "hidden",
            "a" => e.HasAttribute("href"),
            _ => e.GetAttribute("tabindex") is { } ti && int.TryParse(ti, out var n) && n >= 0,
        };
    }

    private static IElement Root(IElement element)
    {
        var e = element;
        while (e.ParentElement is { } p)
        {
            e = p;
        }

        return e;
    }

    private static IElement? FindInTree(IElement element, string id) =>
        Root(element).QuerySelectorAll("[id]").FirstOrDefault(e => e.GetAttribute("id") == id);

    private static string Describe(IElement element)
    {
        var html = element.OuterHtml;
        var end = html.IndexOf('>');
        return end > 0 && end < 200 ? html[..(end + 1)] : html[..Math.Min(html.Length, 200)];
    }
}
