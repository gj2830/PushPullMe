using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class PushPull
{
    /// <summary>Whether a face is a rounding between flat faces (a cylinder whose straight edges it shares with them).</summary>
    public static bool IsRounding(BrepBody body, int face)
    {
        if (body.Faces[face].Surface is not BrepCylinder) return false;
        foreach (var c in body.Faces[face].Loops.SelectMany(l => l))
            if (body.Edges[c.Edge].Curve is BrepLine && body.Faces.Where((_, g) => g != face).Any(g => g.Surface is BrepPlane && g.Loops.Any(l => l.Any(x => x.Edge == c.Edge))))
                return true;
        return false;
    }

    /// <summary>
    /// A rounding between two flat faces A and B pushed or pulled: it gets another radius. In every cross-section the new
    /// rounding is the old one made larger or smaller from the sharp edge A and B would have (where their planes meet):
    /// the lines where it touches A and B slide over them, the arcs at its ends stay in their end faces, and the straight
    /// edges there get longer or shorter. Its face moves along its normal by <paramref name="distance"/> at its middle:
    /// pulling an outside rounding (towards the sharp edge) makes it smaller, pulling an inside one larger.
    /// </summary>
    private static PushPullResult Rounding(BrepBody body, int face, HashSet<int> group, HashSet<int> groupEdges, Dictionary<int, List<int>> facesOf, double distance)
    {
        var F = body.Faces[face];
        var cyl = (BrepCylinder)F.Surface;
        var axis = cyl.Axis.Normalized();
        double r = cyl.Radius;
        bool AlongAxis(Vec3 v) => Math.Abs(Math.Abs(v.Normalized().Dot(axis)) - 1) < 1e-9;
        double FromAxis(Vec3 p) { var d = p - cyl.Origin; return (d - axis * d.Dot(axis)).Length; }
        var (e0, m, touching) = SharpEdge(body, face, group, groupEdges, facesOf);
        Vec3 Edge(Vec3 p) => e0 + axis * (p - e0).Dot(axis);
        double m2 = m + (F.Reversed ? distance : -distance);
        double k = m2 / m;
        double nr = r * k;
        if (nr < 1e-5) throw new PushPullException(Lang.T("Zo wordt de ronding zo klein dat hij verdwijnt.", "That way the rounding gets so small that it disappears."));
        Vec3 Scale(Vec3 p) { var c = Edge(p); return c + (p - c) * k; }

        var verts = body.Vertices.ToList();
        var edges = body.Edges.ToList();
        var moving = new HashSet<int>();
        var ends = new HashSet<int>();
        foreach (int e in groupEdges)
        {
            var E = edges[e];
            switch (E.Curve)
            {
                case BrepCircle c when AlongAxis(c.Normal) && Math.Abs(c.Radius - r) < DistanceTol && FromAxis(c.Centre) < DistanceTol:
                    edges[e] = E with { Curve = c with { Centre = Scale(c.Centre), Radius = nr } };
                    foreach (int h in facesOf[e].Where(h => !group.Contains(h)))
                    {
                        if (body.Faces[h].Surface is not BrepPlane pl || !AlongAxis(pl.Normal))
                            throw new PushPullException(Lang.T("Aan het eind van deze ronding zit een ander rond of schuin vlak (bijvoorbeeld een hoekronding); dat kan nog niet.", "There is another round or slanting face at the end of this rounding (for example a corner rounding); that is not possible yet."));
                        ends.Add(h);
                    }
                    break;
                case BrepLine:
                    break;
                default:
                    throw new PushPullException(Lang.T("Aan het eind van deze ronding zit een ander rond of schuin vlak (bijvoorbeeld een hoekronding); dat kan nog niet.", "There is another round or slanting face at the end of this rounding (for example a corner rounding); that is not possible yet."));
            }
            if (E.Start >= 0) { moving.Add(E.Start); moving.Add(E.End); }
        }
        return RoundingRest(body, group, groupEdges, facesOf, touching, ends, moving, edges, nr, Scale);
    }

    /// <summary>The push/pull distance that gives a cylinder face (pin, hole or rounding) the radius
    /// <paramref name="radius"/>.</summary>
    public static double DistanceForRadius(BrepBody body, int face, double radius)
    {
        var F = body.Faces[face];
        if (IsRoundingSet(body, face))
        {
            // (the middle of a rounding is q·r from the sharp edge: r' = r − s·d/q)
            var (q, outside) = RoundingShape(body, face);
            return (outside ? 1 : -1) * (FaceRadius(F) - radius) * q;
        }
        double r = ((BrepCylinder)F.Surface).Radius;
        return F.Reversed ? r - radius : radius - r;
    }

    /// <summary>The radius a cylinder face (pin, hole or rounding, or a ball corner) gets when pushed or pulled over
    /// <paramref name="distance"/>.</summary>
    public static double RadiusAfter(BrepBody body, int face, double distance)
    {
        var F = body.Faces[face];
        if (IsRoundingSet(body, face))
        {
            var (q, outside) = RoundingShape(body, face);
            return FaceRadius(F) - (outside ? 1 : -1) * distance / q;
        }
        return ((BrepCylinder)F.Surface).Radius + (F.Reversed ? -distance : distance);
    }

    private static double FaceRadius(BrepFace f) => f.Surface switch { BrepCylinder c => c.Radius, BrepSphere s => s.Radius, BrepTorus t => t.MinorRadius, _ => 0 };

    /// <summary>The push/pull distance that gives a rounding the radius <paramref name="radius"/> (see
    /// <see cref="Rounding"/>).</summary>
    public static double RoundingDistanceFor(BrepBody body, int face, double radius)
    {
        var (group, groupEdges, facesOf) = CylinderGroup(body, face);
        var (_, m, _) = SharpEdge(body, face, group, groupEdges, facesOf);
        var F = body.Faces[face];
        double m2 = m * radius / ((BrepCylinder)F.Surface).Radius;
        return F.Reversed ? m2 - m : m - m2;
    }

    /// <summary>The radius a rounding gets when pushed or pulled over <paramref name="distance"/>.</summary>
    public static double RoundingRadiusAfter(BrepBody body, int face, double distance)
    {
        var (group, groupEdges, facesOf) = CylinderGroup(body, face);
        var (_, m, _) = SharpEdge(body, face, group, groupEdges, facesOf);
        var F = body.Faces[face];
        return ((BrepCylinder)F.Surface).Radius * (m + (F.Reversed ? distance : -distance)) / m;
    }

    /// <summary>
    /// Of a rounding: a point of the sharp edge its two flat faces would have, how far its middle is from there, and the
    /// faces it touches. Throws when it is not a rounding between two flat faces at an angle.
    /// </summary>
    private static (Vec3 E0, double M, HashSet<int> Touching) SharpEdge(BrepBody body, int face, HashSet<int> group, HashSet<int> groupEdges, Dictionary<int, List<int>> facesOf)
    {
        var F = body.Faces[face];
        var cyl = (BrepCylinder)F.Surface;
        var axis = cyl.Axis.Normalized();
        double r = cyl.Radius;

        if (groupEdges.Any(e => body.Edges[e].Curve is not (BrepLine or BrepCircle)))
            throw new PushPullException(Lang.T("Deze ronding loopt over in een andere ronding (een hoekronding); dat kan nog niet.", "This rounding runs into another rounding (a corner rounding); that is not possible yet."));
        // the flat faces it touches (along its straight edges): two planes, tangent to it
        var touching = new HashSet<int>();
        foreach (int e in groupEdges)
            if (body.Edges[e].Curve is BrepLine)
                foreach (int h in facesOf[e].Where(h => !group.Contains(h)))
                {
                    if (body.Faces[h].Surface is not BrepPlane pl || Math.Abs(pl.Normal.Normalized().Dot(axis)) > 1e-9
                        || Math.Abs(Math.Abs((cyl.Origin - pl.Origin).Dot(pl.Normal.Normalized())) - r) > DistanceTol)
                        throw new PushPullException(Lang.T("Deze ronding loopt over in een vlak dat er niet plat tegenaan ligt; dat kan nog niet.", "This rounding runs into a face that does not lie flat against it; that is not possible yet."));
                    touching.Add(h);
                }
        var planes = new List<(Vec3 N, double D)>();
        foreach (int h in touching)
        {
            var pl = (BrepPlane)body.Faces[h].Surface;
            var n = pl.Normal.Normalized();
            double d = n.Dot(pl.Origin);
            if (!planes.Any(q => (Math.Abs(q.N.Dot(n) - 1) < 1e-9 && Math.Abs(q.D - d) < DistanceTol) || (Math.Abs(q.N.Dot(n) + 1) < 1e-9 && Math.Abs(q.D + d) < DistanceTol)))
                planes.Add((n, d));
        }
        if (planes.Count != 2) throw new PushPullException(Lang.T("Deze ronding ligt niet tussen precies twee platte vlakken; dat kan nog niet.", "This rounding does not lie between exactly two flat faces; that is not possible yet."));
        var (nA, dA) = planes[0]; var (nB, dB) = planes[1];
        double det = nA.Dot(nB.Cross(axis));
        if (Math.Abs(det) < 1e-9)
            throw new PushPullException(Lang.T("Deze ronding ligt tussen twee evenwijdige vlakken (een halve cirkel); de straal kan dan niet veranderen zonder die vlakken te verschuiven.", "This rounding lies between two parallel faces (a half circle); its radius cannot change without moving those faces."));
        // a point of the sharp edge (on both planes, and level with the cylinder's origin along the axis)
        var e0 = (nB.Cross(axis) * dA + axis.Cross(nA) * dB + nA.Cross(nB) * axis.Dot(cyl.Origin)) * (1 / det);
        // the middle of the rounding is m from the sharp edge
        var dc = cyl.Origin - e0;
        double m = (dc - axis * dc.Dot(axis)).Length - r;
        if (m < 1e-9) throw new PushPullException(Lang.T("Deze ronding heeft geen scherpe rand om naar toe te schuiven; dat kan nog niet.", "This rounding has no sharp edge to move towards; that is not possible yet."));
        return (e0, m, touching);
    }

    private static PushPullResult RoundingRest(BrepBody body, HashSet<int> group, HashSet<int> groupEdges, Dictionary<int, List<int>> facesOf,
        HashSet<int> touching, HashSet<int> ends, HashSet<int> moving, List<BrepEdge> edges, double nr, Func<Vec3, Vec3> Scale)
    {
        var verts = body.Vertices.ToList();
        // the edges that come out of its corners (in the end faces, along A or B): straight, and they must not turn over
        var changed = new HashSet<int>(group);
        changed.UnionWith(touching); changed.UnionWith(ends);
        var newVerts = verts.ToList();
        foreach (int v in moving) newVerts[v] = Scale(verts[v]);
        for (int e = 0; e < edges.Count; e++)
        {
            if (groupEdges.Contains(e)) continue;
            var E = edges[e];
            if (!(moving.Contains(E.Start) || moving.Contains(E.End))) continue;
            if (E.Curve is not BrepLine)
                throw new PushPullException(Lang.T("Op een hoek van deze ronding komt een gebogen rand uit; dat kan nog niet.", "A curved edge ends at a corner of this rounding; that is not possible yet."));
            if ((newVerts[E.End] - newVerts[E.Start]).Dot(verts[E.End] - verts[E.Start]) <= 0)
                throw new PushPullException(Lang.T("Zo groot past de ronding niet: hij komt voorbij de rand van een vlak ernaast.", "A rounding that big does not fit: it reaches past the edge of a face next to it."));
            foreach (int h in facesOf.GetValueOrDefault(e, [])) changed.Add(h);
        }
        var faces = body.Faces.Select((g, i) => group.Contains(i)
            ? g with { Surface = ((BrepCylinder)g.Surface) with { Origin = Scale(((BrepCylinder)g.Surface).Origin), Radius = nr } } : g).ToList();
        var result = new BrepBody { Vertices = newVerts, Edges = edges, Faces = faces };
        result = new BrepBody { Vertices = newVerts, Edges = edges, Faces = OrderLoops(result) };
        var list = changed.Order().ToList();
        Check(result, list);
        if (SignedVolume(result) is double vol && vol <= 0) throw new PushPullException(Lang.T("Zo klopt de vorm niet meer (binnenstebuiten).", "That way the shape is no longer right (inside out)."));

        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (int g in group)
            if (FaceGeometry.CurvedTriangles(result, result.Faces[g]) is { } t) curved[g] = t;
        return new PushPullResult(result, Enumerable.Range(0, faces.Count).ToList(), [], [], curved, list, []);
    }
}
