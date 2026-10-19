using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>Opening a model without the converter's window: STEP and DesignSpark as they are, a Revit project as Revit
/// last drew it, a mesh (STL/3MF) recognised here (cleaned first when it is not closed).</summary>
public static class ModelFiles
{
    public static bool IsStep(string path) => path.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".stp", StringComparison.OrdinalIgnoreCase);

    public static bool IsRsDocx(string path) => path.EndsWith(".rsdocx", StringComparison.OrdinalIgnoreCase);

    public static bool IsRevit(string path) => path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The kinds of file that can be opened (extensions without the dot).</summary>
    public static readonly string[] Extensions = ["step", "stp", "rsdocx", "rvt", "stl", "3mf"];

    /// <summary>Whether an exception is the file's fault (to tell the user), not the program's.</summary>
    public static bool IsFileError(Exception ex) => ex is MeshException or IOException or UnauthorizedAccessException or StlFormatException
        or InvalidDataException or RsDocxReadException or RsDocx.Core.Step.StepException or RevitReadException or RsDocx.Core.Revit.CompoundFileException;

    /// <summary>The document in the file and, when something was done to it on the way, a note (title, text) for the user.</summary>
    public static (SketchDocument Doc, (string Title, string Text)? Note) Load(string path) => Load(path, null);

    /// <summary>As <see cref="Load(string)"/>; for a Revit project with <paramref name="revitLinks"/> (see
    /// <see cref="SketchDocument.RevitLinksFound"/>) the models it links too.</summary>
    public static (SketchDocument Doc, (string Title, string Text)? Note) Load(string path, IReadOnlyList<(RsDocx.Core.Revit.RevitLink Link, string File)>? revitLinks)
    {
        if (IsStep(path)) return (SketchDocument.FromStep(path), null);
        if (IsRevit(path))
        {
            var rvt = SketchDocument.FromRevit(path, out var report, revitLinks);
            int left = report.SkippedSurfaces.Values.Sum();
            int solids = rvt.Bodies.Count(b => !b.Sheet), models = rvt.Groups.Count(g => g.Parent == null);
            return (rvt, (Lang.T("🏢 Revit-model", "🏢 Revit model"),
                Lang.T($"{rvt.Bodies.Count:N0} lichamen ({solids:N0} dicht) uit {models} {(models == 1 ? "model" : "modellen")}, zoals Revit ze het laatst tekende, in de kleuren van hun materialen; ronde vlakken exact (cilinders, bollen, omwentelingsvlakken), andere gebogen vlakken als kleine platte vlakjes."
                        + (left > 0 ? $" {left:N0} vlakken van een soort die nog niet gelezen wordt, zijn weggelaten." : ""),
                    $"{rvt.Bodies.Count:N0} bodies ({solids:N0} closed) from {models} {(models == 1 ? "model" : "models")}, as Revit last drew them, in their materials' colours; round faces exact (cylinders, spheres, surfaces of revolution), other curved faces as small flat faces."
                        + (left > 0 ? $" {left:N0} faces of a kind not read yet were left out." : ""))));
        }
        if (IsRsDocx(path))
        {
            var doc = SketchDocument.FromRsDocx(path, out int hidden);
            return (doc, hidden == 0 ? null : (Lang.T("👻 Onzichtbare lichamen", "👻 Hidden bodies"),
                Lang.T($"{hidden} {(hidden == 1 ? "lichaam staat" : "lichamen staan")} in DesignSpark op onzichtbaar; hier zie je {(hidden == 1 ? "het" : "ze")} gewoon.", $"{hidden} {(hidden == 1 ? "body is" : "bodies are")} hidden in DesignSpark; here you see {(hidden == 1 ? "it" : "them")} all the same.")));
        }
        var stl = path.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) ? ThreeMfReader.Read(File.ReadAllBytes(path)) : StlReader.Read(path);
        var opt = new MeshOptions { MergeCoplanar = true, RecognizeCylinders = true, Scale = 0.001 };
        try { return (SketchDocument.FromMesh(stl, opt), null); }
        catch (MeshException)
        {
            // not closed, or touching itself: cleaned first (as 🧹 in the converter)
            var clean = MeshRepair.Repair(stl, new MeshRepairOptions(), out _);
            return (SketchDocument.FromMesh(clean, opt), (Lang.T("🧹 Eerst opgeschoond", "🧹 Cleaned up first"),
                Lang.T("De mesh was niet helemaal dicht en is eerst opgeschoond.", "The mesh was not quite closed and has been cleaned up first.")));
        }
    }
}
