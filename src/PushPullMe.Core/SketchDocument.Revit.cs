using RsDocx.Core.Brep;
using RsDocx.Core.Revit;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public sealed partial class SketchDocument
{
    /// <summary>
    /// The Revit models a Revit project links (see <see cref="RevitLinks"/>) whose files are found: where the link says,
    /// next to the project, else by name next to it. Each with where it is placed.
    /// </summary>
    public static List<(RevitLink Link, string File)> RevitLinksFound(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var found = new List<(RevitLink, string)>();
        try
        {
            foreach (var link in RevitLinks.Read(RevitFile.Open(path)))
            {
                string saved = link.Path.Replace('\\', Path.DirectorySeparatorChar);
                foreach (var candidate in new[] { Path.IsPathRooted(saved) ? saved : Path.Combine(dir, saved), Path.Combine(dir, Path.GetFileName(saved)) })
                    if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add((link, Path.GetFullPath(candidate)));
                        break;
                    }
            }
        }
        catch (Exception e) when (e is FormatException or CompoundFileException or IOException) { }
        return found;
    }

    /// <summary>
    /// A Revit project (.rvt, from Revit 2023 on) read without Revit (see <see cref="RevitGeometry"/>): every saved body a
    /// body here, its flat faces flat faces (walls, floors and roofs can be pushed and pulled), its curved ones as the
    /// small flat faces they are drawn with; shared edges one edge. A body that does not close is a face without
    /// thickness. Every model a group named after its file, in it a group per Revit class of element ("SWall", "Floor",
    /// "FamilyInstance"), each body named after its element: so the tree shows where everything comes from. With
    /// <paramref name="links"/> (see <see cref="RevitLinksFound"/>) those models too, each where its link places it.
    /// <paramref name="report"/>: what was left out.
    /// </summary>
    public static SketchDocument FromRevit(string path, out RevitGeometryReport report, IReadOnlyList<(RevitLink Link, string File)>? links = null)
    {
        report = new RevitGeometryReport();
        var models = new List<(string Name, string File, double[]? Placement)> { (Path.GetFileNameWithoutExtension(path), path, null) };
        foreach (var (link, file) in links ?? []) models.Add((Path.GetFileNameWithoutExtension(file), file, link.Placement));
        var bodies = new List<BrepBody>();
        var curved = new List<IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>>();
        var colors = new List<IReadOnlyDictionary<int, uint>>();
        var names = new List<string?>();
        var bodyGroups = new List<int?>();
        var groups = new List<SketchGroup>();
        foreach (var model in models)
        {
            // (a linked model where its link puts it: every body placed so, surfaces and all)
            Placement? place = model.Placement is { } m && !m.SequenceEqual(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })
                ? new Placement(new Vec3(m[3], m[7], m[11]), new Vec3(m[0], m[4], m[8]), new Vec3(m[1], m[5], m[9]), new Vec3(m[2], m[6], m[10]))
                : null;
            // (the file a group of its own, its classes in it; made when its first body comes)
            int? top = null;
            var groupOf = new Dictionary<string, int>();
            void Take(RevitElementMesh e)
            {
                // (every piece of every saved body of the element; numbered when there are more)
                var pieces = e.Bodies.SelectMany(ToBody).ToList();
                for (int k = 0; k < pieces.Count; k++)
                {
                    var (b, shown, painted) = pieces[k];
                    if (place is { } pl)
                    {
                        b = BrepTransform.Placed(b, pl);
                        shown = shown.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value
                            .Select(t => pl.Mirrors ? (pl.Point(t.Item1), pl.Point(t.Item3), pl.Point(t.Item2)) : (pl.Point(t.Item1), pl.Point(t.Item2), pl.Point(t.Item3))).ToList());
                    }
                    if (top == null)
                    {
                        top = groups.Count + 1;
                        groups.Add(new SketchGroup(top.Value, model.Name, null));
                    }
                    if (!groupOf.TryGetValue(e.Class, out int g))
                    {
                        groupOf[e.Class] = g = groups.Count + 1;
                        groups.Add(new SketchGroup(g, e.Class, top));
                    }
                    bodies.Add(b);
                    curved.Add(shown);
                    colors.Add(painted);
                    names.Add(pieces.Count == 1 ? $"{e.Class} {e.Id}" : $"{e.Class} {e.Id}.{k + 1}");
                    bodyGroups.Add(g);
                }
            }
            // (element by element: what is read is made into bodies at once, the rest let go)
            try { RevitGeometry.Read(RevitFile.Open(model.File), Take, report); }
            catch (FormatException e) when (model.Placement == null) { throw new RevitReadException(Lang.T("Dit Revit-bestand is (nog) niet te lezen: ", "This Revit file cannot be read (yet): ") + e.Message); }
            catch (Exception e) when (e is FormatException or CompoundFileException or IOException) { }
        }
        if (bodies.Count == 0) throw new RevitReadException(Lang.T("Er staan geen lichamen in dit Revit-bestand.", "There are no bodies in this Revit file."));
        return new SketchDocument { Bodies = bodies, Curved = curved, Colors = colors, Names = names, Groups = groups, BodyGroups = bodyGroups };
    }

    /// <summary>
    /// A Revit body as a body, with the triangles its curved faces are shown with: its points one vertex each (the faces
    /// on either side of an edge share them, so they share the edge); a flat face its rings, a curved face with an exact
    /// surface (see <see cref="RevitSurface"/>) its rings on that surface, shown by its triangles (one face, however
    /// many triangles), any other face its triangles, each a flat face. The outer ring runs counter-clockwise round the
    /// face's outward side (which its triangles give), the holes the other way. Every connected piece (faces sharing
    /// edges) a body of its own: a saved body can be several, and a body here is one shell. Closed (every edge in two
    /// faces, once each way): a solid; else a face without thickness. Every face the colour of its material (none
    /// without one).
    /// </summary>
    private static List<(BrepBody Body, IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>> Curved, IReadOnlyDictionary<int, uint> Colors)> ToBody(RevitBody rb)
    {
        var verts = new List<Vec3>();
        var index = new Dictionary<(long, long, long), int>();
        int V(Vec3 p)
        {
            // (points within two nanometres are one, any two in one cell of a nanometre grid so: looked for in the
            // point's cell and the cells around it, as feet in metres often fall on half a nanometre, where rounding
            // would part two all but equal points)
            var (x, y, z) = ((long)Math.Floor(p.X * 1e9), (long)Math.Floor(p.Y * 1e9), (long)Math.Floor(p.Z * 1e9));
            for (long i = x - 1; i <= x + 1; i++)
                for (long j = y - 1; j <= y + 1; j++)
                    for (long k = z - 1; k <= z + 1; k++)
                        if (index.TryGetValue((i, j, k), out int known) && (verts[known] - p).Length <= 2e-9) return known;
            index.TryAdd((x, y, z), verts.Count);
            verts.Add(p);
            return verts.Count - 1;
        }
        int[] Ring(IEnumerable<Vec3> points)
        {
            var r = new List<int>();
            foreach (var p in points) { int i = V(p); if (r.Count == 0 || r[^1] != i) r.Add(i); }
            while (r.Count > 1 && r[0] == r[^1]) r.RemoveAt(r.Count - 1);
            return r.ToArray();
        }
        Vec3 Newell(int[] ring)
        {
            var n = new Vec3(0, 0, 0);
            for (int i = 0; i < ring.Length; i++)
            {
                var (a, b) = (verts[ring[i]], verts[ring[(i + 1) % ring.Length]]);
                n += new Vec3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            return n;
        }
        // (per face: its exact surface (null: flat, from its outer ring), whether it faces against it, its rings, the
        // triangles it is shown with when curved, whether it is one of the triangles of a face not read exactly, and
        // its colour (0xAARRGGBB), its material's)
        var faces = new List<(BrepSurface? Surface, bool Reversed, int[][] Loops, List<(Vec3, Vec3, Vec3)>? Shown, bool Facet, uint? Color)>();
        foreach (var f in rb.Faces)
        {
            var tris = f.Triangles.Select(t => (t.A, t.B, t.C)).ToList();
            uint? color = f.Material is { } made ? 0xFF000000u | made.Rgb : null;
            var outward = tris.Aggregate(new Vec3(0, 0, 0), (n, t) => n + (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1));
            double area = tris.Sum(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length);
            bool flat = f.Rings is { Count: > 0 } && f.Surface is null or BrepPlane;
            // (a curved face whose triangles face every way, a whole tube: no outward side to turn its rings by; nor a
            // torus that is no ring, which .rsdocx cannot hold)
            bool curved = f.Rings is { Count: > 0 } && f.Surface is not (null or BrepPlane) && outward.Length > 0.2 * area
                && !(f.Surface is BrepTorus torus && torus.MajorRadius <= torus.MinorRadius);
            if (flat || curved)
            {
                var outer = Ring(f.Rings![0]);
                if (outer.Length < 3) continue;
                if (Newell(outer).Dot(outward) < 0) Array.Reverse(outer);
                var loops = new List<int[]> { outer };
                foreach (var h in f.Rings.Skip(1).Select(Ring).Where(h => h.Length >= 3))
                {
                    if (Newell(h).Dot(outward) > 0) Array.Reverse(h);
                    loops.Add(h);
                }
                if (flat)
                {
                    // (a face without a surface read that is not quite flat after all, a ruled one: its triangles)
                    var n = Newell(outer).Normalized(); var o = verts[outer[0]];
                    if (loops.SelectMany(l => l).All(v => Math.Abs((verts[v] - o).Dot(n)) <= OnFace))
                    {
                        faces.Add((null, false, loops.ToArray(), null, false, color));
                        continue;
                    }
                }
                else
                {
                    var surface = f.Surface!;
                    var along = Outward(surface);
                    // (against its surface where the triangles face the other way than the surface's normal there)
                    var t0 = tris.MaxBy(t => (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1).Length);
                    var c0 = (t0.Item1 + t0.Item2 + t0.Item3) * (1.0 / 3);
                    bool reversed = along(c0).Dot((t0.Item2 - t0.Item1).Cross(t0.Item3 - t0.Item1)) < 0;
                    faces.Add((surface, reversed, loops.ToArray(), tris, false, color));
                    continue;
                }
            }
            foreach (var t in f.Triangles)
            {
                var tri = Ring([t.A, t.B, t.C]);
                if (tri.Length == 3) faces.Add((null, false, [tri], null, true, color));
            }
        }
        // every edge its curve (see EdgeCurves); one none fits straight (.rsdocx shows the faces along it by their
        // triangles: see Saveable)
        var curves = EdgeCurves(faces, verts, Newell);
        // the pieces: faces joined where they share an edge, the one along it, the other back (an edge of three faces,
        // or of two the same way round, joins none)
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Root(int f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
        var uses = new Dictionary<(int, int), List<(int Face, bool Up)>>();
        for (int f = 0; f < faces.Count; f++)
            foreach (var l in faces[f].Loops)
                for (int k = 0; k < l.Length; k++)
                {
                    int v = l[k], w = l[(k + 1) % l.Length];
                    var key = v < w ? (v, w) : (w, v);
                    if (!uses.TryGetValue(key, out var u)) uses[key] = u = [];
                    u.Add((f, v < w));
                }
        foreach (var u in uses.Values)
            if (u.Count == 2 && u[0].Up != u[1].Up) parent[Root(u[0].Face)] = Root(u[1].Face);
        var result = new List<(BrepBody, IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>, IReadOnlyDictionary<int, uint>)>();
        foreach (var piece in Enumerable.Range(0, faces.Count).GroupBy(Root))
        {
            var its = piece.Select(f => faces[f]).ToList();
            // (an edge still used wrongly, its faces joined round about: an edge of its own for every use, a seam)
            var wrong = its.SelectMany(f => f.Loops.SelectMany(l => l.Select((v, k) => (v, w: l[(k + 1) % l.Length]))))
                .GroupBy(e => e.v < e.w ? (e.v, e.w) : (e.w, e.v))
                .Where(g => g.Count() > 2 || g.Count() == 2 && g.First().v == g.Last().v).Select(g => g.Key).ToHashSet();
            result.Add(Piece(its, wrong));
        }
        return result;

        // one piece as a body: its own vertices, the edges (each from its lower vertex number to its higher), the faces'
        // loops of them
        (BrepBody, IReadOnlyDictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>, IReadOnlyDictionary<int, uint>) Piece(List<(BrepSurface? Surface, bool Reversed, int[][] Loops, List<(Vec3, Vec3, Vec3)>? Shown, bool Facet, uint? Color)> its, HashSet<(int, int)> seams)
        {
            var own = new Dictionary<int, int>();
            var pts = new List<Vec3>();
            int O(int v) { if (!own.TryGetValue(v, out int i)) { own[v] = i = pts.Count; pts.Add(verts[v]); } return i; }
            var edges = new List<BrepEdge>();
            var edgeOf = new Dictionary<(int, int), int>();
            var directed = new Dictionary<(int, int), int>();
            // (an edge from its lower vertex number to its higher, in the numbers of the whole body: its curve runs so)
            IReadOnlyList<BrepCoedge> Loop(int[] l) => l.Select((v, k) =>
            {
                int w = l[(k + 1) % l.Length];
                directed[(v, w)] = directed.GetValueOrDefault((v, w)) + 1;
                var key = v < w ? (v, w) : (w, v);
                if (seams.Contains(key) || !edgeOf.TryGetValue(key, out int e))
                {
                    edgeOf[key] = e = edges.Count;
                    edges.Add(new BrepEdge(O(key.Item1), O(key.Item2), curves[key]));
                }
                return new BrepCoedge(e, v < w);
            }).ToList();
            var brepFaces = new List<BrepFace>();
            var shown = new Dictionary<int, IReadOnlyList<(Vec3, Vec3, Vec3)>>();
            var colors = new Dictionary<int, uint>();
            foreach (var (surface, reversed, globalLoops, tris, _, color) in its)
            {
                if (color is { } c) colors[brepFaces.Count] = c;
                var loops = globalLoops.Select(l => l.Select(O).ToArray()).ToArray();
                var coedges = globalLoops.Select(Loop).ToList();
                BrepSurface sf;
                if (surface != null) { sf = surface is BrepSphere sp ? Pole(sp, globalLoops.SelectMany(l => l).Select(v => verts[v]), tris!) : surface; shown[brepFaces.Count] = tris!; }
                else
                {
                    var n = Newell(globalLoops[0]).Normalized();
                    var x = pts[loops[0][1]] - pts[loops[0][0]];
                    sf = new BrepPlane(pts[loops[0][0]], n, (x - n * x.Dot(n)).Normalized());
                }
                brepFaces.Add(new BrepFace(sf, reversed, coedges));
            }
            bool closed = directed.All(kv => kv.Value == 1 && directed.GetValueOrDefault((kv.Key.Item2, kv.Key.Item1)) == 1);
            return (new BrepBody { Vertices = pts, Edges = edges, Faces = brepFaces, Sheet = !closed }, shown, colors);
        }
    }

    /// <summary>How far an edge may be off the faces it bounds (metres): under Parasolid's 1e-8, which .rsdocx is held
    /// to.</summary>
    private const double OnFace = 5e-9;

    /// <summary>
    /// A curve for every edge (in the faces' vertex numbers, from the lower to the higher) that lies on all the faces
    /// along it: an edge between flat faces straight; one along a curved face straight when that lies on it (a
    /// cylinder's side), else a circle or an ellipse the surfaces give (a cylinder's, sphere's or torus's circles, a
    /// plane's cut through a cylinder or sphere), or else the circle through it and a point next to it; next to a
    /// triangle of a face not read exactly, straight only (an arc could bulge across the triangle). An edge nothing
    /// fits (where two pipes meet at a slant: no circle nor ellipse) straight after all.
    /// </summary>
    private static Dictionary<(int, int), BrepCurve> EdgeCurves(List<(BrepSurface? Surface, bool Reversed, int[][] Loops, List<(Vec3, Vec3, Vec3)>? Shown, bool Facet, uint? Color)> faces,
        List<Vec3> verts, Func<int[], Vec3> newell)
    {
        // per edge the faces along it and the points next to it in their loops
        var along = new Dictionary<(int, int), (List<int> Faces, List<int> Next)>();
        for (int f = 0; f < faces.Count; f++)
            foreach (var l in faces[f].Loops)
                for (int k = 0; k < l.Length; k++)
                {
                    int v = l[k], w = l[(k + 1) % l.Length];
                    var key = v < w ? (v, w) : (w, v);
                    if (!along.TryGetValue(key, out var a)) along[key] = a = ([], []);
                    a.Faces.Add(f);
                    if (l.Length > 3) { a.Next.Add(l[(k + l.Length - 1) % l.Length]); a.Next.Add(l[(k + 2) % l.Length]); }
                }
        var planes = new Dictionary<int, (Vec3 O, Vec3 N)>();
        (Vec3 O, Vec3 N) Plane(int f)
        {
            if (!planes.TryGetValue(f, out var p)) planes[f] = p = (verts[faces[f].Loops[0][0]], newell(faces[f].Loops[0]).Normalized());
            return p;
        }
        // (a NURBS surface's parameters at its faces' vertices, each from the one before it round the loop (a search
        // all over the surface for the first only): from there those of points along its edges)
        var near = new Dictionary<(BrepNurbsSurface, int), (double, double)>();
        foreach (var (surface, _, loops, _, _, _) in faces)
            if (surface is BrepNurbsSurface ns)
                foreach (var l in loops)
                {
                    (double, double)? last = null;
                    foreach (int v in l)
                    {
                        var uv = last is { } prev ? ns.Parameters(verts[v], prev, OnFace) : ns.Parameters(verts[v]);
                        near.TryAdd((ns, v), uv);
                        last = uv;
                    }
                }
        double OffAt(BrepSurface s, Vec3 p, int from)
        {
            if (s is not BrepNurbsSurface n) return Off(s, p);
            if (!near.TryGetValue((n, from), out var uv)) near[(n, from)] = uv = n.Parameters(verts[from]);
            // (Newton steps only, however far they end: a point near the vertex on the surface they find; one off it
            // needs no search for how far)
            var (u, v) = n.Near(p, uv);
            return (n.At(u, v) - p).Length;
        }
        var curves = new Dictionary<(int, int), BrepCurve>(along.Count);
        foreach (var (key, (fs, next)) in along)
        {
            if (fs.All(f => faces[f].Surface == null)) { curves[key] = new BrepLine(); continue; }
            var (a, b) = (verts[key.Item1], verts[key.Item2]);
            var surfaces = fs.Where(f => faces[f].Surface != null).Select(f => faces[f].Surface!).Distinct().ToList();
            var flats = fs.Where(f => faces[f].Surface == null).Select(Plane).ToList();
            // (next to a triangle of a face not read exactly only straight: an arc could bulge across it)
            bool facets = fs.Any(f => faces[f].Facet);
            // (a circle or ellipse in a flat face's plane, as far off it as its own ends at most)
            double slack = flats.Count == 0 ? 0 : flats.Max(p => Math.Max(Math.Abs((a - p.O).Dot(p.N)), Math.Abs((b - p.O).Dot(p.N))));
            bool Fits(Func<double, Vec3> at, bool straight) =>
                new[] { 0.25, 0.5, 0.75 }.All(t =>
                {
                    var p = at(t);
                    return surfaces.All(s => OffAt(s, p, key.Item1) <= OnFace) && (straight || flats.All(q => Math.Abs((p - q.O).Dot(q.N)) <= OnFace + slack));
                });
            BrepCurve? found = null;
            if (Fits(t => a + (b - a) * t, true)) found = new BrepLine();
            else if (!facets)
                foreach (var c in Candidates(surfaces, flats, a, b, next.Select(i => verts[i])))
                    if (Arc(c, a, b) is var (curve, at) && Fits(at, false)) { found = curve; break; }
            curves[key] = found ?? new BrepLine();
        }
        return curves;
    }

    /// <summary>A sphere face whose edges run through a pole of its sphere (a ball in two halves): its sphere with the
    /// pole in the face's middle instead, where its edges do not come (at a pole a face's way round is not told).</summary>
    private static BrepSphere Pole(BrepSphere sp, IEnumerable<Vec3> corners, List<(Vec3, Vec3, Vec3)> shown)
    {
        var ax = sp.Axis.Normalized();
        var poles = new[] { sp.Centre + ax * sp.Radius, sp.Centre - ax * sp.Radius };
        if (!corners.Any(c => poles.Any(p => (p - c).Length < 1e-6 * sp.Radius))) return sp;
        var m = shown.Aggregate(new Vec3(0, 0, 0), (n, t) => n + (t.Item2 - t.Item1).Cross(t.Item3 - t.Item1));
        if (m.Length < 1e-12) return sp;
        var axis = m.Normalized();
        var x = Math.Abs(axis.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        return sp with { Axis = axis, XAxis = (x - axis * x.Dot(axis)).Normalized() };
    }

    /// <summary>
    /// A body as .rsdocx can hold it, with its faces' colours: a curved face with a straight edge off its surface (from
    /// Revit, where no circle nor ellipse fits it: see EdgeCurves) as the triangles it is shown with, each a flat face of
    /// its colour; the rest as it is.
    /// </summary>
    internal (BrepBody Body, IReadOnlyDictionary<int, uint> Colors) Saveable(int b, IReadOnlyDictionary<int, uint> colors)
    {
        var body = Bodies[b];
        var shown = b < Curved.Count ? Curved[b] : null;
        if (shown == null || shown.Count == 0) return (body, colors);
        // (a straight edge off a curved surface: its midpoint not on it)
        bool Off(BrepFace f) => f.Loops.SelectMany(l => l).Any(c =>
        {
            var e = body.Edges[c.Edge];
            if (e.Curve is not BrepLine) return false;
            var (p, q) = (body.Vertices[e.Start], body.Vertices[e.End]);
            var mid = (p + q) * 0.5;
            switch (f.Surface)
            {
                case BrepNurbsSurface n:
                    var (u, v) = n.Near(mid, n.Parameters(p));
                    return (n.At(u, v) - mid).Length > OnFace;
                case BrepCylinder or BrepSphere or BrepTorus:
                    return SketchDocument.Off(f.Surface, mid) > OnFace;
                default:
                    return false;
            }
        });
        bool IsCurved(int f) => shown.ContainsKey(f) && body.Faces[f].Surface is not BrepPlane;
        var off = Enumerable.Range(0, body.Faces.Count).Where(f => IsCurved(f) && Off(body.Faces[f])).ToHashSet();
        if (off.Count == 0) return (body, colors);
        // (a curved face beside one of those across a circle or ellipse too: their triangles meet along straight lines;
        // a flat face beside one takes the line for its circle or ellipse)
        var facesOf = new Dictionary<int, List<int>>();
        for (int f = 0; f < body.Faces.Count; f++)
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
            {
                if (!facesOf.TryGetValue(c.Edge, out var l)) facesOf[c.Edge] = l = [];
                l.Add(f);
            }
        var todo = new Stack<int>(off);
        while (todo.Count > 0)
            foreach (var c in body.Faces[todo.Pop()].Loops.SelectMany(l => l))
                if (body.Edges[c.Edge].Curve is not BrepLine)
                    foreach (int g in facesOf[c.Edge])
                        if (IsCurved(g) && off.Add(g)) todo.Push(g);
        var edges = body.Edges.ToList();
        foreach (int f in off)
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
                if (edges[c.Edge].Curve is not BrepLine && edges[c.Edge].Start >= 0) edges[c.Edge] = edges[c.Edge] with { Curve = new BrepLine() };
        // (a triangle's corner on the face's border is a vertex there, to a few nanometres)
        var verts = body.Vertices.ToList();
        var cells = new Dictionary<(long, long, long), int>();
        static (long, long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X * 1e8), (long)Math.Floor(p.Y * 1e8), (long)Math.Floor(p.Z * 1e8));
        for (int v = 0; v < verts.Count; v++) cells.TryAdd(Cell(verts[v]), v);
        int V(Vec3 p)
        {
            var (x, y, z) = Cell(p);
            for (long i = x - 1; i <= x + 1; i++)
                for (long j = y - 1; j <= y + 1; j++)
                    for (long k = z - 1; k <= z + 1; k++)
                        if (cells.TryGetValue((i, j, k), out int known) && (verts[known] - p).Length <= 5e-9) return known;
            cells.TryAdd((x, y, z), verts.Count);
            verts.Add(p);
            return verts.Count - 1;
        }
        var straight = new Dictionary<(int, int), int>();
        for (int e = 0; e < edges.Count; e++)
            if (edges[e].Curve is BrepLine && edges[e].Start >= 0) straight.TryAdd((Math.Min(edges[e].Start, edges[e].End), Math.Max(edges[e].Start, edges[e].End)), e);
        // (a triangle's side on the face's border its edge there, the face's own: two edges can join the same two
        // vertices, at a seam)
        var border = new Dictionary<(int, int), int>();
        BrepCoedge Side(int v, int w)
        {
            var key = (Math.Min(v, w), Math.Max(v, w));
            if (!border.TryGetValue(key, out int e) && !straight.TryGetValue(key, out e)) { straight[key] = e = edges.Count; edges.Add(new BrepEdge(key.Item1, key.Item2, new BrepLine())); }
            return new BrepCoedge(e, edges[e].Start == v);
        }
        // (the triangles first; two on the same corners, where faces overlap (a fin of no thickness, as where tori
        // meet at a point), both let go when they face apart, one when they face alike)
        var triangles = new List<(int Face, int A, int C, int D, Vec3 At, Vec3 N)>();
        foreach (int f in off.Order())
            foreach (var (a, c, d) in shown[f])
            {
                int ia = V(a), ic = V(c), id = V(d);
                var n = (c - a).Cross(d - a);
                if (ia != ic && ic != id && id != ia && n.Length >= 1e-18) triangles.Add((f, ia, ic, id, a, n));
            }
        var drop = new HashSet<int>();
        var met = new Dictionary<(int, int, int), (int Index, bool Turn)>();
        for (int t = 0; t < triangles.Count; t++)
        {
            var (_, x, y, z, _, _) = triangles[t];
            // (turned so its smallest corner is first: which way round the other two follow)
            var (p1, p2) = x < y && x < z ? (y, z) : y < z ? (z, x) : (x, y);
            bool turn = p1 < p2;
            var key = (Math.Min(x, Math.Min(y, z)), Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z)), Math.Max(x, Math.Max(y, z)));
            if (!met.TryGetValue(key, out var first)) { met[key] = (t, turn); continue; }
            drop.Add(t);
            if (first.Turn != turn) { drop.Add(first.Index); met.Remove(key); }
        }
        var trianglesOf = Enumerable.Range(0, triangles.Count).Where(t => !drop.Contains(t)).ToLookup(t => triangles[t].Face);
        var faces = new List<BrepFace>();
        var newColors = new Dictionary<int, uint>();
        for (int f = 0; f < body.Faces.Count; f++)
        {
            bool painted = colors.TryGetValue(f, out uint color);
            if (!off.Contains(f))
            {
                if (painted) newColors[faces.Count] = color;
                faces.Add(body.Faces[f]);
                continue;
            }
            border.Clear();
            foreach (var c in body.Faces[f].Loops.SelectMany(l => l))
                if (edges[c.Edge].Start >= 0) border[(Math.Min(edges[c.Edge].Start, edges[c.Edge].End), Math.Max(edges[c.Edge].Start, edges[c.Edge].End))] = c.Edge;
            foreach (int t in trianglesOf[f])
            {
                var (_, ia, ic, id, at, n) = triangles[t];
                if (painted) newColors[faces.Count] = color;
                var (nn, x) = (n.Normalized(), verts[ic] - at);
                faces.Add(new BrepFace(new BrepPlane(at, nn, (x - nn * x.Dot(nn)).Normalized()), false, [[Side(ia, ic), Side(ic, id), Side(id, ia)]]));
            }
        }
        // (the edges and vertices no face uses any more let go, the rest numbered anew)
        var used = faces.SelectMany(f => f.Loops).SelectMany(l => l).Select(c => c.Edge).ToHashSet();
        var renumber = new Dictionary<int, int>();
        var kept = new List<BrepEdge>();
        var vertexOf = new Dictionary<int, int>();
        var keptVerts = new List<Vec3>();
        int Kept(int v)
        {
            if (v < 0) return v;
            if (!vertexOf.TryGetValue(v, out int k)) { vertexOf[v] = k = keptVerts.Count; keptVerts.Add(verts[v]); }
            return k;
        }
        for (int e = 0; e < edges.Count; e++)
            if (used.Contains(e)) { renumber[e] = kept.Count; kept.Add(edges[e] with { Start = Kept(edges[e].Start), End = Kept(edges[e].End) }); }
        faces = faces.Select(f => f with
        {
            Loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c => c with { Edge = renumber[c.Edge] }).ToList()).ToList(),
            Points = f.Points.Select(Kept).ToList(),
        }).ToList();
        // (an edge of more than two triangles, where faces touch along it: an edge of its own for every use, a seam,
        // and the body without thickness)
        var uses = new Dictionary<int, int>();
        foreach (var c in faces.SelectMany(f => f.Loops).SelectMany(l => l)) uses[c.Edge] = uses.GetValueOrDefault(c.Edge) + 1;
        bool sheet = body.Sheet;
        if (uses.Any(u => u.Value > 2))
        {
            sheet = true;
            var taken = new HashSet<int>();
            faces = faces.Select(f => f with
            {
                Loops = f.Loops.Select(l => (IReadOnlyList<BrepCoedge>)l.Select(c =>
                {
                    if (uses[c.Edge] <= 2 || taken.Add(c.Edge)) return c;
                    kept.Add(kept[c.Edge]);
                    return c with { Edge = kept.Count - 1 };
                }).ToList()).ToList(),
            }).ToList();
        }
        return (new BrepBody { Vertices = keptVerts, Edges = kept, Faces = faces, Sheet = sheet }, newColors);
    }

    /// <summary>How far a point is off a curved surface (a cone, which Revit's are not read as, or a NURBS surface, which
    /// EdgeCurves measures from a vertex's parameters: never on it).</summary>
    private static double Off(BrepSurface s, Vec3 p)
    {
        switch (s)
        {
            case BrepCylinder c:
            {
                var ax = c.Axis.Normalized(); var d = p - c.Origin;
                return Math.Abs((d - ax * d.Dot(ax)).Length - c.Radius);
            }
            case BrepSphere sp: return Math.Abs((p - sp.Centre).Length - sp.Radius);
            case BrepTorus t:
            {
                var ax = t.Axis.Normalized(); var d = p - t.Centre; double h = d.Dot(ax);
                double r = (d - ax * h).Length - t.MajorRadius;
                return Math.Abs(Math.Sqrt(r * r + h * h) - t.MinorRadius);
            }
            case BrepPlane pl: return Math.Abs((p - pl.Origin).Dot(pl.Normal.Normalized()));
            default: return double.MaxValue;
        }
    }

    /// <summary>A circle (Ellipse null) or ellipse that may carry an edge: centre, normal, major axis, radii.</summary>
    private readonly record struct Conic(Vec3 Centre, Vec3 Normal, Vec3 XAxis, double Radius, double? Minor = null);

    /// <summary>The circles and ellipses an edge from a to b may be on: those of its curved surfaces (and of their cuts
    /// by its flat faces), then those through a, b and a point next to them.</summary>
    private static IEnumerable<Conic> Candidates(List<BrepSurface> surfaces, List<(Vec3 O, Vec3 N)> flats, Vec3 a, Vec3 b, IEnumerable<Vec3> next)
    {
        static Vec3 Any(Vec3 n) { var x = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0); return (x - n * x.Dot(n)).Normalized(); }
        // (the circle round an axis through a: its parallel)
        static Conic Round(Vec3 o, Vec3 ax, Vec3 a) { var c = o + ax * (a - o).Dot(ax); return new Conic(c, ax, Any(ax), (a - c).Length); }
        foreach (var s in surfaces)
            switch (s)
            {
                case BrepCylinder cy:
                {
                    var ax = cy.Axis.Normalized();
                    yield return Round(cy.Origin, ax, a) with { Radius = cy.Radius };
                    foreach (var (o, n) in flats)
                    {
                        // (a plane's slanting cut: an ellipse, its minor radius the cylinder's, its major along the axis's slope)
                        double cos = n.Dot(ax);
                        if (Math.Abs(cos) < 1e-9 || Math.Abs(cos) > 1 - 1e-12) continue;
                        var centre = cy.Origin + ax * ((o - cy.Origin).Dot(n) / cos);
                        var major = (ax - n * cos).Normalized();
                        yield return new Conic(centre, n, major, cy.Radius / Math.Abs(cos), cy.Radius);
                    }
                    break;
                }
                case BrepSphere sp:
                {
                    var ax = sp.Axis.Normalized();
                    yield return Round(sp.Centre, ax, a);
                    var g = (a - sp.Centre).Cross(b - sp.Centre);
                    if (g.Length > 1e-15) yield return new Conic(sp.Centre, g.Normalized(), (a - sp.Centre).Normalized(), sp.Radius);
                    foreach (var (o, n) in flats)
                    {
                        var c = sp.Centre - n * (sp.Centre - o).Dot(n);
                        yield return new Conic(c, n, Any(n), (a - c).Length);
                    }
                    break;
                }
                case BrepTorus t:
                {
                    var ax = t.Axis.Normalized();
                    yield return Round(t.Centre, ax, a);
                    var d = a - t.Centre; var r = d - ax * d.Dot(ax);
                    if (r.Length < 1e-15) break;
                    var tube = t.Centre + r.Normalized() * t.MajorRadius;
                    var mn = ax.Cross(r.Normalized());
                    yield return new Conic(tube, mn, ax, t.MinorRadius);
                    break;
                }
            }
        foreach (var c in next)
        {
            // (the circle through a, b and c)
            var (u, v) = (b - a, c - a);
            var w = u.Cross(v);
            double w2 = w.Dot(w);
            if (w2 < 1e-30) continue;
            var centre = a + (v.Cross(w) * u.Dot(u) + w.Cross(u) * v.Dot(v)) * (1 / (2 * w2));
            yield return new Conic(centre, w.Normalized(), (a - centre).Normalized(), (a - centre).Length);
        }
    }

    /// <summary>A conic as the edge from a to b: the curve (its normal turned so that it runs from a to b the short way
    /// round) and its points along that way; null when a or b is not on it.</summary>
    private static (BrepCurve Curve, Func<double, Vec3> At)? Arc(Conic c, Vec3 a, Vec3 b)
    {
        var n = c.Normal.Normalized(); var x = c.XAxis.Normalized();
        double rx = c.Radius, ry = c.Minor ?? c.Radius;
        if (rx <= 0 || ry <= 0) return null;
        double Angle(Vec3 p, Vec3 y) { var d = p - c.Centre; return Math.Atan2(d.Dot(y) / ry, d.Dot(x) / rx); }
        Vec3 Point(double t, Vec3 y) => c.Centre + x * (rx * Math.Cos(t)) + y * (ry * Math.Sin(t));
        var yAxis = n.Cross(x);
        double t0 = Angle(a, yAxis), t1 = Angle(b, yAxis);
        if ((Point(t0, yAxis) - a).Length > OnFace || (Point(t1, yAxis) - b).Length > OnFace) return null;
        double sweep = t1 - t0;
        while (sweep <= -Math.PI) sweep += 2 * Math.PI;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;
        if (sweep < 0)
        {
            // (the other way round: the normal turned, the angles mirrored)
            n = n * -1; yAxis = yAxis * -1; t0 = -t0; sweep = -sweep;
        }
        var (fy, f0, fs) = (yAxis, t0, sweep);
        BrepCurve curve = c.Minor is { } minor ? new BrepEllipse(c.Centre, n, x, rx, minor) : new BrepCircle(c.Centre, n, x, rx);
        return (curve, t => Point(f0 + fs * t, fy));
    }

    /// <summary>A curved surface's natural normal at a point on it (for a NURBS surface: dS/du × dS/dv, from nearby
    /// parameters).</summary>
    private static Func<Vec3, Vec3> Outward(BrepSurface s) => s switch
    {
        BrepCylinder c => p => { var d = p - c.Origin; return d - c.Axis * d.Dot(c.Axis); },
        BrepSphere sp => p => p - sp.Centre,
        BrepTorus t => p => { var d = p - t.Centre; var r = d - t.Axis * d.Dot(t.Axis); return p - (t.Centre + r.Normalized() * t.MajorRadius); },
        BrepNurbsSurface n => p =>
        {
            var (u, v) = n.Parameters(p);
            double u0 = n.FullU[n.UDegree], u1 = n.FullU[^(n.UDegree + 1)], v0 = n.FullV[n.VDegree], v1 = n.FullV[^(n.VDegree + 1)];
            double h = 1e-5;
            var du = n.At(Math.Min(u + h, u1), v) - n.At(Math.Max(u - h, u0), v);
            var dv = n.At(u, Math.Min(v + h, v1)) - n.At(u, Math.Max(v - h, v0));
            return du.Cross(dv);
        },
        _ => _ => new Vec3(0, 0, 0),
    };
}

/// <summary>Why a Revit file cannot be opened (shown to the user as it is).</summary>
public sealed class RevitReadException(string message) : Exception(message);
