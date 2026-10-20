using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>Whether changed faces run through other parts of the model (touching is allowed, crossing is not).</summary>
public static class Intersections
{
    /// <summary>Margin (metres): what is closer than this to a surface counts as touching it.</summary>
    private const double Eps = 1e-8;

    /// <summary>The first pair of faces that cross, a changed face and another one that is not its neighbour; or null.</summary>
    public static (FaceRef A, FaceRef B)? FirstCrossing(SketchDocument doc, IReadOnlySet<FaceRef> changed, bool othersOnly = false)
    {
        foreach (var pair in Crossings(doc, changed, othersOnly)) return pair;
        return null;
    }

    /// <summary>
    /// The first pair of faces that cross in <paramref name="after"/> that did not cross already in
    /// <paramref name="before"/> (with the same face numbers): a model whose shown triangles already cross somewhere (an
    /// imperfect curved face) does not stop every change near it.
    /// </summary>
    public static (FaceRef A, FaceRef B)? FirstNewCrossing(SketchDocument before, SketchDocument after, IReadOnlySet<FaceRef> changed)
    {
        HashSet<(FaceRef, FaceRef)>? had = null;
        foreach (var pair in Crossings(after, changed))
        {
            had ??= Crossings(before, changed).ToHashSet();
            if (!had.Contains(pair)) return pair;
        }
        return null;
    }

    /// <summary>The pairs of faces that cross, a changed face and another one that is not its neighbour
    /// (<paramref name="othersOnly"/>: one of another body; a body that cannot cross itself is not tested against itself).</summary>
    public static IEnumerable<(FaceRef A, FaceRef B)> Crossings(SketchDocument doc, IReadOnlySet<FaceRef> changed, bool othersOnly = false)
    {
        // neighbours (a shared vertex or edge) meet along their edges by nature: not tested against each other
        var touch = new Dictionary<FaceRef, HashSet<(int body, int key)>>();
        HashSet<(int, int)> Keys(FaceRef f)
        {
            if (touch.TryGetValue(f, out var k)) return k;
            var body = doc.Bodies[f.Body];
            k = new();
            foreach (var c in body.Faces[f.Face].Loops.SelectMany(l => l))
            {
                var e = body.Edges[c.Edge];
                k.Add((f.Body, -1 - c.Edge));
                if (e.Start >= 0) { k.Add((f.Body, e.Start)); k.Add((f.Body, e.End)); }
            }
            return touch[f] = k;
        }
        var all = doc.AllTriangles().Select(x => (x.Face, Tris: x.Triangles, Box: Box(x.Triangles))).Where(x => x.Tris.Count > 0).ToList();
        foreach (var a in all.Where(x => changed.Contains(x.Face)))
            foreach (var b in all)
            {
                if (a.Face == b.Face || othersOnly && a.Face.Body == b.Face.Body || !Overlap(a.Box, b.Box)) continue;
                if (a.Face.Body == b.Face.Body && Keys(a.Face).Overlaps(Keys(b.Face))) continue;
                if (Any(a.Tris, b.Tris, b.Box)) yield return (a.Face, b.Face);
            }
    }

    private static bool Any(IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> aTris, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> bTris, (Vec3 Min, Vec3 Max) bBox)
    {
        foreach (var ta in aTris)
        {
            var ba = Box([ta]);
            if (!Overlap(ba, bBox)) continue;
            foreach (var tb in bTris)
                if (Overlap(ba, Box([tb])) && Cross(ta, tb)) return true;
        }
        return false;
    }

    private static (Vec3 Min, Vec3 Max) Box(IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> tris)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c })
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
        return (new Vec3(x0 - Eps, y0 - Eps, z0 - Eps), new Vec3(x1 + Eps, y1 + Eps, z1 + Eps));
    }

    private static bool Overlap((Vec3 Min, Vec3 Max) a, (Vec3 Min, Vec3 Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    /// <summary>Two triangles cross: an edge of one goes through the inside of the other.</summary>
    public static bool Cross((Vec3 A, Vec3 B, Vec3 C) s, (Vec3 A, Vec3 B, Vec3 C) t) =>
        Through(s.A, s.B, t) || Through(s.B, s.C, t) || Through(s.C, s.A, t) || Through(t.A, t.B, s) || Through(t.B, t.C, s) || Through(t.C, t.A, s);

    private static bool Through(Vec3 p, Vec3 q, (Vec3 A, Vec3 B, Vec3 C) t)
    {
        var (a, b, c) = t;
        var n = (b - a).Cross(c - a);
        double len = n.Length;
        if (len == 0) return false;
        n *= 1 / len;
        double dp = (p - a).Dot(n), dq = (q - a).Dot(n);
        if (!((dp > Eps && dq < -Eps) || (dp < -Eps && dq > Eps))) return false;
        var x = p + (q - p) * (dp / (dp - dq));
        bool Inside(Vec3 u, Vec3 v) { var e = v - u; return e.Cross(x - u).Dot(n) > Eps * e.Length; }
        return Inside(a, b) && Inside(b, c) && Inside(c, a);
    }
}
