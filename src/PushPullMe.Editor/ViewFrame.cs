using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>A point of the screen: pixels in the view, from its top left corner, y down.</summary>
public readonly record struct ScreenPoint(double X, double Y)
{
    public static ScreenPoint operator -(ScreenPoint a, ScreenPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static ScreenPoint operator +(ScreenPoint a, ScreenPoint b) => new(a.X + b.X, a.Y + b.Y);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>A ray in the view: where it starts and which way it goes (of length 1).</summary>
public readonly record struct ViewRay(Vec3 Origin, Vec3 Direction)
{
    public Vec3 At(double t) => Origin + Direction * t;
}

/// <summary>
/// The camera as a frame, in double precision: the eye, its right, up and forward directions (of length 1), how far the
/// view reaches sideways and up per unit forward, and the view's size in pixels. Between the screen and the model both
/// ways, exactly each other's way back (a viewer's own single precision matrices put a click up to half a millimetre
/// beside where the point was drawn, enough to miss a face just inside its edge).
/// </summary>
public sealed record ViewFrame(Vec3 Eye, Vec3 Right, Vec3 Up, Vec3 Forward, double Tx, double Ty, double Width, double Height)
{
    /// <summary>The frame of a perspective camera; <paramref name="fieldOfView"/> in degrees, across the view
    /// (<paramref name="horizontal"/>) or up it. Null when there is none (no size, up along the look).</summary>
    public static ViewFrame? Of(Vec3 eye, Vec3 look, Vec3 up, double fieldOfView, bool horizontal, double width, double height)
    {
        if (width < 1 || height < 1 || look.Length < 1e-12) return null;
        var f = look.Normalized();
        var r = f.Cross(up);
        if (r.Length < 1e-12) return null;
        r = r.Normalized();
        var u = r.Cross(f);
        double t = Math.Tan(fieldOfView * Math.PI / 360);
        return horizontal ? new ViewFrame(eye, r, u, f, t, t * height / width, width, height) : new ViewFrame(eye, r, u, f, t * width / height, t, width, height);
    }

    /// <summary>Where a point of the model is on the screen.</summary>
    public ScreenPoint Project(Vec3 p)
    {
        var v = p - Eye; double z = v.Dot(Forward);
        if (Math.Abs(z) < 1e-12) z = 1e-12;
        return new ScreenPoint(Width / 2 * (1 + v.Dot(Right) / (z * Tx)), Height / 2 * (1 - v.Dot(Up) / (z * Ty)));
    }

    /// <summary>How far in front of the eye a point is (along the look; behind it: below 0).</summary>
    public double Depth(Vec3 p) => (p - Eye).Dot(Forward);

    /// <summary>The ray from the eye through a point of the screen (exactly the way back of <see cref="Project"/>).</summary>
    public ViewRay Ray(ScreenPoint pos) =>
        new(Eye, (Forward + Right * ((2 * pos.X / Width - 1) * Tx) + Up * ((1 - 2 * pos.Y / Height) * Ty)).Normalized());

    /// <summary>How long a pixel is at that depth in front of the eye (across, up).</summary>
    public (double X, double Y) PixelAt(double depth) => (2 * depth * Tx / Width, 2 * depth * Ty / Height);
}
