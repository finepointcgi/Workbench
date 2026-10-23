namespace Workbench.Application.Forms;

/// <summary>
/// Lets one form response add one row to one sheet through the normal item pipeline (activity, live updates, the sheet's
/// rules) without the person filling in the form having edit access. <see cref="Allow"/> covers only the awaited flow
/// that called it (an <see cref="AsyncLocal{T}"/>, so other requests and other handlers on the same Blazor circuit never
/// see it), and <see cref="TryUse"/> spends it: the first item create on that sheet, nothing else.
/// </summary>
public sealed class FormSubmissionGrant
{
    private readonly AsyncLocal<Slot?> _current = new();

    /// <summary>Grants one create on <paramref name="sheetId"/> until the returned scope is disposed.</summary>
    public IDisposable Allow(Guid sheetId)
    {
        var slot = new Slot(sheetId);
        _current.Value = slot;
        return new Release(this, slot);
    }

    /// <summary>True (once) when a form response may create an item on <paramref name="sheetId"/> right now.</summary>
    public bool TryUse(Guid sheetId)
    {
        if (_current.Value is not { Used: false } slot || slot.SheetId != sheetId)
        {
            return false;
        }

        slot.Used = true;
        return true;
    }

    private sealed class Slot(Guid sheetId)
    {
        public Guid SheetId { get; } = sheetId;

        public bool Used { get; set; }
    }

    private sealed class Release(FormSubmissionGrant owner, Slot slot) : IDisposable
    {
        public void Dispose()
        {
            slot.Used = true;
            if (owner._current.Value == slot)
            {
                owner._current.Value = null;
            }
        }
    }
}
