using Workbench.Contracts.Odcs;

namespace Workbench.Ui.Odcs;

/// <summary>
/// What the expense line dialog returns: the line, plus any other charges from the same hotel folio the person chose to
/// claim as their own lines (parking, for example), all backed by the same receipt.
/// </summary>
public sealed record ExpenseLineDialogResult(SaveExpenseLineRequest Line, IReadOnlyList<SaveExpenseLineRequest> ExtraLines);
