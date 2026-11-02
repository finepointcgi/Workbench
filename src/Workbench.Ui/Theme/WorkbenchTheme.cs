using MudBlazor;

namespace Workbench.Ui.Theme;

/// <summary>
/// The Workbench look, after Material 3: the color roles Material Theme Builder generates from the logo's orange seed (primary,
/// primary/secondary containers, surface and its tinted container levels, outline), the M3 type scale (regular-weight
/// headlines, medium-weight titles and labels), pill-shaped buttons and navigation, and M3's five elevation levels.
/// MudBlazor implements Material 2, so the shapes and tonal surfaces it can't express live in wwwroot/theme.css, which
/// derives the surface-container levels from these colors (so dark mode follows).
/// <para>
/// Every color meets WCAG 2.1 AA (Section 508): text and status colors are at least 4.5:1 on the surface and its
/// containers and against their contrast text when filled; icons and input outlines at least 3:1. Check new colors with
/// docs/CONVENTIONS.md ("Accessibility") before changing them.
/// </para>
/// </summary>
public static class WorkbenchTheme
{
    // Roboto where it's installed (Android, ChromeOS, most Linux), else the platform's UI font: no web font to download,
    // so it works offline and air-gapped.
    private static readonly string[] Font =
        ["\"Roboto Flex\"", "Roboto", "-apple-system", "BlinkMacSystemFont", "\"Segoe UI Variable Text\"", "\"Segoe UI\"", "\"Helvetica Neue\"", "Arial", "sans-serif"];

    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#C2410C",                 // brand orange, deepened from the logo for contrast; 4.9:1 on the surface
            PrimaryContrastText = "#FFFFFF",
            PrimaryDarken = "#9A3412",           // hover on filled buttons
            PrimaryLighten = "#FFDBCB",          // M3 primary container
            Secondary = "#AD1457",
            Tertiary = "#705575",
            Info = "#1565C0",
            Success = "#2E7D32",
            Warning = "#A35200",
            Error = "#BA1A1A",                   // M3 error
            Dark = "#44474E",

            Background = "#F9F9FF",              // M3 surface
            BackgroundGray = "#EDEDF4",
            Surface = "#F9F9FF",
            AppbarBackground = "#F9F9FF",
            AppbarText = "#191C20",
            DrawerBackground = "#F3F3FA",        // M3 surface container low
            DrawerText = "#44474E",
            DrawerIcon = "#44474E",

            TextPrimary = "#191C20",             // M3 on surface
            TextSecondary = "#44474E",           // M3 on surface variant; 8.9:1
            ActionDefault = "#44474E",
            ActionDisabled = "rgba(25,28,32,0.38)",
            ActionDisabledBackground = "rgba(25,28,32,0.12)",
            LinesDefault = "#C4C6D0",            // M3 outline variant
            LinesInputs = "#74777F",             // M3 outline; 4.3:1
            TableLines = "#E1E2EA",
            TableStriped = "rgba(194,65,12,0.04)",
            TableHover = "rgba(194,65,12,0.06)",
            Divider = "#C4C6D0",
            DividerLight = "#E1E2EA",
            OverlayDark = "rgba(25,28,32,0.40)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#FFB59A",                 // M3 primary (dark); 10.9:1 on the surface
            PrimaryContrastText = "#5A1A00",
            PrimaryDarken = "#FFCBB6",
            PrimaryLighten = "#7A2E00",          // M3 primary container (dark)
            Secondary = "#F48FB1",
            Tertiary = "#DDBCE0",
            Info = "#64B5F6",
            Success = "#66BB6A",
            Warning = "#FFB74D",
            Error = "#FFB4AB",                   // M3 error (dark)
            Dark = "#C4C6D0",
            // Light fills take dark text (white on these is under 3:1).
            SecondaryContrastText = "#121212",
            TertiaryContrastText = "#121212",
            InfoContrastText = "#121212",
            SuccessContrastText = "#121212",
            WarningContrastText = "#121212",
            ErrorContrastText = "#690005",

            Background = "#111318",              // M3 surface (dark)
            BackgroundGray = "#1D2024",
            Surface = "#111318",
            AppbarBackground = "#111318",
            AppbarText = "#E2E2E9",
            DrawerBackground = "#191C20",        // M3 surface container low (dark)
            DrawerText = "#C4C6D0",
            DrawerIcon = "#C4C6D0",

