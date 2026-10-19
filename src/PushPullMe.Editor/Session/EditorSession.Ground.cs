using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using static PushPullMe.Editor.Units;

namespace PushPullMe.Editor.Session;


/// <summary>
/// Drawing on the work plane (at first the ground, z = 0; ⊞ puts it level with a face, shifts and turns it), where there
/// is no face, as SketchUp starts a model: ▭ a rectangle and ◯ a circle become a face without thickness (⇕ pulls it into
/// a block), ╱ lines stay loose until they close a shape, which then becomes such a face. In an empty window the first
/// click starts a new, empty document.
/// </summary>
public sealed partial class EditorSession
{
    private enum GroundKind { Rectangle, Circle, Line }

    /// <summary>What is being drawn on the work plane (null: nothing), the points clicked and the point under the mouse.</summary>
    private GroundKind? _gKind;
    private readonly List<Vec3> _gPts = new();
    private Vec3 _gEnd;

    /// <summary>The plane drawn on where there is no face: its origin, its red and green directions (square to each
    /// other, unit length) and their normal.</summary>
    private readonly record struct WorkPlane(Vec3 Origin, Vec3 X, Vec3 Y)
    {
        public Vec3 N => X.Cross(Y);
        public static WorkPlane Ground => new(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        /// <summary>A point given in the plane's own red/green measures.</summary>
        public Vec3 At(double u, double v) => Origin + X * u + Y * v;
        public bool On(Vec3 p) => Math.Abs((p - Origin).Dot(N)) < 1e-9;
    }

    private WorkPlane _plane = WorkPlane.Ground;
    private Vec3 GroundUp => _plane.N;
    private static readonly Color GridColor = Color.FromRgb(0xD3, 0xDC, 0xE8);
    private readonly List<LineSet> _groundVisuals = new();

    /// <summary>Where the mouse points on the work plane, or null (looking along it, or away from it); close (12 pixels) to
    /// the end of a loose line, a point clicked or a corner of a face on the work plane: that point.</summary>
    private Vec3? GroundAt(Point pos)
    {
        if (Viewport3DHelper.GetRay(View, pos) is not { } r) return null;
        // (the ray in metres; where it meets the work plane)
        var o = new Vec3(r.Origin.X / Display, r.Origin.Y / Display, r.Origin.Z / Display);
        var d = new Vec3(r.Direction.X, r.Direction.Y, r.Direction.Z);
        double den = d.Dot(_plane.N);
        if (Math.Abs(den) < 1e-9) return null;
        double t = (_plane.Origin - o).Dot(_plane.N) / den;
        if (t <= 0) return null;
        var near = GroundCorners().Select(c => (C: c, Px: (Viewport3DHelper.Point3DtoPoint2D(View, P(c)) - pos).Length))
            .Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
        return near.Count > 0 ? near[0].C : o + d * t;
    }

    /// <summary>Whether a click drawing on <paramref name="onFace"/> goes onto the work plane instead: the plane is set
    /// with ⊞ (not the ground), the mouse meets it, and the face does not lie in it.</summary>
    private bool OnSetPlane(Point3D onFace, Point pos) =>
        _plane != WorkPlane.Ground && GroundAt(pos) != null && Math.Abs((V(onFace) - _plane.Origin).Dot(_plane.N)) > 1e-6;

    private IEnumerable<Vec3> GroundCorners()
    {
        bool On(Vec3 v) => _plane.On(v);
        foreach (var p in _gPts) yield return p;
        if (_doc == null) yield break;
        foreach (var l in _doc.Lines.Where(l => !l.Construction && On(l.Start) && On(l.End))) { yield return l.Start; yield return l.End; }
        foreach (var b in _doc.Bodies) foreach (var v in b.Vertices.Where(On)) yield return v;
    }

    /// <summary>An empty document to draw in (the window was empty).</summary>
    private void NewDocument()
    {
        _doc = SketchDocument.FromBodies([]);
        _path = null; _plane = WorkPlane.Ground;
        _history.Clear(); _last = null; _selected = null; _hover = null;
        UpdateUndoButtons();
        FileText.Text = T("nieuw", "new");
        EmptyHint.On = false;
        SaveButton.On = true;
        Render();
        StatusText.Text = T("📄 Leeg werkblad. Teken op de grond met ▭ (R), ◯ (C) of ╱ (L) en trek het vlak met ⇕ (P) tot een blok, of open of sleep een model hierheen.", "📄 Empty work sheet. Draw on the ground with ▭ (R), ◯ (C) or ╱ (L) and pull the face into a block with ⇕ (P), or open or drop a model here.");
        Log("new document");
    }

    /// <summary>
    /// The next point of a line on the work plane: a corner close by (see <see cref="GroundAt"/>), else from the last
    /// point along an axis of the plane, parallel or square to a line drawn before or to the edge rested on last (see
    /// <see cref="InferDirs"/>), else where the mouse points; with the direction it was inferred along.
    /// </summary>
    private (Vec3 P, InferDir? Along)? GroundLinePoint(Point pos)
    {
        if (GroundAt(pos) is not { } g) return null;
        if (_gKind != GroundKind.Line || _gPts.Count == 0 || GroundCorners().Any(c => (c - g).Length < 1e-12)) return (g, null);
        var drawn = _gPts.Zip(_gPts.Skip(1)).Select(s => (s.Second - s.First).Normalized());
        return InferFrom(_gPts[^1], pos, g, InferDirs(_plane.N, drawn)) is { } inf ? (inf.P, inf.Dir) : (g, null);
    }

    /// <summary>A click on the work plane: the first point of a shape, or its next or last.</summary>
    private void GroundClick(Point pos)
    {
        if (_doc == null || GroundLinePoint(pos) is not { } gl) return;
        var g = gl.P;
        if (_gKind == null)
        {
            _gKind = RectMode ? GroundKind.Rectangle : CircleMode ? GroundKind.Circle : GroundKind.Line;
            _gPts.Clear(); _gPts.Add(g); _gEnd = g; _typed = "";
            _selected = null; _hover = null; UpdateHighlight();
            ShowSketch([g]);
            MeasureLabel.Text = _gKind == GroundKind.Rectangle ? T("Maten", "Size") : _gKind == GroundKind.Circle ? T("Straal", "Radius") : T("Lengte", "Length");
            MeasureBox.Text = "";
            StatusText.Text = _gKind switch
            {
                GroundKind.Rectangle => T("▭ Op het werkvlak: klik de andere hoek, of typ breedte;diepte + Enter. Esc breekt af.", "▭ On the work plane: click the other corner, or type width;depth + Enter. Esc cancels."),
                GroundKind.Circle => T("◯ Op het werkvlak: klik de rand, of typ de straal + Enter. Esc breekt af.", "◯ On the work plane: click the edge, or type the radius + Enter. Esc cancels."),
                _ => T("╱ Op het werkvlak: klik het volgende punt, of typ een lengte + Enter. Terug op het begin: een vlak. Esc: klaar.", "╱ On the work plane: click the next point, or type a length + Enter. Back at the start: a face. Esc: done."),
            };
            return;
        }
        GroundPoint(g);
    }

    /// <summary>The shape on the work plane taken on to point g (the second corner, the edge of the circle, the next point).</summary>
    private void GroundPoint(Vec3 g)
    {
        if (_doc is not { } before || _gKind is not { } kind) return;
        var a = _gPts[0];
        switch (kind)
        {
            case GroundKind.Rectangle:
            {
                double du = (g - a).Dot(_plane.X), dv = (g - a).Dot(_plane.Y);
                if (Math.Abs(du) < 1e-6 || Math.Abs(dv) < 1e-6) return;
                var (withRect, rectFaces) = before.AddShape([a, a + _plane.X * du, a + _plane.X * du + _plane.Y * dv, a + _plane.Y * dv, a], [new BrepLine(), new BrepLine(), new BrepLine(), new BrepLine()], GroundUp, _plane.Origin);
                Made(before, withRect, T($"✅ Rechthoek van {Mm(Math.Abs(du))} × {Mm(Math.Abs(dv))} mm op het werkvlak. Kies ⇕ (P) om er een blok van te trekken.", $"✅ Rectangle of {Mm(Math.Abs(du))} × {Mm(Math.Abs(dv))} mm on the work plane. Choose ⇕ (P) to pull a block from it."), rectFaces.Count > 0 ? rectFaces[^1] : null);
                return;
            }
            case GroundKind.Circle:
            {
                double r = (g - a).Length;
                if (r < 1e-6) return;
                var x = (g - a).Normalized();
                var (withDisc, discFaces) = before.AddShape([a + x * r, a + x * r], [new BrepCircle(a, GroundUp, x, r)], GroundUp, _plane.Origin);
                Made(before, withDisc, T($"✅ Cirkel met straal {Mm(r)} mm op het werkvlak. Kies ⇕ (P) om er een cilinder van te trekken.", $"✅ Circle with radius {Mm(r)} mm on the work plane. Choose ⇕ (P) to pull a cylinder from it."), discFaces.Count > 0 ? discFaces[^1] : null);
                return;
            }
            default:
            {
                var last = _gPts[^1];
                if ((g - last).Length < 1e-9) return;
                var (after, made) = before.AddLines([new SketchLine(last, g)]).CloseLooseLinesOnPlane(GroundUp, _plane.Origin);
                Log(string.Create(CultureInfo.InvariantCulture, $"ground line ({last.X * Display:0.###},{last.Y * Display:0.###}) → ({g.X * Display:0.###},{g.Y * Display:0.###}): {made.Count} face(s) closed"));
                if (made.Count > 0)
                {
                    Made(before, after, T("✅ De lijnen sluiten een vorm: een vlak op het werkvlak. Kies ⇕ (P) om er een blok van te trekken.", "✅ The lines close a shape: a face on the work plane. Choose ⇕ (P) to pull a block from it."), made[^1]);
                    return;
                }
                Commit(before, after);
                _gPts.Add(g); _gEnd = g;
                Render();
                ShowSketch([g]);
                StatusText.Text = T($"╱ {_gPts.Count - 1} {(_gPts.Count == 2 ? "lijn" : "lijnen")} op het werkvlak · klik het volgende punt. Terug op een begin: een vlak. Esc: klaar.", $"╱ {_gPts.Count - 1} {(_gPts.Count == 2 ? "line" : "lines")} on the work plane · click the next point. Back at a start: a face. Esc: done.");
                return;
            }
        }
    }

    /// <summary>A shape on the work plane done: the document taken, the new face selected.</summary>
    private void Made(SketchDocument before, SketchDocument after, string status, FaceRef? face = null)
    {
        EndGround();
        Commit(before, after);
        _selected = face ?? new FaceRef(after.Bodies.Count - 1, 0); _last = null;
        Render();
        StatusText.Text = status;
        Log($"on the work plane: {status}");
    }

    /// <summary>The shape on the work plane as the mouse moves.</summary>
    private void GroundTo(Point pos)
    {
        if (_gKind is not { } kind || GroundLinePoint(pos) is not { } gl) return;
        var g = gl.P;
        _gEnd = g;
        var a = _gPts[0];
        switch (kind)
        {
            case GroundKind.Rectangle:
            {
                double du = (g - a).Dot(_plane.X), dv = (g - a).Dot(_plane.Y);
                ShowSketch([a, a + _plane.X * du, a + _plane.X * du + _plane.Y * dv, a + _plane.Y * dv], default, closed: true);
                MeasureBox.Text = $"{Mm(Math.Abs(du))};{Mm(Math.Abs(dv))}";
                break;
            }
            case GroundKind.Circle:
                double r = (g - a).Length;
                if (r > 1e-9) ShowSketch(PushPullMe.Core.Drawing.ArcPoints(new BrepCircle(a, GroundUp, (g - a).Normalized(), r), g, g, 64), default, closed: true);
                MeasureBox.Text = Mm(r);
                break;
            default:
                ShowSketch([_gPts[^1], g], default, color: gl.Along?.Color);
                MeasureBox.Text = Mm((g - _gPts[^1]).Length);
                if (gl.Along is { } al) StatusText.Text = T($"╱ {Mm((g - _gPts[^1]).Length)} mm {al.Name} · klik, of typ een lengte + Enter. Esc: klaar.", $"╱ {Mm((g - _gPts[^1]).Length)} mm {al.Name} · click, or type a length + Enter. Esc: done.");
                break;
        }
    }

    /// <summary>Typed while drawing on the work plane: width;depth of the rectangle, the radius, or the length of the next
    /// line (toward the mouse); millimetres.</summary>
    private void GroundTyped(string typed)
    {
        if (_gKind is not { } kind) return;
        var parts = typed.Replace(',', '.').Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v / Display : double.NaN).ToList();
        if (parts.Count == 0 || parts.Any(double.IsNaN)) return;
        var a = _gPts[0];
        switch (kind)
        {
            case GroundKind.Rectangle:
            {
                double sx = (_gEnd - a).Dot(_plane.X) >= 0 ? 1 : -1, sy = (_gEnd - a).Dot(_plane.Y) >= 0 ? 1 : -1;
                double w = Math.Abs(parts[0]), d = Math.Abs(parts.Count > 1 ? parts[1] : parts[0]);
                GroundPoint(a + _plane.X * (sx * w) + _plane.Y * (sy * d));
                break;
            }
            case GroundKind.Circle:
            {
                var dir = (_gEnd - a).Length > 1e-9 ? (_gEnd - a).Normalized() : _plane.X;
                GroundPoint(a + dir * Math.Abs(parts[0]));
                break;
            }
            default:
            {
                var last = _gPts[^1];
                var dir = (_gEnd - last).Length > 1e-9 ? (_gEnd - last).Normalized() : _plane.X;
                GroundPoint(last + dir * Math.Abs(parts[0]));
                break;
            }
        }
    }

