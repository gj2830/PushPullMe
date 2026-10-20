using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using static PushPullMe.Editor.Units;

namespace PushPullMe.Editor.Session;

/// <summary>
/// Inferring a direction, as SketchUp does for every tool that goes from one point to the next: along the red, green or
/// blue axis, parallel to an edge, or (in a plane) square to it. The edges: those given by the tool (the face drawn on,
/// the face under the mouse) and the straight edge the mouse last rested on, anywhere in the model (as in SketchUp: rest
/// on an edge, then move away, and a line can run parallel to it).
/// </summary>
public sealed partial class EditorSession
{
    /// <summary>A direction a point can be inferred along, as it is named and coloured.</summary>
    private readonly record struct InferDir(Vec3 Dir, string Name, Color Color);

    /// <summary>The direction of the straight edge the mouse last rested on (null: none yet).</summary>
    private Vec3? _restEdge;

    /// <summary>Remembers the straight edge the mouse is on (within 10 pixels; see <see cref="EdgeAt"/>).</summary>
    private void RestOnEdge(Point pos)
    {
        if (_doc == null || EdgeAt(FaceAt(pos), pos) is not { } at) return;
        var e = _doc.Bodies[at.Body].Edges[at.Edge];
        if (e.Curve is BrepLine && e.Start >= 0 && e.End >= 0)
            _restEdge = (_doc.Bodies[at.Body].Vertices[e.End] - _doc.Bodies[at.Body].Vertices[e.Start]).Normalized();
    }

    /// <summary>
    /// The directions to infer along: the axes (in a plane with normal <paramref name="n"/>: those lying in it; null: in
    /// space), and parallel to the straight edges in <paramref name="edges"/> and to the edge rested on last, in a plane
    /// only those lying in it, with the direction square to each in the plane too.
    /// </summary>
    private List<InferDir> InferDirs(Vec3? n, IEnumerable<Vec3> edges)
    {
        var dirs = new List<InferDir>();
        foreach (var ax in Axes.Where(a => n is not { } nn || Math.Abs(a.Dir.Dot(nn)) < 1e-6))
            dirs.Add(new(ax.Dir, T("langs de ", "along the ") + ax.Name, ax.Color));
        var all = edges.ToList();
        if (_restEdge is { } re) all.Add(re);
        foreach (var ed in all)
        {
            if (n is { } pn && Math.Abs(ed.Dot(pn)) > 1e-6) continue;
            // (an edge along an axis: the axis says it already)
            if (dirs.Any(x => Math.Abs(x.Dir.Dot(ed)) > 1 - 1e-9)) continue;
            dirs.Add(new(ed, T("evenwijdig aan een rand", "parallel to an edge"), InferColor));
            if (n is { } sn && !dirs.Any(x => Math.Abs(x.Dir.Dot(sn.Cross(ed))) > 1 - 1e-9))
                dirs.Add(new(sn.Cross(ed).Normalized(), T("loodrecht op een rand", "square to an edge"), InferColor));
        }
        return dirs;
    }

