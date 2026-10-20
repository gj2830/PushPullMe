using PushPullMe.Core;
using RsDocx.Core.Stl;

namespace PushPullMe.Tests;

/// <summary>Lining a point up with a corner along an axis, as SketchUp's dotted "in line with a point".</summary>
public class InferenceTests
{
    private static readonly Vec3[] Axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    private static readonly Vec3 Up = new(0, 0, 1);

    [Fact]
    public void Without_a_direction_a_point_lines_up_with_the_corner_along_each_axis_in_the_plane()
    {
        // a corner at (3,5) on the ground, the mouse at (7,4.8): in line along red at y = 5, along green at x = 3
        var ups = Inference.LineUp(new Vec3(7, 4.8, 0), Up, new Vec3(3, 5, 0), Axes).ToList();
        Assert.Equal([0, 1], ups.Select(u => u.Axis));
        Assert.Equal(new Vec3(7, 5, 0), ups[0].P);
        Assert.Equal(new Vec3(3, 4.8, 0), ups[1].P);
    }

    [Fact]
    public void Along_a_direction_the_point_is_where_it_crosses_the_line_through_the_corner()
    {
        // from (0,0) along red, the corner at (4,2): in line with it along green at (4,0); along red they run parallel
        var ups = Inference.LineUp(new Vec3(3.9, 0, 0), Up, new Vec3(4, 2, 0), Axes, new Vec3(0, 0, 0), new Vec3(1, 0, 0)).ToList();
        var (p, axis, _) = Assert.Single(ups);
        Assert.Equal(1, axis);
        Assert.Equal(4, p.X, 12); Assert.Equal(0, p.Y, 12);
    }

    [Fact]
    public void A_corner_above_the_plane_is_taken_into_it()
    {
        // drawing on the top of a block at z = 2, a corner on the ground at (1,1,0): lined up as if at (1,1,2)
        var ups = Inference.LineUp(new Vec3(5, 1.1, 2), Up, new Vec3(1, 1, 0), Axes).ToList();
        Assert.Equal(new Vec3(1, 1, 2), ups[0].Corner);
        Assert.Equal(new Vec3(5, 1, 2), ups[0].P);
    }
}
