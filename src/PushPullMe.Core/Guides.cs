using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A guide line (as the tape measure makes in SketchUp): endless, through <see cref="Point"/> along
/// <see cref="Dir"/> (a unit vector). Not part of the model: only for drawing and moving precisely.</summary>
public readonly record struct Guide(Vec3 Point, Vec3 Dir);

/// <summary>The geometry of guide lines.</summary>
public static class Guides
{
    /// <summary>Distance (metres) within which lines count as meeting, and points as on a line or plane.</summary>
    public const double Tol = 1e-7;

    /// <summary>The guide parallel to the edge line through <paramref name="a"/> along <paramref name="dir"/>, through
    /// <paramref name="p"/>; with the offset from the edge (square to it) that makes.</summary>
    public static (Guide Guide, Vec3 Offset) Parallel(Vec3 a, Vec3 dir, Vec3 p)
    {
        dir = dir.Normalized();
        var d = p - a;
        var off = d - dir * d.Dot(dir);
        return (new Guide(a + off, dir), off);
    }

    /// <summary>The point of the line nearest to p.</summary>
    public static Vec3 Closest(Guide g, Vec3 p) => g.Point + g.Dir * (p - g.Point).Dot(g.Dir);

    /// <summary>Where two lines (through a along da, through b along db) meet; null when they are parallel or miss each
    /// other (by more than <see cref="Tol"/>).</summary>
    public static Vec3? Meet(Vec3 a, Vec3 da, Vec3 b, Vec3 db)
    {
        da = da.Normalized(); db = db.Normalized();
        var w = a - b;
        double bb = da.Dot(db), den = 1 - bb * bb;
        if (den < 1e-12) return null;
        double t = (bb * w.Dot(db) - w.Dot(da)) / den, s = (w.Dot(db) - bb * w.Dot(da)) / den;
        Vec3 p = a + da * t, q = b + db * s;
        return (p - q).Length < Tol ? (p + q) * 0.5 : null;
    }

    /// <summary>Where a guide meets the straight segment a–b (inside it, ends included); or null.</summary>
    public static Vec3? MeetSegment(Guide g, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        if (ab.Length < Tol) return null;
        if (Meet(g.Point, g.Dir, a, ab) is not { } p) return null;
        double t = (p - a).Dot(ab) / ab.Dot(ab);
        return t >= -1e-9 && t <= 1 + 1e-9 ? p : null;
    }

    /// <summary>Whether a guide lies in the plane through o with normal n.</summary>
    public static bool InPlane(Guide g, Vec3 o, Vec3 n) => Math.Abs(g.Dir.Dot(n)) < 1e-9 && Math.Abs((g.Point - o).Dot(n)) < Tol;
}