    private void EndGround()
    {
        _gKind = null; _gPts.Clear();
        ShowSketch([]);
    }

    /// <summary>A faint grid on the work plane (every 10 mm, or 50 for a large model) with its red and green axes,
    /// as far as the model reaches seen along it (at least 100 mm round its origin).</summary>
    private void RenderGround(SketchDocument? doc)
    {
        // (the button back to the ground: only while a work plane is set)
        GroundButtonShown.On = _plane != WorkPlane.Ground;
        foreach (var v in _groundVisuals) View.Children.Remove(v);
        _groundVisuals.Clear();
        if (doc == null) return;
        double reach = 100;
        foreach (var b in doc.Bodies)
            foreach (var v in b.Vertices) reach = Math.Max(reach, Math.Max(Math.Abs((v - _plane.Origin).Dot(_plane.X)), Math.Abs((v - _plane.Origin).Dot(_plane.Y))) * Display * 1.2);
        double step = reach > 400 ? 50 : 10;
        reach = Math.Ceiling(reach / step) * step / Display;
        step /= Display;
        Point3D Q(double u, double v) => P(_plane.At(u, v));
        var grid = new Point3DCollection();
        for (double s = -reach; s <= reach + 1e-12; s += step)
        {
            if (Math.Abs(s) < 1e-12) continue;
            grid.Add(Q(s, -reach)); grid.Add(Q(s, reach));
            grid.Add(Q(-reach, s)); grid.Add(Q(reach, s));
        }
        var grey = new LineSet { Points = grid, Color = GridColor, Thickness = 0.8 };
        var red = new LineSet { Points = [Q(-reach, 0), Q(reach, 0)], Color = AxisX, Thickness = 1.2 };
        var green = new LineSet { Points = [Q(0, -reach), Q(0, reach)], Color = AxisY, Thickness = 1.2 };
        foreach (var v in new[] { grey, red, green })
        {
            // (behind everything drawn on it: the edges of a face there stay on top)
            v.Behind = true;
            View.Children.Add(v); _groundVisuals.Add(v);
        }
        RenderGizmo();
    }

