using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Booleans of bodies (as DesignSpark's and SketchUp's Union), exact: so far for bodies with flat faces and straight
/// edges only. Every face of one body is cut where the other body's faces cross it (and where faces of the two lie in
/// one plane, along each other's edges); the pieces of the one outside the other are kept (and of faces lying on each
/// other facing the same way, one), and stitched into one body.
/// </summary>
public static class Booleans
{
    private const double Tol = 1e-9;

    /// <summary>A flat face as polygons: its plane (n·x = d), axes in it (x × y = n) and loops (outer first,
    /// counter-clockwise round n; holes clockwise).</summary>
    private sealed record Poly(Vec3 N, double D, Vec3 X, Vec3 Y, List<List<Vec3>> Loops, int Face)
    {
        public (double U, double V) P2(Vec3 p) => (p.Dot(X), p.Dot(Y));
        public Vec3 P3((double U, double V) q) => X * q.U + Y * q.V + N * D;
    }

    /// <summary>The union of two bodies, as one body; for each of its faces, the body (0 or 1) and face it came from.
    /// Throws when they are not (yet) possible: curved faces, not overlapping or touching in a face, or touching only in
    /// an edge or a point.</summary>
    public static (BrepBody Body, List<(int Body, int Face)> From) Union(BrepBody a, BrepBody b)
    {
        var pa = Polys(a); var pb = Polys(b);
        var cutsA = pa.Select(_ => new List<(Vec3, Vec3)>()).ToList();
        var cutsB = pb.Select(_ => new List<(Vec3, Vec3)>()).ToList();
        for (int i = 0; i < pa.Count; i++)
            for (int j = 0; j < pb.Count; j++)
                Cut(pa[i], pb[j], cutsA[i], cutsB[j]);
        var trisA = Triangles(pa); var trisB = Triangles(pb);
        var kept = new List<(Poly Plane, List<List<Vec3>> Loops, int Body, int Face)>();
        bool any = false;
        for (int i = 0; i < pa.Count; i++)
            foreach (var (loops, at) in Pieces(pa[i], cutsA[i]))
            {
                var where = Classify(at, pa[i], pb, trisB);
                if (where != Where.Outside) any = true;
                if (where is Where.Outside or Where.OnSame) kept.Add((pa[i], loops, 0, pa[i].Face));
            }
        for (int j = 0; j < pb.Count; j++)
            foreach (var (loops, at) in Pieces(pb[j], cutsB[j]))
            {
                var where = Classify(at, pb[j], pa, trisA);
                if (where != Where.Outside) any = true;
                if (where == Where.Outside) kept.Add((pb[j], loops, 1, pb[j].Face));
            }
        if (!any) throw new PushPullException("Die delen raken elkaar niet; samenvoegen kan alleen als ze elkaar overlappen of met een vlak raken.");
        return Stitch(kept);
    }

