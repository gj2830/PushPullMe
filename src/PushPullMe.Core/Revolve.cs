using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// A planar face turned out of its body round an axis in its plane (as DesignSpark's pull with a revolve axis, SketchUp's
/// Follow Me round a circle): the face moves along an arc to its new place (the end cap), every edge of it sweeps a
/// side face (a line parallel to the axis a cylinder, square to it a plane, slanting a cone; an arc a torus, or a sphere
/// when it is centred on the axis), every corner an arc. Edges on the axis stay where they are. The face must lie on one
/// side of the axis (touching it is fine), and the angle is less than a full turn.
/// </summary>
public static class Revolve
{
    private const double Tol = 1e-9;

    /// <summary>The result: the body, the end cap (the face's own number) and the side faces (new, last).</summary>
    public sealed record Result(BrepBody Body, int Cap, IReadOnlyList<int> Sides);

    /// <summary>
    /// The axis direction to turn round so that a positive angle moves the face out of the body (along its normal), or
    /// throws when the face does not lie on one side of the line through <paramref name="p"/> along
    /// <paramref name="dir"/> in its plane.
    /// </summary>
    public static Vec3 OutwardAxis(BrepBody body, int face, Vec3 p, Vec3 dir)
    {
        var f = body.Faces[face];
        if (f.Surface is not BrepPlane pl) throw new PushPullException("Ronddraaien kan alleen met een plat vlak.");
        var n = FaceGeometry.Normal(f); var a = dir.Normalized();
        if (Math.Abs(a.Dot(n)) > 1e-6 || Math.Abs((p - pl.Origin).Dot(n)) > 1e-7) throw new PushPullException("De as moet in het vlak liggen.");
        var side = n.Cross(a); // in the plane, square to the axis
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var l in f.Loops)
            foreach (var q in FaceGeometry.LoopPoints(body, l)) { double s = (q - p).Dot(side); lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
        if (lo < -1e-7 && hi > 1e-7) throw new PushPullException("Het vlak ligt aan beide kanten van de as; kies een as op of naast het vlak.");
        // turning round a, a point at s·side moves along a × (s·side) = s·n: out of the body where s > 0
        return hi > 1e-7 ? a : a * -1;
    }

