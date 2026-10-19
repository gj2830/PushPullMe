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
}
