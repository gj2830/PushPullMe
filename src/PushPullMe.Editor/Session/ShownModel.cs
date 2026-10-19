using System.Numerics;

namespace PushPullMe.Editor.Session;

/// <summary>The triangles of one body in one colour as the view shows them (millimetres): each three positions a
/// triangle, a normal per position, per triangle its face; null colour: the standard one; faded: outside the open group
/// (see-through, not to be picked); the model it is of, for the cuts (see <see cref="EditorSession.Cuts"/>; -1: none).</summary>
public sealed class ShownMesh(Vector3[] positions, Vector3[] normals, int[] faceOfTriangle, int body, Color? color, bool faded, int model = -1)
{
    public int Model { get; } = model;
    public Vector3[] Positions { get; } = positions;
    public Vector3[] Normals { get; } = normals;
    public int Body { get; } = body;
    public Color? Color { get; } = color;
    public bool Faded { get; } = faded;
    public PickMesh Pick { get; } = new(positions, faceOfTriangle, body);
}

/// <summary>The model as the view shows it: its meshes and its edges (pairs of points, millimetres), each edge's model
/// (see <see cref="ShownMesh.Model"/>).</summary>
public sealed class ShownModel(IReadOnlyList<ShownMesh> meshes, Point3DCollection edges, IReadOnlyList<int>? edgeModels = null)
{
    public IReadOnlyList<ShownMesh> Meshes { get; } = meshes;
    public Point3DCollection Edges { get; } = edges;
    public IReadOnlyList<int> EdgeModels { get; } = edgeModels ?? [];
}
