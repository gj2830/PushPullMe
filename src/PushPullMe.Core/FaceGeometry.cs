using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>Points along edges and loops, normals and triangles of planar faces.</summary>
public static class FaceGeometry
{
    /// <summary>Angle step (radians) for sampling arcs: 6°.</summary>
    public const double ArcStep = Math.PI / 30;

    /// <summary>The points of an edge from its start to its end (both included; a ring edge: closed, the first point
    /// repeated at the end).</summary>
    public static List<Vec3> EdgePoints(BrepBody body, BrepEdge e, double step = ArcStep)
    {
        switch (e.Curve)
        {
            case BrepLine:
                return [body.Vertices[e.Start], body.Vertices[e.End]];
            case BrepCircle c:
            {
                var n = c.Normal.Normalized(); var x = c.XAxis.Normalized(); var y = n.Cross(x);
                double Angle(Vec3 p) { var d = p - c.Centre; return Math.Atan2(d.Dot(y), d.Dot(x)); }
                double a0, sweep;
                if (e.Start < 0) { a0 = 0; sweep = 2 * Math.PI; }
                else
                {
                    a0 = Angle(body.Vertices[e.Start]);
                    double a1 = Angle(body.Vertices[e.End]);
                    sweep = a1 - a0;
                    while (sweep <= 1e-12) sweep += 2 * Math.PI;
                    if (e.Start == e.End) sweep = 2 * Math.PI;
                }
                int k = Math.Max(1, (int)Math.Ceiling(sweep / step));
                var pts = new List<Vec3>(k + 1);
                for (int i = 0; i <= k; i++)
                {
                    double a = a0 + sweep * i / k;
                    pts.Add(c.Centre + x * (c.Radius * Math.Cos(a)) + y * (c.Radius * Math.Sin(a)));
                }
                if (e.Start >= 0) { pts[0] = body.Vertices[e.Start]; pts[^1] = body.Vertices[e.End]; }
                return pts;
            }
            case BrepEllipse el:
            {
                var n = el.Normal.Normalized(); var x = el.XAxis.Normalized(); var y = n.Cross(x);
                double Angle(Vec3 p) { var d = p - el.Centre; return Math.Atan2(d.Dot(y) / el.MinorRadius, d.Dot(x) / el.MajorRadius); }
                double a0 = 0, sweep = 2 * Math.PI;
                if (e.Start >= 0 && e.Start != e.End)
                {
                    a0 = Angle(body.Vertices[e.Start]);
                    sweep = Angle(body.Vertices[e.End]) - a0;
                    while (sweep <= 1e-12) sweep += 2 * Math.PI;
                }
                int k = Math.Max(1, (int)Math.Ceiling(sweep / step));
                var pts = new List<Vec3>(k + 1);
                for (int i = 0; i <= k; i++)
                {
                    double a = a0 + sweep * i / k;
                    pts.Add(el.Centre + x * (el.MajorRadius * Math.Cos(a)) + y * (el.MinorRadius * Math.Sin(a)));
                }
                if (e.Start >= 0) { pts[0] = body.Vertices[e.Start]; pts[^1] = body.Vertices[e.End]; }
                return pts;
            }
            case BrepBSpline s:
            {
                var pts = new List<Vec3>();
                int k = Math.Max(8, s.ControlPoints.Count * 4);
                double t0 = s.Knots[0], t1 = s.Knots[^1];
                var U = BSpline.Expand(s.Knots, s.Mults);
                for (int i = 0; i <= k; i++)
                {
                    double t = t0 + (t1 - t0) * i / k;
                    pts.Add(s.Weights != null ? BSpline.EvalRational(s.Degree, s.ControlPoints, s.Weights, U, t) : BSpline.Eval(s.Degree, s.ControlPoints, U, t));
                }
                if (e.Start >= 0) { pts[0] = body.Vertices[e.Start]; pts[^1] = body.Vertices[e.End]; }
                return pts;
            }
            default:
                throw new NotSupportedException(e.Curve.GetType().Name);
        }
    }