    private static List<Poly> Polys(BrepBody body)
    {
        var result = new List<Poly>();
        for (int f = 0; f < body.Faces.Count; f++)
        {
            var face = body.Faces[f];
            if (face.Surface is not BrepPlane pl || face.Loops.SelectMany(l => l).Any(c => body.Edges[c.Edge].Curve is not BrepLine))
                throw new PushPullException("Samenvoegen kan (nog) alleen met delen die alleen platte vlakken en rechte randen hebben.");
            var n = FaceGeometry.Normal(face);
            var x = (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)); x = (x - n * x.Dot(n)).Normalized();
            var y = n.Cross(x);
            result.Add(new Poly(n, pl.Origin.Dot(n), x, y, face.Loops.Select(l => Clean(FaceGeometry.LoopPoints(body, l))).ToList(), f));
        }
        return result;
    }

    /// <summary>A loop's corners without repeats.</summary>
    private static List<Vec3> Clean(List<Vec3> pts)
    {
        var r = new List<Vec3>();
        foreach (var p in pts) if (r.Count == 0 || (r[^1] - p).Length > Tol) r.Add(p);
        if (r.Count > 1 && (r[0] - r[^1]).Length <= Tol) r.RemoveAt(r.Count - 1);
        return r;
    }

    // ---------------------------------------------------------------- where two faces cross

    private static void Cut(Poly p, Poly q, List<(Vec3, Vec3)> cutP, List<(Vec3, Vec3)> cutQ)
    {
        var dir = p.N.Cross(q.N);
        if (dir.Length < 1e-9)
        {
            // in one plane: each one's edges, where they lie on the other
            if (Math.Abs(p.D - q.D * p.N.Dot(q.N)) > Tol) return;
            foreach (var loop in q.Loops)
                for (int k = 0; k < loop.Count; k++)
                    foreach (var s in Clip(p, loop[k], loop[(k + 1) % loop.Count])) cutP.Add(s);
            foreach (var loop in p.Loops)
                for (int k = 0; k < loop.Count; k++)
                    foreach (var s in Clip(q, loop[k], loop[(k + 1) % loop.Count])) cutQ.Add(s);
            return;
        }
        dir = dir.Normalized();
        // a point on both planes
        var n1 = p.N; var n2 = q.N; double d1 = p.D, d2 = q.D;
        var origin = (n2.Cross(dir) * d1 + dir.Cross(n1) * d2) * (1 / n1.Cross(n2).Dot(dir));
        var ip = Intervals(p, origin, dir); var iq = Intervals(q, origin, dir);
        foreach (var (a0, a1) in ip)
            foreach (var (b0, b1) in iq)
            {
                double t0 = Math.Max(a0, b0), t1 = Math.Min(a1, b1);
                if (t1 - t0 <= Tol) continue;
                var s = (origin + dir * t0, origin + dir * t1);
                cutP.Add(s); cutQ.Add(s);
            }
    }

    /// <summary>Where (as parameters along it) the line through o along d is within the face (its edge counting as in).</summary>
    private static List<(double, double)> Intervals(Poly p, Vec3 o, Vec3 d)
    {
        var o2 = p.P2(o); var d2 = (U: d.Dot(p.X), V: d.Dot(p.Y));
        double Side((double U, double V) q) => (q.U - o2.U) * d2.V - (q.V - o2.V) * d2.U;
        double T((double U, double V) q) => (q.U - o2.U) * d2.U + (q.V - o2.V) * d2.V;
        var ts = new List<double>();
        foreach (var loop in p.Loops)
            for (int k = 0; k < loop.Count; k++)
            {
                var a = p.P2(loop[k]); var b = p.P2(loop[(k + 1) % loop.Count]);
                double sa = Side(a), sb = Side(b);
                if (Math.Abs(sa) <= Tol) ts.Add(T(a));
                if (Math.Abs(sb) <= Tol) ts.Add(T(b));
                if ((sa > Tol && sb < -Tol) || (sa < -Tol && sb > Tol)) { double u = sa / (sa - sb); ts.Add(T((a.U + (b.U - a.U) * u, a.V + (b.V - a.V) * u))); }
            }
        ts.Sort();
        var result = new List<(double, double)>();
        for (int k = 0; k + 1 < ts.Count; k++)
        {
            if (ts[k + 1] - ts[k] <= Tol) continue;
            double m = (ts[k] + ts[k + 1]) / 2;
            if (!InsideClosed(p, p.P2(o + d * m))) continue;
            if (result.Count > 0 && Math.Abs(result[^1].Item2 - ts[k]) <= Tol) result[^1] = (result[^1].Item1, ts[k + 1]);
            else result.Add((ts[k], ts[k + 1]));
        }
        return result;
    }

    /// <summary>The parts of segment a–b (in the face's plane) within the face (its edge counting as in).</summary>
    private static IEnumerable<(Vec3, Vec3)> Clip(Poly p, Vec3 a, Vec3 b)
    {
        double len = (b - a).Length;
        if (len <= Tol) yield break;
        var d = (b - a) * (1 / len);
        foreach (var (t0, t1) in Intervals(p, a, d))
        {
            double s0 = Math.Max(t0, 0), s1 = Math.Min(t1, len);
            if (s1 - s0 > Tol) yield return (a + d * s0, a + d * s1);
        }
    }

    /// <summary>Whether a point of the plane is inside the face or on its edge.</summary>
    private static bool InsideClosed(Poly p, (double U, double V) q)
    {
        bool inside = false;
        foreach (var loop in p.Loops)
            for (int k = 0; k < loop.Count; k++)
            {
                var a = p.P2(loop[k]); var b = p.P2(loop[(k + 1) % loop.Count]);
                if (SegDist(a, b, q) <= Tol) return true;
                if ((a.V > q.V) != (b.V > q.V) && q.U < a.U + (b.U - a.U) * (q.V - a.V) / (b.V - a.V)) inside = !inside;
            }
        return inside;
    }

    private static double SegDist((double U, double V) a, (double U, double V) b, (double U, double V) q)
    {
        double du = b.U - a.U, dv = b.V - a.V, l2 = du * du + dv * dv;
        double t = l2 == 0 ? 0 : Math.Clamp(((q.U - a.U) * du + (q.V - a.V) * dv) / l2, 0, 1);
        double eu = a.U + du * t - q.U, ev = a.V + dv * t - q.V;
        return Math.Sqrt(eu * eu + ev * ev);
    }

    // ---------------------------------------------------------------- a face cut into pieces

    /// <summary>The pieces of a face cut along the segments: each its loops (outer counter-clockwise, holes clockwise)
    /// and a point inside it.</summary>
    private static List<(List<List<Vec3>> Loops, Vec3 At)> Pieces(Poly p, List<(Vec3, Vec3)> cuts)
    {
        // all segments in the plane: the face's edges and the cuts
        var segs = new List<((double U, double V) A, (double U, double V) B)>();
        foreach (var loop in p.Loops)
            for (int k = 0; k < loop.Count; k++) segs.Add((p.P2(loop[k]), p.P2(loop[(k + 1) % loop.Count])));
        foreach (var (a, b) in cuts) segs.Add((p.P2(a), p.P2(b)));
        // their points (merged), every segment cut at the points on it and where it crosses another
        var pts = new List<(double U, double V)>();
        int Id((double U, double V) q)
        {
            for (int i = 0; i < pts.Count; i++) if (Math.Abs(pts[i].U - q.U) <= Tol && Math.Abs(pts[i].V - q.V) <= Tol) return i;
            pts.Add(q); return pts.Count - 1;
        }
        foreach (var (a, b) in segs) { Id(a); Id(b); }
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
                if (Cross(segs[i], segs[j]) is { } x) Id(x);
        var edges = new HashSet<(int, int)>();
        foreach (var (a, b) in segs)
        {
            double du = b.U - a.U, dv = b.V - a.V, l2 = du * du + dv * dv;
            if (l2 <= Tol * Tol) continue;
            var on = Enumerable.Range(0, pts.Count).Where(i => SegDist(a, b, pts[i]) <= Tol)
                .OrderBy(i => ((pts[i].U - a.U) * du + (pts[i].V - a.V) * dv) / l2).ToList();
            for (int k = 0; k + 1 < on.Count; k++)
                if (on[k] != on[k + 1]) edges.Add((Math.Min(on[k], on[k + 1]), Math.Max(on[k], on[k + 1])));
        }
        // loose ends (a cut that stops inside the face) go
        var adj = Enumerable.Range(0, pts.Count).Select(_ => new HashSet<int>()).ToList();
        foreach (var (u, v) in edges) { adj[u].Add(v); adj[v].Add(u); }
        bool pruned = true;
        while (pruned)
        {
            pruned = false;
            for (int v = 0; v < pts.Count; v++)
                if (adj[v].Count == 1) { int w = adj[v].First(); adj[v].Clear(); adj[w].Remove(v); pruned = true; }
        }
        // the cycles round every region (the face on the left: at each point, the next edge clockwise from the way back)
        var outgoing = adj.Select((s, v) => s.OrderBy(w => Math.Atan2(pts[w].V - pts[v].V, pts[w].U - pts[v].U)).ToList()).ToList();
        var seen = new HashSet<(int, int)>();
        var cycles = new List<List<int>>();
        for (int v0 = 0; v0 < pts.Count; v0++)
            foreach (int w0 in outgoing[v0])
            {
                if (seen.Contains((v0, w0))) continue;
                var cyc = new List<int>(); int u = v0, v = w0;
                while (seen.Add((u, v)))
                {
                    cyc.Add(u);
                    var around = outgoing[v]; int back = around.IndexOf(u);
                    int next = around[(back - 1 + around.Count) % around.Count];
                    (u, v) = (v, next);
                }
                cycles.Add(cyc);
            }
        double Area(List<int> c) { double s = 0; for (int k = 0; k < c.Count; k++) { var a = pts[c[k]]; var b = pts[c[(k + 1) % c.Count]]; s += a.U * b.V - b.U * a.V; } return s / 2; }
        bool In(List<int> c, (double U, double V) q)
        {
            bool inside = false;
            for (int k = 0; k < c.Count; k++)
            {
                var a = pts[c[k]]; var b = pts[c[(k + 1) % c.Count]];
                if ((a.V > q.V) != (b.V > q.V) && q.U < a.U + (b.U - a.U) * (q.V - a.V) / (b.V - a.V)) inside = !inside;
            }
            return inside;
        }
        var outers = cycles.Where(c => Area(c) > Tol * Tol).ToList();
        var holes = cycles.Where(c => Area(c) < -Tol * Tol).ToList();
        var regions = outers.Select(o => new List<List<int>> { o }).ToList();
        foreach (var h in holes)
        {
            // inside the smallest region round it (none: the outside of a group of regions)
            var probe = pts[h[0]];
            // (a point of the hole off the region's own edge: the middle of its first edge, nudged inward... a corner will do
            // when the region does not share it)
            int best = -1; double bestArea = double.MaxValue;
            for (int r = 0; r < outers.Count; r++)
            {
                if (outers[r].Intersect(h).Count() == h.Count) continue;
                var q = h.Select(i => pts[i]).FirstOrDefault(x => !outers[r].Any(i => Math.Abs(pts[i].U - x.U) <= Tol && Math.Abs(pts[i].V - x.V) <= Tol), probe);
                if (In(outers[r], q) && Area(outers[r]) < bestArea) { best = r; bestArea = Area(outers[r]); }
            }
            if (best >= 0) regions[best].Add(h);
        }
        var result = new List<(List<List<Vec3>>, Vec3)>();
        foreach (var region in regions)
        {
            var loops2 = region.Select(c => c.Select(i => pts[i]).ToList()).ToList();
            var tris = Triangulator.Triangulate(loops2.Select(l => (IReadOnlyList<(double X, double Y)>)l.Select(q => (q.U, q.V)).ToList()).ToList());
            if (tris.Count == 0) continue;
            var flat = loops2.SelectMany(l => l).ToList();
            var (ta, tb, tc) = tris.MaxBy(t => Math.Abs((flat[t.B].U - flat[t.A].U) * (flat[t.C].V - flat[t.A].V) - (flat[t.C].U - flat[t.A].U) * (flat[t.B].V - flat[t.A].V)));
            var mid = ((flat[ta].U + flat[tb].U + flat[tc].U) / 3, (flat[ta].V + flat[tb].V + flat[tc].V) / 3);
            // (only the pieces of the face itself: not those in its holes)
            if (!InsideClosed(p, mid)) continue;
            result.Add((loops2.Select(l => l.Select(p.P3).ToList()).ToList(), p.P3(mid)));
        }
        return result;
    }

    /// <summary>Where two segments cross or one touches the other (null: apart, or along each other).</summary>
    private static (double U, double V)? Cross(((double U, double V) A, (double U, double V) B) s, ((double U, double V) A, (double U, double V) B) t)
    {
        double rx = s.B.U - s.A.U, ry = s.B.V - s.A.V, qx = t.B.U - t.A.U, qy = t.B.V - t.A.V;
        double den = rx * qy - ry * qx;
        if (Math.Abs(den) < 1e-20) return null;
        double ux = t.A.U - s.A.U, uy = t.A.V - s.A.V;
        double a = (ux * qy - uy * qx) / den, b = (ux * ry - uy * rx) / den;
        double ls = Math.Sqrt(rx * rx + ry * ry), lt = Math.Sqrt(qx * qx + qy * qy);
        if (a < -Tol / ls || a > 1 + Tol / ls || b < -Tol / lt || b > 1 + Tol / lt) return null;
        return (s.A.U + rx * a, s.A.V + ry * a);
    }

    // ---------------------------------------------------------------- inside, outside or on

    private enum Where { Outside, Inside, OnSame, OnOpposite }

    private static List<(Vec3 A, Vec3 B, Vec3 C)> Triangles(List<Poly> polys)
    {
        var result = new List<(Vec3, Vec3, Vec3)>();
        foreach (var p in polys)
        {
            var flat = p.Loops.SelectMany(l => l).ToList();
            foreach (var (a, b, c) in Triangulator.Triangulate(p.Loops.Select(l => (IReadOnlyList<(double X, double Y)>)l.Select(q => { var t = p.P2(q); return (t.U, t.V); }).ToList()).ToList()))
                result.Add((flat[a], flat[b], flat[c]));
        }
        return result;
    }

    private static Where Classify(Vec3 q, Poly own, List<Poly> other, List<(Vec3 A, Vec3 B, Vec3 C)> tris)
    {
        foreach (var o in other)
            if (Math.Abs(o.N.Dot(own.N)) > 1 - 1e-9 && Math.Abs(q.Dot(o.N) - o.D) <= Tol && InsideClosed(o, o.P2(q)))
                return o.N.Dot(own.N) > 0 ? Where.OnSame : Where.OnOpposite;
        // an odd number of the other's triangles crossed by a ray from it
        var d = new Vec3(0.5773, 0.6071, 0.5461).Normalized();
        int hits = 0;
        foreach (var (A, B, C) in tris)
        {
            var e1 = B - A; var e2 = C - A; var h = d.Cross(e2); double det = e1.Dot(h);
            if (Math.Abs(det) < 1e-20) continue;
            var s = q - A; double u = s.Dot(h) / det; if (u < 0 || u > 1) continue;
            var qq = s.Cross(e1); double v = d.Dot(qq) / det; if (v < 0 || u + v > 1) continue;
            if (e2.Dot(qq) / det > Tol) hits++;
        }
        return hits % 2 == 1 ? Where.Inside : Where.Outside;
    }

    // ---------------------------------------------------------------- one body again

    private static (BrepBody, List<(int, int)>) Stitch(List<(Poly Plane, List<List<Vec3>> Loops, int Body, int Face)> pieces)
    {
        var verts = new List<Vec3>();
        int Id(Vec3 q) { int i = verts.FindIndex(v => (v - q).Length <= 1e-8); if (i < 0) { verts.Add(q); i = verts.Count - 1; } return i; }
        var loopsOf = pieces.Select(pc => pc.Loops.Select(l => l.Select(Id).ToList()).ToList()).ToList();
        // a corner of one piece on the edge of another: that edge cut there too
        foreach (var loops in loopsOf)
            for (int li = 0; li < loops.Count; li++)
            {
                var l = loops[li]; var r = new List<int>();
                for (int k = 0; k < l.Count; k++)
                {
                    int i = l[k], j = l[(k + 1) % l.Count];
                    r.Add(i);
                    Vec3 a = verts[i], ab = verts[j] - a; double l2 = ab.Dot(ab);
                    r.AddRange(Enumerable.Range(0, verts.Count).Where(v => v != i && v != j)
                        .Select(v => (v, t: (verts[v] - a).Dot(ab) / l2)).Where(x => x.t > 0 && x.t < 1 && (a + ab * x.t - verts[x.v]).Length <= 1e-8)
                        .OrderBy(x => x.t).Select(x => x.v));
                }
                var c = new List<int>();
                foreach (int v in r) if (c.Count == 0 || c[^1] != v) c.Add(v);
                if (c.Count > 1 && c[0] == c[^1]) c.RemoveAt(c.Count - 1);
                loops[li] = c;
            }
        var edges = new List<BrepEdge>(); var edgeOf = new Dictionary<(int, int), int>(); var uses = new Dictionary<int, List<bool>>();
        var faces = new List<BrepFace>(); var from = new List<(int, int)>();
        for (int pi = 0; pi < pieces.Count; pi++)
        {
            var loops = new List<IReadOnlyList<BrepCoedge>>();
            foreach (var l in loopsOf[pi])
            {
                if (l.Count < 3) continue;
                var co = new List<BrepCoedge>();
                for (int k = 0; k < l.Count; k++)
                {
                    int i = l[k], j = l[(k + 1) % l.Count];
                    var key = (Math.Min(i, j), Math.Max(i, j));
                    if (!edgeOf.TryGetValue(key, out int e)) { e = edges.Count; edgeOf[key] = e; edges.Add(new BrepEdge(i, j, new BrepLine())); uses[e] = []; }
                    bool fwd = edges[e].Start == i;
                    uses[e].Add(fwd);
                    co.Add(new BrepCoedge(e, fwd));
                }
                loops.Add(co);
            }
            if (loops.Count == 0) continue;
            var pl = pieces[pi].Plane;
            faces.Add(new BrepFace(new BrepPlane(pl.N * pl.D, pl.N, pl.X), false, loops));
            from.Add((pieces[pi].Body, pieces[pi].Face));
        }
        if (uses.Values.Any(u => u.Count != 2 || u[0] == u[1]))
            throw new PushPullException("Zo samengevoegd klopt de vorm niet: raken de delen elkaar alleen in een rand of een punt?");
        // one piece of solid (else: apart, or a hollow inside)
        var faceOfEdge = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++) foreach (var c in faces[f].Loops.SelectMany(l => l)) { if (!faceOfEdge.TryGetValue(c.Edge, out var lst)) faceOfEdge[c.Edge] = lst = []; lst.Add(f); }
        var reached = new HashSet<int> { 0 }; var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0) { int f = stack.Pop(); foreach (var c in faces[f].Loops.SelectMany(l => l)) foreach (int g in faceOfEdge[c.Edge]) if (reached.Add(g)) stack.Push(g); }
        if (reached.Count != faces.Count) throw new PushPullException("Samengevoegd zijn het nog steeds losse delen (of zit er een holte in); dat kan nog niet.");
        return (new BrepBody { Vertices = verts, Edges = edges, Faces = faces }, from);
    }
}
