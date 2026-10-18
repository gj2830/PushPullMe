namespace PushPullMe.Editor.Session;

/// <summary>What a 2D label over the view is: a text label (with a leader to its point), a dimension's text (centred on
/// its point; faded while it is being placed).</summary>
public enum LabelKind { Note, Dimension }

/// <summary>A label over the view at a point of the screen (view pixels).</summary>
public sealed record ViewLabel(LabelKind Kind, string Text, Point At, double Opacity = 1);

/// <summary>
/// What lies over the view in 2D (screen pixels in the view, from its top left): text labels and dimensions, the tag next
/// to the mouse, the lasso or zoom frame, the box a label is typed in. The window places them when told
/// (<see cref="IEditorWindow.LabelsChanged"/>).
/// </summary>
public sealed partial class EditorSession
{
    private readonly List<ViewLabel> _labels = new();
    private readonly List<Point> _lassoLine = new();

    public IReadOnlyList<ViewLabel> Labels => _labels;

    /// <summary>The tag next to the mouse (the radius of a rounding, pin or hole): its text and where its corner goes.</summary>
    public (string Text, Point At)? Tag { get; private set; }

    /// <summary>The lasso's loop or the zoom window's frame (empty: none).</summary>
    public IReadOnlyList<Point> LassoLine => _lassoLine;

    /// <summary>The box a label (or a 3D text) is typed in: where its top left corner goes (null: none). The window shows it,
    /// takes the typing and ends it with <see cref="EndNoteBox(string?)"/>.</summary>
    public Point? NoteBox { get; private set; }

    private void SetLasso(IEnumerable<Point> points)
    {
        _lassoLine.Clear(); _lassoLine.AddRange(points);
        _window.LabelsChanged();
    }

    /// <summary>The tag next to the mouse with a text (the radius of a rounding, pin or hole).</summary>
    private void ShowTag(string text, Point pos)
    {
        Tag = (text, new Point(pos.X + 16, pos.Y - 34));
        _window.LabelsChanged();
    }

    private void HideTag()
    {
        if (Tag == null) return;
        Tag = null;
        _window.LabelsChanged();
    }

    /// <summary>Where a point of the model (metres) is on the screen.</summary>
    private Point Screen2(RsDocx.Core.Stl.Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View, P(v));
}
