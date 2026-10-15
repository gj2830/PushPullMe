using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;
using Xunit.Abstractions;

namespace PushPullMe.Tests;

public class TriangulatorTests
{
    [Fact]
    public void A_square_with_a_square_hole_is_covered_once()
    {
        var outer = new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var hole = new List<(double, double)> { (3, 3), (3, 7), (7, 7), (7, 3) };
        var pts = outer.Concat(hole).ToList();
        var tris = Triangulator.Triangulate([outer, hole]);
        double area = tris.Sum(t => ((pts[t.B].Item1 - pts[t.A].Item1) * (pts[t.C].Item2 - pts[t.A].Item2) - (pts[t.B].Item2 - pts[t.A].Item2) * (pts[t.C].Item1 - pts[t.A].Item1)) / 2);
        Assert.Equal(100 - 16, area, 9);
        Assert.Equal(8, tris.Count);
    }

    [Fact]
    public void An_L_shape_is_covered_once()
    {
        var l = new List<(double, double)> { (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10) };
        var tris = Triangulator.Triangulate([l]);
        double area = tris.Sum(t => ((l[t.B].Item1 - l[t.A].Item1) * (l[t.C].Item2 - l[t.A].Item2) - (l[t.B].Item2 - l[t.A].Item2) * (l[t.C].Item1 - l[t.A].Item1)) / 2);
        Assert.Equal(64, area, 9);
    }
}

public class PushPullTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;

    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);

    private static int FaceWithNormal(BrepBody b, Vec3 n) => Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b.Faces[f]).Dot(n) > 0.999);

    private static void AssertSound(SketchDocument d)
    {
        foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
    }

    [Fact]
    public void Pulling_the_top_of_a_cube_makes_it_taller()
    {
        var doc = Cube();
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        var (d, r) = doc.PushPull(new FaceRef(0, top), 5 * Mm);
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(8, d.Bodies[0].Vertices.Count);
        Assert.Equal(12, d.Bodies[0].Edges.Count);
        Assert.Empty(r.NewFaces);
        Assert.Equal(4, r.GrownFaces.Count);
        Assert.Equal(1500, d.Volume() / (Mm * Mm * Mm), 6);
    }

    [Fact]
    public void Pushing_the_top_of_a_cube_makes_it_lower()
    {
        var doc = Cube();
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        var (d, _) = doc.PushPull(new FaceRef(0, top), -4 * Mm);
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(8, d.Bodies[0].Vertices.Count);
        Assert.Equal(600, d.Volume() / (Mm * Mm * Mm), 6);
    }

    [Fact]
    public void A_side_pulled_and_pushed_back_gives_the_same_volume()
    {
        var doc = Cube();
        int side = FaceWithNormal(doc.Bodies[0], new Vec3(1, 0, 0));
        var (d1, _) = doc.PushPull(new FaceRef(0, side), 2.5 * Mm);
        var (d2, _) = d1.PushPull(new FaceRef(0, side), -2.5 * Mm);
        AssertSound(d2);
        Assert.Equal(6, d2.Bodies[0].Faces.Count);
        Assert.Equal(1000, d2.Volume() / (Mm * Mm * Mm), 6);
    }

    [Fact]
    public void Pushing_through_the_bottom_is_refused()
    {
        var doc = Cube();
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        Assert.Throws<PushPullException>(() => doc.PushPull(new FaceRef(0, top), -10 * Mm));
        Assert.Throws<PushPullException>(() => doc.PushPull(new FaceRef(0, top), -15 * Mm));
    }

    /// <summary>L profile (x 0..20, z 0..10 with a step down to z 4 from x 8), 10 deep along y.</summary>
    private static SketchDocument LBlock()
    {
        var p = new Polyhedron
        {
            Vertices =
            [
                (0, 0, 0), (20 * Mm, 0, 0), (20 * Mm, 0, 4 * Mm), (8 * Mm, 0, 4 * Mm), (8 * Mm, 0, 10 * Mm), (0, 0, 10 * Mm),
                (0, 10 * Mm, 0), (20 * Mm, 10 * Mm, 0), (20 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 10 * Mm), (0, 10 * Mm, 10 * Mm),
            ],
            Faces =
            [
                [0, 1, 2, 3, 4, 5],        // front -y
                [6, 11, 10, 9, 8, 7],      // back +y
                [0, 6, 7, 1],              // bottom
                [1, 7, 8, 2],              // right +x
                [2, 8, 9, 3],              // low top +z
                [3, 9, 10, 4],             // riser +x
                [4, 10, 11, 5],            // high top +z
                [5, 11, 6, 0],             // left -x
            ],
        };
        return SketchDocument.FromBodies([BrepShapes.FromPolyhedron(p)]);
    }

    [Fact]
    public void The_low_step_of_an_L_is_raised()
    {
        var doc = LBlock();
        double v0 = doc.Volume();
        var b = doc.Bodies[0];
        int low = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).Z > 0.999 && b.Vertices.Any(v => v.Z < 5 * Mm && b.Faces[f].Loops[0].Any(c => b.Edges[c.Edge].Start >= 0 && b.Vertices[b.Edges[c.Edge].Start].Z < 5 * Mm)));
        var (d, r) = doc.PushPull(new FaceRef(0, low), 3 * Mm);
        AssertSound(d);
        Assert.Equal(8, d.Bodies[0].Faces.Count);
        Assert.Equal((v0 + 12 * 10 * 3 * Mm * Mm * Mm) / (Mm * Mm * Mm), d.Volume() / (Mm * Mm * Mm), 6);
        output.WriteLine($"grown {string.Join(",", r.GrownFaces)}");
        // the riser (concave edge) shrank, the front, back and right grew
        Assert.Equal(4, r.GrownFaces.Count);
    }

    /// <summary>Pulled exactly up to the high step: the riser goes and the two tops become one (an L becomes a block).</summary>
    [Fact]
    public void The_low_step_pulled_flush_with_the_high_one_makes_a_block()
    {
        var doc = LBlock();
        var b = doc.Bodies[0];
        int low = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).Z > 0.999 && b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start].Z < 5 * Mm));
        var (d, _) = doc.PushPull(new FaceRef(0, low), 6 * Mm);
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(8, d.Bodies[0].Vertices.Count);
        Assert.Equal(20 * 10 * 10, d.Volume() / (Mm * Mm * Mm), 6);
    }

    [Fact]
    public void A_rod_gets_longer_when_its_end_is_pulled()
    {
        double r = 3 * Mm;
        var rod = BrepShapes.ExtrudeProfile((r, 0), [new ProfileSegment((-r, 0), (0, 0)), new ProfileSegment((r, 0), (0, 0))], 0, 20 * Mm);
        var doc = SketchDocument.FromBodies([rod]);
        int end = FaceWithNormal(rod, new Vec3(1, 0, 0));
        var (d, res) = doc.PushPull(new FaceRef(0, end), 10 * Mm);
        AssertSound(d);
        // (the two halves of the round side become one cylinder face)
        Assert.Equal(3, d.Bodies[0].Faces.Count);
        Assert.Single(d.Bodies[0].Faces, x => x.Surface is BrepCylinder);
        Assert.Empty(res.NewFaces);
        Assert.Equal(30 * Mm, d.Bodies[0].Vertices.Max(v => v.X), 9);
        end = res.FaceMap[end];
        var (d2, _) = d.PushPull(new FaceRef(0, end), -25 * Mm);
        AssertSound(d2);
        Assert.Equal(5 * Mm, d2.Bodies[0].Vertices.Max(v => v.X), 9);
    }

    [Fact]
    public void A_face_with_a_hole_is_pulled_and_the_hole_goes_along()
    {
        var p = new Polyhedron
        {
            Vertices =
            [
                (0, 0, 0), (10 * Mm, 0, 0), (10 * Mm, 10 * Mm, 0), (0, 10 * Mm, 0),
                (0, 0, 5 * Mm), (10 * Mm, 0, 5 * Mm), (10 * Mm, 10 * Mm, 5 * Mm), (0, 10 * Mm, 5 * Mm),
                (3 * Mm, 3 * Mm, 0), (7 * Mm, 3 * Mm, 0), (7 * Mm, 7 * Mm, 0), (3 * Mm, 7 * Mm, 0),
                (3 * Mm, 3 * Mm, 5 * Mm), (7 * Mm, 3 * Mm, 5 * Mm), (7 * Mm, 7 * Mm, 5 * Mm), (3 * Mm, 7 * Mm, 5 * Mm),
            ],
            Faces =
            [
                [0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
                // the hole's walls face inward
                [8, 12, 13, 9], [9, 13, 14, 10], [10, 14, 15, 11], [11, 15, 12, 8],
            ],
            Holes = [[[8, 9, 10, 11]], [[12, 15, 14, 13]], [], [], [], [], [], [], [], []],
        };
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(p)]);
        AssertSound(doc);
        double v0 = doc.Volume() / (Mm * Mm * Mm);
        Assert.Equal(5 * (100 - 16), v0, 6);
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        var (d, r) = doc.PushPull(new FaceRef(0, top), 2 * Mm);
        AssertSound(d);
        Assert.Equal(10, d.Bodies[0].Faces.Count);
        Assert.Equal(7 * (100 - 16), d.Volume() / (Mm * Mm * Mm), 6);
    }

    [Fact]
    public void Crossing_triangles_are_found_and_touching_ones_not()
    {
        var a = (new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        Assert.True(Intersections.Cross(a, (new Vec3(0.2, 0.2, -1), new Vec3(0.2, 0.2, 1), new Vec3(0.3, 0.3, 1))));
        Assert.False(Intersections.Cross(a, (new Vec3(0.2, 0.2, 0), new Vec3(0.2, 0.2, 1), new Vec3(0.3, 0.3, 1))));
        Assert.False(Intersections.Cross(a, (new Vec3(2, 2, -1), new Vec3(2, 2, 1), new Vec3(3, 3, 1))));
    }

    /// <summary>Two blocks on a base: one's inner side pulled into the other is refused.</summary>
    [Fact]
    public void Pulling_into_another_part_is_refused()
    {
        var p = new Polyhedron
        {
            Vertices =
            [
                // U profile in x/z, 10 deep along y: base 0..30 x 0..4, arms 0..8 and 22..30 up to 12
                (0, 0, 0), (30 * Mm, 0, 0), (30 * Mm, 0, 12 * Mm), (22 * Mm, 0, 12 * Mm), (22 * Mm, 0, 4 * Mm), (8 * Mm, 0, 4 * Mm), (8 * Mm, 0, 12 * Mm), (0, 0, 12 * Mm),
                (0, 10 * Mm, 0), (30 * Mm, 10 * Mm, 0), (30 * Mm, 10 * Mm, 12 * Mm), (22 * Mm, 10 * Mm, 12 * Mm), (22 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 12 * Mm), (0, 10 * Mm, 12 * Mm),
            ],
            Faces =
            [
                [0, 1, 2, 3, 4, 5, 6, 7],
                [8, 15, 14, 13, 12, 11, 10, 9],
                [0, 8, 9, 1], [1, 9, 10, 2], [2, 10, 11, 3], [3, 11, 12, 4], [4, 12, 13, 5], [5, 13, 14, 6], [6, 14, 15, 7], [7, 15, 8, 0],
            ],
        };
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(p)]);
        AssertSound(doc);
        // the left arm's inner side (x = 8, facing +x), pulled 20 mm: through the right arm
        var b = doc.Bodies[0];
        int inner = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).X > 0.999 && b.Faces[f].Loops[0].All(c => Math.Abs(b.Vertices[b.Edges[c.Edge].Start].X - 8 * Mm) < 1e-12));
        var (ok, _) = doc.PushPull(new FaceRef(0, inner), 5 * Mm);
        AssertSound(ok);
        Assert.Throws<PushPullException>(() => doc.PushPull(new FaceRef(0, inner), 20 * Mm));
    }
}

/// <summary>Making things flush: a notch filled up to the face next to it.</summary>
public class FlushTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;

    /// <summary>
    /// A base plate (x 0..40, y 0..30, z 0..5) with a block on it (x 10..30, y 10..20, z 5..15) that has a notch in its
    /// front right corner (x 20..30, y 10..14): one body.
    /// </summary>
    private static SketchDocument PlateWithNotchedBlock()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 40 * Mm, 30 * Mm, 5 * Mm))]);
        var b = doc.Bodies[0];
        int top = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).Z > 0.999);
        var (d, r) = doc.DrawRectangle(new FaceRef(0, top), new Vec3(10 * Mm, 10 * Mm, 5 * Mm), new Vec3(30 * Mm, 20 * Mm, 5 * Mm), new Vec3(1, 0, 0));
        (d, var pr) = d.PushPull(r, 10 * Mm);
        // the notch: the block's top cut by a line at x 20, its right part by a line at y 14, the front right part
        // pushed down onto the plate
        int blockTop = pr.FaceMap[r.Face];
        var (d2, left) = d.DrawLine(new FaceRef(0, blockTop), new Vec3(20 * Mm, 20 * Mm, 15 * Mm), new Vec3(20 * Mm, 10 * Mm, 15 * Mm));
        var b2 = d2.Bodies[0];
        // the right part: the one of the two with x > 20
        int right = new[] { blockTop, left.Face }.First(f => b2.Faces[f].Loops[0].All(c => b2.Vertices[b2.Edges[c.Edge].Start].X >= 20 * Mm - 1e-9));
        var (d3, front) = d2.DrawLine(new FaceRef(0, right), new Vec3(20 * Mm, 14 * Mm, 15 * Mm), new Vec3(30 * Mm, 14 * Mm, 15 * Mm));
        var b3 = d3.Bodies[0];
        int corner = new[] { right, front.Face }.First(f => b3.Faces[f].Loops[0].All(c => b3.Vertices[b3.Edges[c.Edge].Start].Y <= 14 * Mm + 1e-9));
        var (d4, _) = d3.PushPull(new FaceRef(0, corner), -10 * Mm);
        return d4;
    }

    [Fact]
    public void A_notch_filled_flush_with_the_face_next_to_it()
    {
        var doc = PlateWithNotchedBlock();
        foreach (var x in doc.Bodies) Assert.Null(XtBrepBuilder.TopologyError(x));
        double v0 = doc.Volume() / (Mm * Mm * Mm);
        output.WriteLine($"faces {doc.Bodies[0].Faces.Count}, volume {v0:0.###}");
        {
            var bd = doc.Bodies[0];
            for (int f = 0; f < bd.Faces.Count; f++)
            {
                var n = FaceGeometry.Normal(bd.Faces[f]);
                double tri = doc.TrianglesOf(new FaceRef(0, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Dot(n) / 2) * 1e6;
                double loops = bd.Faces[f].Loops.Sum(l => FaceGeometry.Area(FaceGeometry.LoopPoints(bd, l), n)) * 1e6;
                output.WriteLine($"  face {f} n {n.X:0},{n.Y:0},{n.Z:0}: loops {bd.Faces[f].Loops.Count} ({string.Join(",", bd.Faces[f].Loops.Select(l => l.Count))}), area tris {tri:0.###} loops {loops:0.###}{(Math.Abs(tri - loops) > 1e-6 ? "  <<<<" : "")}");
                if (Math.Abs(tri - loops) > 1e-6)
                    output.WriteLine("    " + string.Join(" -> ", FaceGeometry.LoopPoints(bd, bd.Faces[f].Loops[0]).Select(p => $"({p.X * 1e3:0.##},{p.Y * 1e3:0.##},{p.Z * 1e3:0.##})")));
            }
        }
        Assert.Equal(40 * 30 * 5 + 20 * 10 * 10 - 10 * 4 * 10, v0, 6);
        var b = doc.Bodies[0];
        // the notch's inner wall: y = 14, facing -y
        int wall = Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b.Faces[f]).Y < -0.999
            && b.Faces[f].Loops[0].All(c => Math.Abs(b.Vertices[b.Edges[c.Edge].Start].Y - 14 * Mm) < 1e-9));
        // (6: past flush, its side faces partly against the block's faces facing the other way: those parts go)
        foreach (double mm in new[] { 2.0, 4.0, 6.0 })
        {
            try
            {
                var (d, r) = doc.PushPull(new FaceRef(0, wall), mm * Mm);
                foreach (var x in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(x));
                output.WriteLine($"+{mm}: faces {d.Bodies[0].Faces.Count}, volume {d.Volume() / (Mm * Mm * Mm):0.###}");
                Assert.Equal(v0 + 10 * mm * 10, d.Volume() / (Mm * Mm * Mm), 6);
            }
            catch (PushPullException e) { output.WriteLine($"+{mm}: {e.Message}"); throw; }
        }
    }
}

