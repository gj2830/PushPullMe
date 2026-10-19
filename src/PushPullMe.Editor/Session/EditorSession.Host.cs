using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor.Session;

/// <summary>The tools, one at a time (the buttons on the left, the keys).</summary>
public enum Tool
{
    Select, PushPull, Move, Rotate, Scale,
    Line, Rectangle, Circle, Polygon, RotatedRectangle, Arc, Arc3, CentreArc, Pie, Freehand, WorkPlane,
    Offset, FollowMe, Round, Mirror, Union, Subtract, Intersect, Trim, Split,
    Tape, Dimension, Protractor, Axes, Text, Text3D, Section, Hide,
    Paint, Lasso, Eraser,
    Zoom, ZoomWindow, PositionCamera, LookAround, Walk,
}

/// <summary>What only a window can do for the editor: ask for a file, open the converter, show a message, wait for the
/// screen. The window gives one to its <see cref="EditorSession"/>.</summary>
public interface IEditorWindow
{
    /// <summary>A model to open, chosen by the user (null: none).</summary>
    Task<string?> AskOpenPath();

    /// <summary>Where to save, chosen by the user (null: nowhere); <paramref name="kind"/>: 1 STEP, 2 rsdocx, 3 STL.</summary>
    Task<string?> AskSavePath(string name, string? folder, int kind);

    /// <summary>A mesh opened in the converter's window and what it hands over; null when the user closed it without
    /// taking anything over; an exception when the converter cannot open (then it is recognised here).</summary>
    Task<(SketchDocument Doc, string Note)?> OpenInConverter(string path);

    /// <summary>Whether the window has the converter (else a mesh is recognised here).</summary>
    bool HasConverter { get; }

    /// <summary>A yes/no question (the AI assistant asks before it throws work away; opening a Revit project, whether to
    /// open the models it links too), titled <paramref name="title"/> (null: the AI assistant).</summary>
    Task<bool> Ask(string question, string? title = null);

    void ShowToast(string title, string text);

    /// <summary>The busy cover with a text, or none (null).</summary>
    void Busy(string? text);

    /// <summary>Done later on the window's thread, when it has nothing else to do.</summary>
    void Post(Action action);

    /// <summary>Done on the window's thread (from another thread: the MCP pipe).</summary>
    Task<T> OnUiThread<T>(Func<Task<T>> action);

    /// <summary>Waits until the window has drawn what changed (for the test aid and the assistant's screenshot).</summary>
    Task Idle();

    /// <summary>The view as a PNG (the assistant's screenshot), or null.</summary>
    Task<byte[]?> Snapshot();

    /// <summary>The whole window as a PNG file (the test aid), rendered by the window itself, not copied from the screen.</summary>
    Task SaveWindowPng(string path);

    /// <summary>The window closed (the test aid, done).</summary>
    void CloseWindow();

    /// <summary>The outlines of a text's letters (for 3D text): each closed loop of points, y up from the baseline, scaled so
    /// a capital is 1 high; null when the window has no font for it.</summary>
    List<List<(double X, double Y)>>? TextOutlines(string text);

    /// <summary>The structure tree to be made anew (the document, the bodies shown or the drawing aids changed), and
    /// what is picked in it.</summary>
    void StructureChanged();

    /// <summary>The list of storeys under the structure tree to be made anew (another document opened).</summary>
    void LevelsChanged();
    void StructurePick(int? body, int? face);
    void StructurePickGroup(int group);

    /// <summary>The 2D labels over the view (notes, dimensions, the radius tag, the lasso) to be placed anew.</summary>
    void LabelsChanged();
}

/// <summary>A text the window shows (the status bar, the measurements box, …); set by the editor, read by the window.</summary>
public sealed class TextSlot
{
    private string _text = "";
    public string Text { get => _text; set { if (_text == value) return; _text = value; Changed?.Invoke(); } }
    public event Action? Changed;
}

/// <summary>Whether a button can be used (undo, redo, save) or a panel is shown; set by the editor.</summary>
public sealed class FlagSlot(bool on = false)
{
    private bool _on = on;
    public bool On { get => _on; set { if (_on == value) return; _on = value; Changed?.Invoke(); } }
    public event Action? Changed;
}

