using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A whole body moved or turned: every vertex, surface and curve the same way.</summary>
public static class BrepTransform
{
    /// <summary>A rigid motion: points turned <see cref="Angle"/> (radians, counter-clockwise looking against
    /// <see cref="Axis"/>) round the line through <see cref="Centre"/>, then nothing else.</summary>
    public readonly record struct Turn(Vec3 Centre, Vec3 Axis, double Angle)
    {
        /// <summary>A direction turned (Rodrigues).</summary>
        public Vec3 Dir(Vec3 v)
        {
            var k = Axis.Normalized();
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
        }

        public Vec3 Point(Vec3 p) => Centre + Dir(p - Centre);
    }

    /// <summary>
    /// The body made larger or smaller from <paramref name="fixedPoint"/>: by <paramref name="factor"/> in all directions,
    /// or (<paramref name="along"/>) only along that direction. One way only: just straight edges and flat faces (a circle
    /// would become an ellipse); refused otherwise.
    /// </summary>
    public static BrepBody Scaled(BrepBody body, Vec3 fixedPoint, double factor, Vec3? along = null)
    {
        if (factor <= 1e-6) throw new PushPullException(Lang.T("Schalen moet met een factor groter dan 0.", "Scaling needs a factor of more than 0."));
        if (along is { } ax)
        {
            var a = ax.Normalized();
            Vec3 Pt(Vec3 p) { var d = p - fixedPoint; return fixedPoint + d + a * (d.Dot(a) * (factor - 1)); }
            // (a normal: the inverse-transpose: its part along a divided by the factor)
            Vec3 Normal(Vec3 n) => (n + a * (n.Dot(a) * (1 / factor - 1))).Normalized();
            Vec3 InPlane(Vec3 x, Vec3 n) { var y = Pt(fixedPoint + x) - fixedPoint; return (y - n * y.Dot(n)).Normalized(); }
            if (body.Edges.Any(e => e.Curve is not BrepLine) || body.Faces.Any(f => f.Surface is not BrepPlane))
                throw new PushPullException(Lang.T("Langs één as schalen kan (nog) alleen bij rechte randen en platte vlakken; een ronding zou ovaal worden. Schaal in alle richtingen.", "Scaling along one axis only works (for now) with straight edges and flat faces; a rounding would become oval. Scale in all directions."));
            return new BrepBody
            {
                Sheet = body.Sheet,
            Vertices = body.Vertices.Select(Pt).ToList(),
                Edges = body.Edges,
                Faces = body.Faces.Select(f =>
                {
                    var p = (BrepPlane)f.Surface;
                    var n = Normal(p.Normal);
                    return f with { Surface = new BrepPlane(Pt(p.Origin), n, InPlane(p.XAxis, n)) };
                }).ToList(),
            };
        }
        Vec3 P(Vec3 p) => fixedPoint + (p - fixedPoint) * factor;
        BrepCurve C(BrepCurve c) => c switch
        {
            BrepLine => c,
            BrepCircle ci => ci with { Centre = P(ci.Centre), Radius = ci.Radius * factor },
            BrepEllipse el => el with { Centre = P(el.Centre), MajorRadius = el.MajorRadius * factor, MinorRadius = el.MinorRadius * factor },
            BrepBSpline bs => bs with { ControlPoints = bs.ControlPoints.Select(P).ToList(), Helix = null },
            _ => throw new PushPullException(Lang.T("Dit lichaam heeft een rand die (nog) niet geschaald kan worden.", "This body has an edge that cannot be scaled (yet).")),
        };
        BrepSurface Sf(BrepSurface s) => s switch
        {
            BrepPlane p => p with { Origin = P(p.Origin) },
            BrepCylinder c => c with { Origin = P(c.Origin), Radius = c.Radius * factor },
            BrepCone c => c with { Origin = P(c.Origin), Radius = c.Radius * factor },
            BrepSphere sp => sp with { Centre = P(sp.Centre), Radius = sp.Radius * factor },
            BrepTorus t => t with { Centre = P(t.Centre), MajorRadius = t.MajorRadius * factor, MinorRadius = t.MinorRadius * factor },
            BrepNurbsSurface n when n.Helix == null => n with { Points = n.Points.Select(P).ToList() },
            _ => throw new PushPullException(Lang.T("Dit lichaam heeft een vlak dat (nog) niet geschaald kan worden.", "This body has a face that cannot be scaled (yet).")),
        };
        return new BrepBody
        {
            Sheet = body.Sheet,
            Vertices = body.Vertices.Select(P).ToList(),
            Edges = body.Edges.Select(e => e with { Curve = C(e.Curve) }).ToList(),
            Faces = body.Faces.Select(f => f with { Surface = Sf(f.Surface) }).ToList(),
        };
    }