    /// <summary>
    /// The point the mouse infers from <paramref name="from"/>: in a plane (<paramref name="onPlane"/>: the mouse on it)
    /// the direction within 3° of the way to the mouse, in space the direction whose line passes within 15 pixels of the
    /// mouse on screen (the nearest); the length rounded to 0.1 mm. Null: none.
    /// </summary>
    private (Vec3 P, InferDir Dir)? InferFrom(Vec3 from, Point pos, Vec3? onPlane, IReadOnlyList<InferDir> dirs)
    {
        static double Round(double len) => Math.Round(len * Display * 10) / 10 / Display;
        if (onPlane is { } p)
        {
            var d = p - from;
            if (d.Length < 1e-9) return null;
            foreach (var dir in dirs)
            {
                double along = d.Dot(dir.Dir);
                if (Math.Abs(along) >= d.Length * Math.Cos(3 * Math.PI / 180)) return (from + dir.Dir * Round(along), dir);
            }
            return null;
        }
        if (Viewport3DHelper.GetRay(View.Viewport, pos) is not { } ray) return null;
        var org = V(ray.Origin); var look = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        (Vec3 P, InferDir Dir, double Px)? best = null;
        foreach (var dir in dirs)
        {
            // the point on the line from `from` along the direction nearest to the mouse ray
            double b = dir.Dir.Dot(look);
            if (1 - b * b < 1e-9) continue;
            var w0 = from - org;
            double t = (b * w0.Dot(look) - w0.Dot(dir.Dir)) / (1 - b * b);
            var q = from + dir.Dir * Round(t);
            double px = (Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(q)) - pos).Length;
            if (px < 15 && (q - from).Length > 1e-9 && (best == null || px < best.Value.Px)) best = (q, dir, px);
        }
        return best is { } bb ? (bb.P, bb.Dir) : null;
    }

    /// <summary>The corner the mouse last rested on (within 10 pixels: of the face under it, or on the work plane), to
    /// line up with along an axis (SketchUp's dotted "in line with a point"); null: none yet.</summary>
    private Vec3? _restPoint;

    /// <summary>Remembers the corner the mouse is on (see <see cref="_restPoint"/>).</summary>
    private void RestOnPoint(Point pos)
    {
        if (_doc == null) return;
        if (FaceAt(pos) is { } h)
        {
            var body = _doc.Bodies[h.Face.Body];
            foreach (var c in body.Faces[h.Face.Face].Loops.SelectMany(l => l))
                if (body.Edges[c.Edge] is { Start: >= 0 } e && body.Vertices[c.Forward ? e.Start : e.End] is var v && (Screen2(v) - pos).Length < 10) { _restPoint = v; return; }
        }
        foreach (var v in GroundCorners())
            if ((Screen2(v) - pos).Length < 10) { _restPoint = v; return; }
    }

    /// <summary>
    /// A point lined up with the corner rested on (see <see cref="_restPoint"/>) along an axis lying in the plane through
    /// <paramref name="q"/> with normal <paramref name="n"/>: on the line from <paramref name="from"/> along
    /// <paramref name="dir"/> where it crosses such a line, or else the point of such a line nearest q; only when it lies
    /// within 12 pixels of the mouse. With the corner (taken into the plane) it lines up with, and the axis's colour.
    /// </summary>
    private (Vec3 P, Vec3 Corner, Color Color)? LineUp(Vec3 q, Vec3 n, Point pos, Vec3? from = null, Vec3? dir = null)
    {
        if (_restPoint is not { } corner) return null;
        var axes = Axes;
        var best = Inference.LineUp(q, n, corner, axes.Select(a => a.Dir).ToList(), from, dir)
            .Select(x => (x.P, x.Corner, axes[x.Axis].Color, Px: (Screen2(x.P) - pos).Length))
            .Where(x => x.Px < 12 && (x.P - x.Corner).Length > 1e-9).OrderBy(x => x.Px).ToList();
        return best.Count > 0 ? (best[0].P, best[0].Corner, best[0].Color) : null;
    }

    /// <summary>What a point inferred on a face's plane is: where, its name (null: nothing special), the colour to draw
    /// toward it in (null: the usual), and the line to the corner it lines up with (null: none).</summary>
    private readonly record struct PlanePick(Vec3 P, string? Name, Color? Color, (Vec3 A, Vec3 B, Color Color)? Guide);

    /// <summary>
    /// The next point on face <paramref name="f"/>'s plane under the mouse, as the line infers it, for the tools that go
    /// from point to point there (arcs, ◇, ⬡): a corner, middle, centre or guide crossing near the mouse; else from
    /// <paramref name="from"/> (null: the first point) along an axis, parallel or square to an edge of the face or the
    /// edge rested on (see <see cref="InferFrom"/>), lined up with the corner rested on where that is near; else a point
    /// on an edge; else lined up with the corner rested on; else the mouse's point. Null: the mouse is off the plane.
    /// </summary>
    private PlanePick? PlanePoint(FaceRef f, Vec3? from, Point pos)
    {
        if (_doc == null || OnPlane(f, pos) is not { } p) return null;
        var n = NormalOf(f);
        var es = EdgeSnap(f, p, pos);
        if (es is { Kind: not (Snap.OnEdge or Snap.OnLoose) } hard) return new(hard.Point, hard.Kind.Name(), null, null);
        if (from is { } a)
        {
            var body = _doc.Bodies[f.Body];
            var edges = body.Faces[f.Face].Loops.SelectMany(l => l).Select(c => body.Edges[c.Edge]).Where(e => e.Curve is BrepLine && e.Start >= 0)
                .Select(e => (body.Vertices[e.End] - body.Vertices[e.Start]).Normalized());
            if (InferFrom(a, pos, p, InferDirs(n, edges)) is { } inf)
                return LineUp(inf.P, n, pos, a, inf.Dir.Dir) is { } lu
                    ? new(lu.P, inf.Dir.Name + " · " + T("in lijn met een punt", "in line with a point"), inf.Dir.Color, (lu.Corner, lu.P, lu.Color))
                    : new(inf.P, inf.Dir.Name, inf.Dir.Color, null);
        }
        if (es is { } on) return new(on.Point, on.Kind.Name(), null, null);
        if (LineUp(p, n, pos) is { } l2) return new(l2.P, T("in lijn met een punt", "in line with a point"), null, (l2.Corner, l2.P, l2.Color));
        return new(p, null, null, null);
    }
}
