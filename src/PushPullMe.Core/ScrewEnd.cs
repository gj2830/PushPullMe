using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// The end face of a thread (a profile swept along a helix, as DesignSpark makes it: testdata/helix.rsdocx) pushed or
/// pulled along the helix, as DesignSpark does, not straight along its normal. Such a piece lies between two flat end
/// faces; its sides are coaxial cylinders and helical faces (B-surfaces with a helix form) of one axis and pitch, its
/// side edges helices from one end face to the other. The moved end is the other end face screwed on (turned round the
/// axis and moved along it by the pitch per turn); the side edges and the helical faces are made anew from the end that
/// stays (<see cref="ThreadShapes"/>), for as many turns as the piece now has.
/// </summary>
public static class ScrewEnd
{
    /// <summary>
    /// The screw a thread's end face moves along: the axis (through <see cref="Point"/>; turning counter-clockwise round
    /// <see cref="Out"/> takes the end further from the end that stays), the pitch, the hand, how far the face's middle
    /// goes per radian (along the helix), and the end face that stays.
    /// </summary>
    public sealed record Screw(Vec3 Point, Vec3 Out, double Pitch, bool Right, double PerRadian, int Fixed);

    private const double Tol = 1e-7;

    /// <summary>The screw when face <paramref name="face"/> is such an end face (see <see cref="ScrewEnd"/>), else null.</summary>
    public static Screw? Of(BrepBody body, int face)
    {
        var f = body.Faces[face];
        if (f.Surface is not BrepPlane || f.Loops.Count != 1) return null;
        var mine = f.Loops[0].Select(c => c.Edge).ToHashSet();
        var corners = mine.SelectMany(e => new[] { body.Edges[e].Start, body.Edges[e].End }).Where(v => v >= 0).ToHashSet();
        // the helix: of a helical face next to it
        HelixForm? form = null;
        var round = new List<BrepSurface>();
        for (int g = 0; g < body.Faces.Count; g++)
        {
            if (g == face || !body.Faces[g].Loops.SelectMany(l => l).Any(c => mine.Contains(c.Edge))) continue;
            switch (body.Faces[g].Surface)
            {
                case BrepNurbsSurface { Helix: { } h }: form ??= h; round.Add(body.Faces[g].Surface); break;
                case BrepCylinder: round.Add(body.Faces[g].Surface); break;
                default: return null;
            }
        }
        if (form is not { Pitch: > 0 } hx) return null;
        var a = hx.AxisDir.Normalized(); var p0 = hx.AxisPoint;
        bool OnAxis(Vec3 p, Vec3 d) => d.Normalized().Cross(a).Length < 1e-9 && ((p - p0) - a * (p - p0).Dot(a)).Length < 1e-7;
        foreach (var s in round)
            if (s switch
            {
                BrepCylinder c => !OnAxis(c.Origin, c.Axis),
                BrepNurbsSurface { Helix: { } h } => !OnAxis(h.AxisPoint, h.AxisDir) || Math.Abs(h.Pitch - hx.Pitch) > 1e-9 || h.RightHanded != hx.RightHanded,
                _ => true,
            }) return null;
        // every corner one side edge, a B-spline, to a corner of one other flat face (the end that stays)
        var side = SideEdges(body, face, mine, corners);
        if (side == null) return null;
        var far = side.Select(x => x.Far).ToHashSet();
        int fixedFace = -1;
        for (int g = 0; g < body.Faces.Count; g++)
            if (g != face && body.Faces[g] is { Surface: BrepPlane, Loops: [var l] } && l.Count == f.Loops[0].Count
                && l.SelectMany(c => new[] { body.Edges[c.Edge].Start, body.Edges[c.Edge].End }).ToHashSet().SetEquals(far)) fixedFace = g;
        if (fixedFace < 0) return null;
        // which way round goes on from the end that stays: along the axis as far as the hand says
        var (v0, far0) = (body.Vertices[side[0].Near], body.Vertices[side[0].Far]);
        double h0 = (v0 - far0).Dot(a);
        var outAxis = h0 * (hx.RightHanded ? 1 : -1) > 0 ? a : a * -1;
        // (the face's middle: its corners' mean, how far from the axis)
        var mid = corners.Aggregate(new Vec3(0, 0, 0), (s, v) => s + body.Vertices[v]) * (1.0 / corners.Count);
        double r = ((mid - p0) - a * (mid - p0).Dot(a)).Length;
        double climb = hx.Pitch / (2 * Math.PI);
        return new Screw(p0, outAxis, hx.Pitch, hx.RightHanded, Math.Sqrt(r * r + climb * climb), fixedFace);
    }

    /// <summary>Per corner of the face its one edge off the face (a B-spline) and the corner at its other end, not on the
    /// face; null when it is not so.</summary>
    private static List<(int Edge, int Near, int Far)>? SideEdges(BrepBody body, int face, HashSet<int> mine, HashSet<int> corners)
    {
        var result = new List<(int, int, int)>();
        foreach (int v in corners)
        {
            var off = Enumerable.Range(0, body.Edges.Count).Where(e => !mine.Contains(e) && (body.Edges[e].Start == v || body.Edges[e].End == v)).ToList();
            if (off is not [var e] || body.Edges[e].Curve is not BrepBSpline) return null;
            int far = body.Edges[e].Start == v ? body.Edges[e].End : body.Edges[e].Start;
            if (far < 0 || corners.Contains(far)) return null;
            result.Add((e, v, far));
        }
        return result;
    }

