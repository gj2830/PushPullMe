using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class PushPull
{
    /// <summary>
    /// A face on a cylinder pushed or pulled (as DesignSpark does; SketchUp only pulls flat faces): it moves along its
    /// outward normal, so its radius changes: a pin gets thicker when pulled, a hole narrower (its face points into the
    /// hole). All faces of that cylinder that hang together go along (a hole in two halves), the circles at its ends
    /// too, in the flat faces they lie in (perpendicular to the axis). A rounding between two flat faces gets another
    /// radius (<see cref="Rounding"/>). Not yet: one with a rounding, chamfer or slanted cut at its end. Face numbers stay
    /// the same.
    /// </summary>
    /// <summary>
    /// The faces of the cylinder of <paramref name="face"/> that hang together with it (the same axis and radius, facing
    /// the same way), their edges, and per edge the faces that use it.
    /// </summary>
    private static (HashSet<int> Group, HashSet<int> GroupEdges, Dictionary<int, List<int>> FacesOf) CylinderGroup(BrepBody body, int face)
    {
        var F = body.Faces[face];
        var cyl = (BrepCylinder)F.Surface;
        var axis = cyl.Axis.Normalized();
        double r = cyl.Radius;
        bool OnAxis(Vec3 p) { var d = p - cyl.Origin; return (d - axis * d.Dot(axis)).Length < DistanceTol; }
        bool AlongAxis(Vec3 v) => Math.Abs(Math.Abs(v.Normalized().Dot(axis)) - 1) < 1e-9;
        bool Same(BrepFace g) => g.Surface is BrepCylinder c && g.Reversed == F.Reversed && Math.Abs(c.Radius - r) < DistanceTol && AlongAxis(c.Axis) && OnAxis(c.Origin);
        var facesOf = new Dictionary<int, List<int>>();
        for (int g = 0; g < body.Faces.Count; g++)
            foreach (var c in body.Faces[g].Loops.SelectMany(l => l))
                (facesOf.TryGetValue(c.Edge, out var l) ? l : facesOf[c.Edge] = []).Add(g);
        var group = new HashSet<int> { face };
        var queue = new Queue<int>([face]);
        while (queue.Count > 0)
            foreach (var c in body.Faces[queue.Dequeue()].Loops.SelectMany(l => l))
                foreach (int h in facesOf[c.Edge])
                    if (!group.Contains(h) && Same(body.Faces[h])) { group.Add(h); queue.Enqueue(h); }
        var groupEdges = group.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => c.Edge).ToHashSet();
        return (group, groupEdges, facesOf);
    }

    public static PushPullResult Cylinder(BrepBody body, int face, double distance)
    {
        var F = body.Faces[face];
        var cyl = (BrepCylinder)F.Surface;
        var axis = cyl.Axis.Normalized();
        double r = cyl.Radius, nr = r + (F.Reversed ? -distance : distance);

        bool OnAxis(Vec3 p) { var d = p - cyl.Origin; return (d - axis * d.Dot(axis)).Length < DistanceTol; }
        bool AlongAxis(Vec3 v) => Math.Abs(Math.Abs(v.Normalized().Dot(axis)) - 1) < 1e-9;
        var (group, groupEdges, facesOf) = CylinderGroup(body, face);
        var verts = body.Vertices.ToList();
        var edges = body.Edges.ToList();
        // a rounding between two flat faces (its straight edges shared with them): another radius, see Rounding
        if (groupEdges.Any(e => body.Edges[e].Curve is BrepLine && facesOf[e].Any(h => !group.Contains(h))))
            return Rounding(body, face, group, groupEdges, facesOf, distance);
        if (nr < 1e-5) throw new PushPullException(F.Reversed ? Lang.T("Zo wordt het gat zo klein dat het verdwijnt.", "That way the hole gets so small that it disappears.") : Lang.T("Zo wordt de cilinder zo dun dat hij verdwijnt.", "That way the cylinder gets so thin that it disappears."));
        var moving = new HashSet<int>();
        var neighbours = new HashSet<int>();
        foreach (int e in groupEdges)
        {
            var E = edges[e];
            var others = facesOf[e].Where(h => !group.Contains(h)).Distinct().ToList();
            switch (E.Curve)
            {
                case BrepCircle c when OnAxis(c.Centre) && AlongAxis(c.Normal) && Math.Abs(c.Radius - r) < DistanceTol:
                    edges[e] = E with { Curve = c with { Radius = nr } };
                    foreach (int h in others)
                    {
                        if (body.Faces[h].Surface is not BrepPlane pl || !AlongAxis(pl.Normal))
                            throw new PushPullException(Lang.T("Aan de rand van deze cilinder zit een ronding of een schuine kant; dan kan de straal (nog) niet veranderen.", "There is a rounding or a chamfer at the edge of this cylinder; then the radius cannot change (yet)."));
                        neighbours.Add(h);
                    }
                    break;
                case BrepLine when others.Count == 0:
                    break; // a seam between parts of the cylinder
                case BrepLine:
                    throw new PushPullException(Lang.T("Deze cilinder loopt over in een plat vlak (een ronding tussen vlakken, of een afgevlakte kant); de straal daarvan kan (nog) niet veranderen.", "This cylinder runs into a flat face (a rounding between faces, or a flattened side); its radius cannot change (yet)."));
                default:
                    throw new PushPullException(Lang.T("Deze cilinder heeft een rand die geen cirkel is (bijvoorbeeld waar hij een ander vlak schuin snijdt); dat kan nog niet.", "This cylinder has an edge that is not a circle (for example where it cuts another face at a slant); that is not possible yet."));
            }
            if (E.Start >= 0) { moving.Add(E.Start); moving.Add(E.End); }
        }
        // (a corner on the circle where another edge comes out: that edge would have to change too)
        for (int e = 0; e < edges.Count; e++)
            if (!groupEdges.Contains(e) && (moving.Contains(edges[e].Start) || moving.Contains(edges[e].End)))
                throw new PushPullException(Lang.T("Op de rand van deze cilinder komt een andere rand uit; dan kan de straal (nog) niet veranderen.", "Another edge ends on the edge of this cylinder; then the radius cannot change (yet)."));
        foreach (int v in moving)
        {
            var p = verts[v];
            var foot = cyl.Origin + axis * (p - cyl.Origin).Dot(axis);
            verts[v] = foot + (p - foot).Normalized() * nr;
        }
        var faces = body.Faces.Select((g, i) => group.Contains(i) ? g with { Surface = ((BrepCylinder)g.Surface) with { Radius = nr } } : g).ToList();
        var result = new BrepBody { Vertices = verts, Edges = edges, Faces = faces };
        result = new BrepBody { Vertices = verts, Edges = edges, Faces = OrderLoops(result) };

        var changed = group.Concat(neighbours).Order().ToList();
        Check(result, changed);
        // a hole that ended up outside its face (a pin made thinner than a hole in its top)
        foreach (int h in neighbours)
        {
            var g = result.Faces[h];
            if (g.Loops.Count < 2) continue;
            var n = FaceGeometry.Normal(g);
            var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
            var y = n.Cross(x);
            var outer = FaceGeometry.LoopPoints(result, g.Loops[0]).Select(q => (X: q.Dot(x), Y: q.Dot(y))).ToList();
            foreach (var hole in g.Loops.Skip(1))
            {
                var q = FaceGeometry.LoopPoints(result, hole)[0];
                double qx = q.Dot(x), qy = q.Dot(y);
                bool inside = false;
                for (int i = 0, j = outer.Count - 1; i < outer.Count; j = i++)
                    if ((outer[i].Y > qy) != (outer[j].Y > qy) && qx < (outer[j].X - outer[i].X) * (qy - outer[i].Y) / (outer[j].Y - outer[i].Y) + outer[i].X)
                        inside = !inside;
                if (!inside) throw new PushPullException(Lang.T("Zo komt een gat buiten het vlak te liggen; dat kan niet.", "That way a hole would lie outside the face; that is not possible."));
            }
        }
        if (SignedVolume(result) is double vol && vol <= 0) throw new PushPullException(Lang.T("Zo klopt de vorm niet meer (binnenstebuiten).", "That way the shape is no longer right (inside out)."));

        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (int g in group)
            if (FaceGeometry.CurvedTriangles(result, result.Faces[g]) is { } t) curved[g] = t;
        return new PushPullResult(result, Enumerable.Range(0, faces.Count).ToList(), [], [], curved, changed, []);
    }
}
