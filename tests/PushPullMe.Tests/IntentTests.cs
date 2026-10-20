using System.Text;
using PushPullMe.Core;
using PushPullMe.Editor.Session;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Tests;

/// <summary>
/// Decisions the user took that must not change unnoticed (guarded: an assistant may not edit this file without asking).
/// Each test names the decision; when one fails, the code changed a decision: ask the user, do not adjust the test.
/// </summary>
public class IntentTests
{
    private const double Mm = 1e-3;
    private static Vec3 P(double x, double y, double z) => new(x * Mm, y * Mm, z * Mm);
    private static readonly Vec3 Up = new(0, 0, 1);
    private static SketchDocument Box(double x, double y, double z) => SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, x * Mm, y * Mm, z * Mm))]);
    private static double Mm3(SketchDocument d) => d.Volume() / (Mm * Mm * Mm);
    private static FaceRef Facing(SketchDocument d, int body, Vec3 n) =>
        new(body, Enumerable.Range(0, d.Bodies[body].Faces.Count).First(f => d.Bodies[body].Faces[f].Surface is BrepPlane && FaceGeometry.Normal(d.Bodies[body].Faces[f]).Dot(n) > 0.999));

    /// <summary>The top of a 20 × 20 × 10 block cut by both diagonals into four triangles.</summary>
    private static (SketchDocument Doc, Func<SketchDocument, double, double, FaceRef> Triangle) Diagonals()
    {
        var doc = Box(20, 20, 10);
        var top = Facing(doc, 0, Up);
        (doc, _) = doc.DrawPath(top, [P(0, 0, 10), P(20, 20, 10)]);
        (doc, _) = doc.DrawPath(top, [P(20, 0, 10), P(0, 20, 10)]);
        static FaceRef Triangle(SketchDocument d, double x, double y)
        {
            foreach (var (fr, t) in d.AllTriangles())
                if (d.Bodies[fr.Body].Faces[fr.Face].Surface is BrepPlane && FaceGeometry.Normal(d.Bodies[fr.Body].Faces[fr.Face]).Dot(Up) > 0.999
                    && t.All(q => Math.Abs(q.A.Z - 10 * Mm) < 1e-9) && t.Any(q => ((q.A + q.B + q.C) * (1.0 / 3) - P(x, y, 10)).Length < 4 * Mm))
                    return fr;
            throw new Exception($"no triangle at {x}, {y}");
        }
        return (doc, Triangle);
    }

    [Fact]
    public void Pieces_touching_only_along_an_edge_stay_separate_bodies_as_in_DesignSpark()
    {
        // (user, 16 Oct 2026: as DesignSpark makes it; no solid that touches itself along an edge)
        var (doc, triangle) = Diagonals();
        (doc, _) = doc.PushPull(triangle(doc, 10, 3), 10 * Mm);
        Assert.Single(doc.Bodies);
        (doc, _) = doc.PushPull(triangle(doc, 10, 17), 10 * Mm);
        Assert.Equal(2, doc.Bodies.Count);
    }

    [Fact]
    public void A_pulled_piece_touching_another_with_a_face_joins_it_unless_join_is_off()
    {
        // (user, 18 Oct 2026: "join" box bottom right, on by default, as DesignSpark's switch)
        var (doc, triangle) = Diagonals();
        (doc, _) = doc.PushPull(triangle(doc, 10, 3), 10 * Mm);
        (doc, _) = doc.PushPull(triangle(doc, 10, 17), 10 * Mm);
        var (apart, _) = doc.PushPull(triangle(doc, 3, 10), 10 * Mm, join: false);
        Assert.Equal(2, apart.Bodies.Count);
        (doc, _) = doc.PushPull(triangle(doc, 3, 10), 10 * Mm);
        Assert.Single(doc.Bodies);
    }

    [Fact]
    public void Parts_in_different_groups_neither_join_nor_combine()
    {
        // (user, 18 Oct 2026: groups as in SketchUp)
        var d = SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, 10 * Mm, 10 * Mm, 10 * Mm)),
            BrepShapes.FromPolyhedron(Polyhedron.Box(12 * Mm, 0, 0, 22 * Mm, 10 * Mm, 10 * Mm))]);
        var (apart, _) = d.MakeGroup([GroupUnit.OfBody(0)]);
        var (pulled, _) = apart.PushPull(Facing(apart, 0, new Vec3(1, 0, 0)), 2 * Mm);
        Assert.Equal(2, pulled.Bodies.Count);
        Assert.Throws<PushPullException>(() => apart.Combine(0, 1, Booleans.Op.Union));
    }

    [Fact]
    public void Pulling_the_end_of_a_turned_piece_follows_the_turn()
    {
        // (user, 17 Oct 2026: "DesignSpark simply follows the bend"; straight extending is a copy of the face)
        var doc = Box(10, 10, 10);
        var (d, cap) = doc.Revolve(Facing(doc, 0, Up), P(12, 0, 10), new Vec3(0, 1, 0), Math.PI / 6);
        Assert.NotNull(PushPullMe.Core.Revolve.EndAxis(d.Bodies[0], cap.Face));
        var (e, _) = d.PushPull(cap, 3 * Mm);
        // the ring turns on: still two cylinder faces, no straight piece added
        Assert.Equal(2, e.Bodies[0].Faces.Count(f => f.Surface is BrepCylinder));
    }

    [Fact]
    public void A_change_in_one_component_instance_comes_in_the_others_and_scaling_along_one_axis_is_refused()
    {
        // (user, 19 Oct 2026: components as in SketchUp; bodies stay in world coordinates)
        var (d, c) = Box(10, 10, 10).MakeComponent([GroupUnit.OfBody(0)]);
        var offset = new Vec3(0, 50 * Mm, 0);
        var (two, c2) = d.TransformGroup(c, (x, b, cp) => x.MoveBody(b, offset, cp), copy: true, Placement.Move(offset));
        var (pulled, _) = two.PushPull(Facing(two, 0, Up), 5 * Mm);
        var after = SketchDocument.CarryMeta(two, pulled, c).SyncChanged(two);
        Assert.Equal(15 * Mm, after.Bodies[after.Members(c2).Single()].Vertices.Max(v => v.Z), 12);
        Assert.Throws<PushPullException>(() => d.TransformGroup(c, (x, b, cp) => x.ScaleBody(b, new Vec3(0, 0, 0), 2, new Vec3(1, 0, 0), cp), copy: false));
    }

    [Fact]
    public void Pulled_and_pushed_back_to_the_same_place_gives_the_old_model()
    {
        // (the first promise of version 1, 11 Oct 2026)
        var doc = Box(10, 10, 10);
        var side = Facing(doc, 0, new Vec3(1, 0, 0));
        var (d1, _) = doc.PushPull(side, 2.5 * Mm);
        var (d2, _) = d1.PushPull(side, -2.5 * Mm);
        Assert.Equal(6, d2.Bodies[0].Faces.Count);
        Assert.Equal(1000, Mm3(d2), 6);
    }

    [Fact]
    public void A_refused_operation_leaves_the_model_as_it_was()
    {
        // (refuse with a clear message rather than repair; the document is immutable, so undo is keeping the old one)
        var doc = Box(10, 10, 10);
        var body = doc.Bodies[0];
        Assert.ThrowsAny<PushPullException>(() => doc.PushPull(Facing(doc, 0, Up), -15 * Mm));
        Assert.Same(body, doc.Bodies[0]);
        Assert.Equal(1000, Mm3(doc), 6);
    }

    [Fact]
    public void Standard_names_are_no_names_of_their_own()
    {
        // (user, 18 Oct 2026: names kept per body; a program's standard name is not one)
        foreach (var n in new[] { "Vaste vorm", "Vaste vorm 3", "Oppervlak 1", "Solid", "Surface 2", "body 7", " ", null })
            Assert.True(SketchDocument.IsStandardName(n), n);
        foreach (var n in new[] { "Blok", "Vaste vorm A", "Solids" })
            Assert.False(SketchDocument.IsStandardName(n), n);
    }

    [Fact]
    public void Glass_from_Revit_is_never_shown_less_than_a_quarter_opaque()
    {
        // (20 Oct 2026: Revit's glass is wholly clear (1); it must still be seen)
        Assert.Equal(255u, SketchDocument.Opacity(0));
        Assert.Equal(64u, SketchDocument.Opacity(1));
        Assert.Equal(64u, SketchDocument.Opacity(2));
    }

    [Fact]
    public void A_see_through_colour_is_written_in_rsdocx_as_A_R_G_B()
    {
        // (20 Oct 2026: as .NET's ColorConverter, which DesignSpark uses; an opaque colour stays "R, G, B")
        var doc = Box(10, 10, 10);
        doc = doc.Paint([new FaceRef(0, 0)], 0x5594B0CD);
        doc = doc.Paint([new FaceRef(0, 1)], 0xFF102030);
        var pkg = doc.ToRsDocx();
        string xml = string.Concat(pkg.Entries.Where(e => e.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(e => Encoding.UTF8.GetString(pkg.GetPart(e.Path))));
        Assert.Contains("85, 148, 176, 205", xml);
        Assert.Contains("16, 32, 48", xml);
        Assert.DoesNotContain("255, 16, 32, 48", xml);
    }

    [Fact]
    public void Storeys_come_from_the_main_model_numbered_from_the_one_height_0_is_on()
    {
        // (user, 20 Oct 2026: "storey -1, 0, 1, 2 … for all files together; look at the main file; not special for this set")
        var box = Box(1, 1, 1).Bodies[0];
        SketchLevel L(string model, string name, double m) => new(model, name, m, [box]);
        var storeys = SketchStoreys.Of([L("main", "Parking", -5), L("main", "L1 - Block A", 0), L("main", "L1 - Block B", -1.05),
            L("main", "L1 - Block C", 0.6), L("main", "L2", 4), L("other", "Top of steel", 3.8), L("other", "Deep", -9)]);
        Assert.Equal([-1, 0, 1], storeys.Select(s => s.Number));
        // ground floors of blocks within 2 m of each other: one storey, number 0 because height 0 is on it
        Assert.Equal(["L1 - Block B", "L1 - Block A", "L1 - Block C"], storeys.Single(s => s.Number == 0).Levels);
        // another model's level just under a storey (≤ 0.3 m) is on it; one under the lowest goes to the lowest
        Assert.Contains(storeys.Single(s => s.Number == 1).Parts, p => p.Model == "other" && p.Bodies.Count > 0);
        Assert.Contains(storeys.Single(s => s.Number == -1).Parts, p => p.Model == "other" && p.Bodies.Count > 0);
    }

    [Fact]
    public void Every_tool_tip_is_there_in_English_too()
    {
        // (user, 15 Oct 2026: the app is bilingual; every new text in Dutch and English)
        var missing = ToolBar.Items.Where(i => i.Tip is { Length: > 0 } && !UiTexts.En.ContainsKey(i.Tip)).Select(i => i.Tip).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void The_angle_of_view_is_vertical()
    {
        // (as HelixToolkit had it; everything that snaps in pixels depends on it)
        Assert.False(new ViewHost().FieldOfViewAcross);
    }
}
