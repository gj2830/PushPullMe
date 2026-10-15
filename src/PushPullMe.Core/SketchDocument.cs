using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A face of a document: body and face index.</summary>
public readonly record struct FaceRef(int Body, int Face);

/// <summary>
/// What is being drawn: exact bodies (metres), and for their curved faces the triangles they are shown with (from the
/// mesh they were recognised in, or made when a push/pull adds one). Immutable: every change gives a new document,
/// so undo is a list of documents.
/// </summary>
public sealed class SketchDocument
{
    public required IReadOnlyList<BrepBody> Bodies { get; init; }

    /// <summary>Per body: the triangles of its curved faces (by face index), wound outward, in metres.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>>> Curved { get; init; }

    /// <summary>Per body: the colour painted on its faces (by face index; ARGB). Faces not in it have no colour of their
    /// own (shown in the standard colour).</summary>
    public IReadOnlyList<IReadOnlyDictionary<int, uint>> Colors { get; init; } = [];

    /// <summary>Loose lines and arcs (sketch curves), not part of any body.</summary>
    public IReadOnlyList<SketchLine> Lines { get; init; } = [];

    /// <summary>The same bodies with these loose lines.</summary>
    public SketchDocument WithLines(IReadOnlyList<SketchLine> lines) => new() { Bodies = Bodies, Curved = Curved, Colors = Colors, Lines = lines };

    /// <summary>Loose lines added.</summary>
    public SketchDocument AddLines(IEnumerable<SketchLine> lines) => WithLines(Lines.Concat(lines).ToList());

    /// <summary>Loose lines taken away (each once, as found).</summary>
    public SketchDocument RemoveLines(IEnumerable<SketchLine> lines)
    {
        var left = Lines.ToList();
        foreach (var l in lines) left.Remove(l);
        return WithLines(left);
    }

    /// <summary>The colour painted on a face, or null.</summary>
    public uint? ColorOf(FaceRef f) => f.Body < Colors.Count && Colors[f.Body].TryGetValue(f.Face, out var c) ? c : null;

    /// <summary>The faces painted <paramref name="color"/> (null: the paint taken off).</summary>
    public SketchDocument Paint(IEnumerable<FaceRef> faces, uint? color)
    {
        var colors = Enumerable.Range(0, Bodies.Count).Select(b => b < Colors.Count ? Colors[b].ToDictionary() : new Dictionary<int, uint>()).ToList();
        foreach (var f in faces)
            if (color is { } c) colors[f.Body][f.Face] = c; else colors[f.Body].Remove(f.Face);
        return new SketchDocument { Lines = Lines, Bodies = Bodies, Curved = Curved, Colors = colors };
    }

    /// <summary>The colours with the faces of one body renumbered (old index → new, or -1: gone); a new face drawn on a
    /// painted one (<paramref name="newFace"/> from <paramref name="parent"/>) gets its colour.</summary>
    private IReadOnlyList<IReadOnlyDictionary<int, uint>> Recolor(int body, IReadOnlyList<int>? map, int? newFace = null, int? parent = null)
    {
        var result = new List<IReadOnlyDictionary<int, uint>>();
        for (int b = 0; b < Bodies.Count; b++)
        {
            var old = b < Colors.Count ? Colors[b] : new Dictionary<int, uint>();
            if (b != body) { result.Add(old); continue; }
            var d = new Dictionary<int, uint>();
            foreach (var (k, c) in old)
            {
                int nk = map == null ? k : k < map.Count ? map[k] : -1;
                if (nk >= 0 && !d.ContainsKey(nk)) d[nk] = c;
            }
            if (newFace is int nf && parent is int pf && old.TryGetValue(pf, out var pc)) d[nf] = pc;
            result.Add(d);
        }
        return result;
    }

    /// <summary>A mesh (STL/3MF) recognised as bodies: flat parts become planes, round ones (as the options say) cylinders
    /// and the like.</summary>
    public static SketchDocument FromMesh(StlFile stl, MeshOptions opt)
    {
        var bodies = MeshConverter.ConvertToBrep(stl, opt, out _, out var labels);
        return FromRecognized(bodies, labels);
    }