    // ------------------------------------------------------------------ ⊞ the work plane

    private bool WorkPlaneMode => Tool == Tool.WorkPlane;

    /// <summary>A click with ⊞: the work plane level with the flat face clicked (its origin where it was clicked), or
    /// back on the ground (an empty spot).</summary>
    /// <summary>The ⊞ grond button: the work plane back on the ground, as ⊞ and a click on empty space.</summary>
    public void GroundButton()
    {
        _grip = null;
        RenderGizmo();
        SetPlane(WorkPlane.Ground, T("⊞ Het werkvlak ligt weer op de grond.", "⊞ The work plane is back on the ground."));
    }

    private void WorkPlaneClick(Point pos)
    {
        if (_doc == null) return;
        if (FaceAt(pos) is not { } h) { SetPlane(WorkPlane.Ground, T("⊞ Het werkvlak ligt weer op de grond.", "⊞ The work plane is back on the ground.")); return; }
        var face = _doc.Bodies[h.Face.Body].Faces[h.Face.Face];
        if (face.Surface is not BrepPlane) { StatusText.Text = T("⊞ Het werkvlak kan alleen gelijk komen met een plat vlak.", "⊞ The work plane can only be put level with a flat face."); return; }
        var n = FaceGeometry.Normal(face);
        // red: the red axis laid on the face (or the green one, when the face stands square to the red)
        static Vec3? Flat(Vec3 d, Vec3 n) { var f = d - n * d.Dot(n); return f.Length > 0.1 ? f.Normalized() : null; }
        var x = Flat(new Vec3(1, 0, 0), n) ?? Flat(new Vec3(0, 1, 0), n)!.Value;
        SetPlane(new WorkPlane(V(h.Point), x, n.Cross(x)), T("⊞ Het werkvlak ligt nu gelijk met dit vlak (de oorsprong waar je klikte). Teken naast het model met ▭ ◯ ╱.", "⊞ The work plane now lies level with this face (its origin where you clicked). Draw beside the model with ▭ ◯ ╱."));
    }