/// <summary>Faces next to a rounding: the rounding is cut back (or extended) instead of a side face running through it.</summary>
public class RoundingTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;

    /// <summary>Volume (mm³) of a profile in (y,z) extruded 10 mm, from its height per y: a fine sum.</summary>
    private static double Extruded(Func<double, double> height, double y0, double y1)
    {
        int n = 200000; double s = 0, dy = (y1 - y0) / n;
        for (int i = 0; i < n; i++) s += height(y0 + (i + 0.5) * dy) * dy;
        return s * 10;
    }

    private static void AssertSound(SketchDocument d)
    {
        foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
    }

    private static int FaceWithNormal(BrepBody b, Vec3 n, Func<Vec3, bool>? where = null) =>
        Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b.Faces[f]).Dot(n) > 0.999
            && (where == null || b.Faces[f].Loops[0].All(c => b.Edges[c.Edge].Start < 0 || where(b.Vertices[b.Edges[c.Edge].Start]))));

    /// <summary>
    /// As in the key rack: a post (y 0..5, up to z 20) and an arm (up to z 10) whose top runs down into the post in a
    /// quarter round (radius 5, centre y 10 z 5) that meets the post's side at y 5 z 5. Extruded 10 mm along x.
    /// </summary>
    private static SketchDocument PostAndArm()
    {
        var body = BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((20 * Mm, 0)), new ProfileSegment((20 * Mm, 10 * Mm)), new ProfileSegment((10 * Mm, 10 * Mm)),
            new ProfileSegment((5 * Mm, 5 * Mm), (10 * Mm, 5 * Mm), Ccw: true),
            new ProfileSegment((5 * Mm, 20 * Mm)), new ProfileSegment((0, 20 * Mm)), new ProfileSegment((0, 0)),
        ], 0, 10 * Mm);
        return SketchDocument.FromBodies([body]);
    }

    private static FaceRef RoundingOf(SketchDocument d) =>
        new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).Single(f => d.Bodies[0].Faces[f].Surface is BrepCylinder));

    private static double RadiusOf(SketchDocument d, FaceRef f) => ((BrepCylinder)d.Bodies[0].Faces[f.Face].Surface).Radius;

    private static bool HasVertex(SketchDocument d, double y, double z) =>
        d.Bodies[0].Vertices.Any(v => Math.Abs(v.Y - y * Mm) < 1e-9 && Math.Abs(v.Z - z * Mm) < 1e-9);

    [Fact]
    public void An_outside_rounding_gets_another_radius()
    {
        // the arm's quarter round (r 5) between its top (z 10) and the post's side (y 5); its sharp edge would be at
        // y 5 z 10, its middle m = 5(√2 − 1) from there
        var doc = PostAndArm();
        var f = RoundingOf(doc);
        Assert.True(PushPullMe.Core.PushPull.IsRounding(doc.Bodies[0], f.Face));
        double m = 5 * (Math.Sqrt(2) - 1);
        // pushed in (away from the sharp edge): larger, r 6 → touches the top at y 11 and the side at z 4
        var (big, _) = doc.PushPull(f, -0.2 * m * Mm);
        AssertSound(big);
        Assert.Equal(6 * Mm, RadiusOf(big, f), 9);
        Assert.True(HasVertex(big, 11, 10) && HasVertex(big, 5, 4));
        Assert.Equal(Extruded(y => y < 5 ? 20 : y < 11 ? 4 + Math.Sqrt(36 - (y - 11) * (y - 11)) : 10, 0, 20), big.Volume() / (Mm * Mm * Mm), 1.0);
        big.ToRsDocx();
        // pulled out (towards it): smaller, r 2
        var (small, _) = doc.PushPull(f, 0.6 * m * Mm);
        AssertSound(small);
        Assert.Equal(2 * Mm, RadiusOf(small, f), 9);
        Assert.True(HasVertex(small, 7, 10) && HasVertex(small, 5, 8));
        // the same by radius: the distance for r 6, and the radius after that distance
        Assert.Equal(-0.2 * m * Mm, PushPullMe.Core.PushPull.RoundingDistanceFor(doc.Bodies[0], f.Face, 6 * Mm), 12);
        Assert.Equal(2 * Mm, PushPullMe.Core.PushPull.RoundingRadiusAfter(doc.Bodies[0], f.Face, 0.6 * m * Mm), 12);
        // r 11 would run below the floor: refused
        Assert.Throws<PushPullException>(() => doc.PushPull(f, -1.2 * m * Mm));
        // to nothing: refused
        Assert.Throws<PushPullException>(() => doc.PushPull(f, m * Mm));
    }

    [Fact]
    public void An_inside_rounding_gets_another_radius()
    {
        // an L: floor (z 5, y 5..20) and wall (y 5, z 5..20), the inside corner rounded (r 5, centre y 10 z 10)
        var body = BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((20 * Mm, 0)), new ProfileSegment((20 * Mm, 5 * Mm)), new ProfileSegment((10 * Mm, 5 * Mm)),
            new ProfileSegment((5 * Mm, 10 * Mm), (10 * Mm, 10 * Mm), Ccw: false),
            new ProfileSegment((5 * Mm, 20 * Mm)), new ProfileSegment((0, 20 * Mm)), new ProfileSegment((0, 0)),
        ], 0, 10 * Mm);
        var doc = SketchDocument.FromBodies([body]);
        AssertSound(doc);
        var f = RoundingOf(doc);
        Assert.True(doc.Bodies[0].Faces[f.Face].Reversed);
        double m = 5 * (Math.Sqrt(2) - 1);
        // pulled (into the air, away from the corner): larger, r 6
        var (big, _) = doc.PushPull(f, 0.2 * m * Mm);
        AssertSound(big);
        Assert.Equal(6 * Mm, RadiusOf(big, f), 9);
        Assert.True(HasVertex(big, 11, 5) && HasVertex(big, 5, 11));
        Assert.Equal(Extruded(y => y < 5 ? 20 : y < 11 ? 11 - Math.Sqrt(36 - (y - 11) * (y - 11)) : 5, 0, 20), big.Volume() / (Mm * Mm * Mm), 1.0);
        Assert.Equal(0.2 * m * Mm, PushPullMe.Core.PushPull.RoundingDistanceFor(doc.Bodies[0], f.Face, 6 * Mm), 12);
        // pushed: smaller, r 2
        var (small, _) = doc.PushPull(f, -0.6 * m * Mm);
        AssertSound(small);
        Assert.Equal(2 * Mm, RadiusOf(small, f), 9);
    }

    private static int EdgeBetween(BrepBody b, Func<Vec3, bool> at) =>
        Enumerable.Range(0, b.Edges.Count).Single(e => b.Edges[e].Start >= 0 && b.Edges[e].Curve is BrepLine && at(b.Vertices[b.Edges[e].Start]) && at(b.Vertices[b.Edges[e].End]));

    [Fact]
    public void A_sharp_outside_edge_is_rounded()
    {
        // the cube's top front edge (y 0, z 10) rounded with r 2: 10 × (4 − π) mm³ less
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);
        int e = EdgeBetween(doc.Bodies[0], v => Math.Abs(v.Y) < 1e-12 && Math.Abs(v.Z - 10 * Mm) < 1e-12);
        var (d, f) = doc.RoundEdge(0, e, 2 * Mm);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        Assert.False(d.Bodies[0].Faces[f.Face].Reversed);
        Assert.Equal(1000 - 10 * (4 - Math.PI), d.Volume() / (Mm * Mm * Mm), 1.0);
        d.ToRsDocx();
        // then another radius with ⇕ (by radius): r 3
        var (d2, _) = d.PushPull(f, PushPullMe.Core.PushPull.DistanceForRadius(d.Bodies[0], f.Face, 3 * Mm));
        AssertSound(d2);
        Assert.Equal(1000 - 10 * 9 * (1 - Math.PI / 4), d2.Volume() / (Mm * Mm * Mm), 1.0);
        // a second edge next to it (the top's right edge, r 1): the first is made again with it (a mitre of r 2 and r 1)
        int e2 = EdgeBetween(d.Bodies[0], v => Math.Abs(v.X - 10 * Mm) < 1e-12 && Math.Abs(v.Z - 10 * Mm) < 1e-12);
        var (dd, _) = d.RoundEdge(0, e2, 1 * Mm);
        AssertSound(dd);
        Assert.Equal([1 * Mm, 2 * Mm], dd.Bodies[0].Faces.Where(f => f.Surface is BrepCylinder).Select(f => Math.Round(((BrepCylinder)f.Surface).Radius, 9)).Order().ToList());
        dd.ToRsDocx();
        // larger than the cube: refused
        Assert.Throws<PushPullException>(() => doc.RoundEdge(0, e, 10 * Mm));
    }

    private static SketchDocument Box10() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);

    [Fact]
    public void All_edges_of_a_block_are_rounded_with_ball_corners()
    {
        var doc = Box10();
        var all = Enumerable.Range(0, doc.Bodies[0].Edges.Count).ToList();
        var (d, faces) = doc.RoundEdges(0, all, 2 * Mm);
        AssertSound(d);
        // 6 flat faces, 12 roundings, 8 ball corners
        Assert.Equal(26, d.Bodies[0].Faces.Count);
        Assert.Equal(8, d.Bodies[0].Faces.Count(f => f.Surface is BrepSphere));
        // a rounded block: core, slabs on the faces, quarter cylinders along the edges, eighth balls in the corners
        double r = 2, k = 10 - 2 * r;
        double exact = k * k * k + 2 * r * 3 * k * k + Math.PI * r * r * 3 * k + 4.0 / 3 * Math.PI * r * r * r;
        Assert.Equal(exact, d.Volume() / (Mm * Mm * Mm), 3.0);
        d.ToRsDocx();
    }

    private static double RoundedBlock(double r) { double k = 10 - 2 * r; return k * k * k + 2 * r * 3 * k * k + Math.PI * r * r * 3 * k + 4.0 / 3 * Math.PI * r * r * r; }

    [Fact]
    public void A_rounded_block_gets_another_radius_from_a_rounding_or_a_ball_corner()
    {
        var (d, faces) = Box10().RoundEdges(0, Enumerable.Range(0, 12).ToList(), 2 * Mm);
        var b = d.Bodies[0];
        // taken away again: the cube
        var (sharp, map, edges, radius) = PushPullMe.Core.PushPull.Unround(b, faces[0].Face);
        Assert.Null(XtBrepBuilder.TopologyError(sharp));
        Assert.Equal(6, sharp.Faces.Count);
        Assert.Equal(12, edges.Count);
        Assert.Equal(2 * Mm, radius, 12);
        // one rounding to r 3 (by radius, as ⇕ does): only that one (its corners pieces of torus), the others stay r 2
        var cylinder = faces[0];
        var (d3, r3) = d.PushPull(cylinder, PushPullMe.Core.PushPull.DistanceForRadius(b, cylinder.Face, 3 * Mm));
        AssertSound(d3);
        Assert.Equal(26, d3.Bodies[0].Faces.Count);
        Assert.Equal(3 * Mm, ((BrepCylinder)d3.Bodies[0].Faces[r3.FaceMap[cylinder.Face]].Surface).Radius, 12);
        Assert.Equal(11, d3.Bodies[0].Faces.Count(f => f.Surface is BrepCylinder c && Math.Abs(c.Radius - 2 * Mm) < 1e-9));
        Assert.InRange(d3.Volume() / (Mm * Mm * Mm), RoundedBlock(3), RoundedBlock(2));
        d3.ToRsDocx();
        // a ball corner to r 1
        var ball = new FaceRef(0, Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepSphere));
        var (d1, _) = d.PushPull(ball, PushPullMe.Core.PushPull.DistanceForRadius(b, ball.Face, 1 * Mm));
        AssertSound(d1);
        Assert.Equal(RoundedBlock(1), d1.Volume() / (Mm * Mm * Mm), 3.0);
        // one of them smaller (r 1 with r 2, r 2 in its corners): there the cylinders cut each other (as DesignSpark), the others stay
        var (dsm, rsm) = d.PushPull(cylinder, PushPullMe.Core.PushPull.DistanceForRadius(b, cylinder.Face, 1 * Mm));
        AssertSound(dsm);
        Assert.Equal(1 * Mm, ((BrepCylinder)dsm.Bodies[0].Faces[rsm.FaceMap[cylinder.Face]].Surface).Radius, 12);
        Assert.Equal(0, dsm.Bodies[0].Faces.Count(f => f.Surface is BrepNurbsSurface));
        Assert.Equal(6, dsm.Bodies[0].Faces.Count(f => f.Surface is BrepSphere));
        Assert.InRange(dsm.Volume() / (Mm * Mm * Mm), RoundedBlock(2), 1000);
        dsm.ToRsDocx();
        // too large (9 + 2 > 10): refused
        Assert.Throws<PushPullException>(() => d.PushPull(cylinder, PushPullMe.Core.PushPull.DistanceForRadius(b, cylinder.Face, 9 * Mm)));
    }

    [Fact]
    public void One_upright_edge_of_a_rounded_block_gets_a_larger_radius_with_torus_corners()
    {
        var (d, faces) = Box10().RoundEdges(0, Enumerable.Range(0, 12).ToList(), 2 * Mm);
        var b = d.Bodies[0];
        // the upright rounding at x 10, y 10 to r 4: its two corners become pieces of torus (the others stay balls)
        var upright = faces.Single(f => b.Faces[f.Face].Surface is BrepCylinder c && Math.Abs(Math.Abs(c.Axis.Z) - 1) < 1e-9 && c.Origin.X > 5 * Mm && c.Origin.Y > 5 * Mm);
        var (d4, r4) = d.PushPull(upright, PushPullMe.Core.PushPull.DistanceForRadius(b, upright.Face, 4 * Mm));
        AssertSound(d4);
        var fs = d4.Bodies[0].Faces;
        // (r 4 with r 2: the ring is no larger than the tube, so not a torus but the same piece as a NURBS patch)
        Assert.Equal(2, fs.Count(f => f.Surface is BrepTorus or BrepNurbsSurface));
        Assert.Equal(6, fs.Count(f => f.Surface is BrepSphere));
        Assert.Equal(4 * Mm, ((BrepCylinder)fs[r4.FaceMap[upright.Face]].Surface).Radius, 12);
        // Pappus: the rounded profile (corners 2, 2, 2, 4) extruded, less the top and bottom roundings swept along it
        double r = 2, a0 = (1 - Math.PI / 4) * r * r, c = r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);
        double area = 100 - (1 - Math.PI / 4) * (4 + 4 + 4 + 16), length = 40 - (2 - Math.PI / 2) * (2 + 2 + 2 + 4);
        Assert.Equal(area * 10 - 2 * a0 * (length - 2 * Math.PI * c), d4.Volume() / (Mm * Mm * Mm), 3.0);
        d4.ToRsDocx();
        // taken away again: the cube
        var (sharp, _, _, _) = PushPullMe.Core.PushPull.Unround(d4.Bodies[0], r4.FaceMap[upright.Face]);
        Assert.Equal(6, sharp.Faces.Count);
    }

    [Fact]
    public void An_edge_that_ends_at_roundings_is_rounded_together_with_them()
    {
        // the top edges rounded (r 2), then an upright edge: its top corner held the mitre of two roundings
        var doc = Box10();
        var b0 = doc.Bodies[0];
        bool Top(int e) => Math.Abs(b0.Vertices[b0.Edges[e].Start].Z - 10 * Mm) < 1e-12 && Math.Abs(b0.Vertices[b0.Edges[e].End].Z - 10 * Mm) < 1e-12;
        var top = Enumerable.Range(0, b0.Edges.Count).Where(Top).ToList();
        var (d, _) = doc.RoundEdges(0, top, 2 * Mm);
        var b = d.Bodies[0];
        int upright = Enumerable.Range(0, b.Edges.Count).Single(e => b.Edges[e].Curve is BrepLine && b.Edges[e].Start >= 0
            && Math.Abs(b.Vertices[b.Edges[e].Start].X - 10 * Mm) < 1e-12 && Math.Abs(b.Vertices[b.Edges[e].Start].Y - 10 * Mm) < 1e-12
            && Math.Abs(b.Vertices[b.Edges[e].End].X - 10 * Mm) < 1e-12 && Math.Abs(b.Vertices[b.Edges[e].End].Y - 10 * Mm) < 1e-12);
        // the same radius: a ball corner there
        var (d2, _) = d.RoundEdges(0, [upright], 2 * Mm);
        AssertSound(d2);
        Assert.Equal(1, d2.Bodies[0].Faces.Count(f => f.Surface is BrepSphere));
        Assert.Equal(5, d2.Bodies[0].Faces.Count(f => f.Surface is BrepCylinder));
        d2.ToRsDocx();
        // a larger one: a piece of torus there
        var (d3, _) = d.RoundEdges(0, [upright], 3 * Mm);
        AssertSound(d3);
        Assert.Equal(1, d3.Bodies[0].Faces.Count(f => f.Surface is BrepTorus or BrepNurbsSurface));
        d3.ToRsDocx();
    }

    [Fact]
    public void A_corner_of_three_different_radii_is_where_the_roundings_cut_each_other()
    {
        var doc = Box10();
        var b = doc.Bodies[0];
        int corner = Enumerable.Range(0, b.Vertices.Count).Single(v => b.Vertices[v] is { X: > 9e-3, Y: > 9e-3, Z: > 9e-3 });
        var three = Enumerable.Range(0, b.Edges.Count).Where(e => b.Edges[e].Start == corner || b.Edges[e].End == corner).ToList();
        var (sharp, faces) = PushPullMe.Core.PushPull.RoundEdges(b, three.Select((e, i) => (e, (i + 1) * Mm)).ToList());
        Assert.Null(XtBrepBuilder.TopologyError(sharp));
        // as DesignSpark (test/kubus_bewerkt_bewerkt.rsdocx: r 1, 2, 3): 6 flat faces and the 3 cylinders, no corner piece;
        // each two cut each other along a curve, the three curves meet in one point
        Assert.Equal(9, sharp.Faces.Count);
        Assert.Equal(3, sharp.Edges.Count(e => e.Curve is BrepBSpline));
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") != null)
        {
            foreach (var e in sharp.Edges.Where(e => e.Curve is BrepBSpline))
            {
                var pts = FaceGeometry.EdgePoints(sharp, e);
                output.WriteLine($"curve {sharp.Vertices[e.Start] * 1e3} -> {sharp.Vertices[e.End] * 1e3}, {pts.Count} pts, mid {pts[pts.Count / 2] * 1e3}");
            }
            for (int f = 0; f < sharp.Faces.Count; f++)
            {
                var tris = sharp.Faces[f].Surface is BrepPlane ? null : FaceGeometry.CurvedTriangles(sharp, sharp.Faces[f]);
                if (tris == null) continue;
                double area = tris.Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) * 1e6;
                var cy = (BrepCylinder)sharp.Faces[f].Surface;
                output.WriteLine($"face {f} r{cy.Radius * 1e3:0.#}: {tris.Count} tris, area {area:0.###} mm²");
            }
        }
        var d = SketchDocument.FromBodies([sharp]);
        double v = d.Volume() / (Mm * Mm * Mm);
        // (the cube with each edge rounded on its own, the three together: 971.8 mm³ by sampling)
        Assert.InRange(v, 970.5, 973);
        d.ToRsDocx();
        // and taken away again: the cube
        var (back, _, _, _) = PushPullMe.Core.PushPull.Unround(sharp, faces[0]);
        Assert.Equal(6, back.Faces.Count);
    }

    [Fact]
    public void Mitred_roundings_get_another_radius()
    {
        var doc = Box10();
        var b0 = doc.Bodies[0];
        var top = Enumerable.Range(0, b0.Edges.Count).Where(e => Math.Abs(b0.Vertices[b0.Edges[e].Start].Z - 10 * Mm) < 1e-12 && Math.Abs(b0.Vertices[b0.Edges[e].End].Z - 10 * Mm) < 1e-12).ToList();
        var (d, faces) = doc.RoundEdges(0, top, 2 * Mm);
        // only that one gets r 3 (as in DesignSpark); where it meets the others (r 2): the larger runs on, a free curve between
        var (d3, r3) = d.PushPull(faces[1], PushPullMe.Core.PushPull.DistanceForRadius(d.Bodies[0], faces[1].Face, 3 * Mm));
        AssertSound(d3);
        var cyls = d3.Bodies[0].Faces.Where(f => f.Surface is BrepCylinder).Select(f => ((BrepCylinder)f.Surface).Radius).OrderBy(x => x).ToList();
        Assert.Equal([2 * Mm, 2 * Mm, 2 * Mm, 3 * Mm], cyls.Select(x => Math.Round(x, 9)).ToList());
        Assert.Equal(3 * Mm, ((BrepCylinder)d3.Bodies[0].Faces[r3.FaceMap[faces[1].Face]].Surface).Radius, 12);
        Assert.Equal(2, d3.Bodies[0].Edges.Count(e => e.Curve is BrepBSpline));
        static double Mitred(double r) { double a0 = (1 - Math.PI / 4) * r * r, c = r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4); return 1000 - a0 * (40 - 8 * c); }
        Assert.InRange(d3.Volume() / (Mm * Mm * Mm), Mitred(3), Mitred(2));
        d3.ToRsDocx();
        // and back to r 2: the same as before
        int again = r3.FaceMap[faces[1].Face];
        var (d2, _) = d3.PushPull(new FaceRef(0, again), PushPullMe.Core.PushPull.DistanceForRadius(d3.Bodies[0], again, 2 * Mm));
        Assert.Equal(Mitred(2), d2.Volume() / (Mm * Mm * Mm), 1.0);
        Assert.Equal(0, d2.Bodies[0].Edges.Count(e => e.Curve is BrepBSpline));
    }

    [Fact]
    public void The_top_edges_of_a_block_are_rounded_with_mitres()
    {
        var doc = Box10();
        var b = doc.Bodies[0];
        var top = Enumerable.Range(0, b.Edges.Count).Where(e => Math.Abs(b.Vertices[b.Edges[e].Start].Z - 10 * Mm) < 1e-12 && Math.Abs(b.Vertices[b.Edges[e].End].Z - 10 * Mm) < 1e-12).ToList();
        Assert.Equal(4, top.Count);
        var (d, faces) = doc.RoundEdges(0, top, 2 * Mm);
        AssertSound(d);
        Assert.Equal(10, d.Bodies[0].Faces.Count);
        // Pappus: the corner piece (r² − πr²/4) swept along the path of its centroid (mitred: shorter at each corner)
        double r = 2, a0 = (1 - Math.PI / 4) * r * r, c = r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);
        Assert.Equal(1000 - a0 * (40 - 8 * c), d.Volume() / (Mm * Mm * Mm), 1.0);
        d.ToRsDocx();
    }

    [Fact]
    public void The_three_edges_of_one_corner_are_rounded_with_a_ball()
    {
        var doc = Box10();
        var b = doc.Bodies[0];
        int corner = Enumerable.Range(0, b.Vertices.Count).Single(v => b.Vertices[v] is { X: > 9e-3, Y: > 9e-3, Z: > 9e-3 });
        var three = Enumerable.Range(0, b.Edges.Count).Where(e => b.Edges[e].Start == corner || b.Edges[e].End == corner).ToList();
        var (d, _) = doc.RoundEdges(0, three, 2 * Mm);
        AssertSound(d);
        Assert.Equal(10, d.Bodies[0].Faces.Count);
        double v = d.Volume() / (Mm * Mm * Mm), a0 = (1 - Math.PI / 4) * 4;
        Assert.InRange(v, 1000 - 3 * a0 * 10, 1000 - 3 * a0 * 8);
        d.ToRsDocx();
    }

    [Fact]
    public void A_sharp_inside_edge_is_rounded()
    {
        // an L: floor (z 5) and wall (y 5) meet in a sharp inside corner; r 2 fills 10 × (4 − π) mm³
        var body = BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((20 * Mm, 0)), new ProfileSegment((20 * Mm, 5 * Mm)), new ProfileSegment((5 * Mm, 5 * Mm)),
            new ProfileSegment((5 * Mm, 20 * Mm)), new ProfileSegment((0, 20 * Mm)), new ProfileSegment((0, 0)),
        ], 0, 10 * Mm);
        var doc = SketchDocument.FromBodies([body]);
        int e = EdgeBetween(doc.Bodies[0], v => Math.Abs(v.Y - 5 * Mm) < 1e-12 && Math.Abs(v.Z - 5 * Mm) < 1e-12);
        double before = doc.Volume() / (Mm * Mm * Mm);
        var (d, f) = doc.RoundEdge(0, e, 2 * Mm);
        AssertSound(d);
        Assert.True(d.Bodies[0].Faces[f.Face].Reversed);
        Assert.Equal(before + 10 * (4 - Math.PI), d.Volume() / (Mm * Mm * Mm), 1.0);
        d.ToRsDocx();
    }

    [Fact]
    public void The_side_of_a_post_is_pulled_into_the_rounding_next_to_it()
    {
        var doc = PostAndArm();
        AssertSound(doc);
        double h0(double y) => y < 5 ? 20 : y < 10 ? 5 + Math.Sqrt(25 - (y - 10) * (y - 10)) : 10;
        Assert.Equal(Extruded(h0, 0, 20), doc.Volume() / (Mm * Mm * Mm), 1.0);
        // the post's side facing the arm (y = 5, above z 5)
        int side = FaceWithNormal(doc.Bodies[0], new Vec3(0, 1, 0), p => Math.Abs(p.Y - 5 * Mm) < 1e-9);
        var (d, r) = doc.PushPull(new FaceRef(0, side), 2 * Mm);
        AssertSound(d);
        output.WriteLine($"faces {d.Bodies[0].Faces.Count}, new {r.NewFaces.Count}, grown {string.Join(",", r.GrownFaces)}, trimmed {string.Join(",", r.TrimmedFaces)}");
        Assert.Single(r.TrimmedFaces);
        Assert.Empty(r.NewFaces);
        // the side now runs down to where it meets the rounding: y 7, z 5 + 4 = 9
        var moved = d.Bodies[0].Faces[r.FaceMap[side]];
        double low = moved.Loops[0].Select(c => d.Bodies[0].Edges[c.Edge]).Min(e => d.Bodies[0].Vertices[e.Start].Z);
        Assert.Equal(9 * Mm, low, 9);
        double h1(double y) => y < 7 ? 20 : h0(y);
        Assert.Equal(Extruded(h1, 0, 20), d.Volume() / (Mm * Mm * Mm), 1.0);
        Assert.Equal(d.Bodies[0].Faces.Count, doc.Bodies[0].Faces.Count);

        // and back: the rounding as it was
        var (d2, r2) = d.PushPull(new FaceRef(0, r.FaceMap[side]), -2 * Mm);
        AssertSound(d2);
        Assert.Single(r2.TrimmedFaces);
        Assert.Equal(doc.Bodies[0].Faces.Count, d2.Bodies[0].Faces.Count);
        Assert.Equal(doc.Bodies[0].Edges.Count, d2.Bodies[0].Edges.Count);
        Assert.Equal(doc.Volume(), d2.Volume(), 1e-15);
    }

    /// <summary>A block (y 0..20, z 0..10) with its top right edge rounded (radius 5).</summary>
    private static SketchDocument RoundedBlock() => SketchDocument.FromBodies([BrepShapes.ExtrudeProfile((0, 0),
    [
        new ProfileSegment((20 * Mm, 0)), new ProfileSegment((20 * Mm, 5 * Mm)),
        new ProfileSegment((15 * Mm, 10 * Mm), (15 * Mm, 5 * Mm), Ccw: true),
        new ProfileSegment((0, 10 * Mm)), new ProfileSegment((0, 0)),
    ], 0, 10 * Mm)]);

    /// <summary>The block with its rounded edge (radius 5) when its side is at y = <paramref name="w"/>: height per y.</summary>
    private static Func<double, double> RoundedProfile(double w) => y => y < w - 5 ? 10 : 5 + Math.Sqrt(Math.Max(0, 25 - (y - (w - 5)) * (y - (w - 5))));

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void The_side_below_a_rounded_edge_takes_the_rounding_along_and_the_top_grows_or_shrinks(double mm)
    {
        var doc = RoundedBlock();
        AssertSound(doc);
        int side = FaceWithNormal(doc.Bodies[0], new Vec3(0, 1, 0));
        var (d, r) = doc.PushPull(new FaceRef(0, side), mm * Mm);
        AssertSound(d);
        Assert.Single(r.MovedAlong);
        Assert.Empty(r.TrimmedFaces);
        Assert.Empty(r.NewFaces);
        Assert.Equal(doc.Bodies[0].Faces.Count, d.Bodies[0].Faces.Count);
        Assert.Equal(Extruded(RoundedProfile(20 + mm), 0, 20 + mm), d.Volume() / (Mm * Mm * Mm), 1.0);
        // the rounding keeps its radius and moved over mm
        var cyl = (BrepCylinder)d.Bodies[0].Faces[r.MovedAlong[0]].Surface;
        Assert.Equal(5 * Mm, cyl.Radius, 9);
        Assert.Equal((15 + mm) * Mm, cyl.Origin.Y, 9);
        // and back: as it was
        var (d2, _) = d.PushPull(new FaceRef(0, r.FaceMap[side]), -mm * Mm);
        AssertSound(d2);
        Assert.Equal(doc.Volume(), d2.Volume(), 1e-15);
        Assert.Equal(doc.Bodies[0].Faces.Count, d2.Bodies[0].Faces.Count);
    }

    /// <summary>
    /// A plate (y 0..10, z 0..4) with a half-round end (one cylinder face, radius 2, centre y 10 z 2): the top pulled
    /// up 3 takes only the upper quarter along (the half-round is cut where it faces +y), and a flat strip grows between.
    /// </summary>
    [Fact]
    public void A_half_round_end_is_cut_and_only_its_upper_quarter_goes_along_with_the_top()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((10 * Mm, 0)),
            new ProfileSegment((10 * Mm, 4 * Mm), (10 * Mm, 2 * Mm), Ccw: true),
            new ProfileSegment((0, 4 * Mm)), new ProfileSegment((0, 0)),
        ], 0, 10 * Mm)]);
        AssertSound(doc);
        Assert.Single(doc.Bodies[0].Faces, f => f.Surface is BrepCylinder);
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        var (d, r) = doc.PushPull(new FaceRef(0, top), 3 * Mm);
        AssertSound(d);
        Assert.Single(r.MovedAlong);
        Assert.Equal(2, d.Bodies[0].Faces.Count(f => f.Surface is BrepCylinder));
        double area = 10 * 7 + Math.PI * 4 / 2 + 2 * 3;
        Assert.Equal(area * 10, d.Volume() / (Mm * Mm * Mm), 1.0);
        var (d2, _) = d.PushPull(new FaceRef(0, r.FaceMap[top]), -3 * Mm);
        AssertSound(d2);
        Assert.Equal(doc.Volume(), d2.Volume(), 1e-14);
        Assert.Equal(doc.Bodies[0].Faces.Count, d2.Bodies[0].Faces.Count);
    }

    /// <summary>A slab (y 0..10, z 0..20) with both top edges rounded (radius 3): the top pulled up takes both along.</summary>
    [Fact]
    public void The_top_of_a_slab_with_two_rounded_edges_takes_both_along()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((10 * Mm, 0)), new ProfileSegment((10 * Mm, 17 * Mm)),
            new ProfileSegment((7 * Mm, 20 * Mm), (7 * Mm, 17 * Mm), Ccw: true),
            new ProfileSegment((3 * Mm, 20 * Mm)),
            new ProfileSegment((0, 17 * Mm), (3 * Mm, 17 * Mm), Ccw: true),
            new ProfileSegment((0, 0)),
        ], 0, 10 * Mm)]);
        AssertSound(doc);
        int top = FaceWithNormal(doc.Bodies[0], new Vec3(0, 0, 1));
        double v0 = doc.Volume() / (Mm * Mm * Mm);
        var (d, r) = doc.PushPull(new FaceRef(0, top), 4 * Mm);
        AssertSound(d);
        Assert.Equal(2, r.MovedAlong.Count);
        Assert.Equal(v0 + 10 * 4 * 10, d.Volume() / (Mm * Mm * Mm), 1e-6);
    }
}

