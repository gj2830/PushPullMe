using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.Step;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A face of a document: body and face index.</summary>
public readonly record struct FaceRef(int Body, int Face);

/// <summary>
/// What is being drawn: exact bodies (metres), and for their curved faces the triangles they are shown with (from the
/// mesh they were recognised in, or made when a push/pull adds one). Immutable: every change gives a new document,
/// so undo is a list of documents.
/// </summary>
public sealed partial class SketchDocument
{
    public required IReadOnlyList<BrepBody> Bodies { get; init; }

    /// <summary>Per body: the triangles of its curved faces (by face index), wound outward, in metres.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>>> Curved { get; init; }

    /// <summary>Per body: the colour painted on its faces (by face index; ARGB). Faces not in it have no colour of their
    /// own (shown in the standard colour).</summary>
    public IReadOnlyList<IReadOnlyDictionary<int, uint>> Colors { get; init; } = [];

    /// <summary>Loose lines and arcs (sketch curves), not part of any body.</summary>
    public IReadOnlyList<SketchLine> Lines { get; init; } = [];

    /// <summary>A name per body, as given in a file it came from (null, or missing: none of its own; it is then called
    /// "Vaste vorm n" or "Oppervlak n", see <see cref="BodyName"/>). Carried along by <see cref="CarryNames"/>.</summary>
    public IReadOnlyList<string?> Names { get; init; } = [];

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
        var bodies = RsDocxReader.Read(path, out hidden, out var colors, out var names);
        // (a flat face's outer loop first, as the rest expects; a file need not have it so: boltest.rsdocx)
        var doc = FromBodies(bodies.Select(b => new BrepBody { Vertices = b.Vertices, Edges = b.Edges, Faces = Core.PushPull.OrderLoops(b), Sheet = b.Sheet }).ToList());
        return new SketchDocument { Bodies = doc.Bodies, Curved = doc.Curved, Colors = colors, Lines = RsDocxSketch.Read(RsDocxPackage.Open(path)), Names = names };
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
    /// <paramref name="join"/>: a piece pulled against another part with a face becomes one part with it (as DesignSpark's
    /// "merge" in its pull; off: it stays apart).
    /// </summary>
    public (SketchDocument Doc, PushPullResult Result) PushPull(FaceRef f, double distance, bool checkCrossing = true, bool join = true)
    {
        var body = Bodies[f.Body];
        if (body.Sheet) return PushSheet(f, distance, checkCrossing, join);
        // the end of a piece turned round an axis: it follows the turn, out and in (as DesignSpark)
        if (Core.Revolve.EndAxis(body, f.Face) is { } end) return TurnEnd(f, end, distance, checkCrossing);
        if (body.Faces[f.Face].Surface is BrepCylinder || (body.Faces[f.Face].Surface is BrepSphere or BrepTorus && Core.PushPull.IsRoundingSet(body, f.Face)))
            return PushPullCylinder(f, distance, checkCrossing);
        PushPullResult r;
        try { r = Core.PushPull.Apply(body, f.Face, distance); }
        catch (TouchesItselfException) when (distance > 0 && body.Faces[f.Face].Surface is BrepPlane)
        {
            // the body would touch itself only along an edge: the piece pulled out a body of its own, the body as it was
            // (as DesignSpark makes it)
            return PullApart(f, distance, checkCrossing);
        }
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
                ? Lang.T("Zo komt de ronding die meeschuift door een ander deel heen (bijvoorbeeld de ronding aan de andere kant: dan zou de straal kleiner moeten); dat kan nog niet.", "That way the rounding that moves along goes through another part (for example the rounding on the other side: then the radius would have to be smaller); that is not possible yet.")
                : Lang.T("Zo komt het vlak door een ander deel van het model heen; dat kan nog niet.", "That way the face goes through another part of the model; that is not possible yet."));
        if (checkCrossing && r.FaceMap[f.Face] is >= 0 and var mf && doc.AgainstOpposite(new FaceRef(f.Body, mf)))
            throw new PushPullException(Lang.T("Zo komt het vlak precies tegen de andere kant aan: doorduwen tot een gat kan nog niet.", "That way the face comes exactly against the other side: pushing through to a hole is not possible yet."));
        // pulled against a part of its own (a piece that came apart before, touching along an edge only): one part again
        // (with the groups, so only parts in the same group join)
        if (join && checkCrossing && distance > 0 && CarryMeta(this, doc).JoinTouching(f.Body, changedFaces) is { } joined)
            return (joined.Doc, r with { Body = joined.Doc.Bodies[joined.Body], FaceMap = r.FaceMap.Select(_ => -1).ToList() });
        return (doc, r);
    }

    /// <summary>
    /// A face pulled out as a body of its own, standing on it (the face and a twin pulled, see <see cref="Sheets.Twin"/>);
    /// the body it is on stays as it was. The face map is the body's own (nothing of it changed).
    /// </summary>
    private (SketchDocument Doc, PushPullResult Result) PullApart(FaceRef f, double distance, bool checkCrossing)
    {
        var body = Bodies[f.Body];
        var (td, _) = FromBodies([Sheets.Twin(body, f.Face)]).PushPull(new FaceRef(0, 0), distance, checkCrossing: false);
        var piece = td.Bodies[0];
        // (not into any part: what the piece shares with a body must be nothing, or no more than touching)
        if (checkCrossing)
            foreach (var other in Bodies.Where(x => !x.Sheet))
            {
                List<(BrepBody Body, List<(int Body, int Face)> From)> common;
                try { common = Booleans.Combine(other, piece, Booleans.Op.Intersect); }
                catch (PushPullException) { continue; }
                if (common.Any(c => FromBodies([c.Body]).Volume() > 1e-12))
                    throw new PushPullException(Lang.T("Zo komt het vlak door een ander deel van het model heen; dat kan nog niet.", "That way the face goes through another part of the model; that is not possible yet."));
            }
        var doc = AddBody(piece);
        var same = Enumerable.Range(0, body.Faces.Count).ToList();
        return (doc, new PushPullResult(body, same, [], [], new Dictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>>(), [], []));
    }

    /// <summary>
    /// A face without thickness (of a sheet) pulled out into a solid, as in SketchUp: along its normal, or the other way
    /// for a negative distance. The face and a twin facing the other way make a closed body without inside, whose
    /// face (or twin) is pulled as any other (<see cref="Sheets.Twin"/>). The solid takes the sheet's place; the
    /// sheet's other faces, if any, stay a sheet of their own (a new body). The face map gives the pulled face's new
    /// number in the solid.
    /// </summary>
    private (SketchDocument Doc, PushPullResult Result) PushSheet(FaceRef f, double distance, bool checkCrossing, bool join = true)
    {
        if (Math.Abs(distance) < 1e-9) throw new PushPullException(Lang.T("Trek het vlak een eindje uit om er een blok van te maken.", "Pull the face out a little to make a block of it."));
        var sheet = Bodies[f.Body];
        int pulled = distance > 0 ? 0 : 1;
        var (td, tr) = FromBodies([Sheets.Twin(sheet, f.Face)]).PushPull(new FaceRef(0, pulled), Math.Abs(distance), checkCrossing: false);
        var rest = Sheets.Without(sheet, f.Face);
        var map = Enumerable.Repeat(-1, sheet.Faces.Count).ToArray();
        map[f.Face] = tr.FaceMap[pulled];
        var bodies = Bodies.Select((b, i) => i == f.Body ? td.Bodies[0] : b).ToList();
        var curved = Curved.Select((c, i) => i == f.Body ? td.Curved[0] : c).ToList();
        if (rest != null)
        {
            bodies.Add(rest);
            curved.Add(new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>());
        }
        var doc = new SketchDocument { Lines = Lines, Bodies = bodies, Curved = curved, Colors = Recolor(f.Body, map) };
        var changed = Enumerable.Range(0, td.Bodies[0].Faces.Count).Select(g => new FaceRef(f.Body, g)).ToHashSet();
        if (checkCrossing && Intersections.FirstCrossing(doc, changed) is not null)
            throw new PushPullException(Lang.T("Zo komt het blok door een ander deel van het model heen; dat kan nog niet.", "That way the block goes through another part of the model; that is not possible yet."));
        if (!checkCrossing) return (doc, tr with { Body = td.Bodies[0], FaceMap = map });
        // a block lying with a face against a solid part (the triangle next to it, pulled from the same loose face before):
        // one part with it, as pulling faces of a block keeps them one; touching only along an edge it stays apart (as
        // DesignSpark). (Only when it is set, not while dragging: a boolean on every move of the mouse is too slow.)
        if (join && CarryMeta(this, doc).JoinTouching(f.Body, Enumerable.Range(0, td.Bodies[0].Faces.Count)) is { } joined)
        {
            map[f.Face] = -1;
            return (joined.Doc, tr with { Body = joined.Doc.Bodies[joined.Body], FaceMap = map });
        }
        return (doc, tr with { Body = td.Bodies[0], FaceMap = map });
    }

    /// <summary>
    /// Body <paramref name="body"/> joined with each solid part one of its faces <paramref name="near"/> (those just
    /// pulled) lies against with a face: tried as a union, kept only when that gives one body (touching along an edge
    /// only, a part stays apart, as DesignSpark makes it), then looked again from the larger part. Null when nothing joined.
    /// </summary>
    internal (SketchDocument Doc, int Body)? JoinTouching(int body, IEnumerable<int> near)
    {
        var doc = this; int at = body; bool joined = false;
        var region = Box(near.SelectMany(g => TrianglesOf(new FaceRef(body, g))));
        for (int j = 0; j < doc.Bodies.Count; j++)
        {
            if (j == at || doc.Bodies[j].Sheet || region is not { } rg) continue;
            // (only within the same group: parts in different groups never join, as in SketchUp)
            if (doc.GroupOf(j) != doc.GroupOf(at)) continue;
            if (Box(doc.AllTriangles().Where(t => t.Face.Body == j).SelectMany(t => t.Triangles)) is not { } other || !Overlap(rg, other)) continue;
            try
            {
                var (u, made) = doc.Combine(at, j, Booleans.Op.Union);
                if (made.Count != 1) continue;
                doc = CarryMeta(doc, u); at = made[0]; joined = true;
                // (start over: the part is larger now, and what it lies against with it)
                region = Box(doc.AllTriangles().Where(t => t.Face.Body == at).SelectMany(t => t.Triangles));
                j = -1;
            }
            catch (PushPullException) { }
        }
        return joined ? (doc, at) : null;

        static (Vec3 Lo, Vec3 Hi)? Box(IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> tris)
        {
            var p = tris.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            if (p.Count == 0) return null;
            return (new Vec3(p.Min(q => q.X), p.Min(q => q.Y), p.Min(q => q.Z)), new Vec3(p.Max(q => q.X), p.Max(q => q.Y), p.Max(q => q.Z)));
        }
        static bool Overlap((Vec3 Lo, Vec3 Hi) a, (Vec3 Lo, Vec3 Hi) b)
        {
            const double tol = 1e-6;
            return a.Lo.X <= b.Hi.X + tol && b.Lo.X <= a.Hi.X + tol && a.Lo.Y <= b.Hi.Y + tol && b.Lo.Y <= a.Hi.Y + tol && a.Lo.Z <= b.Hi.Z + tol && b.Lo.Z <= a.Hi.Z + tol;
        }
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
            throw new PushPullException(Lang.T("Zo komt de cilinder door een ander deel van het model heen; dat kan nog niet.", "That way the cylinder goes through another part of the model; that is not possible yet."));
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
            throw new PushPullException(Lang.T("Zo komt de ronding door een ander deel van het model heen; dat kan nog niet.", "That way the rounding goes through another part of the model; that is not possible yet."));
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
        if (pick.Whole != MoveWhole.None || IsClosedFreeForm(body, pick)) return MoveWholeBody(body, pick.Whole == MoveWhole.Copy, offset);
        return Move(body, pick, new Motion(offset), checkCrossing);
    }

    /// <summary>Whether the pick is the whole of a body made of faces without edges (a closed free form, as DesignSpark's
    /// revolved spline): moving it is moving the body.</summary>
    private bool IsClosedFreeForm(int body, MoveSelection pick) =>
        pick.Faces.Count > 0 && pick.Faces.Count == Bodies[body].Faces.Count && Bodies[body].Edges.Count == 0;

    /// <summary>
    /// The end face of a piece turned round an axis (<see cref="Core.Revolve.EndAxis"/>) turned on or back along it:
    /// <paramref name="distance"/> is the way its middle goes along the turn (out positive), so the angle is that over its
    /// distance from the axis. The faces round it, all of revolution round the axis, grow or shrink with it.
    /// </summary>
    private (SketchDocument Doc, PushPullResult Result) TurnEnd(FaceRef f, (Vec3 Point, Vec3 Dir, double Radius) end, double distance, bool checkCrossing)
    {
        double angle = distance / end.Radius;
        if (Math.Abs(angle) >= 2 * Math.PI) throw new PushPullException(Lang.T("Zo ver draait het vlak helemaal rond; dat kan niet.", "That far the face turns all the way round; that is not possible."));
        var (doc, r) = Move(f.Body, MoveSelection.Face(f.Face), new Motion(default, new BrepTransform.Turn(end.Point, end.Dir, angle)), checkCrossing);
        return (doc, new PushPullResult(r.Body, r.FaceMap, [], [], new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>(), r.Changed, []));
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
            throw new PushPullException(Lang.T("Zo komt een vlak door een ander deel van het model heen; dat kan nog niet.", "That way a face goes through another part of the model; that is not possible yet."));
        return (doc, r);
    }

    /// <summary>SketchUp's Offset on a planar face (see <see cref="Drawing.Offset"/>); the inner face.</summary>
    public (SketchDocument Doc, FaceRef Face) Offset(FaceRef f, double distance)
    {
        // (outward: drawn on the face round it, whose colour the new face gets)
        int on = f.Face;
        if (distance < 0)
        {
            var (corners, circle) = Drawing.OffsetOutline(Bodies[f.Body], f.Face, distance);
            on = Drawing.OffsetTarget(Bodies[f.Body], f.Face, corners, circle);
        }
        return With(f.Body, Drawing.Offset(Bodies[f.Body], f.Face, distance), on);
    }

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
        if (runs.Count == 0) throw new PushPullException(Lang.T("Die lijn ligt helemaal op randen die er al zijn.", "That line lies wholly on edges that are already there."));
        var doc = this; var last = f;
        foreach (var run in runs)
        {
            var b = doc.Bodies[f.Body];
            int face = Drawing.FaceOfRun(b, f.Face, run) ?? throw new PushPullException(Lang.T("De lijn loopt buiten het vlak.", "The line runs outside the face."));
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
        // what closes a shape of its own outside every face there (in the air, or touching the model only at a corner
        // or along an edge): a face without thickness, a body of its own (as SketchUp)
        for (int guard = 0; guard < 200; guard++)
        {
            var body = doc.Bodies[f.Body];
            var plane = Drawing.CoplanarFaces(body, f.Face);
            var edges = plane.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge]).Distinct()
                .Where(e => e.Start >= 0 && e.Curve is BrepLine or BrepCircle)
                .Select(e => new LoosePiece(body.Vertices[e.Start], body.Vertices[e.End], e.Curve as BrepCircle)).ToList();
            var loose = LooseLines.Pieces(doc.Lines, n, o, edges);
            if (loose.Count == 0) break;
            // (the faces' edges there count too, cut where loose lines end on them: a line from the edge out and back to
            // it closes a shape with the piece of edge between, as in SketchUp; at least one loose piece in it)
            // (only the outer border of the faces there: an edge between two of them never bounds a shape outside them)
            var border = plane.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).GroupBy(c => c.Edge).Where(g => g.Count() == 1)
                .Select(g => body.Edges[g.Key]).Where(e => e.Start >= 0 && e.Curve is BrepLine or BrepCircle)
                .Select(e => new LoosePiece(body.Vertices[e.Start], body.Vertices[e.End], e.Curve as BrepCircle)).ToList();
            var rims = LooseLines.Pieces(border.Select(e => e.ToLine()).ToList(), n, o, loose);
            var pieces = loose.Concat(rims).ToList();
            var inside = plane.Select(g => doc.TrianglesOf(new FaceRef(f.Body, g))).Where(t => t.Count > 0)
                .Select(t => (t[0].A + t[0].B + t[0].C) * (1.0 / 3)).ToList();
            List<int>? found = null;
            foreach (var ring in LooseLines.Candidates(pieces, _ => false))
                if (ring.Any(i => i < loose.Count) && OutsideFaces(body, plane, pieces, ring, n, loose.Count, inside)) { found = ring; break; }
            if (found == null) break;
            doc = doc.RingToSheet(pieces, found, n, o, loose.Count);
            made.Add(new FaceRef(doc.Bodies.Count - 1, 0));
        }
        return (doc, made);
    }

    /// <summary>
    /// Loose lines and arcs on a plane without faces (the ground, through o with normal n): every closed shape they make
    /// becomes a face without thickness, a body of its own; the faces made.
    /// </summary>
    public (SketchDocument Doc, List<FaceRef> Faces) CloseLooseLinesIn(Vec3 n, Vec3 o)
    {
        var doc = this; var made = new List<FaceRef>();
        n = n.Normalized();
        for (int guard = 0; guard < 200; guard++)
        {
            var pieces = LooseLines.Pieces(doc.Lines, n, o);
            if (pieces.Count == 0) break;
            if (LooseLines.Candidates(pieces, _ => false).FirstOrDefault() is not { } ring) break;
            doc = doc.RingToSheet(pieces, ring, n, o);
            made.Add(new FaceRef(doc.Bodies.Count - 1, 0));
        }
        return (doc, made);
    }

    /// <summary>
    /// A closed shape (points, the last the first again, and the curve of each step) drawn on a plane where there may be
    /// faces without thickness already (through o, normal n): where it overlaps them it divides them (the overlap a face
    /// of its own, as other programs do), the rest becomes a face of its own; the faces made, the newest last.
    /// </summary>
    public (SketchDocument Doc, List<FaceRef> Faces) AddShape(IReadOnlyList<Vec3> pts, IReadOnlyList<BrepCurve> curves, Vec3 n, Vec3 o)
    {
        n = n.Normalized();
        bool InPlane(BrepFace face) => face.Surface is BrepPlane pl && Math.Abs(Math.Abs(FaceGeometry.Normal(face).Dot(n)) - 1) < 1e-9 && Math.Abs((pl.Origin - o).Dot(n)) < 1e-9;
        if (!Bodies.Any(b => b.Sheet && b.Faces.Any(InPlane)))
        {
            var alone = AddSheet(Sheets.Face(pts, curves, n));
            return (alone, [new FaceRef(alone.Bodies.Count - 1, 0)]);
        }
        // (as loose lines: they divide the faces they cross, and close what is left with the edges)
        var lines = Enumerable.Range(0, curves.Count).Select(i => curves[i] is BrepCircle c ? new SketchLine(pts[i], pts[i + 1], c.Centre, c.Normal) : new SketchLine(pts[i], pts[i + 1])).ToList();
        var (doc, made) = CloseLooseLinesOnPlane(AddLines(lines), n, o);
        return (doc, made);
    }

    /// <summary>
    /// Loose lines on a plane (through o, normal n) closing shapes: first on the faces without thickness lying in it
    /// (dividing them), then what is left in the air (see <see cref="CloseLooseLinesIn"/>); the faces made.
    /// </summary>
    public (SketchDocument Doc, List<FaceRef> Faces) CloseLooseLinesOnPlane(Vec3 n, Vec3 o) => CloseLooseLinesOnPlane(this, n, o);

    private static (SketchDocument Doc, List<FaceRef> Faces) CloseLooseLinesOnPlane(SketchDocument doc, Vec3 n, Vec3 o)
    {
        n = n.Normalized();
        var made = new List<FaceRef>();
        for (int b = 0; b < doc.Bodies.Count; b++)
        {
            if (!doc.Bodies[b].Sheet) continue;
            int f = Enumerable.Range(0, doc.Bodies[b].Faces.Count).FirstOrDefault(g => doc.Bodies[b].Faces[g].Surface is BrepPlane pl
                && Math.Abs(Math.Abs(FaceGeometry.Normal(doc.Bodies[b].Faces[g]).Dot(n)) - 1) < 1e-9 && Math.Abs((pl.Origin - o).Dot(n)) < 1e-9, -1);
            if (f < 0) continue;
            var (d, m) = doc.CloseLooseLines(new FaceRef(b, f));
            doc = d; made.AddRange(m);
        }
        var (d2, m2) = doc.CloseLooseLinesIn(n, o);
        made.AddRange(m2);
        return (d2, made);
    }

    /// <summary>A closed run of loose pieces (in the plane through o with normal n) made a face without thickness: a new
    /// body; the lines of the plane left are its other pieces.</summary>
    private SketchDocument RingToSheet(IReadOnlyList<LoosePiece> pieces, IReadOnlyList<int> ring, Vec3 n, Vec3 o, int looseCount = int.MaxValue)
    {
        // (the loose pieces not used stay loose; pieces of face edges, after the loose ones, are no loose lines)
        var rest = Lines.Where(l => !LooseLines.InPlane(l, n, o)).Concat(pieces.Where((_, i) => i < looseCount && !ring.Contains(i)).Select(p => p.ToLine())).ToList();
        var (pts, curves) = LooseLines.Path(pieces, ring);
        return new SketchDocument { Lines = rest, Colors = Colors, Bodies = Bodies, Curved = Curved }.AddSheet(Sheets.Face(pts, curves, n));
    }

    /// <summary>A face without thickness added as a body of its own (the last one).</summary>
    public SketchDocument AddSheet(BrepBody sheet) => new()
    {
        Lines = Lines, Colors = Colors, Bodies = Bodies.Append(sheet).ToList(),
        Curved = Curved.Append(new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>()).ToList(),
    };

    /// <summary>
    /// Bodies taken out; the others keep their order (so their numbers go down past the ones taken) and, through
    /// <see cref="CarryMeta"/>, their names and groups (a group left empty goes).
    /// </summary>
    public SketchDocument RemoveBodies(IReadOnlyCollection<int> bodies)
    {
        if (bodies.Count == 0) throw new PushPullException(Lang.T("Kies wat er weg moet.", "Pick what is to go."));
        foreach (int b in bodies)
            if (b < 0 || b >= Bodies.Count) throw new PushPullException(Lang.T($"Er is geen deel {b}.", $"There is no part {b}."));
        return new SketchDocument
        {
            Lines = Lines,
            Bodies = Bodies.Where((_, i) => !bodies.Contains(i)).ToList(),
            Curved = Curved.Where((_, i) => !bodies.Contains(i)).ToList(),
            Colors = Colors.Where((_, i) => !bodies.Contains(i)).ToList(),
        };
    }

    /// <summary>Whether a closed run of pieces lies outside every face of the plane: none of its pieces runs over a face,
    /// and no corner of a face lies inside it.</summary>
    private static bool OutsideFaces(BrepBody body, List<int> plane, IReadOnlyList<LoosePiece> pieces, IReadOnlyList<int> ring, Vec3 n,
        int looseCount, IReadOnlyList<Vec3> inside)
    {
        // (a loose piece running over a face; the pieces of face edges lie on its border)
        if (ring.Any(i => i < looseCount && plane.Any(g => Drawing.Contains(body, g, pieces[i].Middle)))) return false;
        // the run as points (arcs by points along them), laid flat in the plane
        var (pts, curves) = LooseLines.Path(pieces, ring);
        var poly = new List<Vec3>();
        for (int i = 0; i + 1 < pts.Count; i++)
            if (curves[i] is BrepCircle c) poly.AddRange(Drawing.ArcPoints(c, pts[i], pts[i + 1], 16).SkipLast(1));
            else poly.Add(pts[i]);
        var x = (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n).Normalized(); var y = n.Cross(x);
        bool Inside(Vec3 q)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].Dot(x), yi = poly[i].Dot(y), xj = poly[j].Dot(x), yj = poly[j].Dot(y), qx = q.Dot(x), qy = q.Dot(y);
                if ((yi > qy) != (yj > qy) && qx < (xj - xi) * (qy - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }
        // (a corner of a face on the run itself does not count: the shape may touch the model there)
        bool OnRun(Vec3 q) => ring.Any(i => (pieces[i].A - q).Length < 1e-7 || (pieces[i].B - q).Length < 1e-7);
        var corners = plane.SelectMany(g => body.Faces[g].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge])
            .SelectMany(e => e.Start >= 0 ? new[] { body.Vertices[e.Start], body.Vertices[e.End] } : []).Distinct();
        // (nor a face itself inside it: a run round the outside of a face along its edges)
        return !corners.Any(q => !OnRun(q) && Inside(q)) && !inside.Any(Inside);
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
        if (face < 0) throw new PushPullException(Lang.T("De vorm loopt buiten het vlak.", "The shape runs outside the face."));
        return With(f.Body, Drawing.Path(b, face, pts, curves), face);
    }

    /// <summary>
    /// A planar face turned out of its body round an axis in its plane (see <see cref="Core.Revolve"/>; a positive angle
    /// moves it out along its normal), unless that makes the body cross itself; the end cap (the face's number).
    /// </summary>
    public (SketchDocument Doc, FaceRef Cap) Revolve(FaceRef f, Vec3 axisPoint, Vec3 axisDir, double angle, bool checkCrossing = true)
    {
        if (Bodies[f.Body].Sheet) return RevolveSheet(f, axisPoint, axisDir, angle, checkCrossing);
        var a = Core.Revolve.OutwardAxis(Bodies[f.Body], f.Face, axisPoint, axisDir);
        if (angle >= 2 * Math.PI - 1e-9)
        {
            // a whole turn: the solid it sweeps, united with the body
            var solid = Core.Revolve.Solid(Bodies[f.Body], f.Face, axisPoint, a);
            var both = new SketchDocument { Lines = Lines, Bodies = Bodies.Append(solid).ToList(), Curved = Curved.Append(new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>()).ToList(), Colors = Colors };
            var (united, made) = both.Combine(f.Body, Bodies.Count, Booleans.Op.Union);
            return (united, new FaceRef(made[0], 0));
        }
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
            throw new PushPullException(Lang.T("Zo ver gedraaid snijdt het vlak door het lichaam heen.", "Turned that far the face cuts through the body."));
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
    public SketchDocument Union(int a, int b) => Combine(a, b, Booleans.Op.Union).Doc;

    /// <summary>
    /// Two bodies combined (see <see cref="Booleans.Combine"/>): the result (one body or more) in the place of the
    /// first, the second gone, unless <paramref name="keepSecond"/> (SketchUp's Trim: the cutter stays). The faces keep
    /// the colour of the face they came from; flat faces in one plane next to each other merge. Also the bodies the
    /// result is in.
    /// </summary>
    /// <summary>Two parts in different groups are not combined (as SketchUp keeps a group apart).</summary>
    private void SameGroup(int a, int b)
    {
        if (GroupOf(a) != GroupOf(b))
            throw new PushPullException(Lang.T("Deze delen liggen in verschillende groepen; open een groep of hef hem op om ze samen te bewerken.", "These parts lie in different groups; open a group or explode it to work on them together."));
    }

    public (SketchDocument Doc, List<int> Bodies) Combine(int a, int b, Booleans.Op op, bool keepSecond = false)
    {
        if (a == b) throw new PushPullException(Lang.T("Kies twee verschillende delen.", "Pick two different parts."));
        NoSheet(a); NoSheet(b); SameGroup(a, b);
        return Replace(keepSecond ? [a] : [a, b], [Booleans.Combine(Bodies[a], Bodies[b], op)], a, b);
    }

    /// <summary>
    /// Two bodies split where they meet (as SketchUp's Split): what is only in the first, what is in both and what is
    /// only in the second, each a body (or more) of its own, in the place of the two.
    /// </summary>
    public (SketchDocument Doc, List<int> Bodies) Split(int a, int b)
    {
        if (a == b) throw new PushPullException(Lang.T("Kies twee verschillende delen.", "Pick two different parts."));
        NoSheet(a); NoSheet(b); SameGroup(a, b);
        var common = Booleans.Combine(Bodies[a], Bodies[b], Booleans.Op.Intersect);
        List<(BrepBody, List<(int, int)>)> Less(int x, int y)
        {
            // (nothing left of one: then none of it)
            try { return Booleans.Combine(Bodies[x], Bodies[y], Booleans.Op.Subtract).Select(r => (r.Body, r.From.Select(f => (f.Body == 0 ? 0 : 1, f.Face)).ToList())).ToList(); }
            catch (PushPullException) { return []; }
        }
        var onlyB = Less(b, a).Select(r => (r.Item1, r.Item2.Select(f => (1 - f.Item1, f.Item2)).ToList())).ToList();
        return Replace([a, b], [Less(a, b), common, onlyB], a, b);
    }

    /// <summary>The bodies <paramref name="remove"/> taken out and the new ones put in their place (at the first's place,
    /// on from there); a new body's faces coloured as the faces of a (0) or b (1) they came from.</summary>
    private (SketchDocument Doc, List<int> Bodies) Replace(int[] remove, List<List<(BrepBody Body, List<(int Body, int Face)> From)>> results, int a, int b)
    {
        var made = results.SelectMany(r => r).ToList();
        var bodies = new List<BrepBody>(); var curved = new List<IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>>();
        var colors = new List<IReadOnlyDictionary<int, uint>>(); var newAt = new List<int>();
        for (int i = 0; i < Bodies.Count; i++)
        {
            if (i == remove[0])
                foreach (var (body, from) in made)
                {
                    var c = new Dictionary<int, uint>();
                    for (int f = 0; f < from.Count; f++) if (ColorOf(new FaceRef(from[f].Body == 0 ? a : b, from[f].Face)) is uint col) c[f] = col;
                    newAt.Add(bodies.Count);
                    // (curved faces in triangles to be shown)
                    var tris = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
                    for (int g = 0; g < body.Faces.Count; g++)
                        if (body.Faces[g].Surface is not BrepPlane)
                            tris[g] = FaceGeometry.CurvedTriangles(body, body.Faces[g]) ?? BrepBooleans.Triangles(body, g);
                    bodies.Add(body); curved.Add(tris); colors.Add(c);
                }
            if (remove.Contains(i)) continue;
            bodies.Add(Bodies[i]); curved.Add(Curved[i]); colors.Add(i < Colors.Count ? Colors[i] : new Dictionary<int, uint>());
        }
        var doc = new SketchDocument { Lines = Lines, Bodies = bodies, Curved = curved, Colors = colors };
        foreach (int nb in newAt) doc = doc.MergeFlat(nb);
        return (doc, newAt);
    }

    /// <summary>
    /// SketchUp's Follow Me along a path (see <see cref="Sweep"/>): the profile face swept along the outer edge of the
    /// path face (both of one body). Where the sweep lies in the body (a profile drawn on its side, at a corner of the
    /// path) it is cut away, as a chamfer or a groove round it; else it is added, as a rim. The bodies the result is in.
    /// </summary>
    public (SketchDocument Doc, List<int> Bodies) FollowPath(FaceRef profile, FaceRef path)
    {
        NoSheet(path.Body);
        // (a face without thickness may be the profile for a path on another part: it is used up)
        bool sheet = Bodies[profile.Body].Sheet;
        if (profile.Body != path.Body && !sheet) throw new PushPullException(Lang.T("Het profiel en het pad moeten van hetzelfde deel zijn (of het profiel een vlak zonder dikte).", "The profile and the path must be of the same part (or the profile a face without thickness)."));
        var prisms = Sweep.Prisms(Bodies[profile.Body], profile.Face, Bodies[path.Body], path.Face);
        var swept = prisms[0];
        for (int i = 1; i < prisms.Count; i++) swept = Booleans.Combine(swept, prisms[i], Booleans.Op.Union).Single().Body;
        // (a point of the first prism: in the body or not)
        var probe = prisms[0].Vertices.Aggregate(new Vec3(0, 0, 0), (a, v) => a + v) * (1.0 / prisms[0].Vertices.Count);
        var tris = Enumerable.Range(0, Bodies[path.Body].Faces.Count).SelectMany(g => TrianglesOf(new FaceRef(path.Body, g))).ToList();
        var op = Inside(tris, probe) ? Booleans.Op.Subtract : Booleans.Op.Union;
        var (start, pathBody) = sheet ? WithoutSheetFace(profile, path.Body) : (this, path.Body);
        var both = start.AddBody(swept);
        return both.Combine(pathBody, both.Bodies.Count - 1, op);
    }

    /// <summary>A body added as the last one (its round faces in triangles).</summary>
    private SketchDocument AddBody(BrepBody b) => new()
    {
        Lines = Lines, Colors = Colors, Bodies = Bodies.Append(b).ToList(),
        Curved = Curved.Append(FromBodies([b]).Curved[0]).ToList(),
    };

    /// <summary>The document without one face of a sheet (the sheet gone when it was its only face); where body
    /// <paramref name="keep"/> is now.</summary>
    private (SketchDocument Doc, int Keep) WithoutSheetFace(FaceRef f, int keep)
    {
        if (Sheets.Without(Bodies[f.Body], f.Face) is { } rest)
            return (new SketchDocument
            {
                Lines = Lines, Bodies = Bodies.Select((b, i) => i == f.Body ? rest : b).ToList(),
                Curved = Curved.Select((c, i) => i == f.Body ? new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>() : c).ToList(),
                Colors = Recolor(f.Body, Enumerable.Repeat(-1, Bodies[f.Body].Faces.Count).ToArray()),
            }, keep);
        return (new SketchDocument
        {
            Lines = Lines, Bodies = Bodies.Where((_, i) => i != f.Body).ToList(), Curved = Curved.Where((_, i) => i != f.Body).ToList(),
            Colors = Colors.Where((_, i) => i != f.Body).ToList(),
        }, keep > f.Body ? keep - 1 : keep);
    }

    /// <summary>
    /// A face without thickness turned round an axis: the solid it sweeps (its twin's face turned, as push/pull pulls it;
    /// a whole turn: the solid of revolution) takes the sheet's place, its other faces stay a sheet of their own.
    /// </summary>
    private (SketchDocument Doc, FaceRef Cap) RevolveSheet(FaceRef f, Vec3 axisPoint, Vec3 axisDir, double angle, bool checkCrossing)
    {
        var sheet = Bodies[f.Body];
        SketchDocument solid; int cap;
        if (angle >= 2 * Math.PI - 1e-9)
        {
            solid = FromBodies([Core.Revolve.Solid(sheet, f.Face, axisPoint, Core.Revolve.OutwardAxis(sheet, f.Face, axisPoint, axisDir))]);
            cap = 0;
        }
        else
        {
            var (td, tc) = FromBodies([Sheets.Twin(sheet, f.Face)]).Revolve(new FaceRef(0, 0), axisPoint, axisDir, angle, checkCrossing: false);
            solid = td; cap = tc.Face;
        }
        var rest = Sheets.Without(sheet, f.Face);
        var bodies = Bodies.Select((b, i) => i == f.Body ? solid.Bodies[0] : b).ToList();
        var curved = Curved.Select((c, i) => i == f.Body ? solid.Curved[0] : c).ToList();
        if (rest != null) { bodies.Add(rest); curved.Add(new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>()); }
        var doc = new SketchDocument { Lines = Lines, Bodies = bodies, Curved = curved, Colors = Recolor(f.Body, Enumerable.Repeat(-1, sheet.Faces.Count).ToArray()) };
        var changed = Enumerable.Range(0, solid.Bodies[0].Faces.Count).Select(g => new FaceRef(f.Body, g)).ToHashSet();
        if (checkCrossing && Intersections.FirstCrossing(doc, changed) is not null)
            throw new PushPullException(Lang.T("Zo komt het gedraaide vlak door een ander deel van het model heen; dat kan nog niet.", "That way the turned face goes through another part of the model; that is not possible yet."));
        return (doc, new FaceRef(f.Body, cap));
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
        Bodies = Bodies.Select((x, i) => i == b ? Keep(x, r.Body) : x).ToList(),
        Curved = Curved,
        Colors = Recolor(b, null, r.Face, drawnOn),
    }, new FaceRef(b, r.Face));

    /// <summary>What needs a solid refuses a face without thickness (with what to do instead).</summary>
    private void NoSheet(int body)
    {
        if (Bodies[body].Sheet)
            throw new PushPullException(Lang.T("Dit is een vlak zonder dikte; trek het eerst uit (⇕) tot een blok.", "This is a face without thickness; pull it out first (⇕) into a block."));
    }

    /// <summary>A body made anew from another (drawn on, say): a sheet stays a sheet.</summary>
    private static BrepBody Keep(BrepBody was, BrepBody now) =>
        was.Sheet == now.Sheet ? now : new BrepBody { Vertices = now.Vertices, Edges = now.Edges, Faces = now.Faces, Sheet = was.Sheet };

    /// <summary>As a binary STL (in millimetres when <paramref name="scale"/> is 1000).</summary>
    public byte[] ToStl(double scale = 1000) =>
        StlWriter.WriteBinary(AllTriangles().SelectMany(x => x.Triangles).Select(t => new StlTriangle(t.A * scale, t.B * scale, t.C * scale)).ToList(), "PushPullMe<stl unit=MM>");

    /// <summary>As a DesignSpark package (exact surfaces).</summary>
    /// <summary>The model as STEP, its main format (see <see cref="StepWriter"/>): the bodies by name ("Vaste vorm 1",
    /// "Oppervlak 2"), the colours painted here and the loose lines (construction lines are aids only: left out).</summary>
    public string ToStep(string fileName = "model.step")
    {
        // the groups each before the groups in them (an assembly per group), by index
        var order = new List<SketchGroup>();
        void Add(int? parent) { foreach (var g in GroupsIn(parent)) { order.Add(g); Add(g.Id); } }
        Add(null);
        var index = order.Select((g, k) => (g.Id, k)).ToDictionary(x => x.Id, x => x.k);
        return StepWriter.Write(new StepModel(
            Bodies.Select((b, i) => new StepBody(b, BodyName(i), i < Colors.Count && Colors[i].Count > 0 ? Colors[i] : null, GroupOf(i) is { } g ? index[g] : null)).ToList(),
            Lines.Where(l => !l.Construction).Select(l => new StepCurve(l.Start, l.End, l.Circle)).ToList(),
            order.Select(g => new StepGroup(GroupName(g.Id), g.Parent is { } p ? index[p] : null)).ToList()), fileName);
    }

    /// <summary>A body's name: its own (<see cref="Names"/>), or as DesignSpark gives it, numbered: "Vaste vorm 1",
    /// "Oppervlak 2" (a face without thickness).</summary>
    public string BodyName(int b) => b < Names.Count && !string.IsNullOrWhiteSpace(Names[b]) ? Names[b]!
        : (Bodies[b].Sheet ? Lang.T("Oppervlak", "Surface") : Lang.T("Vaste vorm", "Solid")) + $" {b + 1}";

    /// <summary>Whether a name is only the standard one a program gives (DesignSpark's "Vaste vorm", "Oppervlak", ours
    /// numbered, "Solid"/"Surface"): no name of its own.</summary>
    public static bool IsStandardName(string? name) => string.IsNullOrWhiteSpace(name)
        || System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(vaste vorm|oppervlak|solid|surface|body)( \d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The document with body <paramref name="b"/> named <paramref name="name"/> (empty, or a standard name such as
    /// "Vaste vorm": no name of its own any more).</summary>
    public SketchDocument Rename(int b, string? name)
    {
        var names = OwnNames();
        names[b] = IsStandardName(name) ? null : name!.Trim();
        return new SketchDocument { Bodies = Bodies, Curved = Curved, Colors = Colors, Lines = Lines, Names = names };
    }

    /// <summary>The bodies' own names (null: none), one per body.</summary>
    private List<string?> OwnNames() => Enumerable.Range(0, Bodies.Count).Select(b => b < Names.Count && !string.IsNullOrWhiteSpace(Names[b]) ? Names[b] : null).ToList();

    /// <summary>A STEP file (from here or from another CAD program, see <see cref="StepReader"/>): its bodies, colours and
    /// loose lines. Throws <see cref="StepException"/> when it cannot be read.</summary>
    public static SketchDocument FromStep(string path)
    {
        var m = StepReader.Read(path);
        if (m.Bodies.Count == 0 && m.Curves.Count == 0) throw new StepException(Lang.T("Er staan geen lichamen in dit STEP-bestand.", "There are no bodies in this STEP file."));
        var doc = FromBodies(m.Bodies.Select(b => new BrepBody { Vertices = b.Body.Vertices, Edges = b.Body.Edges, Faces = Core.PushPull.OrderLoops(b.Body), Sheet = b.Body.Sheet }).ToList());
        return new SketchDocument
        {
            Bodies = doc.Bodies, Curved = doc.Curved,
            Colors = m.Bodies.Select(b => (IReadOnlyDictionary<int, uint>)(b.FaceColors ?? new Dictionary<int, uint>())).ToList(),
            Lines = m.Curves.Select(c => c.Arc is { } a ? new SketchLine(c.Start, c.End, a.Centre, a.Normal) : new SketchLine(c.Start, c.End)).ToList(),
            Names = m.Bodies.Select(b => IsStandardName(b.Name) ? null : b.Name).ToList(),
            // (the file's groups numbered from 1, in its order; a name that is only "Groep n" is none of its own)
            Groups = m.Groups.Select((g, k) => new SketchGroup(k + 1, IsStandardGroupName(g.Name) ? null : g.Name, g.Parent + 1)).ToList(),
            BodyGroups = m.Groups.Count > 0 ? m.Bodies.Select(b => b.Group + 1).ToList() : [],
        };
    }

    public RsDocxPackage ToRsDocx()
    {
        var pkg = RsDocxComposer.Compose(Bodies, names: OwnNames());
        // the colours painted here, as DesignSpark keeps them (see RsDocxColors)
        RsDocxColors.Write(pkg, Colors);
        // the loose lines, as DesignSpark keeps them (see RsDocxSketch)
        RsDocxSketch.Write(pkg, Lines);
        return pkg;
    }

    /// <summary>Volume (cubic metres) by the divergence theorem over the shown triangles.</summary>
    public double Volume() => AllTriangles().Where(x => !Bodies[x.Face.Body].Sheet).SelectMany(x => x.Triangles).Sum(t => t.A.Dot(t.B.Cross(t.C))) / 6;
}
