using System.Numerics;
using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>The triangles of one body in one colour as a view draws them (millimetres): each three positions a
/// triangle, a normal per position, per triangle the face it belongs to; null colour: the standard one.</summary>
public sealed class SceneMesh(Vector3[] positions, Vector3[] normals, int[] faceOfTriangle, int body, uint? color)
{
    public Vector3[] Positions { get; } = positions;
    public Vector3[] Normals { get; } = normals;
    public int Body { get; } = body;
    public uint? Color { get; } = color;
    public PickMesh Pick { get; } = new(positions, faceOfTriangle, body);
}

/// <summary>
/// A document as a view shows it: per body and colour its triangles (flat faces flat, round faces shaded smooth) and
/// its edges as pairs of points (not between two round faces: those are soft, as in SketchUp). The same for every window.
/// </summary>
public sealed class Scene
{
    public IReadOnlyList<SceneMesh> Meshes { get; }
    public Vector3[] Edges { get; }

    private Scene(List<SceneMesh> meshes, Vector3[] edges) { Meshes = meshes; Edges = edges; }

    public IEnumerable<PickMesh> Picks => Meshes.Select(m => m.Pick);

    public static Scene Build(SketchDocument doc)
    {
        var meshes = new List<SceneMesh>();
        var edges = new List<Vector3>();
        static Vector3 P(Vec3 v) => new((float)(v.X * Units.Display), (float)(v.Y * Units.Display), (float)(v.Z * Units.Display));
        for (int b = 0; b < doc.Bodies.Count; b++)
        {
            var body = doc.Bodies[b];
            var byColor = new Dictionary<uint, (List<Vector3> Pos, List<Vector3> Nor, List<int> Map)>();
            const uint Standard = 0;
            for (int f = 0; f < body.Faces.Count; f++)
            {
                var face = body.Faces[f];
                uint key = doc.ColorOf(new FaceRef(b, f)) ?? Standard;
                if (!byColor.TryGetValue(key, out var mm)) byColor[key] = mm = (new(), new(), new());
                var tris = doc.TrianglesOf(new FaceRef(b, f));
                // (a free-form face has no normal of its own here: the mean of its triangles round each point)
                var smooth = face.Surface is BrepNurbsSurface ? FaceGeometry.SmoothNormals(tris) : null;
                foreach (var (a, bb, c) in tris)
                {
                    foreach (var p in new[] { a, bb, c })
                    {
                        mm.Pos.Add(P(p));
                        var n = smooth?.Invoke(p) ?? FaceGeometry.NormalAt(face, p) ?? (bb - a).Cross(c - a).Normalized();
                        mm.Nor.Add(new Vector3((float)n.X, (float)n.Y, (float)n.Z));
                    }
                    mm.Map.Add(f);
                }
            }
            foreach (var (key, (pos, nor, map)) in byColor)
                if (pos.Count > 0) meshes.Add(new SceneMesh(pos.ToArray(), nor.ToArray(), map.ToArray(), b, key == Standard ? null : key));

            var curvedUses = new int[body.Edges.Count];
            foreach (var face in body.Faces.Where(f => f.Surface is not BrepPlane))
                foreach (var c in face.Loops.SelectMany(l => l)) curvedUses[c.Edge]++;
            for (int e = 0; e < body.Edges.Count; e++)
            {
                if (curvedUses[e] >= 2) continue;
                var line = FaceGeometry.EdgePoints(body, body.Edges[e]);
                for (int i = 0; i + 1 < line.Count; i++) { edges.Add(P(line[i])); edges.Add(P(line[i + 1])); }
            }
        }
        return new Scene(meshes, edges.ToArray());
    }

    /// <summary>The triangles of one face (millimetres), for a highlight.</summary>
    public static Vector3[] FaceTriangles(SketchDocument doc, FaceRef f)
    {
        if (f.Body >= doc.Bodies.Count || f.Face < 0 || f.Face >= doc.Bodies[f.Body].Faces.Count) return [];
        return doc.TrianglesOf(f).SelectMany(t => new[] { t.A, t.B, t.C })
            .Select(v => new Vector3((float)(v.X * Units.Display), (float)(v.Y * Units.Display), (float)(v.Z * Units.Display))).ToArray();
    }

    /// <summary>
    /// The camera looking the way <paramref name="look"/> does (from the front left, a little above, when null), put back
    /// until the corners of the box round the scene just fit (90 % of the narrower side). Null when there is nothing.
    /// </summary>
    public CameraPose? FitAll(Vec3? look, double tx, double ty)
    {
        if (Picking.Bounds(Picks) is not { } box) return null;
        var (lo, hi) = box;
        var centre = (lo + hi) * 0.5;
        var f = (look ?? new Vec3(1, 1.4, -0.9)).Normalized();
        var r = f.Cross(new Vec3(0, 0, 1)); if (r.Length < 1e-9) r = new Vec3(1, 0, 0); r = r.Normalized();
        var u = r.Cross(f);
        var corners = new[] { lo.X, hi.X }.SelectMany(x => new[] { lo.Y, hi.Y }.SelectMany(y => new[] { lo.Z, hi.Z }.Select(z => new Vec3(x, y, z)))).ToList();
        double far = ViewAngles.FitDistance(corners, centre, f, tx, ty);
        // (up the z axis, unless looking straight down or up)
        return new CameraPose(centre - f * far, f * far, Math.Abs(f.Z) > 0.999 ? u : new Vec3(0, 0, 1));
    }
}
