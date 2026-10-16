using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Core;

/// <summary>
/// SketchUp's Follow Me along a path: a flat profile (a face with straight edges) swept along the outer edge of a flat
/// face (straight edges, closed), the profile standing square to the path where it touches it (at a corner of it). Every
/// edge of the path sweeps a prism of the profile, cut at the corners by the plane halfway between the two edges (a
/// mitre), so the prisms fit; together they are the swept solid.
/// </summary>
public static class Sweep
{
    private const double Tol = 1e-7;

    /// <summary>The prisms the profile face <paramref name="profile"/> sweeps along the outer edge of face
    /// <paramref name="path"/> (both of <paramref name="body"/>), one per edge of the path.</summary>
    public static List<BrepBody> Prisms(BrepBody body, int profile, int path) => Prisms(body, profile, body, path);

    /// <summary>As <see cref="Prisms(BrepBody, int, int)"/>, the profile a face of one body (a face without thickness, say)
    /// and the path the outer edge of a face of another.</summary>
    public static List<BrepBody> Prisms(BrepBody profileBody, int profile, BrepBody pathBody, int path)
    {
        var P = profileBody.Faces[profile]; var Q = pathBody.Faces[path];
        if (P.Surface is not BrepPlane || Q.Surface is not BrepPlane || P.Loops.Count != 1
            || P.Loops[0].Any(c => profileBody.Edges[c.Edge].Curve is not BrepLine) || Q.Loops[0].Any(c => pathBody.Edges[c.Edge].Curve is not BrepLine))
            throw new PushPullException(Lang.T("Langs een pad kan (nog) alleen met een plat profiel en een plat pad met rechte randen.", "Along a path only works (for now) with a flat profile and a flat path with straight edges."));
        var nP = FaceGeometry.Normal(P); var u = FaceGeometry.Normal(Q);
        var prof = Corners(FaceGeometry.LoopPoints(profileBody, P.Loops[0]));
        var cs = Corners(FaceGeometry.LoopPoints(pathBody, Q.Loops[0]));
        int m = cs.Count;
        // where the profile touches the path: a corner of the path in the profile's plane, on or in the profile
        double Dist(Vec3 c) => prof.Select((q, i) => (Closest(q, prof[(i + 1) % prof.Count], c) - c).Length).Min();
        int start = Enumerable.Range(0, m).Where(i => Math.Abs((cs[i] - prof[0]).Dot(nP)) < 1e-6).OrderBy(i => Dist(cs[i])).FirstOrDefault(-1);
        if (start < 0 || Dist(cs[start]) > 1e-6)
            throw new PushPullException(Lang.T("Het profiel moet het pad raken, bij een hoek van het pad.", "The profile must touch the path, at a corner of the path."));
        // along the path the way that leaves that corner square to the profile
        var next = (cs[(start + 1) % m] - cs[start]).Normalized(); var prev = (cs[(start - 1 + m) % m] - cs[start]).Normalized();
        int step = Math.Abs(next.Dot(nP)) > 0.999 ? 1 : Math.Abs(prev.Dot(nP)) > 0.999 ? -1 : 0;
        if (step == 0) throw new PushPullException(Lang.T("Het profiel moet haaks op het pad staan.", "The profile must stand square to the path."));
        var c = Enumerable.Range(0, m).Select(i => cs[((start + step * i) % m + m) % m]).ToList();
        var t = Enumerable.Range(0, m).Select(i => (c[(i + 1) % m] - c[i]).Normalized()).ToList();
        // the profile in the path's frame at the start: sideways and up
        var s0 = t[0].Cross(u);
        var local = prof.Select(p => ((p - c[0]).Dot(s0), (p - c[0]).Dot(u))).ToList();
        var mitre = Enumerable.Range(0, m).Select(i => (t[(i - 1 + m) % m] + t[i]) is var b && b.Length > 1e-9 ? b.Normalized() : throw new PushPullException(Lang.T("Het pad keert op zichzelf terug.", "The path turns back on itself."))).ToList();
        var prisms = new List<BrepBody>();
        for (int i = 0; i < m; i++)
        {
            var s = t[i].Cross(u);
            var a = c[i]; var b = c[(i + 1) % m]; var ma = mitre[i]; var mb = mitre[(i + 1) % m];
            var startPts = local.Select(q => { var off = s * q.Item1 + u * q.Item2; return a + off + t[i] * (-off.Dot(ma) / t[i].Dot(ma)); }).ToList();
            var endPts = local.Select(q => { var off = s * q.Item1 + u * q.Item2; return a + off + t[i] * ((b - a - off).Dot(mb) / t[i].Dot(mb)); }).ToList();
            prisms.Add(Prism(startPts, endPts));
        }
        return prisms;
    }

    /// <summary>A prism between two outlines with the same number of corners (start and end cap), as a body.</summary>
    private static BrepBody Prism(List<Vec3> a, List<Vec3> b)
    {
        int k = a.Count;
        var verts = a.Concat(b).Select(v => (v.X, v.Y, v.Z)).ToList();
        var faces = new List<int[]> { Enumerable.Range(0, k).Reverse().ToArray(), Enumerable.Range(k, k).ToArray() };
        for (int i = 0; i < k; i++) faces.Add([i, (i + 1) % k, k + (i + 1) % k, k + i]);
        // (the right way out: a positive volume)
        double vol = 0;
        foreach (var f in faces)
            for (int i = 1; i + 1 < f.Length; i++)
            {
                var p = verts[f[0]]; var q = verts[f[i]]; var r = verts[f[i + 1]];
                vol += new Vec3(p.X, p.Y, p.Z).Dot(new Vec3(q.X, q.Y, q.Z).Cross(new Vec3(r.X, r.Y, r.Z)));
            }
        if (vol < 0) faces = faces.Select(f => f.Reverse().ToArray()).ToList();
        return BrepShapes.FromPolyhedron(new Polyhedron { Vertices = verts, Faces = faces });
    }

    /// <summary>A loop's corners (points where it only runs straight on left out).</summary>
    private static List<Vec3> Corners(List<Vec3> pts)
    {
        var r = new List<Vec3>();
        foreach (var p in pts) if (r.Count == 0 || (r[^1] - p).Length > Tol) r.Add(p);
        if (r.Count > 1 && (r[0] - r[^1]).Length <= Tol) r.RemoveAt(r.Count - 1);
        var result = new List<Vec3>();
        for (int i = 0; i < r.Count; i++)
        {
            Vec3 a = r[(i - 1 + r.Count) % r.Count], b = r[i], c = r[(i + 1) % r.Count];
            if ((b - a).Normalized().Cross((c - b).Normalized()).Length > 1e-9) result.Add(b);
        }
        return result;
    }

    private static Vec3 Closest(Vec3 a, Vec3 b, Vec3 p)
    {
        var ab = b - a; double l2 = ab.Dot(ab);
        return l2 < 1e-30 ? a : a + ab * Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
    }
}
