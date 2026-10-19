using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Tests;

/// <summary>A Revit project opened: testdata/test.rvt of the converter (Autodesk's "Snowdon Towers Sample
/// Architectural", Revit 2024; not in the repository: the test passes without it).</summary>
public class RevitTests
{
    [Fact]
    public void A_Revit_project_opens_as_bodies_and_a_wall_can_be_pulled()
    {
        string path = TestFiles.Converter("test.rvt");
        if (!File.Exists(path)) return;
        var d = SketchDocument.FromRevit(path, out var report);
        Assert.True(d.Bodies.Count > 10_000);
        // (nearly all faces in closed bodies: they meet along shared edges; a body Revit saved crooked, an edge of
        // three faces or of two the same way round, in pieces, some of them a face alone; a wall's sweep (a cornice,
        // a skirting) saved without ends where it meets the wall: open as saved)
        Assert.True(d.Bodies.Where(b => !b.Sheet).Sum(b => b.Faces.Count) > 0.9 * d.Bodies.Sum(b => b.Faces.Count));
        // (its levels from the bottom up, the third floor among them with its walls and floors on it)
        Assert.Equal(d.Levels.OrderBy(l => l.Elevation), d.Levels);
        var l3 = d.Levels.Single(l => l.Name == "L3");
        Assert.Equal(5.74, l3.Elevation, 2);
        Assert.True(l3.Bodies.Count > 1000);
        // (the storeys by height: the blocks' ground floors at -1.80, -1.05 and 0 one storey, number 0, the one the
        // project's height 0 is on; the parking under it -1; the third floor 2)
        var storeys = SketchStoreys.Of(d.Levels);
        Assert.Equal([-1, 0, 1, 2, 3], storeys.Take(5).Select(s => s.Number));
        Assert.Contains("L1 - Block 43", storeys.Single(s => s.Number == 0).Levels);
        Assert.Equal(["L3"], storeys.Single(s => s.Number == 2).Levels);
        var walls = d.Groups.Single(g => g.Name == "SWall");
        int wall = d.Members(walls.Id).First(b => !d.Bodies[b].Sheet && d.Bodies[b].Faces.Count == 6);
        Assert.StartsWith("SWall ", d.BodyName(wall));
        // its largest face pulled out 10 mm: the wall that much thicker
        var body = d.Bodies[wall];
        int face = Enumerable.Range(0, body.Faces.Count).MaxBy(f => d.TrianglesOf(new FaceRef(wall, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length));
        double area = d.TrianglesOf(new FaceRef(wall, face)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length) / 2;
        var one = SketchDocument.FromBodies([body]);
        var (pulled, _) = one.PushPull(new FaceRef(0, face), 0.01);
        Assert.Equal(one.Volume() + area * 0.01, pulled.Volume(), 9);
    }
}
