using RsDocx.Core.Brep;
using RsDocx.Core.Geometry;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Booleans of bodies with curved faces (planes, cylinders, cones, spheres and tori; edges lines, circles, ellipses and
/// B-splines), exact. As <see cref="Booleans"/> for flat ones, but every face is laid flat in a map of its own (a plane as
/// it is, a surface of revolution round its axis: the angle round it, and the height or tube angle as the distance from
/// the middle) where it is cut along the curves where the other body's faces cross it. Those curves are found on the
/// faces' triangles and then put exactly on both surfaces (Newton); they are lines, circles or ellipses where they can
/// be, else B-splines through such points. Pieces are kept as for flat faces and stitched with the exact curves.
/// </summary>
public static partial class BrepBooleans
{
    /// <summary>Distance (metres) within which points are the same and lie on a surface.</summary>
    private const double Tol = 1e-7;

    // ======================================================================= surfaces

    /// <summary>A surface: signed distance (along its natural normal), that normal, and its flat map.</summary>
    internal abstract class Surf
    {
        public abstract double Dist(Vec3 p);
        public abstract Vec3 Grad(Vec3 p);
        public Vec3 Project(Vec3 p)
        {
            for (int i = 0; i < 4; i++) { double d = Dist(p); if (Math.Abs(d) < 1e-14) break; p -= Grad(p) * d; }
            return p;
        }
        /// <summary>The map: a point of the surface to 2D and back (set up for one face).</summary>
        public abstract (double U, double V) Map(Vec3 p);
        public abstract Vec3 Unmap((double U, double V) q);
        /// <summary>The map made to fit a face with these points (for a surface of revolution: the range of its second
        /// parameter).</summary>
        public virtual void Fit(IEnumerable<Vec3> pts) { }
        public abstract bool SameAs(Surf o);
    }

    internal sealed class PlaneS(Vec3 o, Vec3 n) : Surf
    {
        public readonly Vec3 O = o, N = n.Normalized();
        readonly Vec3 X = Axes(n).X, Y = Axes(n).Y;
        public override double Dist(Vec3 p) => (p - O).Dot(N);
        public override Vec3 Grad(Vec3 p) => N;
        public override (double U, double V) Map(Vec3 p) => ((p - O).Dot(X), (p - O).Dot(Y));
        public override Vec3 Unmap((double U, double V) q) => O + X * q.U + Y * q.V;
        public override bool SameAs(Surf o) => o is PlaneS p && Math.Abs(Math.Abs(p.N.Dot(N)) - 1) < 1e-9 && Math.Abs(p.Dist(O)) < Tol;
    }

    /// <summary>A surface of revolution round an axis: the angle round it and a second parameter s (height, or tube
    /// angle on a torus); mapped to (e^(k(s − s0)) cos θ, e^(k(s − s0)) sin θ) so a band round it is a ring, without a seam.</summary>
    internal abstract class RevS(Vec3 c, Vec3 a, Vec3 x) : Surf
    {
        public readonly Vec3 C = c, A = a.Normalized();
        protected readonly Vec3 X = (x - a.Normalized() * x.Dot(a.Normalized())).Normalized();
        protected Vec3 Y => A.Cross(X);
        double s0, k = 1;
        protected abstract double S(Vec3 p);
        protected abstract Vec3 At(double theta, double s);
        protected double Theta(Vec3 p) { var d = p - C; return Math.Atan2(d.Dot(Y), d.Dot(X)); }
        public override (double U, double V) Map(Vec3 p) { double r = Math.Exp(k * (S(p) - s0)), t = Theta(p); return (r * Math.Cos(t), r * Math.Sin(t)); }
        public override Vec3 Unmap((double U, double V) q) => At(Math.Atan2(q.V, q.U), s0 + Math.Log(Math.Sqrt(q.U * q.U + q.V * q.V)) / k);
        public override void Fit(IEnumerable<Vec3> pts)
        {
            var s = pts.Select(S).ToList();
            if (s.Count == 0) return;
            double lo = s.Min(), hi = s.Max(), span = Math.Max(hi - lo, 1e-9);
            // (the face between radius e^0.5 and e^2.5)
            k = 2 / span; s0 = lo - 0.5 / k;
        }
        protected bool SameAxis(RevS o) => Math.Abs(Math.Abs(o.A.Dot(A)) - 1) < 1e-9 && ((o.C - C) - A * (o.C - C).Dot(A)).Length < Tol;
    }

    internal sealed class CylS(Vec3 c, Vec3 a, Vec3 x, double r) : RevS(c, a, x)
    {
        public readonly double R = r;
        Vec3 Radial(Vec3 p) { var d = p - C; return d - A * d.Dot(A); }
        public override double Dist(Vec3 p) => Radial(p).Length - R;
        public override Vec3 Grad(Vec3 p) => Radial(p).Normalized();
        protected override double S(Vec3 p) => (p - C).Dot(A);
        protected override Vec3 At(double t, double s) => C + A * s + (X * Math.Cos(t) + Y * Math.Sin(t)) * R;
        public override bool SameAs(Surf o) => o is CylS c && SameAxis(c) && Math.Abs(c.R - R) < Tol;
    }

    internal sealed class ConeS(Vec3 c, Vec3 a, Vec3 x, double r0, double half) : RevS(c, a, x)
    {
        public readonly double R0 = r0, Half = half;
        public override double Dist(Vec3 p)
        {
            var d = p - C; double h = d.Dot(A), rho = (d - A * h).Length;
            return (rho - R0 - h * Math.Tan(Half)) * Math.Cos(Half);
        }
        public override Vec3 Grad(Vec3 p)
        {
            var d = p - C; var radial = (d - A * d.Dot(A)).Normalized();
            return (radial * Math.Cos(Half) - A * Math.Sin(Half)).Normalized();
        }
        protected override double S(Vec3 p) => (p - C).Dot(A);
        protected override Vec3 At(double t, double s) => C + A * s + (X * Math.Cos(t) + Y * Math.Sin(t)) * (R0 + s * Math.Tan(Half));
        public override bool SameAs(Surf o) => o is ConeS k && SameAxis(k) && A.Dot(k.A) > 0 && Math.Abs(k.Half - Half) < 1e-9 && Math.Abs(k.Dist(C + X * R0)) < Tol;
    }

    internal sealed class SphereS(Vec3 c, double r, Vec3 a, Vec3 x) : RevS(c, a, x)
    {
        public readonly double R = r;
        public override double Dist(Vec3 p) => (p - C).Length - R;
        public override Vec3 Grad(Vec3 p) => (p - C).Normalized();
        protected override double S(Vec3 p) => (p - C).Dot(A);
        protected override Vec3 At(double t, double s) { double h = Math.Clamp(s, -R, R), rho = Math.Sqrt(Math.Max(R * R - h * h, 0)); return C + A * h + (X * Math.Cos(t) + Y * Math.Sin(t)) * rho; }
        public override bool SameAs(Surf o) => o is SphereS s && (s.C - C).Length < Tol && Math.Abs(s.R - R) < Tol;
    }

    internal sealed class TorusS(Vec3 c, Vec3 a, Vec3 x, double big, double small) : RevS(c, a, x)
    {
        public readonly double Big = big, Small = small;
        double mid;
        Vec3 Tube(Vec3 p) { var d = p - C; return C + (d - A * d.Dot(A)).Normalized() * Big; }
        public override double Dist(Vec3 p) => (p - Tube(p)).Length - Small;
        public override Vec3 Grad(Vec3 p) => (p - Tube(p)).Normalized();
        double Psi(Vec3 p) { var d = p - C; double h = d.Dot(A); return Math.Atan2(h, (d - A * h).Length - Big); }
        protected override double S(Vec3 p) { double s = Psi(p) - mid; while (s > Math.PI) s -= 2 * Math.PI; while (s <= -Math.PI) s += 2 * Math.PI; return s + mid; }
        protected override Vec3 At(double t, double s) => C + A * (Small * Math.Sin(s)) + (X * Math.Cos(t) + Y * Math.Sin(t)) * (Big + Small * Math.Cos(s));
        public override void Fit(IEnumerable<Vec3> pts)
        {
            var list = pts.ToList();
            if (list.Count > 0)
            {
                // (the tube angle measured round the middle of the face, so it does not wrap)
                double sx = list.Sum(p => Math.Cos(Psi(p))), sy = list.Sum(p => Math.Sin(Psi(p)));
                mid = Math.Atan2(sy, sx);
            }
            base.Fit(list);
        }
        public override bool SameAs(Surf o) => o is TorusS t && SameAxis(t) && Math.Abs(t.Big - Big) < Tol && Math.Abs(t.Small - Small) < Tol && Math.Abs((t.C - C).Dot(A)) < Tol;
    }

    internal static (Vec3 X, Vec3 Y) Axes(Vec3 n)
    {
        n = n.Normalized();
        var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        x = (x - n * x.Dot(n)).Normalized();
        return (x, n.Cross(x));
    }

    internal static Surf SurfOf(BrepSurface s) => s switch
    {
        BrepPlane p => new PlaneS(p.Origin, p.Normal),
        BrepCylinder c => new CylS(c.Origin, c.Axis, c.XAxis, c.Radius),
        BrepCone k => new ConeS(k.Origin, k.Axis, k.XAxis, k.Radius, k.HalfAngle),
        BrepSphere sp => new SphereS(sp.Centre, sp.Radius, sp.Axis, sp.XAxis),
        BrepTorus t => new TorusS(t.Centre, t.Axis, t.XAxis, t.MajorRadius, t.MinorRadius),
        _ => throw new PushPullException(Lang.T("Dat kan (nog) niet met vrije-vormvlakken.", "That is not possible (yet) with free-form faces.")),
    };

    // ======================================================================= points on two or three surfaces

    /// <summary>The point nearest p on all the surfaces (two: a curve where they meet; three: a corner): Newton with the
    /// smallest step (least norm).</summary>
    internal static Vec3 OnAll(IReadOnlyList<Surf> s, Vec3 p)
    {
        for (int it = 0; it < 30; it++)
        {
            var f = s.Select(x => x.Dist(p)).ToArray();
            if (f.All(v => Math.Abs(v) < 1e-14)) break;
            var g = s.Select(x => x.Grad(p)).ToArray();
            // step = Gᵀ (G Gᵀ)⁻¹ f
            int m = s.Count;
            var M = new double[m, m];
            for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) M[i, j] = g[i].Dot(g[j]);
            var lam = Solve(M, f);
            if (lam == null) break;
            var step = new Vec3(0, 0, 0);
            for (int i = 0; i < m; i++) step += g[i] * lam[i];
            p -= step;
            if (step.Length < 1e-16) break;
        }
        return p;
    }

    private static double[]? Solve(double[,] M, double[] b)
    {
        int n = b.Length;
        var a = (double[,])M.Clone(); var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = Enumerable.Range(c, n - c).MaxBy(r => Math.Abs(a[r, c]));
            if (Math.Abs(a[piv, c]) < 1e-14) return null;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (x[c], x[piv]) = (x[piv], x[c]); }
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = a[r, c] / a[c, c];
                for (int k = 0; k < n; k++) a[r, k] -= f * a[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int i = 0; i < n; i++) x[i] /= a[i, i];
        return x;
    }

    // ======================================================================= faces

    /// <summary>A face of one of the two bodies: its surface (mapped for it), its edges' points, its triangles, and for
    /// each edge the surface of the face on the other side.</summary>
    internal sealed class FaceI
    {
        public required int Body, Index;
        public required BrepBody Of;
        public required BrepFace Face;
        public required Surf S;
        public required List<List<(int Edge, bool Forward, List<Vec3> Pts)>> Loops;
        public required List<(Vec3 A, Vec3 B, Vec3 C)> Tris;
        public required Dictionary<int, Surf> Across;
        public (Vec3 Lo, Vec3 Hi) Box;
        public List<List<(double U, double V)>> Loops2 = [];
    }

    private static List<FaceI> Faces(BrepBody b, int which)
    {
        var faceOf = new Dictionary<int, List<int>>();
        for (int f = 0; f < b.Faces.Count; f++)
            foreach (var c in b.Faces[f].Loops.SelectMany(l => l)) { if (!faceOf.TryGetValue(c.Edge, out var l2)) faceOf[c.Edge] = l2 = []; l2.Add(f); }
        var surfs = b.Faces.Select(f => SurfOf(f.Surface)).ToList();
        var result = new List<FaceI>();
        for (int f = 0; f < b.Faces.Count; f++)
        {
            var face = b.Faces[f];
            var loops = face.Loops.Select(l => l.Select(c =>
            {
                var pts = FaceGeometry.EdgePoints(b, b.Edges[c.Edge], Math.PI / 90);
                if (!c.Forward) pts.Reverse();
                return (c.Edge, c.Forward, Pts: pts);
            }).ToList()).ToList();
            surfs[f].Fit(loops.SelectMany(l => l.SelectMany(e => e.Pts)));
            var across = new Dictionary<int, Surf>();
            foreach (var c in face.Loops.SelectMany(l => l))
                if (faceOf[c.Edge].FirstOrDefault(g => g != f, -1) is int g && g >= 0) across[c.Edge] = surfs[g];
            var fi = new FaceI { Body = which, Index = f, Of = b, Face = face, S = surfs[f], Loops = loops, Tris = [], Across = across };
            fi.Loops2 = loops.Select(l => Flat(l.SelectMany(e => e.Pts.Take(e.Pts.Count - 1)).Select(fi.S.Map).ToList())).ToList();
            fi.Tris = Tessellate(fi);
            var all = fi.Tris.SelectMany(t => new[] { t.A, t.B, t.C }).Concat(loops.SelectMany(l => l.SelectMany(e => e.Pts))).ToList();
            fi.Box = (new Vec3(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)), new Vec3(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
            result.Add(fi);
        }
        return result;
    }

    private static List<(double U, double V)> Flat(List<(double U, double V)> l)
    {
        var r = new List<(double U, double V)>();
        foreach (var q in l) if (r.Count == 0 || Math.Abs(r[^1].U - q.U) + Math.Abs(r[^1].V - q.V) > 1e-12) r.Add(q);
        if (r.Count > 1 && Math.Abs(r[0].U - r[^1].U) + Math.Abs(r[0].V - r[^1].V) < 1e-12) r.RemoveAt(r.Count - 1);
        return r;
    }

    /// <summary>A face in triangles that lie on its surface: its map triangulated, the triangles made smaller until they
    /// follow the surface closely, their corners put on it.</summary>
    internal static List<(Vec3 A, Vec3 B, Vec3 C)> Tessellate(FaceI f)
    {
        if (f.S is PlaneS) return Triangulate2(f.S, f.Loops2, 0);
        double size = f.Loops.SelectMany(l => l.SelectMany(e => e.Pts)).ToList() is var all && all.Count > 0
            ? Math.Max((all.Max(p => p.X) - all.Min(p => p.X)), Math.Max(all.Max(p => p.Y) - all.Min(p => p.Y), all.Max(p => p.Z) - all.Min(p => p.Z))) : 1;
        return Triangulate2(f.S, f.Loops2, size / 400);
    }

    /// <summary>The loops of a map triangulated; triangles whose sides bend away from the surface by more than
    /// <paramref name="sag"/> (0: none, a plane) cut in four, their corners on the surface.</summary>
    internal static List<(Vec3 A, Vec3 B, Vec3 C)> Triangulate2(Surf s, List<List<(double U, double V)>> loops, double sag)
    {
        if (loops.Count == 0 || loops[0].Count < 3) return [];
        // (outer loop counter-clockwise, holes clockwise, in the map)
        double Area(List<(double U, double V)> l) { double a = 0; for (int i = 0; i < l.Count; i++) { var p = l[i]; var q = l[(i + 1) % l.Count]; a += p.U * q.V - q.U * p.V; } return a / 2; }
        var ordered = loops.OrderByDescending(l => Math.Abs(Area(l))).Select((l, i) => (i == 0) == (Area(l) > 0) ? l : Enumerable.Reverse(l).ToList()).ToList();
        var flat = ordered.SelectMany(l => l).ToList();
        var tris = Triangulator.Triangulate(ordered.Select(l => (IReadOnlyList<(double X, double Y)>)l.Select(q => (q.U, q.V)).ToList()).ToList());
        var result = new List<(Vec3, Vec3, Vec3)>();
        void Add((double U, double V) a, (double U, double V) b, (double U, double V) c, int depth)
        {
            Vec3 A = s.Unmap(a), B = s.Unmap(b), C = s.Unmap(c);
            (double U, double V) M((double U, double V) p, (double U, double V) q) => ((p.U + q.U) / 2, (p.V + q.V) / 2);
            double Bend((double U, double V) p, (double U, double V) q, Vec3 P, Vec3 Q) => (s.Unmap(M(p, q)) - (P + Q) * 0.5).Length;
            if (sag > 0 && depth < 7 && Math.Max(Bend(a, b, A, B), Math.Max(Bend(b, c, B, C), Bend(c, a, C, A))) > sag)
            {
                var ab = M(a, b); var bc = M(b, c); var ca = M(c, a);
                Add(a, ab, ca, depth + 1); Add(ab, b, bc, depth + 1); Add(ca, bc, c, depth + 1); Add(ab, bc, ca, depth + 1);
                return;
            }
            result.Add((A, B, C));
        }
        foreach (var (a, b, c) in tris) Add(flat[a], flat[b], flat[c], 0);
        return result;
    }

    // ======================================================================= where two faces cross

    /// <summary>A curve cutting a face: its points (on both surfaces), the two surfaces it lies on, its exact curve (or
    /// null: a B-spline is made per piece), whether it is closed; and its name (shared by the two faces it cuts).</summary>
    internal sealed class Cut
    {
        public required string Key;
        public required List<Vec3> Pts;
        public required Surf S1, S2;
        public BrepCurve? Exact;
        public bool Closed;
    }

    /// <summary>The curves where two faces cross: from where their triangles cross, put on both surfaces.</summary>
    private static List<Cut> Crossing(FaceI a, FaceI b, string key)
    {
        if (!Overlap(a.Box, b.Box) || a.S.SameAs(b.S)) return [];
        // the curve where the two surfaces meet worked out exactly where that is plain, cut to the two faces
        if (Analytic(a.S, b.S, Grow(a.Box, b.Box)) is { } curves)
        {
            var result = new List<Cut>();
            int n = 0;
            foreach (var c in curves)
                result.AddRange(Clip(a, b, c, $"{key}#{n++}"));
            return result;
        }
        return ByTriangles(a, b, key);
    }

    /// <summary>A curve where two surfaces meet, exactly: its points by parameter (over the range given; closed: the
    /// range is once round) and the curve itself.</summary>
    internal sealed record Plain(Func<double, Vec3> At, double T0, double T1, bool Closed, BrepCurve Exact);

    private static (Vec3 Lo, Vec3 Hi) Grow((Vec3 Lo, Vec3 Hi) a, (Vec3 Lo, Vec3 Hi) b) =>
        (new Vec3(Math.Min(a.Lo.X, b.Lo.X), Math.Min(a.Lo.Y, b.Lo.Y), Math.Min(a.Lo.Z, b.Lo.Z)), new Vec3(Math.Max(a.Hi.X, b.Hi.X), Math.Max(a.Hi.Y, b.Hi.Y), Math.Max(a.Hi.Z, b.Hi.Z)));

    /// <summary>The curves where two surfaces meet, when they are lines, circles or ellipses worked out directly (empty:
    /// they do not meet); null when the pair is not one of those.</summary>
    private static List<Plain>? Analytic(Surf a, Surf b, (Vec3 Lo, Vec3 Hi) box)
    {
        if (b is PlaneS && a is not PlaneS) (a, b) = (b, a);
        double reach = (box.Hi - box.Lo).Length + 1e-6;
        var mid = (box.Lo + box.Hi) * 0.5;
        Plain Line(Vec3 o, Vec3 d)
        {
            d = d.Normalized();
            o += d * (mid - o).Dot(d);
            return new Plain(t => o + d * t, -reach, reach, false, new BrepLine());
        }
        Plain Circle(Vec3 c, Vec3 n, double r)
        {
            var (x, y) = Axes(n);
            return new Plain(t => c + (x * Math.Cos(t) + y * Math.Sin(t)) * r, 0, 2 * Math.PI, true, new BrepCircle(c, n.Normalized(), x, r));
        }
        switch (a, b)
        {
            case (PlaneS p, PlaneS q):
            {
                var d = p.N.Cross(q.N);
                if (d.Length < 1e-12) return [];
                var o = (q.N.Cross(d) * p.N.Dot(p.O) + d.Cross(p.N) * q.N.Dot(q.O)) * (1 / p.N.Cross(q.N).Dot(d));
                return [Line(o, d)];
            }
            case (PlaneS p, CylS c):
            {
                double cos = p.N.Dot(c.A);
                if (Math.Abs(cos) < 1e-12)
                {
                    // along the axis: none, or two lines
                    double dist = p.Dist(c.C);
                    if (Math.Abs(dist) >= c.R - 1e-12) return [];
                    var foot = c.C - p.N * dist; var w = p.N.Cross(c.A).Normalized(); double h = Math.Sqrt(c.R * c.R - dist * dist);
                    return [Line(foot + w * h, c.A), Line(foot - w * h, c.A)];
                }
                var centre = c.C + c.A * (-p.Dist(c.C) / cos);
                if (Math.Abs(Math.Abs(cos) - 1) < 1e-12) return [Circle(centre, p.N, c.R)];
                var u = c.A.Cross(p.N).Normalized(); var v = p.N.Cross(u).Normalized(); double big = c.R / Math.Abs(cos);
                return [new Plain(t => centre + v * (Math.Cos(t) * big) + u * (Math.Sin(t) * c.R), 0, 2 * Math.PI, true, new BrepEllipse(centre, p.N, v, big, c.R))];
            }
            case (PlaneS p, SphereS sp):
            {
                double d = p.Dist(sp.C);
                if (Math.Abs(d) >= sp.R - 1e-12) return [];
                return [Circle(sp.C - p.N * d, p.N, Math.Sqrt(sp.R * sp.R - d * d))];
            }
            case (PlaneS p, ConeS k) when Math.Abs(Math.Abs(p.N.Dot(k.A)) - 1) < 1e-12:
            {
                double h = -p.Dist(k.C) / p.N.Dot(k.A); double r = k.R0 + h * Math.Tan(k.Half);
                return r > 1e-12 ? [Circle(k.C + k.A * h, p.N, r)] : [];
            }
            case (PlaneS p, TorusS t) when Math.Abs(Math.Abs(p.N.Dot(t.A)) - 1) < 1e-12:
            {
                double h = -p.Dist(t.C) / p.N.Dot(t.A);
                if (Math.Abs(h) >= t.Small - 1e-12) return [];
                double w = Math.Sqrt(t.Small * t.Small - h * h);
                return [Circle(t.C + t.A * h, p.N, t.Big + w), Circle(t.C + t.A * h, p.N, t.Big - w)];
            }
            case (PlaneS p, TorusS t) when Math.Abs(p.N.Dot(t.A)) < 1e-12 && Math.Abs(p.Dist(t.C)) < 1e-12:
            {
                var w = p.N.Cross(t.A).Normalized();
                return [Circle(t.C + w * t.Big, p.N, t.Small), Circle(t.C - w * t.Big, p.N, t.Small)];
            }
            case (CylS c1, CylS c2) when Math.Abs(Math.Abs(c1.A.Dot(c2.A)) - 1) < 1e-12:
            {
                // parallel: where their circles meet, square to the axis
                var d = (c2.C - c1.C) - c1.A * (c2.C - c1.C).Dot(c1.A); double dist = d.Length;
                if (dist < 1e-12 || dist >= c1.R + c2.R - 1e-12 || dist <= Math.Abs(c1.R - c2.R) + 1e-12) return [];
                double x = (dist * dist + c1.R * c1.R - c2.R * c2.R) / (2 * dist), y = Math.Sqrt(c1.R * c1.R - x * x);
                var ux = d.Normalized(); var uy = c1.A.Cross(ux);
                return [Line(c1.C + ux * x + uy * y, c1.A), Line(c1.C + ux * x - uy * y, c1.A)];
            }
        }
        // round one axis: circles at the heights where they are equally far from it
        if (a is RevS ra && b is RevS rb && Math.Abs(Math.Abs(ra.A.Dot(rb.A)) - 1) < 1e-12 && ((rb.C - ra.C) - ra.A * (rb.C - ra.C).Dot(ra.A)).Length < 1e-12)
        {
            var cs = new List<Plain>();
            var dir = Axes(ra.A).X;
            double hm = (mid - ra.C).Dot(ra.A), lo = hm - reach, hi = hm + reach;
            double Gap(double h) => ProjectAlong(ra, ra.C + ra.A * h, dir) is { } q ? rb.Dist(q) : double.NaN;
            const int N = 2000;
            double prevH = lo, prev = Gap(lo);
            for (int i = 1; i <= N; i++)
            {
                double h = lo + (hi - lo) * i / N, g = Gap(h);
                if (!double.IsNaN(prev) && !double.IsNaN(g) && Math.Sign(prev) != Math.Sign(g))
                {
                    double x0 = prevH, x1 = h, g0 = prev;
                    for (int k = 0; k < 80; k++) { double m = (x0 + x1) / 2, gm = Gap(m); if (Math.Sign(gm) == Math.Sign(g0)) { x0 = m; g0 = gm; } else x1 = m; }
                    if (ProjectAlong(ra, ra.C + ra.A * ((x0 + x1) / 2), dir) is { } q)
                    {
                        var centre = ra.C + ra.A * (q - ra.C).Dot(ra.A);
                        double r = ((q - ra.C) - ra.A * (q - ra.C).Dot(ra.A)).Length;
                        if (r > 1e-12) cs.Add(Circle(centre, ra.A, r));
                    }
                }
                prevH = h; prev = g;
            }
            return cs;
        }
        return null;
    }

    /// <summary>The point of a surface of revolution at the height of p, in the direction <paramref name="dir"/> (square
    /// to the axis) from the axis; null where it has none (a sphere above its top).</summary>
    private static Vec3? ProjectAlong(RevS s, Vec3 p, Vec3 dir)
    {
        var o = s.C + s.A * (p - s.C).Dot(s.A);
        double Far(double r) => s.Dist(o + dir * r);
        double r0 = 0, r1 = 1e-6, f0 = Far(r0);
        for (int i = 0; i < 80 && Math.Sign(Far(r1)) == Math.Sign(f0); i++) r1 *= 2;
        if (Math.Sign(Far(r1)) == Math.Sign(f0)) return null;
        for (int k = 0; k < 100; k++) { double m = (r0 + r1) / 2; if (Math.Sign(Far(m)) == Math.Sign(f0)) r0 = m; else r1 = m; }
        return o + dir * ((r0 + r1) / 2);
    }

    /// <summary>The parts of a curve where it lies on both faces: its points there (exact, from the curve), the ends
    /// worked out where it leaves a face (on the surface across that face's edge).</summary>
    private static IEnumerable<Cut> Clip(FaceI a, FaceI b, Plain c, string key)
    {
        double size = Math.Min(Size(a), Size(b));
        int n = c.Closed ? 720 : Math.Clamp((int)Math.Ceiling((c.T1 - c.T0) / Math.Max(size / 300, 1e-9)), 200, 20000);
        // (a curve along an edge of either face is that edge: it comes in where the faces lie in one surface)
        bool In(double t) { var p = c.At(t); return Inside(a, a.S.Map(p)) && Inside(b, b.S.Map(p)) && !OnEdge(a, p) && !OnEdge(b, p); }
        int m = c.Closed ? n : n + 1;
        var inside = Enumerable.Range(0, m).Select(i => In(c.T0 + (c.T1 - c.T0) * i / n)).ToList();
        if (!inside.Any(x => x)) yield break;
        if (c.Closed && inside.All(x => x))
        {
            yield return new Cut { Key = key, Pts = Enumerable.Range(0, n).Select(i => c.At(c.T0 + (c.T1 - c.T0) * i / n)).ToList(), S1 = a.S, S2 = b.S, Closed = true, Exact = c.Exact };
            yield break;
        }
        // (a closed curve read from a point outside, so no run goes round past its start)
        int start = c.Closed ? inside.IndexOf(false) : 0;
        bool InAt(int k) => inside[(start + k) % m];
        double T(int k) => c.T0 + (c.T1 - c.T0) * (double)(start + k) / n;
        double Edge(double tIn, double tOut) { for (int it = 0; it < 60; it++) { double h = (tIn + tOut) / 2; if (In(h)) tIn = h; else tOut = h; } return tIn; }
        int part = 0;
        for (int k = 0; k < m; k++)
        {
            if (!InAt(k) || (k > 0 && InAt(k - 1))) continue;
            int j = k;
            while (j + 1 < m && InAt(j + 1)) j++;
            double tStart = k == 0 ? T(0) : Edge(T(k), T(k - 1));
            double tEnd = j == m - 1 ? T(m - 1) : Edge(T(j), T(j + 1));
            if (tEnd - tStart < 1e-12) continue;
            int count = Math.Max(8, (int)Math.Ceiling((tEnd - tStart) / (c.T1 - c.T0) * n));
            var pts = Enumerable.Range(0, count + 1).Select(q => c.At(tStart + (tEnd - tStart) * q / count)).ToList();
            pts[0] = EndOn(a, b, pts[0]); pts[^1] = EndOn(a, b, pts[^1]);
            if (Length(pts, false) < 10 * Tol) continue;
            yield return new Cut { Key = $"{key}.{part++}", Pts = pts, S1 = a.S, S2 = b.S, Closed = false, Exact = c.Exact };
        }
    }

    /// <summary>The curves where two faces cross, found where their triangles cross and put on both surfaces (for pairs
    /// of surfaces whose curve is not plain).</summary>
    private static List<Cut> ByTriangles(FaceI a, FaceI b, string key)
    {
        var segs = new List<(Vec3, Vec3)>();
        foreach (var ta in a.Tris)
        {
            var ba = TriBox(ta);
            foreach (var tb in b.Tris)
                if (Overlap(ba, TriBox(tb)) && TriTri(ta, tb) is { } s) segs.Add(s);
        }
        // (joined within the bend the triangles may have from the surface: where a triangle was cut in four next to one
        // that was not, the crossings do not quite meet)
        var chains = Chain(segs, Math.Max(1e-8, Math.Min(Size(a), Size(b)) / 400));
        var result = new List<Cut>();
        int n = 0;
        foreach (var (pts0, closed) in chains)
        {
            if (pts0.Count < 2) continue;
            var surfs = new[] { a.S, b.S };
            var pts = pts0.Select(p => OnAll(surfs, p)).ToList();
            // the ends: where the curve meets an edge of either face (a corner of three surfaces)
            if (!closed)
            {
                pts[0] = EndOn(a, b, pts[0]);
                pts[^1] = EndOn(a, b, pts[^1]);
            }
            pts = Resample(pts, closed, surfs);
            if (Length(pts, closed) < 10 * Tol) continue;
            var cut = new Cut { Key = $"{key}#{n++}", Pts = pts, S1 = a.S, S2 = b.S, Closed = closed };
            cut.Exact = Recognise(pts, closed);
            result.Add(cut);
        }
        return result;
    }

    private static double Size(FaceI f) => Math.Max(f.Box.Hi.X - f.Box.Lo.X, Math.Max(f.Box.Hi.Y - f.Box.Lo.Y, f.Box.Hi.Z - f.Box.Lo.Z));

    private static bool Overlap((Vec3 Lo, Vec3 Hi) a, (Vec3 Lo, Vec3 Hi) b) =>
        a.Lo.X <= b.Hi.X + Tol && b.Lo.X <= a.Hi.X + Tol && a.Lo.Y <= b.Hi.Y + Tol && b.Lo.Y <= a.Hi.Y + Tol && a.Lo.Z <= b.Hi.Z + Tol && b.Lo.Z <= a.Hi.Z + Tol;

    private static (Vec3 Lo, Vec3 Hi) TriBox((Vec3 A, Vec3 B, Vec3 C) t) =>
        (new Vec3(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)), Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z))),
         new Vec3(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)), Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)), Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z))));

    /// <summary>Where two triangles cross: a segment (null: they do not, or lie in one plane). Its ends are where an edge
    /// of one crosses the other's plane, worked out the same way for both triangles at an edge (so runs join up).</summary>
    private static (Vec3, Vec3)? TriTri((Vec3 A, Vec3 B, Vec3 C) t, (Vec3 A, Vec3 B, Vec3 C) u)
    {
        var n1 = (t.B - t.A).Cross(t.C - t.A); var n2 = (u.B - u.A).Cross(u.C - u.A);
        if (n1.Length < 1e-24 || n2.Length < 1e-24) return null;
        n1 = n1.Normalized(); n2 = n2.Normalized();
        if (n1.Cross(n2).Length < 1e-12) return null;
        var dir = n1.Cross(n2).Normalized();
        // the points where a triangle's edges cross the other plane (an edge always the same way round: by its ends' order)
        List<Vec3>? Part((Vec3 A, Vec3 B, Vec3 C) tri, Vec3 n, double d)
        {
            var p = new[] { tri.A, tri.B, tri.C };
            var r = new List<Vec3>();
            for (int i = 0; i < 3; i++)
            {
                var (a, b) = (p[i], p[(i + 1) % 3]);
                if (Less(b, a)) (a, b) = (b, a);
                double sa = a.Dot(n) - d, sb = b.Dot(n) - d;
                if (sa == 0) r.Add(a);
                if (sb == 0) r.Add(b);
                if ((sa > 0 && sb < 0) || (sa < 0 && sb > 0)) r.Add(a + (b - a) * (sa / (sa - sb)));
            }
            return r.Count >= 2 ? r : null;
        }
        var pt = Part(t, n2, u.A.Dot(n2)); var pu = Part(u, n1, t.A.Dot(n1));
        if (pt == null || pu == null) return null;
        var all = pt.Select(q => (Q: q, T: q.Dot(dir), Of: 0)).Concat(pu.Select(q => (Q: q, T: q.Dot(dir), Of: 1))).ToList();
        var lo0 = pt.Min(q => q.Dot(dir)); var hi0 = pt.Max(q => q.Dot(dir)); var lo1 = pu.Min(q => q.Dot(dir)); var hi1 = pu.Max(q => q.Dot(dir));
        double t0 = Math.Max(lo0, lo1), t1 = Math.Min(hi0, hi1);
        if (t1 - t0 < 1e-13) return null;
        // (the actual points at those ends)
        var start = all.Where(x => x.T == t0).Select(x => x.Q).First();
        var end = all.Where(x => x.T == t1).Select(x => x.Q).First();
        return (start, end);
    }

    private static bool Less(Vec3 a, Vec3 b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    /// <summary>Segments joined end to end into runs (closed when they come round): their ends merged into points first
    /// (within a nanometre), then walked from point to point; a point where more than two meet ends a run.</summary>
    private static List<(List<Vec3> Pts, bool Closed)> Chain(List<(Vec3, Vec3)> segs, double join)
    {
        var pts = new List<Vec3>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        int Id(Vec3 p)
        {
            var k = ((long)Math.Floor(p.X / join / 4), (long)Math.Floor(p.Y / join / 4), (long)Math.Floor(p.Z / join / 4));
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((k.Item1 + dx, k.Item2 + dy, k.Item3 + dz), out var l))
                    foreach (int i in l) if ((pts[i] - p).Length < join) return i;
            pts.Add(p);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = [];
            list.Add(pts.Count - 1);
            return pts.Count - 1;
        }
        var adj = new Dictionary<int, List<int>>();
        var edgeSet = new HashSet<(int, int)>();
        foreach (var (a, b) in segs)
        {
            int i = Id(a), j = Id(b);
            if (i == j || !edgeSet.Add((Math.Min(i, j), Math.Max(i, j)))) continue;
            if (!adj.TryGetValue(i, out var li)) adj[i] = li = []; li.Add(j);
            if (!adj.TryGetValue(j, out var lj)) adj[j] = lj = []; lj.Add(i);
        }
        var used = new HashSet<(int, int)>();
        bool Use(int i, int j) => used.Add((Math.Min(i, j), Math.Max(i, j)));
        var result = new List<(List<Vec3>, bool)>();
        // runs from ends (and branch points) first, then what is left are rings
        foreach (int start in adj.Keys.Where(v => adj[v].Count != 2).Concat(adj.Keys.Where(v => adj[v].Count == 2)).ToList())
            foreach (int first in adj[start].ToList())
            {
                if (!Use(start, first)) continue;
                var run = new List<int> { start, first };
                int prev = start, cur = first;
                while (adj[cur].Count == 2 && cur != start)
                {
                    int next = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    if (!Use(cur, next)) break;
                    run.Add(next); prev = cur; cur = next;
                }
                bool closed = run.Count > 3 && run[0] == run[^1];
                if (closed) run.RemoveAt(run.Count - 1);
                result.Add((run.Select(i => pts[i]).ToList(), closed));
            }
        return result;
    }

    /// <summary>The end of a crossing curve put where it meets the nearest edge of either face exactly: on the two
    /// surfaces and the one across that edge (snapped to a corner of the edge when it is that).</summary>
    private static Vec3 EndOn(FaceI a, FaceI b, Vec3 p)
    {
        (FaceI F, int Edge, Vec3 At, double D)? best = null;
        foreach (var f in new[] { a, b })
            foreach (var l in f.Loops)
                foreach (var (e, _, pts) in l)
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        var q = Closest(pts[i], pts[i + 1], p); double d = (q - p).Length;
                        if (best == null || d < best.Value.D) best = (f, e, q, d);
                    }
        if (best is not { } bb || !bb.F.Across.TryGetValue(bb.Edge, out var third)) return p;
        var q3 = OnAll([a.S, b.S, third], p);
        if ((q3 - p).Length > 1e-3) return p;
        // (a corner of the edge)
        foreach (var l in bb.F.Loops)
            foreach (var (e, _, pts) in l)
                if (e == bb.Edge) foreach (var v in new[] { pts[0], pts[^1] }) if ((v - q3).Length < Tol) return v;
        return q3;
    }

    /// <summary>Whether a point lies on an edge of the face (within the tolerance, in space): near the edge's points, and
    /// on its exact curve.</summary>
    private static bool OnEdge(FaceI f, Vec3 p)
    {
        if (p.X < f.Box.Lo.X - Tol || p.Y < f.Box.Lo.Y - Tol || p.Z < f.Box.Lo.Z - Tol || p.X > f.Box.Hi.X + Tol || p.Y > f.Box.Hi.Y + Tol || p.Z > f.Box.Hi.Z + Tol) return false;
        double near = Size(f) * 1e-3;
        foreach (var l in f.Loops)
            foreach (var (e, _, pts) in l)
            {
                bool close = false;
                for (int i = 0; i + 1 < pts.Count && !close; i++) close = (Closest(pts[i], pts[i + 1], p) - p).Length < near;
                if (!close) continue;
                var E = f.Of.Edges[e];
                double d = E.Curve switch
                {
                    BrepLine => (Closest(f.Of.Vertices[E.Start], f.Of.Vertices[E.End], p) - p).Length,
                    BrepCircle c => CircleDist(c.Centre, c.Normal, c.Radius, p),
                    _ => pts.Zip(pts.Skip(1)).Min(s => (Closest(s.First, s.Second, p) - p).Length),
                };
                if (d < Tol) return true;
            }
        return false;
    }

    private static double CircleDist(Vec3 c, Vec3 n, double r, Vec3 p)
    {
        n = n.Normalized(); var d = p - c; double h = d.Dot(n); var radial = d - n * h;
        return Math.Sqrt(h * h + Math.Pow(radial.Length - r, 2));
    }

    private static Vec3 Closest(Vec3 a, Vec3 b, Vec3 p)
    {
        var ab = b - a; double l2 = ab.Dot(ab);
        return l2 < 1e-30 ? a : a + ab * Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
    }

    private static double Length(List<Vec3> pts, bool closed)
    {
        double s = 0;
        for (int i = 0; i + 1 < pts.Count; i++) s += (pts[i + 1] - pts[i]).Length;
        if (closed && pts.Count > 1) s += (pts[0] - pts[^1]).Length;
        return s;
    }

    /// <summary>The points evenly along the run again (at least 2° of turning or 1/64 of it apart), each on both
    /// surfaces; the ends stay.</summary>
    private static List<Vec3> Resample(List<Vec3> pts, bool closed, Surf[] surfs)
    {
        var src = closed ? pts.Append(pts[0]).ToList() : pts;
        double total = Length(src, false);
        int n = Math.Clamp((int)Math.Ceiling(src.Count * 1.0), 64, 400);
        var cum = new List<double> { 0 };
        for (int i = 1; i < src.Count; i++) cum.Add(cum[^1] + (src[i] - src[i - 1]).Length);
        var result = new List<Vec3>();
        int seg = 0;
        for (int k = 0; k <= n; k++)
        {
            if (closed && k == n) break;
            double t = total * k / n;
            while (seg + 1 < cum.Count - 1 && cum[seg + 1] < t) seg++;
            double f = cum[seg + 1] - cum[seg] < 1e-30 ? 0 : (t - cum[seg]) / (cum[seg + 1] - cum[seg]);
            var p = src[seg] + (src[seg + 1] - src[seg]) * f;
            result.Add(k == 0 ? src[0] : k == n ? src[^1] : OnAll(surfs, p));
        }
        return result;
    }

    /// <summary>The exact curve the points lie on, when it is a line, a circle or an ellipse (else null).</summary>
    private static BrepCurve? Recognise(List<Vec3> pts, bool closed)
    {
        Vec3 a = pts[0], b = pts[^1];
        if (!closed && (b - a).Length > Tol && pts.All(p => (Closest(a, b, p) - p).Length < 1e-9)) return new BrepLine();
        // a plane through them
        var c = pts.Aggregate(new Vec3(0, 0, 0), (s, p) => s + p) * (1.0 / pts.Count);
        Vec3 n = new(0, 0, 0);
        for (int i = 0; i < pts.Count; i++) n += (pts[i] - c).Cross(pts[(i + pts.Count / 3) % pts.Count] - c);
        if (n.Length < 1e-18) return null;
        n = n.Normalized();
        if (pts.Any(p => Math.Abs((p - c).Dot(n)) > 1e-9)) return null;
        // a circle round the centre of three points far apart
        var p0 = pts[0]; var p1 = pts[pts.Count / 3]; var p2 = pts[2 * pts.Count / 3];
        if (CircleThrough(p0, p1, p2, n) is { } cc && pts.All(p => Math.Abs((p - cc.C).Length - cc.R) < 1e-9))
            return new BrepCircle(cc.C, n, (p0 - cc.C).Normalized(), cc.R);
        // an ellipse: centre and axes from the points (the longest and shortest reach from the middle of a closed run)
        if (closed)
        {
            var far = pts.MaxBy(p => (p - c).Length); var near = pts.MinBy(p => (p - c).Length);
            double big = (far - c).Length, small = (near - c).Length;
            var x = (far - c).Normalized(); var y = n.Cross(x);
            if (pts.All(p => { double u = (p - c).Dot(x) / big, v = (p - c).Dot(y) / small; return Math.Abs(u * u + v * v - 1) < 1e-7; }))
                return new BrepEllipse(c, n, x, big, small);
        }
        return null;
    }

    private static (Vec3 C, double R)? CircleThrough(Vec3 a, Vec3 b, Vec3 c, Vec3 n)
    {
        var (x, y) = Axes(n);
        double ax = a.Dot(x), ay = a.Dot(y), bx = b.Dot(x), by = b.Dot(y), cx = c.Dot(x), cy = c.Dot(y);
        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-24) return null;
        double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d, uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
        var centre = a + x * (ux - ax) + y * (uy - ay);
        return (centre, (a - centre).Length);
    }
}
