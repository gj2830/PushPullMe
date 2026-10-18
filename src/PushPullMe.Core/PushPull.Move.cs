using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>What is picked up to move: faces, edges and vertices of one body (any of them may be empty); or the whole
/// body (<see cref="Whole"/>), or a copy of it.</summary>
public sealed record MoveSelection(IReadOnlyCollection<int> Faces, IReadOnlyCollection<int> Edges, IReadOnlyCollection<int> Vertices, MoveWhole Whole = MoveWhole.None)
{
    public static MoveSelection Face(int f) => new([f], [], []);
    public static MoveSelection Edge(int e) => new([], [e], []);
    public static MoveSelection Vertex(int v) => new([], [], [v]);
    /// <summary>The whole body (as SketchUp moves a group).</summary>
    public static MoveSelection WholeBody() => new([], [], [], MoveWhole.Body);
    /// <summary>A copy of the whole body, moved (as SketchUp's Move with Ctrl); the body itself stays.</summary>
    public static MoveSelection Copy() => new([], [], [], MoveWhole.Copy);
}

public enum MoveWhole { None, Body, Copy }

/// <summary>How what is picked up moves: shifted over <see cref="Offset"/>, or turned (<see cref="Turn"/>: tilting a face
/// round one of its edges).</summary>
public readonly record struct Motion(Vec3 Offset, BrepTransform.Turn? Turn = null)
{
    public Vec3 Point(Vec3 p) => Turn is { } t ? t.Point(p) : p + Offset;
    public bool IsNothing => Turn is { } t ? Math.Abs(t.Angle) < 1e-12 : Offset.Length < 1e-12;
}

/// <summary>The result of <see cref="PushPull.Move"/>: the new body, per old face its new index (or -1), and the faces
/// (new numbers) that changed: moved, tilted, folded or merged.</summary>
public sealed record MoveResult(BrepBody Body, IReadOnlyList<int> FaceMap, IReadOnlyList<int> Changed);

public static partial class PushPull
{
    /// <summary>
    /// Move, as in SketchUp: the picked vertices (those of a face, the ends of an edge, or one vertex) go over
    /// <paramref name="offset"/> and what hangs on them follows. A face whose every vertex (and ring edge) goes along is
    /// moved as it is (also a curved one); a planar face of which only some go along gets the plane through its new
    /// corners (a four-sided one that cannot stay flat is folded along a diagonal into two triangles, as SketchUp
    /// does); a curved face cannot bend and is refused, as is a curved edge of which only one end goes along. Then the
    /// same tidying as after Push/Pull: vertices that land on each other are one, faces that end up in one plane are
    /// one, and the shape must stay sound.
    /// </summary>
    public static MoveResult Move(BrepBody body, MoveSelection pick, Vec3 offset) => Move(body, pick, new Motion(offset));

