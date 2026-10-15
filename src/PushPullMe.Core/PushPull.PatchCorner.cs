using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class PushPull
{
    /// <summary>A cylinder: a point of its centre line, the line's direction (unit) and its radius.</summary>
    internal readonly record struct Cyl(Vec3 Point, Vec3 Dir, double R)
    {
        public Vec3 Perp(Vec3 p) { var w = p - Point; return w - Dir * w.Dot(Dir); }
        public double Off(Vec3 p) => Perp(p).Length - R;
    }

    /// <summary>
    /// The point on all three cylinders nearest to <paramref name="corner"/> (three cylinders cut each other in up to eight
    /// points): Newton's method from several starting points; null when none is found.
    /// </summary>
    internal static Vec3? TriplePoint(Cyl a, Cyl b, Cyl c, Vec3 corner, IEnumerable<Vec3> guesses)
    {
        Vec3? best = null;
        foreach (var g in guesses)
            if (TriplePointFrom(a, b, c, g) is { } p && (best == null || (p - corner).Length < (best.Value - corner).Length - 1e-12)) best = p;
        return best;
    }

    private static Vec3? TriplePointFrom(Cyl a, Cyl b, Cyl c, Vec3 guess)
    {
        var p = guess;
        var cs = new[] { a, b, c };
        for (int it = 0; it < 60; it++)
        {
            var f = cs.Select(x => x.Perp(p).Dot(x.Perp(p)) - x.R * x.R).ToArray();
            if (f.All(v => Math.Abs(v) < 1e-22)) return p;
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++) { var g = cs[i].Perp(p) * 2; m[i, 0] = g.X; m[i, 1] = g.Y; m[i, 2] = g.Z; }
            if (Solve3(m, f.Select(v => -v).ToArray()) is not { } d) return null;
            p += new Vec3(d[0], d[1], d[2]);
        }
        return cs.All(x => Math.Abs(x.Off(p)) < 1e-10) ? p : null;
    }

    /// <summary>
    /// The curve where two cylinders cut each other, from <paramref name="from"/> to <paramref name="to"/> (both on both):
    /// round the one with the smaller radius, each point moved along its axis onto the other; a B-spline through 97 of
    /// them (close enough to both for the writer's 1e-8 m).
    /// </summary>
    internal static BrepBSpline CylinderCut(Cyl x, Cyl y, Vec3 from, Vec3 to)
    {
        // (made from the end where the two cut across back to where they touch: where two meet on a flat face they touch
        // (the same normal), the two roots of each step coincide there; following the right one is only sure starting
        // from the other end)
        double Touch(Vec3 p) => Math.Abs(x.Perp(p).Normalized().Dot(y.Perp(p).Normalized()));
        bool flip = Touch(from) >= Touch(to);
        if (flip) (from, to) = (to, from);
        var (carrier, other) = x.R <= y.R ? (x, y) : (y, x);
        var x0 = carrier.Perp(from); var y0 = carrier.Perp(to);
        var ex = x0.Normalized(); var ey = carrier.Dir.Cross(ex);
        double end = Math.Atan2(y0.Dot(ey), y0.Dot(ex));
        double tPrev = (from - carrier.Point).Dot(carrier.Dir);
        // t along the carrier's axis where its point at angle th lies on the other (the root nearest the last)
        double OnOther(Vec3 p0, double near)
        {
            Vec3 a0 = other.Perp(p0), av = carrier.Dir - other.Dir * carrier.Dir.Dot(other.Dir);
            double qa = av.Dot(av), qb = 2 * a0.Dot(av), qc = a0.Dot(a0) - other.R * other.R;
            double disc = qb * qb - 4 * qa * qc;
            if (qa < 1e-18 || disc < 0) throw new PushPullException("Deze rondingen snijden elkaar niet zoals verwacht; dat kan nog niet.");
            double t1 = (-qb - Math.Sqrt(disc)) / (2 * qa), t2 = (-qb + Math.Sqrt(disc)) / (2 * qa);
            return Math.Abs(t1 - near) < Math.Abs(t2 - near) ? t1 : t2;
        }
        const int n = 96;
        var pts = new List<Vec3> { from };
        for (int i = 1; i < n; i++)
        {
            double th = end * i / n;
            var p0 = carrier.Point + (ex * Math.Cos(th) + ey * Math.Sin(th)) * carrier.R;
            double t = OnOther(p0, tPrev);
            pts.Add(p0 + carrier.Dir * t);
            tPrev = t;
        }
        pts.Add(to);
        if (flip) pts.Reverse();
        return BSpline.Interpolate(pts, false);
    }
}
