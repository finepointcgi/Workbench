using MudBlazor;

namespace Workbench.Ui.Theme;

/// <summary>
/// The Workbench look: a light app bar and drawer on a cool gray page, white surfaces with soft shadows, the system UI
/// font (no web font to download, so it works offline and air-gapped), sentence-case buttons and a compact type scale.
/// Surface styling the theme can't express lives in wwwroot/theme.css.
/// <para>
/// Every color meets WCAG 2.1 AA (Section 508): text and status colors are at least 4.5:1 on the page, the surface and
/// against their contrast text when filled; icons and input borders at least 3:1. Check new colors with
/// docs/CONVENTIONS.md ("Accessibility") before changing them.
/// </para>
/// </summary>
public static class WorkbenchTheme
{
    private static readonly string[] Font =
        ["-apple-system", "BlinkMacSystemFont", "\"Segoe UI Variable Text\"", "\"Segoe UI\"", "Inter", "Roboto", "\"Helvetica Neue\"", "Arial", "sans-serif"];

    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2952CC",                 // 6.6:1 on white
            PrimaryDarken = "#1E3FA8",
            PrimaryLighten = "#EAF0FE",
            Secondary = "#AD1457",
            Tertiary = "#00796B",
            Info = "#1565C0",
            Success = "#2E7D32",
            Warning = "#A35200",
            Error = "#C62828",
            Dark = "#344054",

            Background = "#F5F7FA",
            BackgroundGray = "#EEF1F5",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1D2433",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#344054",
            DrawerIcon = "#667085",              // 5:1 on white

            TextPrimary = "#101828",
            TextSecondary = "#475467",           // 7.2:1 on the page
            ActionDefault = "#475467",
            ActionDisabled = "rgba(16,24,40,0.38)",
            ActionDisabledBackground = "rgba(16,24,40,0.10)",
            LinesDefault = "#E4E7EC",
            LinesInputs = "#868FA0",             // 3.3:1 on white
            TableLines = "#EAECF0",
            TableStriped = "#F9FAFB",
            TableHover = "#F4F6FB",
            Divider = "#E4E7EC",
            DividerLight = "#F2F4F7",
            OverlayDark = "rgba(16,24,40,0.45)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#8FB3FF",                 // 8.3:1 on the surface
            PrimaryDarken = "#B5CCFF",
            PrimaryLighten = "#1E2A44",
            Secondary = "#F48FB1",
            Tertiary = "#4DB6AC",
            Info = "#64B5F6",
            Success = "#66BB6A",
            Warning = "#FFB74D",
            Error = "#FF8A80",
            Dark = "#C9CED6",
            // Light fills take dark text (white on these is under 3:1).
            PrimaryContrastText = "#121212",
            SecondaryContrastText = "#121212",
            TertiaryContrastText = "#121212",
            InfoContrastText = "#121212",
            SuccessContrastText = "#121212",
            WarningContrastText = "#121212",
            ErrorContrastText = "#121212",

            Background = "#0E1117",
            BackgroundGray = "#12161D",
            Surface = "#161B22",
            AppbarBackground = "#161B22",
            AppbarText = "rgba(255,255,255,0.90)",
            DrawerBackground = "#12161D",
            DrawerText = "rgba(255,255,255,0.87)",
            DrawerIcon = "rgba(255,255,255,0.70)",

