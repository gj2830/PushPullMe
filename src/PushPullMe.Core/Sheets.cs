using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Faces without thickness (<see cref="BrepBody.Sheet"/>; DesignSpark's "Oppervlak"): a closed shape drawn in the air
/// becomes one, and pulling it makes a solid of it, as in SketchUp.
/// </summary>
public static class Sheets
{
    /// <summary>
    /// A flat face of its own from a closed run of points (the last one the first again) and the curve of each step (a
    /// line, or a circle running counter-clockwise round its normal from the step's start to its end), facing along
    /// <paramref name="n"/> (the run is turned round when it goes the other way round n).
    /// </summary>
    public static BrepBody Face(IReadOnlyList<Vec3> pts, IReadOnlyList<BrepCurve> curves, Vec3 n)
    {
        int m = curves.Count;
        if (m == 0 || pts.Count != m + 1) throw new ArgumentException("one curve per step, the last point the first again");
        n = n.Normalized();
        // counter-clockwise round n: the area of the run (arcs by points along them) taken along n
        var along = new List<Vec3>();
        for (int i = 0; i < m; i++)
            if (curves[i] is BrepCircle c) along.AddRange(Drawing.ArcPoints(c, pts[i], pts[i + 1], 8).SkipLast(1));
            else along.Add(pts[i]);
        var area = new Vec3(0, 0, 0);
        for (int i = 0; i < along.Count; i++) area += along[i].Cross(along[(i + 1) % along.Count]);
        var p = pts.ToList(); var cv = curves.ToList();
        if (area.Dot(n) < 0)
        {
            // the other way round: each arc turned round (from the step's new start)
            var rp = new List<Vec3>(); var rc = new List<BrepCurve>();
            for (int j = m; j >= 0; j--) rp.Add(pts[j]);
            for (int j = m - 1; j >= 0; j--)
                rc.Add(curves[j] is BrepCircle c ? c with { Normal = c.Normal * -1, XAxis = (pts[j + 1] - c.Centre).Normalized() } : curves[j]);
            p = rp; cv = rc;
        }
        var x = p.Count > 1 && (p[1] - p[0]).Length > 1e-12 ? (p[1] - p[0]) - n * (p[1] - p[0]).Dot(n) : Any(n);
        var plane = new BrepPlane(p[0], n, x.Normalized());
        // a whole circle: one ring edge
        if (m == 1 && cv[0] is BrepCircle ring)
            return new BrepBody
            {
                Vertices = [], Edges = [new BrepEdge(-1, -1, ring)],
                Faces = [new BrepFace(plane, false, [[new BrepCoedge(0, true)]])], Sheet = true,
            };
        var verts = p.Take(m).ToList();
        var edges = Enumerable.Range(0, m).Select(i => new BrepEdge(i, (i + 1) % m, cv[i])).ToList();
        return new BrepBody
        {
            Vertices = verts, Edges = edges,
            Faces = [new BrepFace(plane, false, [Enumerable.Range(0, m).Select(i => new BrepCoedge(i, true)).ToList()])], Sheet = true,
        };
    }

    private static Vec3 Any(Vec3 n) => (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n);

    /// <summary>
    /// One face of a sheet as a closed body without inside: the face and a twin facing the other way, sharing its
    /// edges (face 0 the face itself, face 1 the twin). Pulling one of the two out (an ordinary push/pull) makes the
    /// solid the face sweeps.
    /// </summary>
    public static BrepBody Twin(BrepBody sheet, int face)
    {
        var f = sheet.Faces[face];
        // only the edges and corners of this face, numbered anew
        var edgeMap = new Dictionary<int, int>(); var vertMap = new Dictionary<int, int>();
        var edges = new List<BrepEdge>(); var verts = new List<Vec3>();
        int V(int v)
        {
            if (v < 0) return v;
            if (!vertMap.TryGetValue(v, out int i)) { i = verts.Count; verts.Add(sheet.Vertices[v]); vertMap[v] = i; }
            return i;
        }
        int E(int e)
        {
            if (!edgeMap.TryGetValue(e, out int i))
            {
                var old = sheet.Edges[e];
                i = edges.Count; edges.Add(new BrepEdge(V(old.Start), V(old.End), old.Curve)); edgeMap[e] = i;
            }
            return i;
        }
        var loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(E(c.Edge), c.Forward)).ToList()).ToList();
        // the twin: every loop run the other way round
        var back = loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Reverse().Select(c => new BrepCoedge(c.Edge, !c.Forward)).ToList()).ToList();
        return new BrepBody { Vertices = verts, Edges = edges, Faces = [new BrepFace(f.Surface, f.Reversed, loops), new BrepFace(f.Surface, !f.Reversed, back)] };
    }

    /// <summary>The sheet without one of its faces (null when that was its only one): the edges and corners only that
    /// face used left out.</summary>
    public static BrepBody? Without(BrepBody sheet, int face)
    {
        if (sheet.Faces.Count <= 1) return null;
        var keep = sheet.Faces.Where((_, i) => i != face).ToList();
        var usedEdges = keep.SelectMany(f => f.Loops.SelectMany(l => l)).Select(c => c.Edge).Distinct().Order().ToList();
        var edgeMap = usedEdges.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        var usedVerts = usedEdges.SelectMany(e => new[] { sheet.Edges[e].Start, sheet.Edges[e].End }).Where(v => v >= 0).Distinct().Order().ToList();
        var vertMap = usedVerts.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        int V(int v) => v < 0 ? v : vertMap[v];
        return new BrepBody
        {
            Vertices = usedVerts.Select(v => sheet.Vertices[v]).ToList(),
            Edges = usedEdges.Select(e => sheet.Edges[e] with { Start = V(sheet.Edges[e].Start), End = V(sheet.Edges[e].End) }).ToList(),
            Faces = keep.Select(f => f with { Loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(edgeMap[c.Edge], c.Forward)).ToList()).ToList() }).ToList(),
            Sheet = true,
        };
    }
}
