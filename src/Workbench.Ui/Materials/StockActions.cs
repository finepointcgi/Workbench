using MudBlazor;
using Workbench.Contracts.Materials;

namespace Workbench.Ui.Materials;

/// <summary>Shows a <see cref="StockActionDialog"/> and sends the movement; true when something changed.</summary>
public static class StockActions
{
    public static async Task<bool> RunAsync(
        IDialogService dialogs, IInventoryService inventory, ISnackbar snackbar, StockAction mode, MaterialsOptionsDto options,
        InventoryBalanceDto? balance = null, PartDto? part = null)
    {
        var title = mode switch
        {
            StockAction.Issue => "Issue to a project",
            StockAction.Transfer => "Transfer stock",
            StockAction.Count => "Cycle count",
            _ => "Adjust inventory",
        };
        var parameters = new DialogParameters<StockActionDialog>
        {
            { d => d.Mode, mode },
            { d => d.Options, options },
            { d => d.Balance, balance },
            { d => d.Part, part },
        };
        var dialog = await dialogs.ShowAsync<StockActionDialog>(title, parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: { } request })
        {
            return false;
        }

        try
        {
            var message = request switch
            {
                IssueToProjectRequest r => $"Issued: {string.Join(", ", (await inventory.IssueToProjectAsync(r)).Select(t => t.Number))}",
                TransferRequest r => $"Transferred: {(await inventory.TransferAsync(r))[0].Number}",
                CycleCountRequest r => await inventory.CycleCountAsync(r) is { } t ? $"Count recorded: {t.Number} ({MaterialsFormat.Qty(t.Quantity)})" : "The count matches; nothing to adjust.",
                AdjustInventoryRequest r => $"Adjusted: {(await inventory.AdjustAsync(r)).Number}",
                _ => "",
            };
            snackbar.Add(message, Severity.Success);
            return true;
        }
        catch (Exception ex) when (MaterialsFormat.IsExpected(ex))
        {
            snackbar.Add(MaterialsFormat.Error(ex), Severity.Error);
            return false;
        }
    }
}
