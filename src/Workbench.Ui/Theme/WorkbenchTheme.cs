using MudBlazor;

namespace Workbench.Ui.Theme;

public static class WorkbenchTheme
{
    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1F5AA6",
            AppbarBackground = "#1F5AA6",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6EA8FE",
        },
    };
}
