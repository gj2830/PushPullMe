using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Tests;

public class ComponentTests
{
    private const double Mm = 0.001;

    /// <summary>Blocks of 10 mm side by side along x, 20 mm apart.</summary>
    private static SketchDocument Blocks(int n) => SketchDocument.FromBodies(Enumerable.Range(0, n)
        .Select(i => BrepShapes.FromPolyhedron(Polyhedron.Box(i * 30 * Mm, 0, 0, (i * 30 + 10) * Mm, 10 * Mm, 10 * Mm))).ToList());

    private static FaceRef Side(SketchDocument d, int body, Vec3 n) =>
        new(body, Enumerable.Range(0, d.Bodies[body].Faces.Count).First(f => d.Bodies[body].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(d.Bodies[body].Faces[f]).Dot(n) > 0.999));

    /// <summary>A change made while group <paramref name="open"/> is open, as the editor commits it.</summary>
    private static SketchDocument Commit(SketchDocument before, SketchDocument after, int? open) =>
        SketchDocument.CarryMeta(before, after, open).SyncChanged(before);

    private static Vec3 Low(BrepBody b) => new(b.Vertices.Min(v => v.X), b.Vertices.Min(v => v.Y), b.Vertices.Min(v => v.Z));
    private static Vec3 High(BrepBody b) => new(b.Vertices.Max(v => v.X), b.Vertices.Max(v => v.Y), b.Vertices.Max(v => v.Z));

    private static void Near(Vec3 a, Vec3 b) => Assert.True((a - b).Length < 1e-9, $"{a} ≠ {b}");

    [Fact]
    public void A_pull_in_one_instance_is_made_in_the_moved_copy_too()
    {
        var (d, c) = Blocks(2).MakeComponent([GroupUnit.OfBody(0)]);
        Assert.Equal(1, d.Group(c)!.Component);
        Assert.Equal("Component 1", d.GroupName(c));
        var offset = new Vec3(0, 50 * Mm, 0);
        var (two, c2) = d.TransformGroup(c, (x, b, cp) => x.MoveBody(b, offset, cp), copy: true, Placement.Move(offset));
        Assert.Equal(1, two.Group(c2)!.Component);
        Assert.Equal(2, two.Instances(1).Count);
        // the top of the first instance 5 mm up, with it open
        var (pulled, _) = two.PushPull(Side(two, 0, new Vec3(0, 0, 1)), 5 * Mm);
        var after = Commit(two, pulled, c);
        Assert.Equal(3, after.Bodies.Count);
        int copy = after.Members(c2).Single();
        Assert.Equal(15 * Mm, High(after.Bodies[copy]).Z, 12);
        Near(Low(after.Bodies[0]) + offset, Low(after.Bodies[copy]));
        // the other block, outside the component, as it was
        Assert.Same(two.Bodies[1], after.Bodies[1]);
        Assert.Null(XtBrepBuilder.TopologyError(after.Bodies[copy]));
        // and the other way round: a pull in the copy goes back to the first
        var (back, _) = after.PushPull(Side(after, copy, new Vec3(1, 0, 0)), 2 * Mm);
        var again = Commit(after, back, c2);
        Assert.Equal(12 * Mm, High(again.Bodies[0]).X, 12);
    }

    [Fact]
    public void A_turned_mirrored_and_scaled_instance_follows_in_its_own_place()
    {
        var (d, c) = Blocks(1).MakeComponent([GroupUnit.OfBody(0)]);
        var centre = new Vec3(0, 0, 0); var up = new Vec3(0, 0, 1);
        var (turned, ct) = d.TransformGroup(c, (x, b, cp) => x.RotateBody(b, centre, up, Math.PI / 2, cp), copy: true, Placement.Turn(centre, up, Math.PI / 2));
        var (mirrored, cm) = turned.TransformGroup(c, (x, b, cp) => x.MirrorBody(b, new Vec3(-20 * Mm, 0, 0), new Vec3(1, 0, 0), cp), copy: true, Placement.Mirror(new Vec3(-20 * Mm, 0, 0), new Vec3(1, 0, 0)));
        var (scaled, cs) = mirrored.TransformGroup(c, (x, b, cp) => x.ScaleBody(b, centre, 2, null, cp), copy: true, Placement.Scaled(centre, 2));
        Assert.Equal(4, scaled.Instances(1).Count);
        // the first block pulled 10 mm along x (its +x side)
        var (pulled, _) = scaled.PushPull(Side(scaled, 0, new Vec3(1, 0, 0)), 10 * Mm);
        var after = Commit(scaled, pulled, c);
        // turned a quarter round z: now 20 mm along y
        var t = after.Bodies[after.Members(ct).Single()];
        Assert.Equal(20 * Mm, High(t).Y - Low(t).Y, 12);
        Assert.Equal(10 * Mm, High(t).X - Low(t).X, 12);
        // mirrored in x = -20 mm: from -60 to -40 mm
        var m = after.Bodies[after.Members(cm).Single()];
        Assert.Equal(-60 * Mm, Low(m).X, 12);
        Assert.Equal(-40 * Mm, High(m).X, 12);
        // twice as large: 40 × 20 × 20 mm
        var s = after.Bodies[after.Members(cs).Single()];
        Assert.Equal(40 * Mm, High(s).X - Low(s).X, 12);
        Assert.Equal(20 * Mm, High(s).Z - Low(s).Z, 12);
        foreach (var b in after.Bodies) Assert.Null(XtBrepBuilder.TopologyError(b));
        // (each a solid facing outward: 2 + 2 + 2 + 16 cubic cm)
        Assert.Equal((2 + 2 + 2 + 16) * 1e-6, after.Volume(), 12);
    }