    /// <summary>The point at <paramref name="s"/> (0 = start, 1 = end) along a line, circle or ellipse edge, in the
    /// parameter <see cref="EdgePoints"/> uses; null for other curves and for rings.</summary>
    public static Vec3? EdgePoint(BrepBody body, BrepEdge e, double s)
    {
        if (e.Start < 0) return null;
        switch (e.Curve)
        {
            case BrepLine:
                return body.Vertices[e.Start] + (body.Vertices[e.End] - body.Vertices[e.Start]) * s;
            case BrepCircle c:
            {
                var n = c.Normal.Normalized(); var x = c.XAxis.Normalized(); var y = n.Cross(x);
                double Angle(Vec3 p) { var d = p - c.Centre; return Math.Atan2(d.Dot(y), d.Dot(x)); }
                double a0 = Angle(body.Vertices[e.Start]), sweep = Angle(body.Vertices[e.End]) - a0;
                while (sweep <= 1e-12) sweep += 2 * Math.PI;
                if (e.Start == e.End) sweep = 2 * Math.PI;
                double a = a0 + sweep * s;
                return c.Centre + x * (c.Radius * Math.Cos(a)) + y * (c.Radius * Math.Sin(a));
            }
            case BrepEllipse el:
            {
                var n = el.Normal.Normalized(); var x = el.XAxis.Normalized(); var y = n.Cross(x);
                double Angle(Vec3 p) { var d = p - el.Centre; return Math.Atan2(d.Dot(y) / el.MinorRadius, d.Dot(x) / el.MajorRadius); }
                double a0 = Angle(body.Vertices[e.Start]), sweep = 2 * Math.PI;
                if (e.Start != e.End) { sweep = Angle(body.Vertices[e.End]) - a0; while (sweep <= 1e-12) sweep += 2 * Math.PI; }
                double a = a0 + sweep * s;
                return el.Centre + x * (el.MajorRadius * Math.Cos(a)) + y * (el.MinorRadius * Math.Sin(a));
            }
            default:
                return null;
        }
    }

    /// <summary>The points of a loop in its direction, each once (the loop closes from the last to the first).</summary>
    public static List<Vec3> LoopPoints(BrepBody body, IReadOnlyList<BrepCoedge> loop, double step = ArcStep)
    {
        var pts = new List<Vec3>();
        foreach (var c in loop)
        {
            var p = EdgePoints(body, body.Edges[c.Edge], step);
            if (!c.Forward) p.Reverse();
            pts.AddRange(p.Take(p.Count - 1));
        }
        return pts;
    }

    /// <summary>The outward normal of a planar face.</summary>
    public static Vec3 Normal(BrepFace f) => f.Surface is BrepPlane p ? (f.Reversed ? p.Normal * -1 : p.Normal).Normalized()
        : throw new ArgumentException("not a planar face");

    /// <summary>Signed area of a loop around a normal (positive: counter-clockwise).</summary>
    public static double Area(IReadOnlyList<Vec3> pts, Vec3 n)
    {
        var s = new Vec3(0, 0, 0);
        for (int i = 0; i < pts.Count; i++) s += pts[i].Cross(pts[(i + 1) % pts.Count]);
        return s.Dot(n) / 2;
    }