/// <summary>Drawing on a face: a rectangle or a line splits it; Push/Pull then makes a block, a pocket or a step.</summary>
public class DrawingTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;
    private static readonly Vec3 Up = new(0, 0, 1), XDir = new(1, 0, 0);

    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);

    private static FaceRef Top(SketchDocument d) => new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).First(f => FaceGeometry.Normal(d.Bodies[0].Faces[f]).Dot(Up) > 0.999));

    private static void AssertSound(SketchDocument d)
    {
        foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
    }

    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);

    [Fact]
    public void A_rectangle_on_the_top_becomes_a_face_and_is_pulled_into_a_block()
    {
        var doc = Cube();
        var top = Top(doc);
        var (d, rect) = doc.DrawRectangle(top, new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(6 * Mm, 8 * Mm, 10 * Mm), XDir);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        Assert.Equal(1000, Mm3(d), 6);
        var (d2, r) = d.PushPull(rect, 3 * Mm);
        AssertSound(d2);
        Assert.Equal(4, r.NewFaces.Count);
        Assert.Equal(1000 + 4 * 5 * 3, Mm3(d2), 6);
        d2.ToRsDocx();
    }

    [Fact]
    public void A_rectangle_pushed_in_makes_a_pocket_and_pushed_back_it_is_gone()
    {
        var doc = Cube();
        var (d, rect) = doc.DrawRectangle(Top(doc), new Vec3(6 * Mm, 8 * Mm, 10 * Mm), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), XDir);
        var (d2, r) = d.PushPull(rect, -4 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 - 4 * 5 * 4, Mm3(d2), 6);
        var (d3, _) = d2.PushPull(new FaceRef(0, r.FaceMap[rect.Face]), 4 * Mm);
        AssertSound(d3);
        Assert.Equal(1000, Mm3(d3), 6);
        // all the way through: a hole
        Assert.True(d.PushPull(rect, -10 * Mm).Result.Punched);
    }

    [Fact]
    public void A_rectangle_that_does_not_fit_is_refused()
    {
        var doc = Cube();
        Assert.Throws<PushPullException>(() => doc.DrawRectangle(Top(doc), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(12 * Mm, 8 * Mm, 10 * Mm), XDir));
        Assert.Throws<PushPullException>(() => doc.DrawRectangle(Top(doc), new Vec3(0, 3 * Mm, 10 * Mm), new Vec3(5 * Mm, 8 * Mm, 10 * Mm), XDir));
        Assert.Throws<PushPullException>(() => doc.DrawRectangle(Top(doc), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(2 * Mm, 8 * Mm, 10 * Mm), XDir));
    }

    [Fact]
    public void A_line_across_the_top_splits_it_and_one_half_is_pushed_down_into_a_step()
    {
        var doc = Cube();
        var top = Top(doc);
        var (d, half) = doc.DrawLine(top, new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(4 * Mm, 10 * Mm, 10 * Mm));
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        // which half is new: the one left of the line, seen from above (x < 4)
        var hb = d.Bodies[0]; var hf = hb.Faces[half.Face];
        Assert.True(hf.Loops[0].All(c => hb.Vertices[hb.Edges[c.Edge].Start].X <= 4 * Mm + 1e-12));
        var (d2, r) = d.PushPull(half, -3 * Mm);
        AssertSound(d2);
        output.WriteLine($"faces {d2.Bodies[0].Faces.Count}, new {r.NewFaces.Count}, grown {r.GrownFaces.Count}");
        Assert.Equal(1000 - 4 * 10 * 3, Mm3(d2), 6);
        // the other half pulled up: a step the other way
        var (d3, _) = d.PushPull(top, 2 * Mm);
        AssertSound(d3);
        Assert.Equal(1000 + 6 * 10 * 2, Mm3(d3), 6);
    }

    [Fact]
    public void A_line_corner_to_corner_makes_two_triangles()
    {
        var doc = Cube();
        var (d, tri) = doc.DrawLine(Top(doc), new Vec3(0, 0, 10 * Mm), new Vec3(10 * Mm, 10 * Mm, 10 * Mm));
        AssertSound(d);
        Assert.Equal(3, d.Bodies[0].Faces[tri.Face].Loops[0].Count);
        var (d2, _) = d.PushPull(tri, 5 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 + 50 * 5, Mm3(d2), 6);
    }

    [Fact]
    public void A_path_with_a_bend_divides_the_top_and_the_corner_is_pushed_down()
    {
        // from the front edge (x 6) back to y 4, then right to the right edge: a corner of 4 × 4 cut off
        var doc = Cube();
        var (d, part) = doc.DrawPath(Top(doc), [new Vec3(6 * Mm, 0, 10 * Mm), new Vec3(6 * Mm, 4 * Mm, 10 * Mm), new Vec3(10 * Mm, 4 * Mm, 10 * Mm)]);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        var b = d.Bodies[0];
        // the corner (x 6..10, y 0..4): one of the two parts
        int corner = new[] { Top(doc).Face, part.Face }.Single(f => b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start] is { X: >= 6e-3 - 1e-12, Y: <= 4e-3 + 1e-12 }));
        var (d2, _) = d.PushPull(new FaceRef(0, corner), -3 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 - 4 * 4 * 3, Mm3(d2), 6);
    }

    [Fact]
    public void A_closed_path_is_a_face_of_its_own()
    {
        var doc = Cube();
        var tri = new[] { new Vec3(2 * Mm, 2 * Mm, 10 * Mm), new Vec3(8 * Mm, 2 * Mm, 10 * Mm), new Vec3(5 * Mm, 7 * Mm, 10 * Mm), new Vec3(2 * Mm, 2 * Mm, 10 * Mm) };
        var (d, t) = doc.DrawPath(Top(doc), tri);
        AssertSound(d);
        Assert.Equal(3, d.Bodies[0].Faces[t.Face].Loops[0].Count);
        var (d2, _) = d.PushPull(t, 2 * Mm);
        Assert.Equal(1000 + 6 * 5 / 2.0 * 2, Mm3(d2), 6);
    }

    [Fact]
    public void A_path_starting_or_ending_along_an_edge_starts_where_it_leaves_it()
    {
        // from the corner (0, 10) down the left edge to y 6, right to x 4, down to the front edge, and along it to x 0
        // (the user's log: the first piece along the edge was refused)
        var doc = Cube();
        var (d, part) = doc.DrawPath(Top(doc), [new Vec3(0, 10 * Mm, 10 * Mm), new Vec3(0, 6 * Mm, 10 * Mm), new Vec3(4 * Mm, 6 * Mm, 10 * Mm),
            new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(0, 0, 10 * Mm)]);
        AssertSound(d);
        var b = d.Bodies[0];
        // the corner (x 0..4, y 0..6): one of the two parts
        int corner = new[] { Top(doc).Face, part.Face }.Single(f => b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start] is { X: <= 4e-3 + 1e-12, Y: <= 6e-3 + 1e-12 }));
        var (d2, _) = d.PushPull(new FaceRef(0, corner), -2 * Mm);
        Assert.Equal(1000 - 4 * 6 * 2, Mm3(d2), 6);
    }

    private static SketchDocument CubeHalved(out FaceRef top)
    {
        // the cube's top divided at x 5: two faces in one plane
        var doc = Cube();
        var (d, _) = doc.DrawPath(Top(doc), [new Vec3(5 * Mm, 0, 10 * Mm), new Vec3(5 * Mm, 10 * Mm, 10 * Mm)]);
        top = Top(d);
        return d;
    }

    private static int TopFaces(SketchDocument d) => d.Bodies[0].Faces.Count(f => FaceGeometry.Normal(f).Dot(Up) > 0.999);

    [Fact]
    public void A_line_across_two_faces_divides_both()
    {
        var doc = CubeHalved(out var top);
        Assert.Equal(2, TopFaces(doc));
        var (d, _) = doc.DrawPath(top, [new Vec3(0, 3 * Mm, 10 * Mm), new Vec3(10 * Mm, 3 * Mm, 10 * Mm)]);
        AssertSound(d);
        Assert.Equal(4, TopFaces(d));
        // a strip of 10 × 3 along the front: push both its parts down 1 mm
        var b = d.Bodies[0];
        var front = Enumerable.Range(0, b.Faces.Count).Where(f => FaceGeometry.Normal(b.Faces[f]).Dot(Up) > 0.999
            && b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start].Y <= 3e-3 + 1e-12)).ToList();
        Assert.Equal(2, front.Count);
        var (d2, _) = d.PushPull(new FaceRef(0, front[0]), -1 * Mm);
        Assert.Equal(1000 - 5 * 3, Mm3(d2), 6);
    }

    [Fact]
    public void A_line_from_a_shared_edge_into_the_other_face_divides_that_one()
    {
        // the user's case: started on the edge two faces share, then into the face that was not under the first click
        var doc = CubeHalved(out var top);
        var b0 = doc.Bodies[0];
        int left = Enumerable.Range(0, b0.Faces.Count).Single(f => FaceGeometry.Normal(b0.Faces[f]).Dot(Up) > 0.999
            && b0.Faces[f].Loops[0].All(c => b0.Vertices[b0.Edges[c.Edge].Start].X <= 5e-3 + 1e-12));
        var (d, _) = doc.DrawPath(new FaceRef(0, left), [new Vec3(5 * Mm, 4 * Mm, 10 * Mm), new Vec3(8 * Mm, 4 * Mm, 10 * Mm), new Vec3(8 * Mm, 0, 10 * Mm)]);
        AssertSound(d);
        Assert.Equal(3, TopFaces(d));
    }

    [Fact]
    public void A_closed_shape_over_a_shared_edge_divides_both_faces()
    {
        var doc = CubeHalved(out var top);
        var sq = new[] { (3, 3), (7, 3), (7, 7), (3, 7), (3, 3) }.Select(p => new Vec3(p.Item1 * Mm, p.Item2 * Mm, 10 * Mm)).ToList();
        var (d, _) = doc.DrawPath(top, sq);
        AssertSound(d);
        Assert.Equal(4, TopFaces(d));
        // pull both halves of the square up 2 mm
        var b = d.Bodies[0];
        var halves = Enumerable.Range(0, b.Faces.Count).Where(f => FaceGeometry.Normal(b.Faces[f]).Dot(Up) > 0.999
            && b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start] is { X: >= 3e-3 - 1e-12 and <= 7e-3 + 1e-12, Y: >= 3e-3 - 1e-12 and <= 7e-3 + 1e-12 })).ToList();
        Assert.Equal(2, halves.Count);
        var (d2, _) = d.PushPull(new FaceRef(0, halves[0]), 2 * Mm);
        Assert.Equal(1000 + 2 * 4 * 2, Mm3(d2), 6);
    }

    [Fact]
    public void Offset_of_the_top_makes_a_rim_and_an_inner_face()
    {
        var doc = Cube();
        var (d, inner) = doc.Offset(Top(doc), 2 * Mm);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        var (d2, _) = d.PushPull(inner, -3 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 - 6 * 6 * 3, Mm3(d2), 6);
        // as far in as fits, and not further
        Assert.Throws<PushPullException>(() => doc.Offset(Top(doc), 5 * Mm));
        Assert.Throws<PushPullException>(() => doc.Offset(Top(doc), -1 * Mm));
        doc.Offset(Top(doc), 4.9 * Mm);
    }

    [Fact]
    public void Offset_of_an_L_shape_follows_its_inner_corner()
    {
        // the top with a corner of 4 × 4 cut off at the front right: an L of 84 mm²; 1 mm in: 8 × 8 less 4 × 4
        var doc = Cube();
        var (d, part) = doc.DrawPath(Top(doc), [new Vec3(6 * Mm, 0, 10 * Mm), new Vec3(6 * Mm, 4 * Mm, 10 * Mm), new Vec3(10 * Mm, 4 * Mm, 10 * Mm)]);
        var b = d.Bodies[0];
        int l = new[] { Top(doc).Face, part.Face }.Single(f => b.Faces[f].Loops[0].Count > 4);
        var (d2, inner) = d.Offset(new FaceRef(0, l), 1 * Mm);
        AssertSound(d2);
        var (d3, _) = d2.PushPull(inner, -1 * Mm);
        Assert.Equal(1000 - 48, Mm3(d3), 6);
    }

    [Fact]
    public void Offset_of_a_circle_is_a_smaller_circle_and_a_hole_moves_into_the_inner_face()
    {
        var doc = Cube();
        var (d, disk) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 3 * Mm);
        var (d2, small) = d.Offset(disk, 1 * Mm);
        AssertSound(d2);
        Assert.Equal(2 * Mm, ((BrepCircle)d2.Bodies[0].Edges[d2.Bodies[0].Faces[small.Face].Loops[0][0].Edge].Curve).Radius, 12);
        var (d3, _) = d2.PushPull(small, 2 * Mm);
        Assert.Equal(1000 + ShownCircleArea(2) * 2, Mm3(d3), 3);

        // the top with a circle (r 1) in its middle: offset 2 → the circle's hole belongs to the inner face
        var (e, _) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 1 * Mm);
        var (e2, inner) = e.Offset(Top(e), 2 * Mm);
        AssertSound(e2);
        Assert.Equal(2, e2.Bodies[0].Faces[inner.Face].Loops.Count);
        Assert.Equal(2, e2.Bodies[0].Faces[Top(e).Face].Loops.Count);
        var (e3, _) = e2.PushPull(inner, -2 * Mm);
        AssertSound(e3);
        // (the circle stays: a post of r 1 standing in the pocket)
        Assert.Equal(1000 - (36 - ShownCircleArea(1)) * 2, Mm3(e3), 3);
    }

    [Fact]
    public void An_arc_rounds_a_corner_off_or_makes_a_face_of_its_own()
    {
        // from the front edge (6, 0) to the right edge (10, 4), round (6, 4) (r 4): the corner cut off by a quarter circle
        var doc = Cube();
        var mid = new Vec3((6 + 4 * Math.Cos(-Math.PI / 4)) * Mm, (4 + 4 * Math.Sin(-Math.PI / 4)) * Mm, 10 * Mm);
        var (d, f) = doc.DrawArc(Top(doc), new Vec3(6 * Mm, 0, 10 * Mm), new Vec3(10 * Mm, 4 * Mm, 10 * Mm), mid);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        var arc = d.Bodies[0].Edges.Single(e => e.Curve is BrepCircle);
        Assert.Equal(4 * Mm, ((BrepCircle)arc.Curve).Radius, 12);
        // the corner piece (between the arc and the corner) pushed through the whole cube: a rounded upright corner
        var b = d.Bodies[0];
        int corner = new[] { Top(doc).Face, f.Face }.Single(g => b.Faces[g].Loops[0].Count == 3);
        var (d2, _) = d.PushPull(new FaceRef(0, corner), -10 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 - 10 * (16 - Math.PI * 16 / 4), Mm3(d2), 0.5); // (the arc shown in straight pieces)
        Assert.Contains(d2.Bodies[0].Faces, g => g.Surface is BrepCylinder);
        d2.ToRsDocx();

        // wholly inside: a half circle (3, 5) → (7, 5) by way of (5, 7), closed with its chord, pulled up 2
        var (d3, half) = doc.DrawArc(Top(doc), new Vec3(3 * Mm, 5 * Mm, 10 * Mm), new Vec3(7 * Mm, 5 * Mm, 10 * Mm), new Vec3(5 * Mm, 7 * Mm, 10 * Mm));
        AssertSound(d3);
        var (d4, _) = d3.PushPull(half, 2 * Mm);
        AssertSound(d4);
        Assert.Equal(1000 + Math.PI * 4 / 2 * 2, Mm3(d4), 0.1);
        d4.ToRsDocx();
        // half on the edge: refused; on one line: refused
        Assert.Throws<PushPullException>(() => doc.DrawArc(Top(doc), new Vec3(5 * Mm, 0, 10 * Mm), new Vec3(5 * Mm, 4 * Mm, 10 * Mm), new Vec3(6 * Mm, 2 * Mm, 10 * Mm)));
        Assert.Throws<PushPullException>(() => doc.DrawArc(Top(doc), new Vec3(3 * Mm, 5 * Mm, 10 * Mm), new Vec3(7 * Mm, 5 * Mm, 10 * Mm), new Vec3(5 * Mm, 5 * Mm, 10 * Mm)));
    }

    [Fact]
    public void A_three_point_arc_goes_through_its_middle_point()
    {
        // (as the 3 point arc tool): from 0° to 270° on a circle r 3 round (5, 5) by way of 200° (not the middle): the long
        // way round, closed by its chord; pulled up 2
        var doc = Cube();
        var c = new Vec3(5 * Mm, 5 * Mm, 10 * Mm);
        Vec3 At(double deg) => c + new Vec3(Math.Cos(deg * Math.PI / 180), Math.Sin(deg * Math.PI / 180), 0) * (3 * Mm);
        var (d, f) = doc.DrawArc(Top(doc), At(0), At(270), At(200));
        AssertSound(d);
        var arc = (BrepCircle)d.Bodies[0].Edges.Single(e => e.Curve is BrepCircle).Curve;
        Assert.Equal(3 * Mm, arc.Radius, 12);
        Assert.True((arc.Centre - c).Length < 1e-12);
        var (d2, _) = d.PushPull(f, 2 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 + (9 * Math.PI * 3 / 4 + 4.5) * 2, Mm3(d2), 0.1);
        d2.ToRsDocx();
        // from edge to edge, bulging away from the corner it cuts off
        var (d5, _) = doc.DrawArc(Top(doc), new Vec3(0, 4 * Mm, 10 * Mm), new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(2.7 * Mm, 2.7 * Mm, 10 * Mm));
        AssertSound(d5);
    }

    [Fact]
    public void A_pie_is_drawn_and_pulled_up()
    {
        // a quarter (and three quarters) of a circle r 3 round (5, 5), pulled up 2
        var doc = Cube();
        var c = new Vec3(5 * Mm, 5 * Mm, 10 * Mm);
        Vec3 At(double deg) => c + new Vec3(Math.Cos(deg * Math.PI / 180), Math.Sin(deg * Math.PI / 180), 0) * (3 * Mm);
        var (d, f) = doc.DrawPie(Top(doc), c, At(0), At(90), At(45));
        AssertSound(d);
        Assert.Equal(3, d.Bodies[0].Faces[f.Face].Loops[0].Count);
        var (d2, _) = d.PushPull(f, 2 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 + Math.PI * 9 / 4 * 2, Mm3(d2), 0.05);
        d2.ToRsDocx();
        var (d3, f3) = doc.DrawPie(Top(doc), c, At(0), At(270), At(135));
        var (d4, _) = d3.PushPull(f3, 2 * Mm);
        AssertSound(d4);
        Assert.Equal(1000 + Math.PI * 9 * 3 / 4 * 2, Mm3(d4), 0.1);
        // too large for the face: refused
        Assert.Throws<PushPullException>(() => doc.DrawPie(Top(doc), c, c + new Vec3(6 * Mm, 0, 0), c + new Vec3(0, 6 * Mm, 0), c + new Vec3(6 * Mm, 6 * Mm, 0) * (1 / Math.Sqrt(2))));
    }

    [Fact]
    public void A_face_with_an_arc_in_its_edge_and_holes_with_arcs_is_shown_whole()
    {
        // the simulation of 14 Oct 2026: a pie, a corner rounded off with an arc, a rectangle at an angle, all on the top
        var doc = Cube();
        Vec3 P(double x, double y) => new(x * Mm, y * Mm, 10 * Mm);
        var (d1, _) = doc.DrawPie(Top(doc), P(3, 3), P(5, 3), P(3, 5), P(3 + 2 * Math.Cos(Math.PI / 4), 3 + 2 * Math.Sin(Math.PI / 4)));
        var (d2, _) = d1.DrawArc(Top(d1), P(8, 0), P(10, 2), P(8 + 2 * Math.Cos(-Math.PI / 4), 2 + 2 * Math.Sin(-Math.PI / 4)));
        double Shown(SketchDocument d, Func<BrepFace, bool> which)
        {
            var bb = d.Bodies[0];
            int f = Enumerable.Range(0, bb.Faces.Count).Single(g => bb.Faces[g].Surface is BrepPlane && FaceGeometry.Normal(bb.Faces[g]).Z > 0.999 && which(bb.Faces[g]));
            return d.TrianglesOf(new FaceRef(0, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) / (Mm * Mm);
        }
        output.WriteLine($"pie only: top {Shown(d1, f => f.Loops.Count == 2):0.###} (exact {100 - Math.PI:0.###}), pie {Shown(d1, f => f.Loops.Count == 1):0.###} (exact {Math.PI:0.###})");
        var (dA, _) = doc.DrawArc(Top(doc), P(8, 0), P(10, 2), P(8 + 2 * Math.Cos(-Math.PI / 4), 2 + 2 * Math.Sin(-Math.PI / 4)));
        output.WriteLine($"arc only: parts {string.Join(", ", Enumerable.Range(0, dA.Bodies[0].Faces.Count).Where(g => dA.Bodies[0].Faces[g].Surface is BrepPlane && FaceGeometry.Normal(dA.Bodies[0].Faces[g]).Z > 0.999).Select(g => dA.TrianglesOf(new FaceRef(0, g)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) / (Mm * Mm)).Select(v => v.ToString("0.###")))} (exact {100 - (4 - Math.PI):0.###} and {4 - Math.PI:0.###})");
        output.WriteLine($"both: big {Shown(d2, f => f.Loops.Count == 2):0.###} (exact {100 - 4:0.###})");
        var (pr, _) = d1.DrawPath(Top(d1), [P(2, 6), P(4, 8), P(3, 9), P(1, 7), P(2, 6)]);
        output.WriteLine($"pie + rectangle: top {Shown(pr, f => f.Loops.Count == 3):0.###} (exact {100 - Math.PI - 4:0.###})");
        var (ar, _) = dA.DrawPath(Top(dA), [P(2, 6), P(4, 8), P(3, 9), P(1, 7), P(2, 6)]);
        output.WriteLine($"arc + rectangle: top {Shown(ar, f => f.Loops.Count == 2):0.###} (exact {100 - (4 - Math.PI) - 4:0.###})");
        var (rr, _) = doc.DrawPath(Top(doc), [P(2, 6), P(4, 8), P(3, 9), P(1, 7), P(2, 6)]);
        var (rr2, _) = rr.DrawPath(Top(rr), [P(6, 6), P(8, 6), P(8, 8), P(6, 8), P(6, 6)]);
        output.WriteLine($"two rectangles: top {Shown(rr2, f => f.Loops.Count == 3):0.###} (exact {100 - 8:0.###})");
        var b = d2.Bodies[0];
        // the large part of the top: the face whose outer loop has the arc, with the pie as a hole
        int big = Enumerable.Range(0, b.Faces.Count).Single(f => b.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b.Faces[f]).Z > 0.999 && b.Faces[f].Loops.Count == 2);
        var (d3, _) = d2.DrawPath(new FaceRef(0, big), [P(2, 6), P(4, 8), P(3, 9), P(1, 7), P(2, 6)]);
        var b3 = d3.Bodies[0];
        int top = Enumerable.Range(0, b3.Faces.Count).Single(f => b3.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b3.Faces[f]).Z > 0.999 && b3.Faces[f].Loops.Count == 3);
        var tris = d3.TrianglesOf(new FaceRef(0, top));
        double shown = tris.Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) / (Mm * Mm);
        // 100 − the corner piece (4 − π) − the pie (π) − the rectangle (√8 · √2 = 4)
        double exact = 100 - (4 - Math.PI) - Math.PI - 4;
        output.WriteLine($"shown {shown:0.###} mm², exact {exact:0.###}, {tris.Count} triangles");
        Assert.Equal(exact, shown, 0.05);
        // and every triangle faces up
        Assert.All(tris, t => Assert.True((t.B - t.A).Cross(t.C - t.A).Z > -1e-15));
    }

    [Fact]
    public void A_hexagon_is_drawn_and_pulled_into_a_prism()
    {
        // the polygon tool's shape: six corners round (5, 5) on the top, r 3, as a closed path
        var doc = Cube();
        var hex = Enumerable.Range(0, 7).Select(k => new Vec3((5 + 3 * Math.Cos(k * Math.PI / 3)) * Mm, (5 + 3 * Math.Sin(k * Math.PI / 3)) * Mm, 10 * Mm)).ToList();
        var (d, f) = doc.DrawPath(Top(doc), hex);
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces[f.Face].Loops[0].Count);
        var (d2, _) = d.PushPull(f, 4 * Mm);
        AssertSound(d2);
        Assert.Equal(1000 + 3 * Math.Sqrt(3) / 2 * 9 * 4, Mm3(d2), 6);
    }

    [Fact]
    public void A_path_crossing_itself_or_running_off_the_face_is_refused()
    {
        var doc = Cube();
        // a bow tie
        Assert.Throws<PushPullException>(() => doc.DrawPath(Top(doc), [new Vec3(2 * Mm, 2 * Mm, 10 * Mm), new Vec3(8 * Mm, 8 * Mm, 10 * Mm), new Vec3(8 * Mm, 2 * Mm, 10 * Mm), new Vec3(2 * Mm, 8 * Mm, 10 * Mm), new Vec3(2 * Mm, 2 * Mm, 10 * Mm)]));
        // from edge to edge, but a bend outside the face
        Assert.Throws<PushPullException>(() => doc.DrawPath(Top(doc), [new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(4 * Mm, -3 * Mm, 10 * Mm), new Vec3(10 * Mm, 4 * Mm, 10 * Mm)]));
        // not closed and not from edge to edge
        Assert.Throws<PushPullException>(() => doc.DrawPath(Top(doc), [new Vec3(2 * Mm, 2 * Mm, 10 * Mm), new Vec3(8 * Mm, 2 * Mm, 10 * Mm)]));
    }

    [Fact]
    public void A_line_along_an_edge_or_off_the_edge_is_refused()
    {
        var doc = Cube();
        Assert.Throws<PushPullException>(() => doc.DrawLine(Top(doc), new Vec3(0, 0, 10 * Mm), new Vec3(10 * Mm, 0, 10 * Mm)));
        Assert.Throws<PushPullException>(() => doc.DrawLine(Top(doc), new Vec3(4 * Mm, 2 * Mm, 10 * Mm), new Vec3(4 * Mm, 10 * Mm, 10 * Mm)));
    }

    [Fact]
    public void Helpers_for_typed_sizes_find_the_edge_across_and_the_corner()
    {
        var doc = Cube();
        var top = Top(doc);
        var b = doc.Bodies[0];
        // from the middle of the front edge straight across: the back edge, 10 mm away
        var end = Drawing.RayToEdge(b, top.Face, new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(0, 1, 0));
        Assert.NotNull(end);
        Assert.Equal(10 * Mm, end!.Value.Y, 12);
        Assert.Equal(4 * Mm, end.Value.X, 12);
        var (corner, other) = Drawing.EdgeAt(b, top.Face, new Vec3(3 * Mm, 0.1 * Mm, 10 * Mm))!.Value;
        Assert.Equal(0, corner.X, 12); Assert.Equal(0, corner.Y, 12);
        Assert.Equal(10 * Mm, other.X, 12);
        Assert.True(Drawing.Contains(b, top.Face, new Vec3(5 * Mm, 5 * Mm, 10 * Mm)));
        Assert.False(Drawing.Contains(b, top.Face, new Vec3(15 * Mm, 5 * Mm, 10 * Mm)));
        var c = Drawing.NearestCorner(b, top.Face, new Vec3(9 * Mm, 8 * Mm, 10 * Mm));
        Assert.Equal(10 * Mm, c.X, 12); Assert.Equal(10 * Mm, c.Y, 12);
    }

    /// <summary>The area of the circle as it is shown: a polygon of its edge points (6° apart).</summary>
    private static double ShownCircleArea(double r) { int k = (int)Math.Ceiling(2 * Math.PI / FaceGeometry.ArcStep); return k * r * r * Math.Sin(2 * Math.PI / k) / 2; }

    [Fact]
    public void A_circle_on_the_top_is_pulled_into_a_rod()
    {
        var doc = Cube();
        var (d, c) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        var (d2, r) = d.PushPull(c, 4 * Mm);
        AssertSound(d2);
        Assert.Single(r.NewFaces);
        Assert.Equal(1000 + ShownCircleArea(2) * 4, Mm3(d2), 3);
        d2.ToRsDocx();
    }

    private static FaceRef CylinderOf(SketchDocument d) =>
        new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).Single(f => d.Bodies[0].Faces[f].Surface is BrepCylinder));

    [Fact]
    public void A_rod_pulled_on_its_side_gets_thicker_and_pushed_thinner()
    {
        var doc = Cube();
        var (d, c) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (rod, _) = d.PushPull(c, 4 * Mm);
        var side = CylinderOf(rod);
        var (thick, r) = rod.PushPull(side, 1 * Mm);
        AssertSound(thick);
        Assert.Equal(rod.Bodies[0].Faces.Count, thick.Bodies[0].Faces.Count);
        Assert.Equal(3 * Mm, ((BrepCylinder)thick.Bodies[0].Faces[side.Face].Surface).Radius, 12);
        Assert.Equal(1000 + ShownCircleArea(3) * 4, Mm3(thick), 3);
        var (thin, _) = rod.PushPull(side, -1.5 * Mm);
        AssertSound(thin);
        Assert.Equal(1000 + ShownCircleArea(0.5) * 4, Mm3(thin), 3);
        thick.ToRsDocx();
        // thinner than nothing: refused
        Assert.Throws<PushPullException>(() => rod.PushPull(side, -2 * Mm));
        // by radius: r 3 is 1 mm out
        Assert.Equal(1 * Mm, PushPullMe.Core.PushPull.DistanceForRadius(rod.Bodies[0], side.Face, 3 * Mm), 12);
        Assert.Equal(3 * Mm, PushPullMe.Core.PushPull.RadiusAfter(rod.Bodies[0], side.Face, 1 * Mm), 12);
    }

    [Fact]
    public void A_hole_pulled_on_its_wall_gets_narrower_and_pushed_wider_up_to_the_edge()
    {
        var doc = Cube();
        var (d, c) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (hole, _) = d.PushPull(c, -10 * Mm);
        var wall = CylinderOf(hole);
        // the wall faces into the hole: pulling it (towards you, into the hole) makes the hole narrower
        var (narrow, _) = hole.PushPull(wall, 0.5 * Mm);
        AssertSound(narrow);
        Assert.Equal(1000 - ShownCircleArea(1.5) * 10, Mm3(narrow), 3);
        var (wide, _) = hole.PushPull(wall, -2 * Mm);
        AssertSound(wide);
        Assert.Equal(1000 - ShownCircleArea(4) * 10, Mm3(wide), 3);
        wide.ToRsDocx();
        // by radius: r 1.5 is 0.5 mm into the hole (its face points in)
        Assert.Equal(0.5 * Mm, PushPullMe.Core.PushPull.DistanceForRadius(hole.Bodies[0], wall.Face, 1.5 * Mm), 12);
        Assert.Equal(4 * Mm, PushPullMe.Core.PushPull.RadiusAfter(hole.Bodies[0], wall.Face, -2 * Mm), 12);
        // wider than the cube: the hole would run through its sides
        Assert.Throws<PushPullException>(() => hole.PushPull(wall, -3.5 * Mm));
    }

    [Fact]
    public void A_hole_next_to_another_cannot_grow_into_it()
    {
        var doc = Cube();
        var (d, c1) = doc.DrawCircle(Top(doc), new Vec3(3 * Mm, 5 * Mm, 10 * Mm), 1 * Mm);
        var (d1, _) = d.PushPull(c1, -10 * Mm);
        var (d2, c2) = d1.DrawCircle(Top(d1), new Vec3(7 * Mm, 5 * Mm, 10 * Mm), 1 * Mm);
        var (two, _) = d2.PushPull(c2, -10 * Mm);
        var b = two.Bodies[0];
        var first = new FaceRef(0, Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepCylinder cy && cy.Origin.X < 5 * Mm));
        var (ok, _) = two.PushPull(first, -0.5 * Mm);
        AssertSound(ok);
        Assert.Throws<PushPullException>(() => two.PushPull(first, -2.5 * Mm));
    }

    [Fact]
    public void A_circle_pushed_through_to_the_bottom_makes_a_hole()
    {
        var doc = Cube();
        var (d, c) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (d2, r) = d.PushPull(c, -10 * Mm);
        AssertSound(d2);
        Assert.True(r.Punched);
        Assert.Equal(-1, r.FaceMap[c.Face]);
        // top and bottom each with a hole, four sides, the wall of the hole
        Assert.Equal(7, d2.Bodies[0].Faces.Count);
        Assert.Equal(1000 - ShownCircleArea(2) * 10, Mm3(d2), 3);
        d2.ToRsDocx();
        // half way is a pocket, not a hole
        var (d3, r3) = d.PushPull(c, -5 * Mm);
        Assert.False(r3.Punched);
        Assert.Equal(1000 - ShownCircleArea(2) * 5, Mm3(d3), 3);
    }

    [Fact]
    public void A_rectangle_pushed_through_makes_a_square_hole_that_reads_back()
    {
        var doc = Cube();
        var (d, rect) = doc.DrawRectangle(Top(doc), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(6 * Mm, 8 * Mm, 10 * Mm), XDir);
        var (d2, r) = d.PushPull(rect, -10 * Mm);
        AssertSound(d2);
        Assert.True(r.Punched);
        Assert.Equal(1000 - 4 * 5 * 10, Mm3(d2), 6);
    }

    [Fact]
    public void The_eraser_takes_away_a_line_a_rectangle_and_a_circle()
    {
        var doc = Cube();
        var (dl, _) = doc.DrawLine(Top(doc), new Vec3(4 * Mm, 0, 10 * Mm), new Vec3(4 * Mm, 10 * Mm, 10 * Mm));
        int line = Enumerable.Range(0, dl.Bodies[0].Edges.Count).First(e => dl.Bodies[0].Edges[e] is { Start: >= 0 } x
            && Math.Abs(dl.Bodies[0].Vertices[x.Start].X - 4 * Mm) < 1e-12 && Math.Abs(dl.Bodies[0].Vertices[x.End].X - 4 * Mm) < 1e-12);
        var back = dl.Erase(0, line);
        AssertSound(back);
        Assert.Equal(6, back.Bodies[0].Faces.Count);
        Assert.Equal(12, back.Bodies[0].Edges.Count);
        Assert.Equal(8, back.Bodies[0].Vertices.Count);

        var (dr, rect) = doc.DrawRectangle(Top(doc), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(6 * Mm, 8 * Mm, 10 * Mm), XDir);
        var back2 = dr.Erase(0, dr.Bodies[0].Faces[rect.Face].Loops[0][0].Edge);
        AssertSound(back2);
        Assert.Equal(6, back2.Bodies[0].Faces.Count);

        var (dc, circ) = doc.DrawCircle(Top(doc), new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var back3 = dc.Erase(0, dc.Bodies[0].Faces[circ.Face].Loops[0][0].Edge);
        AssertSound(back3);
        Assert.Equal(6, back3.Bodies[0].Faces.Count);

        // a real edge of the cube (between two faces at an angle) stays
        Assert.Throws<PushPullException>(() => doc.Erase(0, 0));
    }

    [Fact]
    public void Snapping_to_the_edge_finds_vertices_middles_and_points_on_it()
    {
        var doc = Cube();
        var top = Top(doc);
        var b = doc.Bodies[0];
        var (p, kind) = Drawing.NearestOnEdge(b, top.Face, new Vec3(5.2 * Mm, 0.3 * Mm, 10 * Mm), 0.5 * Mm)!.Value;
        Assert.Equal("midden", kind);
        Assert.Equal(5 * Mm, p.X, 12);
        (p, kind) = Drawing.NearestOnEdge(b, top.Face, new Vec3(3 * Mm, 0.3 * Mm, 10 * Mm), 0.5 * Mm)!.Value;
        Assert.Equal("op de rand", kind);
        Assert.Equal(0, p.Y, 12);
        (_, kind) = Drawing.NearestOnEdge(b, top.Face, new Vec3(9.8 * Mm, 9.9 * Mm, 10 * Mm), 0.5 * Mm)!.Value;
        Assert.Equal("eindpunt", kind);
    }
}

