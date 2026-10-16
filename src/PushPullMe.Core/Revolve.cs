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
        if (f.Surface is not BrepPlane pl) throw new PushPullException(Lang.T("Ronddraaien kan alleen met een plat vlak.", "Follow Me only works with a flat face."));
        var n = FaceGeometry.Normal(f); var a = dir.Normalized();
        if (Math.Abs(a.Dot(n)) > 1e-6 || Math.Abs((p - pl.Origin).Dot(n)) > 1e-7) throw new PushPullException(Lang.T("De as moet in het vlak liggen.", "The axis must lie in the plane of the face."));
        var side = n.Cross(a); // in the plane, square to the axis
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var l in f.Loops)
            foreach (var q in FaceGeometry.LoopPoints(body, l)) { double s = (q - p).Dot(side); lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
        if (lo < -1e-7 && hi > 1e-7) throw new PushPullException(Lang.T("Het vlak ligt aan beide kanten van de as; kies een as op of naast het vlak.", "The face lies on both sides of the axis; choose an axis on or beside the face."));
        // turning round a, a point at s·side moves along a × (s·side) = s·n: out of the body where s > 0
        return hi > 1e-7 ? a : a * -1;
    }

    /// <summary>Face <paramref name="face"/> turned out of the body by <paramref name="angle"/> (radians, 0..2π) round the
    /// axis through <paramref name="p"/> along <paramref name="axis"/> (from <see cref="OutwardAxis"/>).</summary>
    public static Result Face(BrepBody body, int face, Vec3 p, Vec3 axis, double angle)
    {
        if (angle <= 1e-9) throw new PushPullException(Lang.T("Draai het vlak een stukje naar buiten.", "Turn the face out a little."));
        if (angle >= 2 * Math.PI - 1e-9) throw new PushPullException(Lang.T("Een hele slag rond kan nog niet; kies minder dan 360°.", "A whole turn is not possible yet; choose less than 360°."));
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
            if (E.Curve is not (BrepLine or BrepCircle)) throw new PushPullException(Lang.T("Ronddraaien kan (nog) alleen met rechte randen en bogen.", "Follow Me only works with straight edges and arcs (for now)."));
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

    /// <summary>
    /// The solid the face sweeps in a whole turn round the axis, as a body of its own (to be united with the body it is
    /// on): every edge of the face (not on the axis) a whole surface of revolution, every corner (not on the axis) a ring
    /// edge between two of them. A corner on the axis must be where a flat end (a disc) meets the axis: a cone's tip or a
    /// sphere's pole there is not possible yet.
    /// </summary>
    public static BrepBody Solid(BrepBody body, int face, Vec3 p, Vec3 axis)
    {
        var F = body.Faces[face];
        var a = axis.Normalized();
        Vec3 Foot(Vec3 q) => p + a * (q - p).Dot(a);
        bool OnAxis(Vec3 q) => (q - Foot(q)).Length < 1e-7;
        var n = FaceGeometry.Normal(F);
        var edges = new List<BrepEdge>(); var ring = new Dictionary<int, int>();
        int Ring(int v)
        {
            if (ring.TryGetValue(v, out int e)) return e;
            var q = body.Vertices[v]; var foot = Foot(q);
            e = edges.Count; edges.Add(new BrepEdge(-1, -1, new BrepCircle(foot, a, (q - foot).Normalized(), (q - foot).Length)));
            ring[v] = e;
            return e;
        }
        var faces = new List<BrepFace>();
        foreach (var loop in F.Loops)
            foreach (var c in loop)
            {
                var E = body.Edges[c.Edge];
                if (E.Start < 0) throw new PushPullException(Lang.T("Een hele slag met een rond gat in het vlak kan nog niet.", "A whole turn with a round hole in the face is not possible yet."));
                if (E.Curve is not (BrepLine or BrepCircle)) throw new PushPullException(Lang.T("Ronddraaien kan (nog) alleen met rechte randen en bogen.", "Revolving only works (for now) with straight edges and arcs."));
                int from = c.Forward ? E.Start : E.End, to = c.Forward ? E.End : E.Start;
                Vec3 qf = body.Vertices[from], qt = body.Vertices[to];
                if (E.Curve is BrepLine && OnAxis(qf) && OnAxis(qt)) continue;
                var (surface, mid, inward) = Swept(body, E, c.Forward, n, p, a);
                if ((OnAxis(qf) || OnAxis(qt)) && surface is not BrepPlane)
                    throw new PushPullException(Lang.T("Een hele slag met een punt of bol op de as kan nog niet (alleen een plat einde op de as).", "A whole turn with a tip or ball on the axis is not possible yet (only a flat end on the axis)."));
                bool reversed = NaturalNormal(surface, mid).Dot(inward) > 0;
                var normal = NaturalNormal(surface, mid) * (reversed ? -1 : 1);
                var loops = new List<IReadOnlyList<BrepCoedge>>();
                // a ring at each end off the axis, run so the face is on its left (counter-clockwise round the face normal)
                foreach (var (v, other) in new[] { (from, to), (to, from) })
                {
                    var q = body.Vertices[v];
                    if (OnAxis(q)) continue;
                    var foot = Foot(q);
                    var tangent = a.Cross(q - foot).Normalized();
                    // (into the face from the ring: along the edge toward its other end)
                    var pts = FaceGeometry.EdgePoints(body, E);
                    if (pts.Count >= 2 && (pts[0] - q).Length > (pts[^1] - q).Length) pts.Reverse();
                    var into = (pts[1] - pts[0]).Normalized();
                    var nAt = NaturalNormal(surface, q) * (reversed ? -1 : 1);
                    bool forward = nAt.Cross(tangent).Dot(into) > 0;
                    loops.Add([new BrepCoedge(Ring(v), forward)]);
                }
                faces.Add(new BrepFace(surface, reversed, loops));
            }
        return new BrepBody { Vertices = [], Edges = edges, Faces = faces };
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
                if (big <= c.Radius + 1e-7) throw new PushPullException(Lang.T("Een boog die tot over de as reikt, geeft een vorm die (nog) niet kan; kies een as verder van de boog.", "An arc that reaches over the axis gives a shape that is not possible (yet); choose an axis further from the arc."));
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
