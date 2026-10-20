using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Geometry;
using RsDocx.Core.Xt;
using RsDocx.Core.Stl;

namespace PushPullMe.Tests;

/// <summary>Which edges are soft (not drawn), as SketchUp softens a mesh: a Revit terrain shows no lines between its
/// facets, but does where its top meets its side.</summary>
public class SoftEdgeTests
{
    /// <summary>Two flat triangles sharing the edge from (0,0,0) to (0,1,0), the second bent up by
    /// <paramref name="degrees"/>; a face without thickness.</summary>
    private static BrepBody Hinge(double degrees)
    {
        double a = degrees * Math.PI / 180;
        var v = new List<Vec3> { new(0, 0, 0), new(0, 1, 0), new(-1, 0.5, 0), new(Math.Cos(a), 0.5, Math.Sin(a)) };
        var edges = new List<BrepEdge> { new(0, 1, new BrepLine()), new(1, 2, new BrepLine()), new(2, 0, new BrepLine()), new(1, 3, new BrepLine()), new(3, 0, new BrepLine()) };
        static Vec3 N(Vec3 p, Vec3 q, Vec3 r) => (q - p).Cross(r - p).Normalized();
        // (both wound so their normals point up: 0 → 2 → 1 and 0 → 1 → 3)
        var left = new BrepFace(new BrepPlane(v[0], N(v[0], v[2], v[1]), new Vec3(0, 1, 0)), false, [[new BrepCoedge(2, false), new BrepCoedge(1, false), new BrepCoedge(0, false)]]);
        var right = new BrepFace(new BrepPlane(v[0], N(v[0], v[1], v[3]), new Vec3(0, 1, 0)), false, [[new BrepCoedge(0, true), new BrepCoedge(3, true), new BrepCoedge(4, true)]]);
        return new BrepBody { Vertices = v, Edges = edges, Faces = [left, right], Sheet = true };
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(29, true)]
    [InlineData(31, false)]
    [InlineData(90, false)]
    public void An_edge_between_two_flat_triangles_is_soft_when_they_bend_less_than_30_degrees(double degrees, bool soft)
    {
        var s = FaceGeometry.SoftEdges(Hinge(degrees));
        Assert.Equal(soft, s[0]);
        // (their outer edges have one face: drawn)
        Assert.All(s.Skip(1), Assert.False);
    }

    [Fact]
    public void The_edges_of_a_block_stay_drawn()
    {
        var block = BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01));
        Assert.All(FaceGeometry.SoftEdges(block), Assert.False);
    }
}
