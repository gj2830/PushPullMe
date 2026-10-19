using System.Globalization;
using System.Xml.Linq;
using RsDocx.Core.Package;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>
/// Loose lines in a DesignSpark file (document.xml, as DesignSpark writes them, RsCOnverter testdata/kubus.rsdocx of
/// 14 October 2026): a <c>PartSketchCurveContainerDef</c> (namespace urn:sketch) in the PartDef, before its bodies, with a
/// <c>SketchCurveDef</c> per curve: a <c>LineSegment</c> (start, dir, length) or an <c>Arc</c> (center, start, end, axis;
/// counter-clockwise round the axis), <c>isConstruction</c>, <c>isInfinite</c>. Metres, numbers separated by spaces.
/// </summary>
public static class RsDocxSketch
{
    private const string DocumentPart = "SpaceClaim/document.xml";
    private static readonly XNamespace Sketch = "urn:sketch", Nom = "urn:nom", Core = "urn:core";

    /// <summary>The loose lines and arcs of the package (curves of other kinds, and infinite lines, are left out).</summary>
    public static List<SketchLine> Read(RsDocxPackage pkg)
    {
        var result = new List<SketchLine>();
        if (!pkg.Contains(DocumentPart)) return result;
        XDocument doc;
        try { doc = pkg.GetXml(DocumentPart); } catch (System.Xml.XmlException) { return result; }
        foreach (var def in doc.Descendants().Where(e => e.Name.LocalName == "SketchCurveDef"))
        {
            if (Child(def, "isInfinite")?.Value.Trim() == "True") continue;
            bool construction = Child(def, "isConstruction")?.Value.Trim() == "True";
            if (Child(def, "curve") is not { } c) continue;
            string type = (string?)c.Attribute("sctype") ?? "";
            try
            {
                if (type.StartsWith("SpaceClaim.Geometry.LineSegment,", StringComparison.Ordinal))
                {
                    var start = V(c, "start"); var dir = V(c, "dir");
                    double length = double.Parse(Child(c, "length")!.Value, CultureInfo.InvariantCulture);
                    result.Add(new SketchLine(start, start + dir.Normalized() * length, Construction: construction));
                }
                else if (type.StartsWith("SpaceClaim.Geometry.Arc,", StringComparison.Ordinal))
                    result.Add(new SketchLine(V(c, "start"), V(c, "end"), V(c, "center"), V(c, "axis").Normalized(), construction));
            }
            catch (Exception e) when (e is FormatException or NullReferenceException) { }
        }
        return result;
    }

    /// <summary>The loose lines written into a package made from the bodies (into its document.xml).</summary>
    public static void Write(RsDocxPackage pkg, IReadOnlyList<SketchLine> lines)
    {
        if (lines.Count == 0 || !pkg.Contains(DocumentPart)) return;
        var doc = pkg.GetXml(DocumentPart);
        var root = doc.Root!;
        // (the design's own part: the first; components come after it)
        var part = root.Descendants(Nom + "PartDef").First();
        part.Elements(Sketch + "PartSketchCurveContainerDef").Remove();
        // monikers from the document's counter on
        var next = root.Element(Core + "nextId")!;
        int n = int.Parse(next.Value, CultureInfo.InvariantCulture);
        string Take() => $"0:{n++}";
        string layer = (string?)root.Descendants().FirstOrDefault(e => e.Name.LocalName == "LayerDef")?.Attribute("Id") ?? "0:9";
        string containerId = Take();
        var container = new XElement(Sketch + "PartSketchCurveContainerDef", new XAttribute("Id", containerId),
            new XElement(Sketch + "updateState", Take()), new XElement(Sketch + "sketchType", "Normal"));
        foreach (var l in lines)
        {
            XElement curve;
            if (l.Centre is { } centre && l.Axis is { } axis)
                curve = new XElement(Sketch + "curve", new XAttribute("sctype", "SpaceClaim.Geometry.Arc, Geometry"),
                    new XElement(Sketch + "center", S(centre)), new XElement(Sketch + "start", S(l.Start)),
                    new XElement(Sketch + "end", S(l.End)), new XElement(Sketch + "axis", S(axis.Normalized())));
            else
            {
                var d = l.End - l.Start;
                curve = new XElement(Sketch + "curve", new XAttribute("sctype", "SpaceClaim.Geometry.LineSegment, Geometry"),
                    new XElement(Sketch + "start", S(l.Start)), new XElement(Sketch + "dir", S(d.Normalized())),
                    new XElement(Sketch + "length", d.Length.ToString("R", CultureInfo.InvariantCulture)));
            }
            container.Add(new XElement(Sketch + "SketchCurveDef", new XAttribute("Id", Take()),
                new XElement(Sketch + "updateState", Take()), new XElement(Sketch + "layerId", layer), new XElement(Sketch + "lineWeight"),
                curve,
                new XElement(Sketch + "isConstruction", l.Construction ? "True" : "False"),
                new XElement(Sketch + "isInfinite", "False")));
        }
        // before the bodies, as DesignSpark puts it
        if (part.Elements(Nom + "NominalBodyDef").FirstOrDefault() is { } firstBody) firstBody.AddBeforeSelf(container);
        else part.Add(container);
        next.Value = n.ToString(CultureInfo.InvariantCulture);
        pkg.SetXml(DocumentPart, doc);
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static Vec3 V(XElement c, string name)
    {
        var p = Child(c, name)!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new Vec3(p[0], p[1], p[2]);
    }

    private static string S(Vec3 v) => string.Join(" ", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
}
