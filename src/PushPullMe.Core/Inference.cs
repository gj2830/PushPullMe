using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>Inferring a point as SketchUp does, the geometry of it (what is near the mouse is the editor's to judge).</summary>
public static class Inference
{
    /// <summary>
    /// Points lined up with <paramref name="corner"/> (SketchUp's dotted "in line with a point") in the plane through
    /// <paramref name="q"/> with normal <paramref name="n"/>: per axis lying in that plane, on the line from the corner
    /// (taken into the plane) along it, where the line from <paramref name="from"/> along <paramref name="dir"/> crosses
    /// it (none when they run parallel), or without them its point nearest q. With the axis's number and the corner in
    /// the plane.
    /// </summary>
    public static IEnumerable<(Vec3 P, int Axis, Vec3 Corner)> LineUp(Vec3 q, Vec3 n, Vec3 corner, IReadOnlyList<Vec3> axes, Vec3? from = null, Vec3? dir = null)
    {
        var r = corner - n * (corner - q).Dot(n);
        for (int i = 0; i < axes.Count; i++)
        {
            var ax = axes[i];
            if (Math.Abs(ax.Dot(n)) > 1e-6) continue;
            if (from is { } a && dir is { } d)
            {
                double den = d.Cross(ax).Dot(n);
                if (Math.Abs(den) < 1e-9) continue;
                yield return (a + d * ((r - a).Cross(ax).Dot(n) / den), i, r);
            }
            else yield return (r + ax * (q - r).Dot(ax), i, r);
        }
    }
}
