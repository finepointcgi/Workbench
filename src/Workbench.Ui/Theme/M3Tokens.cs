using System.Text;

namespace Workbench.Ui.Theme;

/// <summary>
/// The Material 3 design tokens, defined once. The color roles are the Material Theme Builder output for the brand seed
/// #C2410C (the tonal palettes: primary at tone 40 light / 80 dark, containers at 90 / 30, surfaces at 98 / 6 with the
/// five container levels). They are emitted as <c>--md-sys-color-*</c> CSS variables (<see cref="ToCss"/>; the
/// ThemeTokens component for the app, wwwroot/m3-tokens.css for the public pages) and mapped into the MudBlazor palette by
/// <see cref="WorkbenchTheme"/>, so a component picks up the same color whichever way it is styled. wwwroot/theme.css
/// holds the shape, elevation, motion and state-layer tokens and the component styles built on them.
/// <para>
/// Pairs meet WCAG 2.1 AA: every "on-x" role is at least 4.5:1 on its "x" role and on the surfaces, and outline is at
/// least 3:1 on surface (docs/ux/design-system.md). Change the seed or a role only after re-checking the pairs.
/// </para>
/// </summary>
public static class M3Tokens
{
    /// <summary>The brand seed the tonal palettes are generated from.</summary>
    public const string Seed = "#B53C0A";

    /// <summary>Light scheme, role name (kebab-case, no prefix) to hex.</summary>
    public static IReadOnlyDictionary<string, string> Light { get; } = new Dictionary<string, string>
    {
        ["primary"] = "#B53C0A", ["on-primary"] = "#FFFFFF", ["primary-container"] = "#FFDBCB", ["on-primary-container"] = "#380D00",
        ["secondary"] = "#565F71", ["on-secondary"] = "#FFFFFF", ["secondary-container"] = "#DAE2F9", ["on-secondary-container"] = "#131C2B",
        ["tertiary"] = "#705575", ["on-tertiary"] = "#FFFFFF", ["tertiary-container"] = "#FAD8FD", ["on-tertiary-container"] = "#28132E",
        ["error"] = "#BA1A1A", ["on-error"] = "#FFFFFF", ["error-container"] = "#FFDAD6", ["on-error-container"] = "#410002",
        ["surface"] = "#F9F9FF", ["on-surface"] = "#191C20", ["surface-variant"] = "#E0E2EC", ["on-surface-variant"] = "#44474E",
        ["surface-dim"] = "#D9D9E0", ["surface-bright"] = "#F9F9FF",
        ["surface-container-lowest"] = "#FFFFFF", ["surface-container-low"] = "#F3F3FA", ["surface-container"] = "#EDEDF4",
        ["surface-container-high"] = "#E7E8EE", ["surface-container-highest"] = "#E2E2E9",
        ["outline"] = "#74777F", ["outline-variant"] = "#C4C6D0",
        ["inverse-surface"] = "#2E3135", ["inverse-on-surface"] = "#F0F0F7", ["inverse-primary"] = "#FFB59A",
        ["scrim"] = "#000000",
        // Extended colors (M3 "custom colors"): status, harmonised with the orange seed.
        ["success"] = "#2E7D32", ["on-success"] = "#FFFFFF",
        ["warning"] = "#A35200", ["on-warning"] = "#FFFFFF",
        ["info"] = "#1565C0", ["on-info"] = "#FFFFFF",
    };

    /// <summary>Dark scheme, same roles.</summary>
    public static IReadOnlyDictionary<string, string> Dark { get; } = new Dictionary<string, string>
    {
        ["primary"] = "#FFB59A", ["on-primary"] = "#5A1A00", ["primary-container"] = "#7A2E00", ["on-primary-container"] = "#FFDBCB",
        ["secondary"] = "#BEC6DC", ["on-secondary"] = "#283141", ["secondary-container"] = "#3E4759", ["on-secondary-container"] = "#DAE2F9",
        ["tertiary"] = "#DDBCE0", ["on-tertiary"] = "#3F2844", ["tertiary-container"] = "#573E5C", ["on-tertiary-container"] = "#FAD8FD",
        ["error"] = "#FFB4AB", ["on-error"] = "#690005", ["error-container"] = "#93000A", ["on-error-container"] = "#FFDAD6",
        ["surface"] = "#111318", ["on-surface"] = "#E2E2E9", ["surface-variant"] = "#44474E", ["on-surface-variant"] = "#C4C6D0",
        ["surface-dim"] = "#111318", ["surface-bright"] = "#37393E",
        ["surface-container-lowest"] = "#0C0E13", ["surface-container-low"] = "#191C20", ["surface-container"] = "#1D2024",
        ["surface-container-high"] = "#282A2F", ["surface-container-highest"] = "#33353A",
        ["outline"] = "#8E9099", ["outline-variant"] = "#44474E",
        ["inverse-surface"] = "#E2E2E9", ["inverse-on-surface"] = "#2E3135", ["inverse-primary"] = "#B53C0A",
        ["scrim"] = "#000000",
        ["success"] = "#66BB6A", ["on-success"] = "#121212",
        ["warning"] = "#FFB74D", ["on-warning"] = "#121212",
        ["info"] = "#64B5F6", ["on-info"] = "#121212",
    };

    public static IReadOnlyDictionary<string, string> Scheme(bool dark) => dark ? Dark : Light;

    /// <summary>The declarations of one scheme: <c>--md-sys-color-primary: #C2410C;</c> per role.</summary>
    public static string Declarations(bool dark, string indent = "")
    {
        var sb = new StringBuilder();
        foreach (var (role, hex) in Scheme(dark))
        {
            sb.Append(indent).Append("--md-sys-color-").Append(role).Append(": ").Append(hex).Append(";\n");
        }

        return sb.ToString();
    }

    /// <summary>A <c>:root</c> rule for one scheme; the app emits the one for the current mode.</summary>
    public static string ToCss(bool dark) => ":root {\n" + Declarations(dark, "    ") + "}\n";

    /// <summary>
    /// The static stylesheet for pages without a theme switch (wwwroot/m3-tokens.css): light, and dark under the
    /// operating system's preference. A test keeps the file equal to this.
    /// </summary>
    public static string ToStaticStylesheet() =>
        "/* Generated from Workbench.Ui.Theme.M3Tokens (a test keeps this file equal to it). Material 3 color roles for\n" +
        "   pages that follow the operating system's light/dark setting; the app itself emits the same variables per mode. */\n" +
        ToCss(dark: false) +
        "@media (prefers-color-scheme: dark) {\n" +
        "    :root {\n" + Declarations(dark: true, "        ") + "    }\n" +
        "}\n";
}