/// <summary>The camera of the view (millimetres), as the tools set it; the window draws with it.</summary>
public class ProjectionCamera
{
    private Point3D _position = new(150, -200, 150);
    private Vector3D _look = new(-150, 200, -150), _up = new(0, 0, 1);

    public Point3D Position { get => _position; set { _position = value; Changed?.Invoke(); } }
    public Vector3D LookDirection { get => _look; set { _look = value; Changed?.Invoke(); } }
    public Vector3D UpDirection { get => _up; set { _up = value; Changed?.Invoke(); } }
    public double NearPlaneDistance { get; set; } = 1;
    public double FarPlaneDistance { get; set; } = 20000;

    /// <summary>Position, look and up at once (one change).</summary>
    public void Set(Point3D position, Vector3D look, Vector3D up) { _position = position; _look = look; _up = up; Changed?.Invoke(); }

    public CameraPose Pose => new(_position, _look, _up);

    public event Action? Changed;
}

/// <summary>A perspective camera: its angle of view in degrees.</summary>
public sealed class PerspectiveCamera : ProjectionCamera
{
    public double FieldOfView { get; set; } = 45;
}

/// <summary>A ray in the view: where it starts and which way it goes (millimetres).</summary>
public sealed record Ray3D(Point3D Origin, Vector3D Direction);

/// <summary>
/// The view as the tools see it: its camera, its size, what is shown in it beside the model (<see cref="Children"/>),
/// and the mouse held to it while dragging. The window keeps the size up to date and draws the rest.
/// </summary>
public sealed class ViewHost
{
    public PerspectiveCamera Camera { get; } = new();

    /// <summary>The size of the view (device-independent pixels, as the mouse gives them).</summary>
    public double ActualWidth { get; set; } = 1000;
    public double ActualHeight { get; set; } = 700;

    /// <summary>Whether the angle of view is across the view (else up it, as HelixToolkit draws and every window follows).</summary>
    public bool FieldOfViewAcross { get; set; }

    /// <summary>The lines and highlights shown beside the model.</summary>
    public SceneItems Children { get; } = new();

    /// <summary>(as WPF had it: the view itself)</summary>
    public ViewHost Viewport => this;

    public event Action<bool>? MouseCaptureChanged;
    public void CaptureMouse() => MouseCaptureChanged?.Invoke(true);
    public void ReleaseMouseCapture() => MouseCaptureChanged?.Invoke(false);

    /// <summary>The camera as a frame over the view (null when it cannot be used).</summary>
    public ViewFrame? Frame => ViewFrame.Of(Camera.Position, Camera.LookDirection, Camera.UpDirection, Camera.FieldOfView, FieldOfViewAcross, ActualWidth, ActualHeight);
}

/// <summary>Between the screen and the model (millimetres), on the editor's frame (as the window's helper did it).</summary>
public static class Viewport3DHelper
{
    public static Point Point3DtoPoint2D(ViewHost view, Point3D p) => view.Frame is { } f ? (Point)f.Project(p) : new Point(view.ActualWidth / 2, view.ActualHeight / 2);

    public static Ray3D? GetRay(ViewHost view, Point pos)
    {
        if (view.Frame is not { } f) return null;
        var r = f.Ray(pos);
        return new Ray3D(r.Origin, r.Direction);
    }

    public static (double Tx, double Ty)? Reach(ViewHost view) => view.Frame is { } f ? (f.Tx, f.Ty) : null;

    /// <summary>From the model to the screen: X and Y in pixels (Z the depth along the look).</summary>
    public static ScreenTransform GetTotalTransform(ViewHost view) => new(view.Frame);
}

/// <summary>From the model (millimetres) to the screen.</summary>
public sealed class ScreenTransform(ViewFrame? frame)
{
    public Point3D Transform(Point3D p)
    {
        if (frame is not { } f) return default;
        var s = f.Project(p);
        return new Point3D(s.X, s.Y, f.Depth(p));
    }
}