    /// <summary>Face <paramref name="face"/> turned out of the body by <paramref name="angle"/> (radians, 0..2π) round the
    /// axis through <paramref name="p"/> along <paramref name="axis"/> (from <see cref="OutwardAxis"/>).</summary>
    public static Result Face(BrepBody body, int face, Vec3 p, Vec3 axis, double angle)
    {
        if (angle <= 1e-9) throw new PushPullException("Draai het vlak een stukje naar buiten.");
        if (angle >= 2 * Math.PI - 1e-9) throw new PushPullException("Een hele slag rond kan nog niet; kies minder dan 360°.");
        var F = body.Faces[face];
        var a = axis.Normalized();
        var turn = new BrepTransform.Turn(p, a, angle);
        Vec3 Foot(Vec3 q) => p + a * (q - p).Dot(a);
        bool OnAxis(Vec3 q) => (q - Foot(q)).Length < 1e-7;

        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var faces = body.Faces.ToList();
        var used = F.Loops.SelectMany(l => l).Select(c => c.Edge).Distinct().ToList();
        // the corners of the face: where they go (the same vertex on the axis), and the arc each sweeps
        var turnedVertex = new Dictionary<int, int>(); var arc = new Dictionary<int, int>();
        foreach (int v in used.SelectMany(e => new[] { edges[e].Start, edges[e].End }).Where(v => v >= 0).Distinct())
        {
            var q = verts[v];
            if (OnAxis(q)) { turnedVertex[v] = v; continue; }
            int nv = verts.Count; verts.Add(turn.Point(q)); turnedVertex[v] = nv;
            var foot = Foot(q);
            arc[v] = edges.Count;
            edges.Add(new BrepEdge(v, nv, new BrepCircle(foot, a, (q - foot).Normalized(), (q - foot).Length)));
        }
        // the edges of the end cap (one on the axis stays itself)
        var turnedEdge = new Dictionary<int, int>();
        foreach (int e in used)
        {
            var E = edges[e];
            if (E.Curve is not (BrepLine or BrepCircle)) throw new PushPullException("Ronddraaien kan (nog) alleen met rechte randen en bogen.");
            if (E.Start >= 0 && E.Curve is BrepLine && OnAxis(verts[E.Start]) && OnAxis(verts[E.End])) { turnedEdge[e] = e; continue; }
            turnedEdge[e] = edges.Count;
            edges.Add(new BrepEdge(E.Start >= 0 ? turnedVertex[E.Start] : -1, E.End >= 0 ? turnedVertex[E.End] : -1, BrepTransform.TurnCurve(E.Curve, turn)));
        }
        // the end cap in the face's place (its number kept), its loops along the turned edges
        faces[face] = new BrepFace(BrepTransform.TurnSurface(F.Surface, turn), F.Reversed,
            F.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(turnedEdge[c.Edge], c.Forward)).ToList()).ToList());

        // a side face for every edge not on the axis
        var n = FaceGeometry.Normal(F);
        var half = new BrepTransform.Turn(p, a, angle / 2);
        var sides = new List<int>();
        foreach (var loop in F.Loops)
            foreach (var c in loop)
            {
                var E = body.Edges[c.Edge];
                if (turnedEdge[c.Edge] == c.Edge) continue;
                int from = c.Forward ? E.Start : E.End, to = c.Forward ? E.End : E.Start;
                var ring = new List<BrepCoedge> { c };
                if (to >= 0 && arc.TryGetValue(to, out int at)) ring.Add(new BrepCoedge(at, true));
                ring.Add(new BrepCoedge(turnedEdge[c.Edge], !c.Forward));
                if (from >= 0 && arc.TryGetValue(from, out int af)) ring.Add(new BrepCoedge(af, false));
                var loops = new List<IReadOnlyList<BrepCoedge>>();
                // (a ring edge sweeps a band: its two circles are loops of their own)
                if (E.Start < 0) loops.AddRange([[c], [new BrepCoedge(turnedEdge[c.Edge], !c.Forward)]]);
                else loops.Add(ring);
                var (surface, mid, inward) = Swept(body, E, c.Forward, n, p, a);
                // the face normal points away from the solid swept: against the face's inward direction there, turned halfway
                var at2 = half.Point(mid); var inward2 = half.Dir(inward);
                bool reversed = NaturalNormal(surface, at2).Dot(inward2) > 0;
                sides.Add(faces.Count);
                faces.Add(new BrepFace(surface, reversed, loops));
            }
        return new Result(new BrepBody { Vertices = verts, Edges = edges, Faces = faces }, face, sides);
    }

    /// <summary>The surface an edge of the face sweeps round the axis; and a point in the middle of the edge with the
    /// direction into the face there.</summary>
    private static (BrepSurface Surface, Vec3 Mid, Vec3 Inward) Swept(BrepBody body, BrepEdge e, bool forward, Vec3 n, Vec3 p, Vec3 a)
    {
        Vec3 Foot(Vec3 q) => p + a * (q - p).Dot(a);
        var pts = FaceGeometry.EdgePoints(body, e);
        if (!forward) pts.Reverse();
        int m = pts.Count / 2;
        var mid = pts.Count == 2 ? (pts[0] + pts[1]) * 0.5 : pts[m];
        var tangent = pts.Count == 2 ? (pts[1] - pts[0]).Normalized() : (pts[Math.Min(m + 1, pts.Count - 1)] - pts[Math.Max(m - 1, 0)]).Normalized();
        // (loops run counter-clockwise round the normal: the face is to the left)
        var inward = n.Cross(tangent).Normalized();
        var radial = mid - Foot(mid);
        var x = radial.Length > 1e-12 ? radial.Normalized() : n.Cross(a).Normalized();
        BrepSurface s;
        if (e.Curve is BrepCircle c)
        {
            var cf = Foot(c.Centre); double big = (c.Centre - cf).Length;
            if (big < 1e-7) s = new BrepSphere(cf, c.Radius, a, x);
            else
            {
                if (big <= c.Radius + 1e-7) throw new PushPullException("Een boog die tot over de as reikt, geeft een vorm die (nog) niet kan; kies een as verder van de boog.");
                s = new BrepTorus(cf, a, (c.Centre - cf).Normalized(), big, c.Radius);
            }
        }
        else
        {
            Vec3 q0 = body.Vertices[e.Start], q1 = body.Vertices[e.End];
            double r0 = (q0 - Foot(q0)).Length, r1 = (q1 - Foot(q1)).Length, h = (q1 - q0).Dot(a);
            if (Math.Abs(r1 - r0) < 1e-9) s = new BrepCylinder(Foot(q0), a, x, r0);
            else if (Math.Abs(h) < 1e-9) s = new BrepPlane(Foot(q0), a, x);
            else
            {
                // a cone: from the narrow end, growing along its axis
                var (qa, ra, qb, rb) = r0 < r1 ? (q0, r0, q1, r1) : (q1, r1, q0, r0);
                var ax = (Foot(qb) - Foot(qa)).Normalized();
                s = new BrepCone(Foot(qa), ax, x, ra, Math.Atan2(rb - ra, (Foot(qb) - Foot(qa)).Length));
            }
        }
        return (s, mid, inward);
    }

    /// <summary>A surface's natural normal at a point on it.</summary>
    private static Vec3 NaturalNormal(BrepSurface s, Vec3 q)
    {
        switch (s)
        {
            case BrepPlane pl: return pl.Normal.Normalized();
            case BrepCylinder cy: { var r = q - cy.Origin; return (r - cy.Axis * r.Dot(cy.Axis)).Normalized(); }
            case BrepCone co:
            {
                var r = q - co.Origin; var radial = (r - co.Axis * r.Dot(co.Axis)).Normalized();
                return (radial * Math.Cos(co.HalfAngle) - co.Axis.Normalized() * Math.Sin(co.HalfAngle)).Normalized();
            }
            case BrepSphere sp: return (q - sp.Centre).Normalized();
            case BrepTorus to:
            {
                var r = q - to.Centre; var radial = (r - to.Axis * r.Dot(to.Axis)).Normalized();
                return (q - (to.Centre + radial * to.MajorRadius)).Normalized();
            }
            default: throw new NotSupportedException(s.GetType().Name);
        }
    }
}