    private void SetPlane(WorkPlane plane, string status)
    {
        _plane = plane;
        RenderGround(_doc);
        StatusText.Text = status + PlaneHelp();
        Log(string.Create(CultureInfo.InvariantCulture, $"work plane at ({plane.Origin.X * Display:0.###},{plane.Origin.Y * Display:0.###},{plane.Origin.Z * Display:0.###}) normal ({plane.N.X:0.###},{plane.N.Y:0.###},{plane.N.Z:0.###})"));
    }

    /// <summary>Arrow keys with ⊞: the work plane turned round its own green (left/right) or red (up/down) axis through
    /// its origin, 15° a press (5° with Shift).</summary>
    private bool WorkPlaneKey(Key key)
    {
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return false;
        double deg = (Mods & ModifierKeys.Shift) != 0 ? 5 : 15;
        bool aroundGreen = key is Key.Left or Key.Right;
        double sign = key is Key.Right or Key.Up ? 1 : -1;
        var turn = new BrepTransform.Turn(_plane.Origin, aroundGreen ? _plane.Y : _plane.X, sign * deg * Math.PI / 180);
        var p = new WorkPlane(_plane.Origin, turn.Dir(_plane.X).Normalized(), turn.Dir(_plane.Y).Normalized());
        SetPlane(p, T($"⊞ Werkvlak {deg:0}° gedraaid om de {(aroundGreen ? "groene" : "rode")} as.", $"⊞ Work plane turned {deg:0}° round the {(aroundGreen ? "green" : "red")} axis."));
        return true;
    }

