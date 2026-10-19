using RsDocx.Core.Brep;
using RsDocx.Core.Geometry;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class BrepBooleans
{
    /// <summary>
    /// Two bodies combined (see <see cref="Booleans.Op"/>), exact, with curved faces: as one body or more, for each face
    /// the body (0 or 1) and face it came from. Throws when it is not (yet) possible.
    /// </summary>
    /// <summary>For finding out what happens (tests): told every step.</summary>
    public static Action<string>? Trace;

    public static List<(BrepBody Body, List<(int Body, int Face)> From)> Combine(BrepBody a, BrepBody b, Booleans.Op op)
    {
        var fa = Faces(a, 0); var fb = Faces(b, 1);
        var bodies = new[] { a, b };
        var cuts = fa.Concat(fb).ToDictionary(f => f, _ => new List<Cut>());
        var alias = new Dictionary<string, string>();
        for (int i = 0; i < fa.Count; i++)
            for (int j = 0; j < fb.Count; j++)
            {
                if (!Overlap(fa[i].Box, fb[j].Box)) continue;
                // each one's edges as far as they lie on the other (in one surface, or an edge where a surface touches);
                // an edge once per face (it is in the loops of two)
                foreach (var c in EdgesOn(fb[j], fa[i])) if (!cuts[fa[i]].Any(o => o.Key == c.Key && (o.Pts[0] - c.Pts[0]).Length + (o.Pts[^1] - c.Pts[^1]).Length < 1e-6 || o.Key == c.Key && (o.Pts[0] - c.Pts[^1]).Length + (o.Pts[^1] - c.Pts[0]).Length < 1e-6)) cuts[fa[i]].Add(c);
                foreach (var c in EdgesOn(fa[i], fb[j])) if (!cuts[fb[j]].Any(o => o.Key == c.Key && (o.Pts[0] - c.Pts[0]).Length + (o.Pts[^1] - c.Pts[^1]).Length < 1e-6 || o.Key == c.Key && (o.Pts[0] - c.Pts[^1]).Length + (o.Pts[^1] - c.Pts[0]).Length < 1e-6)) cuts[fb[j]].Add(c);
                if (fa[i].S.SameAs(fb[j].S)) continue;
                foreach (var c in Crossing(fa[i], fb[j], $"X{i}.{j}"))
                {
                    cuts[fa[i]].Add(c); cuts[fb[j]].Add(c);
                    Trace?.Invoke($"cut {c.Key}: {c.Pts.Count} pts, closed {c.Closed}, {c.Exact?.GetType().Name ?? "spline"}, from {c.Pts[0]} to {c.Pts[^1]}");
                }
            }
        // every face in pieces, each piece where it lies
        var verts = new List<Vec3>();
        int Vid(Vec3 p)
        {
            for (int i = 0; i < verts.Count; i++) if ((verts[i] - p).Length < Tol) return i;
            verts.Add(p); return verts.Count - 1;
        }
        var kept = new List<Piece>();
        bool meet = cuts.Values.Any(l => l.Count > 0);
        foreach (var f in fa.Concat(fb))
        {
            var other = f.Body == 0 ? fb : fa;
            var otherTris = other.SelectMany(g => g.Tris).ToList();
            foreach (var piece in Split(f, cuts[f], Vid, verts))
            {
                var where = Where(piece.At, f, other, otherTris);
                if (where != Booleans.Where.Outside) meet = true;
                bool keep, flip = false;
                if (f.Body == 0)
                    keep = op switch
                    {
                        Booleans.Op.Union => where is Booleans.Where.Outside or Booleans.Where.OnSame,
                        Booleans.Op.Subtract => where is Booleans.Where.Outside or Booleans.Where.OnOpposite,
                        _ => where is Booleans.Where.Inside or Booleans.Where.OnSame,
                    };
                else
                {
                    keep = op switch
                    {
                        Booleans.Op.Union => where == Booleans.Where.Outside,
                        Booleans.Op.Subtract => where == Booleans.Where.Inside,
                        _ => where == Booleans.Where.Inside,
                    };
                    flip = op == Booleans.Op.Subtract;
                }
                if (keep) kept.Add(piece with { Flip = flip });
                Trace?.Invoke($"face {f.Body}.{f.Index} {f.S.GetType().Name}: piece at {piece.At} {where} keep {keep}; loops {string.Join(" | ", piece.Loops.Select(l => string.Join(" ", l.Select(x => $"{x.S.Key}[{x.S.V0}{(x.Forward ? ">" : "<")}{x.S.V1}]"))))}");
            }
        }
        if (!meet) throw new PushPullException(Lang.T("Die delen raken elkaar niet; dat kan alleen als ze elkaar overlappen of met een vlak raken.", "Those parts do not touch; that only works when they overlap or touch with a face."));
        if (kept.Count == 0) throw new PushPullException(Lang.T("Dan blijft er niets over.", "Then nothing is left."));
        return Stitch(kept, verts, bodies);
    }

    /// <summary>A curved face of a body in triangles on its surface, wound outward (for showing it when
    /// <see cref="FaceGeometry.CurvedTriangles"/> cannot).</summary>
    public static List<(Vec3 A, Vec3 B, Vec3 C)> Triangles(BrepBody body, int face)
    {
        var f = Faces(body, 0)[face];
        double s = f.Face.Reversed ? -1 : 1;
        return f.Tris.Select(t =>
        {
            var n = (t.B - t.A).Cross(t.C - t.A);
            return n.Dot(f.S.Grad((t.A + t.B + t.C) * (1.0 / 3))) * s >= 0 ? t : (t.A, t.C, t.B);
        }).ToList();
    }

    // ======================================================================= edges in a face of the other body

    /// <summary>The edges of face <paramref name="g"/> as far as they lie on face <paramref name="f"/> (on its surface
    /// and within it), as cuts named after those edges: each edge followed along its exact curve, the places where it
    /// comes onto or leaves the face found by halving.</summary>
    private static IEnumerable<Cut> EdgesOn(FaceI g, FaceI f)
    {
        foreach (var l in g.Loops)
            foreach (var (e, fwd, pts0) in l)
            {
                var E = g.Of.Edges[e];
                bool ring = E.Start < 0 || E.Start == E.End;
                // (a quick look first: no point of it on the face's surface, nothing to do)
                if (!pts0.Any(p => Math.Abs(f.S.Dist(p)) < Tol)) continue;
                Vec3 At(double t) => E.Start >= 0 && FaceGeometry.EdgePoint(g.Of, E, t) is { } q ? q : Along(pts0, fwd ? t : 1 - t);
                bool On(double t) { var p = At(t); return Math.Abs(f.S.Dist(p)) < Tol && Inside(f, f.S.Map(p)) && !OnEdge(f, p); }
                int n = Math.Max(64, pts0.Count * 2);
                var ts = Enumerable.Range(0, n + 1).Select(i => (double)i / n).ToList();
                var on = ts.Select(On).ToList();
                if (!on.Any(x => x)) continue;
                if (ring && on.All(x => x)) { yield return EdgeCut(g, e, ts.Take(n).Select(At).ToList(), true); continue; }
                double Edge(double tIn, double tOut) { for (int k = 0; k < 60; k++) { double m = (tIn + tOut) / 2; if (On(m)) tIn = m; else tOut = m; } return tIn; }
                for (int k = 0; k <= n; k++)
                {
                    if (!on[k] || (k > 0 && on[k - 1])) continue;
                    int j = k;
                    while (j + 1 <= n && on[j + 1]) j++;
                    double t0 = k == 0 ? 0 : Edge(ts[k], ts[k - 1]), t1 = j == n ? 1 : Edge(ts[j], ts[j + 1]);
                    if (t1 - t0 < 1e-12) continue;
                    int count = Math.Max(2, (int)Math.Ceiling((t1 - t0) * n));
                    var pts = Enumerable.Range(0, count + 1).Select(q => At(t0 + (t1 - t0) * q / count)).ToList();
                    // (where it comes onto the face or leaves it: exactly on the edge of the face there)
                    if (k > 0) pts[0] = OntoEdge(f, g, e, pts[0]);
                    if (j < n) pts[^1] = OntoEdge(f, g, e, pts[^1]);
                    if (Length(pts, false) < 10 * Tol) continue;
                    yield return EdgeCut(g, e, pts, false);
                }
            }
    }

    /// <summary>A point of a run of points by the fraction of the way along it.</summary>
    private static Vec3 Along(List<Vec3> pts, double t)
    {
        double total = Length(pts, false), want = t * total, run = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double l = (pts[i + 1] - pts[i]).Length;
            if (run + l >= want) return pts[i] + (pts[i + 1] - pts[i]) * (l < 1e-30 ? 0 : (want - run) / l);
            run += l;
        }
        return pts[^1];
    }

    /// <summary>Where edge <paramref name="e"/> of face <paramref name="g"/> crosses the edge of face <paramref name="f"/>
    /// near p, exactly: on the surfaces either side of both edges (a corner of f when it is one).</summary>
    private static Vec3 OntoEdge(FaceI f, FaceI g, int e, Vec3 p)
    {
        (int Edge, double D)? best = null;
        foreach (var l in f.Loops)
            foreach (var (fe, _, pts) in l)
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double d = (Closest(pts[i], pts[i + 1], p) - p).Length;
                    if (best == null || d < best.Value.D) best = (fe, d);
                }
        if (best is not { } bb) return p;
        foreach (var l in f.Loops)
            foreach (var (fe, _, pts) in l)
                if (fe == bb.Edge) foreach (var v in new[] { pts[0], pts[^1] }) if ((v - p).Length < 1e-6) return v;
        var surfs = new List<Surf> { g.S };
        if (g.Across.TryGetValue(e, out var s2) && !s2.SameAs(g.S)) surfs.Add(s2);
        if (f.Across.TryGetValue(bb.Edge, out var s3) && !surfs.Any(x => x.SameAs(s3))) surfs.Add(s3);
        if (surfs.Count < 3) return p;
        var q = OnAll(surfs, p);
        return (q - p).Length < 1e-4 ? q : p;
    }

    private static Cut EdgeCut(FaceI g, int e, List<Vec3> pts, bool closed) => new()
    {
        Key = $"E{g.Body}:{e}", Pts = closed ? pts.Take(pts.Count - 1).ToList() : pts, S1 = g.S, S2 = g.Across.TryGetValue(e, out var s) ? s : g.S,
        Closed = closed, Exact = g.Of.Edges[e].Curve is BrepLine or BrepCircle or BrepEllipse ? g.Of.Edges[e].Curve : null,
    };

    /// <summary>Whether a point of the map is inside the face (its edge counting as in).</summary>
    private static bool Inside(FaceI f, (double U, double V) q)
    {
        bool inside = false;
        double tol = MapTol(f);
        foreach (var l in f.Loops2)
            for (int k = 0; k < l.Count; k++)
            {
                var a = l[k]; var b = l[(k + 1) % l.Count];
                if (SegDist(a, b, q) <= tol) return true;
                if ((a.V > q.V) != (b.V > q.V) && q.U < a.U + (b.U - a.U) * (q.V - a.V) / (b.V - a.V)) inside = !inside;
            }
        return inside;
    }

    /// <summary>The distance in a face's map within which points count as the same (its size × 1e-6).</summary>
    private static double MapTol(FaceI f)
    {
        var all = f.Loops2.SelectMany(l => l).ToList();
        if (all.Count == 0) return 1e-9;
        double size = Math.Max(all.Max(p => p.U) - all.Min(p => p.U), all.Max(p => p.V) - all.Min(p => p.V));
        return size * 1e-6;
    }

    private static double SegDist((double U, double V) a, (double U, double V) b, (double U, double V) q)
    {
        double du = b.U - a.U, dv = b.V - a.V, l2 = du * du + dv * dv;
        double t = l2 == 0 ? 0 : Math.Clamp(((q.U - a.U) * du + (q.V - a.V) * dv) / l2, 0, 1);
        double eu = a.U + du * t - q.U, ev = a.V + dv * t - q.V;
        return Math.Sqrt(eu * eu + ev * ev);
    }

    // ======================================================================= a face in pieces

    /// <summary>A stretch of a curve between two corners (or a whole ring): its name, its corners (-1 for a ring), its
    /// points (corner to corner, on the curve), the cut it belongs to (null: an edge of the body), and the edge.</summary>
    internal sealed record Stretch(string Key, int V0, int V1, List<Vec3> Pts, Cut? Cut, (int Body, int Edge)? Edge);

    /// <summary>A piece of a face: its loops (stretches with the way they are run), a point inside it, the face it is of,
    /// and whether it is turned round (a piece of what is taken away, as a face of the hollow).</summary>
    internal sealed record Piece(FaceI Of, List<List<(Stretch S, bool Forward)>> Loops, Vec3 At, List<(Vec3 A, Vec3 B, Vec3 C)> Tris, bool Flip = false);

    /// <summary>The face cut along its cuts: every piece of it (inside its edge).</summary>
    private static List<Piece> Split(FaceI f, List<Cut> cuts, Func<Vec3, int> vid, List<Vec3> verts)
    {
        double tol = MapTol(f);
        // the curves in the face: its edges and the cuts
        var curves = new List<(string Key, List<Vec3> Pts, bool Closed, Cut? Cut, (int, int)? Edge, Surf[] Surfs)>();
        foreach (var l in f.Loops)
            foreach (var (e, fwd, pts) in l)
            {
                bool ring = pts.Count > 2 && (pts[0] - pts[^1]).Length < Tol;
                var p = fwd ? pts : Enumerable.Reverse(pts).ToList(); // (each as the edge runs)
                curves.Add(($"E{f.Body}:{e}", ring ? p.Take(p.Count - 1).ToList() : p, ring, null, (f.Body, e), [f.S, f.Across.TryGetValue(e, out var s) ? s : f.S]));
            }
        foreach (var c in cuts) curves.Add((c.Key, c.Pts, c.Closed, c, null, [c.S1, c.S2]));
        // the corners on each curve: its ends, the ends of other curves on it, and where it crosses another
        var on = curves.Select(_ => new List<(int V, double T)>()).ToList();
        var maps = curves.Select(c => c.Pts.Select(f.S.Map).ToList()).ToList();
        (double T, double D) Param(int ci, (double U, double V) q)
        {
            var m = maps[ci]; int n = m.Count + (curves[ci].Closed ? 0 : -1);
            (double T, double D) best = (0, double.MaxValue);
            for (int i = 0; i < n; i++)
            {
                var a = m[i]; var b = m[(i + 1) % m.Count];
                double du = b.U - a.U, dv = b.V - a.V, l2 = du * du + dv * dv;
                double t = l2 == 0 ? 0 : Math.Clamp(((q.U - a.U) * du + (q.V - a.V) * dv) / l2, 0, 1);
                double d = Math.Sqrt(Math.Pow(a.U + du * t - q.U, 2) + Math.Pow(a.V + dv * t - q.V, 2));
                if (d < best.D) best = (i + t, d);
            }
            return best;
        }
        for (int i = 0; i < curves.Count; i++)
            if (!curves[i].Closed)
            {
                on[i].Add((vid(curves[i].Pts[0]), 0)); on[i].Add((vid(curves[i].Pts[^1]), curves[i].Pts.Count - 1));
            }
        for (int i = 0; i < curves.Count; i++)
            for (int j = 0; j < curves.Count; j++)
            {
                if (i == j) continue;
                // an end of j on i
                if (!curves[j].Closed)
                    foreach (var p in new[] { curves[j].Pts[0], curves[j].Pts[^1] })
                    {
                        var (t, d) = Param(i, f.S.Map(p));
                        if (d < tol * 50) on[i].Add((vid(p), t));
                    }
                // crossings (i < j, once)
                if (j < i) continue;
                var mi = maps[i]; var mj = maps[j];
                int ni = mi.Count + (curves[i].Closed ? 0 : -1), nj = mj.Count + (curves[j].Closed ? 0 : -1);
                for (int a = 0; a < ni; a++)
                    for (int b = 0; b < nj; b++)
                        if (Cross2(mi[a], mi[(a + 1) % mi.Count], mj[b], mj[(b + 1) % mj.Count]) is { } x && x.Ta > 1e-6 && x.Ta < 1 - 1e-6 && x.Tb > 1e-6 && x.Tb < 1 - 1e-6)
                        {
                            var p3 = curves[i].Pts[a] + (curves[i].Pts[(a + 1) % curves[i].Pts.Count] - curves[i].Pts[a]) * x.Ta;
                            var surfs = curves[i].Surfs.Concat(curves[j].Surfs).Distinct().ToList();
                            if (surfs.Count > 3) surfs = [f.S, curves[i].Surfs.First(s => s != f.S), curves[j].Surfs.First(s => s != f.S)];
                            int v = vid(OnAll(surfs, p3));
                            on[i].Add((v, a + x.Ta)); on[j].Add((v, b + x.Tb));
                        }
            }
        // each curve cut at its corners into stretches
        var stretches = new List<Stretch>();
        for (int i = 0; i < curves.Count; i++)
        {
            var (key, pts, closed, cut, edge, _) = curves[i];
            var corners = on[i].GroupBy(c => c.V).Select(g => g.First()).OrderBy(c => c.T).ToList();
            if (closed && corners.Count == 0) { stretches.Add(new Stretch(key, -1, -1, pts, cut, edge)); continue; }
            if (!closed && corners.Count < 2) continue;
            int m = corners.Count;
            for (int k = 0; k < (closed ? m : m - 1); k++)
            {
                var (v0, t0) = corners[k]; var (v1, t1) = corners[(k + 1) % m];
                if (closed && k == m - 1) t1 += pts.Count;
                var inner = new List<Vec3> { verts[v0] };
                for (int s = (int)Math.Floor(t0) + 1; s < t1; s++) if (s - t0 > 1e-9 && t1 - s > 1e-9) inner.Add(pts[s % pts.Count]);
                inner.Add(verts[v1]);
                if (v0 == v1 && inner.Count <= 2) continue;
                stretches.Add(new Stretch(key, v0, v1, inner, cut, edge));
            }
        }
        // the same stretch twice (an edge of the other body along this face's edge): once
        var unique = new List<Stretch>();
        foreach (var s in stretches)
        {
            var mid = s.Pts[s.Pts.Count / 2];
            double len = Length(s.Pts, false);
            bool Along(Stretch u) => u.Pts.Zip(u.Pts.Skip(1)).Min(x => (Closest(x.First, x.Second, mid) - mid).Length) < Math.Max(1e-7, len * 1e-3);
            if (unique.Any(u => ((u.V0 == s.V0 && u.V1 == s.V1) || (u.V0 == s.V1 && u.V1 == s.V0)) && Along(u)))
                continue;
            unique.Add(s);
        }
        return Regions(f, unique, verts);
    }

    private static (double Ta, double Tb)? Cross2((double U, double V) a0, (double U, double V) a1, (double U, double V) b0, (double U, double V) b1)
    {
        double rx = a1.U - a0.U, ry = a1.V - a0.V, qx = b1.U - b0.U, qy = b1.V - b0.V, den = rx * qy - ry * qx;
        if (Math.Abs(den) < 1e-30) return null;
        double ux = b0.U - a0.U, uy = b0.V - a0.V;
        double ta = (ux * qy - uy * qx) / den, tb = (ux * ry - uy * rx) / den;
        return ta >= 0 && ta <= 1 && tb >= 0 && tb <= 1 ? (ta, tb) : null;
    }

    /// <summary>The regions the stretches cut the face into (the face on the left of each loop in the map), inside it.</summary>
    private static List<Piece> Regions(FaceI f, List<Stretch> stretches, List<Vec3> verts)
    {
        // loose ends go
        var list = stretches.ToList();
        for (bool again = true; again;)
        {
            again = false;
            var degree = new Dictionary<int, int>();
            foreach (var s in list.Where(s => s.V0 >= 0)) { degree[s.V0] = degree.GetValueOrDefault(s.V0) + 1; degree[s.V1] = degree.GetValueOrDefault(s.V1) + 1; }
            int before = list.Count;
            list = list.Where(s => s.V0 < 0 || (degree[s.V0] > 1 && degree[s.V1] > 1) || s.V0 == s.V1).ToList();
            again = list.Count != before;
        }
        // at each corner the stretches leaving it, by angle in the map
        (double U, double V) M(Vec3 p) => f.S.Map(p);
        var outgoing = new Dictionary<int, List<(int S, bool Fwd, double Angle)>>();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.V0 < 0) continue;
            void Add(int v, bool fwd)
            {
                var a = M(fwd ? s.Pts[0] : s.Pts[^1]); var b = M(fwd ? s.Pts[1] : s.Pts[^2]);
                if (!outgoing.TryGetValue(v, out var l)) outgoing[v] = l = [];
                l.Add((i, fwd, Math.Atan2(b.V - a.V, b.U - a.U)));
            }
            Add(s.V0, true); Add(s.V1, false);
        }
        foreach (var l in outgoing.Values) l.Sort((x, y) => x.Angle.CompareTo(y.Angle));
        var seen = new HashSet<(int, bool)>();
        var cycles = new List<List<(Stretch S, bool Forward)>>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].V0 < 0)
            {
                cycles.Add([(list[i], true)]); cycles.Add([(list[i], false)]);
                continue;
            }
            foreach (bool fwd0 in new[] { true, false })
            {
                if (seen.Contains((i, fwd0))) continue;
                var cyc = new List<(Stretch, bool)>();
                int si = i; bool fwd = fwd0;
                while (seen.Add((si, fwd)))
                {
                    cyc.Add((list[si], fwd));
                    int at = fwd ? list[si].V1 : list[si].V0;
                    var around = outgoing[at];
                    int back = around.FindIndex(o => o.S == si && o.Fwd == !fwd);
                    var next = around[(back - 1 + around.Count) % around.Count];
                    (si, fwd) = (next.S, next.Fwd);
                }
                cycles.Add(cyc);
            }
        }
        List<(double U, double V)> Poly(List<(Stretch S, bool Forward)> c) =>
            c.SelectMany(x => (x.Forward ? x.S.Pts : Enumerable.Reverse(x.S.Pts)).SkipLast(1)).Select(M).ToList() is var p && p.Count > 0 ? p
            : c.SelectMany(x => x.Forward ? x.S.Pts : Enumerable.Reverse(x.S.Pts)).Select(M).ToList();
        double Area(List<(double U, double V)> p) { double s = 0; for (int k = 0; k < p.Count; k++) { var a = p[k]; var b = p[(k + 1) % p.Count]; s += a.U * b.V - b.U * a.V; } return s / 2; }
        bool In(List<(double U, double V)> p, (double U, double V) q)
        {
            bool inside = false;
            for (int k = 0; k < p.Count; k++)
            {
                var a = p[k]; var b = p[(k + 1) % p.Count];
                if ((a.V > q.V) != (b.V > q.V) && q.U < a.U + (b.U - a.U) * (q.V - a.V) / (b.V - a.V)) inside = !inside;
            }
            return inside;
        }
        var polys = cycles.Select(Poly).ToList();
        var areas = polys.Select(Area).ToList();
        double small = MapTol(f) * MapTol(f);
        var outers = Enumerable.Range(0, cycles.Count).Where(k => areas[k] > small).ToList();
        var regions = outers.ToDictionary(k => k, k => new List<int> { k });
        foreach (int h in Enumerable.Range(0, cycles.Count).Where(k => areas[k] < -small))
        {
            int best = -1; double bestArea = double.MaxValue;
            foreach (int o in outers)
            {
                if (areas[o] <= -areas[h] + small) continue;
                // (a point of the hole not on the region's own edge)
                var q = polys[h].FirstOrDefault(x => !polys[o].Any(y => Math.Abs(y.U - x.U) + Math.Abs(y.V - x.V) < MapTol(f)), polys[h][0]);
                if (In(polys[o], q) && areas[o] < bestArea) { best = o; bestArea = areas[o]; }
            }
            if (best >= 0) regions[best].Add(h);
        }
        var result = new List<Piece>();
        foreach (var (o, ks) in regions)
        {
            var loops2 = ks.Select(k => polys[k]).ToList();
            var tris2 = Triangulator.Triangulate(loops2.Select(l => (IReadOnlyList<(double X, double Y)>)l.Select(q => (q.U, q.V)).ToList()).ToList());
            if (tris2.Count == 0) continue;
            var flat = loops2.SelectMany(l => l).ToList();
            var (ta, tb, tc) = tris2.MaxBy(t => Math.Abs((flat[t.B].U - flat[t.A].U) * (flat[t.C].V - flat[t.A].V) - (flat[t.C].U - flat[t.A].U) * (flat[t.B].V - flat[t.A].V)));
            var mid = ((flat[ta].U + flat[tb].U + flat[tc].U) / 3, (flat[ta].V + flat[tb].V + flat[tc].V) / 3);
            // (only the pieces of the face itself: not those in its holes or outside it)
            if (!Inside(f, mid)) continue;
            double size = loops2.SelectMany(l => l).Select(f.S.Unmap).ToList() is var all3 ? Math.Max(all3.Max(p => p.X) - all3.Min(p => p.X), Math.Max(all3.Max(p => p.Y) - all3.Min(p => p.Y), all3.Max(p => p.Z) - all3.Min(p => p.Z))) : 1;
            var tris = Triangulate2(f.S, loops2, f.S is PlaneS ? 0 : size / 24);
            result.Add(new Piece(f, ks.Select(k => cycles[k]).ToList(), f.S.Unmap(mid), tris));
        }
        return result;
    }

    // ======================================================================= inside, outside or on

    private static Booleans.Where Where(Vec3 q, FaceI own, List<FaceI> other, List<(Vec3 A, Vec3 B, Vec3 C)> tris)
    {
        var nOwn = own.S.Grad(q) * (own.Face.Reversed ? -1 : 1);
        foreach (var g in other)
            if (g.S.SameAs(own.S) && Math.Abs(g.S.Dist(q)) < Tol && Inside(g, g.S.Map(q)))
                return (g.S.Grad(q) * (g.Face.Reversed ? -1 : 1)).Dot(nOwn) > 0 ? Booleans.Where.OnSame : Booleans.Where.OnOpposite;
        var d = new Vec3(0.5773, 0.6071, 0.5461).Normalized();
        int hits = 0;
        foreach (var (A, B, C) in tris)
        {
            var e1 = B - A; var e2 = C - A; var h = d.Cross(e2); double det = e1.Dot(h);
            if (Math.Abs(det) < 1e-24) continue;
            var s = q - A; double u = s.Dot(h) / det; if (u < 0 || u > 1) continue;
            var qq = s.Cross(e1); double v = d.Dot(qq) / det; if (v < 0 || u + v > 1) continue;
            if (e2.Dot(qq) / det > 1e-12) hits++;
        }
        return hits % 2 == 1 ? Booleans.Where.Inside : Booleans.Where.Outside;
    }

    // ======================================================================= one body again (or more)

    private static List<(BrepBody, List<(int, int)>)> Stitch(List<Piece> pieces, List<Vec3> verts, BrepBody[] bodies)
    {
        var edges = new List<BrepEdge>(); var keyOf = new Dictionary<string, int>(); var uses = new Dictionary<int, List<bool>>();
        // (the same stretch seen from its two faces: by its name, its corners, and its middle halfway along it)
        string Canon(Stretch s) => s.V0 < 0 ? $"{s.Key}|ring" : $"{s.Key}|{Math.Min(s.V0, s.V1)}|{Math.Max(s.V0, s.V1)}|{Q(Halfway(s.V0 <= s.V1 ? s.Pts : Enumerable.Reverse(s.Pts).ToList()))}";
        static string Q(Vec3 p) => $"{Math.Round(p.X * 1e5)},{Math.Round(p.Y * 1e5)},{Math.Round(p.Z * 1e5)}";
        var faces = new List<BrepFace>(); var from = new List<(int, int)>();
        foreach (var pc in pieces)
        {
            var f = pc.Of;
            bool reversed = f.Face.Reversed ^ pc.Flip;
            // loops traced counter-clockwise in the map: on a surface of revolution that is clockwise round its natural
            // normal (the map is a mirror); the loops must run counter-clockwise round the face's normal
            bool turn = (f.S is RevS) ^ reversed;
            var loops = new List<IReadOnlyList<BrepCoedge>>();
            foreach (var cyc in pc.Loops)
            {
                var run = turn ? Enumerable.Reverse(cyc).Select(x => (x.S, Forward: !x.Forward)).ToList() : cyc;
                var co = new List<BrepCoedge>();
                foreach (var (s, fwd) in run)
                {
                    string key = Canon(s);
                    if (!keyOf.TryGetValue(key, out int e))
                    {
                        e = edges.Count; keyOf[key] = e; uses[e] = [];
                        edges.Add(MakeEdge(s, fwd, bodies));
                    }
                    var E = edges[e];
                    // (the way this loop runs it: from its start to its end, or back)
                    var start = fwd ? s.Pts[0] : s.Pts[^1]; var second = fwd ? s.Pts[1] : s.Pts[^2];
                    bool forward = E.Start >= 0 ? (verts[E.Start] - start).Length < Tol && (E.Start != E.End || Along(E, verts, start, second)) : Along(E, verts, start, second);
                    uses[e].Add(forward);
                    co.Add(new BrepCoedge(e, forward));
                }
                loops.Add(co);
            }
            faces.Add(new BrepFace(f.Face.Surface, reversed, loops));
            from.Add((f.Body, f.Index));
        }
        foreach (var (key, e) in keyOf) if (uses[e].Count != 2 || uses[e][0] == uses[e][1]) Trace?.Invoke($"edge {key}: uses {string.Join(",", uses[e])}");
        if (uses.Values.Any(u => u.Count != 2 || u[0] == u[1]))
            throw new PushPullException(Lang.T("Zo klopt de vorm niet: raken de delen elkaar alleen in een rand of een punt?", "Like this the shape is not right: do the parts touch only in an edge or a point?"));
        // every piece of solid a body of its own
        var faceOfEdge = new Dictionary<int, List<int>>();
        for (int i = 0; i < faces.Count; i++) foreach (var c in faces[i].Loops.SelectMany(l => l)) { if (!faceOfEdge.TryGetValue(c.Edge, out var l2)) faceOfEdge[c.Edge] = l2 = []; l2.Add(i); }
        var part = Enumerable.Repeat(-1, faces.Count).ToArray(); int parts = 0;
        for (int f0 = 0; f0 < faces.Count; f0++)
        {
            if (part[f0] >= 0) continue;
            var stack = new Stack<int>(); stack.Push(f0); part[f0] = parts;
            while (stack.Count > 0) { int i = stack.Pop(); foreach (var c in faces[i].Loops.SelectMany(l => l)) foreach (int g in faceOfEdge[c.Edge]) if (part[g] < 0) { part[g] = parts; stack.Push(g); } }
            parts++;
        }
        var result = new List<(BrepBody, List<(int, int)>)>();
        for (int k = 0; k < parts; k++)
        {
            var fs = Enumerable.Range(0, faces.Count).Where(i => part[i] == k).ToList();
            var es = fs.SelectMany(i => faces[i].Loops.SelectMany(l => l)).Select(c => c.Edge).Distinct().OrderBy(e => e).ToList();
            var vs = es.SelectMany(e => new[] { edges[e].Start, edges[e].End }).Where(v => v >= 0).Distinct().OrderBy(v => v).ToList();
            var ei = es.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i); var vi = vs.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            var body = new BrepBody
            {
                Vertices = vs.Select(v => verts[v]).ToList(),
                Edges = es.Select(e => new BrepEdge(edges[e].Start >= 0 ? vi[edges[e].Start] : -1, edges[e].End >= 0 ? vi[edges[e].End] : -1, edges[e].Curve)).ToList(),
                Faces = fs.Select(i => new BrepFace(faces[i].Surface, faces[i].Reversed, faces[i].Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(ei[c.Edge], c.Forward)).ToList()).ToList())).ToList(),
            };
            // (a hollow inside: its faces turned inward, a negative volume)
            double volume = fs.Sum(i => pieces[i].Tris.Sum(t =>
            {
                var nrm = (t.B - t.A).Cross(t.C - t.A);
                var at = (t.A + t.B + t.C) * (1.0 / 3);
                var want = pieces[i].Of.S.Grad(at) * (faces[i].Reversed ? -1 : 1);
                double v = t.A.Dot(t.B.Cross(t.C)) / 6;
                return nrm.Dot(want) >= 0 ? v : -v;
            }));
            if (volume < 0) throw new PushPullException(Lang.T("Er zou een holte binnenin ontstaan; dat kan nog niet.", "There would be a hollow inside; that is not possible yet."));
            result.Add((body, fs.Select(i => from[i]).ToList()));
        }
        return result;
    }

    /// <summary>The point halfway along a run of points.</summary>
    private static Vec3 Halfway(List<Vec3> pts)
    {
        double half = Length(pts, false) / 2, run = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double l = (pts[i + 1] - pts[i]).Length;
            if (run + l >= half) return pts[i] + (pts[i + 1] - pts[i]) * (l < 1e-30 ? 0 : (half - run) / l);
            run += l;
        }
        return pts[^1];
    }

    /// <summary>Whether the edge, from <paramref name="start"/>, runs on toward <paramref name="next"/> (rather than the
    /// other way), judged by its curve's direction there.</summary>
    private static bool Along(BrepEdge e, List<Vec3> verts, Vec3 start, Vec3 next)
    {
        var d = next - start;
        switch (e.Curve)
        {
            case BrepCircle c: return c.Normal.Cross(start - c.Centre).Dot(d) > 0;
            case BrepEllipse el: return el.Normal.Cross(start - el.Centre).Dot(d) > 0;
            case BrepBSpline bs:
            {
                var p0 = bs.ControlPoints[0]; var p1 = bs.ControlPoints[1];
                return (p1 - p0).Dot(d) > 0;
            }
            default: return e.Start >= 0 && (verts[e.End] - verts[e.Start]).Dot(d) > 0;
        }
    }

    /// <summary>The edge for a stretch, its curve exact: the body's own edge's curve, the cut's line, circle or ellipse, or
    /// else a B-spline through its points; running the way the stretch is run first.</summary>
    private static BrepEdge MakeEdge(Stretch s, bool fwd, BrepBody[] bodies)
    {
        var pts = fwd ? s.Pts : Enumerable.Reverse(s.Pts).ToList();
        int v0 = fwd ? s.V0 : s.V1, v1 = fwd ? s.V1 : s.V0;
        BrepCurve? curve = s.Edge is { } e ? bodies[e.Body].Edges[e.Edge].Curve : s.Cut?.Exact;
        if (curve is BrepBSpline || curve == null)
        {
            // (enough points for a cubic: between few, more put on both surfaces)
            if (pts.Count < 6 && s.Cut is { } cut)
            {
                var more = new List<Vec3> { pts[0] };
                for (int i = 0; i + 1 < pts.Count; i++)
                    for (int k = 1; k <= 4; k++) more.Add(k == 4 ? pts[i + 1] : OnAll([cut.S1, cut.S2], pts[i] + (pts[i + 1] - pts[i]) * (k / 4.0)));
                pts = more;
            }
            curve = BSpline.Interpolate(pts, s.V0 < 0);
        }
        // a circle or an ellipse runs counter-clockwise round its normal from start to end: turned when this runs the
        // other way
        if (curve is BrepCircle c && c.Normal.Cross(pts[0] - c.Centre).Dot(pts[1] - pts[0]) < 0) curve = c with { Normal = c.Normal * -1 };
        if (curve is BrepEllipse el && el.Normal.Cross(pts[0] - el.Centre).Dot(pts[1] - pts[0]) < 0) curve = el with { Normal = el.Normal * -1 };
        return new BrepEdge(v0, v1, curve);
    }
}