            TextPrimary = "rgba(255,255,255,0.90)",
            TextSecondary = "#A3ABB8",           // 7.5:1 on the surface
            ActionDefault = "#A3ABB8",
            LinesDefault = "rgba(255,255,255,0.10)",
            LinesInputs = "#737B88",             // 4:1 on the surface
            TableLines = "rgba(255,255,255,0.08)",
            TableStriped = "rgba(255,255,255,0.02)",
            TableHover = "rgba(255,255,255,0.04)",
            Divider = "rgba(255,255,255,0.10)",
            DividerLight = "rgba(255,255,255,0.06)",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "56px",
            DrawerWidthLeft = "256px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font, FontSize = "0.9375rem", LineHeight = "1.5", LetterSpacing = "normal" },
            H1 = new H1Typography { FontFamily = Font, FontSize = "2.25rem", FontWeight = "700", LineHeight = "1.2", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontFamily = Font, FontSize = "1.875rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H3 = new H3Typography { FontFamily = Font, FontSize = "1.625rem", FontWeight = "650", LineHeight = "1.25", LetterSpacing = "-0.015em" },
            // Page titles (rendered as <h1>).
            H4 = new H4Typography { FontFamily = Font, FontSize = "1.625rem", FontWeight = "650", LineHeight = "1.25", LetterSpacing = "-0.015em" },
            H5 = new H5Typography { FontFamily = Font, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "-0.01em" },
            // Section and card titles.
            H6 = new H6Typography { FontFamily = Font, FontSize = "1.0625rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-0.005em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Font, FontSize = "0.9375rem", FontWeight = "600", LineHeight = "1.45", LetterSpacing = "normal" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.45", LetterSpacing = "normal" },
            Body1 = new Body1Typography { FontFamily = Font, FontSize = "0.9375rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "normal" },
            Body2 = new Body2Typography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
            Button = new ButtonTypography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "normal", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Font, FontSize = "0.8125rem", FontWeight = "400", LineHeight = "1.45", LetterSpacing = "normal" },
            Overline = new OverlineTypography { FontFamily = Font, FontSize = "0.75rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0.06em", TextTransform = "uppercase" },
        },
        Shadows = new Shadow
        {
            // Soft, low shadows: a hairline for resting surfaces, a lift for menus and dialogs.
            Elevation =
            [
                "none",
                "0 1px 2px rgba(16,24,40,0.06), 0 1px 3px rgba(16,24,40,0.08)",
                "0 1px 2px rgba(16,24,40,0.06), 0 2px 4px rgba(16,24,40,0.08)",
                "0 2px 4px rgba(16,24,40,0.06), 0 4px 8px rgba(16,24,40,0.08)",
                "0 4px 6px rgba(16,24,40,0.05), 0 6px 12px rgba(16,24,40,0.08)",
                "0 4px 8px rgba(16,24,40,0.05), 0 8px 16px rgba(16,24,40,0.09)",
                "0 6px 10px rgba(16,24,40,0.05), 0 10px 20px rgba(16,24,40,0.10)",
                "0 8px 12px rgba(16,24,40,0.05), 0 12px 24px rgba(16,24,40,0.10)",
                "0 8px 16px rgba(16,24,40,0.06), 0 16px 32px rgba(16,24,40,0.11)",
                "0 10px 18px rgba(16,24,40,0.06), 0 18px 36px rgba(16,24,40,0.12)",
                "0 12px 20px rgba(16,24,40,0.06), 0 20px 40px rgba(16,24,40,0.12)",
                "0 12px 22px rgba(16,24,40,0.07), 0 22px 44px rgba(16,24,40,0.13)",
                "0 14px 24px rgba(16,24,40,0.07), 0 24px 48px rgba(16,24,40,0.13)",
                "0 14px 26px rgba(16,24,40,0.07), 0 26px 52px rgba(16,24,40,0.14)",
                "0 16px 28px rgba(16,24,40,0.08), 0 28px 56px rgba(16,24,40,0.14)",
                "0 16px 30px rgba(16,24,40,0.08), 0 30px 60px rgba(16,24,40,0.15)",
                "0 18px 32px rgba(16,24,40,0.08), 0 32px 64px rgba(16,24,40,0.15)",
                "0 18px 34px rgba(16,24,40,0.09), 0 34px 68px rgba(16,24,40,0.16)",
                "0 20px 36px rgba(16,24,40,0.09), 0 36px 72px rgba(16,24,40,0.16)",
                "0 20px 38px rgba(16,24,40,0.09), 0 38px 76px rgba(16,24,40,0.17)",
                "0 22px 40px rgba(16,24,40,0.10), 0 40px 80px rgba(16,24,40,0.17)",
                "0 22px 42px rgba(16,24,40,0.10), 0 42px 84px rgba(16,24,40,0.18)",
                "0 24px 44px rgba(16,24,40,0.10), 0 44px 88px rgba(16,24,40,0.18)",
                "0 24px 46px rgba(16,24,40,0.11), 0 46px 92px rgba(16,24,40,0.19)",
                "0 24px 48px rgba(16,24,40,0.12), 0 48px 96px rgba(16,24,40,0.20)",
                "0 26px 50px rgba(16,24,40,0.12), 0 50px 100px rgba(16,24,40,0.20)",
            ],
        },
    };
}