            TextPrimary = "#E2E2E9",
            TextSecondary = "#C4C6D0",
            ActionDefault = "#C4C6D0",
            LinesDefault = "#44474E",
            LinesInputs = "#8E9099",             // 5.8:1
            TableLines = "#2E3036",
            TableStriped = "rgba(255,181,154,0.04)",
            TableHover = "rgba(255,181,154,0.06)",
            Divider = "#44474E",
            DividerLight = "#2E3036",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",        // M3 medium shape; text fields and menus are narrowed in theme.css
            AppbarHeight = "64px",               // M3 small top app bar
            DrawerWidthLeft = "280px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font, FontSize = "0.9375rem", LineHeight = "1.5", LetterSpacing = "0.01em" },
            H1 = new H1Typography { FontFamily = Font, FontSize = "2.8125rem", FontWeight = "400", LineHeight = "1.16", LetterSpacing = "0" },        // display medium
            H2 = new H2Typography { FontFamily = Font, FontSize = "2.25rem", FontWeight = "400", LineHeight = "1.22", LetterSpacing = "0" },          // display small
            H3 = new H3Typography { FontFamily = Font, FontSize = "2rem", FontWeight = "400", LineHeight = "1.25", LetterSpacing = "0" },             // headline large
            H4 = new H4Typography { FontFamily = Font, FontSize = "1.75rem", FontWeight = "400", LineHeight = "1.29", LetterSpacing = "0" },          // headline medium: page titles
            H5 = new H5Typography { FontFamily = Font, FontSize = "1.5rem", FontWeight = "400", LineHeight = "1.33", LetterSpacing = "0" },           // headline small
            H6 = new H6Typography { FontFamily = Font, FontSize = "1.125rem", FontWeight = "500", LineHeight = "1.4", LetterSpacing = "0" },          // title: sections and cards
            Subtitle1 = new Subtitle1Typography { FontFamily = Font, FontSize = "1rem", FontWeight = "500", LineHeight = "1.5", LetterSpacing = "0.009em" },   // title medium
            Subtitle2 = new Subtitle2Typography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "500", LineHeight = "1.43", LetterSpacing = "0.007em" }, // title small
            Body1 = new Body1Typography { FontFamily = Font, FontSize = "0.9375rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0.02em" },
            Body2 = new Body2Typography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.43", LetterSpacing = "0.018em" },     // body medium
            Button = new ButtonTypography { FontFamily = Font, FontSize = "0.875rem", FontWeight = "500", LineHeight = "1.43", LetterSpacing = "0.007em", TextTransform = "none" }, // label large
            Caption = new CaptionTypography { FontFamily = Font, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.33", LetterSpacing = "0.033em" },  // body small
            Overline = new OverlineTypography { FontFamily = Font, FontSize = "0.75rem", FontWeight = "500", LineHeight = "1.33", LetterSpacing = "0.04em", TextTransform = "none" }, // label medium
        },
        Shadows = new Shadow
        {
            // M3's five elevation levels (key + ambient shadow), spread across MudBlazor's 0–25 scale.
            Elevation =
            [
                "none",
                Level1, Level2, Level2,
                Level3, Level3, Level3,
                Level4, Level4, Level4, Level4, Level4,
                Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5, Level5,
            ],
        },
    };

    private const string Level1 = "0 1px 2px 0 rgba(0,0,0,0.30), 0 1px 3px 1px rgba(0,0,0,0.15)";
    private const string Level2 = "0 1px 2px 0 rgba(0,0,0,0.30), 0 2px 6px 2px rgba(0,0,0,0.15)";
    private const string Level3 = "0 1px 3px 0 rgba(0,0,0,0.30), 0 4px 8px 3px rgba(0,0,0,0.15)";
    private const string Level4 = "0 2px 3px 0 rgba(0,0,0,0.30), 0 6px 10px 4px rgba(0,0,0,0.15)";
    private const string Level5 = "0 4px 4px 0 rgba(0,0,0,0.30), 0 8px 12px 6px rgba(0,0,0,0.15)";
}
