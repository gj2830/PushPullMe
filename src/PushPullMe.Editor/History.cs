using PushPullMe.Core;

namespace PushPullMe.Editor;

/// <summary>
/// Undo and redo: the documents as they were before each change, and the ones undone. A document is never changed in
/// place, so a step is simply the one before.
/// </summary>
public sealed class History
{
    private readonly List<SketchDocument> _undo = new(), _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    /// <summary>A change made: <paramref name="before"/> kept to go back to, what was undone gone. Returns the document
    /// to work on from now on (the bodies' names and groups carried along, a new body into the open group: see
    /// <see cref="SketchDocument.CarryMeta"/>).</summary>
    public SketchDocument Commit(SketchDocument before, SketchDocument after, int? openGroup)
    {
        _undo.Add(before); _redo.Clear();
        return SketchDocument.CarryMeta(before, after, openGroup);
    }

    /// <summary>The last change made over (a distance typed after it): the step back stays the one before it.</summary>
    public SketchDocument Replace(SketchDocument before, SketchDocument after, int? openGroup)
    {
        _redo.Clear();
        return SketchDocument.CarryMeta(before, after, openGroup);
    }

    /// <summary>The document before <paramref name="current"/>, or null when there is none.</summary>
    public SketchDocument? Undo(SketchDocument current)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(current);
        var doc = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        return doc;
    }

    /// <summary>The document undone last, or null when there is none.</summary>
    public SketchDocument? Redo(SketchDocument current)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(current);
        var doc = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        return doc;
    }

    /// <summary>Another document from now on: nothing to go back to.</summary>
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