/// <summary>Opening .rsdocx: what we write reads back the same; DesignSpark's own files open.</summary>
public class RsDocxReaderTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;

    private static SketchDocument RoundTrip(SketchDocument d)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ppm_{Guid.NewGuid():N}.rsdocx");
        try { d.ToRsDocx().Save(path); return SketchDocument.FromRsDocx(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_pulled_cube_reads_back_the_same()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);
        var b = doc.Bodies[0];
        int top = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).Z > 0.999);
        var (d, rect) = doc.DrawRectangle(new FaceRef(0, top), new Vec3(2 * Mm, 3 * Mm, 10 * Mm), new Vec3(6 * Mm, 8 * Mm, 10 * Mm), new Vec3(1, 0, 0));
        (d, _) = d.PushPull(rect, 3 * Mm);
        var back = RoundTrip(d);
        Assert.Null(XtBrepBuilder.TopologyError(back.Bodies[0]));
        Assert.Equal(d.Bodies[0].Faces.Count, back.Bodies[0].Faces.Count);
        Assert.Equal(d.Volume(), back.Volume(), 1e-15);
        // and it can be worked on further
        int t2 = Enumerable.Range(0, back.Bodies[0].Faces.Count).Where(f => back.Bodies[0].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(back.Bodies[0].Faces[f]).Z > 0.999)
            .MaxBy(f => back.TrianglesOf(new FaceRef(0, f)).Sum(t => t.A.Z));
        var (d2, _) = back.PushPull(new FaceRef(0, t2), 2 * Mm);
        Assert.Null(XtBrepBuilder.TopologyError(d2.Bodies[0]));
    }

    [Fact]
    public void Roundings_read_back_the_same()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.ExtrudeProfile((0, 0),
        [
            new ProfileSegment((10 * Mm, 0)),
            new ProfileSegment((10 * Mm, 4 * Mm), (10 * Mm, 2 * Mm), Ccw: true),
            new ProfileSegment((0, 4 * Mm)), new ProfileSegment((0, 0)),
        ], 0, 10 * Mm)]);
        var back = RoundTrip(doc);
        Assert.Null(XtBrepBuilder.TopologyError(back.Bodies[0]));
        Assert.Equal(doc.Bodies[0].Faces.Count, back.Bodies[0].Faces.Count);
        Assert.Equal(doc.Volume(), back.Volume(), 1e-14);
    }

    [Theory]
    [InlineData("kubus.rsdocx")]
    [InlineData("sleutelrek.rsdocx")]
    [InlineData("RondeVorm.rsdocx")]
    [InlineData("schaaltje.rsdocx")]
    [InlineData("boltest.rsdocx")]
    [InlineData("yubikey.rsdocx")]
    [InlineData("helix.rsdocx")]
    public void DesignSpark_files_open(string file)
    {
        string path = TestFiles.Converter(file);
        try
        {
            var d = SketchDocument.FromRsDocx(path);
            int faces = d.Bodies.Sum(b => b.Faces.Count), shown = d.AllTriangles().Count(x => x.Triangles.Count > 0);
            var errors = d.Bodies.Select(XtBrepBuilder.TopologyError).Where(e => e != null).ToList();
            output.WriteLine($"{file}: {d.Bodies.Count} bodies, {faces} faces ({shown} shown), volume {d.Volume() * 1e9:0.##} mm³, topology {(errors.Count == 0 ? "ok" : errors[0])}");
            foreach (var bb in d.Bodies.Where(x => x.Vertices.Count > 0))
                output.WriteLine($"  body: x {bb.Vertices.Min(v => v.X) * 1e3:0.##}..{bb.Vertices.Max(v => v.X) * 1e3:0.##} y {bb.Vertices.Min(v => v.Y) * 1e3:0.##}..{bb.Vertices.Max(v => v.Y) * 1e3:0.##} z {bb.Vertices.Min(v => v.Z) * 1e3:0.##}..{bb.Vertices.Max(v => v.Z) * 1e3:0.##}");
            string stl = Path.ChangeExtension(path, ".stl");
            if (File.Exists(stl))
            {
                var mesh = StlReader.Read(stl);
                double v = mesh.Triangles.Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6;
                output.WriteLine($"  STL volume {v:0.##} mm³");
            }
            Assert.Empty(errors);
            if (file is "boltest.rsdocx" or "RondeVorm.rsdocx")
                foreach (var (fr, tris) in d.AllTriangles())
                {
                    var face = d.Bodies[fr.Body].Faces[fr.Face];
                    string s = face.Surface switch
                    {
                        BrepSphere sp => $"sphere r {sp.Radius * 1e3:0.##} c {sp.Centre * 1e3}",
                        BrepTorus to => $"torus R {to.MajorRadius * 1e3:0.##} r {to.MinorRadius * 1e3:0.##} axis {to.Axis}",
                        BrepCylinder cy => $"cylinder r {cy.Radius * 1e3:0.##}",
                        BrepCone => "cone",
                        BrepPlane => $"plane n {FaceGeometry.Normal(face)}",
                        var o => o.GetType().Name,
                    };
                    foreach (var ce in face.Loops.SelectMany(l => l))
                        if (d.Bodies[fr.Body].Edges[ce.Edge].Curve is BrepCircle cc && face.Surface is BrepSphere)
                            output.WriteLine($"    circle c {cc.Centre * 1e3} n {cc.Normal} r {cc.Radius * 1e3:0.###} start {d.Bodies[fr.Body].Edges[ce.Edge].Start}");
                    output.WriteLine($"  face {fr.Face}: {s}, rev {face.Reversed}, loops {face.Loops.Count} ({string.Join(",", face.Loops.Select(l => string.Join("+", l.Select(c => d.Bodies[fr.Body].Edges[c.Edge].Curve.GetType().Name.Replace("Brep", "") + (d.Bodies[fr.Body].Edges[c.Edge].Start < 0 ? "(ring)" : "")))))}), tris {tris.Count}, area {tris.Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) * 1e6:0.##} mm², vol-part {tris.Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6 * 1e9:0.##}");
                }
        }
        catch (RsDocxReadException e)
        {
            output.WriteLine($"{file}: not opened: {e.Message}");
            Assert.True(file is "helix.rsdocx" or "yubikey.rsdocx", e.Message);
        }
    }
}

