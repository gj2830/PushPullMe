using RsDocx.Core.Brep;
using RsDocx.Core.Geometry;
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
                // (an edge on part of its curve, such as a thread's helix in DesignSpark's helix.rsdocx that runs on past the
                // edge's ends: only between its vertices; not a ring, nor a closed curve's edge across its seam)
                if (e.Start >= 0 && e.Start != e.End && ((s.At(t0) - body.Vertices[e.Start]).Length > 1e-9 || (s.At(t1) - body.Vertices[e.End]).Length > 1e-9))
                {
                    double a = BSpline.ClosestParam(s, body.Vertices[e.Start]), b = BSpline.ClosestParam(s, body.Vertices[e.End]);
                    if (!s.Closed || a < b)
                    {
                        k = Math.Max(8, (int)Math.Ceiling(k * Math.Abs(b - a) / (t1 - t0)));
                        (t0, t1) = (a, b);
                    }
                }
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

    /// <summary>
    /// The outward normal of a face at a point on it, from its exact surface (so a round face can be shaded smooth);
    /// null for a free-form surface.
    /// </summary>
    public static Vec3? NormalAt(BrepFace f, Vec3 p)
    {
        double s = f.Reversed ? -1 : 1;
        switch (f.Surface)
        {
            case BrepPlane pl: return pl.Normal.Normalized() * s;
            case BrepCylinder cy:
            {
                var ax = cy.Axis.Normalized(); var d = p - cy.Origin; d -= ax * d.Dot(ax);
                return d.Length < 1e-15 ? null : d.Normalized() * s;
            }
            case BrepCone co:
            {
                // away from the axis and back along it, square to the cone's sloping line
                var ax = co.Axis.Normalized(); var d = p - co.Origin; d -= ax * d.Dot(ax);
                if (d.Length < 1e-15) return null;
                return (d.Normalized() * Math.Cos(co.HalfAngle) - ax * Math.Sin(co.HalfAngle)).Normalized() * s;
            }
            case BrepSphere sp:
            {
                var d = p - sp.Centre;
                return d.Length < 1e-15 ? null : d.Normalized() * s;
            }
            case BrepTorus to:
            {
                // away from the circle through the middle of the tube
                var ax = to.Axis.Normalized(); var q = p - to.Centre; var radial = q - ax * q.Dot(ax);
                if (radial.Length < 1e-15) return null;
                var d = p - (to.Centre + radial.Normalized() * to.MajorRadius);
                return d.Length < 1e-15 ? null : d.Normalized() * s;
            }
            default: return null;
        }
    }

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
        // (a whole turn: a loop over a seam, as OpenCASCADE writes a whole cylinder, done by Unwrapped; more than a turn
        // without going round, such as a band between two helices of a thread (helix.rsdocx), is strips as any other)
        if (Math.Abs(ts[^1] - ts[0] - 2 * Math.PI) < 1e-6) return null;
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
            {
                if (CylinderTriangles(body, f) is { } cyl) return cyl;
                // (one loop over a seam, as OpenCASCADE writes a whole cylinder or cone: in its angle and height)
                var (o, ax, x, r, slope) = f.Surface switch
                {
                    BrepCylinder c => (c.Origin, c.Axis.Normalized(), c.XAxis, c.Radius, 0.0),
                    BrepCone k => (k.Origin, k.Axis.Normalized(), k.XAxis, k.Radius, Math.Tan(k.HalfAngle)),
                    _ => default,
                };
                var cx = (x - ax * x.Dot(ax)).Normalized(); var cy = ax.Cross(cx);
                return Unwrapped(body, f, true, false,
                    p => { var q = p - o; return (Math.Atan2(q.Dot(cy), q.Dot(cx)), q.Dot(ax)); },
                    (u, v) => o + ax * v + (cx * Math.Cos(u) + cy * Math.Sin(u)) * (r + v * slope),
                    Math.Max(r, 1e-6), 1);
            }
            case BrepNurbsSurface ns:
                return NurbsTriangles(body, f, ns);
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
                // (a piece turned round its axis, such as a revolved arc: in longitude and latitude)
                var sAx = sp.Axis.Normalized(); var sx = (sp.XAxis - sAx * sp.XAxis.Dot(sAx)).Normalized(); var sy = sAx.Cross(sx);
                // (not with a point on a pole: there the longitude means nothing)
                bool pole = f.Loops.Any(l => LoopPoints(body, l).Any(p => Math.Abs((p - sp.Centre).Normalized().Dot(sAx)) > 1 - 1e-9));
                if (!pole && InAngles(body, f, Outward, false,
                        p => { var q = (p - sp.Centre).Normalized(); return (Math.Atan2(q.Dot(sy), q.Dot(sx)), Math.Asin(Math.Clamp(q.Dot(sAx), -1, 1))); },
                        (u, v) => sp.Centre + ((sx * Math.Cos(u) + sy * Math.Sin(u)) * Math.Cos(v) + sAx * Math.Sin(v)) * sp.Radius,
                        sp.Radius, sp.Radius) is { } piece) return piece;
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
                // (a piece turned round its axis, such as a revolved arc: in the angle round the axis and round the tube)
                var tx = (to.XAxis - ax * to.XAxis.Dot(ax)).Normalized(); var ty = ax.Cross(tx);
                if (InAngles(body, f, Outward, true,
                        p => { var q = p - to.Centre; double h = q.Dot(ax); var w = q - ax * h; return (Math.Atan2(w.Dot(ty), w.Dot(tx)), Math.Atan2(h, w.Length - to.MajorRadius)); },
                        (u, v) => to.Centre + (tx * Math.Cos(u) + ty * Math.Sin(u)) * (to.MajorRadius + to.MinorRadius * Math.Cos(v)) + ax * (to.MinorRadius * Math.Sin(v)),
                        to.MajorRadius, to.MinorRadius) is { } piece) return piece;
                // (one loop over two seams, as OpenCASCADE writes a whole ring)
                if (Unwrapped(body, f, true, true,
                        p => { var q = p - to.Centre; double h = q.Dot(ax); var w = q - ax * h; return (Math.Atan2(w.Dot(ty), w.Dot(tx)), Math.Atan2(h, w.Length - to.MajorRadius)); },
                        (u, v) => to.Centre + (tx * Math.Cos(u) + ty * Math.Sin(u)) * (to.MajorRadius + to.MinorRadius * Math.Cos(v)) + ax * (to.MinorRadius * Math.Sin(v)),
                        to.MajorRadius, to.MinorRadius) is { } whole) return whole;
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

    /// <summary>
    /// The triangles of a free-form (NURBS) face, from its parameters. The whole surface (no loops but single points, as
    /// DesignSpark's revolved spline; or a loop round the edge of the parameters, as a corner piece PushPullMe makes): a
    /// grid over the parameters. A face cut out of it: its loops taken to the parameters (<see cref="BrepNurbsSurface.Parameters"/>),
    /// cut where a closed direction has the widest gap, triangulated there and refined with the points on the surface.
    /// Wound along the natural normal dS/du × dS/dv (reversed: the other way). Null when that does not work.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? NurbsTriangles(BrepBody body, BrepFace f, BrepNurbsSurface ns)
    {
        double u0 = ns.UKnots[0], u1 = ns.UKnots[^1], v0 = ns.VKnots[0], v1 = ns.VKnots[^1];
        double du = u1 - u0, dv = v1 - v0;
        Vec3 Natural(double u, double v)
        {
            double hu = du * 1e-6, hv = dv * 1e-6;
            double ua = Math.Max(u0, u - hu), ub = Math.Min(u1, u + hu), va = Math.Max(v0, v - hv), vb = Math.Min(v1, v + hv);
            var n = (ns.At(ub, v) - ns.At(ua, v)).Cross(ns.At(u, vb) - ns.At(u, va));
            return f.Reversed ? n * -1 : n;
        }
        var loops = f.Loops.Select(l => LoopPoints(body, l)).Where(l => l.Count >= 3).ToList();
        // (each point from the last one along the loop: Newton only, the grid search only when that fails)
        var uv = loops.Select(l =>
        {
            var r = new List<(double U, double V)>(l.Count);
            foreach (var p in l) r.Add(r.Count == 0 ? ns.Parameters(p) : ns.Parameters(p, r[^1]));
            return r;
        }).ToList();
        bool Border((double U, double V) q) => Math.Abs(q.U - u0) < du * 1e-6 || Math.Abs(q.U - u1) < du * 1e-6 || Math.Abs(q.V - v0) < dv * 1e-6 || Math.Abs(q.V - v1) < dv * 1e-6;
        // (round the edge of the parameters means reaching all four sides: a thread's flank in helix.rsdocx has all its
        // points on the two sides along its helices, yet stops short of the surface's ends)
        bool Spans(List<(double U, double V)> l) => l.Min(q => q.U) - u0 < du * 1e-6 && u1 - l.Max(q => q.U) < du * 1e-6
            && l.Min(q => q.V) - v0 < dv * 1e-6 && v1 - l.Max(q => q.V) < dv * 1e-6;
        if (uv.Count == 0 || (uv.Count == 1 && uv[0].All(Border) && Spans(uv[0])))
        {
            // the whole surface: a grid, fine enough that the surface between its points keeps within 0.01 mm of a straight
            // line (in each direction doubled from 4 per control point till it does; a twisted loft needs many)
            int Steps(int start, Func<double, double, Vec3> p)
            {
                for (int n = start; ; n *= 2)
                {
                    double worst = 0;
                    for (int k = 0; k <= 8 && worst <= 1e-5; k++)
                        for (int i = 0; i < n; i++)
                        {
                            double a = (double)i / n, b = (double)(i + 1) / n, c = k / 8.0;
                            worst = Math.Max(worst, (p((a + b) / 2, c) - (p(a, c) + p(b, c)) * 0.5).Length);
                        }
                    if (worst <= 1e-5 || n >= 512) return n;
                }
            }
            int nu = Steps(Math.Max(12, 4 * ns.NU), (t, c) => ns.At(u0 + du * t, v0 + dv * c));
            int nv = Steps(Math.Max(12, 4 * ns.NV), (t, c) => ns.At(u0 + du * c, v0 + dv * t));
            var g = new Vec3[nu + 1, nv + 1];
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++) g[i, j] = ns.At(u0 + du * i / nu, v0 + dv * j / nv);
            var grid = new List<(Vec3, Vec3, Vec3)>();
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    // (natural normal dS/du × dS/dv; reversed: the other way round)
                    if (!f.Reversed) { grid.Add((g[i, j], g[i + 1, j], g[i + 1, j + 1])); grid.Add((g[i, j], g[i + 1, j + 1], g[i, j + 1])); }
                    else { grid.Add((g[i, j], g[i + 1, j + 1], g[i + 1, j])); grid.Add((g[i, j], g[i, j + 1], g[i + 1, j + 1])); }
                }
            return grid.Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-18).ToList();
        }
        // a closed direction: cut where the loops leave the widest gap (a face going all the way round: not this way)
        bool uClosed = ns.Format is { UClosed: true } or { UPeriodic: true }, vClosed = ns.Format is { VClosed: true } or { VPeriodic: true };
        static double? Cut(IEnumerable<double> values, double lo, double period)
        {
            var s = values.Select(x => x - lo).Order().ToList();
            double gap = s[0] + period - s[^1], mid = s[^1] + gap / 2;
            for (int i = 0; i + 1 < s.Count; i++)
                if (s[i + 1] - s[i] > gap) { gap = s[i + 1] - s[i]; mid = (s[i] + s[i + 1]) / 2; }
            return gap > period * 0.02 ? lo + mid % period : null;
        }
        double cu = u0, cv = v0;
        if (uClosed) { if (Cut(uv.SelectMany(l => l.Select(q => q.U)), u0, du) is not double c) return null; cu = c; }
        if (vClosed) { if (Cut(uv.SelectMany(l => l.Select(q => q.V)), v0, dv) is not double c) return null; cv = c; }
        static double From(double x, double cut, double period) { double t = (x - cut) % period; return t < 0 ? t + period : t; }
        (double U, double V) Lay((double U, double V) q) => (uClosed ? From(q.U, cu, du) : q.U - u0, vClosed ? From(q.V, cv, dv) : q.V - v0);
        double Wrap(double x, double cut, double lo, double period, bool closed) => closed ? lo + (x + cut - lo) % period : lo + x;
        Vec3 At((double U, double V) q) => ns.At(Wrap(q.U, cu, u0, du, uClosed), Wrap(q.V, cv, v0, dv, vClosed));
        var laid = uv.Select(l => l.Select(Lay).ToList()).ToList();
        // (a rectangle in the parameters, as a seam makes it: a neat grid through its own edge points)
        // (the normal at each triangle near the parameters of the one before, its neighbour: only for its side, so Newton
        // steps from there do; a search over the whole surface per triangle takes most of a minute for helix.rsdocx's
        // flanks of 13 turns, and a triangle's middle, off the surface, often does not let Newton settle)
        var last = uv[0][0];
        Vec3 Outward(Vec3 p) { last = ns.Near(p, last); return Natural(last.U, last.V); }
        if (laid.Count == 1 && Grid(laid[0], loops[0], (u, v) => At((u, v)), Outward) is { } rect && rect.Count > 0) return rect;
        // (to lengths: so the triangles are not long and thin where the parameters stretch)
        double Len(Func<double, Vec3> c) { double s = 0; var p = c(0); for (int i = 1; i <= 16; i++) { var q = c(i / 16.0); s += (q - p).Length; p = q; } return s; }
        double su = Math.Max(1e-12, Len(t => ns.At(u0 + du * t, v0 + dv / 2))) / du, sv = Math.Max(1e-12, Len(t => ns.At(u0 + du / 2, v0 + dv * t))) / dv;
        double AreaOf(List<(double U, double V)> l) { double a = 0; for (int i = 0; i < l.Count; i++) a += l[i].U * l[(i + 1) % l.Count].V - l[(i + 1) % l.Count].U * l[i].V; return Math.Abs(a); }
        int outer = Enumerable.Range(0, laid.Count).MaxBy(i => AreaOf(laid[i]));
        var ordered = new[] { outer }.Concat(Enumerable.Range(0, laid.Count).Where(i => i != outer)).ToList();
        var flat = ordered.SelectMany(i => laid[i]).ToList();
        var pts3 = ordered.SelectMany(i => loops[i]).ToList();
        var idx = Triangulator.Triangulate(ordered.Select(i => (IReadOnlyList<(double, double)>)laid[i].Select(q => (q.U * su, q.V * sv)).ToList()).ToList());
        if (idx.Count == 0) return null;
        var tris = idx.Select(t => ((flat[t.A], pts3[t.A]), (flat[t.B], pts3[t.B]), (flat[t.C], pts3[t.C]))).ToList();
        ((double U, double V), Vec3) Mid(((double U, double V) Q, Vec3) a, ((double U, double V) Q, Vec3) b)
        {
            var q = ((a.Q.U + b.Q.U) / 2, (a.Q.V + b.Q.V) / 2);
            return (q, At(q));
        }
        for (int level = 0; level < 3; level++)
        {
            var next = new List<(((double, double), Vec3), ((double, double), Vec3), ((double, double), Vec3))>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                var ab = Mid(a, b); var bc = Mid(b, c); var ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        return tris.Select(t =>
        {
            Vec3 a = t.Item1.Item2, b = t.Item2.Item2, c = t.Item3.Item2;
            var (mu, mv) = ((t.Item1.Item1.Item1 + t.Item2.Item1.Item1 + t.Item3.Item1.Item1) / 3, (t.Item1.Item1.Item2 + t.Item2.Item1.Item2 + t.Item3.Item1.Item2) / 3);
            var n = Natural(Wrap(mu, cu, u0, du, uClosed), Wrap(mv, cv, v0, dv, vClosed));
            return (b - a).Cross(c - a).Dot(n) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>
    /// A face of one loop laid out in its two parameters with the angles counted on along the loop (not jumping back at
    /// a whole turn): a loop over a seam (the same edge both ways, as OpenCASCADE writes a whole cylinder, cone or ring)
    /// becomes a rectangle there. Triangulated (scaled by <paramref name="su"/>, <paramref name="sv"/> to lengths),
    /// refined three times with the points exactly on the surface, wound along the face's normal. Null when that does not
    /// give a proper outline.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? Unwrapped(BrepBody body, BrepFace f, bool periodicU, bool periodicV,
        Func<Vec3, (double U, double V)> angles, Func<double, double, Vec3> at, double su, double sv)
    {
        if (f.Loops.Count != 1) return null;
        var pts = LoopPoints(body, f.Loops[0]);
        if (pts.Count < 3) return null;
        var uv = new List<(double U, double V)>(pts.Count);
        foreach (var p in pts)
        {
            var (u, v) = angles(p);
            if (uv.Count > 0)
            {
                var (pu, pv) = uv[^1];
                if (periodicU) u += 2 * Math.PI * Math.Round((pu - u) / (2 * Math.PI));
                if (periodicV) v += 2 * Math.PI * Math.Round((pv - v) / (2 * Math.PI));
            }
            uv.Add((u, v));
        }
        // (a rectangle there, as a seam makes it: a neat grid through its own edge points)
        if (Grid(uv, pts, at, p => NormalAt(f, p) ?? new Vec3(0, 0, 0)) is { } grid && grid.Count > 0) return grid;
        var idx = Triangulator.Triangulate([uv.Select(q => (q.U * su, q.V * sv)).ToList()]);
        if (idx.Count == 0) return null;
        var tris = idx.Select(t => ((uv[t.A], pts[t.A]), (uv[t.B], pts[t.B]), (uv[t.C], pts[t.C]))).ToList();
        ((double U, double V), Vec3) Mid(((double U, double V) Q, Vec3) a, ((double U, double V) Q, Vec3) b)
        {
            var q = ((a.Q.U + b.Q.U) / 2, (a.Q.V + b.Q.V) / 2);
            return (q, at(q.Item1, q.Item2));
        }
        for (int level = 0; level < 3; level++)
        {
            var next = new List<(((double, double), Vec3), ((double, double), Vec3), ((double, double), Vec3))>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                var ab = Mid(a, b); var bc = Mid(b, c); var ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        return tris.Select(t =>
        {
            Vec3 a = t.Item1.Item2, b = t.Item2.Item2, c = t.Item3.Item2;
            var n = NormalAt(f, (a + b + c) * (1.0 / 3)) ?? (b - a).Cross(c - a);
            return (b - a).Cross(c - a).Dot(n) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>The largest bend between two flat triangles whose edge is soft (see <see cref="SoftEdges"/>): as
    /// SketchUp softens a mesh it imports (its default is 20°; a Revit terrain bends up to some 30° between facets).</summary>
    public const double SoftBendDegrees = 30;

    /// <summary>
    /// Which edges of a body are soft, not drawn (as in SketchUp): between two curved faces, and between two flat
    /// triangles (a mesh's facets: a Revit terrain, a face Revit saved only as triangles) that bend less than
    /// <see cref="SoftBendDegrees"/>. The edge where such facets meet a face of another kind, or bend sharply (a terrain's
    /// top and its side), stays.
    /// </summary>
    public static bool[] SoftEdges(BrepBody body)
    {
        var curvedUses = new int[body.Edges.Count];
        var facets = new List<int>?[body.Edges.Count];
        for (int f = 0; f < body.Faces.Count; f++)
        {
            var face = body.Faces[f];
            bool facet = face is { Surface: BrepPlane, Loops: [{ Count: 3 }] };
            foreach (var c in face.Loops.SelectMany(l => l))
            {
                if (face.Surface is not BrepPlane) curvedUses[c.Edge]++;
                if (facet) (facets[c.Edge] ??= []).Add(f);
            }
        }
        double cos = Math.Cos(SoftBendDegrees * Math.PI / 180);
        var soft = new bool[body.Edges.Count];
        for (int e = 0; e < body.Edges.Count; e++)
            soft[e] = curvedUses[e] >= 2 || facets[e] is [var a, var b] && Normal(body.Faces[a]).Dot(Normal(body.Faces[b])) > cos;
        return soft;
    }

    /// <summary>
    /// Smooth normals for triangles of one face whose surface gives none (a free-form face): at each point the
    /// area-weighted mean of the normals of the triangles round it; null for a point not among them.
    /// </summary>
    public static Func<Vec3, Vec3?> SmoothNormals(IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> tris)
    {
        static (long, long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e8), (long)Math.Round(p.Y * 1e8), (long)Math.Round(p.Z * 1e8));
        var sum = new Dictionary<(long, long, long), Vec3>();
        foreach (var (a, b, c) in tris)
        {
            var n = (b - a).Cross(c - a);
            foreach (var p in new[] { a, b, c }) sum[Key(p)] = sum.TryGetValue(Key(p), out var s) ? s + n : n;
        }
        return p => sum.TryGetValue(Key(p), out var s) && s.Length > 1e-30 ? s.Normalized() : null;
    }

    /// <summary>
    /// A face on a surface with two angles (u round its axis, v along its profile; v periodic for a torus) laid out in
    /// those angles (scaled by <paramref name="su"/> and <paramref name="sv"/> to lengths), each cut where the face is not
    /// (the middle of the widest gap between its edge points), triangulated there, every triangle cut in 4 three times
    /// with the new points exactly on the surface; wound along <paramref name="outward"/>. Unlike laying it flat this
    /// holds for a face going more than half way round. Null when the face goes all the way round in an angle.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? InAngles(BrepBody body, BrepFace f, Func<Vec3, Vec3> outward, bool periodicV,
        Func<Vec3, (double U, double V)> angles, Func<double, double, Vec3> at, double su, double sv)
    {
        var loops = f.Loops.Select(l => LoopPoints(body, l)).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return null;
        var raw = loops.Select(l => l.Select(angles).ToList()).ToList();
        static double? Cut(IEnumerable<double> values)
        {
            var s = values.Select(a => a < 0 ? a + 2 * Math.PI : a).Order().ToList();
            double gap = s[0] + 2 * Math.PI - s[^1], mid = (s[^1] + s[0] + 2 * Math.PI) / 2;
            for (int i = 0; i + 1 < s.Count; i++)
                if (s[i + 1] - s[i] > gap) { gap = s[i + 1] - s[i]; mid = (s[i] + s[i + 1]) / 2; }
            return gap > 3 * ArcStep ? mid : null;
        }
        static double From(double a, double cut) { double t = (a - cut) % (2 * Math.PI); return t < 0 ? t + 2 * Math.PI : t; }
        // (round the axis: not in a gap the face runs on into, which the widest is when its edges all go round the tube,
        // such as a ring turned 270°: seen from outside the face lies left of its edge)
        if (CutU() is not double cu) return null;
        double? CutU()
        {
            var all = new List<(double U, double Side)>();
            for (int li = 0; li < loops.Count; li++)
                for (int i = 0; i < loops[li].Count; i++)
                {
                    var l = loops[li]; var (u, v) = raw[li][i];
                    var t = l[(i + 1) % l.Count] - l[(i - 1 + l.Count) % l.Count];
                    var eu = at(u + 1e-6, v) - at(u - 1e-6, v);
                    double side = outward(l[i]).Cross(t).Normalized().Dot(eu.Normalized());
                    all.Add((u < 0 ? u + 2 * Math.PI : u, double.IsNaN(side) ? 0 : side));
                }
            all.Sort((x, y) => x.U.CompareTo(y.U));
            var gaps = new List<(double Size, double Mid, double A, double B)>();
            for (int i = 0; i < all.Count; i++)
            {
                double a = all[i].U, b = i + 1 < all.Count ? all[i + 1].U : all[0].U + 2 * Math.PI;
                if (b - a > 3 * ArcStep) gaps.Add((b - a, (a + b) / 2, a, b));
            }
            // the side the face goes to from the points at either end of a gap (the clearest of them)
            double Side(double at) => all.Where(x => Math.Abs(Math.IEEERemainder(x.U - at, 2 * Math.PI)) < 1e-9).Select(x => x.Side).OrderByDescending(Math.Abs).FirstOrDefault();
            foreach (var g in gaps.OrderByDescending(g => g.Size))
                if (!(Side(g.A) > 0.5 || Side(g.B) < -0.5)) return g.Mid;
            return null;
        }
        double cv = 0;
        if (periodicV)
        {
            if (Cut(raw.SelectMany(l => l.Select(q => q.V))) is double c) cv = c;
            // all the way round the tube (a disc turned round an axis): a band between two rings, round in v
            else return Band(raw.Select(l => l.Select(q => From(q.U, cu)).ToList()).ToList(), (u, v) => at(u + cu, v), outward)
                ?? RingBand(raw.Select(l => l.Select(q => (From(q.U, cu), q.V)).ToList()).ToList(), loops, (u, v) => at(u + cu, v), outward)
                ?? WrappedBand(raw.Select(l => l.Select(q => (From(q.U, cu), q.V)).ToList()).ToList(), loops, (u, v) => at(u + cu, v), outward, su, sv);
        }
        var uv = raw.Select(l => l.Select(q => (U: From(q.U, cu), V: periodicV ? From(q.V, cv) : q.V)).ToList()).ToList();
        if (uv.Count == 1 && Grid(uv[0], loops[0], (u, v) => at(u + cu, periodicV ? v + cv : v), outward) is { } grid) return grid;
        // the outer loop: the largest in the angles
        double Area(List<(double U, double V)> l) { double s = 0; for (int i = 0; i < l.Count; i++) s += l[i].U * l[(i + 1) % l.Count].V - l[(i + 1) % l.Count].U * l[i].V; return Math.Abs(s); }
        int outer = Enumerable.Range(0, uv.Count).MaxBy(i => Area(uv[i]));
        var ordered = new[] { uv[outer] }.Concat(uv.Where((_, i) => i != outer)).ToList();
        var flat = ordered.SelectMany(l => l).ToList();
        var pts3 = new[] { loops[outer] }.Concat(loops.Where((_, i) => i != outer)).SelectMany(l => l).ToList();
        var idx = Triangulator.Triangulate(ordered.Select(l => (IReadOnlyList<(double, double)>)l.Select(q => (q.U * su, q.V * sv)).ToList()).ToList());
        if (idx.Count == 0) return null;
        // (the corners keep their own points; new ones are made from the angles)
        var tris = idx.Select(t => ((flat[t.A], pts3[t.A]), (flat[t.B], pts3[t.B]), (flat[t.C], pts3[t.C]))).ToList();
        ((double U, double V), Vec3) Mid(((double U, double V) A, Vec3) a, ((double U, double V) A, Vec3) b)
        {
            double u = (a.A.U + b.A.U) / 2, v = (a.A.V + b.A.V) / 2;
            return ((u, v), at(u + cu, periodicV ? v + cv : v));
        }
        for (int level = 0; level < 3; level++)
        {
            var next = new List<(((double, double), Vec3), ((double, double), Vec3), ((double, double), Vec3))>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                var ab = Mid(a, b); var bc = Mid(b, c); var ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        return tris.Select(t =>
        {
            Vec3 a = t.Item1.Item2, b = t.Item2.Item2, c = t.Item3.Item2;
            return (b - a).Cross(c - a).Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>A band all the way round the tube of a torus between two rings at fixed u (the ends of a disc turned
    /// part of the way round an axis): a grid over u (steps of at most ArcStep) and all the way round v; null when its
    /// loops are not two such rings.</summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? Band(List<List<double>> loopU, Func<double, double, Vec3> at, Func<Vec3, Vec3> outward)
    {
        if (loopU.Count != 2 || loopU.Any(l => l.Max() - l.Min() > 1e-6)) return null;
        double u0 = Math.Min(loopU[0].Average(), loopU[1].Average()), u1 = Math.Max(loopU[0].Average(), loopU[1].Average());
        if (u1 - u0 < 1e-9) return null;
        int nu = Math.Max(1, (int)Math.Ceiling((u1 - u0) / ArcStep)), nv = (int)Math.Round(2 * Math.PI / ArcStep);
        var g = new Vec3[nu + 1, nv];
        for (int i = 0; i <= nu; i++) for (int j = 0; j < nv; j++) g[i, j] = at(u0 + (u1 - u0) * i / nu, 2 * Math.PI * j / nv);
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int k = (j + 1) % nv;
                tris.Add((g[i, j], g[i + 1, j], g[i + 1, k]));
                tris.Add((g[i, j], g[i + 1, k], g[i, k]));
            }
        return tris.Select(t =>
        {
            var (a, b, c) = t;
            return (b - a).Cross(c - a).Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>
    /// A band all the way round the tube between two loops that each go once round it, steadily (each point further round
    /// than the one before): rings of points round the tube, the first and last the loops themselves with all their own
    /// points (so it meets the faces beside it without a gap), between them rings at even steps of v whose u runs evenly
    /// from the one loop to the other; neighbouring rings zipped into triangles. Null when the loops are not like that.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? RingBand(List<List<(double U, double V)>> uv, List<List<Vec3>> pts, Func<double, double, Vec3> at, Func<Vec3, Vec3> outward)
    {
        if (uv.Count != 2) return null;
        const double Tau = 2 * Math.PI;
        var rings = new List<List<(double Phi, double U, Vec3 P)>>();
        foreach (var (l, p) in uv.Zip(pts))
        {
            // v unwrapped along the loop; it must go once round, always the same way
            var un = new List<double> { l[0].V };
            for (int i = 1; i < l.Count; i++) { double step = l[i].V - l[i - 1].V; step -= Tau * Math.Round(step / Tau); un.Add(un[^1] + step); }
            double close = l[0].V - l[^1].V; close -= Tau * Math.Round(close / Tau);
            double turn = un[^1] + close - un[0];
            if (Math.Abs(Math.Abs(turn) - Tau) > 1e-6) return null;
            for (int i = 1; i < un.Count; i++) if ((un[i] - un[i - 1]) * Math.Sign(turn) < -1e-12) return null;
            var ring = Enumerable.Range(0, l.Count).Select(i => (Phi: ((l[i].V % Tau) + Tau) % Tau, l[i].U, P: p[i])).OrderBy(x => x.Phi).ToList();
            ring = ring.Where((x, i) => i == 0 || x.Phi - ring[i - 1].Phi > 1e-12).ToList();
            if (ring.Count < 3) return null;
            rings.Add(ring);
        }
        double UAt(List<(double Phi, double U, Vec3 P)> r, double phi)
        {
            int j = r.FindIndex(x => x.Phi > phi);
            var (a, b) = j <= 0 ? (r[^1], r[0]) : (r[j - 1], r[j]);
            double pa = a.Phi, pb = b.Phi; if (pb <= pa) pb += Tau; double ph = phi; if (ph < pa) ph += Tau;
            return a.U + (b.U - a.U) * (pb - pa < 1e-15 ? 0 : (ph - pa) / (pb - pa));
        }
        int nv = (int)Math.Round(Tau / ArcStep);
        var vs = Enumerable.Range(0, nv).Select(j => Tau * j / nv).ToList();
        double widest = vs.Max(v => Math.Abs(UAt(rings[1], v) - UAt(rings[0], v)));
        int nu = Math.Max(1, (int)Math.Ceiling(widest / ArcStep));
        var rows = new List<List<(double Phi, Vec3 P)>> { rings[0].Select(x => (x.Phi, x.P)).ToList() };
        for (int i = 1; i < nu; i++)
            rows.Add(vs.Select(v => { double ua = UAt(rings[0], v), ub = UAt(rings[1], v); return (v, at(ua + (ub - ua) * i / nu, v)); }).ToList());
        rows.Add(rings[1].Select(x => (x.Phi, x.P)).ToList());
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int r = 0; r + 1 < rows.Count; r++) Zip(rows[r], rows[r + 1], tris);
        return tris.Select(t =>
        {
            var (a, b, c) = t;
            return (b - a).Cross(c - a).Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>
    /// A band all the way round the tube between two loops that each go once round it, of any shape (DesignSpark's
    /// turned ring in kubus_getrokken.rsdocx: one end where the ring leaves the block, an arc and a cut-curve; the other
    /// a circle): both cut open where they cross one angle v* round the tube, laid one after the other into a single
    /// outline from v* to v* + 2π and back, triangulated in the angles and refined with the points exactly on the
    /// surface; wound along <paramref name="outward"/>. <paramref name="uv"/>: per loop the angles of its points (u already
    /// cut open, v as it comes). Null when the loops are not two going once round the tube.
    /// </summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? WrappedBand(List<List<(double U, double V)>> uv, List<List<Vec3>> pts, Func<double, double, Vec3> at,
        Func<Vec3, Vec3> outward, double su, double sv)
    {
        if (uv.Count != 2) return null;
        // per loop: v unwrapped along it, and how far it turns round the tube
        var paths = new List<List<(double U, double V, Vec3 P)>>();
        double seam = uv[0][0].V + 1e-3;
        foreach (var (l, p) in uv.Zip(pts))
        {
            var un = new List<double> { l[0].V };
            for (int i = 1; i <= l.Count; i++)
            {
                double step = l[i % l.Count].V - l[i - 1].V;
                step -= 2 * Math.PI * Math.Round(step / (2 * Math.PI));
                un.Add(un[^1] + step);
            }
            double turn = un[^1] - un[0];
            if (Math.Abs(Math.Abs(turn) - 2 * Math.PI) > 1e-6) return null;
            // run it so v goes up, and start where it crosses the seam
            var seq = Enumerable.Range(0, l.Count + 1).Select(i => (U: l[i % l.Count].U, V: un[i], P: p[i % l.Count])).ToList();
            if (turn < 0) seq.Reverse();
            int k = -1; double vs = 0;
            for (int i = 0; i + 1 < seq.Count && k < 0; i++)
            {
                double a = seq[i].V, b = seq[i + 1].V;
                // (the seam, shifted by whole turns into this step)
                double lo = Math.Min(a, b), hi = Math.Max(a, b);
                double t0 = seam + 2 * Math.PI * Math.Ceiling((lo - seam) / (2 * Math.PI));
                if (t0 >= lo && t0 < hi) { k = i; vs = t0; }
            }
            if (k < 0) return null;
            var (qa, qb) = (seq[k], seq[k + 1]);
            double f = (vs - qa.V) / (qb.V - qa.V);
            double us = qa.U + (qb.U - qa.U) * f;
            var start = (U: us, V: vs, P: at(us, vs));
            var path = new List<(double U, double V, Vec3 P)> { start };
            for (int i = k + 1; i < seq.Count; i++) path.Add(seq[i]);
            for (int i = 1; i <= k; i++) path.Add((seq[i].U, seq[i].V + 2 * Math.PI, seq[i].P));
            path.Add((us, vs + 2 * Math.PI, start.P));
            // (in the angles from the seam: 0 .. 2π, going up)
            double v0 = path[0].V;
            paths.Add(path.Select(q => (q.U, q.V - v0, q.P)).ToList());
        }
        // one outline: the first loop up from 0 to 2π, the second back down from 2π to 0
        var outline = paths[0].Concat(Enumerable.Reverse(paths[1])).ToList();
        double seamV = seam;
        var idx = Triangulator.Triangulate([outline.Select(q => (q.U * su, q.V * sv)).ToList()]);
        if (idx.Count == 0) return null;
        var tris = idx.Select(t => ((outline[t.A].U, outline[t.A].V, outline[t.A].P), (outline[t.B].U, outline[t.B].V, outline[t.B].P), (outline[t.C].U, outline[t.C].V, outline[t.C].P))).ToList();
        (double U, double V, Vec3 P) Mid((double U, double V, Vec3 P) a, (double U, double V, Vec3 P) b)
        {
            double u = (a.U + b.U) / 2, v = (a.V + b.V) / 2;
            return (u, v, at(u, v + seamV));
        }
        for (int level = 0; level < 3; level++)
        {
            var next = new List<((double, double, Vec3), (double, double, Vec3), (double, double, Vec3))>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                var ab = Mid(a, b); var bc = Mid(b, c); var ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        return tris.Select(t =>
        {
            Vec3 a = t.Item1.Item3, b = t.Item2.Item3, c = t.Item3.Item3;
            return (b - a).Cross(c - a).Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
    }

    /// <summary>A face that is a rectangle in its two angles (an arc or line turned round an axis, its edges the curves
    /// at the ends): a grid through the angles of its own edge points, so the triangles are neat and meet the edges;
    /// null when the face is not like that.</summary>
    private static List<(Vec3 A, Vec3 B, Vec3 C)>? Grid(List<(double U, double V)> uv, List<Vec3> pts, Func<double, double, Vec3> at, Func<Vec3, Vec3> outward)
    {
        const double eps = 1e-7;
        double u0 = uv.Min(q => q.U), u1 = uv.Max(q => q.U), v0 = uv.Min(q => q.V), v1 = uv.Max(q => q.V);
        if (u1 - u0 < eps || v1 - v0 < eps) return null;
        bool OnU(double u, double x) => Math.Abs(u - x) < eps;
        if (!uv.All(q => OnU(q.U, u0) || OnU(q.U, u1) || OnU(q.V, v0) || OnU(q.V, v1))) return null;
        // the steps: the angles of the edge points along the sides (plus the corners)
        static List<double> Steps(IEnumerable<double> xs, double lo, double hi)
        {
            var r = new List<double>();
            foreach (var x in xs.Append(lo).Append(hi).Where(x => x >= lo - eps && x <= hi + eps).Order())
                if (r.Count == 0 || x - r[^1] > eps) r.Add(x);
            r[0] = lo; r[^1] = hi;
            return r;
        }
        var us = Steps(uv.Where(q => OnU(q.V, v0) || OnU(q.V, v1)).Select(q => q.U), u0, u1);
        var vs = Steps(uv.Where(q => OnU(q.U, u0) || OnU(q.U, u1)).Select(q => q.V), v0, v1);
        // (the face's own points where it has them: exactly the edges)
        Vec3 P(double u, double v)
        {
            for (int i = 0; i < uv.Count; i++) if (OnU(uv[i].U, u) && OnU(uv[i].V, v)) return pts[i];
            return at(u, v);
        }
        var g = new Vec3[us.Count, vs.Count];
        for (int i = 0; i < us.Count; i++) for (int j = 0; j < vs.Count; j++) g[i, j] = P(us[i], vs[j]);
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i + 1 < us.Count; i++)
            for (int j = 0; j + 1 < vs.Count; j++)
            {
                tris.Add((g[i, j], g[i + 1, j], g[i + 1, j + 1]));
                tris.Add((g[i, j], g[i + 1, j + 1], g[i, j + 1]));
            }
        return tris.Select(t =>
        {
            var (a, b, c) = t;
            return (b - a).Cross(c - a).Dot(outward((a + b + c) * (1.0 / 3))) >= 0 ? (a, b, c) : (a, c, b);
        }).Where(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length > 1e-20).ToList();
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
