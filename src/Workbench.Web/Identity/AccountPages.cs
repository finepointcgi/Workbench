using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Workbench.Web.Identity;

/// <summary>
/// The server-rendered account pages (sign-in, MFA, password links, account security): plain HTML forms outside the
/// Blazor circuit, because signing in and out needs real HTTP responses to set cookies. No scripts (CSP <c>script-src 'self'</c>).
/// </summary>
internal static class AccountPages
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public static IResult Page(string title, string body, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(Layout(title, body), "text/html; charset=utf-8", Encoding.UTF8, statusCode);

    /// <summary>A POST form to <paramref name="action"/> with the antiforgery token.</summary>
    public static string Form(AntiforgeryTokenSet tokens, string action, string inner, string? hiddenReturnUrl = null) =>
        $"""
        <form method="post" action="{E(action)}">
          <input type="hidden" name="{E(tokens.FormFieldName)}" value="{E(tokens.RequestToken)}" />
        {(hiddenReturnUrl is null ? "" : $"""  <input type="hidden" name="returnUrl" value="{E(hiddenReturnUrl)}" />""")}
        {inner}
        </form>
        """;

    public static string Error(string? message) => message is null ? "" : $"""<p class="error" role="alert">{E(message)}</p>""";

    public static string Notice(string? message) => message is null ? "" : $"""<p class="notice" role="status">{E(message)}</p>""";

    public static string Field(string label, string name, string type, string autocomplete, string? value = null, bool required = true, bool autofocus = false,
        string? hint = null, string? extra = null)
    {
        var id = "f-" + name;
        var hintId = hint is null ? null : id + "-hint";
        return $"""
            <label for="{id}">{E(label)}</label>
            <input id="{id}" name="{E(name)}" type="{type}" autocomplete="{autocomplete}"{(value is null ? "" : $" value=\"{E(value)}\"")}{(required ? " required" : "")}{(autofocus ? " autofocus" : "")}{(hintId is null ? "" : $" aria-describedby=\"{hintId}\"")}{(extra is null ? "" : " " + extra)} />
            {(hint is null ? "" : $"""<small id="{hintId}" class="hint">{E(hint)}</small>""")}
            """;
    }

    public static string WithReturnUrl(string path, string returnUrl) =>
        returnUrl == "/" ? path : $"{path}{(path.Contains('?') ? '&' : '?')}returnUrl={Uri.EscapeDataString(returnUrl)}";

    private static string Layout(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
        <meta name="robots" content="noindex" />
        <title>{{E(title)}} · Workbench</title>
        <style>
          :root { color-scheme: light dark; --bg: #f5f6f8; --card: #fff; --text: #1f2937; --muted: #6b7280; --line: #d1d5db; --accent: #1d4ed8; --accent-text: #fff;
                  --error-bg: #fef2f2; --error: #991b1b; --notice-bg: #eff6ff; --notice: #1e3a8a; --code-bg: #f3f4f6; }
          @media (prefers-color-scheme: dark) {
            :root { --bg: #111827; --card: #1f2937; --text: #f3f4f6; --muted: #9ca3af; --line: #4b5563; --accent: #60a5fa; --accent-text: #0b1220;
                    --error-bg: #450a0a; --error: #fecaca; --notice-bg: #172554; --notice: #dbeafe; --code-bg: #111827; }
          }
          * { box-sizing: border-box; }
          body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: var(--bg); margin: 0; padding: 48px 16px; color: var(--text); line-height: 1.5; }
          main { max-width: 440px; margin: 0 auto; background: var(--card); border-radius: 10px; padding: 28px 24px; box-shadow: 0 1px 3px rgba(0,0,0,.14); }
          .brand { font-weight: 700; letter-spacing: .02em; color: var(--accent); margin: 0 0 16px; font-size: .95rem; }
          h1 { font-size: 1.45rem; margin: 0 0 8px; } h2 { font-size: 1.05rem; margin: 28px 0 6px; padding-top: 18px; border-top: 1px solid var(--line); }
          p { margin: 0 0 14px; } .muted, .hint { color: var(--muted); } .hint { display: block; font-size: .85rem; margin: -6px 0 12px; }
          label { display: block; font-weight: 600; margin: 12px 0 4px; }
          input { display: block; width: 100%; padding: 10px 12px; border: 1px solid var(--line); border-radius: 6px; font: inherit; background: var(--card); color: var(--text); margin-bottom: 10px; }
          input:focus-visible, button:focus-visible, a:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
          button, .button { display: block; width: 100%; text-align: center; margin: 14px 0 6px; padding: 11px 14px; border-radius: 6px; font: inherit; font-weight: 600;
                            cursor: pointer; text-decoration: none; border: 1px solid var(--accent); background: var(--accent); color: var(--accent-text); }
          .secondary { background: transparent; color: var(--accent); }
          .danger { background: transparent; color: var(--error); border-color: var(--error); }
          a { color: var(--accent); }
          .error { background: var(--error-bg); color: var(--error); padding: 10px 12px; border-radius: 6px; }
          .notice { background: var(--notice-bg); color: var(--notice); padding: 10px 12px; border-radius: 6px; }
          .or { display: flex; align-items: center; gap: 10px; color: var(--muted); margin: 20px 0 8px; font-size: .9rem; }
          .or::before, .or::after { content: ""; flex: 1; border-top: 1px solid var(--line); }
          .qr { background: #fff; padding: 12px; border-radius: 8px; width: 220px; margin: 8px auto 12px; } .qr svg { display: block; width: 100%; height: auto; }
          code, .codes { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; background: var(--code-bg); border-radius: 4px; padding: 2px 6px; word-break: break-all; }
          .codes { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 16px; padding: 12px; list-style: none; margin: 0 0 14px; }
          .links { display: flex; flex-wrap: wrap; gap: 8px 18px; margin-top: 18px; font-size: .95rem; }
        </style></head><body><main>
        <p class="brand">Workbench</p>
        {{body}}
        </main></body></html>
        """;
}
