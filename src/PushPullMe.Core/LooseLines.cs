using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A piece of a loose line: straight, or an arc running counter-clockwise round its normal from A to B.</summary>
public readonly record struct LoosePiece(Vec3 A, Vec3 B, BrepCircle? Arc)
{
    public double Length => Arc is { } c ? c.Radius * Sweep(c, A, B) : (B - A).Length;
    public Vec3 Middle => Arc is { } c ? Drawing.ArcPoints(c, A, B, 2)[1] : (A + B) * 0.5;
    public SketchLine ToLine() => Arc is { } c ? new SketchLine(A, B, c.Centre, c.Normal) : new SketchLine(A, B);

    /// <summary>The angle an arc turns from a to b (counter-clockwise round its normal; a full turn when they meet).</summary>
    public static double Sweep(BrepCircle c, Vec3 a, Vec3 b)
    {
        var ex = (a - c.Centre).Normalized(); var ey = c.Normal.Normalized().Cross(ex); var v = b - c.Centre;
        double t = Math.Atan2(v.Dot(ey), v.Dot(ex));
        return t <= 1e-12 ? t + 2 * Math.PI : t;
    }
}

/// <summary>
/// Loose lines that together (with the edges of a face) close a shape become edges, as in SketchUp: in the plane of a
/// face, the loose lines and arcs are cut where one ends on another or where they cross (lines with lines and with
/// arcs); a run of pieces from the face's edge to its edge, or a closed ring of them, is drawn on the face
/// (<see cref="SketchDocument.DrawPath(FaceRef, IReadOnlyList{Vec3}, IReadOnlyList{BrepCurve}?)"/>); what is left stays
/// loose.
/// </summary>
public static class LooseLines
{
    private const double Tol = 1e-7;

    /// <summary>Whether a loose line lies in the plane through o with normal n (an arc: also turning in it).</summary>
    public static bool InPlane(SketchLine l, Vec3 n, Vec3 o)
    {
        bool On(Vec3 q) => Math.Abs((q - o).Dot(n)) < Tol;
        return !l.Construction && On(l.Start) && On(l.End) && (l.Centre is not { } c || (On(c) && l.Axis is { } a && Math.Abs(a.Normalized().Dot(n)) > 1 - 1e-9));
    }

