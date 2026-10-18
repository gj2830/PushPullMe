using System.Collections.ObjectModel;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor.Session;

// The few kinds the tools were written with in WPF (a point on the screen, a point and a direction in the view's
// millimetres, a colour, the keys), here without WPF, so the tools run in the editor layer as they were written.

/// <summary>A point of the screen: pixels in the view, from its top left corner, y down.</summary>
public readonly record struct Point(double X, double Y)
{
    public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    public static Point operator +(Point a, Vector v) => new(a.X + v.X, a.Y + v.Y);
    public static Point operator -(Point a, Vector v) => new(a.X - v.X, a.Y - v.Y);
    public static implicit operator ScreenPoint(Point p) => new(p.X, p.Y);
    public static implicit operator Point(ScreenPoint p) => new(p.X, p.Y);
}

/// <summary>A step on the screen (pixels).</summary>
public record struct Vector(double X, double Y)
{
    public readonly double Length => Math.Sqrt(X * X + Y * Y);
    public readonly double LengthSquared => X * X + Y * Y;
    public void Normalize() { double l = Length; if (l > 0) { X /= l; Y /= l; } }
    public static Vector operator *(Vector v, double s) => new(v.X * s, v.Y * s);
    public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y);
    /// <summary>The dot product.</summary>
    public static double Multiply(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;
}

/// <summary>A point in the view (millimetres).</summary>
public record struct Point3D(double X, double Y, double Z)
{
    public static Vector3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3D operator +(Point3D a, Vector3D v) => new(a.X + v.X, a.Y + v.Y, a.Z + v.Z);
    public static Point3D operator -(Point3D a, Vector3D v) => new(a.X - v.X, a.Y - v.Y, a.Z - v.Z);
    public static implicit operator Vec3(Point3D p) => new(p.X, p.Y, p.Z);
    public static implicit operator Point3D(Vec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>A direction in the view.</summary>
public record struct Vector3D(double X, double Y, double Z)
{
    public readonly double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public void Normalize() { double l = Length; if (l > 0) { X /= l; Y /= l; Z /= l; } }
    public static Vector3D operator *(Vector3D v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3D operator /(Vector3D v, double s) => new(v.X / s, v.Y / s, v.Z / s);
    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(Vector3D v) => new(-v.X, -v.Y, -v.Z);
    public static double DotProduct(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3D CrossProduct(Vector3D a, Vector3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static implicit operator Vec3(Vector3D v) => new(v.X, v.Y, v.Z);
    public static implicit operator Vector3D(Vec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>Points in the view, two by two for lines.</summary>
public sealed class Point3DCollection : List<Point3D>
{
    public Point3DCollection() { }
    public Point3DCollection(IEnumerable<Point3D> points) : base(points) { }
}

/// <summary>A colour (with how opaque it is).</summary>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color FromRgb(byte r, byte g, byte b) => new(255, r, g, b);
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

/// <summary>The modifier keys held.</summary>
[Flags]
public enum ModifierKeys { None = 0, Alt = 1, Control = 2, Shift = 4 }

/// <summary>The keys the tools listen to.</summary>
public enum Key
{
    None, A, B, C, E, F, G, K, L, M, O, P, Q, R, S, T, Y, Z,
    Back, Delete, Enter, Escape, Space, Left, Right, Up, Down, PageUp, PageDown,
    /// <summary>(as WPF: the same key as Enter)</summary>
    Return = Enter,
}

/// <summary>A rectangle on the screen.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public Rect(Point a, Point b) : this(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) { }
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(Point p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>Lines between pairs of points (each two points one line), a fixed thickness in pixels; what the tools show
/// beside the model (a sketch, snap marks, guide lines, the grid…).</summary>
public sealed class LineSet
{
    public Point3DCollection Points { get; init; } = new();
    public Color Color { get; init; }
    public double Thickness { get; init; } = 1;

    /// <summary>Drawn over everything (the work plane's handle).</summary>
    public bool OnTop { get; init; }

    /// <summary>Drawn a little behind what lies in the same place (the ground grid: the edges of a face on it stay on top).</summary>
    public bool Behind { get; set; }
}

/// <summary>See-through triangles (a highlight; each three points one triangle), unlit, in front of the faces.</summary>
public sealed class FaceSet
{
    public List<Point3D> Triangles { get; init; } = new();
    public Color Color { get; init; }
    public double Opacity { get; init; } = 1;
}

/// <summary>What the tools show in the view beside the model (<see cref="LineSet"/>s, <see cref="FaceSet"/>s): a window
/// draws them as they are added and takes them away as they are removed.</summary>
public sealed class SceneItems : ObservableCollection<object>;

/// <summary>A mouse button.</summary>
public enum MouseButton { Left, Middle, Right }
