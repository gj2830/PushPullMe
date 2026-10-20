using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Tests;

/// <summary>Pushing and pulling the end of a thread follows the helix, as DesignSpark does (issue #37).</summary>
public class ScrewEndTests
{
    private const double Mm = 1e-3;

    /// <summary>A thread as DesignSpark makes it: a trapezoid (base 1.2, crest 0.4 at radius 8.15..8.9) swept 3 turns
    /// with a pitch of 1.5 round the z axis.</summary>
    private static BrepBody Coil() => ThreadShapes.Coil(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), 8.15 * Mm, 8.9 * Mm, 1.2 * Mm, 0.4 * Mm, 1.5 * Mm, true, 3);

    /// <summary>The ends: flat faces whose screw is known.</summary>
    private static List<int> Ends(BrepBody b) => Enumerable.Range(0, b.Faces.Count).Where(f => ScrewEnd.Of(b, f) != null).ToList();

    /// <summary>The area of a flat face and how far its middle is from the z axis through (x, y).</summary>
    private static (double Area, double R) Profile(SketchDocument d, FaceRef f, Vec3 axisPoint, Vec3 axis)
    {
        double area = 0; var mid = new Vec3(0, 0, 0);
        foreach (var (a, b, c) in d.TrianglesOf(f)) { double w = (b - a).Cross(c - a).Length / 2; area += w; mid += (a + b + c) * (w / 3); }
        mid *= 1 / area;
        var r = mid - axisPoint; r -= axis * r.Dot(axis);
        return (area, r.Length);
    }

    private static void AssertSound(SketchDocument d)
    {
        foreach (var b in d.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
        d.ToRsDocx();
    }

    [Fact]
    public void Both_ends_of_a_thread_are_known_as_ends_and_a_block_face_is_not()
    {
        Assert.Equal(2, Ends(Coil()).Count);
        Assert.Empty(Ends(BrepShapes.FromPolyhedron(RsDocx.Core.Xt.Polyhedron.Box(0, 0, 0, 0.01, 0.01, 0.01))));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(40)]
    public void The_end_of_a_thread_pulled_or_pushed_follows_the_helix_and_back_gives_the_same(double mm)
    {
        var doc = SketchDocument.FromBodies([Coil()]);
        int end = Ends(doc.Bodies[0])[^1];
        var screw = ScrewEnd.Of(doc.Bodies[0], end)!;
        var (area, r) = Profile(doc, new FaceRef(0, end), screw.Point, screw.Out.Normalized());
        var (pulled, _) = doc.PushPull(new FaceRef(0, end), mm * Mm);
        AssertSound(pulled);
        // the volume: the profile's area times the way its middle goes round (the climb along the axis adds none)
        double angle = mm * Mm / screw.PerRadian;
        // (by the triangles shown: within a percent of the change)
        Assert.Equal(area * r * angle, pulled.Volume() - doc.Volume(), Math.Abs(area * r * angle) * 0.01);
        Assert.Equal(6, pulled.Bodies[0].Faces.Count);
        // the end face still an end, its screw the same; back as far: the thread as long as before
        Assert.NotNull(ScrewEnd.Of(pulled.Bodies[0], end));
        var (back, _) = pulled.PushPull(new FaceRef(0, end), -mm * Mm);
        Assert.Equal(doc.Volume(), back.Volume(), doc.Volume() * 1e-6);
    }

    [Fact]
    public void A_thread_cannot_be_pushed_back_further_than_it_is_long()
    {
        var doc = SketchDocument.FromBodies([Coil()]);
        int end = Ends(doc.Bodies[0])[^1];
        Assert.Throws<PushPullException>(() => doc.PushPull(new FaceRef(0, end), -200 * Mm));
    }

    [Fact]
    public void The_end_of_the_DesignSpark_thread_follows_the_helix_and_is_saved()
    {
        string path = TestFiles.Converter("helix.rsdocx");
        if (!File.Exists(path)) return;
        var doc = SketchDocument.FromRsDocx(path);
        var ends = Ends(doc.Bodies[0]);
        Assert.Equal(2, ends.Count);
        foreach (int end in ends)
        {
            var screw = ScrewEnd.Of(doc.Bodies[0], end)!;
            var (area, r) = Profile(doc, new FaceRef(0, end), screw.Point, screw.Out.Normalized());
            // (two results, both made anew, compared: the file's own helical faces are shown coarser)
            var (p5, _) = doc.PushPull(new FaceRef(0, end), 5 * Mm);
            var (p15, _) = doc.PushPull(new FaceRef(0, end), 15 * Mm);
            AssertSound(p15);
            double expected = area * r * (10 * Mm / screw.PerRadian);
            // (within 3 %: measured by the triangles shown, over 14 turns their error is a fair part of a change this small)
            Assert.Equal(expected, p15.Volume() - p5.Volume(), expected * 0.03);
        }
    }

    [Fact]
    public void With_ctrl_the_end_of_a_thread_goes_straight()
    {
        var doc = SketchDocument.FromBodies([Coil()]);
        int end = Ends(doc.Bodies[0])[^1];
        var (straight, _) = doc.PushPull(new FaceRef(0, end), 2 * Mm, straight: true);
        // (a straight prism of the profile on its end: a flat side face at every edge of it)
        Assert.Equal(6 + 4, straight.Bodies[0].Faces.Count);
    }
}