    /// <summary>
    /// The triangles of a face on a cylinder, wound outward; or null when it goes all the way round (a ring) or is not
    /// on a cylinder. The face is unrolled (angle, height), cut into strips at every angle a loop point has (at most
    /// <see cref="ArcStep"/> wide) and every strip filled between the loop segments crossing it, so the triangles meet
    /// the points of the edges exactly as the planar faces next to it do.
    /// </summary>
    public static List<(Vec3 A, Vec3 B, Vec3 C)>? CylinderTriangles(BrepBody body, BrepFace f)
    {
        // (a cone the same way, its radius growing along the axis)
        var (origin, axisV, xAxis, radius, slope) = f.Surface switch
        {
            BrepCylinder c => (c.Origin, c.Axis, c.XAxis, c.Radius, 0.0),
            BrepCone k => (k.Origin, k.Axis, k.XAxis, k.Radius, Math.Tan(k.HalfAngle)),
            _ => default,
        };
        if (f.Surface is not (BrepCylinder or BrepCone)) return null;
        var cy = (Origin: origin, Radius: radius);
        var ax = axisV.Normalized();
        var x = (xAxis - ax * xAxis.Dot(ax)).Normalized();
        var y = ax.Cross(x);
        var loops = new List<List<(double T, double H)>>();
        var rings = new List<List<(double T, double H)>>();
        foreach (var l in f.Loops)
        {
            var pts = LoopPoints(body, l);
            if (pts.Count < 3) return null;
            var q = new List<(double T, double H)>();
            double prev = 0;
            foreach (var p in pts)
            {
                var d = p - cy.Origin;
                double t = Math.Atan2(d.Dot(y), d.Dot(x));
                if (q.Count > 0) { while (t - prev > Math.PI) t -= 2 * Math.PI; while (t - prev < -Math.PI) t += 2 * Math.PI; }
                q.Add((t, d.Dot(ax)));
                prev = t;
            }
            // a loop that goes round the axis cannot be unrolled (but two of them make a band, below)
            double back = q[0].T - q[^1].T;
            while (back > Math.PI) back -= 2 * Math.PI;
            while (back < -Math.PI) back += 2 * Math.PI;
            double turn = q[^1].T + back - q[0].T;
            if (Math.Abs(turn) > 1e-6)
            {
                if (f.Loops.Count != 2) return null;
                rings.Add(turn > 0 ? q : Enumerable.Reverse(q).ToList());
                continue;
            }
            loops.Add(q);
        }
        if (rings.Count == 2) return Band(rings);
        if (rings.Count > 0) return null;
        // holes into the same turn as the outer loop
        double mid = loops[0].Average(p => p.T);
        for (int i = 1; i < loops.Count; i++)
        {
            double m = loops[i].Average(p => p.T), shift = Math.Round((mid - m) / (2 * Math.PI)) * 2 * Math.PI;
            if (shift != 0) loops[i] = loops[i].Select(p => (p.T + shift, p.H)).ToList();
        }
        var segs = loops.SelectMany(l => l.Select((p, i) => (A: p, B: l[(i + 1) % l.Count]))).ToList();
        var ts = loops.SelectMany(l => l.Select(p => p.T)).Order().ToList();
        if (ts[^1] - ts[0] >= 2 * Math.PI - 1e-9) return null;
        var cuts = new List<double> { ts[0] };
        foreach (double t in ts.Skip(1))
        {
            if (t - cuts[^1] < 1e-9) continue;
            int k = (int)Math.Ceiling((t - cuts[^1]) / ArcStep - 1e-9);
            double from = cuts[^1];
            for (int i = 1; i < k; i++) cuts.Add(from + (t - from) * i / k);
            cuts.Add(t);
        }
        double sgn = f.Reversed ? -1 : 1;
        Vec3 P(double t, double h) => cy.Origin + ax * h + (x * Math.Cos(t) + y * Math.Sin(t)) * (cy.Radius + h * slope);
        static double HAt(((double T, double H) A, (double T, double H) B) s, double t) =>
            s.A.H + (s.B.H - s.A.H) * (t - s.A.T) / (s.B.T - s.A.T);
        var tris = new List<(Vec3, Vec3, Vec3)>();
        void Add(Vec3 a, Vec3 b, Vec3 c, double t)
        {
            var nrm = (b - a).Cross(c - a);
            if (nrm.Length < 1e-18) return;
            var radial = x * Math.Cos(t) + y * Math.Sin(t);
            tris.Add(nrm.Dot(radial) * sgn >= 0 ? (a, b, c) : (a, c, b));
        }
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            double t0 = cuts[i], t1 = cuts[i + 1], tm = (t0 + t1) / 2;
            var across = segs.Where(s => Math.Min(s.A.T, s.B.T) < tm && Math.Max(s.A.T, s.B.T) > tm).OrderBy(s => HAt(s, tm)).ToList();
            if (across.Count % 2 != 0) return null;
            for (int j = 0; j < across.Count; j += 2)
            {
                var lo = across[j]; var hi = across[j + 1];
                Vec3 a = P(t0, HAt(lo, t0)), b = P(t1, HAt(lo, t1)), c = P(t1, HAt(hi, t1)), d = P(t0, HAt(hi, t0));
                Add(a, b, c, tm); Add(a, c, d, tm);
            }
        }
        return tris;