/// <summary>Move, as in SketchUp: a face, edge or vertex moved, what hangs on it follows.</summary>
public class MoveTests(ITestOutputHelper output)
{
    private const double Mm = 0.001;

    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);
    private static int Top(BrepBody b) => Enumerable.Range(0, b.Faces.Count).First(f => b.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(b.Faces[f]).Z > 0.999);
    private static void AssertSound(SketchDocument d) { foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b)); }
    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);

    [Fact]
    public void The_top_moved_up_makes_the_cube_taller()
    {
        var doc = Cube();
        var (d, _) = doc.Move(0, MoveSelection.Face(Top(doc.Bodies[0])), new Vec3(0, 0, 5 * Mm));
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(1500, Mm3(d), 6);
    }

    [Fact]
    public void The_top_moved_sideways_slants_the_walls_and_keeps_the_volume()
    {
        var doc = Cube();
        var (d, r) = doc.Move(0, MoveSelection.Face(Top(doc.Bodies[0])), new Vec3(3 * Mm, 0, 0));
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(1000, Mm3(d), 6);
        // the walls left and right are slanted now
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepPlane && Math.Abs(FaceGeometry.Normal(f).X) is > 0.1 and < 0.99);
    }

    [Fact]
    public void A_top_edge_moved_up_slants_the_top()
    {
        var doc = Cube();
        var b = doc.Bodies[0];
        int edge = Enumerable.Range(0, b.Edges.Count).First(e => b.Vertices[b.Edges[e].Start] is { Z: > 0.009, Y: < 0.001 } && b.Vertices[b.Edges[e].End] is { Z: > 0.009, Y: < 0.001 });
        var (d, _) = doc.Move(0, MoveSelection.Edge(edge), new Vec3(0, 0, 5 * Mm));
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(1250, Mm3(d), 6);
    }

    [Fact]
    public void A_corner_moved_up_folds_the_top_into_two_triangles()
    {
        var doc = Cube();
        var b = doc.Bodies[0];
        int corner = Enumerable.Range(0, b.Vertices.Count).First(v => b.Vertices[v] is { X: > 0.009, Y: > 0.009, Z: > 0.009 });
        var (d, _) = doc.Move(0, MoveSelection.Vertex(corner), new Vec3(0, 0, 5 * Mm));
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Faces.Count);
        double v = Mm3(d);
        output.WriteLine($"volume {v:0.###}");
        Assert.True(Math.Abs(v - (1000 + 250.0 / 3)) < 1e-6 || Math.Abs(v - (1000 + 500.0 / 3)) < 1e-6);
    }

    [Fact]
    public void A_drawn_rectangle_and_circle_are_moved_over_the_face()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (d, rect) = doc.DrawRectangle(top, new Vec3(2 * Mm, 2 * Mm, 10 * Mm), new Vec3(4 * Mm, 4 * Mm, 10 * Mm), new Vec3(1, 0, 0));
        var (d2, r) = d.Move(0, MoveSelection.Face(rect.Face), new Vec3(3 * Mm, 4 * Mm, 0));
        AssertSound(d2);
        Assert.Equal(7, d2.Bodies[0].Faces.Count);
        Assert.Equal(1000, Mm3(d2), 6);
        var moved = d2.Bodies[0].Faces[r.FaceMap[rect.Face]];
        Assert.Equal(5 * Mm, moved.Loops[0].Min(c => d2.Bodies[0].Vertices[d2.Bodies[0].Edges[c.Edge].Start].X), 9);

        var (dc, circ) = doc.DrawCircle(top, new Vec3(3 * Mm, 3 * Mm, 10 * Mm), 1.5 * Mm);
        var (dc2, _) = dc.Move(0, MoveSelection.Face(circ.Face), new Vec3(4 * Mm, 4 * Mm, 0));
        AssertSound(dc2);
        Assert.Equal(1000, Mm3(dc2), 6);
    }

    [Fact]
    public void A_whole_body_is_moved_or_copied()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (dc, circ) = doc.DrawCircle(top, new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (rod, _) = dc.PushPull(circ, 3 * Mm);
        var painted = rod.Paint([new FaceRef(0, 0)], 0xFFFF0000);
        var off = new Vec3(20 * Mm, 0, 0);
        var (moved, _) = painted.Move(0, MoveSelection.WholeBody(), off);
        AssertSound(moved);
        Assert.Single(moved.Bodies);
        Assert.Equal(Mm3(rod), Mm3(moved), 6);
        Assert.Equal(20 * Mm, moved.Bodies[0].Vertices.Min(v => v.X), 12);
        Assert.Equal(25 * Mm, ((BrepCylinder)moved.Bodies[0].Faces.First(f => f.Surface is BrepCylinder).Surface).Origin.X, 12);
        var (copied, r) = painted.Move(0, MoveSelection.Copy(), off);
        AssertSound(copied);
        Assert.Equal(2, copied.Bodies.Count);
        Assert.Equal(2 * Mm3(rod), Mm3(copied), 6);
        Assert.Equal(0xFFFF0000u, copied.ColorOf(new FaceRef(1, 0)));
        copied.ToRsDocx();
    }

    [Fact]
    public void The_top_is_tilted_round_its_front_edge()
    {
        // the cube's top turned 10° round its front edge (y 0, z 10), as a whole: its back edge to y 10·cos, z 10 + 10·sin;
        // the back face follows (leans). Cross-section (0,0) (10,0) (10cos, 10 + 10sin) (0,10), times 10
        static double Tilted(double deg) { double a = deg * Math.PI / 180; return 0.5 * (10 * (10 + 10 * Math.Sin(a)) + 10 * Math.Cos(a) * 10) * 10; }
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var hinge = new Vec3(0, 0, 10 * Mm);
        var (d, r) = doc.Tilt(top, hinge, new Vec3(1, 0, 0), 10 * Math.PI / 180);
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(Tilted(10), Mm3(d), 6);
        Assert.Equal(10 * Mm, d.Bodies[0].Vertices.Where(v => v.Y < 1e-9).Max(v => v.Z), 12);
        d.ToRsDocx();
        // the other way
        var (d2, _) = doc.Tilt(top, hinge, new Vec3(1, 0, 0), -10 * Math.PI / 180);
        Assert.Equal(Tilted(-10), Mm3(d2), 6);
        // as far as 45°
        var (d45, _) = doc.Tilt(top, hinge, new Vec3(1, 0, 0), Math.PI / 4);
        AssertSound(d45);
        d45.ToRsDocx();
    }

    [Fact]
    public void A_whole_body_is_mirrored_or_a_mirrored_copy_made()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (dc, circ) = doc.DrawCircle(top, new Vec3(3 * Mm, 5 * Mm, 10 * Mm), 1.5 * Mm);
        var (rod, _) = dc.PushPull(circ, 4 * Mm);
        // mirrored in its own bottom (z 0): now below it, the rod pointing down
        var (d, _) = rod.MirrorBody(0, new Vec3(0, 0, 0), new Vec3(0, 0, 1), copy: false);
        AssertSound(d);
        Assert.Equal(Mm3(rod), Mm3(d), 6);
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepPlane p && Math.Abs(p.Origin.Z + 14 * Mm) < 1e-9);
        d.ToRsDocx();
        // in the plane x 10 (its right side), as a copy: two halves of one part
        var (two, nb) = rod.MirrorBody(0, new Vec3(10 * Mm, 0, 0), new Vec3(1, 0, 0), copy: true);
        Assert.Equal(1, nb);
        Assert.Null(XtBrepBuilder.TopologyError(two.Bodies[1]));
        Assert.Equal(2 * Mm3(rod), Mm3(two), 6);
        Assert.Equal(17 * Mm, ((BrepCylinder)two.Bodies[1].Faces.First(f => f.Surface is BrepCylinder).Surface).Origin.X, 9);
        two.ToRsDocx();
        // a rounded block (balls, mitres) mirrored too
        var (rb, _) = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]).RoundEdges(0, Enumerable.Range(0, 12).ToList(), 2 * Mm);
        var (rm, _) = rb.MirrorBody(0, new Vec3(0, 0, 0), new Vec3(1, 1, 0), copy: false);
        AssertSound(rm);
        Assert.Equal(Mm3(rb), Mm3(rm), 1.5); // (curved faces made into triangles anew)
        rm.ToRsDocx();
    }

    [Fact]
    public void A_whole_body_is_scaled()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (dc, circ) = doc.DrawCircle(top, new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (rod, _) = dc.PushPull(circ, 4 * Mm);
        // twice as large from the corner: 8× the volume, the rod's radius 4
        var (d, _) = rod.ScaleBody(0, new Vec3(0, 0, 0), 2, null, copy: false);
        AssertSound(d);
        Assert.Equal(8 * Mm3(rod), Mm3(d), 3);
        Assert.Equal(4 * Mm, ((BrepCylinder)d.Bodies[0].Faces.First(f => f.Surface is BrepCylinder).Surface).Radius, 12);
        d.ToRsDocx();
        // half as large, as a copy
        var (h, nb) = rod.ScaleBody(0, new Vec3(0, 0, 0), 0.5, null, copy: true);
        Assert.Equal(2, h.Bodies.Count);
        Assert.Equal(Mm3(rod) * (1 + 1.0 / 8), Mm3(h), 3);
        // along one axis: only with flat faces (the plain cube twice as long along x; the rod's cube refused)
        var (lx, _) = doc.ScaleBody(0, new Vec3(0, 0, 0), 2, new Vec3(1, 0, 0), copy: false);
        AssertSound(lx);
        Assert.Equal(2000, Mm3(lx), 6);
        Assert.Equal(20 * Mm, lx.Bodies[0].Vertices.Max(v => v.X), 12);
        lx.ToRsDocx();
        Assert.Throws<PushPullException>(() => rod.ScaleBody(0, new Vec3(0, 0, 0), 2, new Vec3(1, 0, 0), copy: false));
    }

    [Fact]
    public void A_whole_body_is_turned_or_a_turned_copy_made()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (dc, circ) = doc.DrawCircle(top, new Vec3(3 * Mm, 5 * Mm, 10 * Mm), 1.5 * Mm);
        var (rod, _) = dc.PushPull(circ, 4 * Mm);
        // a quarter turn round the upright line through the middle of the cube: the rod goes from x 3 to y 3
        var centre = new Vec3(5 * Mm, 5 * Mm, 0);
        var (d, b) = rod.RotateBody(0, centre, new Vec3(0, 0, 1), Math.PI / 2, copy: false);
        AssertSound(d);
        Assert.Equal(0, b);
        Assert.Equal(Mm3(rod), Mm3(d), 6);
        var axisAt = ((BrepCylinder)d.Bodies[0].Faces.First(f => f.Surface is BrepCylinder).Surface).Origin;
        Assert.Equal(5 * Mm, axisAt.X, 9);
        Assert.Equal(3 * Mm, axisAt.Y, 9);
        d.ToRsDocx();
        // tipped over (a quarter turn round the x axis): the rod now along y
        var (tipped, _) = rod.RotateBody(0, centre, new Vec3(1, 0, 0), Math.PI / 2, copy: false);
        AssertSound(tipped);
        Assert.Equal(1, Math.Abs(((BrepCylinder)tipped.Bodies[0].Faces.First(f => f.Surface is BrepCylinder).Surface).Axis.Normalized().Y), 9);
        tipped.ToRsDocx();
        // 45°, as a copy: two bodies
        var (two, nb) = rod.RotateBody(0, new Vec3(20 * Mm, 5 * Mm, 0), new Vec3(0, 0, 1), Math.PI / 4, copy: true);
        Assert.Equal(2, two.Bodies.Count);
        Assert.Equal(1, nb);
        Assert.Equal(2 * Mm3(rod), Mm3(two), 6);
        two.ToRsDocx();
    }

    [Fact]
    public void A_rod_top_moved_up_makes_the_rod_longer_and_sideways_is_refused()
    {
        var doc = Cube();
        var top = new FaceRef(0, Top(doc.Bodies[0]));
        var (d, circ) = doc.DrawCircle(top, new Vec3(5 * Mm, 5 * Mm, 10 * Mm), 2 * Mm);
        var (d2, r) = d.PushPull(circ, 4 * Mm);
        int rodTop = r.FaceMap[circ.Face];
        double v0 = Mm3(d2);
        var (d3, _) = d2.Move(0, MoveSelection.Face(rodTop), new Vec3(0, 0, 3 * Mm));
        AssertSound(d3);
        Assert.True(Mm3(d3) > v0 + 30);
        Assert.Throws<PushPullException>(() => d2.Move(0, MoveSelection.Face(rodTop), new Vec3(2 * Mm, 0, 0)));
    }

    [Fact]
    public void A_corner_moved_down_onto_the_one_below_cuts_the_corner_off()
    {
        var doc = Cube();
        var b = doc.Bodies[0];
        int corner = Enumerable.Range(0, b.Vertices.Count).First(v => b.Vertices[v] is { X: > 0.009, Y: > 0.009, Z: > 0.009 });
        var (d, _) = doc.Move(0, MoveSelection.Vertex(corner), new Vec3(0, 0, -10 * Mm));
        AssertSound(d);
        Assert.Equal(7, d.Bodies[0].Vertices.Count);
        Assert.True(Mm3(d) < 1000);
    }

    [Fact]
    public void A_step_moved_up_flush_with_the_high_one_becomes_one_face()
    {
        // an L profile in x/z (low step at z 4 from x 8 to 20, high at z 10), 10 deep: the low top moved up 6
        var p = new Polyhedron
        {
            Vertices =
            [
                (0, 0, 0), (20 * Mm, 0, 0), (20 * Mm, 0, 4 * Mm), (8 * Mm, 0, 4 * Mm), (8 * Mm, 0, 10 * Mm), (0, 0, 10 * Mm),
                (0, 10 * Mm, 0), (20 * Mm, 10 * Mm, 0), (20 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 4 * Mm), (8 * Mm, 10 * Mm, 10 * Mm), (0, 10 * Mm, 10 * Mm),
            ],
            Faces = [[0, 1, 2, 3, 4, 5], [6, 11, 10, 9, 8, 7], [0, 6, 7, 1], [1, 7, 8, 2], [2, 8, 9, 3], [3, 9, 10, 4], [4, 10, 11, 5], [5, 11, 6, 0]],
        };
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(p)]);
        var b = doc.Bodies[0];
        int low = Enumerable.Range(0, b.Faces.Count).First(f => FaceGeometry.Normal(b.Faces[f]).Z > 0.999 && b.Faces[f].Loops[0].All(c => b.Vertices[b.Edges[c.Edge].Start].Z < 5 * Mm));
        var (d, _) = doc.Move(0, MoveSelection.Face(low), new Vec3(0, 0, 6 * Mm));
        AssertSound(d);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        Assert.Equal(2000, Mm3(d), 6);
    }
}

