using MudBlazor;
using Workbench.Contracts.Compliance;

namespace Workbench.Ui.Compliance;

internal static class CmmcFormat
{
    public static string Status(CmmcStatusDto s) => s switch
    {
        CmmcStatusDto.NotAssessed => "Not assessed",
        CmmcStatusDto.Implemented => "Implemented",
        CmmcStatusDto.NotImplemented => "Not implemented",
        _ => "Not applicable",
    };

    public static Color StatusColor(CmmcStatusDto s) => s switch
    {
        CmmcStatusDto.Implemented => Color.Success,
        CmmcStatusDto.NotImplemented => Color.Error,
        CmmcStatusDto.NotApplicable => Color.Info,
        _ => Color.Default,
    };
}
