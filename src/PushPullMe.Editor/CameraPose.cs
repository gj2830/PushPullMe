using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>
/// Where the camera is, the way it looks (its length: how far away it looks, the point turned round when nothing is
/// under the mouse) and which way is up; and how the mouse moves it, as in SketchUp: orbit, pan, zoom. Units as the view
/// has them (millimetres).
/// </summary>
public readonly record struct CameraPose(Vec3 Position, Vec3 Look, Vec3 Up)
{
    /// <summary>Degrees of turn per pixel the mouse moves.</summary>
    public const double OrbitSpeed = 0.4;

    /// <summary>What it looks at: the end of the look.</summary>
    public Vec3 Target => Position + Look;

    /// <summary>Whether it can be used: a number everywhere, a direction to look in, up not along it.</summary>
    public bool IsUsable =>
        double.IsFinite(Position.X + Position.Y + Position.Z + Look.X + Look.Y + Look.Z + Up.X + Up.Y + Up.Z)
        && Look.Length > 1e-9 && Look.Cross(Up).Length > 1e-6 * Look.Length * Up.Length;

    /// <summary>
    /// Turned as the mouse moved (pixels) round the pivot, freely all the way round (as DesignSpark): left/right round the
    /// screen's up, up/down round its sideways direction; up turns along, so over the top the model simply goes on
    /// turning and left/right stays left/right as seen. Null when there is no screen's up (up along the look).
    /// </summary>
    public CameraPose? Orbit(Vec3 pivot, double dx, double dy)
    {
        // (the screen's up, square to the look; the sideways direction from the two)
        var f = Look.Normalized();
        var up = Up - f * Up.Dot(f);
        if (up.Length < 1e-9) return null;
        up = up.Normalized();
        var right = f.Cross(up);
        // camera the other way round than the model seems to turn: first round up, then round the sideways direction
        // (as it was before the first turn)
        double turn = -dx * OrbitSpeed, tilt = -dy * OrbitSpeed;
        Vec3 Point(Vec3 p) => pivot + Rotate(Rotate(p - pivot, up, turn), right, tilt);
        Vec3 Dir(Vec3 v) => Rotate(Rotate(v, up, turn), right, tilt);
        return new CameraPose(Point(Position), Dir(Look), Dir(up));
    }

    /// <summary>Moved with the mouse (pixels): the point at that depth in front of the eye keeps under it.</summary>
    public CameraPose? Pan(ViewFrame frame, double depth, double dx, double dy)
    {
        var f = Look.Normalized();
        var r = f.Cross(Up);
        if (r.Length < 1e-9) return null;
        r = r.Normalized();
        var u = r.Cross(f);
        var (sx, sy) = frame.PixelAt(depth);
        return this with { Position = Position - r * (dx * sx) + u * (dy * sy) };
    }

    /// <summary>Toward the target (a wheel notch of 120: a fifth of the way closer; less than 0: back), the look
    /// shortened along so the pivot stays as far in front.</summary>
    public CameraPose Zoom(Vec3 target, double delta)
    {
        double s = Math.Pow(0.8, delta / 120.0);
        return this with { Position = target + (Position - target) * s, Look = Look * s };
    }

    /// <summary><paramref name="v"/> turned round the axis (through the origin) by that many degrees, the right-hand way.</summary>
    public static Vec3 Rotate(Vec3 v, Vec3 axis, double degrees)
    {
        var k = axis.Normalized();
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        // (Rodrigues)
        return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
    }
}
