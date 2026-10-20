using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Tests;

/// <summary>Follow Me along open paths of loose lines, with arcs in the profile or the path, and round a circle.</summary>
public class SweepPathTests
{
    private const double Mm = 1e-3;
    private static Vec3 P(double x, double y, double z) => new(x * Mm, y * Mm, z * Mm);
    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);
    private static void AssertSound(SketchDocument d) { foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b)); d.ToRsDocx(); }

    /// <summary>A circle of radius r round c as a face without thickness, facing along n.</summary>
    private static BrepBody Disc(Vec3 c, double r, Vec3 n, Vec3 x) =>
        Sheets.Face([c + x * (r * Mm), c + x * (r * Mm)], [new BrepCircle(c, n, x, r * Mm)], n);

    [Fact]
    public void A_square_followed_along_an_open_L_of_loose_lines_is_mitred_at_the_corner_and_square_at_the_ends()
    {
        // the square 2 × 2 standing on the start of the path (0,0,0) → (10,0,0) → (10,10,0), in the plane x 0, centred
        // on it sideways: its middle goes 20 along the path (Pappus)
        var sq = Sheets.Face([P(0, -1, 0), P(0, 1, 0), P(0, 1, 2), P(0, -1, 2), P(0, -1, 0)], [new BrepLine(), new BrepLine(), new BrepLine(), new BrepLine()], new Vec3(1, 0, 0));
        var doc = SketchDocument.FromBodies([sq]).AddLines([new SketchLine(P(0, 0, 0), P(10, 0, 0)), new SketchLine(P(10, 10, 0), P(10, 0, 0))]);
        var (d, made) = doc.FollowLines(new FaceRef(0, 0), 1);
        Assert.Single(d.Bodies);
        Assert.False(d.Bodies[0].Sheet);
        Assert.Equal(4 * 20, Mm3(d), 6);
        AssertSound(d);
    }

    [Fact]
    public void A_circle_round_the_end_of_a_line_makes_a_pipe()
    {
        // a circle of radius 1 round (0,0,0) in the plane x 0; the path a line 10 along red: a pipe π · 1² · 10
        var doc = SketchDocument.FromBodies([Disc(P(0, 0, 0), 1, new Vec3(1, 0, 0), new Vec3(0, 1, 0))]).AddLines([new SketchLine(P(0, 0, 0), P(10, 0, 0))]);
        var (d, _) = doc.FollowLines(new FaceRef(0, 0), 0);
        Assert.Single(d.Bodies);
        Assert.InRange(Mm3(d), Math.PI * 10 * 0.995, Math.PI * 10 * 1.000001);
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepCylinder);
        AssertSound(d);
    }

    [Fact]
    public void A_pipe_bends_round_an_arc_in_the_path()
    {
        // line 10 along red, a quarter circle of radius 5 round (10,5,0) to (15,5,0), line 10 along green: π · (20 + 5π/2)
        var doc = SketchDocument.FromBodies([Disc(P(0, 0, 0), 1, new Vec3(1, 0, 0), new Vec3(0, 1, 0))]).AddLines([
            new SketchLine(P(0, 0, 0), P(10, 0, 0)),
            new SketchLine(P(10, 0, 0), P(15, 5, 0), P(10, 5, 0), new Vec3(0, 0, 1)),
            new SketchLine(P(15, 5, 0), P(15, 15, 0)),
        ]);
        var (d, _) = doc.FollowLines(new FaceRef(0, 0), 2);
        Assert.Single(d.Bodies);
        Assert.InRange(Mm3(d), Math.PI * (20 + 5 * Math.PI / 2) * 0.99, Math.PI * (20 + 5 * Math.PI / 2) * 1.000001);
        Assert.Contains(d.Bodies[0].Faces, f => f.Surface is BrepTorus);
        AssertSound(d);
    }

    [Fact]
    public void A_round_profile_round_a_sharp_corner_is_refused_and_the_model_stays()
    {
        var doc = SketchDocument.FromBodies([Disc(P(0, 0, 0), 1, new Vec3(1, 0, 0), new Vec3(0, 1, 0))]).AddLines([new SketchLine(P(0, 0, 0), P(10, 0, 0)), new SketchLine(P(10, 0, 0), P(10, 10, 0))]);
        Assert.Throws<PushPullException>(() => doc.FollowLines(new FaceRef(0, 0), 0));
    }

    [Fact]
    public void A_square_followed_round_the_top_of_a_cylinder_makes_a_ring_on_it()
    {
        // a cylinder of radius 10, 10 high; a square 2 × 2 on its top across its edge, in the plane y 0 (x 9..11,
        // z 10..12): a ring 4 · 2π · 10 (Pappus) standing on the top, joined with the cylinder
        var disc = SketchDocument.FromBodies([]).AddShape([P(10, 0, 0), P(10, 0, 0)], [new BrepCircle(P(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), 10 * Mm)], new Vec3(0, 0, 1), P(0, 0, 0)).Doc;
        var cyl = disc.PushPull(new FaceRef(0, 0), 10 * Mm).Doc;
        int top = Enumerable.Range(0, cyl.Bodies[0].Faces.Count).First(f => cyl.Bodies[0].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(cyl.Bodies[0].Faces[f]).Z > 0.999);
        double before = Mm3(cyl);
        var sq = Sheets.Face([P(9, 0, 10), P(11, 0, 10), P(11, 0, 12), P(9, 0, 12), P(9, 0, 10)], [new BrepLine(), new BrepLine(), new BrepLine(), new BrepLine()], new Vec3(0, -1, 0));
        var doc = SketchDocument.FromBodies([cyl.Bodies[0], sq]);
        var (d, _) = doc.FollowPath(new FaceRef(1, 0), new FaceRef(0, top));
        Assert.Single(d.Bodies);
        Assert.InRange(Mm3(d) - before, 4 * 2 * Math.PI * 10 * 0.995, 4 * 2 * Math.PI * 10 * 1.005);
        AssertSound(d);
    }
}
