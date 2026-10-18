using System.Globalization;
using System.Numerics;
using PushPullMe.Core;
using PushPullMe.Editor;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Tests;

/// <summary>What the window does without a window: the screen and the camera, picking, undo, units, the app's folder.</summary>
public class EditorTests
{
    private static void Near(Vec3 expected, Vec3 actual, double tol = 1e-9) => Assert.True((expected - actual).Length < tol, $"{actual} is not {expected}");

    private static ViewFrame Frame(CameraPose c, bool horizontal = true) => ViewFrame.Of(c.Position, c.Look, c.Up, 45, horizontal, 800, 600)!;

    private static readonly CameraPose Camera = new(new Vec3(-300, -400, 250), new Vec3(300, 400, -250), new Vec3(0, 0, 1));

    [Fact]
    public void A_ray_through_a_point_of_the_screen_projects_back_onto_it()
    {
        var frame = Frame(Camera);
        foreach (var s in new[] { new ScreenPoint(400, 300), new ScreenPoint(0, 0), new ScreenPoint(799, 17), new ScreenPoint(123.5, 590) })
            foreach (double t in new[] { 1.0, 250, 5000 })
            {
                var back = frame.Project(frame.Ray(s).At(t));
                Assert.Equal(s.X, back.X, 6); Assert.Equal(s.Y, back.Y, 6);
            }
        // (the middle of the screen: straight along the look; the angle of view across or up the view)
        Near(Camera.Look.Normalized(), frame.Ray(new ScreenPoint(400, 300)).Direction);
        Assert.Equal(Math.Tan(22.5 * Math.PI / 180), frame.Tx, 12);
        Assert.Equal(Math.Tan(22.5 * Math.PI / 180), Frame(Camera, horizontal: false).Ty, 12);
    }

    [Fact]
    public void A_frame_needs_a_size_and_an_up_not_along_the_look()
    {
        Assert.Null(ViewFrame.Of(default, new Vec3(0, 0, -1), new Vec3(0, 0, 1), 45, true, 800, 600));
        Assert.Null(ViewFrame.Of(default, new Vec3(1, 0, 0), new Vec3(0, 0, 1), 45, true, 0, 600));
        Assert.False(new CameraPose(default, new Vec3(0, 0, -1), new Vec3(0, 0, 2)).IsUsable);
        Assert.False(new CameraPose(new Vec3(double.NaN, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)).IsUsable);
        Assert.True(Camera.IsUsable);
    }

    [Fact]
    public void Turning_is_the_right_hand_way()
    {
        Near(new Vec3(0, 1, 0), CameraPose.Rotate(new Vec3(1, 0, 0), new Vec3(0, 0, 1), 90));
        Near(new Vec3(0, 0, 1), CameraPose.Rotate(new Vec3(0, 1, 0), new Vec3(2, 0, 0), 90));
    }

    [Fact]
    public void Orbiting_keeps_the_distance_to_the_pivot_and_a_whole_turn_comes_back()
    {
        var pivot = new Vec3(10, 20, 0);
        var turned = Camera.Orbit(pivot, 37, -12)!.Value;
        Assert.Equal((Camera.Position - pivot).Length, (turned.Position - pivot).Length, 9);
        Assert.True(turned.IsUsable);
        // (left/right only: round the screen's up, by 0.4° a pixel)
        var round = Camera.Orbit(pivot, 360 / CameraPose.OrbitSpeed, 0)!.Value;
        Near(Camera.Position, round.Position, 1e-6);
        Near(Camera.Look, round.Look, 1e-6);
    }

    [Fact]
    public void Panning_keeps_the_point_grabbed_under_the_mouse()
    {
        var frame = Frame(Camera);
        var grab = frame.Ray(new ScreenPoint(500, 250)).At(600);
        double depth = frame.Depth(grab);
        var moved = Camera.Pan(frame, depth, 30, -20)!.Value;
        var now = Frame(moved).Project(grab);
        Assert.Equal(530, now.X, 6); Assert.Equal(230, now.Y, 6);
    }

