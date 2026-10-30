using MudBlazor;

namespace Workbench.Ui.Theme;

/// <summary>
/// The Workbench look, after Material 3: the color roles from <see cref="M3Tokens"/> (generated from the brand seed
/// #415F91), mapped into MudBlazor's palette; the M3 type scale (regular-weight
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
        PaletteLight = LightPalette(),
        PaletteDark = DarkPalette(),
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

    // The MudBlazor palette is filled from the M3 color roles (M3Tokens), so a Mud component and a CSS rule that reads
    // --md-sys-color-* agree. Mud's primary-lighten is the primary container and its primary-darken the hover shade.
    private static PaletteLight LightPalette()
    {
        var c = M3Tokens.Light;
        return new PaletteLight
        {
            Primary = c["primary"], PrimaryContrastText = c["on-primary"], PrimaryDarken = "#35507E", PrimaryLighten = c["primary-container"],
            Secondary = c["secondary"], SecondaryContrastText = c["on-secondary"],
            Tertiary = c["tertiary"], TertiaryContrastText = c["on-tertiary"],
            Info = c["info"], InfoContrastText = c["on-info"],
            Success = c["success"], SuccessContrastText = c["on-success"],
            Warning = c["warning"], WarningContrastText = c["on-warning"],
            Error = c["error"], ErrorContrastText = c["on-error"],
            Dark = c["on-surface-variant"],

            Background = c["surface"], BackgroundGray = c["surface-container"], Surface = c["surface"],
            AppbarBackground = c["surface"], AppbarText = c["on-surface"],
            DrawerBackground = c["surface-container-low"], DrawerText = c["on-surface-variant"], DrawerIcon = c["on-surface-variant"],

            TextPrimary = c["on-surface"], TextSecondary = c["on-surface-variant"],
            ActionDefault = c["on-surface-variant"],
            ActionDisabled = "rgba(25,28,32,0.38)", ActionDisabledBackground = "rgba(25,28,32,0.12)",
            LinesDefault = c["outline-variant"], LinesInputs = c["outline"],
            TableLines = c["surface-container-highest"], TableStriped = "rgba(65,95,145,0.04)", TableHover = "rgba(65,95,145,0.06)",
            Divider = c["outline-variant"], DividerLight = c["surface-container-highest"],
            OverlayDark = "rgba(0,0,0,0.40)",
        };
    }

    private static PaletteDark DarkPalette()
    {
        var c = M3Tokens.Dark;
        return new PaletteDark
        {
            Primary = c["primary"], PrimaryContrastText = c["on-primary"], PrimaryDarken = "#C2D6FF", PrimaryLighten = c["primary-container"],
            Secondary = c["secondary"], SecondaryContrastText = c["on-secondary"],
            Tertiary = c["tertiary"], TertiaryContrastText = c["on-tertiary"],
            Info = c["info"], InfoContrastText = c["on-info"],
            Success = c["success"], SuccessContrastText = c["on-success"],
            Warning = c["warning"], WarningContrastText = c["on-warning"],
            Error = c["error"], ErrorContrastText = c["on-error"],
            Dark = c["on-surface-variant"],

            Background = c["surface"], BackgroundGray = c["surface-container"], Surface = c["surface"],
            AppbarBackground = c["surface"], AppbarText = c["on-surface"],
            DrawerBackground = c["surface-container-low"], DrawerText = c["on-surface-variant"], DrawerIcon = c["on-surface-variant"],

            TextPrimary = c["on-surface"], TextSecondary = c["on-surface-variant"],
            ActionDefault = c["on-surface-variant"],
            LinesDefault = c["outline-variant"], LinesInputs = c["outline"],
            TableLines = "#2E3036", TableStriped = "rgba(170,199,255,0.04)", TableHover = "rgba(170,199,255,0.06)",
            Divider = c["outline-variant"], DividerLight = "#2E3036",
        };
    }

    private const string Level1 = "0 1px 2px 0 rgba(0,0,0,0.30), 0 1px 3px 1px rgba(0,0,0,0.15)";
    private const string Level2 = "0 1px 2px 0 rgba(0,0,0,0.30), 0 2px 6px 2px rgba(0,0,0,0.15)";
    private const string Level3 = "0 1px 3px 0 rgba(0,0,0,0.30), 0 4px 8px 3px rgba(0,0,0,0.15)";
    private const string Level4 = "0 2px 3px 0 rgba(0,0,0,0.30), 0 6px 10px 4px rgba(0,0,0,0.15)";
    private const string Level5 = "0 4px 4px 0 rgba(0,0,0,0.30), 0 8px 12px 6px rgba(0,0,0,0.15)";
}