    [Fact]
    public void A_body_added_in_an_instance_comes_in_the_others_and_one_taken_out_goes()
    {
        var (d, c) = Blocks(1).MakeComponent([GroupUnit.OfBody(0)]);
        var offset = new Vec3(0, 50 * Mm, 0);
        var (two, c2) = d.TransformGroup(c, (x, b, cp) => x.MoveBody(b, offset, cp), copy: true, Placement.Move(offset));
        // a copy of the block beside it, made with the first instance open: in both
        var (added, nb) = two.MoveBody(0, new Vec3(0, 0, 20 * Mm), copy: true);
        var more = Commit(two, added, c);
        Assert.Equal(4, more.Bodies.Count);
        Assert.Equal(c, more.GroupOf(nb));
        Assert.Equal(2, more.Members(c2).Count);
        Near(Low(more.Bodies[more.Members(c2)[1]]), new Vec3(0, 50 * Mm, 20 * Mm));
        // taken out again (in the copy): gone from both
        var less = Commit(more, more.RemoveBodies([more.Members(c2)[1]]), c2);
        Assert.Equal(2, less.Bodies.Count);
        Assert.Single(less.Members(c));
        Assert.Single(less.Members(c2));
    }

    [Fact]
    public void Paint_and_names_go_along_and_a_unique_instance_stays_apart()
    {
        var (d, c) = Blocks(1).MakeComponent([GroupUnit.OfBody(0)], "Poot");
        var offset = new Vec3(0, 50 * Mm, 0);
        var (two, c2) = d.TransformGroup(c, (x, b, cp) => x.MoveBody(b, offset, cp), copy: true, Placement.Move(offset));
        Assert.Equal("Poot", two.GroupName(c2));
        var painted = Commit(two, two.Paint([new FaceRef(0, 0)], 0xFFFF0000), c);
        Assert.Equal(0xFFFF0000, painted.ColorOf(new FaceRef(painted.Members(c2)[0], 0)));
        // painted while closed (nothing open): the same
        var closed = Commit(painted, painted.Paint([new FaceRef(painted.Members(c2)[0], 2)], 0xFF00FF00), null);
        Assert.Equal(0xFF00FF00, closed.ColorOf(new FaceRef(0, 2)));
        // a name given to one: both
        var renamed = painted.RenameGroup(c2, "Been");
        Assert.Equal("Been", renamed.GroupName(c));
        // made unique: a change in it stays in it
        var unique = renamed.MakeUnique(c2);
        Assert.NotEqual(unique.Group(c)!.Component, unique.Group(c2)!.Component);
        Assert.Throws<PushPullException>(() => unique.MakeUnique(c2));
        var (pulled, _) = unique.PushPull(Side(unique, 1, new Vec3(0, 0, 1)), 5 * Mm);
        var after = Commit(unique, pulled, c2);
        Assert.Equal(10 * Mm, High(after.Bodies[0]).Z, 12);
        Assert.Equal(15 * Mm, High(after.Bodies[1]).Z, 12);
    }

    [Fact]
    public void An_instance_cannot_be_scaled_along_one_axis()
    {
        var (d, c) = Blocks(1).MakeComponent([GroupUnit.OfBody(0)]);
        Assert.Throws<PushPullException>(() => d.TransformGroup(c, (x, b, cp) => x.ScaleBody(b, new Vec3(0, 0, 0), 2, new Vec3(1, 0, 0), cp), copy: false));
        // a group round it the same
        var (g, outer) = d.MakeGroup([GroupUnit.OfGroup(c)]);
        Assert.Throws<PushPullException>(() => g.TransformGroup(outer, (x, b, cp) => x.ScaleBody(b, new Vec3(0, 0, 0), 2, new Vec3(1, 0, 0), cp), copy: false));
    }

