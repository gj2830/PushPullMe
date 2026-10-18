using System.Numerics;
using PushPullMe.Core;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>
/// A shown mesh as picking sees it: its triangles (each three positions, as the view draws them), per triangle the face
/// of the body it belongs to, and the body. The positions are shared with what is drawn, not copied.
/// </summary>
public sealed class PickMesh
{
    public IReadOnlyList<Vector3> Positions { get; }
    public IReadOnlyList<int> FaceOfTriangle { get; }
    public int Body { get; }

    /// <summary>The box round it.</summary>
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    public PickMesh(IReadOnlyList<Vector3> positions, IReadOnlyList<int> faceOfTriangle, int body)
    {
        Positions = positions; FaceOfTriangle = faceOfTriangle; Body = body;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int i = 0; i < positions.Count; i++) { lo = Vector3.Min(lo, positions[i]); hi = Vector3.Max(hi, positions[i]); }
        Min = lo; Max = hi;
    }
}

/// <summary>A triangle of a shown mesh hit by a ray, and where.</summary>
public readonly record struct PickHit(PickMesh Mesh, int Triangle, Vec3 At)
{
    /// <summary>The face hit (null: a triangle with no face).</summary>
    public FaceRef? Face => Triangle < Mesh.FaceOfTriangle.Count ? new FaceRef(Mesh.Body, Mesh.FaceOfTriangle[Triangle]) : null;
}

/// <summary>What the shown meshes have under a point of the screen, worked out here (a viewer's own hit test lies about a
/// pixel off: a click just inside an edge fell beside the face).</summary>
public static class Picking
{
    /// <summary>The triangles the ray goes through, nearest first.</summary>
    public static List<PickHit> Hits(IEnumerable<PickMesh> meshes, ViewRay ray)
    {
        var o = ray.Origin; var d = ray.Direction.Normalized();
        var hits = new List<(double T, PickMesh Mesh, int Triangle)>();
        foreach (var mesh in meshes)
        {
            var p = mesh.Positions;
            if (p.Count < 3) continue;
            // (not through its box: none of its triangles)
            if (!RayHitsBox(o, d, mesh.Min, mesh.Max)) continue;
            for (int i = 0; i + 2 < p.Count; i += 3)
                if (RayTriangle(o, d, V(p[i]), V(p[i + 1]), V(p[i + 2])) is double t) hits.Add((t, mesh, i / 3));
        }
        return hits.OrderBy(h => h.T).Select(h => new PickHit(h.Mesh, h.Triangle, o + d * h.T)).ToList();
    }

    /// <summary>The nearest face hit and where, or null.</summary>
    public static (FaceRef Face, Vec3 At)? FaceAt(IEnumerable<PickMesh> meshes, ViewRay ray)
    {
        foreach (var h in Hits(meshes, ray))
            if (h.Face is { } f) return (f, h.At);
        return null;
    }

    /// <summary>The box round all of them (null: nothing shown).</summary>
    public static (Vec3 Min, Vec3 Max)? Bounds(IEnumerable<PickMesh> meshes)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var m in meshes)
            if (m.Positions.Count > 0) { lo = Vector3.Min(lo, m.Min); hi = Vector3.Max(hi, m.Max); any = true; }
        return any ? (V(lo), V(hi)) : null;
    }

    private static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Where along the ray (from o along unit d) it goes through the triangle (either side), or null.</summary>
    public static double? RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 e1 = b - a, e2 = c - a;
        var h = d.Cross(e2); double det = e1.Dot(h);
        if (Math.Abs(det) < 1e-12) return null;
        var s = o - a; double u = s.Dot(h) / det;
        if (u < 0 || u > 1) return null;
        var q = s.Cross(e1); double v = d.Dot(q) / det;
        if (v < 0 || u + v > 1) return null;
        double t = e2.Dot(q) / det;
        return t > 0 ? t : null;
    }

    /// <summary>Whether the ray (from o along d) goes through the box (a little grown, for rounding).</summary>
    public static bool RayHitsBox(Vec3 o, Vec3 d, Vector3 lo, Vector3 hi)
    {
        double t0 = 0, t1 = double.MaxValue;
        double[] org = [o.X, o.Y, o.Z], dir = [d.X, d.Y, d.Z], mn = [lo.X - 1e-3, lo.Y - 1e-3, lo.Z - 1e-3], mx = [hi.X + 1e-3, hi.Y + 1e-3, hi.Z + 1e-3];
        for (int k = 0; k < 3; k++)
        {
            if (Math.Abs(dir[k]) < 1e-15) { if (org[k] < mn[k] || org[k] > mx[k]) return false; continue; }
            double ta = (mn[k] - org[k]) / dir[k], tb = (mx[k] - org[k]) / dir[k];
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }
}
