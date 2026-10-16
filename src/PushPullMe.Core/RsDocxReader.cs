using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;
using RsDocx.Core.Xt;

namespace PushPullMe.Core;

/// <summary>Why an .rsdocx cannot be opened (shown to the user as it is).</summary>
public sealed class RsDocxReadException(string message) : Exception(message);

/// <summary>
/// Reads the solid bodies of a DesignSpark file (.rsdocx: a package with the geometry as Parasolid XT, in metres) back
/// into <see cref="BrepBody"/>s: the mirror image of what <see cref="XtBrepBuilder"/> writes. Planes, cylinders, cones,
/// spheres and tori; lines, circles, ellipses and B-curves. Free-form surfaces (B-surfaces) not (yet).
/// </summary>
public static class RsDocxReader
{
    public static List<BrepBody> Read(string path) => Read(path, out _);

    /// <summary>All bodies; <paramref name="hidden"/>: how many of them are hidden in DesignSpark (they are read all the same).</summary>
    public static List<BrepBody> Read(string path, out int hidden) => Read(path, out hidden, out _);

    /// <summary>As <see cref="Read(string, out int)"/>, with the colours DesignSpark gave the faces (per body, by face
    /// index; a body's colour on every face that has none of its own).</summary>
    public static List<BrepBody> Read(string path, out int hidden, out List<Dictionary<int, uint>> colors)
    {
        hidden = 0;
        RsDocxPackage pkg;
        try { pkg = RsDocxPackage.Open(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new RsDocxReadException(Lang.T("Dit is geen leesbaar .rsdocx-bestand: ", "This is not a readable .rsdocx file: ") + e.Message); }
        if (!pkg.Contains(RsDocxComposer.GeometryPart)) throw new RsDocxReadException(Lang.T("Er staat geen geometrie in dit bestand.", "There is no geometry in this file."));
        XtFile xt;
        try { xt = XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart)); }
        catch (XtFormatException e) { throw new RsDocxReadException(Lang.T("De geometrie in dit bestand is niet te lezen: ", "The geometry in this file cannot be read: ") + e.Message); }
        var hide = new HashSet<string>();
        if (pkg.Contains("SpaceClaim/document.xml"))
            try { hide = HiddenIds(pkg.GetXml("SpaceClaim/document.xml")); } catch (System.Xml.XmlException) { }
        // all bodies are read, also the ones hidden in DesignSpark (only counted, so the user can be told)
        var all = Bodies(xt, out var names, out var nodes);
        hidden = names.Count(nm => nm is { } s && s.Split(':') is [_, var id] && hide.Contains(id));
        var map = xt.ByIndex();
        var faceNames = nodes.Select(n => (IReadOnlyList<string?>)RsDocxColors.Faces(map, n).Select(f => XtTopology.NameOf(map, f)).ToList()).ToList();
        colors = RsDocxColors.ForFaces(pkg, names, faceNames);
        return all;
    }

    /// <summary>
    /// The ids (the number after the colon of a moniker such as "3:666") DesignSpark has hidden: in document.xml an
    /// AttributeTableDef has id ranges ("index id id …"), attributes (a VisibilityAttribute with visible False) and keys
    /// ("-1 attribute range range …") tying attributes to ranges.
    /// </summary>
    private static HashSet<string> HiddenIds(System.Xml.Linq.XDocument doc)
    {
        var hidden = new HashSet<string>();
        static IEnumerable<System.Xml.Linq.XElement> Kids(System.Xml.Linq.XElement? e, string name) =>
            e?.Elements().Where(x => x.Name.LocalName == name) ?? [];
        foreach (var table in doc.Descendants().Where(e => e.Name.LocalName == "AttributeTableDef"))
        {
            var ranges = new Dictionary<string, string[]>();
            foreach (var it in Kids(Kids(table, "idRanges").FirstOrDefault(), "item"))
                if (it.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var idx, .. var ids]) ranges[idx] = ids;
            var attrs = Kids(Kids(table, "attributeIndex").FirstOrDefault(), "item").ToList();
            foreach (var key in Kids(Kids(table, "keys").FirstOrDefault(), "item"))
            {
                var parts = key.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !int.TryParse(parts[1], out int a) || a < 0 || a >= attrs.Count) continue;
                var attr = attrs[a];
                bool invisible = ((string?)attr.Attribute("sctype") ?? "").Contains("VisibilityAttribute")
                    && Kids(attr, "visible").FirstOrDefault()?.Value.Trim().Equals("False", StringComparison.OrdinalIgnoreCase) == true;
                if (!invisible) continue;
                foreach (var r in parts.Skip(2))
                    if (ranges.TryGetValue(r, out var ids)) hidden.UnionWith(ids);
            }
        }
        return hidden;
    }

    public static List<BrepBody> Bodies(XtFile file) => Bodies(file, out _);

    /// <summary>The bodies, with their names (the SDL/TYSA_NAME moniker, or null).</summary>
    public static List<BrepBody> Bodies(XtFile file, out List<string?> names) => Bodies(file, out names, out _);

    /// <summary>The bodies, with their names and their nodes in the geometry.</summary>
    public static List<BrepBody> Bodies(XtFile file, out List<string?> names, out List<XtNode> nodes)
    {
        names = new List<string?>();
        nodes = new List<XtNode>();
        var map = file.ByIndex();
        var root = file.Nodes[0];
        IEnumerable<int> bodyIdx = root.NodeType switch
        {
            XtNodeTypes.Body => [root.Index],
            XtNodeTypes.PartXmtBlock or XtNodeTypes.PointerLisBlock => root.Array("entries").Cast<int>().Take(root.Int("n_entries")),
            _ => throw new RsDocxReadException(Lang.T($"Onbekende opbouw van de geometrie (knoop {root.NodeType}).", $"Unknown structure of the geometry (node {root.NodeType}).")),
        };
        var result = new List<BrepBody>();
        foreach (int bi in bodyIdx)
            if (map.TryGetValue(bi, out var b) && b.NodeType == XtNodeTypes.Body)
            {
                result.Add(Body(map, b));
                names.Add(XtTopology.NameOf(map, b));
                nodes.Add(b);
            }
        if (result.Count == 0) throw new RsDocxReadException(Lang.T("Er staan geen vaste lichamen in dit bestand.", "There are no solid bodies in this file."));
        return result;
    }

    private static BrepBody Body(Dictionary<int, XtNode> map, XtNode body)
    {
        var verts = new List<Vec3>();
        var vIndex = new Dictionary<int, int>();
        foreach (var v in XtTopology.Chain(map, body.Ptr("vertex"), "next"))
        {
            vIndex[v.Index] = verts.Count;
            verts.Add(V(map[v.Ptr("point")].Vec("pvec")));
        }
        int Vx(int node) => node == 0 ? -1 : vIndex[node];

        var edges = new List<BrepEdge>();
        var eIndex = new Dictionary<int, int>();
        bool sheet = false;
        foreach (var e in XtTopology.Chain(map, body.Ptr("edge"), "next"))
        {
            // the edge's fin is its positive fin: its vertex is the edge's end; the other fin's vertex its start
            var fin = map[e.Ptr("fin")];
            int other = fin.Ptr("other");
            // (a fin in no loop: the edge borders one face only, of a face without thickness; DesignSpark's "Oppervlak")
            if (fin.Ptr("loop") == 0 || (other != 0 && map[other].Ptr("loop") == 0)) sheet = true;
            int end = Vx(fin.Ptr("vertex")), start = other != 0 ? Vx(map[other].Ptr("vertex")) : end;
            int ci = e.Ptr("curve");
            if (ci == 0 && fin.Has("curve")) ci = fin.Ptr("curve");
            if (ci == 0 || !map.TryGetValue(ci, out var cn)) throw new RsDocxReadException(Lang.T("Een rand zonder kromme; dat kan PushPullMe nog niet lezen.", "An edge without a curve; PushPullMe cannot read that yet."));
            eIndex[e.Index] = edges.Count;
            edges.Add(new BrepEdge(start, end, Curve(map, cn, start >= 0 ? verts[start] : default, end >= 0 ? verts[end] : default)));
        }

        var faces = new List<BrepFace>();
        var seen = new HashSet<int>();
        foreach (var region in XtTopology.Chain(map, body.Ptr("region"), "next"))
            foreach (var shell in XtTopology.Chain(map, region.Ptr("shell"), "next"))
            {
                var fs = XtTopology.Chain(map, shell.Ptr("face"), "next").ToList();
                if (shell.Has("front_face")) fs.AddRange(XtTopology.Chain(map, shell.Ptr("front_face"), "next_front"));
                foreach (var f in fs)
                    if (seen.Add(f.Index)) faces.Add(Face(map, f, eIndex));
            }
        if (faces.Count == 0) throw new RsDocxReadException(Lang.T("Een lichaam zonder vlakken; dat kan PushPullMe niet openen.", "A body without faces; PushPullMe cannot open that."));
        return new BrepBody { Vertices = verts, Edges = edges, Faces = faces, Sheet = sheet };
    }

    private static BrepFace Face(Dictionary<int, XtNode> map, XtNode f, Dictionary<int, int> eIndex)
    {
        var sn = map[f.Ptr("surface")];
        var (surface, flipped) = Surface(sn);
        bool reversed = (f.Byte("sense") == '-') != flipped;
        var loops = new List<IReadOnlyList<BrepCoedge>>();
        foreach (var loop in XtTopology.Chain(map, f.Ptr("loop"), "next"))
        {
            var coedges = new List<BrepCoedge>();
            int first = loop.Ptr("fin"), cur = first, guard = 0;
            do
            {
                var fin = map[cur];
                coedges.Add(new BrepCoedge(eIndex[fin.Ptr("edge")], fin.Byte("sense") == '+'));
                cur = fin.Ptr("forward");
            } while (cur != first && cur != 0 && ++guard < 1_000_000);
            loops.Add(coedges);
        }
        return new BrepFace(surface, reversed, loops);
    }

    /// <summary>The surface, and whether its sense is '-' (its normal the other way round).</summary>
    private static (BrepSurface, bool Flipped) Surface(XtNode s)
    {
        bool flipped = s.Has("sense") && s.Byte("sense") == '-';
        BrepSurface surface = s.NodeType switch
        {
            XtNodeTypes.Plane => new BrepPlane(V(s.Vec("pvec")), V(s.Vec("normal")), V(s.Vec("x_axis"))),
            XtNodeTypes.Cylinder => new BrepCylinder(V(s.Vec("pvec")), V(s.Vec("axis")), V(s.Vec("x_axis")), s.Double("radius")),
            XtNodeTypes.Cone => new BrepCone(V(s.Vec("pvec")), V(s.Vec("axis")), V(s.Vec("x_axis")), s.Double("radius"),
                Math.Atan2(s.Double("sin_half_angle"), s.Double("cos_half_angle"))),
            XtNodeTypes.Sphere => new BrepSphere(V(s.Vec("centre")), s.Double("radius"), V(s.Vec("axis")), V(s.Vec("x_axis"))),
            XtNodeTypes.Torus => new BrepTorus(V(s.Vec("centre")), V(s.Vec("axis")), V(s.Vec("x_axis")), s.Double("major_radius"), s.Double("minor_radius")),
            _ => throw new RsDocxReadException(Lang.T("Dit bestand heeft vrije-vormvlakken; die kan PushPullMe nog niet openen.", "This file has free-form faces; PushPullMe cannot open those yet.")),
        };
        return (surface, flipped);
    }

    private static BrepCurve Curve(Dictionary<int, XtNode> map, XtNode c, Vec3 start, Vec3 end)
    {
        bool flipped = c.Has("sense") && c.Byte("sense") == '-';
        switch (c.NodeType)
        {
            case XtNodeTypes.Line:
                return new BrepLine();
            case XtNodeTypes.Circle:
            {
                var n = V(c.Vec("normal"));
                return new BrepCircle(V(c.Vec("centre")), flipped ? n * -1 : n, V(c.Vec("x_axis")), c.Double("radius"));
            }
            case XtNodeTypes.Ellipse:
            {
                var n = V(c.Vec("normal"));
                return new BrepEllipse(V(c.Vec("centre")), flipped ? n * -1 : n, V(c.Vec("x_axis")), c.Double("major_radius"), c.Double("minor_radius"));
            }
            case XtNodeTypes.BCurve:
            {
                if (flipped) throw new RsDocxReadException(Lang.T("Een omgekeerde B-kromme; dat kan PushPullMe nog niet lezen.", "A reversed B-curve; PushPullMe cannot read that yet."));
                var nurbs = map[c.Ptr("nurbs")];
                int degree = Convert.ToInt32(nurbs["degree"]), count = nurbs.Int("n_vertices"), dim = Convert.ToInt32(nurbs["vertex_dim"]);
                var raw = map[nurbs.Ptr("bspline_vertices")].Array("vertices").Select(Convert.ToDouble).ToArray();
                var pts = new List<Vec3>(); List<double>? weights = dim == 4 ? new() : null;
                for (int i = 0; i < count; i++)
                {
                    double w = dim == 4 ? raw[i * 4 + 3] : 1;
                    // (homogeneous: x·w, y·w, z·w, w)
                    pts.Add(new Vec3(raw[i * dim] / w, raw[i * dim + 1] / w, raw[i * dim + 2] / w));
                    weights?.Add(w);
                }
                var mults = map[nurbs.Ptr("knot_mult")].Array("mult").Select(Convert.ToInt32).ToList();
                var knots = map[nurbs.Ptr("knots")].Array("knots").Select(Convert.ToDouble).ToList();
                bool closed = nurbs.Has("closed") && Convert.ToInt32(nurbs["closed"]) != 0;
                return new BrepBSpline(degree, pts, knots, mults, closed, weights);
            }
            case 38:
                return Intersection(map, c, start, end);
            default:
                throw new RsDocxReadException(Lang.T($"Een rand met een kromme die PushPullMe nog niet kan lezen (knoop {c.NodeType}).", $"An edge with a curve that PushPullMe cannot read yet (node {c.NodeType})."));
        }
    }

    /// <summary>
    /// An intersection curve (XT node 38, as DesignSpark writes where two roundings cut each other: kubus_bewerkt_bewerkt):
    /// the curve where its two surfaces meet, stored as a coarse chart of points. Made into a B-spline from start to end:
    /// the chart spread evenly over 97 points, each put exactly on both surfaces (Newton), and a curve through them.
    /// </summary>
    private static BrepCurve Intersection(Dictionary<int, XtNode> map, XtNode c, Vec3 start, Vec3 end)
    {
        var surfs = c.Array("surface").Select(x => Convert.ToInt32(x)).Where(map.ContainsKey).Select(i => Surface(map[i]).Item1).ToList();
        if (surfs.Count != 2) throw new RsDocxReadException(Lang.T("Een snijkromme zonder twee vlakken; die kan PushPullMe nog niet lezen.", "An intersection curve without two faces; PushPullMe cannot read that yet."));
        // two cylinders (roundings that cut each other): exactly, round the thinner one
        if (surfs[0] is BrepCylinder ca && surfs[1] is BrepCylinder cb)
            try
            {
                return PushPull.CylinderCut(new PushPull.Cyl(ca.Origin, ca.Axis.Normalized(), ca.Radius), new PushPull.Cyl(cb.Origin, cb.Axis.Normalized(), cb.Radius), start, end);
            }
            catch (PushPullException) { }
        // the chart: points along the curve
        var guide = new List<Vec3>();
        if (c.Has("chart") && map.TryGetValue(c.Ptr("chart"), out var chart))
        {
            // (each point a vector, or all of them as one row of numbers)
            var items = chart.Array("hvec");
            if (items.All(x => x is double[]))
                foreach (double[] v in items.Cast<double[]>()) { if (v.Length >= 3) guide.Add(new Vec3(v[0], v[1], v[2])); }
            else
            {
                var raw = items.Select(Convert.ToDouble).ToArray();
                int count = chart.Has("chart_count") ? Convert.ToInt32(chart["chart_count"]) : 0;
                int dim = count > 0 && raw.Length % count == 0 ? raw.Length / count : 3;
                for (int i = 0; i + 2 < raw.Length; i += dim) guide.Add(new Vec3(raw[i], raw[i + 1], raw[i + 2]));
            }
        }
        if (guide.Count > 0 && (guide[0] - end).Length < (guide[0] - start).Length) guide.Reverse();
        guide.Insert(0, start); guide.Add(end);
        // spread evenly along it
        var len = new List<double> { 0 };
        for (int i = 1; i < guide.Count; i++) len.Add(len[^1] + (guide[i] - guide[i - 1]).Length);
        const int n = 96;
        var pts = new List<Vec3> { start };
        for (int k = 1; k < n; k++)
        {
            double at = len[^1] * k / n;
            int i = len.FindIndex(x => x >= at); if (i <= 0) i = 1;
            double f = (at - len[i - 1]) / Math.Max(len[i] - len[i - 1], 1e-30);
            pts.Add(OnBoth(surfs[0], surfs[1], guide[i - 1] + (guide[i] - guide[i - 1]) * f));
        }
        pts.Add(end);
        return BSpline.Interpolate(pts, false);
    }

    /// <summary>The point near p on both surfaces: Newton steps of least length on their two distances.</summary>
    private static Vec3 OnBoth(BrepSurface a, BrepSurface b, Vec3 p)
    {
        for (int it = 0; it < 50; it++)
        {
            double fa = Dist(a, p), fb = Dist(b, p);
            if (Math.Abs(fa) < 1e-13 && Math.Abs(fb) < 1e-13) break;
            Vec3 ga = Grad(a, p), gb = Grad(b, p);
            // Δ = −Jᵀ (J Jᵀ)⁻¹ f
            double m11 = ga.Dot(ga), m12 = ga.Dot(gb), m22 = gb.Dot(gb), det = m11 * m22 - m12 * m12;
            if (Math.Abs(det) < 1e-24) break;
            double l1 = (m22 * fa - m12 * fb) / det, l2 = (m11 * fb - m12 * fa) / det;
            p -= ga * l1 + gb * l2;
        }
        return p;
    }

    private static Vec3 Grad(BrepSurface s, Vec3 p)
    {
        const double h = 1e-7;
        return new Vec3(Dist(s, p + new Vec3(h, 0, 0)) - Dist(s, p - new Vec3(h, 0, 0)),
            Dist(s, p + new Vec3(0, h, 0)) - Dist(s, p - new Vec3(0, h, 0)),
            Dist(s, p + new Vec3(0, 0, h)) - Dist(s, p - new Vec3(0, 0, h))) * (1 / (2 * h));
    }

    /// <summary>Signed distance from p to a surface (plane, cylinder, sphere, torus).</summary>
    private static double Dist(BrepSurface s, Vec3 p)
    {
        switch (s)
        {
            case BrepPlane pl: return (p - pl.Origin).Dot(pl.Normal.Normalized());
            case BrepCylinder c: { var ax = c.Axis.Normalized(); var w = p - c.Origin; return (w - ax * w.Dot(ax)).Length - c.Radius; }
            case BrepSphere sp: return (p - sp.Centre).Length - sp.Radius;
            case BrepTorus t:
            {
                var ax = t.Axis.Normalized(); var w = p - t.Centre;
                var radial = w - ax * w.Dot(ax);
                var ring = t.Centre + radial.Normalized() * t.MajorRadius;
                return (p - ring).Length - t.MinorRadius;
            }
            default: throw new RsDocxReadException(Lang.T("Een snijkromme op een vlak dat PushPullMe nog niet kent.", "An intersection curve on a face that PushPullMe does not know yet."));
        }
    }

    private static Vec3 V(double[] a) => new(a[0], a[1], a[2]);
}