    [Fact]
    public void A_component_inside_a_component_follows_both_ways()
    {
        // a table (component) with a leg (component) in it, the table copied
        var (d, leg) = Blocks(2).MakeComponent([GroupUnit.OfBody(0)], "Poot");
        var (t, table) = d.MakeComponent([GroupUnit.OfGroup(leg), GroupUnit.OfBody(1)], "Tafel");
        var offset = new Vec3(0, 100 * Mm, 0);
        var (two, table2) = t.TransformGroup(table, (x, b, cp) => x.MoveBody(b, offset, cp), copy: true, Placement.Move(offset));
        int leg2 = two.Groups.Single(g => g.Parent == table2).Id;
        Assert.Equal(two.Group(leg)!.Component, two.Group(leg2)!.Component);
        // the leg pulled in the first table: in the second one's leg too
        var (pulled, _) = two.PushPull(Side(two, 0, new Vec3(0, 0, 1)), 5 * Mm);
        var after = Commit(two, pulled, leg);
        Assert.Equal(15 * Mm, High(after.Bodies[after.Members(leg2).Single()]).Z, 12);
        // the top of the second table pulled: in the first one
        int top2 = after.Members(table2).Single(b => after.GroupOf(b) == table2);
        var (raised, _) = after.PushPull(Side(after, top2, new Vec3(0, 0, 1)), 3 * Mm);
        var again = Commit(after, raised, table2);
        Assert.Equal(13 * Mm, High(again.Bodies[again.Members(table).Single(b => again.GroupOf(b) == table)]).Z, 12);
        Assert.Equal(4, again.Bodies.Count);
    }

    [Fact]
    public void Components_go_through_STEP_and_still_follow_each_other_after()
    {
        // a table (component) with a leg (component, twice) and a top, the table copied turned; a block scaled 2× as a
        // component of its own beside it
        var (d, leg) = Blocks(3).MakeComponent([GroupUnit.OfBody(0)], "Poot");
        var (d2, leg2) = d.TransformGroup(leg, (x, b, cp) => x.MoveBody(b, new Vec3(0, 20 * Mm, 0), cp), copy: true, Placement.Move(new Vec3(0, 20 * Mm, 0)));
        var (t, table) = d2.MakeComponent([GroupUnit.OfGroup(leg), GroupUnit.OfGroup(leg2), GroupUnit.OfBody(1)], "Tafel");
        var turn = Placement.Turn(new Vec3(0, 0, 0), new Vec3(0, 0, 1), Math.PI / 2).Then(Placement.Move(new Vec3(200 * Mm, 0, 0)));
        var (two, _) = t.TransformGroup(table, (x, b, cp) =>
        {
            var (r, rb) = x.RotateBody(b, new Vec3(0, 0, 0), new Vec3(0, 0, 1), Math.PI / 2, cp);
            return r.MoveBody(rb, new Vec3(200 * Mm, 0, 0), false);
        }, copy: true, turn);
        var (big, blk) = two.MakeComponent([GroupUnit.OfBody(2)], "Blok");
        var (scaled, _) = big.TransformGroup(blk, (x, b, cp) => x.ScaleBody(b, new Vec3(0, 0, 0), 2, null, cp), copy: true, Placement.Scaled(new Vec3(0, 0, 0), 2));
        string path = Path.Combine(Path.GetTempPath(), $"ppm_componenten_{Guid.NewGuid():N}.step");
        try
        {
            string text = scaled.ToStep("componenten.step");
            File.WriteAllText(path, text);
            // the leg written once, the table once; the blocks each (one larger: not as one part)
            Assert.Equal(1 + 1 + 2, text.Split("MANIFOLD_SOLID_BREP(").Length - 1);
            var back = SketchDocument.FromStep(path);
            Assert.Equal(scaled.Bodies.Count, back.Bodies.Count);
            Assert.Equal(scaled.Volume(), back.Volume(), 12);
            var tables = back.Groups.Where(g => g.Name == "Tafel").ToList();
            var legs = back.Groups.Where(g => g.Name == "Poot").ToList();
            Assert.Equal(2, tables.Count);
            Assert.Equal(4, legs.Count);
            Assert.Single(tables.Select(g => g.Component).Distinct());
            Assert.Single(legs.Select(g => g.Component).Distinct());
            Assert.All(back.Groups.Where(g => g.Name == "Blok"), g => Assert.Null(g.Component));
            // a leg pulled in the file read back: all four 5 mm higher
            int one = legs[0].Id;
            int body = back.Members(one).Single();
            var (pulled, _) = back.PushPull(Side(back, body, new Vec3(0, 0, 1)), 5 * Mm);
            var after = Commit(back, pulled, one);
            Assert.All(legs, l => Assert.Equal(15 * Mm, High(after.Bodies[after.Members(l.Id).Single()]).Z, 12));
            Assert.Equal(back.Volume() + 4 * 0.5e-6, after.Volume(), 12);
        }
        finally { File.Delete(path); }
    }
}