/// <summary>The paint bucket: colours stay on their faces through everything done to the model.</summary>
public class PaintTests
{
    private const double Mm = 0.001;
    private const uint Red = 0xFFE11D48, Blue = 0xFF2563EB;

    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);
    private static FaceRef Top(SketchDocument d) => new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).First(f => d.Bodies[0].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(d.Bodies[0].Faces[f]).Z > 0.999));

    [Fact]
    public void The_colours_DesignSpark_gave_are_read()
    {
        // testdata/kubus_kleuren.rsdocx (made in DesignSpark, 12 October 2026): a red, a white and a blue cube, the smallest with a colour per face
        string path = TestFiles.Converter("kubus_kleuren.rsdocx");
        var d = SketchDocument.FromRsDocx(path);
        var perBody = Enumerable.Range(0, d.Bodies.Count).Select(b => Enumerable.Range(0, d.Bodies[b].Faces.Count).Select(f => d.ColorOf(new FaceRef(b, f))).ToList()).ToList();
        Assert.Contains(perBody, fs => fs.All(c => c == 0xFFFF0000));
        Assert.Contains(perBody, fs => fs.All(c => c == 0xFFFFFFFF));
        Assert.Contains(perBody, fs => fs.All(c => c == 0xFF0000FF));
        Assert.Contains(perBody, fs => fs.Where(c => c != null).Distinct().Count() == 5);
    }

    [Fact]
    public void Colours_painted_here_go_into_the_file_and_come_back()
    {
        var doc = SketchDocument.FromBodies([
            BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm)),
            BrepShapes.FromPolyhedron(Polyhedron.Box(20 * Mm, 0, 0, 30 * Mm, 10 * Mm, 10 * Mm))]);
        var top = Top(doc);
        doc = doc.Paint([top], Red).Paint(Enumerable.Range(0, doc.Bodies[1].Faces.Count).Select(f => new FaceRef(1, f)), Blue);
        string path = Path.Combine(Path.GetTempPath(), $"ppm_{Guid.NewGuid():N}.rsdocx");
        try
        {
            doc.ToRsDocx().Save(path);
            // (a copy to open in DesignSpark: the first cube's top red, the second cube blue)
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output"));
            Directory.CreateDirectory(outDir);
            File.Copy(path, Path.Combine(outDir, "kleuren_test.rsdocx"), overwrite: true);
            var back = SketchDocument.FromRsDocx(path);
            for (int b = 0; b < 2; b++)
                for (int f = 0; f < doc.Bodies[b].Faces.Count; f++)
                    Assert.Equal(doc.ColorOf(new FaceRef(b, f)), back.ColorOf(new FaceRef(b, f)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_painted_face_keeps_its_colour_through_push_pull_drawing_moving_and_erasing()
    {
        var doc = Cube();
        var top = Top(doc);
        doc = doc.Paint([top], Red);
        Assert.Equal(Red, doc.ColorOf(top));
        // pulled up: still red; the walls that grew have no colour
        var (d1, r1) = doc.PushPull(top, 3 * Mm);
        Assert.Equal(Red, d1.ColorOf(new FaceRef(0, r1.FaceMap[top.Face])));
        Assert.All(r1.GrownFaces, g => Assert.Null(d1.ColorOf(new FaceRef(0, g))));
        // a rectangle drawn on it is red too, and pulled out its own new side faces are not
        var top1 = new FaceRef(0, r1.FaceMap[top.Face]);
        var (d2, rect) = d1.DrawRectangle(top1, new Vec3(2 * Mm, 2 * Mm, 13 * Mm), new Vec3(5 * Mm, 5 * Mm, 13 * Mm), new Vec3(1, 0, 0));
        Assert.Equal(Red, d2.ColorOf(rect));
        var d3 = d2.Paint([rect], Blue);
        var (d4, r4) = d3.PushPull(rect, 2 * Mm);
        Assert.Equal(Blue, d4.ColorOf(new FaceRef(0, r4.FaceMap[rect.Face])));
        Assert.All(r4.NewFaces, g => Assert.Null(d4.ColorOf(new FaceRef(0, g))));
        // moved: still blue
        var (d5, r5) = d4.Move(0, MoveSelection.Face(r4.FaceMap[rect.Face]), new Vec3(1 * Mm, 0, 0));
        Assert.Equal(Blue, d5.ColorOf(new FaceRef(0, r5.FaceMap[r4.FaceMap[rect.Face]])));
        // a drawn line erased: the face that is left keeps the colour
        var (d6, half) = d1.DrawLine(top1, new Vec3(4 * Mm, 0, 13 * Mm), new Vec3(4 * Mm, 10 * Mm, 13 * Mm));
        Assert.Equal(Red, d6.ColorOf(half));
        int line = Enumerable.Range(0, d6.Bodies[0].Edges.Count).First(e => d6.Bodies[0].Edges[e] is { Start: >= 0 } x
            && Math.Abs(d6.Bodies[0].Vertices[x.Start].X - 4 * Mm) < 1e-12 && Math.Abs(d6.Bodies[0].Vertices[x.End].X - 4 * Mm) < 1e-12);
        var d7 = d6.Erase(0, line);
        Assert.Single(Enumerable.Range(0, d7.Bodies[0].Faces.Count), f => d7.ColorOf(new FaceRef(0, f)) == Red);
        // the paint taken off again
        Assert.Null(doc.Paint([top], null).ColorOf(top));
    }
}

/// <summary>The tape measure's guide lines.</summary>
public class GuideTests
{
    [Fact]
    public void A_guide_parallel_to_an_edge_and_where_guides_meet()
    {
        // the edge along x through the origin, the mouse at (3, 25, 0): the guide at 25 along y
        var (g, off) = Guides.Parallel(new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 25, 0));
        Assert.Equal(25, off.Length, 12);
        Assert.Equal(25, g.Point.Y, 12);
        Assert.Equal(1, g.Dir.X, 12);
        var h = new Guide(new Vec3(10, 0, 0), new Vec3(0, 1, 0));
        var m = Guides.Meet(g.Point, g.Dir, h.Point, h.Dir);
        Assert.NotNull(m);
        Assert.Equal(10, m!.Value.X, 12); Assert.Equal(25, m.Value.Y, 12);
        // parallel or crossing over each other: no meeting point
        Assert.Null(Guides.Meet(g.Point, g.Dir, new Vec3(0, 30, 0), new Vec3(1, 0, 0)));
        Assert.Null(Guides.Meet(g.Point, g.Dir, new Vec3(10, 0, 5), new Vec3(0, 1, 0)));
        // on a segment of an edge, or past its end
        Assert.NotNull(Guides.MeetSegment(g, new Vec3(5, 0, 0), new Vec3(5, 40, 0)));
        Assert.Null(Guides.MeetSegment(g, new Vec3(5, 0, 0), new Vec3(5, 20, 0)));
        Assert.True(Guides.InPlane(g, new Vec3(0, 0, 0), new Vec3(0, 0, 1)));
    }
}

/// <summary>On real models from the converter (testdata of RsCOnverter).</summary>
public class RealModelTests(ITestOutputHelper output)
{
    private static string TestData(string name) => TestFiles.Converter(name);

    /// <summary>
    /// From the user's session log: a block pulled from a hook, its top pushed down, the strip above it pulled out far
    /// (a plate over the block), then the block's front pushed back 0.2 past where it started: the block goes, a pocket
    /// of 0.2 under the plate is left, and the side of the plate and the wall of the pocket are two faces facing apart.
    /// </summary>
    [Fact]
    public void The_users_path_along_an_edge_on_the_key_rack_divides_the_face()
    {
        // from the session log (12 Oct 2026): sleutelrek.rsdocx, face 1, the first piece along its edge
        var doc = SketchDocument.FromRsDocx(TestData("sleutelrek.rsdocx"), out _);
        var pts = new[] { (-22, -17), (-22, -34.3), (-6.2, -34.3), (-6.2, -39.1), (-22, -39.1) }.Select(p => new Vec3(p.Item1 * 0.001, p.Item2 * 0.001, 0)).ToList();
        var (d, part) = doc.DrawPath(new FaceRef(0, 1), pts);
        foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
        var (d2, _) = d.PushPull(part, -0.5 * 0.001);
        Assert.True(d2.Volume() < doc.Volume());
    }

    [Fact]
    public void The_users_cube_is_rounded_top_bottom_and_then_upright()
    {
        // from the session log (14 Oct 2026): output/kubus_bewerkt.rsdocx, top and bottom edges r 5, then the upright ones
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../RsCOnverter/output/kubus_bewerkt.rsdocx"));
        if (!File.Exists(path)) return;
        var doc = SketchDocument.FromRsDocx(path, out _);
        var b = doc.Bodies[0];
        output.WriteLine($"{b.Faces.Count} faces, {b.Edges.Count} edges");
        var lines = Enumerable.Range(0, b.Edges.Count).Where(e => b.Edges[e].Curve is BrepLine && b.Edges[e].Start >= 0).ToList();
        double zMax = b.Vertices.Max(v => v.Z), zMin = b.Vertices.Min(v => v.Z);
        bool At(int e, double z) => Math.Abs(b.Vertices[b.Edges[e].Start].Z - z) < 1e-9 && Math.Abs(b.Vertices[b.Edges[e].End].Z - z) < 1e-9;
        var topE = lines.Where(e => At(e, zMax)).ToList();
        var bottomE = lines.Where(e => At(e, zMin)).ToList();
        output.WriteLine($"top {topE.Count}, bottom {bottomE.Count}");
        var (d, _) = doc.RoundEdges(0, topE, 5e-3);
        var (d2, _) = d.RoundEdges(0, bottomE.Select(e => e).ToList() is var be && be.Count > 0 ? MapSame(doc, d, be) : [], 5e-3);
        var b2 = d2.Bodies[0];
        var upright = Enumerable.Range(0, b2.Edges.Count).Where(e => b2.Edges[e].Curve is BrepLine && b2.Edges[e].Start >= 0
            && Math.Abs((b2.Vertices[b2.Edges[e].End] - b2.Vertices[b2.Edges[e].Start]).Normalized().Z) > 0.999).ToList();
        output.WriteLine($"upright {upright.Count}");
        var (d3, _) = d2.RoundEdges(0, upright, 5e-3);
        foreach (var bb in d3.Bodies) Assert.Null(XtBrepBuilder.TopologyError(bb));
        output.WriteLine($"{d3.Bodies[0].Faces.Count(f => f.Surface is BrepSphere)} balls");
        d3.ToRsDocx();
    }

    /// <summary>Edges of one document found again in another by their end points (the ones that did not move).</summary>
    private static List<int> MapSame(SketchDocument a, SketchDocument b, List<int> edges)
    {
        var ba = a.Bodies[0]; var bb = b.Bodies[0];
        return edges.Select(e => Enumerable.Range(0, bb.Edges.Count).First(f => bb.Edges[f].Start >= 0
            && (bb.Vertices[bb.Edges[f].Start] - ba.Vertices[ba.Edges[e].Start]).Length < 1e-9 && (bb.Vertices[bb.Edges[f].End] - ba.Vertices[ba.Edges[e].End]).Length < 1e-9)).ToList();
    }

    [Fact]
    public void A_designspark_corner_of_three_radii_opens_changes_and_saves()
    {
        // the user's test/kubus_bewerkt_bewerkt.rsdocx: DesignSpark's corner of r 1, 2 and 3 (its curves where the
        // roundings cut each other are intersection curves, XT node 38)
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../test/kubus_bewerkt_bewerkt.rsdocx"));
        if (!File.Exists(path)) return;
        var doc = SketchDocument.FromRsDocx(path, out _);
        int body = Enumerable.Range(0, doc.Bodies.Count).Single(i => doc.Bodies[i].Faces.Count(f => f.Surface is BrepCylinder) == 3);
        var b = doc.Bodies[body];
        Assert.Equal(3, b.Edges.Count(e => e.Curve is BrepBSpline));
        doc.ToRsDocx();
        // the r 2 rounding to r 1.5: made again, the others keep theirs
        int r2 = Enumerable.Range(0, b.Faces.Count).Single(f => b.Faces[f].Surface is BrepCylinder c && Math.Abs(c.Radius - 2e-3) < 1e-6);
        var (d2, r) = doc.PushPull(new FaceRef(body, r2), PushPullMe.Core.PushPull.DistanceForRadius(b, r2, 1.5e-3));
        Assert.Null(XtBrepBuilder.TopologyError(d2.Bodies[body]));
        Assert.Equal([1.0, 1.5, 3.0], d2.Bodies[body].Faces.Where(f => f.Surface is BrepCylinder).Select(f => Math.Round(((BrepCylinder)f.Surface).Radius * 1e3, 6)).Order().ToList());
        d2.ToRsDocx();
        // taken away: a block
        var (sharp, _, _, _) = PushPullMe.Core.PushPull.Unround(b, r2);
        Assert.Equal(6, sharp.Faces.Count);
    }

    [Fact]
    public void The_corner_roundings_of_the_key_rack_get_another_radius_or_are_refused()
    {
        var doc = SketchDocument.FromRsDocx(TestData("sleutelrek.rsdocx"), out _);
        var b = doc.Bodies[0];
        int done = 0, refused = 0;
        var reasons = new Dictionary<string, int>();
        foreach (int f in Enumerable.Range(0, b.Faces.Count).Where(f => b.Faces[f].Surface is BrepCylinder c && Math.Abs(c.Radius - 2e-3) < 1e-9))
            try
            {
                var (d2, _) = doc.PushPull(new FaceRef(0, f), PushPullMe.Core.PushPull.DistanceForRadius(b, f, 1.5e-3));
                Assert.Null(XtBrepBuilder.TopologyError(d2.Bodies[0]));
                d2.ToRsDocx();
                done++;
            }
            catch (PushPullException ex) { refused++; reasons[ex.Message] = reasons.GetValueOrDefault(ex.Message) + 1; }
        output.WriteLine($"{done} ok, {refused} refused");
        foreach (var (why, n) in reasons) output.WriteLine($"  {n}× {why}");
    }

    [Theory]
    [InlineData("sleutelrek.rsdocx")]
    [InlineData("schaaltje.rsdocx")]
    public void Every_straight_edge_can_be_rounded_a_little_or_is_refused(string file)
    {
        var doc = SketchDocument.FromRsDocx(TestData(file), out _);
        int done = 0, refused = 0;
        var reasons = new Dictionary<string, int>();
        for (int b = 0; b < Math.Min(doc.Bodies.Count, 1); b++)
            for (int e = 0; e < doc.Bodies[b].Edges.Count; e++)
            {
                if (doc.Bodies[b].Edges[e].Curve is not BrepLine) continue;
                try
                {
                    var (d2, _) = doc.RoundEdge(b, e, 0.5e-3);
                    Assert.Null(XtBrepBuilder.TopologyError(d2.Bodies[b]));
                    d2.ToRsDocx();
                    done++;
                }
                catch (PushPullException ex) { refused++; reasons[ex.Message] = reasons.GetValueOrDefault(ex.Message) + 1; }
            }
        output.WriteLine($"{file}: {done} ok, {refused} refused");
        foreach (var (why, n) in reasons) output.WriteLine($"  {n}× {why}");
    }

    [Theory]
    [InlineData("sleutelrek.rsdocx")]
    [InlineData("RondeVorm.rsdocx")]
    [InlineData("schaaltje.rsdocx")]
    public void Every_cylinder_can_be_made_a_little_thicker_and_thinner_or_is_refused(string file)
    {
        var doc = SketchDocument.FromRsDocx(TestData(file), out _);
        int done = 0, refused = 0;
        var reasons = new Dictionary<string, int>();
        for (int b = 0; b < doc.Bodies.Count; b++)
            for (int f = 0; f < doc.Bodies[b].Faces.Count; f++)
            {
                if (doc.Bodies[b].Faces[f].Surface is not BrepCylinder) continue;
                foreach (double d in new[] { 0.1e-3, -0.1e-3 })
                    try
                    {
                        var (d2, _) = doc.PushPull(new FaceRef(b, f), d);
                        Assert.Null(XtBrepBuilder.TopologyError(d2.Bodies[b]));
                        d2.ToRsDocx();
                        done++;
                    }
                    catch (PushPullException ex) { refused++; reasons[ex.Message] = reasons.GetValueOrDefault(ex.Message) + 1; }
            }
        output.WriteLine($"{file}: {done} ok, {refused} refused");
        foreach (var (why, n) in reasons) output.WriteLine($"  {n}× {why}");
    }

    [Fact]
    public void A_block_pushed_back_past_its_start_under_a_plate_leaves_a_pocket()
    {
        var doc = SketchDocument.FromMesh(StlReader.Read(TestData("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        foreach (var (face, mm) in new[] { (9, 5.5), (6, 3.9), (61, 6.2), (64, -1.1), (66, 9.6) })
            doc = doc.PushPull(new FaceRef(0, face), mm / 1000).Doc;
        double before = doc.Volume() * 1e9;
        var (d, _) = doc.PushPull(new FaceRef(0, 61), -6.4e-3);
        Assert.Null(XtBrepBuilder.TopologyError(d.Bodies[0]));
        // the block 5.5 × 2.8 × 6.2 goes, the pocket 5.5 × 2.8 × 0.2 too
        Assert.Equal(before - 5.5 * 2.8 * 6.4, d.Volume() * 1e9, 3);
    }

    [Theory]
    [InlineData("sleutelrek.stl")]
    [InlineData("kubus.stl")]
    public void Every_large_flat_face_can_be_pulled_and_pushed_back(string file)
    {
        var doc = SketchDocument.FromMesh(StlReader.Read(TestData(file)), new MeshOptions { MergeCoplanar = true });
        double v0 = doc.Volume();
        int tried = 0, refused = 0;
        for (int b = 0; b < doc.Bodies.Count; b++)
        {
            var body = doc.Bodies[b];
            // the ten largest planar faces
            var big = Enumerable.Range(0, body.Faces.Count).Where(f => body.Faces[f].Surface is BrepPlane)
                .OrderByDescending(f => doc.TrianglesOf(new FaceRef(b, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length)).Take(10).ToList();
            foreach (int f in big)
            {
                tried++;
                try
                {
                    var (d1, _) = doc.PushPull(new FaceRef(b, f), 0.5e-3);
                    foreach (var x in d1.Bodies) Assert.Null(XtBrepBuilder.TopologyError(x));
                    if (tried == 1)
                    {
                        // written as a DesignSpark file (the builder checks the body as it goes)
                        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../output"));
                        Directory.CreateDirectory(dir);
                        d1.ToRsDocx().Save(Path.Combine(dir, Path.ChangeExtension(file, null) + "_getrokken.rsdocx"));
                        File.WriteAllBytes(Path.Combine(dir, Path.ChangeExtension(file, null) + "_getrokken.stl"), d1.ToStl());
                    }
                    var (d2, _) = d1.PushPull(new FaceRef(b, f), -0.5e-3);
                    Assert.Equal(v0, d2.Volume(), 1e-12);
                }
                catch (PushPullException e) { refused++; output.WriteLine($"body {b} face {f}: {e.Message}"); }
            }
        }
        output.WriteLine($"{file}: {tried} tried, {refused} refused");
        Assert.True(refused * 2 < tried, $"{refused} of {tried} refused");
    }
}

public class DebugTests(ITestOutputHelper output)
{
    [Fact]
    public void Debug_sleutelrek_roundings()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var b = doc.Bodies[0];
        for (int f = 0; f < b.Faces.Count; f++)
        {
            if (b.Faces[f].Surface is not BrepPlane) continue;
            bool nextToRound = b.Faces[f].Loops.SelectMany(l => l).Any(c => Enumerable.Range(0, b.Faces.Count).Any(g => g != f && b.Faces[g].Surface is BrepCylinder && b.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))));
            if (!nextToRound) continue;
            foreach (double mm in new[] { 2.0, -2.0 })
                try
                {
                    var (d, r) = doc.PushPull(new FaceRef(0, f), mm * 1e-3);
                    output.WriteLine($"face {f} {FaceGeometry.Normal(b.Faces[f])} {mm:+0;-0}: ok, trimmed {r.TrimmedFaces.Count}, new {r.NewFaces.Count}");
                }
                catch (PushPullException e) { output.WriteLine($"face {f} {mm:+0;-0}: {e.Message}"); }
        }
    }

    [Fact]
    public void Debug_sleutelrek_drag_into_rounding()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var b = doc.Bodies[0];
        foreach (int face in new[] { 5, 32, 41, 43, 52 })
        {
            var f = b.Faces[face];
            var pts = f.Loops[0].Select(c => b.Vertices[b.Edges[c.Edge].Start]).ToList();
            output.WriteLine($"face {face} normal {FaceGeometry.Normal(f)} z {pts.Min(p => p.Z) * 1000:0.##}..{pts.Max(p => p.Z) * 1000:0.##} x {pts.Min(p => p.X) * 1000:0.##}..{pts.Max(p => p.X) * 1000:0.##}");
            string last = "";
            // every 0.1 mm, and every distance that makes the face flush with a vertex (what the snap gives)
            var n = FaceGeometry.Normal(f); var o = ((BrepPlane)f.Surface).Origin;
            var flush = b.Vertices.Select(v => Math.Round((v - o).Dot(n) * 1e6) / 1e3).Where(x => x > -10 && x < 15);
            foreach (double mm in Enumerable.Range(-100, 251).Select(i => i / 10.0).Concat(flush).Distinct().Order())
            {
                if (Math.Abs(mm) < 1e-9) continue;
                string what;
                try
                {
                    var (d, r) = doc.PushPull(new FaceRef(0, face), mm / 1000, checkCrossing: false);
                    what = r.FaceMap[face] < 0 ? "FACE GONE" : $"ok trimmed {r.TrimmedFaces.Count} new {r.NewFaces.Count} faces {d.Bodies[0].Faces.Count}";
                }
                catch (PushPullException e) { what = "refused: " + e.Message; }
                catch (Exception e) { what = "CRASH " + e.GetType().Name + ": " + e.Message; }
                if (what != last) output.WriteLine($"  {mm:+0.0;-0.0} mm: {what}");
                last = what;
            }
        }
    }

    [Fact]
    public void Debug_sleutelrek_pull_check_volume()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var b = doc.Bodies[0];
        double v0 = doc.Volume();
        for (int face = 0; face < b.Faces.Count; face++)
        {
            if (b.Faces[face].Surface is not BrepPlane) continue;
            bool nextToRound = b.Faces[face].Loops.SelectMany(l => l).Any(c => Enumerable.Range(0, b.Faces.Count).Any(g => g != face && b.Faces[g].Surface is BrepCylinder && b.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))));
            if (!nextToRound) continue;
            double area = doc.TrianglesOf(new FaceRef(0, face)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2);
            foreach (double mm in new[] { 3.0, -1.0 })
                try
                {
                    var (d, r) = doc.PushPull(new FaceRef(0, face), mm / 1000);
                    double dv = (d.Volume() - v0) * 1e9, expect = area * mm / 1000 * 1e9;
                    // a pull (no trimming) adds a prism of exactly area × distance
                    string flag = r.TrimmedFaces.Count == 0 && Math.Abs(dv - expect) > 0.01 * Math.Abs(expect) ? "  <<<< VOLUME" : "";
                    // with roundings moving along: what is added is the outline seen along the normal × distance; check
                    // the way back instead
                    if (r.MovedAlong.Count > 0 || r.TrimmedFaces.Count > 0)
                    {
                        var (back, _) = d.PushPull(new FaceRef(0, r.FaceMap[face]), -mm / 1000);
                        flag = Math.Abs(back.Volume() - v0) * 1e9 > 0.01 ? "  <<<< NOT BACK" : "";
                    }
                    output.WriteLine($"face {face} n {FaceGeometry.Normal(b.Faces[face])} {mm:+0;-0}: dV {dv:0.00} expect {expect:0.00} new {r.NewFaces.Count} grown {r.GrownFaces.Count} trimmed {r.TrimmedFaces.Count} along {r.MovedAlong.Count} curvedGrown {string.Join(",", r.GrownFaces.Where(g => d.Bodies[0].Faces[g].Surface is BrepCylinder))}{flag}");
                }
                catch (PushPullException e) { output.WriteLine($"face {face} {mm:+0;-0}: {e.Message}"); }
        }
    }

    [Fact]
    public void Debug_sleutelrek_side_after_rounding()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc0 = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        // the first hook: its side faces (x) after its inner face (5, +y) went into the rounding
        foreach (var first in new double[] { 0, 2 })
        {
            var doc = doc0;
            var map = Enumerable.Range(0, doc.Bodies[0].Faces.Count).ToList();
            if (first != 0) { var (d0, r0) = doc.PushPull(new FaceRef(0, 5), first / 1000); doc = d0; map = r0.FaceMap.ToList(); }
            var b = doc.Bodies[0];
            foreach (int side in new[] { 8, 9 })
            {
                int f = map[side];
                double area = doc.TrianglesOf(new FaceRef(0, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2);
                foreach (double mm in new[] { 3.0, -1.0 })
                    try
                    {
                        var (d, r) = doc.PushPull(new FaceRef(0, f), mm / 1000);
                        double dv = (d.Volume() - doc.Volume()) * 1e9, expect = area * mm / 1000 * 1e9;
                        output.WriteLine($"first {first}: side {side}->{f} {mm:+0;-0}: dV {dv:0.00} expect {expect:0.00} new {r.NewFaces.Count} grown {string.Join(",", r.GrownFaces.Select(g => g + (d.Bodies[0].Faces[g].Surface is BrepCylinder ? "c" : "")))} trimmed {r.TrimmedFaces.Count}{(Math.Abs(dv - expect) > 0.01 * Math.Abs(expect) ? "  <<<< VOLUME" : "")}");
                        foreach (int g in r.GrownFaces.Concat(r.NewFaces).Where(g => d.Bodies[0].Faces[g].Surface is BrepCylinder))
                            output.WriteLine($"    cyl {g}: tessellated {(FaceGeometry.CylinderTriangles(d.Bodies[0], d.Bodies[0].Faces[g]) != null ? "yes" : "NO")}, loops {d.Bodies[0].Faces[g].Loops.Count} ({string.Join(",", d.Bodies[0].Faces[g].Loops.Select(l => l.Count))})");
                    }
                    catch (PushPullException e) { output.WriteLine($"first {first}: side {side} {mm:+0;-0}: {e.Message}"); }
            }
        }
    }

    [Fact]
    public void Debug_sleutelrek_face5_neighbours()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var b = doc.Bodies[0];
        {
            var (d1, r1) = doc.PushPull(new FaceRef(0, 5), 3e-3);
            var (d2, r2) = d1.PushPull(new FaceRef(0, r1.FaceMap[5]), -3e-3);
            output.WriteLine($"face 5 +3: along {string.Join(",", r1.MovedAlong)} grown {string.Join(",", r1.GrownFaces)} new {r1.NewFaces.Count}; back: along {string.Join(",", r2.MovedAlong)} grown {string.Join(",", r2.GrownFaces)} new {r2.NewFaces.Count} trimmed {r2.TrimmedFaces.Count}; faces {b.Faces.Count} -> {d1.Bodies[0].Faces.Count} -> {d2.Bodies[0].Faces.Count}; dV back {(d2.Volume() - doc.Volume()) * 1e9:0.000}");
            foreach (var (dd, name) in new[] { (doc, "before"), (d2, "after") })
                for (int g = 0; g < dd.Bodies[0].Faces.Count; g++)
                    if (dd.Bodies[0].Faces[g].Surface is BrepCylinder cy && Math.Abs(cy.Origin.X * 1000 + 5) < 1e-3)
                        output.WriteLine($"  {name} cyl {g}: r {cy.Radius * 1000:0.###} origin {cy.Origin * 1000} tris {dd.TrianglesOf(new FaceRef(0, g)).Count} area {dd.TrianglesOf(new FaceRef(0, g)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) * 1e6:0.###}");
        }
        {
            var (d1, r1) = doc.PushPull(new FaceRef(0, 17), 1e-3);
            output.WriteLine($"face 17 -1: along {string.Join(",", r1.MovedAlong)} grown {string.Join(",", r1.GrownFaces)} new {string.Join(",", r1.NewFaces)} trimmed {r1.TrimmedFaces.Count}, faces {b.Faces.Count} -> {d1.Bodies[0].Faces.Count}, dV {(d1.Volume() - doc.Volume()) * 1e9:0.000}");
            var b1 = d1.Bodies[0];
            double SignedVol(SketchDocument dd, int g) => dd.TrianglesOf(new FaceRef(0, g)).Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6 * 1e9;
            for (int g = 0; g < b.Faces.Count; g++)
            {
                int ng = r1.FaceMap[g];
                double before = SignedVol(doc, g), after = ng >= 0 ? SignedVol(d1, ng) : 0;
                if (Math.Abs(after - before) > 1e-6)
                    output.WriteLine($"  face {g}->{ng} {b.Faces[g].Surface.GetType().Name}{(ng >= 0 ? "" : "")}: vol-part {before:0.###} -> {after:0.###}  (d {after - before:0.###})");
            }
            foreach (int g in r1.NewFaces) output.WriteLine($"  new {g} {b1.Faces[g].Surface.GetType().Name} n {(b1.Faces[g].Surface is BrepPlane ? FaceGeometry.Normal(b1.Faces[g]).ToString() : "")}: vol-part {SignedVol(d1, g):0.###}");
        }
        foreach (int face in new[] { 24 })
        {
            var n = b.Faces[face].Surface is BrepPlane ? FaceGeometry.Normal(b.Faces[face]) : new Vec3(0, 0, 1);
            output.WriteLine($"face {face} n {n}");
            foreach (var c in b.Faces[face].Loops.SelectMany(l => l))
            {
                var e = b.Edges[c.Edge];
                var others = Enumerable.Range(0, b.Faces.Count).Where(g => g != face && b.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))).ToList();
                foreach (int g in others)
                {
                    string desc = b.Faces[g].Surface switch
                    {
                        BrepCylinder cy => $"cyl r {cy.Radius * 1000:0.##} axis {cy.Axis} origin {cy.Origin * 1000} rev {b.Faces[g].Reversed} outward·n {((b.Vertices[e.Start] - cy.Origin) - cy.Axis.Normalized() * (b.Vertices[e.Start] - cy.Origin).Dot(cy.Axis.Normalized())).Normalized().Dot(n) * (b.Faces[g].Reversed ? -1 : 1):0.00}",
                        BrepPlane => $"plane n {FaceGeometry.Normal(b.Faces[g])}",
                        _ => b.Faces[g].Surface.GetType().Name,
                    };
                    output.WriteLine($"  edge {c.Edge} {e.Curve.GetType().Name} {b.Vertices[Math.Max(0, e.Start)] * 1000} -> face {g}: {desc}");
                }
            }
        }
    }

    [Fact]
    public void Debug_sleutelrek_crossing()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var all = Enumerable.Range(0, doc.Bodies[0].Faces.Count).Select(f => new FaceRef(0, f)).ToHashSet();
        output.WriteLine($"before: {Intersections.FirstCrossing(doc, all)}");
        {
            var (d1, r1) = doc.PushPull(new FaceRef(0, 8), 3e-3);
            output.WriteLine($"face 8 +3: along {string.Join(",", r1.MovedAlong)} grown {string.Join(",", r1.GrownFaces)} new {r1.NewFaces.Count} faces {doc.Bodies[0].Faces.Count}->{d1.Bodies[0].Faces.Count}");
            try
            {
                var (d2, r2) = d1.PushPull(new FaceRef(0, r1.FaceMap[8]), -3e-3);
                output.WriteLine($"  back: along {string.Join(",", r2.MovedAlong)} grown {string.Join(",", r2.GrownFaces)} new {r2.NewFaces.Count} trimmed {r2.TrimmedFaces.Count} faces ->{d2.Bodies[0].Faces.Count}, dV {(d2.Volume() - doc.Volume()) * 1e9:0.000}");
            }
            catch (PushPullException e) { output.WriteLine("  back: " + e.Message); }
            var b1 = d1.Bodies[0]; int mf = r1.FaceMap[8];
            foreach (var c in b1.Faces[mf].Loops.SelectMany(l => l))
            {
                var others = Enumerable.Range(0, b1.Faces.Count).Where(g => g != mf && b1.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))).ToList();
                output.WriteLine($"  edge {c.Edge} {b1.Edges[c.Edge].Curve.GetType().Name} -> {string.Join(",", others.Select(g => g + ":" + b1.Faces[g].Surface.GetType().Name + (b1.Faces[g].Surface is BrepCylinder cc ? $"(axis {cc.Axis.X:0},{cc.Axis.Y:0},{cc.Axis.Z:0} rev {b1.Faces[g].Reversed})" : "")))}");
            }
        }
        foreach (int face in new[] { 29, 17, 8 })
        {
            var (d, r) = doc.PushPull(new FaceRef(0, face), -1e-3, checkCrossing: false);
            var changed = new[] { r.FaceMap[face] }.Concat(r.NewFaces).Concat(r.GrownFaces).Concat(r.TrimmedFaces).Concat(r.MovedAlong).Select(x => new FaceRef(0, x)).ToHashSet();
            var hit = Intersections.FirstCrossing(d, changed);
            string Desc(FaceRef f) { var s = d.Bodies[0].Faces[f.Face].Surface; return $"{f.Face} {s.GetType().Name}{(s is BrepCylinder c ? $" r {c.Radius * 1000:0.#} axis {c.Axis}" : s is BrepPlane ? $" n {FaceGeometry.Normal(d.Bodies[0].Faces[f.Face])}" : "")}"; }
            output.WriteLine($"face {face} -1: along {string.Join(",", r.MovedAlong)}, crossing {(hit is { } h ? Desc(h.A) + "  x  " + Desc(h.B) : "none")}");
        }
    }

    [Fact]
    public void Debug_sleutelrek_second_pull()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
        var b = doc.Bodies[0];
        // the planar faces of the third hook (x 21..25) next to a rounding
        var hook = Enumerable.Range(0, b.Faces.Count).Where(f => b.Faces[f].Surface is BrepPlane
            && b.Faces[f].Loops[0].All(c => b.Edges[c.Edge].Start < 0 || b.Vertices[b.Edges[c.Edge].Start].X is > 20e-3 and < 26e-3)
            && b.Faces[f].Loops.SelectMany(l => l).Any(c => Enumerable.Range(0, b.Faces.Count).Any(g => g != f && b.Faces[g].Surface is BrepCylinder && b.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))))).ToList();
        output.WriteLine($"hook faces: {string.Join(",", hook.Select(f => $"{f}{FaceGeometry.Normal(b.Faces[f])}"))}");
        void Tip(SketchDocument dd, string title)
        {
            var bb = dd.Bodies[0];
            output.WriteLine(title);
            for (int g = 0; g < bb.Faces.Count; g++)
            {
                var vs = bb.Faces[g].Loops.SelectMany(l => l).Select(c => bb.Edges[c.Edge]).Where(e => e.Start >= 0).SelectMany(e => new[] { bb.Vertices[e.Start], bb.Vertices[e.End] }).ToList();
                if (vs.Count == 0 || vs.Any(v => v.X < 18e-3 || v.X > 28e-3 || v.Y < -33e-3 || v.Z < 20e-3)) continue;
                string s = bb.Faces[g].Surface switch { BrepCylinder c => $"cyl r {c.Radius * 1e3:0.#} axis {c.Axis.X:0},{c.Axis.Y:0},{c.Axis.Z:0} centre y {c.Origin.Y * 1e3:0.##} z {c.Origin.Z * 1e3:0.##}", BrepPlane => $"plane n {FaceGeometry.Normal(bb.Faces[g]).X:0},{FaceGeometry.Normal(bb.Faces[g]).Y:0},{FaceGeometry.Normal(bb.Faces[g]).Z:0}", var o => o.GetType().Name };
                output.WriteLine($"  {g}: {s}  x {vs.Min(v => v.X) * 1e3:0.##}..{vs.Max(v => v.X) * 1e3:0.##} y {vs.Min(v => v.Y) * 1e3:0.##}..{vs.Max(v => v.Y) * 1e3:0.##} z {vs.Min(v => v.Z) * 1e3:0.##}..{vs.Max(v => v.Z) * 1e3:0.##}");
            }
        }
        Tip(doc, "tip before:");
        Tip(doc.PushPull(new FaceRef(0, 29), 2e-3).Doc, "tip after 29 +2:");
        var alone = new Dictionary<int, int>();
        foreach (int f in hook)
            try { alone[f] = doc.PushPull(new FaceRef(0, f), 2e-3).Result.MovedAlong.Count; } catch (PushPullException) { alone[f] = -1; }
        foreach (int f1 in hook)
        {
            if (alone[f1] < 0) continue;
            var (d1, r1) = doc.PushPull(new FaceRef(0, f1), 2e-3);
            foreach (int f2 in hook.Where(x => x != f1))
            {
                int g2 = r1.FaceMap[f2];
                if (g2 < 0) continue;
                string res;
                try
                {
                    var (d2, r2) = d1.PushPull(new FaceRef(0, g2), 2e-3);
                    res = $"along {r2.MovedAlong.Count} new {r2.NewFaces.Count}";
                    var (d3, _) = d2.PushPull(new FaceRef(0, r2.FaceMap[g2]), -2e-3);
                    double dv = (d3.Volume() - d1.Volume()) * 1e9;
                    foreach (var x in d2.Bodies.Concat(d3.Bodies)) if (XtBrepBuilder.TopologyError(x) is { } te) res += " TOPOLOGY " + te;
                    if (Math.Abs(dv) > 0.05) res += $" NOT BACK {dv:0.000}";
                    output.WriteLine($"first {f1} then {f2}: {res}, dV {(d2.Volume() - d1.Volume()) * 1e9:0.00}");
                }
                catch (PushPullException e) { res = "refused: " + e.Message; }
                if (!res.StartsWith($"along {alone[f2]} "))
                    output.WriteLine($"first {f1} then {f2}: {res} (alone: along {alone[f2]})");
            }
        }
    }

    [Fact]
    public void Debug_xt_nodes()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        foreach (var file in new[] { "yubikey.rsdocx", "sleutelrek.rsdocx" })
        {
            var pkg = RsDocx.Core.Package.RsDocxPackage.Open(TestFiles.Converter(file));
            var xt = XtReader.Read(pkg.GetPart(RsDocx.Core.RsDocxComposer.GeometryPart));
            var map = xt.ByIndex();
            var root = xt.Nodes[0];
            output.WriteLine($"{file}: root type {root.NodeType}, entries {(root.Has("entries") ? string.Join(",", root.Array("entries").Take(root.Int("n_entries"))) : "-")}");
            foreach (var bn in xt.Nodes.Where(n => n.NodeType == XtNodeTypes.Body))
                output.WriteLine($"  body #{bn.Index} name {XtTopology.NameOf(map, bn)}");
            foreach (var bn in xt.Nodes.Where(n => n.NodeType == XtNodeTypes.Body))
                output.WriteLine($"  body #{bn.Index} type {bn.Byte("body_type")} faces {XtTopology.Chain(map, XtTopology.Chain(map, bn.Ptr("region"), "next").Select(r => r.Ptr("shell")).FirstOrDefault(s => s != 0), "next").Count()}");
            foreach (var type in xt.Nodes.Select(n => n.NodeType).Distinct().Order())
                output.WriteLine($"  type {type}: {xt.Nodes.Count(n => n.NodeType == type)}x  fields {string.Join(" ", xt.Nodes.First(n => n.NodeType == type).Schema.Fields.Select(f => f.Name))}");
            foreach (var n38 in xt.Nodes.Where(n => n.NodeType == 38).Take(1))
                for (int i = 0; i < n38.Schema.Fields.Count; i++)
                {
                    var v = n38.Values[i];
                    output.WriteLine($"    38.{n38.Schema.Fields[i].Name} = {(v is object[] arr ? "[" + string.Join(",", arr.Take(12)) + "]" : v is double[] dv ? "(" + string.Join(",", dv) + ")" : v)}{(v is int p && map.TryGetValue(p, out var pn) ? $" -> type {pn.NodeType}" : "")}");
                }
        }
    }

    /// <summary>A session log of the user replayed (PPM_REPLAY = the log file).</summary>
    [Fact]
    public void Debug_replay_session()
    {
        if (Environment.GetEnvironmentVariable("PPM_REPLAY") is not { Length: > 0 } log) return;
        SketchDocument? doc = null;
        var undo = new Stack<SketchDocument>(); var redo = new Stack<SketchDocument>();
        foreach (var line in File.ReadLines(log))
        {
            if (line.EndsWith("] undo") && undo.Count > 0) { redo.Push(doc!); doc = undo.Pop(); output.WriteLine("undo"); continue; }
            if (line.EndsWith("] redo") && redo.Count > 0) { undo.Push(doc!); doc = redo.Pop(); output.WriteLine("redo"); continue; }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\] (open (.+?): \d+ faces.*|pushpull face (\d+) (-?[\d,\.]+) mm: (.*))$");
            if (!m.Success) continue;
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            {
                string path = m.Groups[2].Value;
                doc = path.EndsWith(".rsdocx") ? SketchDocument.FromRsDocx(path)
                    : SketchDocument.FromMesh(StlReader.Read(path), new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 });
                output.WriteLine($"open {path}: {doc.Bodies.Sum(b => b.Faces.Count)} faces");
                continue;
            }
            int face = int.Parse(m.Groups[3].Value);
            double mm = double.Parse(m.Groups[4].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var b = doc!.Bodies[0];
            var f = b.Faces[face];
            string desc = f.Surface is BrepPlane ? $"n {FaceGeometry.Normal(f)}" : f.Surface.GetType().Name;
            var pts = f.Loops[0].Select(c => b.Edges[c.Edge]).Where(e => e.Start >= 0).Select(e => b.Vertices[e.Start]).ToList();
            output.WriteLine($"face {face} ({desc}, x {pts.Min(p => p.X) * 1e3:0.##}..{pts.Max(p => p.X) * 1e3:0.##} y {pts.Min(p => p.Y) * 1e3:0.##}..{pts.Max(p => p.Y) * 1e3:0.##} z {pts.Min(p => p.Z) * 1e3:0.##}..{pts.Max(p => p.Z) * 1e3:0.##}) {mm} mm  [log: {m.Groups[5].Value}]");
            try
            {
                var (d, r) = doc.PushPull(new FaceRef(0, face), mm / 1000);
                var fresh = SketchDocument.FromBodies(d.Bodies);
                output.WriteLine($"   ok: faces {d.Bodies[0].Faces.Count}, along {r.MovedAlong.Count}, trimmed {r.TrimmedFaces.Count}, new {r.NewFaces.Count}, volume {d.Volume() * 1e9:0.###} (fresh {fresh.Volume() * 1e9:0.###}), topology {XtBrepBuilder.TopologyError(d.Bodies[0]) ?? "ok"}");
                if (Environment.GetEnvironmentVariable("PPM_REPLAY_FACES") == "1")
                {
                    var bd = d.Bodies[0];
                    for (int g = 0; g < bd.Faces.Count; g++)
                        if (bd.Faces[g].Surface is BrepPlane && bd.Faces[g].Loops.SelectMany(l => l).Select(c => bd.Edges[c.Edge]).Where(e => e.Start >= 0).Any(e => bd.Vertices[e.Start].Z > 25.9e-3 && bd.Vertices[e.Start].X < -6.9e-3 && bd.Vertices[e.Start].X > -12.6e-3 && bd.Vertices[e.Start].Y > -28.3e-3))
                        {
                            var nn = FaceGeometry.Normal(bd.Faces[g]);
                            output.WriteLine($"     face {g} n {nn.X:0},{nn.Y:0},{nn.Z:0}: " + string.Join(" | ", bd.Faces[g].Loops.Select(l => string.Join(" ", FaceGeometry.LoopPoints(bd, l).Select(p => $"({p.X * 1e3:0.##},{p.Y * 1e3:0.##},{p.Z * 1e3:0.##})")))));
                        }
                }
                undo.Push(doc); redo.Clear();
                doc = d;
            }
            catch (PushPullException e) { output.WriteLine("   refused: " + e.Message); }
        }
    }

    [Fact]
    public void Debug_sleutelrek_183()
    {
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") is not ("1" or "2")) return;
        var doc = SketchDocument.FromMesh(StlReader.Read(TestFiles.Converter("sleutelrek.stl")), new MeshOptions { MergeCoplanar = true });
        var b = doc.Bodies[0];
        var f = b.Faces[183];
        output.WriteLine($"face 183 normal {FaceGeometry.Normal(f)} loops {f.Loops.Count} sizes {string.Join(",", f.Loops.Select(l => l.Count))}");
        foreach (var c in f.Loops[0])
        {
            var e = b.Edges[c.Edge];
            var other = Enumerable.Range(0, b.Faces.Count).Where(g => g != 183 && b.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge))).ToList();
            output.WriteLine($" edge {c.Edge} {e.Start}->{e.End} {b.Vertices[e.Start]} | across {string.Join(",", other.Select(g => $"{g}:{b.Faces[g].Surface.GetType().Name}:{(b.Faces[g].Surface is BrepPlane ? FaceGeometry.Normal(b.Faces[g]).ToString() : "")}"))}");
        }
        var (d1, r1) = doc.PushPull(new FaceRef(0, 183), 0.5e-3);
        output.WriteLine($"pull ok: new {r1.NewFaces.Count}, grown {string.Join(",", r1.GrownFaces)}, moved -> {r1.FaceMap[183]}");
        try { d1.PushPull(new FaceRef(0, r1.FaceMap[183]), -0.5e-3); output.WriteLine("push ok"); }
        catch (PushPullException e) { output.WriteLine("push: " + e.Message); }
    }
}