    /// <summary>As <see cref="Move(BrepBody, MoveSelection, Vec3)"/>, with any <see cref="Motion"/>: also turned (a face
    /// tilted round one of its edges: the edge stays, the faces round it follow).</summary>
    public static MoveResult Move(BrepBody body, MoveSelection pick, Motion motion)
    {
        if (motion.IsNothing) return new(body, Enumerable.Range(0, body.Faces.Count).ToList(), []);
        var offset = motion.Offset;
        var verts = body.Vertices.ToList();
        var edges = body.Edges.Select(e => (BrepEdge?)e).ToList();
        var faces = body.Faces.Select(f => new Work(f.Surface, f.Reversed, f.Loops.Select(l => l.ToList()).ToList())).ToList();
        var oldNormal = body.Faces.Select(f => f.Surface is BrepPlane ? FaceGeometry.Normal(f) : default).ToList();

        // what goes along: the vertices, and ring edges (no vertices) of picked faces or picked themselves
        var movedV = new HashSet<int>(pick.Vertices);
        var movedRing = new HashSet<int>();
        foreach (int e in pick.Edges)
        {
            var ed = body.Edges[e];
            if (ed.Start < 0) movedRing.Add(e); else { movedV.Add(ed.Start); movedV.Add(ed.End); }
        }
        foreach (int f in pick.Faces)
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
            {
                var ed = body.Edges[c.Edge];
                if (ed.Start < 0) movedRing.Add(c.Edge); else { movedV.Add(ed.Start); movedV.Add(ed.End); }
            }
        if (movedV.Count == 0 && movedRing.Count == 0) throw new PushPullException(Lang.T("Er is niets gekozen om te verplaatsen.", "Nothing is selected to move."));
        foreach (int v in movedV) verts[v] = motion.Point(verts[v]);

        // edges: a curve goes along when all of it does; one end of a curve alone cannot
        for (int e = 0; e < edges.Count; e++)
        {
            var ed = edges[e]!;
            if (ed.Curve is BrepLine) continue;
            bool all = ed.Start < 0 ? movedRing.Contains(e) : movedV.Contains(ed.Start) && movedV.Contains(ed.End);
            bool some = ed.Start >= 0 && (movedV.Contains(ed.Start) || movedV.Contains(ed.End));
            if (all) edges[e] = ed with { Curve = motion.Turn is { } tc ? BrepTransform.TurnCurve(ed.Curve, tc) : Shifted(ed.Curve, offset) };
            // (a circle round the axis of the turn: its moved end just runs on along it, as when an end of a turned
            // piece is turned on, DesignSpark's pull on it)
            else if (some && motion.Turn is { } tk && ed.Curve is BrepCircle kc && RoundAxis(kc.Centre, kc.Normal, tk)) continue;
            else if (some) throw new PushPullException(Lang.T("Een ronde rand kan niet meebuigen; verplaats hem helemaal (het hele vlak of beide eindpunten).", "A round edge cannot bend along; move it wholly (the whole face or both endpoints)."));
        }

        // faces: all along → moved as they are; partly → a planar one gets its new plane (or is folded), a curved one cannot
        var changed = new HashSet<int>();
        int oldFaces = faces.Count;
        for (int f = 0; f < oldFaces; f++)
        {
            var w = faces[f];
            var co = w.Loops.SelectMany(l => l).ToList();
            var vs = co.Select(c => edges[c.Edge]!).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End }).ToHashSet();
            var rings = co.Where(c => edges[c.Edge]!.Start < 0).Select(c => c.Edge).ToList();
            int movedCount = vs.Count(movedV.Contains) + rings.Count(movedRing.Contains);
            if (movedCount == 0) continue;
            changed.Add(f);
            if (movedCount == vs.Count + rings.Count) { w.Surface = motion.Turn is { } ts ? BrepTransform.TurnSurface(w.Surface, ts) : ShiftedSurface(w.Surface, offset); continue; }
            // (a cylinder moved partly along its axis just gets longer or shorter: it stays as it is)
            if (motion.Turn == null && w.Surface is BrepCylinder cy && Math.Abs(cy.Axis.Normalized().Dot(offset.Normalized())) > 1 - AngleTol * AngleTol) continue;
            // (a round face turned round its own axis stays as it is: it just grows or shrinks along the turn)
            if (motion.Turn is { } tw && KeptByTurn(w.Surface, tw)) continue;
            if (w.Surface is not BrepPlane) throw new PushPullException(Lang.T("Een rond vlak kan niet meebuigen; zo kan het (nog) niet.", "A round face cannot bend along; that is not possible (yet)."));
            if (Replane(verts, edges, w, oldNormal[f]) is not { } fold) continue;
            // not flat any more: a four-sided face folded along a diagonal, as SketchUp does
            faces.Add(fold);
            changed.Add(faces.Count - 1);
        }

        // tidying: vertices on each other are one, edges in the middle of others split, same stretches one edge,
        // slivers out, faces in one plane one, straight edges in one line one
        var snap = new Dictionary<int, int>();
        var others = Enumerable.Range(0, verts.Count).Where(v => !movedV.Contains(v)).ToList();
        foreach (int v in movedV)
            foreach (int u in others)
                if ((verts[u] - verts[v]).Length < DistanceTol) { snap[v] = u; break; }
        if (snap.Count > 0)
            for (int i = 0; i < edges.Count; i++)
                if (edges[i] is { } e && (snap.ContainsKey(e.Start) || snap.ContainsKey(e.End)))
                    edges[i] = e with { Start = snap.GetValueOrDefault(e.Start, e.Start), End = snap.GetValueOrDefault(e.End, e.End) };
        var near = movedV.Select(v => snap.GetValueOrDefault(v, v)).Concat(changed.SelectMany(f => faces[f].Loops.SelectMany(l => l))
            .Select(c => edges[c.Edge]).Where(e => e is { Start: >= 0 }).SelectMany(e => new[] { e!.Start, e.End })).Distinct().ToList();
        SplitAtVertices(verts, edges, faces, near);
        Canonicalize(edges, faces);
        foreach (var e in edges)
            if (e is { Curve: BrepLine } && e.Start >= 0 && e.Start != e.End && (verts[e.Start] - verts[e.End]).Length < 1e-9)
                throw new PushPullException(Lang.T("Twee hoekpunten komen op elkaar terecht; dat kan nog niet.", "Two vertices end up on each other; that is not possible yet."));
        RemoveSlivers(faces, edges);
        var mergedInto = new Dictionary<int, int>();
        Work Before(int f) => new(body.Faces[f].Surface, body.Faces[f].Reversed, body.Faces[f].Loops.Select(l => l.ToList()).ToList());
        for (bool again = true; again;)
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
            foreach (var (_, fs) in users)
            {
                if (fs.Count != 2) continue;
                int a = Math.Min(fs[0], fs[1]), b = Math.Max(fs[0], fs[1]);
                if (!changed.Contains(a) && !changed.Contains(b)) continue;
                if (!SamePlane(verts, edges, faces[a], faces[b])) continue;
                // (faces that were in one plane already stay apart: a drawn line or rectangle is meant to be there)
                if (a < oldFaces && b < oldFaces && SamePlane(body.Vertices.ToList(), body.Edges.Select(e => (BrepEdge?)e).ToList(), Before(a), Before(b))) continue;
                if (MergePlanar(verts, edges, faces, a, b) is not { } extra) continue;
                faces[b].Dead = true;
                mergedInto[b] = a;
                changed.Add(a);
                changed.UnionWith(extra);
                again = true;
                break;
            }
            if (!again) again = RemoveSlivers(faces, edges);
        }
        JoinStraight(verts, edges, faces, near);
        if (Environment.GetEnvironmentVariable("PPM_DEBUG") == "2")
            for (int f = 0; f < faces.Count; f++)
                if (!faces[f].Dead && faces[f].Loops.SelectMany(l => l).Any(c => edges[c.Edge] == null))
                    Console.Error.WriteLine($"[move] face {f} (old {f < oldFaces}) uses a removed edge: {string.Join(" | ", faces[f].Loops.Select(l => string.Join(",", l.Select(c => (edges[c.Edge] == null ? "X" : "") + c.Edge))))}, merged {string.Join(",", mergedInto.Select(kv => kv.Key + "->" + kv.Value))}");

        var (result, fMap) = Compact(verts, edges, faces);
        int Final(int f) { while (!fMap.ContainsKey(f) && mergedInto.TryGetValue(f, out int t)) f = t; return fMap.GetValueOrDefault(f, -1); }
        var changedNew = changed.Select(Final).Where(f => f >= 0).Distinct().Order().ToList();
        Check(result, changedNew);
        if (SignedVolume(result) is double vol && vol <= 0)
            throw new PushPullException(Lang.T("Zo keert de vorm binnenstebuiten; dat kan niet.", "That way the shape turns inside out; that is not possible."));
        return new(result, Enumerable.Range(0, oldFaces).Select(Final).ToList(), changedNew);
    }

    /// <summary>Whether the axis through <paramref name="c"/> along <paramref name="dir"/> is the axis of the turn.</summary>
    internal static bool RoundAxis(Vec3 c, Vec3 dir, BrepTransform.Turn t)
    {
        var a = t.Axis.Normalized();
        if (Math.Abs(Math.Abs(dir.Normalized().Dot(a)) - 1) > 1e-9) return false;
        var d = c - t.Centre;
        return (d - a * d.Dot(a)).Length < DistanceTol;
    }

    /// <summary>Whether a surface is the same after the turn: one of revolution round the turn's axis (a sphere: its
    /// centre on it), or a plane square to it.</summary>
    internal static bool KeptByTurn(BrepSurface s, BrepTransform.Turn t) => s switch
    {
        BrepCylinder c => RoundAxis(c.Origin, c.Axis, t),
        BrepCone k => RoundAxis(k.Origin, k.Axis, t),
        BrepTorus o => RoundAxis(o.Centre, o.Axis, t),
        BrepSphere sp => RoundAxis(sp.Centre, t.Axis, t),
        BrepPlane p => Math.Abs(Math.Abs(p.Normal.Normalized().Dot(t.Axis.Normalized())) - 1) < 1e-9,
        _ => false,
    };

    /// <summary>A surface moved over <paramref name="off"/>.</summary>
    private static BrepSurface ShiftedSurface(BrepSurface s, Vec3 off) => s switch
    {
        BrepPlane p => p with { Origin = p.Origin + off },
        BrepCylinder c => c with { Origin = c.Origin + off },
        BrepCone k => k with { Origin = k.Origin + off },
        BrepSphere sp => sp with { Centre = sp.Centre + off },
        BrepTorus t => t with { Centre = t.Centre + off },
        _ => throw new PushPullException(Lang.T("Dit soort vlak kan nog niet verplaatst worden.", "This kind of face cannot be moved yet.")),
    };

    /// <summary>
    /// A planar face given the plane through its corners as they are now (its normal from its outer loop, so the way
    /// it faces follows). Not flat any more: one loop of four straight edges is folded along the diagonal that keeps both
    /// halves facing the old way, and the second half is returned (the first stays in <paramref name="w"/>); otherwise
    /// it is refused. Turning round (facing more than a quarter turn away from before) is refused too.
    /// </summary>
    private static Work? Replane(List<Vec3> verts, List<BrepEdge?> edges, Work w, Vec3 oldNormal)
    {
        var tmp = new BrepBody { Vertices = verts, Edges = edges.Select(x => x ?? new BrepEdge(0, 0, new BrepLine())).ToList(), Faces = [] };
        var loops = w.Loops.Select(l => FaceGeometry.LoopPoints(tmp, l)).ToList();
        var outer = loops.MaxBy(l => Newell(l).Length)!;
        var n = Newell(outer);
        // (squashed flat: it goes when the edges that fall together are tidied; the check at the end sees to the rest)
        if (n.Length < 1e-18 || loops.All(l => Newell(l).Length < 1e-18)) return null;
        n = n.Normalized();
        var c = outer.Aggregate((a, b) => a + b) * (1.0 / outer.Count);
        double off = loops.SelectMany(l => l).Max(p => Math.Abs((p - c).Dot(n)));
        double size = outer.Max(p => (p - c).Length);
        if (off <= Math.Max(DistanceTol / 2, size * 1e-9))
        {
            if (n.Dot(oldNormal) <= 0) throw new PushPullException(Lang.T("Een vlak keert zo om; dat kan niet.", "A face turns over that way; that is not possible."));
            var x = (w.Surface as BrepPlane)!.XAxis; x = (x - n * x.Dot(n));
            w.Surface = new BrepPlane(c, n, x.Length > 1e-9 ? x.Normalized() : (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n).Normalized());
            w.Reversed = false;
            return null;
        }
        // folding: one loop, four straight edges
        if (w.Loops.Count != 1 || w.Loops[0].Count != 4 || w.Loops[0].Any(co => edges[co.Edge] is not { Curve: BrepLine }))
            throw new PushPullException(Lang.T("Een vlak zou zo krom worden; dat kan (nog) niet. (Een vierhoek wordt gevouwen, andere vlakken nog niet.)", "A face would become curved that way; that is not possible (yet). (A quadrilateral is folded, other faces not yet.)"));
        var loop = w.Loops[0];
        int From(BrepCoedge co) => co.Forward ? edges[co.Edge]!.Start : edges[co.Edge]!.End;
        var p = loop.Select(From).ToList();
        foreach (int start in new[] { 0, 1 })
        {
            // the diagonal p[start] - p[start+2]: triangles (start, start+1, start+2) and (start+2, start+3, start)
            int a = p[start], b = p[start + 1], d = p[(start + 2) % 4], e = p[(start + 3) % 4];
            var n1 = (verts[b] - verts[a]).Cross(verts[d] - verts[a]);
            var n2 = (verts[e] - verts[d]).Cross(verts[a] - verts[d]);
            if (n1.Dot(oldNormal) <= 0 || n2.Dot(oldNormal) <= 0) continue;
            int diag = edges.Count; edges.Add(new BrepEdge(a, d, new BrepLine()));
            var first = new List<BrepCoedge> { loop[start], loop[start + 1], new(diag, false) };
            var second = new List<BrepCoedge> { loop[(start + 2) % 4], loop[(start + 3) % 4], new(diag, true) };
            w.Loops = [first];
            w.Surface = new BrepPlane(verts[a], n1.Normalized(), (verts[b] - verts[a]).Normalized()); w.Reversed = false;
            return new Work(new BrepPlane(verts[d], n2.Normalized(), (verts[e] - verts[d]).Normalized()), false, [second]);
        }
        throw new PushPullException(Lang.T("Een vlak zou zo omklappen; dat kan niet.", "A face would flip over that way; that is not possible."));
    }

    private static Vec3 Newell(List<Vec3> l)
    {
        var s = new Vec3(0, 0, 0);
        for (int i = 0; i < l.Count; i++) s += l[i].Cross(l[(i + 1) % l.Count]);
        return s;
    }

    /// <summary>Two planar faces in one plane, facing the same way.</summary>
    private static bool SamePlane(List<Vec3> verts, List<BrepEdge?> edges, Work a, Work b)
    {
        if (a.Surface is not BrepPlane pa || b.Surface is not BrepPlane pb) return false;
        var na = (pa.Normal * (a.Reversed ? -1 : 1)).Normalized(); var nb = (pb.Normal * (b.Reversed ? -1 : 1)).Normalized();
        if (na.Dot(nb) < 1 - AngleTol * AngleTol / 2) return false;
        return b.Loops.SelectMany(l => l).Select(c => edges[c.Edge]).Where(e => e is { Start: >= 0 })
            .All(e => Math.Abs((verts[e!.Start] - pa.Origin).Dot(na)) < DistanceTol);
    }

    /// <summary>An edge a face runs along both ways (a sliver of nothing) goes; a face of only that goes altogether.
    /// Whether anything changed.</summary>
    private static bool RemoveSlivers(List<Work> faces, List<BrepEdge?> edges)
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
}
