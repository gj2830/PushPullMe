using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// The camera as angles round the model: azimuth 0 looks from the front (from -y), 90 from the right (from +x), going
/// counter-clockwise seen from above; elevation 0 level, 90 straight down from above.
/// </summary>
public static class ViewAngles
{
    private static readonly Vec3 Up = new(0, 0, 1);

    /// <summary>The way the camera looks from that azimuth and elevation (degrees), of length 1.</summary>
    public static Vec3 Look(double azimuth, double elevation)
    {
        double a = azimuth * Math.PI / 180, e = Math.Clamp(elevation, -89.9, 89.9) * Math.PI / 180;
        // (where the camera stands, seen from the model; it looks the other way)
        return new Vec3(-Math.Cos(e) * Math.Sin(a), Math.Cos(e) * Math.Cos(a), -Math.Sin(e));
    }

    /// <summary>The azimuth (0 to 360) and elevation of a look direction, in degrees.</summary>
    public static (double Azimuth, double Elevation) Of(Vec3 look)
    {
        var f = look.Normalized();
        double az = Math.Atan2(-f.X, f.Y) * 180 / Math.PI;
        return (az < 0 ? az + 360 : az, Math.Asin(Math.Clamp(-f.Z, -1, 1)) * 180 / Math.PI);
    }

    /// <summary>How far back from the centre the camera has to stand, looking along look, for every corner to land
    /// within fill of the half view; tx and ty are the tangents of half the view across and up.</summary>
    public static double FitDistance(IReadOnlyList<Vec3> corners, Vec3 centre, Vec3 look, double tx, double ty, double fill = 0.9)
    {
        var f = look.Normalized();
        var r = f.Cross(Up); if (r.Length < 1e-9) r = new Vec3(1, 0, 0); r = r.Normalized();
        var u = r.Cross(f);
        double Reach(double d)
        {
            var eye = centre - f * d; double m = 0;
            foreach (var c in corners)
            {
                var v = c - eye; double z = v.Dot(f);
                if (z <= 1e-9) return double.MaxValue;
                m = Math.Max(m, Math.Max(Math.Abs(v.Dot(r) / (z * tx)), Math.Abs(v.Dot(u) / (z * ty))));
            }
            return m;
        }
        double size = corners.Count == 0 ? 1 : corners.Max(c => (c - centre).Length);
        double near = 1e-3, far = Math.Max(size, 1e-3) * 200;
        for (int i = 0; i < 60; i++) { double mid = (near + far) / 2; if (Reach(mid) > fill) near = mid; else far = mid; }
        return far;
    }

    /// <summary>The distance at which the model fits from every azimuth at this elevation (checked every 5°): a whole
    /// turn round it at that distance keeps the model the same size and in view.</summary>
    public static double FitDistanceAllRound(IReadOnlyList<Vec3> corners, Vec3 centre, double elevation, double tx, double ty, double fill = 0.9) =>
        Enumerable.Range(0, 72).Max(i => FitDistance(corners, centre, Look(i * 5, elevation), tx, ty, fill));
}