    /// <summary>Bodies recognised in a mesh (here or in the converter), with which triangle became which face (curved faces
    /// are shown by those triangles where their exact shape does not cover the same area; null: from their shape only).</summary>
    public static SketchDocument FromRecognized(IReadOnlyList<BrepBody> bodies, MeshLabels? labels)
    {
        if (labels == null) return FromBodies(bodies);
        var curved = bodies.Select(_ => new Dictionary<int, List<(Vec3, Vec3, Vec3)>>()).ToList();
        foreach (var t in labels.Triangles)
        {
            if (t.Body < 0 || t.Body >= bodies.Count || bodies[t.Body].Faces[t.Face].Surface is BrepPlane) continue;
            if (!curved[t.Body].TryGetValue(t.Face, out var l)) curved[t.Body][t.Face] = l = new();
            l.Add((labels.Vertices[t.A], labels.Vertices[t.B], labels.Vertices[t.C]));
        }
        // cylinder faces from their exact shape where that covers the same area (they then meet the planar faces
        // seamlessly, and can be cut back later)
        for (int b = 0; b < bodies.Count; b++)
        {
            foreach (var (f, mesh) in curved[b].ToList())
                if (FaceGeometry.CylinderTriangles(bodies[b], bodies[b].Faces[f]) is { } exact && Math.Abs(Area(exact) - Area(mesh)) < 0.02 * Area(mesh))
                    curved[b][f] = exact;
            // curved faces no triangle of the mesh became (bodies added later, such as threads): from their shape
            for (int f = 0; f < bodies[b].Faces.Count; f++)
                if (bodies[b].Faces[f].Surface is not BrepPlane && !curved[b].ContainsKey(f) && FaceGeometry.CurvedTriangles(bodies[b], bodies[b].Faces[f]) is { } made)
                    curved[b][f] = made;
        }
        return new SketchDocument
        {
            Bodies = bodies,
            Curved = curved.Select(d => (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)d.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value)).ToList(),
        };
    }

    /// <summary>A DesignSpark file (.rsdocx), as saved here or made in DesignSpark: its solid bodies, curved faces shown
    /// by triangles made from their exact shape. Throws <see cref="RsDocxReadException"/> when it cannot be read.</summary>
    public static SketchDocument FromRsDocx(string path) => FromRsDocx(path, out _);

    /// <summary>As <see cref="FromRsDocx(string)"/>; <paramref name="hidden"/>: how many bodies are hidden in DesignSpark
    /// (they are shown here all the same).</summary>
    public static SketchDocument FromRsDocx(string path, out int hidden)
    {
        var bodies = RsDocxReader.Read(path, out hidden, out var colors);
        // (a flat face's outer loop first, as the rest expects; a file need not have it so: boltest.rsdocx)
        var doc = FromBodies(bodies.Select(b => new BrepBody { Vertices = b.Vertices, Edges = b.Edges, Faces = Core.PushPull.OrderLoops(b) }).ToList());
        return new SketchDocument { Bodies = doc.Bodies, Curved = doc.Curved, Colors = colors, Lines = RsDocxSketch.Read(RsDocxPackage.Open(path)) };
    }

    /// <summary>Bodies made here (planes only, or with curved faces shown by their own triangles).</summary>
    public static SketchDocument FromBodies(IReadOnlyList<BrepBody> bodies) => new()
    {
        Bodies = bodies,
        Curved = bodies.Select(b => (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)Enumerable.Range(0, b.Faces.Count)
            .Select(f => (f, t: b.Faces[f].Surface is BrepPlane ? null : FaceGeometry.CurvedTriangles(b, b.Faces[f]))).Where(x => x.t != null)
            .ToDictionary(x => x.f, x => (IReadOnlyList<(Vec3, Vec3, Vec3)>)x.t!)).ToList(),
    };

    private static double Area(IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> tris) => tris.Sum(t => (t.B - t.A).Cross(t.C - t.A).Length) / 2;

    /// <summary>The triangles of one face, wound outward (metres).</summary>
    public IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> TrianglesOf(FaceRef f)
    {
        var face = Bodies[f.Body].Faces[f.Face];
        if (face.Surface is BrepPlane) return FaceGeometry.PlanarTriangles(Bodies[f.Body], face);
        return Curved[f.Body].TryGetValue(f.Face, out var t) ? t : [];
    }

    /// <summary>All faces with their triangles.</summary>
    public IEnumerable<(FaceRef Face, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> Triangles)> AllTriangles()
    {
        for (int b = 0; b < Bodies.Count; b++)
            for (int f = 0; f < Bodies[b].Faces.Count; f++)
                yield return (new FaceRef(b, f), TrianglesOf(new FaceRef(b, f)));
    }

    /// <summary>
    /// The face pushed or pulled over <paramref name="distance"/> (metres, along its outward normal); faces get new numbers
    /// (<see cref="PushPullResult.FaceMap"/>). Throws
    /// <see cref="PushPullException"/> (the document stays as it is) when that cannot be done, also when the face would
    /// run into another part of the model (unless <paramref name="checkCrossing"/> is false: a quick preview while
    /// dragging).
    /// </summary>
    public (SketchDocument Doc, PushPullResult Result) PushPull(FaceRef f, double distance, bool checkCrossing = true)
    {
        var body = Bodies[f.Body];
        if (body.Faces[f.Face].Surface is BrepCylinder || (body.Faces[f.Face].Surface is BrepSphere or BrepTorus && Core.PushPull.IsRoundingSet(body, f.Face)))
            return PushPullCylinder(f, distance, checkCrossing);
        var r = Core.PushPull.Apply(body, f.Face, distance);
        var n = FaceGeometry.Normal(body.Faces[f.Face]);
        var origin = ((BrepPlane)body.Faces[f.Face].Surface).Origin;
        // the triangles of curved faces under their new numbers (two that became one: together)
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (var (k, t) in Curved[f.Body])
        {
            int nk = k < r.FaceMap.Count ? r.FaceMap[k] : -1;
            if (nk < 0) continue;
            curved[nk] = curved.TryGetValue(nk, out var had) ? had.Concat(t).ToList() : t;
        }
        // a curved face a side face joined: its triangles follow the face (stretched to it, or cut back)
        foreach (int g in r.GrownFaces)
        {
            if (!curved.TryGetValue(g, out var tris)) continue;
            Vec3 Move(Vec3 p)
            {
                double s = (p - origin).Dot(n);
                if (distance > 0) return Math.Abs(s) < 1e-5 ? p + n * distance : p;
                return s > distance ? p + n * (distance - s) : p;
            }
            curved[g] = tris.Select(t => (Move(t.Item1), Move(t.Item2), Move(t.Item3))).ToList();
        }
        foreach (var (k, t) in r.NewCurvedTriangles) curved[k] = t;
        // a rounding that moved along: its triangles too (unless they are made anew below)
        foreach (int g in r.MovedAlong)
            if (curved.TryGetValue(g, out var mt)) curved[g] = mt.Select(t => (t.Item1 + n * distance, t.Item2 + n * distance, t.Item3 + n * distance)).ToList();
        var changedFaces = new[] { r.FaceMap[f.Face] }.Where(x => x >= 0).Concat(r.NewFaces).Concat(r.GrownFaces).Concat(r.TrimmedFaces).Concat(r.MovedAlong).Distinct().ToList();
        // changed cylinder faces from their exact shape (a rounding cut back has no other triangles)
        foreach (int g in changedFaces)
            if (r.Body.Faces[g].Surface is not BrepPlane && FaceGeometry.CurvedTriangles(r.Body, r.Body.Faces[g]) is { } exact) curved[g] = exact;
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == f.Body ? r.Body : b).ToList(),
            Curved = Curved.Select((c, i) => i == f.Body ? curved : c).ToList(),
            Colors = Recolor(f.Body, r.FaceMap),
        };
        var changed = changedFaces.Select(x => new FaceRef(f.Body, x)).ToHashSet();
        if (checkCrossing && Intersections.FirstCrossing(doc, changed) is { } hit)
            throw new PushPullException(r.MovedAlong.Contains(hit.A.Face) || r.MovedAlong.Contains(hit.B.Face)
                ? "Zo komt de ronding die meeschuift door een ander deel heen (bijvoorbeeld de ronding aan de andere kant: dan zou de straal kleiner moeten); dat kan nog niet."
                : "Zo komt het vlak door een ander deel van het model heen; dat kan nog niet.");
        if (checkCrossing && r.FaceMap[f.Face] is >= 0 and var mf && doc.AgainstOpposite(new FaceRef(f.Body, mf)))
            throw new PushPullException("Zo komt het vlak precies tegen de andere kant aan: doorduwen tot een gat kan nog niet.");
        return (doc, r);
    }

    /// <summary>
    /// A face on a cylinder pushed or pulled: its radius changes (see <see cref="Core.PushPull.Cylinder"/>); the face
    /// numbers stay the same.
    /// </summary>
    private (SketchDocument Doc, PushPullResult Result) PushPullCylinder(FaceRef f, double distance, bool checkCrossing)
    {
        var body = Bodies[f.Body];
        PushPullResult r;
        try
        {
            if (body.Faces[f.Face].Surface is not BrepCylinder) throw new PushPullException("");
            r = Core.PushPull.Apply(body, f.Face, distance);
        }
        catch (PushPullException) when (Core.PushPull.IsRoundingSet(body, f.Face))
        {
            // a rounding that ends in a ball corner or a mitre: taken away and made again with the new radius
            return ChangedRounding(f, Core.PushPull.ChangeRounding(body, f.Face, distance), checkCrossing);
        }
        var cyl = (BrepCylinder)body.Faces[f.Face].Surface;
        var curved = Curved[f.Body].ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (int g in r.TrimmedFaces.Where(g => r.Body.Faces[g].Surface is BrepCylinder))
        {
            if (r.NewCurvedTriangles.TryGetValue(g, out var exact)) { curved[g] = exact; continue; }
            if (!curved.TryGetValue(g, out var old)) continue;
            // (no exact triangles for it: the old ones, moved out from the axis to the new radius)
            var axis = cyl.Axis.Normalized();
            double nr = ((BrepCylinder)r.Body.Faces[g].Surface).Radius;
            Vec3 Out(Vec3 p) { var foot = cyl.Origin + axis * (p - cyl.Origin).Dot(axis); return foot + (p - foot).Normalized() * nr; }
            curved[g] = old.Select(t => (Out(t.A), Out(t.B), Out(t.C))).ToList();
        }
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == f.Body ? r.Body : b).ToList(),
            Curved = Curved.Select((c, i) => i == f.Body ? (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)curved : c).ToList(),
            Colors = Colors,
        };
        if (checkCrossing && Intersections.FirstNewCrossing(this, doc, r.TrimmedFaces.Select(x => new FaceRef(f.Body, x)).ToHashSet()) is not null)
            throw new PushPullException("Zo komt de cilinder door een ander deel van het model heen; dat kan nog niet.");
        return (doc, r);
    }

    /// <summary>The document after <see cref="Core.PushPull.ChangeRounding"/>: triangles and colours under the new face
    /// numbers.</summary>
    private (SketchDocument Doc, PushPullResult Result) ChangedRounding(FaceRef f, PushPullResult r, bool checkCrossing)
    {
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (var (k, t) in Curved[f.Body])
            if (k < r.FaceMap.Count && r.FaceMap[k] >= 0 && !r.NewCurvedTriangles.ContainsKey(r.FaceMap[k])) curved[r.FaceMap[k]] = t;
        foreach (var (k, t) in r.NewCurvedTriangles) curved[k] = t;
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == f.Body ? r.Body : b).ToList(),
            Curved = Curved.Select((c, i) => i == f.Body ? (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)curved : c).ToList(),
            Colors = Recolor(f.Body, r.FaceMap),
        };
        if (checkCrossing && Intersections.FirstCrossing(doc, r.TrimmedFaces.Select(x => new FaceRef(f.Body, x)).ToHashSet()) is not null)
            throw new PushPullException("Zo komt de ronding door een ander deel van het model heen; dat kan nog niet.");
        return (doc, r);
    }

    /// <summary>Whether a planar face lies against a face of the same body that faces the other way, in the same plane
    /// and overlapping (a wall of no thickness).</summary>
    private bool AgainstOpposite(FaceRef f)
    {
        var body = Bodies[f.Body];
        if (body.Faces[f.Face].Surface is not BrepPlane pf) return false;
        var n = FaceGeometry.Normal(body.Faces[f.Face]);
        var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0).Cross(n).Normalized() : new Vec3(0, 1, 0).Cross(n).Normalized();
        var y = n.Cross(x);
        var mine = TrianglesOf(f);
        static bool In((Vec3 A, Vec3 B, Vec3 C) t, Vec3 p, Vec3 x, Vec3 y)
        {
            double S(Vec3 a, Vec3 b) => (b.Dot(x) - a.Dot(x)) * (p.Dot(y) - a.Dot(y)) - (b.Dot(y) - a.Dot(y)) * (p.Dot(x) - a.Dot(x));
            double s1 = S(t.A, t.B), s2 = S(t.B, t.C), s3 = S(t.C, t.A);
            return (s1 > 0 && s2 > 0 && s3 > 0) || (s1 < 0 && s2 < 0 && s3 < 0);
        }
        static Vec3 Mid((Vec3 A, Vec3 B, Vec3 C) t) => (t.A + t.B + t.C) * (1.0 / 3);
        for (int g = 0; g < body.Faces.Count; g++)
        {
            if (g == f.Face || body.Faces[g].Surface is not BrepPlane pg) continue;
            if (FaceGeometry.Normal(body.Faces[g]).Dot(n) > -1 + 1e-6 || Math.Abs((pg.Origin - pf.Origin).Dot(n)) > PushPullMe.Core.PushPull.DistanceTol) continue;
            var theirs = TrianglesOf(new FaceRef(f.Body, g));
            if (mine.Any(t => theirs.Any(u => In(u, Mid(t), x, y))) || theirs.Any(t => mine.Any(u => In(u, Mid(t), x, y)))) return true;
        }
        return false;
    }

    /// <summary>A rectangle drawn on a planar face (see <see cref="Drawing.Rectangle"/>); the face it became.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawRectangle(FaceRef f, Vec3 a, Vec3 c, Vec3 u) =>
        With(f.Body, Drawing.Rectangle(Bodies[f.Body], f.Face, a, c, u), f.Face);

    /// <summary>
    /// Move (see <see cref="Core.PushPull.Move"/>) in body <paramref name="body"/>: faces get new numbers
    /// (<see cref="MoveResult.FaceMap"/>). Throws <see cref="PushPullException"/> (the document stays as it is) when that
    /// cannot be done, also when a changed face would run into another part (unless <paramref name="checkCrossing"/> is
    /// false: a quick preview while dragging).
    /// </summary>
    public (SketchDocument Doc, MoveResult Result) Move(int body, MoveSelection pick, Vec3 offset, bool checkCrossing = true)
    {
        if (pick.Whole != MoveWhole.None) return MoveWholeBody(body, pick.Whole == MoveWhole.Copy, offset);
        return Move(body, pick, new Motion(offset), checkCrossing);
    }

    /// <summary>A face tilted round an axis (one of its edges): see <see cref="Core.PushPull.Move(BrepBody, MoveSelection, Motion)"/>.</summary>
    public (SketchDocument Doc, MoveResult Result) Tilt(FaceRef f, Vec3 centre, Vec3 axis, double angle, bool checkCrossing = true) =>
        Move(f.Body, MoveSelection.Face(f.Face), new Motion(default, new BrepTransform.Turn(centre, axis, angle)), checkCrossing);

    /// <summary>As <see cref="Move(int, MoveSelection, Vec3, bool)"/>, with any <see cref="Motion"/>.</summary>
    public (SketchDocument Doc, MoveResult Result) Move(int body, MoveSelection pick, Motion motion, bool checkCrossing = true)
    {
        var r = Core.PushPull.Move(Bodies[body], pick, motion);
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (var (k, t) in Curved[body])
            if (k < r.FaceMap.Count && r.FaceMap[k] is >= 0 and var nk && !curved.ContainsKey(nk))
                // (a curved face that moved along as a whole: its triangles too)
                curved[nk] = r.Changed.Contains(nk) ? t.Select(x => (motion.Point(x.Item1), motion.Point(x.Item2), motion.Point(x.Item3))).ToList() : t;
        foreach (int g in r.Changed)
            if (r.Body.Faces[g].Surface is not BrepPlane && FaceGeometry.CurvedTriangles(r.Body, r.Body.Faces[g]) is { } exact) curved[g] = exact;
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == body ? r.Body : b).ToList(),
            Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(),
            Colors = Recolor(body, r.FaceMap),
        };
        if (checkCrossing && Intersections.FirstCrossing(doc, r.Changed.Select(x => new FaceRef(body, x)).ToHashSet()) is not null)
            throw new PushPullException("Zo komt een vlak door een ander deel van het model heen; dat kan nog niet.");
        return (doc, r);
    }

    /// <summary>SketchUp's Offset on a planar face (see <see cref="Drawing.Offset"/>); the inner face.</summary>
    public (SketchDocument Doc, FaceRef Face) Offset(FaceRef f, double distance) =>
        With(f.Body, Drawing.Offset(Bodies[f.Body], f.Face, distance), f.Face);

    /// <summary>
    /// A whole body moved, or (<paramref name="copy"/>) a moved copy of it added as a new body (last), with its colours.
    /// Bodies may overlap (as in DesignSpark). The result's body is the moved one (the copy).
    /// </summary>
    private (SketchDocument Doc, MoveResult Result) MoveWholeBody(int body, bool copy, Vec3 offset)
    {
        var moved = BrepTransform.Translated(Bodies[body], offset);
        var curved = (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)Curved[body].ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value.Select(t => (t.Item1 + offset, t.Item2 + offset, t.Item3 + offset)).ToList());
        var doc = copy
            ? new SketchDocument { Lines = Lines, Bodies = Bodies.Append(moved).ToList(), Curved = Curved.Append(curved).ToList(),
                Colors = Enumerable.Range(0, Bodies.Count + 1).Select(i => Colors.ElementAtOrDefault(i == Bodies.Count ? body : i) ?? new Dictionary<int, uint>()).ToList() }
            : new SketchDocument { Lines = Lines, Bodies = Bodies.Select((b, i) => i == body ? moved : b).ToList(), Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(), Colors = Colors };
        return (doc, new MoveResult(moved, Enumerable.Range(0, moved.Faces.Count).ToList(), Enumerable.Range(0, moved.Faces.Count).ToList()));
    }

    /// <summary>
    /// A sharp edge rounded (see <see cref="Core.PushPull.RoundEdge"/>); the new rounding. It takes the colour of the faces
    /// on either side when they have the same one.
    /// </summary>
    public (SketchDocument Doc, FaceRef Face) RoundEdge(int body, int edge, double radius)
    {
        var (doc, faces) = RoundEdges(body, [edge], radius);
        return (doc, faces[0]);
    }

    /// <summary>
    /// Sharp edges rounded together (see <see cref="Core.PushPull.RoundEdges"/>: with ball corners and mitres where they
    /// meet); the new faces. Each takes the colour of the flat faces around it when they all have the same one.
    /// </summary>
    public (SketchDocument Doc, List<FaceRef> Faces) RoundEdges(int body, IReadOnlyCollection<int> edges, double radius)
    {
        // (roundings already at their ends are made again together with them: Core.PushPull.RoundEdgesJoining)
        var (b, faces, map) = Core.PushPull.RoundEdgesJoining(Bodies[body], edges.Distinct().Select(e => (e, radius)).ToList());
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (var (k, t) in Curved[body])
            if (k < map.Length && map[k] >= 0) curved[map[k]] = t;
        foreach (int f in faces)
            if (FaceGeometry.CurvedTriangles(b, b.Faces[f]) is { } t) curved[f] = t;
        var colors = Recolor(body, map).Select(c => (IReadOnlyDictionary<int, uint>)c.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
        var mine = new Dictionary<int, uint>(colors[body]);
        foreach (int f in faces)
        {
            var edgesOf = b.Faces[f].Loops.SelectMany(l => l).Select(c => c.Edge).ToHashSet();
            var near = Enumerable.Range(0, b.Faces.Count).Where(g => !faces.Contains(g) && b.Faces[g].Surface is BrepPlane && b.Faces[g].Loops.Any(l => l.Any(c => edgesOf.Contains(c.Edge))))
                .Select(g => mine.TryGetValue(g, out var cg) ? cg : (uint?)null).Distinct().ToList();
            if (near.Count == 1 && near[0] is uint col) mine[f] = col;
        }
        colors[body] = mine;
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((x, i) => i == body ? b : x).ToList(),
            Curved = Curved.Select((x, i) => i == body ? (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)curved : x).ToList(),
            Colors = colors,
        };
        return (doc, faces.Select(f => new FaceRef(body, f)).ToList());
    }

    /// <summary>
    /// A whole body turned round an axis (as SketchUp's Rotate), or (<paramref name="copy"/>) a turned copy of it added as
    /// a new body (last), with its colours. Bodies may overlap. The body that was turned (the copy) is returned too.
    /// </summary>
    public (SketchDocument Doc, int Body) RotateBody(int body, Vec3 centre, Vec3 axis, double angle, bool copy)
    {
        var t = new BrepTransform.Turn(centre, axis, angle);
        var turned = BrepTransform.Rotated(Bodies[body], t);
        var curved = (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)Curved[body].ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value.Select(x => (t.Point(x.Item1), t.Point(x.Item2), t.Point(x.Item3))).ToList());
        if (copy)
            return (new SketchDocument
            {
                Lines = Lines,
                Bodies = Bodies.Append(turned).ToList(),
                Curved = Curved.Append(curved).ToList(),
                Colors = Enumerable.Range(0, Bodies.Count + 1).Select(i => Colors.ElementAtOrDefault(i == Bodies.Count ? body : i) ?? new Dictionary<int, uint>()).ToList(),
            }, Bodies.Count);
        return (new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == body ? turned : b).ToList(),
            Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(),
            Colors = Colors,
        }, body);
    }

    /// <summary>
    /// A whole body made larger or smaller from a fixed point (as SketchUp's Scale; see <see cref="BrepTransform.Scaled"/>),
    /// or (<paramref name="copy"/>) a scaled copy added as a new body (last), with its colours. Its curved faces get new
    /// triangles. Returns the body that was scaled (the copy).
    /// </summary>
    public (SketchDocument Doc, int Body) ScaleBody(int body, Vec3 fixedPoint, double factor, Vec3? along, bool copy)
    {
        var scaled = BrepTransform.Scaled(Bodies[body], fixedPoint, factor, along);
        var curved = (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)Enumerable.Range(0, scaled.Faces.Count)
            .Where(f => scaled.Faces[f].Surface is not BrepPlane)
            .Select(f => (f, t: FaceGeometry.CurvedTriangles(scaled, scaled.Faces[f])
                ?? Curved[body].GetValueOrDefault(f)?.Select(x => (fixedPoint + (x.Item1 - fixedPoint) * factor, fixedPoint + (x.Item2 - fixedPoint) * factor, fixedPoint + (x.Item3 - fixedPoint) * factor)).ToList()))
            .Where(x => x.t != null).ToDictionary(x => x.f, x => (IReadOnlyList<(Vec3, Vec3, Vec3)>)x.t!);
        if (copy)
            return (new SketchDocument
            {
                Lines = Lines,
                Bodies = Bodies.Append(scaled).ToList(),
                Curved = Curved.Append(curved).ToList(),
                Colors = Enumerable.Range(0, Bodies.Count + 1).Select(i => Colors.ElementAtOrDefault(i == Bodies.Count ? body : i) ?? new Dictionary<int, uint>()).ToList(),
            }, Bodies.Count);
        return (new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == body ? scaled : b).ToList(),
            Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(),
            Colors = Colors,
        }, body);
    }

    /// <summary>
    /// A whole body mirrored in a plane (see <see cref="BrepTransform.Mirrored"/>), or (<paramref name="copy"/>) a mirrored
    /// copy added as a new body (last), with its colours: two halves of a symmetric part. Returns the mirrored body.
    /// </summary>
    public (SketchDocument Doc, int Body) MirrorBody(int body, Vec3 origin, Vec3 normal, bool copy)
    {
        var mirrored = BrepTransform.Mirrored(Bodies[body], origin, normal);
        var curved = (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)Enumerable.Range(0, mirrored.Faces.Count)
            .Where(f => mirrored.Faces[f].Surface is not BrepPlane)
            .Select(f => (f, t: FaceGeometry.CurvedTriangles(mirrored, mirrored.Faces[f])))
            .Where(x => x.t != null).ToDictionary(x => x.f, x => (IReadOnlyList<(Vec3, Vec3, Vec3)>)x.t!);
        if (copy)
            return (new SketchDocument
            {
                Lines = Lines,
                Bodies = Bodies.Append(mirrored).ToList(),
                Curved = Curved.Append(curved).ToList(),
                Colors = Enumerable.Range(0, Bodies.Count + 1).Select(i => Colors.ElementAtOrDefault(i == Bodies.Count ? body : i) ?? new Dictionary<int, uint>()).ToList(),
            }, Bodies.Count);
        return (new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((b, i) => i == body ? mirrored : b).ToList(),
            Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(),
            Colors = Colors,
        }, body);
    }

    /// <summary>A circle drawn on a planar face (see <see cref="Drawing.Circle"/>); the face it became.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawCircle(FaceRef f, Vec3 centre, double radius) =>
        With(f.Body, Drawing.Circle(Bodies[f.Body], f.Face, centre, radius), f.Face);

    /// <summary>
    /// The eraser (see <see cref="Core.PushPull.EraseEdge"/>): the edge goes and the faces on either side become one.
    /// Curved faces keep their triangles under their new numbers.
    /// </summary>
    public SketchDocument Erase(int body, int edge)
    {
        var (b, map) = Core.PushPull.EraseEdge(Bodies[body], edge);
        var curved = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
        foreach (var (k, t) in Curved[body])
            if (k < map.Count && map[k] >= 0) curved[map[k]] = t;
        return new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((x, i) => i == body ? b : x).ToList(),
            Curved = Curved.Select((c, i) => i == body ? curved : c).ToList(),
            Colors = Recolor(body, map),
        };
    }

    /// <summary>Lines drawn one after the other on a planar face (see <see cref="Drawing.Path"/>): closed a polygon, from
    /// edge to edge dividing the face; the new face.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawPath(FaceRef f, IReadOnlyList<Vec3> pts)
    {
        // across every face in that plane it crosses (Drawing.Runs), one after the other; the last new face
        var runs = Drawing.Runs(Bodies[f.Body], f.Face, pts);
        if (runs.Count == 0) throw new PushPullException("Die lijn ligt helemaal op randen die er al zijn.");
        var doc = this; var last = f;
        foreach (var run in runs)
        {
            var b = doc.Bodies[f.Body];
            int face = Drawing.FaceOfRun(b, f.Face, run) ?? throw new PushPullException("De lijn loopt buiten het vlak.");
            (doc, last) = doc.With(f.Body, Drawing.Path(b, face, run), face);
        }
        return (doc, last);
    }

    /// <summary>
    /// Loose lines in the plane of <paramref name="f"/> that close a shape, with each other or with the edges of the faces
    /// there, made into edges (see <see cref="LooseLines"/>), as long as there are such; the faces made (the last one is
    /// the newest).
    /// </summary>
    public (SketchDocument Doc, List<FaceRef> Faces) CloseLooseLines(FaceRef f)
    {
        var doc = this; var made = new List<FaceRef>();
        var face = Bodies[f.Body].Faces[f.Face];
        if (face.Surface is not BrepPlane pl || Lines.Count == 0) return (doc, made);
        var n = FaceGeometry.Normal(face); var o = pl.Origin;
        for (int guard = 0; guard < 200; guard++)
        {
            var body = doc.Bodies[f.Body];
            var plane = Drawing.CoplanarFaces(body, f.Face);
            // (cut where they cross the edges of the faces in that plane, too)
            var edges = plane.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge]).Distinct()
                .Where(e => e.Start >= 0 && e.Curve is BrepLine or BrepCircle)
                .Select(e => new LoosePiece(body.Vertices[e.Start], body.Vertices[e.End], e.Curve as BrepCircle)).ToList();
            var pieces = LooseLines.Pieces(doc.Lines, n, o, edges);
            if (pieces.Count == 0) break;
            bool OnEdge(Vec3 q) => Drawing.NearestOnEdges(body, plane, q, 0) is { } e && (e.Point - q).Length < 1e-6;
            bool done = false;
            foreach (var run in LooseLines.Candidates(pieces, OnEdge, q => Drawing.OnHoleEdge(body, plane, q)))
            {
                try
                {
                    // (the lines cut into their pieces; the ones used become edges)
                    var rest = doc.Lines.Where(l => !LooseLines.InPlane(l, n, o)).Concat(pieces.Where((_, i) => !run.Contains(i)).Select(p => p.ToLine())).ToList();
                    var (pts, curves) = LooseLines.Path(pieces, run);
                    var (d, nf) = doc.WithLines(rest).DrawPath(f, pts, curves);
                    doc = d; made.Add(nf); done = true;
                    break;
                }
                catch (PushPullException) { }
            }
            if (!done) break;
        }
        return (doc, made);
    }

    /// <summary>A path of lines and arcs drawn on a planar face (see <see cref="Drawing.Path"/>; an arc a circle running
    /// counter-clockwise round its normal); the new face.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawPath(FaceRef f, IReadOnlyList<Vec3> pts, IReadOnlyList<BrepCurve>? curves)
    {
        if (curves == null || curves.All(c => c is BrepLine)) return DrawPath(f, pts);
        // (with arcs: on the face in that plane its first piece runs over)
        var b = Bodies[f.Body];
        var mid = curves[0] is BrepCircle c0 ? Drawing.ArcPoints(c0, pts[0], pts[1], 2)[1] : (pts[0] + pts[1]) * 0.5;
        int face = Drawing.CoplanarFaces(b, f.Face).FirstOrDefault(g => Drawing.Contains(b, g, mid), -1);
        if (face < 0) throw new PushPullException("De vorm loopt buiten het vlak.");
        return With(f.Body, Drawing.Path(b, face, pts, curves), face);
    }

    /// <summary>
    /// A planar face turned out of its body round an axis in its plane (see <see cref="Core.Revolve"/>; a positive angle
    /// moves it out along its normal), unless that makes the body cross itself; the end cap (the face's number).
    /// </summary>
    public (SketchDocument Doc, FaceRef Cap) Revolve(FaceRef f, Vec3 axisPoint, Vec3 axisDir, double angle, bool checkCrossing = true)
    {
        var a = Core.Revolve.OutwardAxis(Bodies[f.Body], f.Face, axisPoint, axisDir);
        var r = Core.Revolve.Face(Bodies[f.Body], f.Face, axisPoint, a, angle);
        var curved = Curved[f.Body].Where(kv => kv.Key != f.Face).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (int g in r.Sides)
            if (FaceGeometry.CurvedTriangles(r.Body, r.Body.Faces[g]) is { } t) curved[g] = t;
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Select((x, i) => i == f.Body ? r.Body : x).ToList(),
            Curved = Curved.Select((x, i) => i == f.Body ? (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)curved : x).ToList(),
            Colors = Colors,
        };
        if (checkCrossing && (Intersections.FirstCrossing(doc, r.Sides.Append(f.Face).Select(g => new FaceRef(f.Body, g)).ToHashSet()) is not null
            || SweepsInto(f, axisPoint, a, angle)))
            throw new PushPullException("Zo ver gedraaid snijdt het vlak door het lichaam heen.");
        // a new flat face in the same plane as the one next to it (a side sweeping along a side of the body, the cap
        // landing on its bottom): one face, as DesignSpark makes it
        var capPoint = new BrepTransform.Turn(axisPoint, a, angle).Point(TrianglesOf(f).Select(t => (t.A + t.B + t.C) * (1.0 / 3)).First());
        var fresh = r.Sides.Append(f.Face).Where(g => r.Body.Faces[g].Surface is BrepPlane)
            .Select(g => (nn: FaceGeometry.Normal(r.Body.Faces[g]), Origin: ((BrepPlane)r.Body.Faces[g].Surface).Origin)).ToList();
        bool Fresh(BrepFace face) => face.Surface is BrepPlane pl && fresh.Any(x => FaceGeometry.Normal(face).Dot(x.nn) > 1 - 1e-9 && Math.Abs((pl.Origin - x.Origin).Dot(x.nn)) < 1e-9);
        doc = doc.MergeFlat(f.Body, Fresh);
        // the cap: the face (now) round a point of it
        var nb = doc.Bodies[f.Body];
        int capFace = Enumerable.Range(0, nb.Faces.Count).FirstOrDefault(g => nb.Faces[g].Surface is BrepPlane pl && Math.Abs((capPoint - pl.Origin).Dot(FaceGeometry.Normal(nb.Faces[g]))) < 1e-7 && Drawing.Contains(nb, g, capPoint), f.Face);
        return (doc, new FaceRef(f.Body, capFace));
    }

    /// <summary>Flat faces of a body lying in one plane, facing the same way, next to each other (at least one of them as
    /// <paramref name="which"/> says, if given): made one face (by erasing the edges between them).</summary>
    private SketchDocument MergeFlat(int body, Func<BrepFace, bool>? which = null)
    {
        var doc = this;
        for (int guard = 0; guard < 200; guard++)
        {
            var b = doc.Bodies[body];
            var faceOf = new Dictionary<int, List<int>>();
            for (int g = 0; g < b.Faces.Count; g++)
                foreach (var c in b.Faces[g].Loops.SelectMany(l => l)) { if (!faceOf.TryGetValue(c.Edge, out var lst)) faceOf[c.Edge] = lst = []; lst.Add(g); }
            int edge = faceOf.Where(kv => kv.Value.Count == 2 && kv.Value[0] != kv.Value[1]).Select(kv => (kv.Key, F0: b.Faces[kv.Value[0]], F1: b.Faces[kv.Value[1]]))
                .Where(x => x.F0.Surface is BrepPlane p0 && x.F1.Surface is BrepPlane p1 && FaceGeometry.Normal(x.F0).Dot(FaceGeometry.Normal(x.F1)) > 1 - 1e-9
                    && Math.Abs((p1.Origin - p0.Origin).Dot(FaceGeometry.Normal(x.F0))) < 1e-9 && (which == null || which(x.F0) || which(x.F1)))
                .Select(x => x.Key).DefaultIfEmpty(-1).First();
            if (edge < 0) break;
            try { doc = doc.Erase(body, edge); } catch (PushPullException) { break; }
        }
        return doc;
    }

    /// <summary>
    /// Two bodies made one (as Union; see <see cref="Booleans.Union"/>): the result in the place of the first, the
    /// second gone; faces keep the colour of the face they came from, flat faces in one plane next to each other merge.
    /// </summary>
    public SketchDocument Union(int a, int b)
    {
        if (a == b) throw new PushPullException("Kies twee verschillende delen.");
        var (body, from) = Booleans.Union(Bodies[a], Bodies[b]);
        var colors = new Dictionary<int, uint>();
        for (int f = 0; f < from.Count; f++)
            if (ColorOf(new FaceRef(from[f].Body == 0 ? a : b, from[f].Face)) is uint c) colors[f] = c;
        var keep = Enumerable.Range(0, Bodies.Count).Where(i => i != b).ToList();
        var doc = new SketchDocument
        {
            Lines = Lines,
            Bodies = keep.Select(i => i == a ? body : Bodies[i]).ToList(),
            Curved = keep.Select(i => i == a ? (IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>)new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>() : Curved[i]).ToList(),
            Colors = keep.Select(i => i == a ? (IReadOnlyDictionary<int, uint>)colors : i < Colors.Count ? Colors[i] : new Dictionary<int, uint>()).ToList(),
        };
        return doc.MergeFlat(keep.IndexOf(a));
    }

    /// <summary>Whether points of the face, turned along their way round the axis, come inside the body (a sweep that
    /// lies wholly inside it, along faces it shares a plane with, crosses no face).</summary>
    private bool SweepsInto(FaceRef f, Vec3 p, Vec3 a, double angle)
    {
        var tris = Enumerable.Range(0, Bodies[f.Body].Faces.Count).SelectMany(g => TrianglesOf(new FaceRef(f.Body, g))).ToList();
        var n = FaceGeometry.Normal(Bodies[f.Body].Faces[f.Face]);
        // (points just inside the face: the centres of its triangles)
        var samples = TrianglesOf(f).Select(t => (t.A + t.B + t.C) * (1.0 / 3)).Take(64).ToList();
        for (int k = 1; k <= 24; k++)
        {
            var t = new BrepTransform.Turn(p, a, angle * k / 24);
            foreach (var q in samples)
                if (Inside(tris, t.Point(q))) return true;
        }
        return false;
    }

    /// <summary>Whether a point lies inside the closed triangles (an odd number of them crossed by a ray from it).</summary>
    private static bool Inside(List<(Vec3 A, Vec3 B, Vec3 C)> tris, Vec3 q)
    {
        // (a slanting direction, so as not to run along edges)
        var d = new Vec3(0.5773, 0.6071, 0.5461).Normalized();
        int hits = 0;
        foreach (var (A, B, C) in tris)
        {
            var e1 = B - A; var e2 = C - A; var h = d.Cross(e2); double det = e1.Dot(h);
            if (Math.Abs(det) < 1e-20) continue;
            var s = q - A; double u = s.Dot(h) / det; if (u < 0 || u > 1) continue;
            var qq = s.Cross(e1); double v = d.Dot(qq) / det; if (v < 0 || u + v > 1) continue;
            if (e2.Dot(qq) / det > 1e-9) hits++;
        }
        return hits % 2 == 1;
    }

    /// <summary>An arc drawn on a planar face (see <see cref="Drawing.Arc"/>); the new face.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawArc(FaceRef f, Vec3 a, Vec3 b, Vec3 through) =>
        With(f.Body, Drawing.Arc(Bodies[f.Body], f.Face, a, b, through), f.Face);

    /// <summary>A pie drawn on a planar face (see <see cref="Drawing.Pie"/>); the new face.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawPie(FaceRef f, Vec3 centre, Vec3 a, Vec3 b, Vec3 through) =>
        With(f.Body, Drawing.Pie(Bodies[f.Body], f.Face, centre, a, b, through), f.Face);

    /// <summary>A line drawn across a planar face from edge to edge (see <see cref="Drawing.Line"/>); the new face.</summary>
    public (SketchDocument Doc, FaceRef Face) DrawLine(FaceRef f, Vec3 p, Vec3 q) =>
        With(f.Body, Drawing.Line(Bodies[f.Body], f.Face, p, q), f.Face);

    /// <summary>One body replaced by a drawing on it (the faces keep their numbers; the new one comes last).</summary>
    private (SketchDocument, FaceRef) With(int b, (BrepBody Body, int Face) r, int drawnOn) => (new SketchDocument
    {
        Lines = Lines,
        Bodies = Bodies.Select((x, i) => i == b ? r.Body : x).ToList(),
        Curved = Curved,
        Colors = Recolor(b, null, r.Face, drawnOn),
    }, new FaceRef(b, r.Face));

    /// <summary>As a binary STL (in millimetres when <paramref name="scale"/> is 1000).</summary>
    public byte[] ToStl(double scale = 1000) =>
        StlWriter.WriteBinary(AllTriangles().SelectMany(x => x.Triangles).Select(t => new StlTriangle(t.A * scale, t.B * scale, t.C * scale)).ToList(), "PushPullMe<stl unit=MM>");

    /// <summary>As a DesignSpark package (exact surfaces).</summary>
    public RsDocxPackage ToRsDocx()
    {
        var pkg = RsDocxComposer.Compose(Bodies);
        // the colours painted here, as DesignSpark keeps them (see RsDocxColors)
        RsDocxColors.Write(pkg, Colors);
        // the loose lines, as DesignSpark keeps them (see RsDocxSketch)
        RsDocxSketch.Write(pkg, Lines);
        return pkg;
    }

    /// <summary>Volume (cubic metres) by the divergence theorem over the shown triangles.</summary>
    public double Volume() => AllTriangles().SelectMany(x => x.Triangles).Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6;
}
