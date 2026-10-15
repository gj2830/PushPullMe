using System.Globalization;
using System.Xml.Linq;
using RsDocx.Core;
using RsDocx.Core.Package;
using RsDocx.Core.Xt;

namespace PushPullMe.Core;

/// <summary>
/// The colours in a DesignSpark file (document.xml, as DesignSpark writes them, RsCOnverter testdata/kubus_kleuren.rsdocx of
/// 12 October 2026): a body's colour as <c>&lt;color&gt;R, G, B&lt;/color&gt;</c> in its NominalBodyDef (after its type), a face's
/// own as a <c>FaceColorDef</c> at the end of the PartDef, its <c>subjectId</c> the face's moniker (the SDL/TYSA_NAME
/// the face carries in the geometry). A face's own colour goes before its body's.
/// </summary>
public static class RsDocxColors
{
    private const string DocumentPart = "SpaceClaim/document.xml";

    /// <summary>The colours by moniker: of bodies, and of faces.</summary>
    public static (Dictionary<string, uint> Bodies, Dictionary<string, uint> Faces) Read(XDocument doc)
    {
        var bodies = new Dictionary<string, uint>(); var faces = new Dictionary<string, uint>();
        foreach (var b in doc.Descendants().Where(e => e.Name.LocalName == "NominalBodyDef"))
            if ((string?)b.Attribute("Id") is { } id && Child(b, "color") is { } c && Parse(c.Value) is { } col) bodies[id] = col;
        foreach (var f in doc.Descendants().Where(e => e.Name.LocalName == "FaceColorDef"))
            if (Child(f, "subjectId")?.Value.Trim() is { Length: > 0 } subject && Child(f, "color") is { } c && Parse(c.Value) is { } col) faces[subject] = col;
        return (bodies, faces);
    }

    /// <summary>
    /// Per body (in the order of the geometry) the colour of each face (by index), from the package's document.xml:
    /// <paramref name="bodyNames"/> and <paramref name="faceNames"/> are the monikers of the bodies and their faces.
    /// </summary>
    public static List<Dictionary<int, uint>> ForFaces(RsDocxPackage pkg, IReadOnlyList<string?> bodyNames, IReadOnlyList<IReadOnlyList<string?>> faceNames)
    {
        var result = bodyNames.Select(_ => new Dictionary<int, uint>()).ToList();
        if (!pkg.Contains(DocumentPart)) return result;
        XDocument doc;
        try { doc = pkg.GetXml(DocumentPart); } catch (System.Xml.XmlException) { return result; }
        var (bodies, faces) = Read(doc);
        for (int b = 0; b < bodyNames.Count; b++)
        {
            uint? bodyColor = bodyNames[b] is { } bn && bodies.TryGetValue(bn, out var bc) ? bc : null;
            for (int f = 0; f < faceNames[b].Count; f++)
                if (faceNames[b][f] is { } fn && faces.TryGetValue(fn, out var fc)) result[b][f] = fc;
                else if (bodyColor is { } c) result[b][f] = c;
        }
        return result;
    }

    /// <summary>
    /// The colours written into a package made from these bodies (its geometry carries the monikers): a body whose every
    /// face has the same colour gets that as its body colour; otherwise every painted face gets a FaceColorDef.
    /// </summary>
    public static void Write(RsDocxPackage pkg, IReadOnlyList<IReadOnlyDictionary<int, uint>> colors)
    {
        if (colors.All(c => c.Count == 0) || !pkg.Contains(DocumentPart)) return;
        var xt = XtReader.Read(pkg.GetPart(RsDocxComposer.GeometryPart));
        var map = xt.ByIndex();
        var bodyNodes = xt.Nodes.Where(n => n.NodeType == XtNodeTypes.Body).ToList();
        var doc = pkg.GetXml(DocumentPart);
        var bodyDefs = doc.Descendants().Where(e => e.Name.LocalName == "NominalBodyDef").ToDictionary(e => (string?)e.Attribute("Id") ?? "");
        var part = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PartDef");
        if (part == null) return;
        // new monikers: a number before the colon not used yet (as DesignSpark starts a new one for what it adds)
        int prefix = doc.Descendants().Select(e => (string?)e.Attribute("Id")).Where(id => id != null && id.Contains(':'))
            .Select(id => int.TryParse(id!.Split(':')[0], out int p) ? p : 0).DefaultIfEmpty(0).Max() + 1;
        int next = 1;
        var ns = part.Name.Namespace;
        for (int b = 0; b < colors.Count && b < bodyNodes.Count; b++)
        {
            var body = bodyNodes[b];
            string? bodyName = XtTopology.NameOf(map, body);
            var faceNodes = Faces(map, body);
            var painted = colors[b];
            if (painted.Count == 0) continue;
            bool whole = faceNodes.Count > 0 && Enumerable.Range(0, faceNodes.Count).All(f => painted.TryGetValue(f, out var c) && c == painted[0]);
            if (whole && bodyName != null && bodyDefs.TryGetValue(bodyName, out var def))
            {
                var color = new XElement(def.Name.Namespace + "color", Format(painted[0]));
                (Child(def, "type") ?? def.Elements().First()).AddAfterSelf(color);
                continue;
            }
            foreach (var (f, c) in painted.OrderBy(kv => kv.Key))
            {
                if (f >= faceNodes.Count || XtTopology.NameOf(map, faceNodes[f]) is not { } faceName) continue;
                string id = $"{prefix}:{next++}";
                part.Add(new XElement(ns + "FaceColorDef", new XAttribute("Id", id),
                    new XElement(ns + "updateState", id), new XElement(ns + "subjectId", faceName), new XElement(ns + "color", Format(c))));
            }
        }
        pkg.SetXml(DocumentPart, doc);
    }

    /// <summary>A body's faces in the order the reader numbers them (all faces of all shells of all regions).</summary>
    public static List<XtNode> Faces(Dictionary<int, XtNode> map, XtNode body)
    {
        var faces = new List<XtNode>();
        var seen = new HashSet<int>();
        foreach (var region in XtTopology.Chain(map, body.Ptr("region"), "next"))
            foreach (var shell in XtTopology.Chain(map, region.Ptr("shell"), "next"))
            {
                var fs = XtTopology.Chain(map, shell.Ptr("face"), "next").ToList();
                if (shell.Has("front_face")) fs.AddRange(XtTopology.Chain(map, shell.Ptr("front_face"), "next_front"));
                foreach (var f in fs) if (seen.Add(f.Index)) faces.Add(f);
            }
        return faces;
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    /// <summary>"R, G, B" (0..255) as ARGB, opaque.</summary>
    private static uint? Parse(string s)
    {
        var p = s.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 3 || !byte.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b)) return null;
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static string Format(uint c) => string.Create(CultureInfo.InvariantCulture, $"{(c >> 16) & 0xFF}, {(c >> 8) & 0xFF}, {c & 0xFF}");
}
