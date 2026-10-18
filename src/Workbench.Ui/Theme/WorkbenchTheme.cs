using MudBlazor;

namespace Workbench.Ui.Theme;

/// <summary>
/// The Workbench palette. Every color meets WCAG 2.1 AA (Section 508): status colors are at least 4.5:1 as text on the
/// page and surface and against their contrast text when filled (chips, buttons, badges, alerts), and secondary text and
/// input borders are darkened from MudBlazor's defaults (4.5:1 and 3:1). Check new colors with docs/CONVENTIONS.md
/// ("Accessibility") before changing them.
/// </summary>
public static class WorkbenchTheme
{
    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1F5AA6",
            AppbarBackground = "#1F5AA6",
            Secondary = "#AD1457",
            Tertiary = "#00796B",
            Info = "#1565C0",
            Success = "#2E7D32",
            Warning = "#A35200",
            Error = "#C62828",
            Dark = "#424242",
            TextSecondary = "rgba(0,0,0,0.62)",
            ActionDefault = "rgba(0,0,0,0.62)",
            LinesInputs = "rgba(0,0,0,0.5)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#9BBEFF",
            Secondary = "#F48FB1",
            Tertiary = "#4DB6AC",
            Info = "#64B5F6",
            Success = "#66BB6A",
            Warning = "#FFB74D",
            Error = "#FF8A80",
            // Light fills take dark text (white on these is under 3:1).
            PrimaryContrastText = "#121212",
            SecondaryContrastText = "#121212",
            TertiaryContrastText = "#121212",
            InfoContrastText = "#121212",
            SuccessContrastText = "#121212",
            WarningContrastText = "#121212",
            ErrorContrastText = "#121212",
            TextPrimary = "rgba(255,255,255,0.87)",
            TextSecondary = "rgba(255,255,255,0.70)",
            ActionDefault = "rgba(255,255,255,0.70)",
            LinesInputs = "rgba(255,255,255,0.5)",
            DrawerText = "rgba(255,255,255,0.87)",
            DrawerIcon = "rgba(255,255,255,0.70)",
            AppbarText = "rgba(255,255,255,0.87)",
        },
    };
}