/// <summary>Loose lines (sketch curves) in DesignSpark files.</summary>
public class SketchLineTests
{
    private static string TestData(string name) => TestFiles.Converter(name);

    [Fact]
    public void Loose_lines_are_read_from_a_DesignSpark_file()
    {
        // kubus.rsdocx (14 October 2026): four lines, one of them a construction line, and an arc
        var doc = SketchDocument.FromRsDocx(TestData("kubus.rsdocx"), out _);
        Assert.Equal(5, doc.Lines.Count);
        Assert.Single(doc.Lines, l => l.Construction);
        var arc = Assert.Single(doc.Lines, l => l.IsArc);
        Assert.Equal(arc.Circle!.Radius, (arc.End - arc.Centre!.Value).Length, 9);
        var first = doc.Lines[0];
        Assert.True((first.Start - new Vec3(-0.01, 0.008, 0.05)).Length < 1e-12);
        Assert.Equal(0.035355339059327383, (first.End - first.Start).Length, 12);
    }

    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01))]);
    private static FaceRef Top(SketchDocument d) => new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).First(f => FaceGeometry.Normal(d.Bodies[0].Faces[f]).Z > 0.999));
    private static Vec3 T(double x, double y) => new(x * 1e-3, y * 1e-3, 0.01);
    private static double Area(SketchDocument d, FaceRef f) => d.TrianglesOf(f).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) * 1e6;

    [Fact]
    public void A_line_onto_an_earlier_loose_line_closes_a_face()
    {
        // (as drawn: a line from the back edge into the face, then one from the right edge onto it)
        var doc = Cube().AddLines([new SketchLine(T(5, 10), T(5, 4)), new SketchLine(T(10, 6), T(5, 6))]);
        var (d, made) = doc.CloseLooseLines(Top(doc));
        var f = Assert.Single(made);
        Assert.Equal(20, Area(d, f), 6);
        // the rest of the first line stays loose
        var rest = Assert.Single(d.Lines);
        Assert.True(((rest.Start - T(5, 6)).Length < 1e-12 && (rest.End - T(5, 4)).Length < 1e-12) || ((rest.End - T(5, 6)).Length < 1e-12 && (rest.Start - T(5, 4)).Length < 1e-12));
        Assert.Equal(1000, d.Volume() * 1e9, 6);
        // on to the front edge as well: the strip left of it splits off too
        var (d2, made2) = d.AddLines([new SketchLine(T(5, 4), T(5, 0))]).CloseLooseLines(Top(d));
        Assert.Single(made2);
        Assert.Empty(d2.Lines);
        Assert.Equal(3, d2.Bodies[0].Faces.Count(g => g.Surface is BrepPlane && FaceGeometry.Normal(g).Z > 0.999)); // (right top, left, right bottom)
    }

    [Fact]
    public void Four_loose_lines_in_a_ring_make_a_face()
    {
        var doc = Cube().AddLines([new SketchLine(T(2, 2), T(6, 2)), new SketchLine(T(6, 2), T(6, 5)), new SketchLine(T(6, 5), T(2, 5)), new SketchLine(T(2, 5), T(2, 2))]);
        var (d, made) = doc.CloseLooseLines(Top(doc));
        Assert.Equal(12, Area(d, Assert.Single(made)), 6);
        Assert.Empty(d.Lines);
        // crossing lines (a # inside the face): the square in the middle
        var hash = Cube().AddLines([new SketchLine(T(1, 3), T(9, 3)), new SketchLine(T(1, 6), T(9, 6)), new SketchLine(T(3, 1), T(3, 9)), new SketchLine(T(7, 1), T(7, 9))]);
        var (dh, mh) = hash.CloseLooseLines(Top(hash));
        Assert.Equal(12, Area(dh, Assert.Single(mh)), 6);
        Assert.Equal(8, dh.Lines.Count);
        // lines that close nothing stay as they are
        var open = Cube().AddLines([new SketchLine(T(2, 2), T(6, 2)), new SketchLine(T(6, 2), T(6, 5))]);
        var (dopen, mopen) = open.CloseLooseLines(Top(open));
        Assert.Empty(mopen);
        Assert.Same(open, dopen);
    }

    /// <summary>A loose arc from a to b round c (counter-clockwise seen from above).</summary>
    private static SketchLine Arc(Vec3 a, Vec3 b, Vec3 c) => new(a, b, c, new Vec3(0, 0, 1));

    [Fact]
    public void Loose_arcs_close_faces_with_lines()
    {
        // a half circle r 3 over a line: the half disc
        var doc = Cube().AddLines([new SketchLine(T(2, 2), T(8, 2)), Arc(T(8, 2), T(2, 2), T(5, 2))]);
        var (d, made) = doc.CloseLooseLines(Top(doc));
        var f = Assert.Single(made);
        Assert.Equal(Math.PI * 9 / 2, Area(d, f), 1);
        Assert.Contains(d.Bodies[0].Faces[f.Face].Loops[0], c => d.Bodies[0].Edges[c.Edge].Curve is BrepCircle);
        Assert.Empty(d.Lines);
        var (d2, _) = d.PushPull(f, 0.002);
        Assert.Equal(1000 + Math.PI * 9 / 2 * 2, d2.Volume() * 1e9, 0);
        d2.ToRsDocx();

        // an arc starting on the front edge, on to a line to the right edge (as the user drew it): the corner splits off
        // (a quarter circle r 4 round (3, 4) from (3, 0) to (7, 4), then (7, 4) → (10, 4))
        var e = Cube().AddLines([Arc(T(3, 0), T(7, 4), T(3, 4)), new SketchLine(T(7, 4), T(10, 4))]);
        var (de, me) = e.CloseLooseLines(Top(e));
        var fe = Assert.Single(me);
        double corner = 4 * 4 - Math.PI * 16 / 4 + 3 * 4, rest = 100 - corner;
        Assert.True(Math.Abs(Area(de, fe) - corner) < 0.1 || Math.Abs(Area(de, fe) - rest) < 0.1, $"area {Area(de, fe)}");
        de.ToRsDocx();

        // a line across the face crossing an arc: first the line splits the face, then the cap of the arc above it
        // (r 4 round (5, 5) from (1, 5) to (9, 5) over the top; the line at y 7)
        var x = Cube().AddLines([Arc(T(9, 5), T(1, 5), T(5, 5)), new SketchLine(T(0, 7), T(10, 7))]);
        var (dx, mx) = x.CloseLooseLines(Top(x));
        Assert.Equal(2, mx.Count);
        Assert.Contains(mx, g => Math.Abs(Area(dx, g) - (16 * Math.Acos(0.5) - 2 * Math.Sqrt(12))) < 0.1);
        Assert.Equal(2, dx.Lines.Count); // (the arc's two feet below the line)
        dx.ToRsDocx();
    }

    [Fact]
    public void A_line_touching_a_hole_in_one_point_divides_the_face()
    {
        // a quarter pie round (4, 5) r 2 (a hole in the top), then a V from the back edge through its point and back
        var doc = Cube();
        Vec3 At(double deg) => T(4, 5) + new Vec3(Math.Cos(deg * Math.PI / 180), Math.Sin(deg * Math.PI / 180), 0) * 0.002;
        var (d0, pie) = doc.DrawPie(Top(doc), T(4, 5), At(180), At(270), At(225));
        var (d1, f) = d0.DrawPath(Top(d0), [T(2, 10), T(4, 5), T(8, 10)]);
        // the V (area 15) and the rest (100 − 15 − π), the pie a face of its own still
        var tops = Enumerable.Range(0, d1.Bodies[0].Faces.Count).Where(g => FaceGeometry.Normal(d1.Bodies[0].Faces[g]).Z > 0.999).Select(g => Area(d1, new FaceRef(0, g))).OrderBy(a => a).ToList();
        Assert.Equal(3, tops.Count);
        Assert.Equal(Math.PI, tops[0], 1);
        Assert.Equal(15, tops[1], 6);
        Assert.Equal(85 - Math.PI, tops[2], 1);
        d1.ToRsDocx(); // (the geometry validated)
        var (d2, _) = d1.PushPull(f, 0.001);
        Assert.Equal(1015, d2.Volume() * 1e9, 3);
        d2.ToRsDocx();
        // the same as loose lines
        var loose = d0.AddLines([new SketchLine(T(2, 10), T(4, 5)), new SketchLine(T(4, 5), T(8, 10))]);
        var (d3, made) = loose.CloseLooseLines(Top(loose));
        Assert.Single(made);
        Assert.Empty(d3.Lines);
    }

    [Fact]
    public void Loose_lines_reaching_over_the_edge_are_cut_on_it()
    {
        // a line from beyond the back edge to beyond the front edge (as drawn past the face): the face splits, the ends
        // outside stay loose
        var doc = Cube().AddLines([new SketchLine(T(4, 12), T(4, -2))]);
        var (d, made) = doc.CloseLooseLines(Top(doc));
        Assert.Equal(60, Area(d, Assert.Single(made)), 6);
        Assert.Equal(2, d.Lines.Count);
        Assert.All(d.Lines, l => Assert.Equal(0.002, (l.End - l.Start).Length, 9));
    }

    [Fact]
    public void Loose_lines_are_saved_and_read_back()
    {
        var doc = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01))]);
        var c = new Vec3(0.005, 0.005, 0.01);
        var lines = new List<SketchLine>
        {
            new(new Vec3(0, 0, 0.01), new Vec3(0.004, 0.003, 0.01)),
            new(c + new Vec3(0.002, 0, 0), c + new Vec3(0, 0.002, 0), c, new Vec3(0, 0, 1)),
            new(new Vec3(0, 0, 0), new Vec3(0, 0, 0.02), Construction: true),
        };
        var pkg = doc.AddLines(lines).ToRsDocx();
        var back = RsDocxSketch.Read(pkg);
        Assert.Equal(3, back.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.True((back[i].Start - lines[i].Start).Length < 1e-12 && (back[i].End - lines[i].End).Length < 1e-12);
            Assert.Equal(lines[i].IsArc, back[i].IsArc);
            Assert.Equal(lines[i].Construction, back[i].Construction);
        }
        // the monikers are new ones, the counter past them
        var xml = pkg.GetXml("SpaceClaim/document.xml");
        var ids = xml.Descendants().Where(e => e.Attribute("Id") != null).Select(e => (string)e.Attribute("Id")!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // and a body change keeps them
        Assert.Equal(3, doc.AddLines(lines).Paint([new FaceRef(0, 0)], 0xFF00FF00).Lines.Count);
    }
}

