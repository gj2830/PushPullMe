using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PushPullMe.Core;
using RsDocx.Core.Stl;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;

namespace PushPullMe;

/// <summary>
/// The work plane's handle (as DesignSpark's Move Grid): at its origin three arrows (shift along red, green, blue), three
/// arcs (turn round them) and a yellow ball (take the origin to a point of the model). Click a part, move, click again
/// (or type the distance / angle + Enter); Esc puts the plane back. Always the same size on the screen.
/// </summary>
public partial class MainWindow
{
    private enum Grip { ShiftX, ShiftY, ShiftN, TurnX, TurnY, TurnN, Ball }

    /// <summary>A grip being moved: which, the plane it started from, where along / round it started.</summary>
    private (Grip Grip, WorkPlane From, double Start, Vec3 StartDir)? _grip;
    private Grip? _gripHover;
    private readonly List<LinesVisual3D> _gizmo = new();

    /// <summary>The handle drawn on top of everything (a block standing on the origin does not hide it).</summary>
    private HelixToolkit.Wpf.SharpDX.TopMostGroup3D? _gizmoHost;
    private static readonly Color BallColor = Color.FromRgb(0xE8, 0xB4, 0x0C);

    /// <summary>The handle's arm length (metres): about 80 pixels at its origin.</summary>
    private double GripSize()
    {
        var o = P(_plane.Origin);
        if (View.Camera is not ProjectionCamera cam) return 0.02;
        var side = Vector3D.CrossProduct(cam.LookDirection, cam.UpDirection); side.Normalize();
        double px = (Viewport3DHelper.Point3DtoPoint2D(View, o + side) - Viewport3DHelper.Point3DtoPoint2D(View, o)).Length;
        return px > 1e-9 ? 80 / px / Display : 0.02;
    }

    private Vec3 Axis(Grip g) => g switch { Grip.ShiftX or Grip.TurnX => _plane.X, Grip.ShiftY or Grip.TurnY => _plane.Y, _ => _plane.N };

    /// <summary>The points of each grip (metres): an arrow from the origin, a quarter arc between the other two axes, the
    /// ball as a small ring.</summary>
    private Dictionary<Grip, List<Vec3>> GripShapes()
    {
        double s = GripSize();
        var o = _plane.Origin;
        var shapes = new Dictionary<Grip, List<Vec3>>();
        foreach (var g in new[] { Grip.ShiftX, Grip.ShiftY, Grip.ShiftN }) shapes[g] = [o + Axis(g) * (0.12 * s), o + Axis(g) * s];
        List<Vec3> Arc(Vec3 a, Vec3 b) => Enumerable.Range(0, 13).Select(i => { double t = Math.PI / 2 * i / 12; return o + (a * Math.Cos(t) + b * Math.Sin(t)) * (0.62 * s); }).ToList();
        shapes[Grip.TurnX] = Arc(_plane.Y, _plane.N);
        shapes[Grip.TurnY] = Arc(_plane.N, _plane.X);
        shapes[Grip.TurnN] = Arc(_plane.X, _plane.Y);
        shapes[Grip.Ball] = Enumerable.Range(0, 17).Select(i => { double t = 2 * Math.PI * i / 16; return o + (_plane.X * Math.Cos(t) + _plane.Y * Math.Sin(t)) * (0.07 * s); }).ToList();
        return shapes;
    }

    private static Color GripColor(Grip g) => g switch
    {
        Grip.ShiftX or Grip.TurnX => AxisX, Grip.ShiftY or Grip.TurnY => AxisY, Grip.Ball => BallColor, _ => AxisZ,
    };

    /// <summary>The handle shown (with ⊞ chosen), the grip under the mouse or being moved thicker.</summary>
    private void RenderGizmo()
    {
        if (_gizmoHost == null) { _gizmoHost = new HelixToolkit.Wpf.SharpDX.TopMostGroup3D { EnableTopMost = true }; View.Children.Add(_gizmoHost); }
        _gizmoHost.Children.Clear();
        _gizmo.Clear();
        if (_doc == null || !WorkPlaneMode) return;
        double s = GripSize();
        foreach (var (g, pts) in GripShapes())
        {
            var line = new Point3DCollection();
            for (int i = 0; i + 1 < pts.Count; i++) { line.Add(P(pts[i])); line.Add(P(pts[i + 1])); }
            // (an arrow gets a head)
            if (g is Grip.ShiftX or Grip.ShiftY or Grip.ShiftN)
            {
                var tip = pts[^1]; var a = Axis(g);
                var across = (Math.Abs(a.Dot(_plane.N)) > 0.9 ? _plane.X : _plane.N) * (0.08 * s);
                line.Add(P(tip)); line.Add(P(tip - a * (0.18 * s) + across));
                line.Add(P(tip)); line.Add(P(tip - a * (0.18 * s) - across));
            }
            bool hot = _grip?.Grip == g || (_grip == null && _gripHover == g);
            var v = new LinesVisual3D { Points = line, Color = GripColor(g), Thickness = g == Grip.Ball ? (hot ? 14 : 10) : (hot ? 9 : 6) };
            _gizmoHost.Children.Add(v); _gizmo.Add(v);
        }
    }