    /// <summary>Typed with ⊞: one number shifts the work plane along its normal (mm), two (x;y) shift its origin within it.</summary>
    private void WorkPlaneTyped(string typed)
    {
        var parts = typed.Replace(',', '.').Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v / Display : double.NaN).ToList();
        if (parts.Count == 0 || parts.Any(double.IsNaN)) return;
        var o = parts.Count == 1 ? _plane.Origin + _plane.N * parts[0] : _plane.Origin + _plane.X * parts[0] + _plane.Y * parts[1];
        SetPlane(_plane with { Origin = o }, parts.Count == 1
            ? T($"⊞ Werkvlak {typed} mm verschoven langs zijn normaal.", $"⊞ Work plane shifted {typed} mm along its normal.")
            : T($"⊞ Oorsprong van het werkvlak {typed} mm verschoven.", $"⊞ Origin of the work plane shifted {typed} mm."));
    }

    // ------------------------------------------------------------------ starting on a loose line

    /// <summary>
    /// A click beside every face, close (12 pixels) to an end, the middle or the centre of a loose line or arc: there,
    /// on a flat face whose plane that loose line lies in (it was drawn in that face's plane, beside the face), so the
    /// drawing starts exactly on the snap point shown, not on the work plane under it.
    /// </summary>
    private (FaceRef Face, Point3D Point)? LooseHit(Point pos)
    {
        if (_doc == null) return null;
        var near = _doc.Lines.Where(l => !l.Construction)
            .SelectMany(l => new[] { (L: l, Q: l.Start), (L: l, Q: l.End), (L: l, Q: l.Middle) }.Concat(l.Centre is { } c ? [(L: l, Q: c)] : []))
            .Select(x => (x.L, x.Q, Px: (Viewport3DHelper.Point3DtoPoint2D(View, P(x.Q)) - pos).Length))
            .Where(x => x.Px < 12).OrderBy(x => x.Px);
        foreach (var (l, q, _) in near)
            for (int b = 0; b < _doc.Bodies.Count; b++)
                for (int f = 0; f < _doc.Bodies[b].Faces.Count; f++)
                    if (_doc.Bodies[b].Faces[f].Surface is BrepPlane pl && LooseLines.InPlane(l, FaceGeometry.Normal(_doc.Bodies[b].Faces[f]), pl.Origin))
                        return (new FaceRef(b, f), P(q));
        return null;
    }

    /// <summary>The face under the mouse, or else a loose line's snap point to start on (see <see cref="LooseHit"/>).</summary>
    private (FaceRef Face, Point3D Point)? FaceOrLoose(Point pos) => FaceAt(pos) ?? LooseHit(pos);
}

