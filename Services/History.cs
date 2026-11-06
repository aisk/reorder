using Reorder.Models;

namespace Reorder.Services;

public abstract record HistoryEntry(string Description);

/// <summary>
/// The pending (unsaved) arrangement: main order, staging areas and deletion marks.
/// </summary>
public sealed record LayoutSnapshot(
    IReadOnlyList<ImageItem> Main,
    IReadOnlyList<(StagingArea Area, IReadOnlyList<ImageItem> Items)> Areas,
    IReadOnlyList<ImageItem> Deleted)
{
    public bool SameAs(LayoutSnapshot other) =>
        Main.SequenceEqual(other.Main) &&
        Deleted.ToHashSet().SetEquals(other.Deleted) &&
        Areas.Count == other.Areas.Count &&
        Areas.Zip(other.Areas).All(p => ReferenceEquals(p.First.Area, p.Second.Area) && p.First.Items.SequenceEqual(p.Second.Items));
}

/// <summary>A change of the pending arrangement.</summary>
public sealed record LayoutEntry(string Description, LayoutSnapshot Before, LayoutSnapshot After)
    : HistoryEntry(Description);

/// <summary>A batch rename on disk.</summary>
public sealed record RenameEntry(string Description, IReadOnlyList<(ImageItem Item, string OldPath, string NewPath)> Changes)
    : HistoryEntry(Description);

/// <summary>Bounded undo / redo stacks.</summary>
public sealed class History
{
    private readonly LinkedList<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();

    public History(int capacity = 50) => Capacity = capacity;

    public int Capacity { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public HistoryEntry? NextUndo => _undo.Last?.Value;
    public HistoryEntry? NextRedo => _redo.Count > 0 ? _redo.Peek() : null;

    public void Push(HistoryEntry entry)
    {
        _undo.AddLast(entry);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
        _redo.Clear();
    }

    public HistoryEntry? PopUndo()
    {
        var node = _undo.Last;
        if (node is null) return null;
        _undo.RemoveLast();
        return node.Value;
    }

    public HistoryEntry? PopRedo() => _redo.Count > 0 ? _redo.Pop() : null;

    /// <summary>Marks an undone entry as redoable.</summary>
    public void PushRedo(HistoryEntry entry) => _redo.Push(entry);

    /// <summary>Marks a redone entry as undoable again without clearing redo.</summary>
    public void PushUndoKeepRedo(HistoryEntry entry)
    {
        _undo.AddLast(entry);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