    /// <summary>The grip under the mouse (within 9 pixels), if any.</summary>
    private Grip? GripAt(Point pos)
    {
        if (_doc == null || !WorkPlaneMode) return null;
        Grip? best = null; double bestPx = 9;
        foreach (var (g, pts) in GripShapes())
        {
            var s = pts.Select(q => Viewport3DHelper.Point3DtoPoint2D(View, P(q))).ToList();
            for (int i = 0; i + 1 < s.Count; i++)
            {
                var ab = s[i + 1] - s[i]; double l2 = ab.LengthSquared;
                double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - s[i], ab) / l2, 0, 1);
                double d = (s[i] + ab * t - pos).Length;
                // (the ball first: it sits where the arrows start)
                if (g == Grip.Ball) d -= 4;
                if (d < bestPx) { bestPx = d; best = g; }
            }
        }
        return best;
    }

    /// <summary>The ray through the mouse, in metres.</summary>
    private (Vec3 O, Vec3 D)? RayAt(Point pos) => Viewport3DHelper.GetRay(View, pos) is { } r
        ? (new Vec3(r.Origin.X / Display, r.Origin.Y / Display, r.Origin.Z / Display), new Vec3(r.Direction.X, r.Direction.Y, r.Direction.Z).Normalized())
        : null;

    /// <summary>How far along the axis through o with direction a the mouse ray comes closest (metres).</summary>
    private double? AlongAxis(Point pos, Vec3 o, Vec3 a)
    {
        if (RayAt(pos) is not { } r) return null;
        var w = o - r.O; double b = a.Dot(r.D), den = 1 - b * b;
        if (den < 1e-9) return null;
        return (b * r.D.Dot(w) - a.Dot(w)) / den;
    }

    /// <summary>Where the mouse ray meets the plane through o square to a, as a direction from o in that plane.</summary>
    private Vec3? RoundAxis(Point pos, Vec3 o, Vec3 a)
    {
        if (RayAt(pos) is not { } r) return null;
        double den = r.D.Dot(a);
        if (Math.Abs(den) < 1e-9) return null;
        var q = r.O + r.D * ((o - r.O).Dot(a) / den) - o;
        q -= a * q.Dot(a);
        return q.Length > 1e-12 ? q.Normalized() : null;
    }

    /// <summary>A click with ⊞ on a grip: that grip starts moving with the mouse; a click while one moves: set.</summary>
    private bool GripClick(Point pos)
    {
        if (_grip is { } gr) { _grip = null; RenderGizmo(); Log($"work plane grip {gr.Grip}: set"); StatusText.Text = T("⊞ Werkvlak gezet.", "⊞ Work plane set.") + PlaneHelp(); return true; }
        if (GripAt(pos) is not { } g) return false;
        var o = _plane.Origin; var a = Axis(g);
        double start = 0; var dir = new Vec3(0, 0, 0);
        if (g is Grip.ShiftX or Grip.ShiftY or Grip.ShiftN) start = AlongAxis(pos, o, a) ?? 0;
        else if (g is Grip.TurnX or Grip.TurnY or Grip.TurnN) dir = RoundAxis(pos, o, a) ?? (g == Grip.TurnN ? _plane.X : _plane.N);
        _grip = (g, _plane, start, dir);
        _typed = "";
        MeasureLabel.Text = g is Grip.TurnX or Grip.TurnY or Grip.TurnN ? T("Hoek", "Angle") : T("Afstand", "Distance");
        MeasureUnit.Text = g is Grip.TurnX or Grip.TurnY or Grip.TurnN ? "°" : "mm";
        MeasureBox.Text = "";
        StatusText.Text = g switch
        {
            Grip.Ball => T("⊞ Beweeg naar een punt van het model (hoeken klikken vast) en klik: daar komt de oorsprong. Esc breekt af.", "⊞ Move to a point of the model (corners snap) and click: the origin goes there. Esc cancels."),
            Grip.TurnX or Grip.TurnY or Grip.TurnN => T("⊞ Beweeg om het werkvlak te draaien (15°-stappen dichtbij) en klik, of typ de hoek + Enter. Esc breekt af.", "⊞ Move to turn the work plane (15° steps when close) and click, or type the angle + Enter. Esc cancels."),
            _ => T("⊞ Beweeg om het werkvlak langs deze as te schuiven en klik, of typ de afstand + Enter. Esc breekt af.", "⊞ Move to shift the work plane along this axis and click, or type the distance + Enter. Esc cancels."),
        };
        RenderGizmo();
        return true;
    }

    /// <summary>The mouse moving with ⊞: the grip being moved follows it; else the grip under it lights up.</summary>
    private void GripMove(Point pos)
    {
        if (_grip is not { } gr)
        {
            var h = GripAt(pos);
            if (h != _gripHover) { _gripHover = h; RenderGizmo(); }
            return;
        }
        var was = gr.From; var o = was.Origin;
        switch (gr.Grip)
        {
            case Grip.Ball:
            {
                // (onto the model: a corner near the mouse, else the point of the face under it)
                Vec3? at = null;
                if (_doc != null)
                {
                    var corners = _doc.Bodies.SelectMany(b => b.Vertices).Concat(_doc.Lines.SelectMany(l => new[] { l.Start, l.End }))
                        .Select(v => (V: v, Px: (Viewport3DHelper.Point3DtoPoint2D(View, P(v)) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
                    if (corners.Count > 0) at = corners[0].V;
                    else if (FaceAt(pos) is { } h) at = V(h.Point);
                }
                if (at is { } p) { PlaceGrip(was with { Origin = p }); MeasureBox.Text = ""; }
                break;
            }
            case Grip.ShiftX or Grip.ShiftY or Grip.ShiftN:
            {
                var a = gr.Grip == Grip.ShiftX ? was.X : gr.Grip == Grip.ShiftY ? was.Y : was.N;
                if (AlongAxis(pos, o, a) is not { } t) return;
                // (whole millimetres)
                double d = Math.Round((t - gr.Start) * Display) / Display;
                PlaceGrip(was with { Origin = o + a * d });
                MeasureBox.Text = Mm(d);
                break;
            }
            default:
            {
                var a = gr.Grip == Grip.TurnX ? was.X : gr.Grip == Grip.TurnY ? was.Y : was.N;
                if (RoundAxis(pos, o, a) is not { } v) return;
                double deg = Math.Atan2(gr.StartDir.Cross(v).Dot(a), gr.StartDir.Dot(v)) * 180 / Math.PI;
                double snapped = Math.Round(deg / 15) * 15;
                deg = Math.Abs(deg - snapped) < 2.5 ? snapped : Math.Round(deg);
                TurnGrip(was, a, deg);
                MeasureBox.Text = deg.ToString("0.#", CultureInfo.CurrentCulture);
                break;
            }
        }
    }

    private void TurnGrip(WorkPlane was, Vec3 a, double deg)
    {
        var turn = new BrepTransform.Turn(was.Origin, a, deg * Math.PI / 180);
        PlaceGrip(new WorkPlane(was.Origin, turn.Dir(was.X).Normalized(), turn.Dir(was.Y).Normalized()));
    }

    /// <summary>The plane where the grip puts it, shown (grid and handle), not yet set.</summary>
    private void PlaceGrip(WorkPlane p)
    {
        _plane = p;
        RenderGround(_doc);
    }

    /// <summary>Typed while a grip moves: the distance (mm) or angle (°) exactly; the plane is set.</summary>
    private bool GripTyped(string typed)
    {
        if (_grip is not { } gr) return false;
        if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return true;
        var was = gr.From;
        switch (gr.Grip)
        {
            case Grip.ShiftX or Grip.ShiftY or Grip.ShiftN:
                PlaceGrip(was with { Origin = was.Origin + (gr.Grip == Grip.ShiftX ? was.X : gr.Grip == Grip.ShiftY ? was.Y : was.N) * (v / Display) });
                break;
            case Grip.TurnX or Grip.TurnY or Grip.TurnN:
                TurnGrip(was, gr.Grip == Grip.TurnX ? was.X : gr.Grip == Grip.TurnY ? was.Y : was.N, v);
                break;
            default: return true;
        }
        _grip = null;
        RenderGizmo();
        StatusText.Text = T($"⊞ Werkvlak gezet ({typed}{(gr.Grip is Grip.TurnX or Grip.TurnY or Grip.TurnN ? "°" : " mm")}).", $"⊞ Work plane set ({typed}{(gr.Grip is Grip.TurnX or Grip.TurnY or Grip.TurnN ? "°" : " mm")}).") + PlaneHelp();
        Log($"work plane grip {gr.Grip}: {typed}");
        return true;
    }

    /// <summary>Esc while a grip moves: the plane back where it was.</summary>
    private bool GripCancel()
    {
        if (_grip is not { } gr) return false;
        _grip = null;
        PlaceGrip(gr.From);
        StatusText.Text = T("Afgebroken.", "Cancelled.");
        return true;
    }

    private string PlaneHelp() => T(" Pijlen: schuiven · bogen: draaien · gele bol: oorsprong naar een punt · klik een vlak: gelijk ermee · klik leeg: de grond.", " Arrows: shift · arcs: turn · yellow ball: origin to a point · click a face: level with it · click empty: the ground.");
}
