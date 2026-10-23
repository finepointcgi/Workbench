namespace Workbench.Ui.Grid;

/// <summary>
/// Undo and redo for the grid (Ctrl+Z, Ctrl+Y): each change records how to reverse and repeat itself. Undoing a deleted
/// row creates it again with a new id, so later entries find rows through <see cref="Resolve"/>. Kept per grid, in memory.
/// </summary>
public sealed class GridHistory
{
    public const int Limit = 100;

    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private readonly Dictionary<Guid, Guid> _ids = [];

    public sealed record Entry(string Label, Func<Task> Undo, Func<Task> Redo);

    /// <summary>True while an undo or redo runs, so the changes it makes aren't recorded again.</summary>
    public bool Replaying { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Last?.Value.Label;

    public string? RedoLabel => _redo.TryPeek(out var entry) ? entry.Label : null;

    public void Record(string label, Func<Task> undo, Func<Task> redo)
    {
        if (Replaying)
        {
            return;
        }

        _undo.AddLast(new Entry(label, undo, redo));
        if (_undo.Count > Limit)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    /// <summary>Reverses the last change; returns its label, or null when there's nothing to undo.</summary>
    public async Task<string?> UndoAsync()
    {
        if (_undo.Last is not { } node || Replaying)
        {
            return null;
        }

        _undo.RemoveLast();
        await RunAsync(node.Value.Undo);
        _redo.Push(node.Value);
        return node.Value.Label;
    }

    public async Task<string?> RedoAsync()
    {
        if (!_redo.TryPop(out var entry) || Replaying)
        {
            return null;
        }

        await RunAsync(entry.Redo);
        _undo.AddLast(entry);
        return entry.Label;
    }

    /// <summary>The row's id now (a row recreated by undo or redo has a new one).</summary>
    public Guid Resolve(Guid id)
    {
        for (var hops = 0; _ids.TryGetValue(id, out var next) && hops < 1000; hops++)
        {
            id = next;
        }

        return id;
    }

    public void Remap(Guid oldId, Guid newId)
    {
        if (oldId != newId)
        {
            _ids[oldId] = newId;
        }
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private async Task RunAsync(Func<Task> action)
    {
        Replaying = true;
        try
        {
            await action();
        }
        finally
        {
            Replaying = false;
        }
    }
}
