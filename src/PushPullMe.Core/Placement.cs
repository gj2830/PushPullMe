using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Where something stands: a point of its own coordinates goes to <see cref="O"/> + x <see cref="X"/> + y <see cref="Y"/>
/// + z <see cref="Z"/>. Only turns, moves, mirrors and scaling the same in every direction (the three directions square
/// to each other and equally long): what a component instance can be given (see <see cref="SketchGroup.Place"/>).
/// </summary>
public readonly record struct Placement(Vec3 O, Vec3 X, Vec3 Y, Vec3 Z)
{
    public static readonly Placement Identity = new(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    public Vec3 Dir(Vec3 v) => X * v.X + Y * v.Y + Z * v.Z;
    public Vec3 Point(Vec3 p) => O + Dir(p);

    /// <summary>How much larger it makes things.</summary>
    public double Scale => X.Length;

    /// <summary>Whether it mirrors (left and right round).</summary>
    public bool Mirrors => X.Cross(Y).Dot(Z) < 0;

    /// <summary>Whether it only turns and moves.</summary>
    public bool Rigid => Math.Abs(Scale - 1) < 1e-9 && !Mirrors;

    /// <summary>Whether it only moves.</summary>
    public bool OnlyMoves => (X - Identity.X).Length < 1e-12 && (Y - Identity.Y).Length < 1e-12 && (Z - Identity.Z).Length < 1e-12;

    /// <summary>This placement, then <paramref name="after"/>.</summary>
    public Placement Then(Placement after) => new(after.Point(O), after.Dir(X), after.Dir(Y), after.Dir(Z));

    /// <summary>The way back.</summary>
    public Placement Inverse()
    {
        // (the directions are square to each other and equally long: the inverse is the transpose over the scale squared)
        double k = 1 / X.Dot(X);
        var x = new Vec3(X.X, Y.X, Z.X) * k; var y = new Vec3(X.Y, Y.Y, Z.Y) * k; var z = new Vec3(X.Z, Y.Z, Z.Z) * k;
        var back = new Placement(new Vec3(0, 0, 0), x, y, z);
        return back with { O = back.Dir(O) * -1 };
    }

    public static Placement Move(Vec3 d) => Identity with { O = d };

    /// <summary>Turned round an axis (see <see cref="BrepTransform.Turn"/>).</summary>
    public static Placement Turn(Vec3 centre, Vec3 axis, double angle)
    {
        var t = new BrepTransform.Turn(centre, axis, angle);
        return new(t.Point(new Vec3(0, 0, 0)), t.Dir(Identity.X), t.Dir(Identity.Y), t.Dir(Identity.Z));
    }

    /// <summary>Larger or smaller from a fixed point, the same in every direction.</summary>
    public static Placement Scaled(Vec3 fixedPoint, double factor) =>
        new(fixedPoint - fixedPoint * factor, Identity.X * factor, Identity.Y * factor, Identity.Z * factor);

    /// <summary>Mirrored in the plane through <paramref name="origin"/> square to <paramref name="normal"/>.</summary>
    public static Placement Mirror(Vec3 origin, Vec3 normal)
    {
        var n = normal.Normalized();
        Vec3 D(Vec3 v) => v - n * (2 * v.Dot(n));
        return new(origin - D(origin), D(Identity.X), D(Identity.Y), D(Identity.Z));
    }
}
