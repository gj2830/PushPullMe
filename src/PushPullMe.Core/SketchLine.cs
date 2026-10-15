using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// A loose line or arc (a sketch curve, as DesignSpark keeps them in document.xml): not part of any body. An arc runs
/// counter-clockwise round <see cref="Axis"/> from <see cref="Start"/> to <see cref="End"/> about <see cref="Centre"/>.
/// A construction line is an aid only.
/// </summary>
public sealed record SketchLine(Vec3 Start, Vec3 End, Vec3? Centre = null, Vec3? Axis = null, bool Construction = false)
{
    public bool IsArc => Centre != null;

    /// <summary>The arc as a circle (null for a straight line).</summary>
    public BrepCircle? Circle => Centre is { } c && Axis is { } ax
        ? new BrepCircle(c, ax.Normalized(), (Start - c).Normalized(), (Start - c).Length) : null;

    /// <summary>Points along it (two for a straight line).</summary>
    public IReadOnlyList<Vec3> Points(int count = 32) => Circle is { } c ? Drawing.ArcPoints(c, Start, End, count) : [Start, End];

    /// <summary>Its middle point.</summary>
    public Vec3 Middle => Circle is { } c ? Drawing.ArcPoints(c, Start, End, 2)[1] : (Start + End) * 0.5;

    public SketchLine Transformed(Func<Vec3, Vec3> point, Func<Vec3, Vec3> direction) =>
        new(point(Start), point(End), Centre is { } c ? point(c) : null, Axis is { } a ? direction(a) : null, Construction);
}
