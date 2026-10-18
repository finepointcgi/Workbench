using Microsoft.AspNetCore.Components;
using MudBlazor;
using Workbench.Contracts.Support;

namespace Workbench.Ui.Support;

/// <summary>Opens the "Report a problem" dialog from anywhere, about the page the user is on.</summary>
public static class SupportDialogs
{
    public static async Task<SupportRequestDto?> ReportAsync(IDialogService dialogs, NavigationManager navigation, bool fromError = false, string? pagePath = null)
    {
        var dialog = await ShowAsync(dialogs, navigation, fromError, pagePath);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: SupportRequestDto sent } ? sent : null;
    }

    /// <summary>Opens the dialog without waiting for it to close (for menus, which stay open until their handler returns).</summary>
    public static Task<IDialogReference> ShowAsync(IDialogService dialogs, NavigationManager navigation, bool fromError = false, string? pagePath = null)
    {
        var parameters = new DialogParameters<ReportProblemDialog>
        {
            { d => d.PagePath, pagePath ?? navigation.ToBaseRelativePath(navigation.Uri) },
            { d => d.FromError, fromError },
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true };
        return dialogs.ShowAsync<ReportProblemDialog>(fromError ? "Report this problem" : "Report a problem", parameters, options);
    }
}
