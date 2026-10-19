using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using PushPullMe.Core;
using PushPullMe.Editor.Session;
using Color = Avalonia.Media.Color;
using Point = Avalonia.Point;
using Vector3D = PushPullMe.Editor.Session.Vector3D;

namespace PushPullMe;

/// <summary>
/// A small cube in the view's corner that turns with the camera (as the view cube of SketchUp, Revit or the old WPF
/// window); a click on the middle of a side looks straight at that side, on an edge or a corner from
/// between them (<see cref="EditorSession.ViewFrom"/>). Every side is cut in three by three: the middle the side, the
/// strips along its edges the edges, its corners the corners; the part under the mouse lights up. Only the cube itself
/// takes the mouse, the rest of the control lets it through to the model.
/// </summary>
internal sealed class ViewCube : Control, ICustomHitTest
{
    private EditorSession? _session;

    /// <summary>Where the strips along the edges begin (of the half side, from the middle).</summary>
    private const double Inner = 0.55;

    /// <summary>A side of the cube: the way out of it (x right, y back, z up), its two directions along it, its name (for
    /// the status line).</summary>
    private sealed record Side(Vector3D Out, Vector3D U, Vector3D V, string Nl, string En);

    private static readonly Side[] Sides =
    [
        new(new(0, 0, 1), new(1, 0, 0), new(0, 1, 0), "Boven", "Top"),
        new(new(0, 0, -1), new(1, 0, 0), new(0, -1, 0), "Onder", "Bottom"),
        new(new(0, -1, 0), new(1, 0, 0), new(0, 0, 1), "Voor", "Front"),
        new(new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1), "Achter", "Back"),
        new(new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1), "Links", "Left"),
        new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), "Rechts", "Right"),
    ];

    /// <summary>A part of a side as drawn: its corners on the screen, the way it looks from, its side.</summary>
    private sealed record Part(Point[] Corners, Vector3D From, Side Side);

    private List<Part> _parts = [];
    private Vector3D? _hover;

    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFA, 0xCC, 0x15));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.FromRgb(0x46, 0x50, 0x66)), 1.3);
    private static readonly IPen PartPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x66, 0x7E)), 0.6);

    public ViewCube()
    {
        Width = 78; Height = 78;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The editor whose camera the cube follows and sets.</summary>
    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session != null) _session.View.Camera.Changed -= CameraMoved;
            _session = value;
            if (value != null) value.View.Camera.Changed += CameraMoved;
            InvalidateVisual();
        }
    }

    /// <summary>The camera moved (by whatever, on whichever thread): drawn again.</summary>
    private void CameraMoved() => Dispatcher.UIThread.Post(InvalidateVisual);

    /// <summary>Only the cube takes the mouse.</summary>
    public bool HitTest(Point point) => _parts.Any(p => Inside(p.Corners, point));

    public override void Render(DrawingContext context)
    {
        _parts = [];
        if (_session == null) return;
        var cam = _session.View.Camera;
        var f = cam.LookDirection; f.Normalize();
        var r = Cross(f, cam.UpDirection); if (r.Length < 1e-9) r = new Vector3D(1, 0, 0); r.Normalize();
        var u = Cross(r, f);
        // (seen along the camera's look without perspective: a point of the cube (each coordinate -1 … 1) on the
        // control, the cube's middle in its middle)
        double scale = Math.Min(Bounds.Width, Bounds.Height) * 0.29;
        var mid = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Point On(Vector3D p) => new(mid.X + Dot(p, r) * scale, mid.Y - Dot(p, u) * scale);
        double[] cuts = [-1, -Inner, Inner, 1];
        foreach (var side in Sides.Where(s => Dot(s.Out, f) < -1e-6))
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Vector3D At(double a, double b) => side.Out + side.U * a + side.V * b;
                    var corners = new[] { On(At(cuts[i], cuts[j])), On(At(cuts[i + 1], cuts[j])), On(At(cuts[i + 1], cuts[j + 1])), On(At(cuts[i], cuts[j + 1])) };
                    // (the way it looks from: the side's, and its neighbours' along the edges or corner it is on)
                    var from = side.Out + side.U * (i - 1) + side.V * (j - 1);
                    _parts.Add(new Part(corners, from, side));
                }
        }
        foreach (var part in _parts)
        {
            var geometry = new PolylineGeometry(part.Corners, true);
            context.DrawGeometry(Shade(part.Side, -Dot(part.Side.Out, f)), PartPen, geometry);
            if (_hover is { } h && Same(h, part.From)) context.DrawGeometry(HoverBrush, null, geometry);
        }
        // the sides' outlines
        foreach (var side in Sides.Where(s => Dot(s.Out, f) < -1e-6))
        {
            Vector3D At(double a, double b) => side.Out + side.U * a + side.V * b;
            context.DrawGeometry(null, EdgePen, new PolylineGeometry([On(At(-1, -1)), On(At(1, -1)), On(At(1, 1)), On(At(-1, 1))], true));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var over = PartAt(e.GetPosition(this))?.From;
        if (over != _hover) { _hover = over; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session == null || PartAt(e.GetPosition(this)) is not { } part) return;
        e.Handled = true;
        _session.ViewFrom(part.From, Name(part.From));
    }

    /// <summary>A side's colour: that of its axis, as the grid's (right and left red, front and back green, top and
    /// bottom blue), darker the less squarely it faces the camera (0 … 1), as if lit from the eye.</summary>
    private static IBrush Shade(Side side, double facing)
    {
        var (r, g, b) = Math.Abs(side.Out.X) > 0.5 ? (0xE5, 0x6B, 0x6B) : Math.Abs(side.Out.Y) > 0.5 ? (0x5C, 0xB8, 0x7A) : (0x5B, 0x8D, 0xE0);
        double light = 0.72 + 0.28 * Math.Clamp(facing, 0, 1);
        byte C(int c) => (byte)Math.Round(Math.Min(255, c * light + (255 - c) * 0.18 * light));
        return new SolidColorBrush(Color.FromRgb(C(r), C(g), C(b)));
    }

    /// <summary>The part of the cube at a point of the control, or null.</summary>
    private Part? PartAt(Point p) => _parts.FirstOrDefault(part => Inside(part.Corners, p));

    /// <summary>What a way to look from is called: its sides' names joined (front left above, …).</summary>
    private static string Name(Vector3D from)
    {
        var names = Sides.Where(s => Dot(s.Out, from) > 0.5).Select(s => Lang.T(s.Nl, s.En).ToLowerInvariant()).ToList();
        return string.Join("-", names);
    }

    /// <summary>Whether a point lies in a convex polygon (either way round).</summary>
    private static bool Inside(Point[] poly, Point p)
    {
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var (a, b) = (poly[i], poly[(i + 1) % poly.Length]);
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    private static bool Same(Vector3D a, Vector3D b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z) < 1e-9;
    private static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vector3D Cross(Vector3D a, Vector3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
