using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public static partial class PushPull
{
    /// <summary>
    /// The roundings that hang together with <paramref name="face"/> (cylinders between flat faces, joined by ball corners
    /// or mitres, all with the same radius) taken away again: their flat faces run on to where they meet, each rounding
    /// becomes the sharp edge it rounded, each of its corners the point where the flat faces meet. Returns the sharp body,
    /// per old face its new number (−1 for the roundings), per sharp edge the rounding (old face) it came from, and the
    /// radius. The inverse of <see cref="RoundEdges"/> (also for roundings DesignSpark made in the same way).
    /// </summary>
    public static (BrepBody Body, int[] FaceMap, List<(int Edge, int From, double Radius)> Edges, double Radius) Unround(BrepBody body, int face)
    {
        var (b, map, edges, r, _) = UnroundMapped(body, face);
        return (b, map, edges, r);
    }

    /// <summary>As <see cref="Unround"/>, with per old edge its new number (−1: gone).</summary>
    public static (BrepBody Body, int[] FaceMap, List<(int Edge, int From, double Radius)> Edges, double Radius, int[] EdgeMap) UnroundMapped(BrepBody body, int face)
    {
        var F = body.Faces[face];
        double r = F.Surface switch { BrepCylinder c => c.Radius, BrepSphere s => s.Radius, BrepTorus t => t.MinorRadius, _ => throw new PushPullException(Lang.T("Dit is geen ronding.", "This is not a rounding.")) };
        var facesOf = new Dictionary<int, List<int>>();
        for (int g = 0; g < body.Faces.Count; g++)
            foreach (var c in body.Faces[g].Loops.SelectMany(l => l))
                (facesOf.TryGetValue(c.Edge, out var l) ? l : facesOf[c.Edge] = []).Add(g);
        // (roundings between flat faces and ball corners, of any radius: a mitre may join two of different radius)
        bool Candidate(int g) => body.Faces[g].Surface switch
        {
            BrepCylinder => IsRounding(body, g),
            BrepSphere or BrepTorus or BrepNurbsSurface => true,
            _ => false,
        };
        // the roundings that hang together (over their curved edges)
        var set = new HashSet<int> { face };
        var queue = new Queue<int>([face]);
        while (queue.Count > 0)
            foreach (var c in body.Faces[queue.Dequeue()].Loops.SelectMany(l => l))
                if (body.Edges[c.Edge].Curve is not BrepLine)
                    foreach (int h in facesOf[c.Edge])
                        if (!set.Contains(h) && Candidate(h)) { set.Add(h); queue.Enqueue(h); }
        var setEdges = set.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => c.Edge).ToHashSet();
        if (setEdges.Any(e => body.Edges[e].Start < 0)) throw new PushPullException(Lang.T("Deze ronding loopt helemaal rond; die kan (nog) niet anders.", "This rounding goes all the way round; it cannot be changed (yet)."));

        // per rounding cylinder: its two straight edges and the flat faces they lie on
        var cyl = new List<(int Face, int L1, int L2, int A, int B)>();
        foreach (int g in set.Where(g => body.Faces[g].Surface is BrepCylinder))
        {
            var lines = body.Faces[g].Loops.SelectMany(l => l).Select(c => c.Edge).Where(e => body.Edges[e].Curve is BrepLine).Distinct().ToList();
            var onFlat = lines.Select(e => (e, f: facesOf[e].Where(h => h != g).ToList())).ToList();
            if (onFlat.Count != 2 || onFlat.Any(x => x.f.Count != 1 || body.Faces[x.f[0]].Surface is not BrepPlane))
                throw new PushPullException(Lang.T("Deze ronding ligt niet tussen twee platte vlakken; die kan (nog) niet anders.", "This rounding does not lie between two flat faces; it cannot be changed (yet)."));
            cyl.Add((g, onFlat[0].e, onFlat[1].e, onFlat[0].f[0], onFlat[1].f[0]));
        }
        if (cyl.Count == 0) throw new PushPullException(Lang.T("Dit is geen ronding tussen vlakken.", "This is not a rounding between faces."));

        // the corners: the points of the roundings that their curved edges join (a ball's three, a mitre's two, an end arc's two)
        var parent = new Dictionary<int, int>();
        int Find(int v) { while (parent[v] != v) v = parent[v] = parent[parent[v]]; return v; }
        foreach (int e in setEdges) foreach (int v in new[] { body.Edges[e].Start, body.Edges[e].End }) parent.TryAdd(v, v);
        foreach (int e in setEdges.Where(e => body.Edges[e].Curve is not BrepLine))
            parent[Find(body.Edges[e].Start)] = Find(body.Edges[e].End);
        var clusters = parent.Keys.GroupBy(Find).ToDictionary(g => g.Key, g => g.ToList());
        // where the flat faces of each corner meet
        var planesOf = clusters.Keys.ToDictionary(k => k, _ => new HashSet<int>());
        foreach (var c in cyl)
            foreach (int v in new[] { body.Edges[c.L1].Start, body.Edges[c.L1].End, body.Edges[c.L2].Start, body.Edges[c.L2].End })
            { planesOf[Find(v)].Add(c.A); planesOf[Find(v)].Add(c.B); }
        foreach (int e in setEdges.Where(e => body.Edges[e].Curve is not BrepLine))
            foreach (int h in facesOf[e].Where(h => !set.Contains(h)))
            {
                if (body.Faces[h].Surface is not BrepPlane) throw new PushPullException(Lang.T("Deze ronding grenst aan een ander rond vlak; die kan (nog) niet anders.", "This rounding borders on another round face; it cannot be changed (yet)."));
                planesOf[Find(body.Edges[e].Start)].Add(h);
            }
        var verts = body.Vertices.ToList();
        var corner = new Dictionary<int, int>();
        foreach (var (k, planes) in planesOf)
        {
            var ps = planes.Select(h => ((BrepPlane)body.Faces[h].Surface)).Select(p => (N: p.Normal.Normalized(), D: p.Normal.Normalized().Dot(p.Origin))).ToList();
            // least squares: Σ n nᵀ x = Σ n d
            double[,] m = new double[3, 3]; double[] rhs = new double[3];
            foreach (var (n, d) in ps)
            {
                double[] nv = [n.X, n.Y, n.Z];
                for (int i = 0; i < 3; i++) { rhs[i] += nv[i] * d; for (int j = 0; j < 3; j++) m[i, j] += nv[i] * nv[j]; }
            }
            var x = Solve3(m, rhs) ?? throw new PushPullException(Lang.T("De vlakken bij een hoek van deze ronding komen niet in één punt samen; die kan (nog) niet anders.", "The faces at a corner of this rounding do not meet in one point; it cannot be changed (yet)."));
            var p0 = new Vec3(x[0], x[1], x[2]);
            if (ps.Any(q => Math.Abs(q.N.Dot(p0) - q.D) > 10 * DistanceTol))
                throw new PushPullException(Lang.T("De vlakken bij een hoek van deze ronding komen niet in één punt samen; die kan (nog) niet anders.", "The faces at a corner of this rounding do not meet in one point; it cannot be changed (yet)."));
            corner[k] = verts.Count; verts.Add(p0);
        }
        int Map(int v) => parent.ContainsKey(v) ? corner[Find(v)] : v;

        // the new edges: the rounding cylinders become their sharp edges (on L1's place), curved edges go
        var edges = body.Edges.Select(e => (BrepEdge?)e).ToList();
        var replace = new Dictionary<int, (int Edge, bool Flip)>();
        var sharp = new List<(int Edge, int From, double Radius)>();
        foreach (var c in cyl)
        {
            var e1 = body.Edges[c.L1]; var e2 = body.Edges[c.L2];
            int s = Map(e1.Start), t = Map(e1.End);
            if (s == t) throw new PushPullException(Lang.T("Deze ronding kan (nog) niet anders.", "This rounding cannot be changed (yet)."));
            bool flip = Map(e2.Start) == t && Map(e2.End) == s;
            if (!flip && !(Map(e2.Start) == s && Map(e2.End) == t)) throw new PushPullException(Lang.T("Deze ronding kan (nog) niet anders.", "This rounding cannot be changed (yet)."));
            edges[c.L1] = new BrepEdge(s, t, new BrepLine());
            edges[c.L2] = null;
            replace[c.L2] = (c.L1, flip);
            sharp.Add((c.L1, c.Face, ((BrepCylinder)body.Faces[c.Face].Surface).Radius));
        }
        foreach (int e in setEdges.Where(e => body.Edges[e].Curve is not BrepLine)) edges[e] = null;
        for (int e = 0; e < edges.Count; e++)
            if (edges[e] is { Start: >= 0 } x && !setEdges.Contains(e) && (parent.ContainsKey(x.Start) || parent.ContainsKey(x.End)))
            {
                if (x.Curve is not BrepLine) throw new PushPullException(Lang.T("Bij een hoek van deze ronding komt een gebogen rand uit; die kan (nog) niet anders.", "A curved edge ends at a corner of this rounding; it cannot be changed (yet)."));
                edges[e] = x with { Start = Map(x.Start), End = Map(x.End) };
                if (edges[e]!.Start == edges[e]!.End) throw new PushPullException(Lang.T("Deze ronding kan (nog) niet anders.", "This rounding cannot be changed (yet)."));
            }
        // the faces: the roundings out; in the flat ones the curved edges out, the second straight edge of a rounding becomes its sharp edge
        var faceMap = new int[body.Faces.Count];
        var outFaces = new List<BrepFace>();
        var changed = new List<int>();
        for (int g = 0; g < body.Faces.Count; g++)
        {
            if (set.Contains(g)) { faceMap[g] = -1; continue; }
            var loops = new List<IReadOnlyList<BrepCoedge>>();
            bool touched = false;
            foreach (var l in body.Faces[g].Loops)
            {
                var nl = new List<BrepCoedge>();
                foreach (var c in l)
                {
                    if (replace.TryGetValue(c.Edge, out var rp)) { nl.Add(new BrepCoedge(rp.Edge, rp.Flip ? !c.Forward : c.Forward)); touched = true; }
                    else if (edges[c.Edge] == null) touched = true;
                    else { nl.Add(c); if (setEdges.Contains(c.Edge) || parent.ContainsKey(body.Edges[c.Edge].Start) || parent.ContainsKey(body.Edges[c.Edge].End)) touched = true; }
                }
                loops.Add(nl);
            }
            faceMap[g] = outFaces.Count;
            if (touched) changed.Add(outFaces.Count);
            outFaces.Add(body.Faces[g] with { Loops = loops });
        }
        // compact: edges and vertices still used, renumbered
        var eMap = new Dictionary<int, int>(); var newEdges = new List<BrepEdge>();
        for (int e = 0; e < edges.Count; e++) if (edges[e] is { } x) { eMap[e] = newEdges.Count; newEdges.Add(x); }
        var usedV = newEdges.Where(x => x.Start >= 0).SelectMany(x => new[] { x.Start, x.End }).Distinct().Order().ToList();
        var vMap = usedV.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        var result = new BrepBody
        {
            Vertices = usedV.Select(v => verts[v]).ToList(),
            Edges = newEdges.Select(x => x.Start < 0 ? x : x with { Start = vMap[x.Start], End = vMap[x.End] }).ToList(),
            Faces = outFaces.Select(f => f with { Loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => c with { Edge = eMap[c.Edge] }).ToList()).ToList() }).ToList(),
        };
        Check(result, changed);
        if (SignedVolume(result) is double vol && vol <= 0) throw new PushPullException(Lang.T("Zo klopt de vorm niet meer (binnenstebuiten).", "That way the shape is no longer right (inside out)."));
        var edgeMap = Enumerable.Range(0, body.Edges.Count).Select(e => eMap.TryGetValue(e, out int ne) ? ne : -1).ToArray();
        return (result, faceMap, sharp.Select(x => (eMap[x.Edge], x.From, x.Radius)).ToList(), r, edgeMap);
    }

    /// <summary>
    /// Edges rounded (<see cref="RoundEdges(BrepBody, IReadOnlyList{ValueTuple{int, double}})"/>) where some already end at
    /// roundings (a corner of a block whose top edges are rounded): those roundings are taken away first and made again
    /// together with the new ones, each with its own radius (as DesignSpark does). Returns the new body, the new faces
    /// (the new roundings first) and per old face its new number (−1 for roundings that were made again).
    /// </summary>
    public static (BrepBody Body, List<int> Faces, int[] FaceMap) RoundEdgesJoining(BrepBody body, IReadOnlyList<(int Edge, double Radius)> edges)
    {
        var cur = body;
        var faceMap = Enumerable.Range(0, body.Faces.Count).ToArray();
        var wanted = edges.ToList();
        var again = new List<(int Edge, double Radius)>();
        for (int guard = 0; guard < 50; guard++)
        {
            // a rounding (or corner piece) at an end of an edge to round
            var ends = wanted.SelectMany(x => new[] { cur.Edges[x.Edge].Start, cur.Edges[x.Edge].End }).Where(v => v >= 0).ToHashSet();
            var near = Enumerable.Range(0, cur.Edges.Count).Where(e => cur.Edges[e].Start >= 0 && (ends.Contains(cur.Edges[e].Start) || ends.Contains(cur.Edges[e].End))).ToHashSet();
            int face = Enumerable.Range(0, cur.Faces.Count).FirstOrDefault(f => cur.Faces[f].Surface is not BrepPlane && IsRoundingSet(cur, f)
                && cur.Faces[f].Loops.Any(l => l.Any(c => near.Contains(c.Edge))), -1);
            if (face < 0) break;
            var (sharp, fmap, from, _, emap) = UnroundMapped(cur, face);
            again = again.Select(x => (emap[x.Edge], x.Radius)).Concat(from.Select(x => (x.Edge, x.Radius))).ToList();
            wanted = wanted.Select(x => (emap[x.Edge], x.Radius)).ToList();
            if (wanted.Any(x => x.Edge < 0) || again.Any(x => x.Edge < 0)) throw new PushPullException(Lang.T("Deze rand kan (nog) niet samen met de rondingen ernaast afgerond worden.", "This edge cannot be rounded together with the roundings next to it (yet)."));
            faceMap = faceMap.Select(f => f < 0 ? -1 : fmap[f]).ToArray();
            cur = sharp;
        }
        var all = wanted.Concat(again.Where(a => !wanted.Any(w => w.Edge == a.Edge))).ToList();
        var (b, faces) = RoundEdges(cur, all);
        return (b, faces, faceMap);
    }

    /// <summary>The centre of a corner piece (a ball or a torus); null for anything else.</summary>
    private static Vec3? CornerCentre(BrepSurface s) => s switch
    {
        BrepSphere sp => sp.Centre,
        BrepTorus t => t.Centre,
        BrepNurbsSurface n => n.Points.Aggregate(new Vec3(0, 0, 0), (a, p) => a + p) * (1.0 / n.Points.Count),
        _ => null,
    };

    private static double[]? Solve3(double[,] m, double[] b)
    {
        double Det(double[,] a) => a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        double det = Det(m);
        if (Math.Abs(det) < 1e-12) return null;
        var x = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var a = (double[,])m.Clone();
            for (int i = 0; i < 3; i++) a[i, k] = b[i];
            x[k] = Det(a) / det;
        }
        return x;
    }

    /// <summary>
    /// A rounding (and those that hang together with it) given another radius: taken away (<see cref="Unround"/>) and
    /// made again (<see cref="RoundEdges"/>). Its face moved <paramref name="distance"/> along its normal at its middle,
    /// as <see cref="Rounding"/> (the same radius for it). Each old rounding's number goes to its new one.
    /// </summary>
    public static PushPullResult ChangeRounding(BrepBody body, int face, double distance)
    {
        double radius = RadiusAfter(body, face, distance);
        if (radius < 1e-5) throw new PushPullException(Lang.T("Zo wordt de ronding zo klein dat hij verdwijnt.", "That way the rounding gets so small that it disappears."));
        var (sharp, map, edgesFrom, _) = Unround(body, face);
        // only this rounding gets the new radius (the others keep theirs); where that cannot (a ball corner), all of them
        BrepBody b; List<int> newFaces;
        if (body.Faces[face].Surface is BrepSphere or BrepTorus) (b, newFaces) = RoundEdges(sharp, edgesFrom.Select(x => (x.Edge, radius)).ToList());
        else
            // (not all of them instead, unasked: a corner piece clicked does that)
            try { (b, newFaces) = RoundEdges(sharp, edgesFrom.Select(x => (x.Edge, x.From == face ? radius : x.Radius)).ToList()); }
            catch (PushPullException ex) { throw new PushPullException(ex.Message + Lang.T(" (Alle rondingen tegelijk: klik een hoekstuk, de bol of torus in een hoek.)", " (All roundings at once: click a corner piece, the ball or torus in a corner.)")); }
        // the old roundings to the new: a cylinder to the one on its edge, a ball to the nearest new ball
        var full = map.ToArray();
        for (int i = 0; i < edgesFrom.Count; i++) full[edgesFrom[i].From] = newFaces[i];
        var balls = newFaces.Skip(edgesFrom.Count).ToList();
        for (int g = 0; g < body.Faces.Count; g++)
            if (full[g] < 0 && CornerCentre(body.Faces[g].Surface) is { } oc && balls.Count > 0)
                full[g] = balls.MinBy(n => CornerCentre(b.Faces[n].Surface) is { } nc ? (nc - oc).Length : double.MaxValue);
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (int f in newFaces)
            if (FaceGeometry.CurvedTriangles(b, b.Faces[f]) is { } t) curved[f] = t;
        var changed = newFaces.Concat(Enumerable.Range(0, b.Faces.Count).Where(f => b.Faces[f].Surface is BrepPlane
            && b.Faces[f].Loops.Any(l => l.Any(c => newFaces.Any(n => b.Faces[n].Loops.Any(nl => nl.Any(x => x.Edge == c.Edge))))))).Distinct().ToList();
        return new PushPullResult(b, full, [], [], curved, changed, []);
    }

    /// <summary>Whether a face is part of a rounding between flat faces: a rounding cylinder, or a ball corner joined to one.</summary>
    public static bool IsRoundingSet(BrepBody body, int face)
    {
        if (IsRounding(body, face)) return true;
        if (body.Faces[face].Surface is not (BrepSphere or BrepTorus)) return false;
        foreach (var c in body.Faces[face].Loops.SelectMany(l => l))
            for (int g = 0; g < body.Faces.Count; g++)
                if (g != face && body.Faces[g].Surface is BrepCylinder && body.Faces[g].Loops.Any(l => l.Any(x => x.Edge == c.Edge)) && IsRounding(body, g)) return true;
        return false;
    }

    /// <summary>
    /// For a rounding: how far its middle is from the sharp edge, per unit of radius (√(2/(1+cos)) − 1, from the angle of
    /// its two flat faces), and whether it is an outside one; a ball corner: from a rounding joined to it.
    /// </summary>
    private static (double Q, bool Outside) RoundingShape(BrepBody body, int face)
    {
        int g = face;
        if (body.Faces[face].Surface is not BrepCylinder)
            g = body.Faces[face].Loops.SelectMany(l => l).SelectMany(c => Enumerable.Range(0, body.Faces.Count)
                .Where(h => h != face && body.Faces[h].Surface is BrepCylinder && body.Faces[h].Loops.Any(l => l.Any(x => x.Edge == c.Edge)))).First();
        var flats = body.Faces[g].Loops.SelectMany(l => l).Where(c => body.Edges[c.Edge].Curve is BrepLine)
            .SelectMany(c => Enumerable.Range(0, body.Faces.Count).Where(h => h != g && body.Faces[h].Surface is BrepPlane && body.Faces[h].Loops.Any(l => l.Any(x => x.Edge == c.Edge))))
            .Distinct().ToList();
        if (flats.Count != 2) throw new PushPullException(Lang.T("Deze ronding ligt niet tussen twee platte vlakken.", "This rounding does not lie between two flat faces."));
        double cos = FaceGeometry.Normal(body.Faces[flats[0]]).Dot(FaceGeometry.Normal(body.Faces[flats[1]]));
        return (Math.Sqrt(2 / (1 + cos)) - 1, !body.Faces[face].Reversed);
    }
}