    [Fact]
    public void Zooming_keeps_the_target_where_it_is_on_the_screen()
    {
        var frame = Frame(Camera);
        var target = frame.Ray(new ScreenPoint(200, 450)).At(400);
        var closer = Camera.Zoom(target, 120);
        Assert.Equal(0.8 * (Camera.Position - target).Length, (closer.Position - target).Length, 9);
        var s = Frame(closer).Project(target);
        Assert.Equal(200, s.X, 6); Assert.Equal(450, s.Y, 6);
    }

    /// <summary>A square of two triangles at height z, faces 7 and 8 of body 3.</summary>
    private static PickMesh Square(float z, int body = 3) => new(
        [new(0, 0, z), new(10, 0, z), new(10, 10, z), new(0, 0, z), new(10, 10, z), new(0, 10, z)], [7, 8], body);

    [Fact]
    public void Picking_finds_the_nearest_face_and_where()
    {
        var low = Square(0); var high = Square(5, body: 4);
        var ray = new ViewRay(new Vec3(8, 2, 100), new Vec3(0, 0, -1));
        var hits = Picking.Hits([low, high], ray);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new FaceRef(4, 7), hits[0].Face);
        Near(new Vec3(8, 2, 5), hits[0].At);
        Assert.Equal(new FaceRef(3, 7), hits[1].Face);
        Assert.Equal(new FaceRef(3, 8), Picking.FaceAt([low], new ViewRay(new Vec3(2, 8, -50), new Vec3(0, 0, 1)))?.Face);
        Assert.Null(Picking.FaceAt([low, high], new ViewRay(new Vec3(20, 2, 100), new Vec3(0, 0, -1))));
        Assert.Equal((new Vec3(0, 0, 0), new Vec3(10, 10, 5)), Picking.Bounds([low, high]));
        Assert.Null(Picking.Bounds([]));
    }

    private static SketchDocument Cube(double size) =>
        SketchDocument.FromBodies([BrepShapes.FromPolyhedron(Polyhedron.Box(0, 0, 0, size, size, size))]);

    [Fact]
    public void Undo_and_redo_go_back_and_forth_and_a_new_change_ends_redo()
    {
        var h = new History();
        SketchDocument a = Cube(1), b = Cube(2), c = Cube(3), d = Cube(4);
        var now = h.Commit(a, b, null);
        now = h.Commit(now, c, null);
        Assert.Same(c, now);
        Assert.Equal(2, h.UndoCount);
        now = h.Undo(now)!;
        Assert.Same(b, now);
        Assert.True(h.CanRedo);
        Assert.Same(c, now = h.Redo(now)!);
        Assert.Null(h.Redo(now));
        now = h.Undo(now)!;
        now = h.Commit(now, d, null);
        Assert.False(h.CanRedo);
        // (the last change made over: the step back stays the one before it)
        now = h.Replace(b, c, null);
        Assert.Same(b, h.Undo(now));
        Assert.Same(a, h.Undo(b));
        Assert.False(h.CanUndo);
        h.Clear();
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Millimetres_are_typed_with_a_comma_or_a_point()
    {
        Assert.True(Units.TryMm("12,5", out double a)); Assert.Equal(12.5, a);
        Assert.True(Units.TryMm("-3.25", out double b)); Assert.Equal(-3.25, b);
        Assert.False(Units.TryMm("twelve", out _));
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("12.35", Units.Mm(0.012345));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void The_apps_folder_is_the_users_own_on_every_system()
    {
        Assert.Equal("PushPullMe", Path.GetFileName(AppData.Folder));
        Assert.True(Path.IsPathRooted(AppData.Folder));
        Assert.StartsWith(AppData.Folder, AppData.ErrorLog);
    }
}
