using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>Why a push/pull cannot be done (shown to the user as it is).</summary>
public class PushPullException(string message) : Exception(message)
{
    /// <summary>The same in English, whatever the app's language (for the AI assistant: its tools speak English); the
    /// message itself when it was not made with <see cref="Lang.T"/> right there.</summary>
    public string English { get; } = Lang.EnglishOf(message) ?? message;
}

/// <summary>The body would touch itself only along an edge (four faces round it): no solid. The piece can be made a body
/// of its own instead (as DesignSpark does).</summary>
public sealed class TouchesItselfException(string message) : PushPullException(message);

/// <summary>The result of <see cref="PushPull.Apply"/>.</summary>
/// <param name="Body">The new body.</param>
/// <param name="FaceMap">For every face of the old body its index in the new one (the face it became part of), or -1.</param>
/// <param name="NewFaces">The faces that are new (side faces that did not join a neighbour).</param>
/// <param name="GrownFaces">Faces (new numbers) that were there and grew or shrank because a side face joined them.</param>
/// <param name="NewCurvedTriangles">Triangles to show the new curved side faces with (per new face).</param>
/// <param name="TrimmedFaces">Faces (new numbers) the moved face cut back or extended (a rounding or a slanted face next to it).</param>
/// <param name="MovedAlong">Roundings (new numbers) that moved along with the face (a rounded edge of it).</param>
/// <param name="Punched">The face was pushed through to the other side: it is gone, a hole goes through.</param>
public sealed record PushPullResult(BrepBody Body, IReadOnlyList<int> FaceMap, IReadOnlyList<int> NewFaces, IReadOnlyList<int> GrownFaces,
    IReadOnlyDictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>> NewCurvedTriangles, IReadOnlyList<int> TrimmedFaces, IReadOnlyList<int> MovedAlong,
    bool Punched = false);

/// <summary>
/// Push/Pull as in SketchUp: a planar face is moved along its normal; every edge of it gets a side face (a plane for a
/// straight edge, a cylinder for an arc around the direction of movement); a side face that lies in the face across its
/// edge joins it (the walls of a box grow with its top; pushed in, they shrink), side faces in one plane join each
/// other, and edges that end up in one straight line become one. A neighbour that does not run along the direction of
/// movement (a rounding, a slanted face) gets no side face: the moved face cuts it back, or extends it, along its edges.
/// </summary>
public static partial class PushPull
{
    /// <summary>Distance (metres) within which points count as on a surface (the converter fits within a micrometre).</summary>
    public const double DistanceTol = 2e-6;

    /// <summary>Angle (radians) within which directions count as the same (0.06°).</summary>
    public const double AngleTol = 1e-3;

    private sealed class Work(BrepSurface surface, bool reversed, List<List<BrepCoedge>> loops)
    {
        public BrepSurface Surface = surface;
        public bool Reversed = reversed;
        public List<List<BrepCoedge>> Loops = loops;
        public bool Dead;
    }