    /// <summary>
    /// The body with end face <paramref name="face"/> moved <paramref name="distance"/> (metres, along the helix at the
    /// face's middle; out of the body when positive) along screw <paramref name="s"/> (from <see cref="Of"/>). The faces
    /// keep their numbers.
    /// </summary>
    public static BrepBody Moved(BrepBody body, int face, Screw s, double distance)
    {
        var f = body.Faces[face];
        var mine = f.Loops[0].Select(c => c.Edge).ToHashSet();
        var corners = mine.SelectMany(e => new[] { body.Edges[e].Start, body.Edges[e].End }).Where(v => v >= 0).ToHashSet();
        var side = SideEdges(body, face, mine, corners)!;
        var a = s.Out.Normalized();
        Vec3 Foot(Vec3 p) => s.Point + a * (p - s.Point).Dot(a);
        // how far round the piece goes now: by the angle round the axis from the end that stays (exact: both end faces
        // lie through the axis), the whole turns by the climb (DesignSpark's helices climb within 0.01 mm only); and after
        var (n0, f0) = (body.Vertices[side[0].Near], body.Vertices[side[0].Far]);
        Vec3 Radial(Vec3 p) => p - Foot(p);
        double part = Math.Atan2(Radial(f0).Cross(Radial(n0)).Dot(a), Radial(f0).Dot(Radial(n0)));
        if (part < 0) part += 2 * Math.PI;
        double climbNow = side.Average(x => Math.Abs((body.Vertices[x.Near] - body.Vertices[x.Far]).Dot(a))) / s.Pitch;
        double turnsNow = Math.Round(climbNow - part / (2 * Math.PI)) + part / (2 * Math.PI);
        double turns = turnsNow + distance / s.PerRadian / (2 * Math.PI);
        if (turns < 1e-3) throw new PushPullException(Lang.T("Zo ver kan het eind van de schroefdraad niet terug: er blijft niets van over.", "The end of the thread cannot go back that far: nothing of it would be left."));
        // the side edges anew: helices from the corner that stays, as many turns
        var helices = new Dictionary<int, BrepBSpline>();
        var verts = body.Vertices.ToList(); var edges = body.Edges.ToList();
        var flip = new HashSet<int>();
        foreach (var (e, near, far) in side)
        {
            var q = body.Vertices[far]; var foot = Foot(q);
            var h = ThreadShapes.Helix(foot, a, q - foot, (q - foot).Length, 0, s.Pitch, s.Right, turns);
            helices[e] = h;
            verts[near] = h.At(h.End);
            // (the edge runs from the corner that stays, as the helix does)
            if (body.Edges[e].Start != far) flip.Add(e);
            edges[e] = new BrepEdge(far, near, h);
        }
        // the end face: the one that stays, screwed on as far, facing the other way
        var fx = (BrepPlane)body.Faces[s.Fixed].Surface;
        var nFixed = FaceGeometry.Normal(body.Faces[s.Fixed]);
        double angle = 2 * Math.PI * turns;
        var turn = new BrepTransform.Turn(s.Point, a, angle);
        var climb = a * ((s.Right ? 1 : -1) * s.Pitch * turns);
        Vec3 Screwed(Vec3 p) => turn.Point(p) + climb;
        var n = turn.Dir(nFixed) * -1;
        var plane = new BrepPlane(Screwed(fx.Origin), n, turn.Dir(fx.XAxis));
        // the helical faces anew between their two new side edges
        var faces = body.Faces.ToList();
        for (int g = 0; g < faces.Count; g++)
        {
            var bf = faces[g];
            if (flip.Count > 0) bf = bf with { Loops = bf.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => flip.Contains(c.Edge) ? new BrepCoedge(c.Edge, !c.Forward) : c).ToList()).ToList() };
            if (g == face) bf = bf with { Surface = plane, Reversed = false };
            else if (bf.Surface is BrepNurbsSurface old && bf.Loops.SelectMany(l => l).Select(c => c.Edge).Where(helices.ContainsKey).Distinct().ToList() is [var e0, var e1])
            {
                // (the first row of the old surface along the edge nearest to it, so the new one runs the same way)
                var first = old.Points[0];
                double D(int e) => Math.Min((((BrepBSpline)body.Edges[e].Curve).ControlPoints[0] - first).Length, (((BrepBSpline)body.Edges[e].Curve).ControlPoints[^1] - first).Length);
                var (h0, h1) = D(e0) <= D(e1) ? (helices[e0], helices[e1]) : (helices[e1], helices[e0]);
                var surface = ThreadShapes.Ruled(h0, h1);
                // facing out: away from the middle of the cross-section there (the side helices at the same place along)
                double t = h0.Start + 0.5 * (h0.End - h0.Start);
                var at = surface.At(0.5, t);
                var middle = helices.Values.Aggregate(new Vec3(0, 0, 0), (acc, h) => acc + h.At(t)) * (1.0 / helices.Count);
                var du = surface.At(0.51, t) - surface.At(0.49, t);
                var dv = surface.At(0.5, Math.Min(h0.End, t + 1e-4)) - surface.At(0.5, Math.Max(h0.Start, t - 1e-4));
                bf = bf with { Surface = surface, Reversed = du.Cross(dv).Dot(at - middle) < 0 };
            }
            faces[g] = bf;
        }
        if ((Screwed(body.Vertices[side[0].Far]) - verts[side[0].Near]).Length > 1e-6)
            throw new PushPullException(Lang.T("De schroefdraad is niet te volgen (de randen liggen niet op één schroeflijn).", "The thread cannot be followed (its edges do not lie on one helix)."));
        return new BrepBody { Vertices = verts, Edges = edges, Faces = faces, Sheet = body.Sheet };
    }
}