        // a band round the axis between two loops that each go round it once: strips at every angle either loop has a
        // point at (at most ArcStep apart), from the one loop to the other
        List<(Vec3, Vec3, Vec3)> Band(List<List<(double T, double H)>> two)
        {
            static double Wrap(double t) { t %= 2 * Math.PI; return t < 0 ? t + 2 * Math.PI : t; }
            // per loop, its points by angle in [0, 2π), the height between them by interpolation
            var sorted = two.Select(l => l.Select(p => (T: Wrap(p.T), p.H)).OrderBy(p => p.T).ToList()).ToList();
            static double HOf(List<(double T, double H)> l, double t)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    var a = l[i]; var b = i + 1 < l.Count ? l[i + 1] : (T: l[0].T + 2 * Math.PI, l[0].H);
                    double tt = t < a.T ? t + 2 * Math.PI : t;
                    if (tt >= a.T && tt <= b.T) return b.T - a.T < 1e-12 ? a.H : a.H + (b.H - a.H) * (tt - a.T) / (b.T - a.T);
                }
                return l[0].H;
            }
            var ts = sorted.SelectMany(l => l.Select(p => p.T)).Order().ToList();
            var cuts = new List<double>();
            foreach (double t in ts) if (cuts.Count == 0 || t - cuts[^1] > 1e-9) cuts.Add(t);
            if (2 * Math.PI + cuts[0] - cuts[^1] < 1e-9) cuts.RemoveAt(cuts.Count - 1);
            var full = new List<double>();
            for (int i = 0; i < cuts.Count; i++)
            {
                double a = cuts[i], b = i + 1 < cuts.Count ? cuts[i + 1] : cuts[0] + 2 * Math.PI;
                int k = Math.Max(1, (int)Math.Ceiling((b - a) / ArcStep - 1e-9));
                for (int j = 0; j < k; j++) full.Add(a + (b - a) * j / k);
            }
            var (lo, hi) = sorted[0].Average(p => p.H) <= sorted[1].Average(p => p.H) ? (sorted[0], sorted[1]) : (sorted[1], sorted[0]);
            double sg = f.Reversed ? -1 : 1;
            var band = new List<(Vec3, Vec3, Vec3)>();
            for (int i = 0; i < full.Count; i++)
            {
                double t0 = full[i], t1 = i + 1 < full.Count ? full[i + 1] : full[0] + 2 * Math.PI, tm = (t0 + t1) / 2;
                Vec3 a = P(t0, HOf(lo, Wrap(t0))), b = P(t1, HOf(lo, Wrap(t1))), c = P(t1, HOf(hi, Wrap(t1))), d = P(t0, HOf(hi, Wrap(t0)));
                var radial = x * Math.Cos(tm) + y * Math.Sin(tm);
                foreach (var (p, q, r) in new[] { (a, b, c), (a, c, d) })
                {
                    var nn = (q - p).Cross(r - p);
                    if (nn.Length < 1e-18) continue;
                    band.Add(nn.Dot(radial) * sg >= 0 ? (p, q, r) : (p, r, q));
                }
            }
            return band;
        }
    }

    /// <summary>
    /// The triangles of a curved face from its exact shape: cylinders and cones unrolled (<see cref="CylinderTriangles"/>),
    /// spheres and tori by laying the face flat, triangulating, and refining with the points put back on the surface.
    /// Null when that does not work (a ring round a cylinder, a free-form surface).
    /// </summary>
    public static List<(Vec3 A, Vec3 B, Vec3 C)>? CurvedTriangles(BrepBody body, BrepFace f)
    {
        double s = f.Reversed ? -1 : 1;
        switch (f.Surface)
        {
            case BrepCylinder or BrepCone:
                return CylinderTriangles(body, f);
            case BrepNurbsSurface ns when ns.Helix == null && f.Loops.Count == 1:
            {
                // (a corner piece PushPullMe made: the face is the whole surface) a grid over its parameters
                const int n = 12;
                double u0 = ns.UKnots[0], u1 = ns.UKnots[^1], v0 = ns.VKnots[0], v1 = ns.VKnots[^1];
                var g = new Vec3[n + 1, n + 1];
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n; j++) g[i, j] = ns.At(u0 + (u1 - u0) * i / n, v0 + (v1 - v0) * j / n);
                var tris = new List<(Vec3, Vec3, Vec3)>();
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        // (natural normal dS/du × dS/dv; reversed: the other way round)
                        if (!f.Reversed) { tris.Add((g[i, j], g[i + 1, j], g[i + 1, j + 1])); tris.Add((g[i, j], g[i + 1, j + 1], g[i, j + 1])); }
                        else { tris.Add((g[i, j], g[i + 1, j + 1], g[i + 1, j])); tris.Add((g[i, j], g[i, j + 1], g[i + 1, j + 1])); }
                    }
                return tris.Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-18).ToList();
            }
            case BrepSphere sp:
            {
                Vec3 Outward(Vec3 p) => (p - sp.Centre).Normalized() * s;
                // around the axis of its edge circles (a cap or a zone), else laid flat
                var circle = f.Loops.SelectMany(l => l).Select(c => body.Edges[c.Edge].Curve).OfType<BrepCircle>().FirstOrDefault();
                if (circle != null)
                {
                    var ax = circle.Normal.Normalized();
                    if (Revolved(body, f, sp.Centre, ax, circle.XAxis, false,
                            p => Math.Acos(Math.Clamp((p - sp.Centre).Dot(ax) / sp.Radius, -1, 1)),
                            st => (sp.Radius * Math.Sin(st), sp.Radius * Math.Cos(st)), Outward) is { } rev) return rev;
                }
                return Projected(body, f, p => sp.Centre + (p - sp.Centre).Normalized() * sp.Radius, Outward, () => UvSphere(sp.Centre, sp.Radius, s));
            }
            case BrepTorus to:
            {
                var ax = to.Axis.Normalized();
                Vec3 Tube(Vec3 p) { var q = p - to.Centre; return to.Centre + (q - ax * q.Dot(ax)).Normalized() * to.MajorRadius; }
                Vec3 Outward(Vec3 p) => (p - Tube(p)).Normalized() * s;
                if (Revolved(body, f, to.Centre, ax, to.XAxis, true,
                        p => { var q = p - to.Centre; double h = q.Dot(ax); return Math.Atan2(h, (q - ax * h).Length - to.MajorRadius); },
                        st => (to.MajorRadius + to.MinorRadius * Math.Cos(st), to.MinorRadius * Math.Sin(st)), Outward) is { } rev) return rev;
                return Projected(body, f, p => Tube(p) + (p - Tube(p)).Normalized() * to.MinorRadius, Outward, null);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// A face on a surface of revolution (around <paramref name="axis"/> through <paramref name="c"/>; a profile point at
    /// parameter s is at distance ρ from the axis and height h along it) bounded by circles around that axis: a cap (one
    /// circle, on a sphere: up to the pole) or a zone between two. Rows of points around the axis: the edge circles'
    /// own points, and every ArcStep of s between; neighbouring rows zipped into triangles. Null when the face is not
    /// like that.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? Revolved(BrepBody body, BrepFace f, Vec3 c, Vec3 axis, Vec3 xAxis, bool periodic,
        Func<Vec3, double> sOf, Func<double, (double Rho, double H)> profile, Func<Vec3, Vec3> outward)
    {
        var x0 = (xAxis - axis * xAxis.Dot(axis)).Normalized();
        if (x0.Length < 0.5) return null;
        var y0 = axis.Cross(x0);
        double Phi(Vec3 p) { var d = p - c; double a = Math.Atan2(d.Dot(y0), d.Dot(x0)); return a < 0 ? a + 2 * Math.PI : a; }
        Vec3 At(double phi, double s) { var (rho, h) = profile(s); return c + axis * h + (x0 * Math.Cos(phi) + y0 * Math.Sin(phi)) * rho; }
        // every loop one circle around the axis
        var bounds = new List<(double S, int Side, List<(double Phi, Vec3 P)> Row)>();
        foreach (var loop in f.Loops)
        {
            if (loop.Count != 1 || body.Edges[loop[0].Edge] is not { Curve: BrepCircle ci } e || (e.Start >= 0 && e.Start != e.End)) return null;
            if (Math.Abs(ci.Normal.Normalized().Dot(axis)) < 1 - 1e-6 || (ci.Centre - c).Cross(axis).Length > 1e-6) return null;
            var pts = LoopPoints(body, loop);
            if (pts.Count < 3) return null;
            double s0 = sOf(pts[0]);
            // the face lies to the left of its edge (seen from outside): towards larger or smaller s
            var left = outward(pts[0]).Cross(pts[1] - pts[0]);
            int side = left.Dot(At(Phi(pts[0]), s0 + 1e-3) - pts[0]) > 0 ? 1 : -1;
            bounds.Add((s0, side, pts.Select(p => (Phi(p), p)).OrderBy(q => q.Item1).ToList()));
        }
        double start, end;
        List<(double Phi, Vec3 P)> first, last;
        if (bounds.Count == 1 && !periodic)
        {
            (start, first) = (bounds[0].S, bounds[0].Row);
            end = bounds[0].Side > 0 ? Math.PI : 0;
            last = [(0, At(0, end))];
        }
        else if (bounds.Count == 2)
        {
            (start, first) = (bounds[0].S, bounds[0].Row);
            (end, last) = (bounds[1].S, bounds[1].Row);
            int side = bounds[0].Side;
            if (periodic) { while ((end - start) * side <= 1e-9) end += 2 * Math.PI * side; }
            else if ((end - start) * side <= 0) return null;
        }
        else return null;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(end - start) / ArcStep - 1e-9));
        var rows = new List<List<(double Phi, Vec3 P)>> { first };
        int around = (int)Math.Round(2 * Math.PI / ArcStep);
        for (int k = 1; k < n; k++)
        {
            double s = start + (end - start) * k / n;
            rows.Add(Enumerable.Range(0, around).Select(j => (2 * Math.PI * j / around, At(2 * Math.PI * j / around, s))).ToList());
        }
        rows.Add(last);
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int r = 0; r + 1 < rows.Count; r++) Zip(rows[r], rows[r + 1], tris);
        return tris.Select(t =>
        {
            var (a, b, d) = t;
            return (b - a).Cross(d - a).Dot(outward((a + b + d) * (1.0 / 3))) >= 0 ? (a, b, d) : (a, d, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>Two closed rows of points around an axis (sorted by angle) joined by triangles, as a zip.</summary>
    private static void Zip(List<(double Phi, Vec3 P)> a, List<(double Phi, Vec3 P)> b, List<(Vec3, Vec3, Vec3)> tris)
    {
        if (a.Count == 1 || b.Count == 1)
        {
            var (tip, ring) = a.Count == 1 ? (a[0].P, b) : (b[0].P, a);
            for (int i = 0; i < ring.Count; i++) tris.Add((tip, ring[i].P, ring[(i + 1) % ring.Count].P));
            return;
        }
        double PhiOf(List<(double Phi, Vec3 P)> r, int i) => r[i % r.Count].Phi + 2 * Math.PI * (i / r.Count);
        int ia = 0, ib = 0;
        while (ia < a.Count || ib < b.Count)
        {
            bool advanceA = ib >= b.Count || (ia < a.Count && PhiOf(a, ia + 1) <= PhiOf(b, ib + 1));
            if (advanceA) { tris.Add((a[ia % a.Count].P, b[ib % b.Count].P, a[(ia + 1) % a.Count].P)); ia++; }
            else { tris.Add((a[ia % a.Count].P, b[ib % b.Count].P, b[(ib + 1) % b.Count].P)); ib++; }
        }
    }

    /// <summary>A face laid flat (along the normal of its outer loop), triangulated, every triangle cut in 4 three times
    /// with the new points put onto the surface; wound along <paramref name="outward"/>.</summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? Projected(BrepBody body, BrepFace f, Func<Vec3, Vec3> onto, Func<Vec3, Vec3> outward,
        Func<List<(Vec3, Vec3, Vec3)>>? whole)
    {
        var loops = f.Loops.Select(l => LoopPoints(body, l)).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return whole?.Invoke();
        // the outer loop: the largest; its Newell normal the direction to lay the face flat along
        Vec3 Newell(List<Vec3> l) { var s = new Vec3(0, 0, 0); for (int i = 0; i < l.Count; i++) s += l[i].Cross(l[(i + 1) % l.Count]); return s; }
        int outer = Enumerable.Range(0, loops.Count).MaxBy(i => Newell(loops[i]).Length);
        var n = Newell(loops[outer]);
        if (n.Length < 1e-18) return null;
        n = n.Normalized();
        var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
        var y = n.Cross(x);
        var ordered = new[] { loops[outer] }.Concat(loops.Where((_, i) => i != outer)).ToList();
        var flat = ordered.SelectMany(l => l).ToList();
        var idx = Triangulator.Triangulate(ordered.Select(l => (IReadOnlyList<(double, double)>)l.Select(p => (p.Dot(x), p.Dot(y))).ToList()).ToList());
        var tris = idx.Select(t => (flat[t.A], flat[t.B], flat[t.C])).ToList();
        for (int level = 0; level < 3; level++)
        {
            var next = new List<(Vec3, Vec3, Vec3)>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                Vec3 ab = onto((a + b) * 0.5), bc = onto((b + c) * 0.5), ca = onto((c + a) * 0.5);
                next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        return tris.Select(t =>
        {
            var (a, b, c) = t;
            var nn = (b - a).Cross(c - a);
            return nn.Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).ToList();
    }

    /// <summary>A whole sphere (a face without edges).</summary>
    private static List<(Vec3, Vec3, Vec3)> UvSphere(Vec3 c, double r, double s)
    {
        var tris = new List<(Vec3, Vec3, Vec3)>();
        const int nu = 48, nv = 24;
        Vec3 P(int i, int j) { double u = 2 * Math.PI * i / nu, v = Math.PI * j / nv; return c + new Vec3(Math.Sin(v) * Math.Cos(u), Math.Sin(v) * Math.Sin(u), Math.Cos(v)) * r; }
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                Vec3 a = P(i, j), b = P(i + 1, j), d = P(i, j + 1), e = P(i + 1, j + 1);
                if (j > 0) tris.Add(s > 0 ? (a, d, b) : (a, b, d));
                if (j < nv - 1) tris.Add(s > 0 ? (b, d, e) : (b, e, d));
            }
        return tris;
    }

    /// <summary>The triangles of a planar face, wound around its outward normal.</summary>
    public static List<(Vec3 A, Vec3 B, Vec3 C)> PlanarTriangles(BrepBody body, BrepFace f)
    {
        var n = Normal(f);
        var x = (f.Surface as BrepPlane)!.XAxis;
        x = (x - n * x.Dot(n)).Normalized();
        if (x.Length < 0.5) x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
        var y = n.Cross(x);
        var loops = f.Loops.Select(l => LoopPoints(body, l)).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return [];
        // the outer loop: the largest
        int outer = Enumerable.Range(0, loops.Count).MaxBy(i => Math.Abs(Area(loops[i], n)));
        var ordered = new[] { loops[outer] }.Concat(loops.Where((_, i) => i != outer)).ToList();
        var flat = ordered.SelectMany(l => l).ToList();
        var tris = Triangulator.Triangulate(ordered.Select(l => (IReadOnlyList<(double, double)>)l.Select(p => (p.Dot(x), p.Dot(y))).ToList()).ToList());
        return tris.Select(t => (flat[t.A], flat[t.B], flat[t.C])).ToList();
    }
}