    /// <summary>
    /// The body mirrored in the plane through <paramref name="origin"/> square to <paramref name="normal"/> (as SketchUp's
    /// Flip). A mirror turns left and right round: every loop is run the other way (so it stays counter-clockwise round
    /// its face's outward normal), an arc's normal turns over (so it still runs from its start to its end), and a NURBS
    /// face, whose normal comes from its parameters, is reversed.
    /// </summary>
    public static BrepBody Mirrored(BrepBody body, Vec3 origin, Vec3 normal)
    {
        var n = normal.Normalized();
        Vec3 D(Vec3 v) => v - n * (2 * v.Dot(n));
        Vec3 P(Vec3 p) => origin + D(p - origin);
        BrepCurve C(BrepCurve c) => c switch
        {
            BrepLine => c,
            BrepCircle ci => ci with { Centre = P(ci.Centre), Normal = D(ci.Normal) * -1, XAxis = D(ci.XAxis) },
            BrepEllipse el => el with { Centre = P(el.Centre), Normal = D(el.Normal) * -1, XAxis = D(el.XAxis) },
            BrepBSpline bs => bs with { ControlPoints = bs.ControlPoints.Select(P).ToList(), Helix = null },
            _ => throw new PushPullException(Lang.T("Dit lichaam heeft een rand die (nog) niet gespiegeld kan worden.", "This body has an edge that cannot be mirrored (yet).")),
        };
        (BrepSurface, bool Flip) S(BrepSurface s) => s switch
        {
            BrepPlane p => (p with { Origin = P(p.Origin), Normal = D(p.Normal), XAxis = D(p.XAxis) }, false),
            BrepCylinder c => (c with { Origin = P(c.Origin), Axis = D(c.Axis), XAxis = D(c.XAxis) }, false),
            BrepCone c => (c with { Origin = P(c.Origin), Axis = D(c.Axis), XAxis = D(c.XAxis) }, false),
            BrepSphere sp => (sp with { Centre = P(sp.Centre), Axis = D(sp.Axis), XAxis = D(sp.XAxis) }, false),
            BrepTorus t => (t with { Centre = P(t.Centre), Axis = D(t.Axis), XAxis = D(t.XAxis) }, false),
            BrepNurbsSurface ns when ns.Helix == null => (ns with { Points = ns.Points.Select(P).ToList() }, true),
            _ => throw new PushPullException(Lang.T("Dit lichaam heeft een vlak dat (nog) niet gespiegeld kan worden.", "This body has a face that cannot be mirrored (yet).")),
        };
        return new BrepBody
        {
            Sheet = body.Sheet,
            Vertices = body.Vertices.Select(P).ToList(),
            Edges = body.Edges.Select(e => e with { Curve = C(e.Curve) }).ToList(),
            Faces = body.Faces.Select(f =>
            {
                var (surface, flip) = S(f.Surface);
                var loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Reverse().Select(c => new BrepCoedge(c.Edge, !c.Forward)).ToList()).ToList();
                return f with { Surface = surface, Reversed = f.Reversed != flip, Loops = loops };
            }).ToList(),
        };
    }

    internal static BrepCurve TurnCurve(BrepCurve c, Turn t) => Curve(c, t);
    internal static BrepSurface TurnSurface(BrepSurface s, Turn t) => Surface(s, t);

    /// <summary>The body turned round an axis (see <see cref="Turn"/>).</summary>
    public static BrepBody Rotated(BrepBody body, Turn t) => new()
    {
        Sheet = body.Sheet,
        Vertices = body.Vertices.Select(t.Point).ToList(),
        Edges = body.Edges.Select(e => e with { Curve = Curve(e.Curve, t) }).ToList(),
        Faces = body.Faces.Select(f => f with { Surface = Surface(f.Surface, t) }).ToList(),
    };

    private static BrepCurve Curve(BrepCurve c, Turn t) => c switch
    {
        BrepLine => c,
        BrepCircle ci => ci with { Centre = t.Point(ci.Centre), Normal = t.Dir(ci.Normal), XAxis = t.Dir(ci.XAxis) },
        BrepEllipse el => el with { Centre = t.Point(el.Centre), Normal = t.Dir(el.Normal), XAxis = t.Dir(el.XAxis) },
        BrepBSpline bs => bs with { ControlPoints = bs.ControlPoints.Select(t.Point).ToList(), Helix = null },
        _ => throw new PushPullException(Lang.T("Dit lichaam heeft een rand die (nog) niet gedraaid kan worden.", "This body has an edge that cannot be rotated (yet).")),
    };

    private static BrepSurface Surface(BrepSurface s, Turn t) => s switch
    {
        BrepPlane p => p with { Origin = t.Point(p.Origin), Normal = t.Dir(p.Normal), XAxis = t.Dir(p.XAxis) },
        BrepCylinder c => c with { Origin = t.Point(c.Origin), Axis = t.Dir(c.Axis), XAxis = t.Dir(c.XAxis) },
        BrepCone c => c with { Origin = t.Point(c.Origin), Axis = t.Dir(c.Axis), XAxis = t.Dir(c.XAxis) },
        BrepSphere sp => sp with { Centre = t.Point(sp.Centre), Axis = t.Dir(sp.Axis), XAxis = t.Dir(sp.XAxis) },
        BrepTorus to => to with { Centre = t.Point(to.Centre), Axis = t.Dir(to.Axis), XAxis = t.Dir(to.XAxis) },
        BrepNurbsSurface n when n.Helix == null => n with { Points = n.Points.Select(t.Point).ToList() },
        _ => throw new PushPullException(Lang.T("Dit lichaam heeft een vlak dat (nog) niet gedraaid kan worden.", "This body has a face that cannot be rotated (yet).")),
    };

    public static BrepBody Translated(BrepBody body, Vec3 d) => new()
    {
        Sheet = body.Sheet,
        Vertices = body.Vertices.Select(v => v + d).ToList(),
        Edges = body.Edges.Select(e => e with { Curve = Curve(e.Curve, d) }).ToList(),
        Faces = body.Faces.Select(f => f with { Surface = Surface(f.Surface, d) }).ToList(),
    };

    private static BrepCurve Curve(BrepCurve c, Vec3 d) => c switch
    {
        BrepLine => c,
        BrepCircle ci => ci with { Centre = ci.Centre + d },
        BrepEllipse el => el with { Centre = el.Centre + d },
        BrepBSpline bs => bs with { ControlPoints = bs.ControlPoints.Select(p => p + d).ToList() },
        _ => throw new PushPullException(Lang.T("Dit lichaam heeft een rand die (nog) niet verplaatst kan worden.", "This body has an edge that cannot be moved (yet).")),
    };

    private static BrepSurface Surface(BrepSurface s, Vec3 d) => s switch
    {
        BrepPlane p => p with { Origin = p.Origin + d },
        BrepCylinder c => c with { Origin = c.Origin + d },
        BrepCone c => c with { Origin = c.Origin + d },
        BrepSphere sp => sp with { Centre = sp.Centre + d },
        BrepTorus t => t with { Centre = t.Centre + d },
        BrepNurbsSurface n => n with { Points = n.Points.Select(p => p + d).ToList() },
        _ => throw new PushPullException(Lang.T("Dit lichaam heeft een vlak dat (nog) niet verplaatst kan worden.", "This body has a face that cannot be moved (yet).")),
    };
}