/// <summary>Turning a face out of its body round an axis (Follow Me, DesignSpark's revolve).</summary>
public class RevolveTests
{
    private const double Mm = 1e-3;
    private static SketchDocument Cube() => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm))]);
    private static FaceRef Face(SketchDocument d, Vec3 n) => new(0, Enumerable.Range(0, d.Bodies[0].Faces.Count).First(f => d.Bodies[0].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(d.Bodies[0].Faces[f]).Dot(n) > 0.999));
    private static Vec3 P(double x, double y, double z) => new(x * Mm, y * Mm, z * Mm);
    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);
    private static void AssertSound(SketchDocument d) { foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b)); d.ToRsDocx(); }

    [Fact]
    public void The_side_of_a_cube_turned_round_its_bottom_edge_makes_a_quarter_cylinder()
    {
        var doc = Cube();
        var (d, cap) = doc.Revolve(Face(doc, new Vec3(1, 0, 0)), P(10, 0, 0), new Vec3(0, 1, 0), Math.PI / 2);
        AssertSound(d);
        Assert.Equal(1000 + Math.PI * 100 / 4 * 10, Mm3(d), 2.0); // (the round faces shown in triangles)
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepCylinder);
        // the sides of the sweep lie in the cube's front and back, the cap on its bottom: merged with them (6 faces)
        Assert.Equal(6, d.Bodies[0].Faces.Count);
        // the end cap lies on the ground, facing down
        Assert.True(FaceGeometry.Normal(d.Bodies[0].Faces[cap.Face]).Dot(new Vec3(0, 0, -1)) > 0.999);
        // the other way round the axis (given reversed) is the same: the face always turns out of the body
        var (d2, _) = doc.Revolve(Face(doc, new Vec3(1, 0, 0)), P(10, 0, 0), new Vec3(0, -1, 0), Math.PI / 2);
        Assert.Equal(Mm3(d), Mm3(d2), 3);
    }

    [Fact]
    public void The_top_turned_round_an_axis_beside_it_makes_part_of_a_ring()
    {
        // round a line along y at x 12 on the top's plane, 30°: (π/6)/2·(12² − 2²)·10
        var doc = Cube();
        var (d, _) = doc.Revolve(Face(doc, new Vec3(0, 0, 1)), P(12, 0, 10), new Vec3(0, 1, 0), Math.PI / 6);
        AssertSound(d);
        Assert.Equal(1000 + Math.PI / 6 / 2 * (144 - 4) * 10, Mm3(d), 1.0);
        Assert.Equal(2, d.Bodies[0].Faces.Count(f => f.Surface is BrepCylinder));
    }

    [Fact]
    public void A_half_disc_turned_round_its_straight_edge_makes_part_of_a_ball()
    {
        // a half circle r 2 round (5, 0) on the top, from the front edge; turned round that edge 90°: a quarter ball
        var doc = Cube();
        var (d0, half) = doc.DrawArc(Face(doc, new Vec3(0, 0, 1)), P(3, 0, 10), P(7, 0, 10), P(5, 2, 10));
        var piece = new[] { half, Face(d0, new Vec3(0, 0, 1)) }.First(f => d0.Bodies[0].Faces[f.Face].Loops[0].Count == 2);
        var (d, _) = d0.Revolve(piece, P(0, 0, 10), new Vec3(1, 0, 0), Math.PI / 2);
        AssertSound(d);
        Assert.Equal(1000 + 4.0 / 3 * Math.PI * 8 / 4, Mm3(d), 0.5);
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepSphere);
    }

    [Fact]
    public void A_face_across_the_axis_or_a_full_turn_is_refused()
    {
        var doc = Cube();
        Assert.Throws<PushPullException>(() => doc.Revolve(Face(doc, new Vec3(0, 0, 1)), P(5, 0, 10), new Vec3(0, 1, 0), 1));
        Assert.Throws<PushPullException>(() => doc.Revolve(Face(doc, new Vec3(1, 0, 0)), P(10, 0, 0), new Vec3(0, 1, 0), 2 * Math.PI));
        // turned on round into the body: it crosses
        Assert.Throws<PushPullException>(() => doc.Revolve(Face(doc, new Vec3(1, 0, 0)), P(10, 0, 0), new Vec3(0, 1, 0), Math.PI * 1.9));
    }
}

/// <summary>Two bodies made one (Union), exact, for flat faces.</summary>
public class UnionTests
{
    private const double Mm = 1e-3;
    private static BrepBody Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        BrepShapes.FromPolyhedron(Polyhedron.Box(x0 * Mm, y0 * Mm, z0 * Mm, x1 * Mm, y1 * Mm, z1 * Mm));
    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);
    private static void AssertSound(SketchDocument d) { foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b)); d.ToRsDocx(); }

    [Fact]
    public void Two_overlapping_cubes_become_one_body()
    {
        var doc = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(5, 5, 5, 15, 15, 15)]);
        var d = doc.Union(0, 1);
        Assert.Single(d.Bodies);
        AssertSound(d);
        Assert.Equal(1000 + 1000 - 125, Mm3(d), 6);
        Assert.Equal(12, d.Bodies[0].Faces.Count);
    }

    [Fact]
    public void Two_stacked_cubes_become_one_block()
    {
        var doc = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(0, 0, 10, 10, 10, 20)]);
        var d = doc.Union(0, 1);
        AssertSound(d);
        Assert.Equal(2000, Mm3(d), 6);
        Assert.Equal(6, d.Bodies[0].Faces.Count);
    }

    [Fact]
    public void A_small_block_on_a_big_one_and_a_bar_through_a_cube()
    {
        var d = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(2, 2, 10, 8, 8, 15)]).Union(0, 1);
        AssertSound(d);
        Assert.Equal(1000 + 180, Mm3(d), 6);
        Assert.Equal(11, d.Bodies[0].Faces.Count);
        Assert.Contains(d.Bodies[0].Faces, f => f.Loops.Count == 2); // (the top with the small block's foot as a hole)
        var x = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(-5, 3, 3, 15, 7, 7)]).Union(0, 1);
        AssertSound(x);
        Assert.Equal(1000 + 2 * 5 * 16, Mm3(x), 6);
        // the other order gives the same
        var y = SketchDocument.FromBodies([Box(-5, 3, 3, 15, 7, 7), Box(0, 0, 0, 10, 10, 10)]).Union(0, 1);
        Assert.Equal(Mm3(x), Mm3(y), 6);
    }

    [Fact]
    public void A_turned_block_and_a_cube_with_a_hole()
    {
        // a block turned 30° round z through the middle of a cube: its volume is the two together less what they share
        var turned = BrepTransform.Rotated(Box(-10, 3, 2, 20, 7, 8), new BrepTransform.Turn(new Vec3(5 * Mm, 5 * Mm, 0), new Vec3(0, 0, 1), Math.PI / 6));
        var d = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), turned]).Union(0, 1);
        AssertSound(d);
        double v = Mm3(d);
        Assert.True(v > 1000 + 30 * 4 * 6 - 4 * 6 * 10 / Math.Cos(Math.PI / 6) - 1 && v < 1000 + 30 * 4 * 6, $"volume {v}");
        // a cube with a square hole through it, and a block across its top over the hole: the hole is covered
        var cube = SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10)]);
        var top = new FaceRef(0, Enumerable.Range(0, 6).First(f => FaceGeometry.Normal(cube.Bodies[0].Faces[f]).Z > 0.999));
        var (c1, hole) = cube.DrawRectangle(top, new Vec3(4 * Mm, 4 * Mm, 10 * Mm), new Vec3(6 * Mm, 6 * Mm, 10 * Mm), new Vec3(1, 0, 0));
        var (c2, _) = c1.PushPull(hole, -10 * Mm);
        Assert.Equal(1000 - 40, Mm3(c2), 6);
        var both = new SketchDocument { Bodies = [c2.Bodies[0], Box(-2, 3, 10, 12, 7, 12)], Curved = [c2.Curved[0], new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>()] };
        var e = both.Union(0, 1);
        AssertSound(e);
        Assert.Equal(1000 - 40 + 14 * 4 * 2, Mm3(e), 6);
    }

    [Fact]
    public void Apart_or_touching_in_an_edge_is_refused()
    {
        Assert.Throws<PushPullException>(() => SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(20, 0, 0, 30, 10, 10)]).Union(0, 1));
        Assert.Throws<PushPullException>(() => SketchDocument.FromBodies([Box(0, 0, 0, 10, 10, 10), Box(10, 10, 0, 20, 20, 10)]).Union(0, 1));
    }
}
