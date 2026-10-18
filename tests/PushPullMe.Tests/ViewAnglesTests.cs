using PushPullMe.Core;
using RsDocx.Core.Stl;

namespace PushPullMe.Tests;

/// <summary>The camera as angles round the model (the MCP screenshot's azimuth and elevation).</summary>
public class ViewAnglesTests
{
    private static List<Vec3> Box(double x, double y, double z) =>
        new[] { 0, x }.SelectMany(a => new[] { 0, y }.SelectMany(b => new[] { 0, z }.Select(c => new Vec3(a, b, c)))).ToList();

    [Theory]
    [InlineData(0, 0, 0, 1, 0)]     // from the front (-y): looks along +y
    [InlineData(90, 0, -1, 0, 0)]   // from the right (+x): looks along -x
    [InlineData(180, 0, 0, -1, 0)]  // from the back
    [InlineData(270, 0, 1, 0, 0)]   // from the left
    public void Azimuth_goes_round_counter_clockwise_from_the_front(double az, double el, double x, double y, double z)
    {
        var f = ViewAngles.Look(az, el);
        Assert.Equal(x, f.X, 9); Assert.Equal(y, f.Y, 9); Assert.Equal(z, f.Z, 9);
    }

    [Fact]
    public void Elevation_looks_down_and_angles_come_back_from_a_direction()
    {
        var f = ViewAngles.Look(0, 30);
        Assert.Equal(-0.5, f.Z, 9);
        Assert.Equal(1, f.Length, 9);
        foreach (var (az, el) in new[] { (0.0, 30.0), (45.0, 10.0), (200.0, -20.0), (325.0, 60.0) })
        {
            var (a, e) = ViewAngles.Of(ViewAngles.Look(az, el) * 7);
            Assert.Equal(az, a, 6); Assert.Equal(el, e, 6);
        }
        // straight down is kept just off it (up along the look is no camera)
        Assert.True(ViewAngles.Look(0, 90).Z > -1);
    }

    [Fact]
    public void All_round_distance_fits_every_azimuth_and_is_the_largest_of_them()
    {
        var corners = Box(4, 3, 4.2);
        var centre = new Vec3(2, 1.5, 2.1);
        double t = Math.Tan(30 * Math.PI / 180);
        double round = ViewAngles.FitDistanceAllRound(corners, centre, 30, t * 1.5, t);
        double front = ViewAngles.FitDistance(corners, centre, ViewAngles.Look(0, 30), t * 1.5, t);
        double corner = ViewAngles.FitDistance(corners, centre, ViewAngles.Look(45, 30), t * 1.5, t);
        Assert.True(round >= front - 1e-9 && round >= corner - 1e-9);
        // a box seen across its corner needs more room than square on
        Assert.True(corner > front);
        // a fuller view (more of the half view used) stands nearer
        Assert.True(ViewAngles.FitDistance(corners, centre, ViewAngles.Look(45, 30), t * 1.5, t, 0.95) < corner);
    }
}