    /// <summary>
    /// The face moved over <paramref name="distance"/> along its normal. <paramref name="prism"/>: a side face at every
    /// edge, as a copy of the face pulled out (the end of a turned piece extended straight); no rounding moves along and
    /// no slanted or round neighbour is extended.
    /// </summary>
    public static PushPullResult Apply(BrepBody body, int face, double distance, bool prism = false)
    {
        if (face < 0 || face >= body.Faces.Count) throw new ArgumentOutOfRangeException(nameof(face));
        var F = body.Faces[face];
        if (Math.Abs(distance) < 1e-9) return new(body, Enumerable.Range(0, body.Faces.Count).ToList(), [], [], new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>(), [], []);
        if (F.Surface is BrepCylinder) return Cylinder(body, face, distance);
        if (F.Surface is not BrepPlane plane) throw new PushPullException(Lang.T("Push/Pull werkt (nog) alleen op platte vlakken en cilinders.", "Push/Pull only works on flat faces and cylinders (for now)."));
        var used = F.Loops.SelectMany(l => l).GroupBy(c => c.Edge).FirstOrDefault(g => g.Count() > 1);
        if (used != null) throw new PushPullException(Lang.T("Dit vlak raakt zichzelf langs een rand; dat kan nog niet.", "This face touches itself along an edge; that is not possible yet."));

        var n = FaceGeometry.Normal(F);
        // a rounding of an edge that runs on past a quarter: cut where it faces across the movement, then again
        if (!prism && SplitRoundings(body, face, n) is { } split)
        {
            var rs = Apply(split.Body, face, distance);
            var staying = split.NewFaces.Select(x => rs.FaceMap[x]).Where(x => x >= 0);
            return rs with { TrimmedFaces = rs.TrimmedFaces.Concat(staying).Distinct().Order().ToList() };
        }
        var off = n * distance;
        double sign = Math.Sign(distance);
        var verts = body.Vertices.ToList();
        var edges = body.Edges.Select(e => (BrepEdge?)e).ToList();
        var faces = body.Faces.Select(f => new Work(f.Surface, f.Reversed, f.Loops.Select(l => l.ToList()).ToList())).ToList();
        // roundings of the face's edges move along with it (as one piece); its edge is then the edge of that piece
        List<int> along = prism ? [] : Along(body, face, n);
        var allCo = new[] { face }.Concat(along).SelectMany(m => body.Faces[m].Loops.SelectMany(l => l)).ToList();
        var inner = allCo.GroupBy(c => c.Edge).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var boundary = allCo.Where(c => !inner.Contains(c.Edge)).ToList();
        // (cutting back or extending only away from the roundings that move along)
        var alongVerts = along.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge])
            .Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End }).ToHashSet();
        var (trimEdges, trimVerts) = prism ? (new Dictionary<int, int>(), new Dictionary<int, (int K, Vec3 P, bool Forward)>())
            : Trims(body, face, n, plane.Origin + off, sign, alongVerts);

        // the moved vertices, with an edge from each old one to its new place: straight along the normal, or (next to a
        // neighbour that is cut back or extended) along the edge it slides on
        var moved = new Dictionary<int, int>();
        var vertical = new Dictionary<int, int>();
        foreach (var c in allCo)
        {
            var e = body.Edges[c.Edge];
            foreach (int v in new[] { e.Start, e.End })
                if (v >= 0 && !moved.ContainsKey(v))
                {
                    moved[v] = verts.Count;
                    if (!trimVerts.TryGetValue(v, out var t))
                    {
                        verts.Add(verts[v] + off);
                        vertical[v] = edges.Count; edges.Add(new BrepEdge(v, moved[v], new BrepLine()));
                        continue;
                    }
                    verts.Add(t.P);
                    var k = edges[t.K]!;
                    int o = k.Start == v ? k.End : k.Start;
                    // the curve running from v towards the other end of k
                    var to = k.Curve is BrepCircle kc && k.Start != v ? kc with { Normal = kc.Normal * -1 } : k.Curve;
                    if (!t.Forward)
                    {
                        // on beyond v: the line or circle of k goes on
                        vertical[v] = edges.Count;
                        edges.Add(new BrepEdge(v, moved[v], to is BrepCircle tc ? tc with { Normal = tc.Normal * -1 } : to));
                        continue;
                    }
                    // into k: k is split at the new place
                    int k1 = edges.Count; edges.Add(new BrepEdge(v, moved[v], to));
                    int k2 = edges.Count; edges.Add(new BrepEdge(moved[v], o, to));
                    edges[t.K] = null;
                    foreach (var w in faces)
                        for (int li = 0; li < w.Loops.Count; li++)
                            if (w.Loops[li].Any(x => x.Edge == t.K))
                                w.Loops[li] = w.Loops[li].SelectMany(x => x.Edge != t.K ? [x]
                                    : (x.Forward ? k.Start : k.End) == v ? new BrepCoedge[] { new(k1, true), new(k2, true) } : [new(k2, false), new(k1, false)]).ToList();
                    vertical[v] = k1;
                }
        }

        // per edge of the face: its moved copy and a side face
        var movedEdge = new Dictionary<int, int>();
        var sides = new List<int>();
        var sideMesh = new Dictionary<int, List<(Vec3, Vec3, Vec3)>>();
        // (an edge between the face and a rounding moving along: just moved, no side face)
        foreach (int ie in inner)
        {
            var e = body.Edges[ie];
            movedEdge[ie] = edges.Count;
            edges.Add(new BrepEdge(e.Start < 0 ? -1 : moved[e.Start], e.End < 0 ? -1 : moved[e.End], Shifted(e.Curve, off)));
        }
        foreach (var c in boundary)
        {
            var e = body.Edges[c.Edge];
            if (trimEdges.ContainsKey(c.Edge))
            {
                // no side face: the neighbour is cut back to (or extended up to) the moved edge
                movedEdge[c.Edge] = edges.Count; edges.Add(new BrepEdge(moved[e.Start], moved[e.End], new BrepLine()));
                continue;
            }
            BrepSurface surface; bool reversed;
            switch (e.Curve)
            {
                case BrepLine:
                {
                    var a = verts[c.Forward ? e.Start : e.End]; var b = verts[c.Forward ? e.End : e.Start];
                    var dir = (b - a).Normalized();
                    if (RoundingInTheWay(body, c.Edge, along.Append(face).ToHashSet(), n, sign))
                        throw new PushPullException(Lang.T("Hier sluit een ronding aan die niet mee kan bewegen en niet afgesneden kan worden; dat kan nog niet.", "A rounding joins here that cannot move along and cannot be cut off; that is not possible yet."));
                    surface = new BrepPlane(a, (dir.Cross(n) * sign).Normalized(), dir); reversed = false;
                    movedEdge[c.Edge] = edges.Count; edges.Add(new BrepEdge(moved[e.Start], moved[e.End], new BrepLine()));
                    break;
                }
                case BrepCircle ci when Math.Abs(ci.Normal.Normalized().Dot(n)) > 1 - AngleTol * AngleTol:
                {
                    var axis = ci.Normal.Normalized();
                    var pts = FaceGeometry.EdgePoints(body, e);
                    var p = pts[pts.Count / 2];
                    var tangent = axis.Cross(p - ci.Centre).Normalized() * (c.Forward ? 1 : -1);
                    var outward = tangent.Cross(n) * sign;
                    var radial = p - ci.Centre; radial = (radial - axis * radial.Dot(axis)).Normalized();
                    surface = new BrepCylinder(ci.Centre, axis, ci.XAxis, ci.Radius); reversed = outward.Dot(radial) < 0;
                    movedEdge[c.Edge] = edges.Count;
                    edges.Add(new BrepEdge(e.Start < 0 ? -1 : moved[e.Start], e.End < 0 ? -1 : moved[e.End], ci with { Centre = ci.Centre + off }));
                    break;
                }
                case BrepCircle ci when e.Start >= 0 && Math.Abs(ci.Normal.Normalized().Dot(n)) < AngleTol:
                {
                    // an arc in a plane along the direction of movement (the end of a rounding moving along): a flat
                    // side face in that plane
                    var pts = FaceGeometry.EdgePoints(body, e);
                    if (!c.Forward) pts.Reverse();
                    int mid = pts.Count / 2;
                    var tangent = (pts[Math.Min(mid + 1, pts.Count - 1)] - pts[Math.Max(mid - 1, 0)]).Normalized();
                    var nn = ci.Normal.Normalized();
                    if (nn.Dot(tangent.Cross(n) * sign) < 0) nn = nn * -1;
                    surface = new BrepPlane(ci.Centre, nn, n); reversed = false;
                    movedEdge[c.Edge] = edges.Count;
                    edges.Add(new BrepEdge(moved[e.Start], moved[e.End], ci with { Centre = ci.Centre + off }));
                    break;
                }
                default:
                {
                    // any other edge (an ellipse where two roundings meet, a spline) whose neighbour runs along the
                    // direction of movement: the side face lies in that neighbour, which grows or shrinks
                    if (e.Start < 0 || NeighbourAlong(body, c.Edge, along.Append(face).ToHashSet(), n) is not { } hs)
                        throw new PushPullException(Lang.T("Dit vlak heeft een rand die geen rechte lijn of cirkel om de duwrichting is; dat kan nog niet.", "This face has an edge that is not a straight line or a circle round the push direction; that is not possible yet."));
                    var pts = FaceGeometry.EdgePoints(body, e);
                    if (!c.Forward) pts.Reverse();
                    int mid = pts.Count / 2;
                    var p = pts[mid];
                    var tangent = (pts[Math.Min(mid + 1, pts.Count - 1)] - pts[Math.Max(mid - 1, 0)]).Normalized();
                    var want = tangent.Cross(n) * sign;
                    var natural = hs switch
                    {
                        BrepPlane hp => hp.Normal,
                        BrepCylinder hc => (p - hc.Origin) - hc.Axis.Normalized() * (p - hc.Origin).Dot(hc.Axis.Normalized()),
                        _ => want,
                    };
                    surface = hs; reversed = want.Dot(natural) < 0;
                    movedEdge[c.Edge] = edges.Count;
                    edges.Add(new BrepEdge(moved[e.Start], moved[e.End], Shifted(e.Curve, off)));
                    break;
                }
            }
            int me = movedEdge[c.Edge];
            List<List<BrepCoedge>> loops;
            if (e.Start < 0) loops = [[new(c.Edge, c.Forward)], [new(me, !c.Forward)]];
            else
            {
                int a = c.Forward ? e.Start : e.End, b = c.Forward ? e.End : e.Start;
                loops = [[new(c.Edge, c.Forward), new(vertical[b], true), new(me, !c.Forward), new(vertical[a], false)]];
            }
            int si = faces.Count;
            sides.Add(si);
            faces.Add(new Work(surface, reversed, loops));
            if (surface is BrepCylinder)
            {
                // shown as a strip of triangles along the arc
                var pts = FaceGeometry.EdgePoints(body, e);
                if (!c.Forward) pts.Reverse();
                var strip = new List<(Vec3, Vec3, Vec3)>();
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    Vec3 p0 = pts[i], p1 = pts[i + 1], q0 = p0 + off, q1 = p1 + off;
                    strip.Add((p0, p1, q1)); strip.Add((p0, q1, q0));
                }
                sideMesh[si] = strip;
            }
        }
        faces[face] = new Work(new BrepPlane(plane.Origin + off, plane.Normal, plane.XAxis), F.Reversed,
            F.Loops.Select(l => l.Select(c => new BrepCoedge(movedEdge[c.Edge], c.Forward)).ToList()).ToList());
        foreach (int g in along)
        {
            var G = body.Faces[g]; var cy = (BrepCylinder)G.Surface;
            faces[g] = new Work(cy with { Origin = cy.Origin + off }, G.Reversed,
                G.Loops.Select(l => l.Select(c => new BrepCoedge(movedEdge[c.Edge], c.Forward)).ToList()).ToList());
        }

        // a neighbour cut back or extended: it runs from the moved vertices along the moved edge (what it now runs along
        // both ways, the strip cut off, goes)
        foreach (var (e, g) in trimEdges)
        {
            var w = faces[g];
            var all = w.Loops.SelectMany(l => l).ToList();
            int i = all.FindIndex(c => c.Edge == e);
            var ce = all[i]; all.RemoveAt(i);
            var ed = body.Edges[e];
            int p = ce.Forward ? ed.Start : ed.End, q = ce.Forward ? ed.End : ed.Start;
            all.Add(new(vertical[p], true)); all.Add(new(movedEdge[e], ce.Forward)); all.Add(new(vertical[q], false));
            var fwd = all.Where(c => c.Forward).Select(c => c.Edge).ToHashSet();
            var both = all.Where(c => !c.Forward).Select(c => c.Edge).Where(fwd.Contains).ToHashSet();
            w.Loops = Chain(all.Where(c => !both.Contains(c.Edge)).ToList(), edges);
        }

        // pushed in exactly to a face on the other side (facing away) and lying within it: a hole right through; that
        // face gets the moved loop as a hole, the side faces are its wall
        bool punched = false;
        if (distance < 0 && along.Count == 0 && trimEdges.Count == 0 && F.Loops.Count == 1 && PunchInto(body, face, n, plane.Origin + off) is int g0)
        {
            faces[g0].Loops.Add(faces[face].Loops[0]);
            faces[face].Dead = true;
            punched = true;
        }

        int oldFaces = body.Faces.Count;
        // a moved vertex that lands on a vertex around the face is that vertex (pushed back exactly where it was, or
        // flush with a step next to it)
        var fVerts = new HashSet<int>(moved.Keys);
        var fEdges = allCo.Select(c => c.Edge).ToHashSet();
        var around = new HashSet<int>(fVerts);
        for (int g = 0; g < oldFaces; g++)
        {
            if (g == face) continue;
            var gv = VertsOf(faces[g], edges);
            if (gv.Overlaps(fVerts) || faces[g].Loops.Any(l => l.Any(c => fEdges.Contains(c.Edge)))) around.UnionWith(gv);
        }
        var snap = new Dictionary<int, int>();
        foreach (int nv in moved.Values)
            foreach (int u in around)
                if ((verts[u] - verts[nv]).Length < DistanceTol) { snap[nv] = u; break; }
        if (snap.Count > 0)
            for (int i = 0; i < edges.Count; i++)
                if (edges[i] is { } e && (snap.ContainsKey(e.Start) || snap.ContainsKey(e.End)))
                    edges[i] = e with { Start = snap.GetValueOrDefault(e.Start, e.Start), End = snap.GetValueOrDefault(e.End, e.End) };
        // a moved vertex in the middle of a straight edge around the face, or a vertex around it in the middle of a moved
        // edge: that edge is split there (so edges along the same stretch are the same edges, and cancel or join)
        var tVerts = moved.Values.Select(v => snap.GetValueOrDefault(v, v)).Concat(around).Distinct().ToList();
        SplitAtVertices(verts, edges, faces, tVerts);
        // edges shrunk to a point drop out; edges along the same line or circle between the same vertices are one edge
        Canonicalize(edges, faces);
        // an edge a face now runs along both ways (a sliver of nothing) goes; a face of only that goes altogether
        bool Slivers()
        {
            bool any = false;
            foreach (var w in faces.Where(w => !w.Dead))
            {
                var all = w.Loops.SelectMany(l => l).ToList();
                var fwd = all.Where(c => c.Forward).Select(c => c.Edge).ToHashSet();
                var both = all.Where(c => !c.Forward).Select(c => c.Edge).Where(fwd.Contains).ToHashSet();
                if (both.Count == 0) continue;
                w.Loops = Chain(all.Where(c => !both.Contains(c.Edge)).ToList(), edges);
                if (w.Loops.Count == 0) w.Dead = true;
                any = true;
            }
            return any;
        }
        Slivers();

        // faces next to each other in one surface become one: a side face and the face across its edge (the walls of a
        // box grow with its top; pushed in, they shrink), side faces in one plane, the face and a face it is now flush
        // with; the one with the lower number stays (an old face before a new one)
        var grown = new HashSet<int>();
        var mergedInto = new Dictionary<int, int>();
        var active = new HashSet<int>(sides) { face };
        active.UnionWith(trimEdges.Values);
        active.UnionWith(along);
        bool SameSurface(BrepSurface s, BrepSurface g, IEnumerable<Vec3> onS)
        {
            switch (s, g)
            {
                case (BrepPlane ps, BrepPlane pg):
                {
                    var ng = pg.Normal.Normalized();
                    return Math.Abs(ps.Normal.Normalized().Dot(ng)) > 1 - AngleTol * AngleTol / 2
                        && onS.All(p => Math.Abs((p - pg.Origin).Dot(ng)) < DistanceTol);
                }
                case (BrepCylinder cs, BrepCylinder cg):
                {
                    var ag = cg.Axis.Normalized();
                    var d = cs.Origin - cg.Origin; d -= ag * d.Dot(ag);
                    return Math.Abs(cs.Axis.Normalized().Dot(ag)) > 1 - AngleTol * AngleTol / 2 && Math.Abs(cs.Radius - cg.Radius) < DistanceTol && d.Length < DistanceTol;
                }
                default: return false;
            }
        }
        IEnumerable<Vec3> PointsOf(Work w) => VertsOf(w, edges).Select(v => verts[v]);
        bool again = true;
        while (again)
        {
            again = false;
            var users = new Dictionary<int, List<int>>();
            for (int f = 0; f < faces.Count; f++)
                if (!faces[f].Dead)
                    foreach (var c in faces[f].Loops.SelectMany(l => l))
                    {
                        if (!users.TryGetValue(c.Edge, out var l)) users[c.Edge] = l = new();
                        if (!l.Contains(f)) l.Add(f);
                    }
            foreach (var (edge, fs) in users)
            {
                if (fs.Count != 2) continue;
                int a = Math.Min(fs[0], fs[1]), b = Math.Max(fs[0], fs[1]);
                if (!active.Contains(a) && !active.Contains(b)) continue;
                if (!SameSurface(faces[b].Surface, faces[a].Surface, PointsOf(faces[b]))) continue;
                // two planar faces of straight edges: laid over each other properly (crossings and touching points
                // become vertices; what is left can be several faces, facing either way)
                if (MergePlanar(verts, edges, faces, a, b) is { } extra)
                {
                    faces[b].Dead = true;
                    mergedInto[b] = a;
                    sides.AddRange(extra);
                    active.UnionWith(extra);
                    if (sideMesh.Remove(b, out var pm) && sideMesh.TryGetValue(a, out var pam)) pam.AddRange(pm);
                    active.Add(a);
                    if (a < oldFaces) grown.Add(a);
                    again = true;
                    break;
                }
                var all = faces[a].Loops.SelectMany(l => l).Concat(faces[b].Loops.SelectMany(l => l)).ToList();
                var fwd = all.Where(c => c.Forward).Select(c => c.Edge).ToHashSet();
                // (only edges no other face runs along: on a mesh that touches itself an edge can have more faces)
                var shared = all.Where(c => !c.Forward).Select(c => c.Edge).Where(e => fwd.Contains(e) && users[e].All(x => x == a || x == b)).ToHashSet();
                foreach (int e in shared) edges[e] = null;
                faces[a].Loops = Chain(all.Where(c => !shared.Contains(c.Edge)).ToList(), edges);
                faces[b].Dead = true;
                mergedInto[b] = a;
                if (faces[a].Loops.Count == 0) faces[a].Dead = true;
                // facing each other (one taken off the other): what is left faces the way of the one it is left of
                else if (faces[a].Surface is BrepPlane pa && faces[b].Surface is BrepPlane pb
                    && (pa.Normal * (faces[a].Reversed ? -1 : 1)).Dot(pb.Normal * (faces[b].Reversed ? -1 : 1)) < 0)
                {
                    var na = (pa.Normal * (faces[a].Reversed ? -1 : 1)).Normalized();
                    var tmp = new BrepBody { Vertices = verts, Edges = edges.Select(x => x ?? new BrepEdge(0, 0, new BrepLine())).ToList(), Faces = [] };
                    var areas = faces[a].Loops.Select(l => FaceGeometry.Area(FaceGeometry.LoopPoints(tmp, l), na)).ToList();
                    if (areas.Count > 0 && areas.MaxBy(Math.Abs) < 0) { faces[a].Surface = faces[b].Surface; faces[a].Reversed = faces[b].Reversed; }
                }
                if (sideMesh.Remove(b, out var mesh) && sideMesh.TryGetValue(a, out var am)) am.AddRange(mesh);
                active.Add(a);
                if (a < oldFaces) grown.Add(a);
                again = true;
                break;
            }
            if (!again) again = Slivers();
        }

        // edges that now lie in one line at a vertex of the face become one
        JoinStraight(verts, edges, faces, moved.Keys.Concat(moved.Values).Concat(snap.Values).ToList());
        foreach (var e in edges)
            if (e is { Curve: BrepLine } && (verts[e.Start] - verts[e.End]).Length < 1e-9)
                throw new PushPullException(Lang.T("Het vlak komt precies op een ander vlak terecht; dat kan nog niet.", "The face lands exactly on another face; that is not possible yet."));

        // the new body: dead faces and edges out, vertices renumbered
        var (result, fMap) = Compact(verts, edges, faces);
        int Final(int f) { while (!fMap.ContainsKey(f) && mergedInto.TryGetValue(f, out int t)) f = t; return fMap.GetValueOrDefault(f, -1); }
        var faceMap = Enumerable.Range(0, oldFaces).Select(Final).ToList();
        var newFaces = sides.Where(s => !faces[s].Dead).Select(s => fMap[s]).ToList();
        var grownNew = grown.Select(Final).Where(f => f >= 0).Distinct().Order().ToList();
        var curved = sideMesh.Where(kv => !faces[kv.Key].Dead).ToDictionary(kv => fMap[kv.Key], kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value);
        var trimmed = trimEdges.Values.Select(Final).Where(f => f >= 0).Distinct().Order().ToList();
        var movedAlong = along.Select(Final).Where(f => f >= 0).Distinct().Order().ToList();
        var changed = new[] { Final(face) }.Concat(newFaces).Concat(grownNew).Concat(trimmed).Concat(movedAlong).Where(f => f >= 0).ToList();
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "1" && XtBrepBuilder.TopologyError(result) is { } te)
        {
            Console.Error.WriteLine("[pp] " + te);
            foreach (var w in Enumerable.Range(0, result.Edges.Count).Where(i => te.Contains($"Edge {i} ")))
            {
                var ed = result.Edges[w];
                Console.Error.WriteLine($"[pp] edge {w}: {ed.Start}->{ed.End} {(ed.Start >= 0 ? result.Vertices[ed.Start] + " " + result.Vertices[ed.End] : "")}");
                for (int g = 0; g < result.Faces.Count; g++)
                    foreach (var l in result.Faces[g].Loops)
                        foreach (var c in l.Where(c => c.Edge == w))
                            Console.Error.WriteLine($"[pp]   face {g} ({result.Faces[g].Surface.GetType().Name} {(result.Faces[g].Surface is BrepPlane ? FaceGeometry.Normal(result.Faces[g]) : default)}) fwd {c.Forward}, loop {l.Count}, new {newFaces.Contains(g)}, grown {grownNew.Contains(g)}, moved {g == Final(face)}");
            }
        }
        Check(result, changed);
        // inside out as a whole (pushed through the other side: the faces that are left then face inward)
        if (SignedVolume(result) is double vol && vol <= 0)
            throw new PushPullException(Lang.T("Zo gaat het vlak door de andere kant heen: doorduwen tot een gat kan nog niet.", "That way the face goes through the other side: pushing through to a hole is not possible yet."));
        return new(result, faceMap, newFaces, grownNew, curved, trimmed, movedAlong, punched);
    }

    /// <summary>
    /// The face on the other side that face <paramref name="face"/>, moved into the plane through
    /// <paramref name="target"/>, lands in: a planar face in that plane facing the other way, with the moved face
    /// strictly inside it (clear of its edges and holes); or null.
    /// </summary>
    private static int? PunchInto(BrepBody body, int face, Vec3 n, Vec3 target)
    {
        var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
        var y = n.Cross(x);
        (double X, double Y) P2(Vec3 p) => (p.Dot(x), p.Dot(y));
        var moved = FaceGeometry.LoopPoints(body, body.Faces[face].Loops[0]).Select(P2).ToList();
        for (int g = 0; g < body.Faces.Count; g++)
        {
            var G = body.Faces[g];
            if (g == face || G.Surface is not BrepPlane pg || FaceGeometry.Normal(G).Dot(n) > -1 + AngleTol * AngleTol / 2) continue;
            if (Math.Abs((pg.Origin - target).Dot(n)) > DistanceTol) continue;
            var loops = G.Loops.Select(l => FaceGeometry.LoopPoints(body, l).Select(P2).ToList()).ToList();
            static bool In(List<List<(double X, double Y)>> region, (double X, double Y) q)
            {
                bool inside = false;
                foreach (var poly in region)
                    for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                        if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                            inside = !inside;
                return inside;
            }
            if (!moved.All(p => In(loops, p))) continue;
            // clear of G's edges (no point of the one on or across an edge of the other), and no hole of G inside it
            static double Dist((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                double t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
                return Math.Sqrt(Math.Pow(a.X + dx * t - p.X, 2) + Math.Pow(a.Y + dy * t - p.Y, 2));
            }
            bool clear = true;
            foreach (var l in loops)
                for (int i = 0; i < l.Count && clear; i++)
                    for (int j = 0; j < moved.Count && clear; j++)
                        if (Dist(moved[j], l[i], l[(i + 1) % l.Count]) < DistanceTol || Dist(l[i], moved[j], moved[(j + 1) % moved.Count]) < DistanceTol) clear = false;
            if (!clear || loops.Skip(1).Any(l => In([moved], l[0]))) continue;
            return g;
        }
        return null;
    }

    /// <summary>The faces still there as a body: dead faces and unused edges out, vertices renumbered, planar faces with
    /// their outer loop first; and per working face its index in the body.</summary>
    private static (BrepBody Body, Dictionary<int, int> FaceMap) Compact(List<Vec3> verts, List<BrepEdge?> edges, List<Work> faces)
    {
        var vUsed = new Dictionary<int, int>(); var newVerts = new List<Vec3>();
        var eMap = new Dictionary<int, int>(); var newEdges = new List<BrepEdge>();
        int V(int v) { if (v < 0) return -1; if (!vUsed.TryGetValue(v, out int i)) { i = newVerts.Count; vUsed[v] = i; newVerts.Add(verts[v]); } return i; }
        int E(int e)
        {
            if (!eMap.TryGetValue(e, out int i)) { var x = edges[e]!; i = newEdges.Count; eMap[e] = i; newEdges.Add(x with { Start = V(x.Start), End = V(x.End) }); }
            return i;
        }
        var outFaces = new List<BrepFace>(); var fMap = new Dictionary<int, int>();
        for (int f = 0; f < faces.Count; f++)
        {
            var w = faces[f];
            if (w.Dead) continue;
            var loops = w.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => new BrepCoedge(E(c.Edge), c.Forward)).ToList()).ToList();
            fMap[f] = outFaces.Count;
            outFaces.Add(new BrepFace(w.Surface, w.Reversed, loops));
        }
        if (outFaces.Count < 2) throw new PushPullException(Lang.T("Er blijft niets over van de vorm.", "Nothing is left of the shape."));
        return (new BrepBody { Vertices = newVerts, Edges = newEdges, Faces = OrderLoops(new BrepBody { Vertices = newVerts, Edges = newEdges, Faces = outFaces }) }, fMap);
    }

    /// <summary>
    /// The eraser: the edge between two faces in one plane, facing the same way (a line, rectangle or circle drawn on a
    /// face), goes, and the two become one face; straight edges that then meet in one line at its ends become one.
    /// Returns the new body and per old face its new index (or -1). Throws when the edge is not such an edge.
    /// </summary>
    public static (BrepBody Body, IReadOnlyList<int> FaceMap) EraseEdge(BrepBody body, int edge)
    {
        if (edge < 0 || edge >= body.Edges.Count) throw new ArgumentOutOfRangeException(nameof(edge));
        var users = Enumerable.Range(0, body.Faces.Count).Where(f => body.Faces[f].Loops.Any(l => l.Any(c => c.Edge == edge))).ToList();
        if (users.Count != 2) throw new PushPullException(Lang.T("Deze rand kun je niet wissen.", "You cannot erase this edge."));
        int a = users[0], b = users[1];
        var A = body.Faces[a]; var B = body.Faces[b];
        if (A.Surface is not BrepPlane pa || B.Surface is not BrepPlane pb) throw new PushPullException(Lang.T("Alleen een rand tussen twee platte vlakken kun je wissen.", "Only an edge between two flat faces can be erased."));
        var na = FaceGeometry.Normal(A); var nb = FaceGeometry.Normal(B);
        if (na.Dot(nb) < 1 - AngleTol * AngleTol / 2 || Math.Abs((pb.Origin - pa.Origin).Dot(na)) > DistanceTol)
            throw new PushPullException(Lang.T("Deze rand scheidt vlakken die niet in één vlak liggen; die kun je niet wissen.", "This edge separates faces that do not lie in one plane; you cannot erase it."));
        var verts = body.Vertices.ToList();
        var edges = body.Edges.Select(e => (BrepEdge?)e).ToList();
        var faces = body.Faces.Select(f => new Work(f.Surface, f.Reversed, f.Loops.Select(l => l.ToList()).ToList())).ToList();
        var e0 = body.Edges[edge];
        if (MergePlanar(verts, edges, faces, a, b) is { } extra)
        {
            if (extra.Count > 0) throw new PushPullException(Lang.T("Na het wissen valt het vlak in stukken uiteen; dat kan nog niet.", "After erasing, the face falls apart into pieces; that is not possible yet."));
        }
        else
        {
            // (curved edges: the two loops' edges joined, what both run along goes)
            var all = faces[a].Loops.Concat(faces[b].Loops).SelectMany(l => l).ToList();
            var fwd = all.Where(c => c.Forward).Select(c => c.Edge).ToHashSet();
            var shared = all.Where(c => !c.Forward).Select(c => c.Edge).Where(fwd.Contains).ToHashSet();
            foreach (int e in shared) edges[e] = null;
            faces[a].Loops = Chain(all.Where(c => !shared.Contains(c.Edge)).ToList(), edges);
        }
        faces[b].Dead = true;
        if (faces[a].Loops.Count == 0) throw new PushPullException(Lang.T("Er blijft niets over van het vlak.", "Nothing is left of the face."));
        var ends = e0.Start >= 0 ? new List<int> { e0.Start, e0.End } : [];
        JoinStraight(verts, edges, faces, ends);
        var (result, fMap) = Compact(verts, edges, faces);
        if (XtBrepBuilder.TopologyError(result) is not null) throw new PushPullException(Lang.T("Na het wissen: ", "After erasing: ") + WhyNotClosed(result));
        var map = Enumerable.Range(0, body.Faces.Count).Select(f => f == b ? fMap.GetValueOrDefault(a, -1) : fMap.GetValueOrDefault(f, -1)).ToList();
        return (result, map);
    }

    /// <summary>The volume of a body (divergence theorem over its faces' triangles); null when a curved face cannot be
    /// made into triangles.</summary>
    private static double? SignedVolume(BrepBody body)
    {
        double v = 0;
        foreach (var f in body.Faces)
        {
            var tris = f.Surface is BrepPlane ? FaceGeometry.PlanarTriangles(body, f) : FaceGeometry.CurvedTriangles(body, f);
            if (tris == null) return null;
            v += tris.Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6;
        }
        return v;
    }

    /// <summary>A curve moved over <paramref name="off"/>.</summary>
    private static BrepCurve Shifted(BrepCurve c, Vec3 off) => c switch
    {
        BrepCircle ci => ci with { Centre = ci.Centre + off },
        BrepEllipse el => el with { Centre = el.Centre + off },
        BrepBSpline s => s with { ControlPoints = s.ControlPoints.Select(p => p + off).ToList() },
        _ => c,
    };

    /// <summary>Whether a surface runs along direction n (a plane containing it, a cylinder around it): moving an edge
    /// on it along n keeps the edge on it.</summary>
    private static bool RunsAlong(BrepSurface s, Vec3 n) => s switch
    {
        BrepPlane p => Math.Abs(p.Normal.Normalized().Dot(n)) < AngleTol,
        BrepCylinder c => Math.Abs(c.Axis.Normalized().Dot(n)) > 1 - AngleTol * AngleTol,
        _ => false,
    };

    /// <summary>The surface of the face across an edge (not one of the moving faces) when it runs along n; or null.</summary>
    private static BrepSurface? NeighbourAlong(BrepBody body, int edge, IReadOnlySet<int> moving, Vec3 n)
    {
        for (int g = 0; g < body.Faces.Count; g++)
            if (!moving.Contains(g) && body.Faces[g].Loops.Any(l => l.Any(c => c.Edge == edge)))
                return RunsAlong(body.Faces[g].Surface, n) ? body.Faces[g].Surface : null;
        return null;
    }

    /// <summary>
    /// Whether the face across a straight edge is a cylinder lying in the way the face moves (the side face would run
    /// through it): a step into it, across the edge, ends up on the side the face moves to.
    /// </summary>
    private static bool RoundingInTheWay(BrepBody body, int edge, IReadOnlySet<int> moving, Vec3 n, double sign)
    {
        for (int g = 0; g < body.Faces.Count; g++)
        {
            if (moving.Contains(g) || body.Faces[g].Surface is not BrepCylinder cy) continue;
            var G = body.Faces[g];
            var ce = G.Loops.SelectMany(l => l).Cast<BrepCoedge?>().FirstOrDefault(x => x!.Value.Edge == edge);
            if (ce is not { } gc) continue;
            var e = body.Edges[edge];
            Vec3 p0 = body.Vertices[e.Start], p1 = body.Vertices[e.End], p = (p0 + p1) * 0.5;
            var d = (gc.Forward ? p1 - p0 : p0 - p1).Normalized();
            var axis = cy.Axis.Normalized();
            Vec3 Radial(Vec3 q) { var r = q - cy.Origin; return (r - axis * r.Dot(axis)).Normalized(); }
            var outward = Radial(p) * (G.Reversed ? -1 : 1);
            // into the face (left of its edge, seen from outside), a little way, back onto the cylinder
            var into = outward.Cross(d).Normalized();
            var q = p + into * (0.1 * cy.Radius);
            var r0 = q - cy.Origin;
            var onCyl = cy.Origin + axis * r0.Dot(axis) + Radial(q) * cy.Radius;
            if ((onCyl - p).Dot(n) * sign > 1e-3 * cy.Radius) return true;
        }
        return false;
    }

    /// <summary>
    /// Roundings of the face's straight edges (tangent to it, as <see cref="Along"/> wants) that run on past a quarter:
    /// cut along the line where they face across the movement, so the part up to there can move along. The body with
    /// the cuts and the new faces (the parts that stay); or null when there is nothing to cut.
    /// </summary>
    private static (BrepBody Body, List<int> NewFaces)? SplitRoundings(BrepBody body, int face, Vec3 n)
    {
        var newFaces = new List<int>();
        for (bool again = true; again;)
        {
            again = false;
            foreach (var c in body.Faces[face].Loops.SelectMany(l => l))
            {
                var e = body.Edges[c.Edge];
                if (e.Curve is not BrepLine) continue;
                int g = Enumerable.Range(0, body.Faces.Count).FirstOrDefault(x => x != face && body.Faces[x].Loops.Any(l => l.Any(y => y.Edge == c.Edge)), -1);
                if (g < 0 || newFaces.Contains(g) || body.Faces[g].Surface is not BrepCylinder cy) continue;
                var G = body.Faces[g];
                var axis = cy.Axis.Normalized();
                Vec3 p0 = body.Vertices[e.Start], p1 = body.Vertices[e.End];
                if (Math.Abs(axis.Dot(n)) > AngleTol || Math.Abs(axis.Dot((p1 - p0).Normalized())) < 1 - AngleTol * AngleTol) continue;
                Vec3 Radial(Vec3 q) { var r = q - cy.Origin; return (r - axis * r.Dot(axis)).Normalized(); }
                var r0 = Radial((p0 + p1) * 0.5);
                if ((r0 * (G.Reversed ? -1 : 1)).Dot(n) < 0.5) continue;
                // the way into the rounding from this edge, across its axis: where the cut goes
                var gc = G.Loops.SelectMany(l => l).First(x => x.Edge == c.Edge);
                var d = (gc.Forward ? p1 - p0 : p0 - p1).Normalized();
                var into = (r0 * (G.Reversed ? -1 : 1)).Cross(d).Normalized();
                // runs on past the quarter: points of it beyond the cutting line
                var pts = G.Loops.SelectMany(l => FaceGeometry.LoopPoints(body, l));
                if (!pts.Any(p => Radial(p).Dot(r0) < -1e-3)) continue;
                if (CylinderSplit.Split(body, g, into, c.Edge) is not { } s) continue;
                body = s.Body;
                newFaces.Add(s.NewFace);
                again = true;
                break;
            }
        }
        return newFaces.Count > 0 ? (body, newFaces) : null;
    }

    /// <summary>
    /// Roundings of the face's straight edges that move along with it: a cylinder with its axis across the direction of
    /// movement, running on from the face the same way it faces (a rounded edge, not a notch), whose other edges are
    /// lines along its axis or arcs around it with neighbours that run along the direction of movement (those just grow
    /// or shrink, as the walls of a box do).
    /// </summary>
    private static List<int> Along(BrepBody body, int face, Vec3 n)
    {
        var users = new Dictionary<int, List<int>>();
        for (int g = 0; g < body.Faces.Count; g++)
            foreach (var c in body.Faces[g].Loops.SelectMany(l => l))
            {
                if (!users.TryGetValue(c.Edge, out var u)) users[c.Edge] = u = new();
                if (!u.Contains(g)) u.Add(g);
            }
        int Other(int e, int of) => users[e] is { Count: 2 } u ? (u[0] == of ? u[1] : u[0]) : -1;
        bool RunsAlong(int g) => body.Faces[g].Surface switch
        {
            BrepPlane p => Math.Abs(p.Normal.Normalized().Dot(n)) < AngleTol,
            BrepCylinder c => Math.Abs(c.Axis.Normalized().Dot(n)) > 1 - AngleTol * AngleTol,
            _ => false,
        };
        var result = new List<int>();
        foreach (var c in body.Faces[face].Loops.SelectMany(l => l))
        {
            var e = body.Edges[c.Edge];
            int g = Other(c.Edge, face);
            if (e.Curve is not BrepLine || g < 0 || g == face || result.Contains(g)) continue;
            var G = body.Faces[g];
            if (G.Surface is not BrepCylinder cy) continue;
            var axis = cy.Axis.Normalized();
            var dir = (body.Vertices[e.End] - body.Vertices[e.Start]).Normalized();
            if (Math.Abs(axis.Dot(n)) > AngleTol || Math.Abs(axis.Dot(dir)) < 1 - AngleTol * AngleTol) { if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2") Console.Error.WriteLine($"[along] face {face} cyl {g}: axis·n {axis.Dot(n):0.000} axis·dir {axis.Dot(dir):0.000}"); continue; }
            // facing the same way where it meets the face (a rounded edge; a notch faces the other way)
            var p = body.Vertices[e.Start] - cy.Origin;
            var outward = (p - axis * p.Dot(axis)).Normalized() * (G.Reversed ? -1 : 1);
            if (outward.Dot(n) < 0.5) { if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2") Console.Error.WriteLine($"[along] face {face} cyl {g}: outward·n {outward.Dot(n):0.00}"); continue; }
            bool ok = true;
            foreach (var gc in G.Loops.SelectMany(l => l))
            {
                if (gc.Edge == c.Edge) continue;
                var ge = body.Edges[gc.Edge];
                int h = Other(gc.Edge, g);
                // (any edge but a ring: its neighbour runs along the direction of movement, so it just grows or shrinks)
                bool shape = ge.Start >= 0;
                // (a line along the axis where the rounding faces across the movement: there it gets a flat side face,
                // whatever is across it; so after a cut)
                if (shape && ge.Curve is BrepLine && h != face)
                {
                    var mp = (body.Vertices[ge.Start] + body.Vertices[ge.End]) * 0.5 - cy.Origin;
                    var radial = (mp - axis * mp.Dot(axis)).Normalized();
                    if (Math.Abs((body.Vertices[ge.End] - body.Vertices[ge.Start]).Normalized().Dot(axis)) > 1 - AngleTol * AngleTol && Math.Abs(radial.Dot(n)) < 1e-3) continue;
                }
                if (!shape || h < 0 || h == face || !RunsAlong(h))
                {
                    if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2") Console.Error.WriteLine($"[along] face {face} cyl {g}: edge {gc.Edge} {ge.Curve.GetType().Name} shape {shape} neighbour {h} {(h >= 0 ? body.Faces[h].Surface.GetType().Name : "")} {(h >= 0 && body.Faces[h].Surface is BrepPlane hp0 ? FaceGeometry.Normal(body.Faces[h]).ToString() : "")} from {body.Vertices[Math.Max(0, ge.Start)] * 1000} to {body.Vertices[Math.Max(0, ge.End)] * 1000}");
                    ok = false; break;
                }
            }
            if (ok) result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// Edges of the face whose neighbour does not run along the direction of movement (a rounding, a slanted face): the
    /// moved face cuts that neighbour back, or extends it, instead of getting a side face there. Per such edge its
    /// neighbour; per vertex at their ends the edge it slides along (its third edge), its new place, and whether that lies
    /// on that edge (forward) or on beyond the vertex. Empty when it does not work out (then all edges get side faces).
    /// </summary>
    private static (Dictionary<int, int> Edges, Dictionary<int, (int K, Vec3 P, bool Forward)> Verts) Trims(BrepBody body, int face, Vec3 n, Vec3 origin, double sign, IReadOnlySet<int> keepOff)
    {
        var none = (new Dictionary<int, int>(), new Dictionary<int, (int, Vec3, bool)>());
        var F = body.Faces[face];
        var fe = F.Loops.SelectMany(l => l).Select(c => c.Edge).ToHashSet();
        var fv = fe.Select(e => body.Edges[e]).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End }).ToHashSet();
        var users = new Dictionary<int, List<int>>();
        var at = new Dictionary<int, List<int>>();
        for (int g = 0; g < body.Faces.Count; g++)
            foreach (var c in body.Faces[g].Loops.SelectMany(l => l))
            {
                if (!users.TryGetValue(c.Edge, out var u)) users[c.Edge] = u = new();
                if (u.Contains(g)) continue;
                u.Add(g);
                var e = body.Edges[c.Edge];
                if (e.Start >= 0)
                    foreach (int v in new[] { e.Start, e.End }) { if (!at.TryGetValue(v, out var l)) at[v] = l = new(); if (!l.Contains(c.Edge)) l.Add(c.Edge); }
            }
        int Other(int e) => users[e] is { Count: 2 } u ? (u[0] == face ? u[1] : u[0]) : -1;
        bool Along(BrepSurface s) => s switch
        {
            BrepPlane p => Math.Abs(p.Normal.Normalized().Dot(n)) < AngleTol,
            BrepCylinder c => Math.Abs(c.Axis.Normalized().Dot(n)) > 1 - AngleTol * AngleTol,
            _ => false,
        };
        var edgesOut = new Dictionary<int, int>();
        var vertsOut = new Dictionary<int, (int K, Vec3 P, bool Forward)>();
        foreach (int e in fe)
        {
            var ed = body.Edges[e];
            int g = Other(e);
            if (ed.Curve is not BrepLine || g < 0 || g == face || keepOff.Contains(ed.Start) || keepOff.Contains(ed.End)) continue;
            var s = body.Faces[g].Surface;
            var dir = (body.Vertices[ed.End] - body.Vertices[ed.Start]).Normalized();
            bool ok = s switch
            {
                BrepPlane p => Math.Abs(p.Normal.Normalized().Dot(n)) is var a && a > AngleTol && a < 1 - AngleTol,
                BrepCylinder c => Math.Abs(c.Axis.Normalized().Dot(n)) < AngleTol && Math.Abs(c.Axis.Normalized().Dot(dir)) > 1 - AngleTol * AngleTol,
                _ => false,
            };
            if (!ok) continue;
            var slid = new List<(int V, int K, Vec3 P, bool Forward)>();
            bool dbg = Environment.GetEnvironmentVariable("PPM_DEBUG") == "2";
            foreach (int v in new[] { ed.Start, ed.End })
            {
                var ks = at[v].Where(k => !fe.Contains(k)).ToList();
                if (ks.Count != 1 || !users[ks[0]].Contains(g)) { if (dbg) Console.Error.WriteLine($"[trim] edge {e} v {v}: third edges {ks.Count}"); ok = false; break; }
                var k = body.Edges[ks[0]];
                if (fv.Contains(k.Start == v ? k.End : k.Start)) { if (dbg) Console.Error.WriteLine($"[trim] edge {e} v {v}: k ends on the face"); ok = false; break; }
                if (Slide(body, k, v, origin, n, sign) is not { } r) { if (dbg) Console.Error.WriteLine($"[trim] edge {e} v {v}: no slide on {k.Curve.GetType().Name}"); ok = false; break; }
                slid.Add((v, ks[0], r.P, r.Forward));
            }
            if (!ok || slid[0].K == slid[1].K) continue;
            // the new edge lies on the neighbour: on a cylinder, a line along its axis
            if (s is BrepCylinder cy && Math.Abs((slid[1].P - slid[0].P).Normalized().Dot(cy.Axis.Normalized())) < 1 - AngleTol * AngleTol) continue;
            edgesOut[e] = g;
            foreach (var x in slid) vertsOut[x.V] = (x.K, x.P, x.Forward);
        }
        if (edgesOut.Count == 0) return none;
        // the other edges of the face at such a vertex get a side face, in their neighbour, which must take in the edge
        // the vertex slides along
        foreach (int e in fe.Where(e => !edgesOut.ContainsKey(e)))
        {
            var ed = body.Edges[e];
            if (ed.Start < 0) continue;
            foreach (int v in new[] { ed.Start, ed.End })
            {
                if (!vertsOut.TryGetValue(v, out var t)) continue;
                int h = Other(e);
                if (h < 0 || !Along(body.Faces[h].Surface) || !users[t.K].Contains(h)) return none;
            }
        }
        return (edgesOut, vertsOut);
    }

    /// <summary>
    /// Where edge <paramref name="k"/>, followed from its vertex <paramref name="v"/>, meets the plane (a point and its
    /// normal): towards its other end (forward), or on beyond v along the same line or circle; or null. On a circle the
    /// way it curves decides which: towards the side the plane moved to.
    /// </summary>
    private static (Vec3 P, bool Forward)? Slide(BrepBody body, BrepEdge k, int v, Vec3 origin, Vec3 n, double sign)
    {
        if (k.Start < 0) return null;
        int o = k.Start == v ? k.End : k.Start;
        if (o == v) return null;
        var a = body.Vertices[v];
        switch (k.Curve)
        {
            case BrepLine:
            {
                var u = body.Vertices[o] - a;
                double den = u.Dot(n);
                if (Math.Abs(den) < 1e-9 * u.Length) return null;
                double t = (origin - a).Dot(n) / den;
                const double tol = 1e-6;
                if (t > tol && t < 1 - tol) return (a + u * t, true);
                if (t < -tol) return (a + u * t, false);
                return null;
            }
            case BrepCircle c:
            {
                var N = c.Normal.Normalized(); var X = (c.XAxis - N * c.XAxis.Dot(N)).Normalized(); var Y = N.Cross(X);
                double Angle(Vec3 p) { var d = p - c.Centre; return Math.Atan2(d.Dot(Y), d.Dot(X)); }
                double a0 = Angle(body.Vertices[k.Start]), sweep = Angle(body.Vertices[k.End]) - a0;
                while (sweep <= 1e-12) sweep += 2 * Math.PI;
                double tv = Angle(a), s = k.Start == v ? 1 : -1;
                Vec3 At(double phi) { double psi = tv + s * phi; return c.Centre + X * (c.Radius * Math.Cos(psi)) + Y * (c.Radius * Math.Sin(psi)); }
                // where the circle meets the plane: A cos ψ + B sin ψ + D = 0
                double A = c.Radius * X.Dot(n), B = c.Radius * Y.Dot(n), D = (c.Centre - origin).Dot(n);
                double M = Math.Sqrt(A * A + B * B);
                if (M < 1e-12) return null;
                double ratio = -D / M;
                if (Math.Abs(ratio) > 1 + 1e-9) return null;
                double b = Math.Atan2(B, A), w = Math.Acos(Math.Clamp(ratio, -1, 1));
                bool fwd = (At(Math.Min(0.1, sweep / 2)) - At(0)).Dot(n) * sign > 0;
                double limit = fwd ? sweep : 2 * Math.PI - sweep, best = double.MaxValue;
                foreach (double psi in new[] { b + w, b - w })
                {
                    double phi = s * (psi - tv);
                    phi = (phi % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                    double amount = fwd ? phi : (2 * Math.PI - phi) % (2 * Math.PI);
                    if (amount > 1e-6 && amount < limit - 1e-6) best = Math.Min(best, amount);
                }
                if (best == double.MaxValue) return null;
                return (At(fwd ? best : -best), fwd);
            }
            default:
                return null;
        }
    }

    private static HashSet<int> VertsOf(Work w, List<BrepEdge?> edges)
    {
        var set = new HashSet<int>();
        foreach (var c in w.Loops.SelectMany(l => l))
            if (edges[c.Edge] is { Start: >= 0 } e) { set.Add(e.Start); set.Add(e.End); }
        return set;
    }

    /// <summary>
    /// Edges shrunk to a point drop out of the loops; straight edges between the same vertices, and ring edges on the
    /// same circle, become one (the first).
    /// </summary>
    private static void Canonicalize(List<BrepEdge?> edges, List<Work> faces)
    {
        static long R(double x) => (long)Math.Round(x / 1e-7);
        static string P(Vec3 p) => $"{R(p.X)},{R(p.Y)},{R(p.Z)}";
        var canon = new Dictionary<string, int>();
        var replace = new Dictionary<int, (int Edge, bool Flip)>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i] is not { } e) continue;
            if (e.Curve is BrepLine && e.Start == e.End) { replace[i] = (-1, false); edges[i] = null; continue; }
            string? key = e.Curve switch
            {
                BrepLine => $"L{Math.Min(e.Start, e.End)},{Math.Max(e.Start, e.End)}",
                BrepCircle c when e.Start < 0 => $"R{P(c.Centre)},{R(c.Radius)},{P(Unsigned(c.Normal.Normalized()) * 1e-3)}",
                _ => null,
            };
            if (key == null) continue;
            if (!canon.TryGetValue(key, out int j)) { canon[key] = i; continue; }
            var k = edges[j]!;
            bool flip = e.Curve is BrepLine ? e.Start != k.Start : ((BrepCircle)e.Curve).Normal.Dot(((BrepCircle)k.Curve).Normal) < 0;
            replace[i] = (j, flip);
            edges[i] = null;
        }
        if (replace.Count == 0) return;
        foreach (var w in faces.Where(w => !w.Dead))
            for (int li = 0; li < w.Loops.Count; li++)
                if (w.Loops[li].Any(c => replace.ContainsKey(c.Edge)))
                    w.Loops[li] = w.Loops[li].SelectMany(c => !replace.TryGetValue(c.Edge, out var r) ? [c]
                        : r.Edge < 0 ? Array.Empty<BrepCoedge>() : [new BrepCoedge(r.Edge, r.Flip ? !c.Forward : c.Forward)]).ToList();
        foreach (var w in faces) w.Loops.RemoveAll(l => l.Count == 0);
    }

    /// <summary>A direction made to point into the upper half (so that n and -n give the same key).</summary>
    private static Vec3 Unsigned(Vec3 n) =>
        n.Z < -1e-9 || (Math.Abs(n.Z) <= 1e-9 && (n.Y < -1e-9 || (Math.Abs(n.Y) <= 1e-9 && n.X < 0))) ? n * -1 : n;

    /// <summary>Coedges joined into loops at their shared vertices (ring edges are a loop each).</summary>
    private static List<List<BrepCoedge>> Chain(List<BrepCoedge> coedges, List<BrepEdge?> edges)
    {
        int From(BrepCoedge c) { var e = edges[c.Edge]!; return c.Forward ? e.Start : e.End; }
        int To(BrepCoedge c) { var e = edges[c.Edge]!; return c.Forward ? e.End : e.Start; }
        var loops = new List<List<BrepCoedge>>();
        var byStart = new Dictionary<int, Queue<BrepCoedge>>();
        foreach (var c in coedges)
        {
            if (edges[c.Edge]!.Start < 0) { loops.Add([c]); continue; }
            if (!byStart.TryGetValue(From(c), out var q)) byStart[From(c)] = q = new();
            q.Enqueue(c);
        }
        foreach (var start in byStart.Keys.ToList())
            while (byStart[start].Count > 0)
            {
                var loop = new List<BrepCoedge>();
                var c = byStart[start].Dequeue();
                int first = From(c);
                while (true)
                {
                    loop.Add(c);
                    int v = To(c);
                    if (v == first) break;
                    if (!byStart.TryGetValue(v, out var q) || q.Count == 0) throw new PushPullException(Lang.T("De randen sluiten niet meer aan; dat kan nog niet.", "The edges no longer join up; that is not possible yet."));
                    c = q.Dequeue();
                }
                loops.Add(loop);
            }
        return loops;
    }

    /// <summary>At the given vertices: two straight edges in one line, or two arcs of one circle (and nothing else
    /// there), become one edge.</summary>
    private static void JoinStraight(List<Vec3> verts, List<BrepEdge?> edges, List<Work> faces, List<int> candidates)
    {
        // (only edges a face still runs along: one dropped from every face is gone)
        var inUse = faces.Where(f => !f.Dead).SelectMany(f => f.Loops).SelectMany(l => l).Select(c => c.Edge).ToHashSet();
        var at = new Dictionary<int, List<int>>();
        for (int e = 0; e < edges.Count; e++)
            if (inUse.Contains(e) && edges[e] is { Start: >= 0 } x)
                foreach (int v in new[] { x.Start, x.End }) { if (!at.TryGetValue(v, out var l)) at[v] = l = new(); if (!l.Contains(e)) l.Add(e); }
        var todo = new Queue<int>(candidates);
        while (todo.TryDequeue(out int v))
        {
            if (!at.TryGetValue(v, out var inc) || inc.Count != 2) continue;
            int e1 = inc[0], e2 = inc[1];
            var a = edges[e1]!; var b = edges[e2]!;
            if (a.Start == a.End || b.Start == b.End) continue;
            int x = a.Start == v ? a.End : a.Start, y = b.Start == v ? b.End : b.Start;
            BrepCurve curve = new BrepLine();
            if (a.Curve is BrepLine && b.Curve is BrepLine)
            {
                var u = verts[v] - verts[x]; var w = verts[y] - verts[v];
                if (u.Cross(w).Length > AngleTol * u.Length * w.Length) continue;
            }
            else if (a.Curve is BrepCircle ca && b.Curve is BrepCircle cb && x != y && SameCircle(ca, cb))
                // the arc from x through v to y: counter-clockwise around the normal that a runs x -> v around
                curve = a.Start == x ? ca : ca with { Normal = ca.Normal * -1 };
            else continue;
            // every loop through v runs over both edges, one after the other
            bool ok = true;
            foreach (var f in faces.Where(f => !f.Dead))
                foreach (var l in f.Loops)
                {
                    int i1 = l.FindIndex(c => c.Edge == e1), i2 = l.FindIndex(c => c.Edge == e2);
                    if ((i1 < 0) != (i2 < 0)) ok = false;
                    else if (i1 >= 0 && (i2 - i1 + l.Count) % l.Count != 1 && (i1 - i2 + l.Count) % l.Count != 1) ok = false;
                }
            if (!ok) continue;
            int ne = -1;
            if (x != y) { ne = edges.Count; edges.Add(new BrepEdge(x, y, curve)); }
            foreach (var f in faces.Where(f => !f.Dead))
                for (int li = 0; li < f.Loops.Count; li++)
                {
                    var l = f.Loops[li];
                    int i1 = l.FindIndex(c => c.Edge == e1);
                    if (i1 < 0) continue;
                    int i2 = l.FindIndex(c => c.Edge == e2);
                    // the first of the two in the loop's direction
                    int first = (i2 - i1 + l.Count) % l.Count == 1 ? i1 : i2;
                    var c0 = l[first];
                    var ec = edges[c0.Edge]!;
                    int from = c0.Forward ? ec.Start : ec.End;
                    var keep = new List<BrepCoedge>();
                    for (int k = 0; k < l.Count; k++)
                    {
                        if (k == i1 || k == i2)
                        {
                            if (k == first && ne >= 0) keep.Add(new BrepCoedge(ne, from == x));
                            continue;
                        }
                        keep.Add(l[k]);
                    }
                    f.Loops[li] = keep;
                }
            foreach (var f in faces) f.Loops.RemoveAll(l => l.Count == 0);
            edges[e1] = null; edges[e2] = null;
            at.Remove(v);
            at[x].Remove(e1); at[y].Remove(e2);
            if (ne >= 0) { at[x].Add(ne); at[y].Add(ne); }
            todo.Enqueue(x); todo.Enqueue(y);
        }
    }

    /// <summary>
    /// Two planar faces in one plane, straight edges only, laid over each other: edges are split where they cross or
    /// touch, edges along the same stretch become one, an edge run both ways (between the two, or within one) goes, and
    /// what is left is traced into loops (at a vertex the sharpest turn to the left, so regions that only touch stay
    /// apart). Every loop that is not a hole becomes a face, facing the way its loop runs round (the way of
    /// <paramref name="a"/> or of <paramref name="b"/>); holes go in the face they lie in. Face a gets the first; the
    /// others are new faces at the end (their indices returned). Null when this does not apply (curved edges).
    /// </summary>
    private static List<int>? MergePlanar(List<Vec3> verts, List<BrepEdge?> edges, List<Work> faces, int a, int b)
    {
        if (faces[a].Surface is not BrepPlane pa || faces[b].Surface is not BrepPlane pb) return null;
        IEnumerable<BrepCoedge> Both() => faces[a].Loops.Concat(faces[b].Loops).SelectMany(l => l);
        if (Both().Any(c => edges[c.Edge] is not { Curve: BrepLine, Start: >= 0 })) return null;
        var na = (pa.Normal * (faces[a].Reversed ? -1 : 1)).Normalized();
        var ax = Math.Abs(na.X) < 0.9 ? new Vec3(1, 0, 0).Cross(na).Normalized() : new Vec3(0, 1, 0).Cross(na).Normalized();
        var ay = na.Cross(ax);
        (double X, double Y) P2(int v) => (verts[v].Dot(ax), verts[v].Dot(ay));
        // the two faces' regions as they are now (even-odd over their loops), to tell later which side of an edge is what
        List<List<(double X, double Y)>> Region(Work w) => w.Loops.Select(l => l.Select(c => P2(c.Forward ? edges[c.Edge]!.Start : edges[c.Edge]!.End)).ToList()).ToList();
        var regionA = Region(faces[a]); var regionB = Region(faces[b]);
        static bool In(List<List<(double X, double Y)>> region, (double X, double Y) q)
        {
            bool inside = false;
            foreach (var poly in region)
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                        inside = !inside;
            return inside;
        }

        void Split(int ei, int v)
        {
            var e = edges[ei]!;
            int e2 = edges.Count;
            edges[ei] = e with { End = v };
            edges.Add(new BrepEdge(v, e.End, new BrepLine()));
            foreach (var w in faces.Where(f => !f.Dead))
                for (int li = 0; li < w.Loops.Count; li++)
                    if (w.Loops[li].Any(x => x.Edge == ei))
                        w.Loops[li] = w.Loops[li].SelectMany(x => x.Edge != ei ? [x]
                            : x.Forward ? new[] { new BrepCoedge(ei, true), new BrepCoedge(e2, true) } : [new BrepCoedge(e2, false), new BrepCoedge(ei, false)]).ToList();
        }

        // 1. crossings and touching points become vertices
        for (bool again = true; again;)
        {
            again = false;
            var es = Both().Select(c => c.Edge).Distinct().ToList();
            foreach (int e1 in es)
            {
                var s = edges[e1]!;
                var (p0, p1) = (P2(s.Start), P2(s.End));
                double dx = p1.X - p0.X, dy = p1.Y - p0.Y, len2 = dx * dx + dy * dy;
                if (len2 < 1e-18) continue;
                // a vertex of the two faces inside this edge
                foreach (int v in es.SelectMany(x => new[] { edges[x]!.Start, edges[x]!.End }).Distinct())
                {
                    if (v == s.Start || v == s.End) continue;
                    var q = P2(v);
                    double t = ((q.X - p0.X) * dx + (q.Y - p0.Y) * dy) / len2;
                    if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                    double ex = p0.X + dx * t - q.X, ey = p0.Y + dy * t - q.Y;
                    if (Math.Sqrt(ex * ex + ey * ey) < DistanceTol) { Split(e1, v); again = true; break; }
                }
                if (again) break;
                // a proper crossing with another edge
                foreach (int e2 in es)
                {
                    if (e2 == e1) continue;
                    var o = edges[e2]!;
                    if (o.Start == s.Start || o.Start == s.End || o.End == s.Start || o.End == s.End) continue;
                    var (q0, q1) = (P2(o.Start), P2(o.End));
                    double ux = q1.X - q0.X, uy = q1.Y - q0.Y, den = dx * uy - dy * ux;
                    if (Math.Abs(den) < 1e-18) continue;
                    double t = ((q0.X - p0.X) * uy - (q0.Y - p0.Y) * ux) / den, w = ((q0.X - p0.X) * dy - (q0.Y - p0.Y) * dx) / den;
                    if (t <= 1e-9 || t >= 1 - 1e-9 || w <= 1e-9 || w >= 1 - 1e-9) continue;
                    int nv = verts.Count;
                    verts.Add(verts[s.Start] + (verts[s.End] - verts[s.Start]) * t);
                    Split(e1, nv); Split(e2, nv);
                    again = true; break;
                }
                if (again) break;
            }
        }
        // 2. edges between the same vertices are one
        Canonicalize(edges, faces);

        // 3. an edge run both ways goes (as often as it is)
        var co = Both().ToList();
        var counts = co.GroupBy(c => c.Edge).ToDictionary(g => g.Key, g => (F: g.Count(c => c.Forward), B: g.Count(c => !c.Forward)));
        var left = new List<BrepCoedge>();
        foreach (var (e, (f, bk)) in counts)
        {
            int k = Math.Min(f, bk);
            for (int i = 0; i < f - k; i++) left.Add(new BrepCoedge(e, true));
            for (int i = 0; i < bk - k; i++) left.Add(new BrepCoedge(e, false));
        }

        // 4. which region an edge that is left bounds: the region on its left (seen along na) is of a only → it bounds
        // a part facing as a, with that part on its left; else it bounds a part facing as b, with that on its right
        int From(BrepCoedge c) => c.Forward ? edges[c.Edge]!.Start : edges[c.Edge]!.End;
        int To(BrepCoedge c) => c.Forward ? edges[c.Edge]!.End : edges[c.Edge]!.Start;
        (double X, double Y) Dir(BrepCoedge c) { var p = P2(From(c)); var q = P2(To(c)); return (q.X - p.X, q.Y - p.Y); }
        // (facing the same way the two just join: every edge left bounds the joined region, on its left)
        var nb = (pb.Normal * (faces[b].Reversed ? -1 : 1)).Normalized();
        bool sameWay = na.Dot(nb) > 0;
        bool OfA(BrepCoedge c)
        {
            if (sameWay) return true;
            var p = P2(From(c)); var d = Dir(c);
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y), eps = Math.Min(1e-7, len * 1e-3);
            var q = (p.X + d.X / 2 - d.Y / len * eps, p.Y + d.Y / 2 + d.X / len * eps);
            return In(regionA, q) && !In(regionB, q);
        }
        // 5. traced into loops, each kind on its own: from where an edge arrives, the sharpest turn towards its region
        // (left for a's kind, right for b's), so parts that only touch at a vertex stay apart
        var loops = new List<(List<BrepCoedge> Loop, bool A)>();
        foreach (bool kindA in new[] { true, false })
        {
            var pool = left.Where(c => OfA(c) == kindA).ToList();
            while (pool.Count > 0)
            {
                var loop = new List<BrepCoedge>();
                var c = pool[0]; pool.RemoveAt(0);
                int start = From(c);
                for (int guard = 0; guard < 100000; guard++)
                {
                    loop.Add(c);
                    int v = To(c);
                    if (v == start) break;
                    var din = Dir(c);
                    var next = pool.Where(x => From(x) == v).ToList();
                    if (next.Count == 0)
                    {
                        if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2")
                            Console.Error.WriteLine($"[merge] {a}+{b}: dead end at vertex {v} {verts[v] * 1000} (kind {(kindA ? "a" : "b")}); left: {string.Join(" ", left.Select(x => $"{From(x)}->{To(x)}{(OfA(x) ? "a" : "b")}"))}");
                        return null;
                    }
                    double Turn(BrepCoedge x) { var d = Dir(x); return Math.Atan2(din.X * d.Y - din.Y * d.X, din.X * d.X + din.Y * d.Y); }
                    var pick = kindA ? next.MaxBy(Turn)! : next.MinBy(Turn)!;
                    pool.Remove(pick);
                    c = pick;
                }
                if (To(loop[^1]) != start) return null;
                loops.Add((loop, kindA));
            }
        }

        // (only now, with the loops traced, the edges that went are taken out)
        var usedElsewhere = faces.Where((w, i) => !w.Dead && i != a && i != b).SelectMany(w => w.Loops).SelectMany(l => l).Select(c => c.Edge).ToHashSet();
        foreach (int e in counts.Keys)
            if (!left.Any(c => c.Edge == e) && !usedElsewhere.Contains(e)) edges[e] = null;

        // 6. outer loops and holes: a's kind runs round na (holes the other way), b's kind the other way round
        var tmp = new BrepBody { Vertices = verts, Edges = edges.Select(x => x ?? new BrepEdge(0, 0, new BrepLine())).ToList(), Faces = [] };
        var pts = loops.Select(l => FaceGeometry.LoopPoints(tmp, l.Loop).Select(p => (p.Dot(ax), p.Dot(ay))).ToList()).ToList();
        var area = pts.Select(p => { double s = 0; for (int i = 0; i < p.Count; i++) s += p[i].Item1 * p[(i + 1) % p.Count].Item2 - p[(i + 1) % p.Count].Item1 * p[i].Item2; return s / 2; }).ToList();
        bool Outer(int i) => loops[i].A ? area[i] > 0 : area[i] < 0;
        var holeOf = new int[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            holeOf[i] = -1;
            if (Outer(i)) continue;
            var q = ((pts[i][0].Item1 + pts[i][1].Item1) / 2, (pts[i][0].Item2 + pts[i][1].Item2) / 2);
            double best = double.MaxValue;
            for (int j = 0; j < loops.Count; j++)
                if (j != i && loops[j].A == loops[i].A && Outer(j) && Math.Abs(area[j]) < best && In([pts[j]], q))
                { best = Math.Abs(area[j]); holeOf[i] = j; }
        }
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2")
            Console.Error.WriteLine($"[merge] faces {a}+{b} n {na}: {loops.Count} loops, kinds {string.Join(",", loops.Select(l => l.A ? "a" : "b"))}, areas {string.Join(", ", area.Select(x => (x * 1e6).ToString("0.###")))}, holes {string.Join(",", holeOf)}");
        var outers = Enumerable.Range(0, loops.Count).Where(i => Outer(i) && Math.Abs(area[i]) > 1e-18).ToList();
        if (outers.Count == 0) { faces[a].Loops = []; faces[a].Dead = true; return []; }
        var sa = faces[a].Surface; bool ra = faces[a].Reversed;
        var sb = faces[b].Surface; bool rb = faces[b].Reversed;
        var extra = new List<int>();
        for (int k = 0; k < outers.Count; k++)
        {
            int o = outers[k];
            var ls = new List<List<BrepCoedge>> { loops[o].Loop };
            ls.AddRange(Enumerable.Range(0, loops.Count).Where(i => holeOf[i] == o).Select(i => loops[i].Loop));
            var (s, r) = loops[o].A ? (sa, ra) : (sb, rb);
            if (k == 0) { faces[a].Surface = s; faces[a].Reversed = r; faces[a].Loops = ls; }
            else { extra.Add(faces.Count); faces.Add(new Work(s, r, ls)); }
        }
        return extra;
    }

    /// <summary>Straight edges (in use) with one of the given vertices strictly inside them are split there, in every
    /// face that runs along them; until none is left.</summary>
    private static void SplitAtVertices(List<Vec3> verts, List<BrepEdge?> edges, List<Work> faces, List<int> candidates)
    {
        for (bool again = true; again;)
        {
            again = false;
            var inUse = faces.Where(f => !f.Dead).SelectMany(f => f.Loops).SelectMany(l => l).Select(c => c.Edge).ToHashSet();
            foreach (int ei in inUse)
            {
                if (edges[ei] is not { Curve: BrepLine, Start: >= 0 } e || e.Start == e.End) continue;
                Vec3 a = verts[e.Start], ab = verts[e.End] - a;
                double len2 = ab.Dot(ab);
                if (len2 < 1e-18) continue;
                int hit = -1;
                foreach (int v in candidates)
                {
                    if (v == e.Start || v == e.End) continue;
                    double t = (verts[v] - a).Dot(ab) / len2;
                    if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                    if ((a + ab * t - verts[v]).Length < DistanceTol) { hit = v; break; }
                }
                if (hit < 0) continue;
                int e2 = edges.Count;
                edges[ei] = e with { End = hit };
                edges.Add(new BrepEdge(hit, e.End, new BrepLine()));
                foreach (var w in faces.Where(f => !f.Dead))
                    for (int li = 0; li < w.Loops.Count; li++)
                        if (w.Loops[li].Any(x => x.Edge == ei))
                            w.Loops[li] = w.Loops[li].SelectMany(x => x.Edge != ei ? [x]
                                : x.Forward ? new[] { new BrepCoedge(ei, true), new BrepCoedge(e2, true) } : [new BrepCoedge(e2, false), new BrepCoedge(ei, false)]).ToList();
                again = true;
                break;
            }
        }
    }

    private static bool SameCircle(BrepCircle a, BrepCircle b) =>
        (a.Centre - b.Centre).Length < DistanceTol && Math.Abs(a.Radius - b.Radius) < DistanceTol
        && Math.Abs(a.Normal.Normalized().Dot(b.Normal.Normalized())) > 1 - AngleTol * AngleTol;

    /// <summary>Planar faces with their outer loop first.</summary>
    internal static List<BrepFace> OrderLoops(BrepBody body)
    {
        var list = new List<BrepFace>();
        foreach (var f in body.Faces)
        {
            if (f.Surface is not BrepPlane || f.Loops.Count < 2) { list.Add(f); continue; }
            var n = FaceGeometry.Normal(f);
            var area = f.Loops.Select(l => FaceGeometry.Area(FaceGeometry.LoopPoints(body, l), n)).ToList();
            int outer = Enumerable.Range(0, area.Count).MaxBy(i => area[i]);
            list.Add(f with { Loops = new[] { f.Loops[outer] }.Concat(f.Loops.Where((_, i) => i != outer)).ToList() });
        }
        return list;
    }

    /// <summary>
    /// Why a body no longer closes, in the user's words: most often two parts of it that would touch only along an edge
    /// (two blocks pulled up from faces that meet only in a point, side by side: four faces round one edge), which no
    /// solid can be; where (millimetres).
    /// </summary>
    /// <summary>Whether an edge of the body is used more than once in one direction (more than two faces round it).</summary>
    private static bool TouchesItself(BrepBody body) =>
        body.Faces.SelectMany(f => f.Loops.SelectMany(l => l)).GroupBy(c => c).Any(g => g.Count() > 1);

    internal static string WhyNotClosed(BrepBody body)
    {
        var uses = new Dictionary<(int, bool), int>();
        foreach (var c in body.Faces.SelectMany(f => f.Loops.SelectMany(l => l))) uses[(c.Edge, c.Forward)] = uses.GetValueOrDefault((c.Edge, c.Forward)) + 1;
        for (int e = 0; e < body.Edges.Count; e++)
        {
            if (uses.GetValueOrDefault((e, true)) < 2 && uses.GetValueOrDefault((e, false)) < 2) continue;
            var E = body.Edges[e];
            var at = E.Start >= 0 ? (body.Vertices[E.Start] + body.Vertices[E.End]) * 0.5 : FaceGeometry.EdgePoints(body, E)[0];
            string mm = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.X * 1000:0.#}, {at.Y * 1000:0.#}, {at.Z * 1000:0.#}");
            return Lang.T($"Zo raken twee uitstekende stukken elkaar alleen langs een rand (bij {mm} mm); dat kan een massief blok niet zijn. Laat ze een vlak delen (trek ook het vlak ertussen mee) of trek er maar één uit.",
                $"That way two raised pieces would touch only along an edge (at {mm} mm); a solid block cannot be like that. Let them share a face (pull the face between them too) or pull only one of them.");
        }
        return Lang.T("Zo klopt de vorm van het blok niet meer (een rand hoort dan niet bij precies twee vlakken).", "That way the shape of the block no longer holds (an edge would not belong to exactly two faces).");
    }

    /// <summary>
    /// The changed faces must still be sound: every edge used once each way, and a planar face one outer loop running
    /// counter-clockwise with holes inside it, no loop crossing itself or another.
    /// </summary>
    private static void Check(BrepBody body, IEnumerable<int> changed)
    {
        if (XtBrepBuilder.TopologyError(body) is not null)
        {
            string why = WhyNotClosed(body);
            throw TouchesItself(body) ? new TouchesItselfException(why) : new PushPullException(why);
        }
        foreach (int f in changed.Distinct())
        {
            var face = body.Faces[f];
            if (face.Surface is not BrepPlane) continue;
            var n = FaceGeometry.Normal(face);
            var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
            var y = n.Cross(x);
            var loops = face.Loops.Select(l => FaceGeometry.LoopPoints(body, l)).ToList();
            var areas = loops.Select(l => FaceGeometry.Area(l, n)).ToList();
            if (areas[0] <= 1e-14 || areas.Skip(1).Any(a => a >= 0))
            {
                if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2")
                    Console.Error.WriteLine($"[check] face {f} n {n}: areas {string.Join(", ", areas.Select(a => (a * 1e6).ToString("0.###")))}\n  "
                        + string.Join("\n  ", loops.Select(l => string.Join(" -> ", l.Select(p => $"({p.X * 1e3:0.##},{p.Y * 1e3:0.##},{p.Z * 1e3:0.##})")))));
                throw new PushPullException(Lang.T("Een vlak komt binnenstebuiten te staan: het gaat door een ander vlak heen. Dat kan nog niet.", "A face turns inside out: it goes through another face. That is not possible yet."));
            }
            var segs = new List<((double X, double Y) A, (double X, double Y) B)>();
            foreach (var l in loops)
                for (int i = 0; i < l.Count; i++)
                    segs.Add(((l[i].Dot(x), l[i].Dot(y)), (l[(i + 1) % l.Count].Dot(x), l[(i + 1) % l.Count].Dot(y))));
            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                    if (Cross(segs[i], segs[j]))
                    {
                        if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2")
                            Console.Error.WriteLine($"[check] face {f} n {n} crosses itself:\n  "
                                + string.Join("\n  ", loops.Select(l => string.Join(" -> ", l.Select(p => $"({p.X * 1e3:0.##},{p.Y * 1e3:0.##},{p.Z * 1e3:0.##})")))));
                        throw new PushPullException(Lang.T("Een vlak gaat door zichzelf heen: het raakt een ander deel van de vorm. Dat kan nog niet.", "A face goes through itself: it touches another part of the shape. That is not possible yet."));
                    }
        }
    }

    /// <summary>Two segments cross properly (not just touching at an end).</summary>
    private static bool Cross(((double X, double Y) A, (double X, double Y) B) s, ((double X, double Y) A, (double X, double Y) B) t)
    {
        if (Math.Max(s.A.X, s.B.X) < Math.Min(t.A.X, t.B.X) || Math.Max(t.A.X, t.B.X) < Math.Min(s.A.X, s.B.X)
            || Math.Max(s.A.Y, s.B.Y) < Math.Min(t.A.Y, t.B.Y) || Math.Max(t.A.Y, t.B.Y) < Math.Min(s.A.Y, s.B.Y)) return false;
        static double O((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        double scale = Math.Max(Math.Abs(s.B.X - s.A.X) + Math.Abs(s.B.Y - s.A.Y), Math.Abs(t.B.X - t.A.X) + Math.Abs(t.B.Y - t.A.Y));
        double eps = 1e-9 * scale * scale;
        double d1 = O(s.A, s.B, t.A), d2 = O(s.A, s.B, t.B), d3 = O(t.A, t.B, s.A), d4 = O(t.A, t.B, s.B);
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }
}