    /// <summary>The loose lines and arcs lying in a plane, cut where one ends on another or two cross, and where they
    /// meet or cross <paramref name="edges"/> (the edges of the faces there: a line running over the edge is cut on it).</summary>
    public static List<LoosePiece> Pieces(IEnumerable<SketchLine> lines, Vec3 n, Vec3 o, IReadOnlyList<LoosePiece>? edges = null)
    {
        var all = lines.Where(l => InPlane(l, n, o) && (l.End - l.Start).Length + (l.IsArc ? 1 : 0) > Tol)
            .Select(l => new LoosePiece(l.Start, l.End, l.Circle)).ToList();
        var cutters = edges == null ? all : all.Concat(edges).ToList();
        var ends = cutters.SelectMany(p => new[] { p.A, p.B }).ToList();
        var pieces = new List<LoosePiece>();
        foreach (var p in all)
        {
            // where along it (0..1 of a line, 0..sweep of an arc) it is cut
            var cuts = new List<double>();
            double span = p.Arc is { } pc ? LoosePiece.Sweep(pc, p.A, p.B) : 1;
            double scale = p.Arc is { } sc ? sc.Radius : (p.B - p.A).Length;
            double? At(Vec3 q)
            {
                if (p.Arc is { } c)
                {
                    if (Math.Abs((q - c.Centre).Length - c.Radius) > Tol) return null;
                    double t = LoosePiece.Sweep(c, p.A, q);
                    return t < span ? t : null;
                }
                var ab = p.B - p.A; double u = (q - p.A).Dot(ab) / ab.Dot(ab);
                return (p.A + ab * u - q).Length < Tol ? u : null;
            }
            foreach (var q in ends)
                if (At(q) is double t && t * scale > Tol && (span - t) * scale > Tol) cuts.Add(t);
            foreach (var other in cutters)
                foreach (var q in Crossings(p, other, n))
                    if (At(q) is double t && t * scale > Tol && (span - t) * scale > Tol) cuts.Add(t);
            cuts.Add(0); cuts.Add(span);
            cuts.Sort();
            Vec3 Point(double t) => p.Arc is { } c
                ? c.Centre + ((p.A - c.Centre).Normalized() * Math.Cos(t) + c.Normal.Normalized().Cross((p.A - c.Centre).Normalized()) * Math.Sin(t)) * c.Radius
                : p.A + (p.B - p.A) * t;
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                if ((cuts[i + 1] - cuts[i]) * scale <= Tol) continue;
                var a = i == 0 ? p.A : Point(cuts[i]); var b = i + 2 == cuts.Count ? p.B : Point(cuts[i + 1]);
                pieces.Add(new LoosePiece(a, b, p.Arc is { } c ? c with { XAxis = (a - c.Centre).Normalized() } : null));
            }
        }
        return pieces;
    }

    /// <summary>Where a line crosses another line or an arc (in the plane; arcs with arcs are not cut).</summary>
    private static IEnumerable<Vec3> Crossings(LoosePiece p, LoosePiece q, Vec3 n)
    {
        if (p.Arc == null && q.Arc == null)
        {
            var ab = p.B - p.A; var cd = q.B - q.A; double den = ab.Cross(cd).Dot(n);
            if (Math.Abs(den) < 1e-18) yield break;
            double s = (q.A - p.A).Cross(cd).Dot(n) / den, u = (q.A - p.A).Cross(ab).Dot(n) / den;
            if (s > 0 && s < 1 && u > -1e-9 && u < 1 + 1e-9) yield return p.A + ab * s;
            yield break;
        }
        var (line, arc) = p.Arc == null ? (p, q) : q.Arc == null ? (q, p) : (default(LoosePiece?), default(LoosePiece?));
        if (line is not { } l || arc is not { Arc: { } c } ar) yield break;
        // |A + t·d − centre| = r
        var d = l.B - l.A; var f = l.A - c.Centre;
        double qa = d.Dot(d), qb = 2 * f.Dot(d), qc = f.Dot(f) - c.Radius * c.Radius, disc = qb * qb - 4 * qa * qc;
        if (disc < 0) yield break;
        double span = LoosePiece.Sweep(c, ar.A, ar.B);
        foreach (double t in new[] { (-qb - Math.Sqrt(disc)) / (2 * qa), (-qb + Math.Sqrt(disc)) / (2 * qa) })
        {
            if (t < -1e-9 || t > 1 + 1e-9) continue;
            var x = l.A + d * t;
            if (LoosePiece.Sweep(c, ar.A, x) <= span + 1e-12) yield return x;
        }
    }

    /// <summary>
    /// Runs of pieces that would make a face, shortest first: from a point on the edge to another (through points that are
    /// not on it), then closed rings clear of the edge. <paramref name="onEdge"/> tells whether a point lies on the edge;
    /// <paramref name="passThrough"/> whether it is one a run may go on through (where it touches a hole).
    /// </summary>
    public static IEnumerable<List<int>> Candidates(IReadOnlyList<LoosePiece> pieces, Func<Vec3, bool> onEdge, Func<Vec3, bool>? passThrough = null)
    {
        // the points (merged within the tolerance) and which pieces meet there
        var pts = new List<Vec3>();
        int Id(Vec3 q) { int i = pts.FindIndex(p => (p - q).Length < Tol); if (i < 0) { pts.Add(q); i = pts.Count - 1; } return i; }
        var ends = pieces.Select(p => (A: Id(p.A), B: Id(p.B))).ToList();
        var boundary = pts.Select(q => onEdge(q) && passThrough?.Invoke(q) != true).ToList();
        // (a piece along the edge itself is no new edge)
        var usable = Enumerable.Range(0, pieces.Count).Where(i => !(boundary[ends[i].A] && boundary[ends[i].B] && onEdge(pieces[i].Middle))).ToList();
        var at = Enumerable.Range(0, pts.Count).Select(_ => new List<int>()).ToList();
        foreach (int i in usable) { at[ends[i].A].Add(i); if (ends[i].B != ends[i].A) at[ends[i].B].Add(i); }
        int Other(int piece, int v) => ends[piece].A == v ? ends[piece].B : ends[piece].A;
        double Len(int i) => pieces[i].Length;

        // shortest path (by length) from v0 over pieces not in 'banned', stopping at targets; interior points off the edge
        List<int>? Path(int v0, Func<int, bool> target, HashSet<int> banned)
        {
            var dist = new Dictionary<int, double> { [v0] = 0 }; var via = new Dictionary<int, int>();
            var queue = new PriorityQueue<int, double>(); queue.Enqueue(v0, 0);
            while (queue.TryDequeue(out int v, out double dv))
            {
                if (dv > dist[v]) continue;
                if (v != v0 && target(v))
                {
                    var path = new List<int>();
                    for (int w = v; w != v0; w = Other(via[w], w)) path.Add(via[w]);
                    path.Reverse();
                    return path;
                }
                if (v != v0 && boundary[v]) continue;
                foreach (int pc in at[v])
                {
                    if (banned.Contains(pc)) continue;
                    int w = Other(pc, v); double dw = dv + Len(pc);
                    if (!dist.TryGetValue(w, out var old) || dw < old - 1e-15) { dist[w] = dw; via[w] = pc; queue.Enqueue(w, dw); }
                }
            }
            return null;
        }

        var found = new List<List<int>>();
        for (int v = 0; v < pts.Count; v++)
            if (boundary[v] && Path(v, w => boundary[w], []) is { } p) found.Add(p);
        foreach (var p in found.OrderBy(p => p.Sum(Len))) yield return p;
        // rings: a piece and the shortest way back round from its other end (a full circle on its own)
        var rings = new List<List<int>>();
        foreach (int i in usable)
        {
            var (a, b) = ends[i];
            if (boundary[a] || boundary[b]) continue;
            if (a == b) { rings.Add([i]); continue; }
            if (Path(b, w => w == a, [i]) is { } back) rings.Add(back.Prepend(i).ToList());
        }
        foreach (var r in rings.OrderBy(r => r.Sum(Len))) yield return r;
    }

    /// <summary>The points of a run of pieces, in order, and the curve of each step (an arc turned round when run from B
    /// to A); a ring ends on its first point again, as a closed path does.</summary>
    public static (List<Vec3> Points, List<BrepCurve> Curves) Path(IReadOnlyList<LoosePiece> pieces, IReadOnlyList<int> run)
    {
        bool Near(Vec3 p, Vec3 q) => (p - q).Length < Tol;
        var first = pieces[run[0]];
        Vec3 start = first.A;
        if (run.Count > 1) { var next = pieces[run[1]]; start = Near(first.A, next.A) || Near(first.A, next.B) ? first.B : first.A; }
        var pts = new List<Vec3> { start }; var curves = new List<BrepCurve>();
        var cur = start;
        foreach (int i in run)
        {
            var p = pieces[i];
            bool forward = Near(cur, p.A);
            var to = forward ? p.B : p.A;
            curves.Add(p.Arc is { } c ? (forward ? c : c with { Normal = c.Normal * -1, XAxis = (p.B - c.Centre).Normalized() }) : new BrepLine());
            pts.Add(to);
            cur = to;
        }
        return (pts, curves);
    }
}
