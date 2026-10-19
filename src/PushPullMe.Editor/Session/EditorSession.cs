using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using static PushPullMe.Editor.Units;

namespace PushPullMe.Editor.Session;


public sealed partial class EditorSession
{
    private SketchDocument? _doc;
    private string? _path;
    private readonly History _history = new();

    /// <summary>Per body and colour its shown triangles and, per triangle, the face it belongs to (for picking).</summary>
    private readonly List<PickMesh> _meshes = new();
    private double _modelSize = 100;

    private FaceRef? _hover, _selected;

    /// <summary>The group open to work in (as SketchUp's: a double click on it), null: none. What lies outside it is shown
    /// faded and cannot be picked.</summary>
    private int? _openGroup;

    /// <summary>The group selected in the structure tree (to keep it in step with the view).</summary>
    private int? _treeGroup;

    /// <summary>The bodies and groups picked (a group with a click, more with Ctrl+click), for Ctrl+G.</summary>
    private readonly List<GroupUnit> _pickedUnits = new();

    // push/pull in progress: the face, where it was grabbed (mm), its normal, the document it started from
    private FaceRef? _dragFace;
    private Point3D _dragStart;
    private Vector3D _dragNormal;
    /// <summary>The end of a turned piece being dragged (it follows the turn): its axis and its middle's distance from it;
    /// and the angle the mouse is at round it so far (to go on past half a turn).</summary>
    private (Vec3 Point, Vec3 Dir, double Radius)? _dragTurn;
    private double _dragTurnAngle;
    private SketchDocument? _dragBase;
    private double _dragDist;

    /// <summary>The face being pushed/pulled is on a cylinder (pin, hole or rounding): the box shows (and takes) its
    /// radius, not the distance.</summary>
    private bool _dragRadius;

    private static bool HasRadius(SketchDocument doc, FaceRef f) => doc.Bodies[f.Body].Faces[f.Face].Surface is BrepCylinder
        || (doc.Bodies[f.Body].Faces[f.Face].Surface is BrepSphere or BrepTorus && PushPullMe.Core.PushPull.IsRoundingSet(doc.Bodies[f.Body], f.Face));
    private bool _dragValid;

    // what is shown (the document, or the preview while dragging), and for a preview its face numbers in the document
    // it started from
    private SketchDocument? _shown;
    private IReadOnlyList<int>? _previewMap;
    private HashSet<int> _previewNew = new();

    /// <summary>While dragging: the face (in the document it started from) the distance is flush with.</summary>
    private FaceRef? _snapBase;

    // drawing in progress (rectangle or line): the face, the first point, the point under the mouse (metres), the
    // rectangle's first side direction, and what the point under the mouse snapped to
    private FaceRef? _drawFace;
    private Vec3 _drawStart, _drawEnd, _drawU;
    private bool _drawEndOk;

    private LineSet? _sketch;

    /// <summary>The last push/pull: typing a distance right after it changes it (as in SketchUp).</summary>
    private (SketchDocument Before, FaceRef Face, double Dist)? _last;

    /// <summary>What is being typed into the measurements box.</summary>
    private string _typed = "";

    /// <summary>The window this editor works in (its dialogs, messages and thread).</summary>
    private readonly IEditorWindow _window;

    /// <summary>The view: its camera and size, and what is shown in it beside the model.</summary>
    public ViewHost View { get; } = new();

    // what the window shows, set here
    public TextSlot StatusText { get; } = new();
    public TextSlot MeasureBox { get; } = new();
    public TextSlot MeasureLabel { get; } = new();
    public TextSlot MeasureUnit { get; } = new();
    public TextSlot FileText { get; } = new();
    public TextSlot PaintName { get; } = new();
    public FlagSlot UndoButton { get; } = new();
    public FlagSlot RedoButton { get; } = new();
    public FlagSlot SaveButton { get; } = new();
    public FlagSlot EmptyHint { get; } = new(true);
    public FlagSlot PaintPanel { get; } = new();
    public FlagSlot JoinBox { get; } = new(true);
    public FlagSlot AssistantBox { get; } = new();
    /// <summary>The button that puts the work plane back on the ground (shown while it lies elsewhere).</summary>
    public FlagSlot GroundButtonShown { get; } = new();

    /// <summary>The tool chosen.</summary>
    public Tool Tool { get; private set; } = Tool.Select;

    /// <summary>Another tool chosen (the window's buttons follow).</summary>
    public event Action? ToolChanged;

    /// <summary>The model shown changed (the window draws <see cref="Scene"/> anew).</summary>
    public event Action? SceneChanged;

    /// <summary>The model as the view shows it now (per body and colour its triangles, the edges).</summary>
    public ShownModel? Shown { get; private set; }

    public EditorSession(IEditorWindow window)
    {
        _window = window;
        JoinBox.On = Settings.LoadJoin();
        JoinBox.Changed += () =>
        {
            Settings.SaveJoin(Join);
            StatusText.Text = Join ? T("⇕ Samenvoegen aan: wat je tegen een ander deel aan trekt, wordt er één deel mee.", "⇕ Merge on: what you pull against another part becomes one part with it.")
                : T("⇕ Samenvoegen uit: wat je trekt blijft een apart deel.", "⇕ Merge off: what you pull stays a part of its own.");
        };
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
        // the highlight and the sketch sit just in front of the faces as seen from the camera: again when it moves
        View.Camera.Changed += CameraChanged;
    }

    /// <summary>The camera moved (by a tool, or the window's orbit, pan or zoom).</summary>
    private void CameraChanged()
    {
        _cameraChanges++;
        // (the work plane's handle keeps its size on the screen)
        if (WorkPlaneMode) RenderGizmo();
        // (a camera that cannot be used, whatever set it: everything back in view; looked at once all its settings
        // are made, as setting them one after the other can pass through such a state)
        if (!CameraOk())
        {
            _window.Post(() => { if (!CameraOk()) { Log("camera unusable: everything back in view"); ZoomAll(0); } });
            return;
        }
        var hc = View.Camera;
        // what can be seen from just in front of the eye to far behind the model, as a fixed ratio (a fine depth
        // test: edges drawn a hair in front of their faces), scaled with how far away the camera looks
        double near = Math.Clamp(hc.LookDirection.Length / 100, 0.01, 50);
        if (Math.Abs(hc.NearPlaneDistance - near) > 0.2 * near) { hc.NearPlaneDistance = near; hc.FarPlaneDistance = near * 20000; }
        // (the snap marks were put a little toward the eye as it was: away, the next mouse move brings them back)
        ShowMarks([]);
        if (_doc == null) return;
        if (_sketchShown is { } sk) ShowSketch(sk.Pts, default, sk.Closed, sk.Color);
        RenderLoose(_doc);
        PlaceNotes();
    }

    /// <summary>The window is shown: the test aid's file (if any), else an empty work sheet, the ground ready to draw on.</summary>
    public async Task Started()
    {
        LoadAssistant();
        await TestAid();
        if (_doc == null) NewDocument();
    }

    /// <summary>An unexpected error: written down (for finding it later), the action in progress stopped, the model kept.</summary>
    public void Crashed(Exception ex)
    {
        AppData.LogError(ex);
        Log("error: " + ex);
        EndDrag(); EndDraw(); EndOffset(); EndRound(); EndRotate(); EndScale(); EndPolygon(); EndArc(); EndSweep(); EndRotRect(); EndFollow(); _typed = "";
        Render();
        ShowToast(T("😬 Er ging iets mis", "😬 Something went wrong"), T($"Dat is afgebroken; je model is er nog. (De fout staat in {AppData.ErrorLog}.)", $"That was stopped; your model is still there. (The error is in {AppData.ErrorLog}.)"));
        StatusText.Text = T("😬 Er ging iets mis; afgebroken.", "😬 Something went wrong; stopped.");
    }

    private void ShowToast(string title, string text) => _window.ShowToast(title, text);

    // ------------------------------------------------------------------ files

    /// <summary>📂: a model chosen by the user, opened.</summary>
    public async Task OpenDialog()
    {
        if (await _window.AskOpenPath() is { } path) await Open(path);
    }

    private static bool IsStep(string path) => ModelFiles.IsStep(path);

    /// <summary>A file with exact shapes (STEP or DesignSpark), opened here right away; a mesh goes through the converter.</summary>
    private static bool IsExact(string path) => IsStep(path) || ModelFiles.IsRsDocx(path) || ModelFiles.IsRevit(path);

    /// <summary>A file to work on: STEP or .rsdocx opens right away; a mesh (STL/3MF) opens in the converter's window (shapes
    /// are best found there, with all its options) when the window has it, and what it hands over is taken over here.</summary>
    public async Task Open(string path)
    {
        if (IsExact(path) || !_window.HasConverter) { await Load(path); return; }
        (SketchDocument Doc, string Note)? r;
        StatusText.Text = T("🔄 Herken de vormen in de converter en kies ✅ Overnemen in PushPullMe.", "🔄 Recognise the shapes in the converter and choose ✅ Take over in PushPullMe.");
        Log($"converter opened for {path}");
        try { r = await _window.OpenInConverter(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            // (the converter cannot open: recognised here, as before)
            Log("converter: " + ex.Message);
            ShowToast(T("😕 De converter opent niet", "😕 The converter does not open"), T("Het bestand wordt hier herkend.", "The file is recognised here."));
            await Load(path);
            return;
        }
        if (r is not { } got)
        {
            StatusText.Text = _doc == null ? T("Open een model om te beginnen.", "Open a model to begin.") : T("De converter is gesloten zonder over te nemen.", "The converter was closed without taking anything over.");
            Log("converter closed without handing over");
            return;
        }
        Adopt(got.Doc, path, got.Note.Length > 0 ? (T("🔄 Uit de converter", "🔄 From the converter"), got.Note) : null);
        Log($"converter handed over: {got.Doc.Bodies.Sum(b => b.Faces.Count)} faces{(got.Note.Length > 0 ? " " + got.Note : "")}");
    }

    /// <summary>A model to work on from now on (opened, or taken over from the converter).</summary>
    private void Adopt(SketchDocument doc, string path, (string Title, string Text)? note)
    {
        ClearGuides();
        _doc = doc; _path = path; _plane = WorkPlane.Ground; _savedDoc = doc;
        _history.Clear(); _last = null; _lastMove = null; _selected = null; _hover = null; EndDrag(); EndDraw(); EndOffset(); EndRound(); _lastOffset = null; _lastRound = null; EndRotate(); EndScale(); EndPolygon(); EndArc(); EndSweep(); EndRotRect(); EndFollow(); EndGround(); _unionFirst = null; _mirrorBody = null;
        _openGroup = null; _pickedUnits.Clear();
        SetLevels(doc.Levels);
        UpdateUndoButtons();
        Render(zoom: true);
        FileText.Text = Path.GetFileName(path);
        EmptyHint.On = false;
        SaveButton.On = true;
        int faces = doc.Bodies.Sum(b => b.Faces.Count), planes = doc.Bodies.Sum(b => b.Faces.Count(f => f.Surface is BrepPlane));
        StatusText.Text = T($"📦 {faces:N0} vlakken, waarvan {planes:N0} plat. Kies ⇕ (of druk P) en klik een plat vlak om het te verschuiven.", $"📦 {faces:N0} faces, {planes:N0} of them flat. Choose ⇕ (or press P) and click a flat face to move it.");
        if (note is { } n) ShowToast(n.Title, n.Text);
        Log($"open {path}: {faces} faces, {planes} planar");
    }

    private async Task Load(string path)
    {
        // (a Revit project linking other models: those too, when the user wants them; they can make it heavy)
        List<(RsDocx.Core.Revit.RevitLink Link, string File)>? links = null;
        if (ModelFiles.IsRevit(path))
        {
            _window.Busy(T("Gekoppelde modellen zoeken…", "Looking for linked models…"));
            var found = await Task.Run(() => SketchDocument.RevitLinksFound(path));
            _window.Busy(null);
            if (found.Count > 0 && await _window.Ask(T(
                    $"Dit Revit-project koppelt {found.Count} {(found.Count == 1 ? "ander model" : "andere modellen")} ({string.Join(", ", found.Select(f => Path.GetFileNameWithoutExtension(f.File)))}). Die ook openen? Dat duurt langer en kost meer geheugen.",
                    $"This Revit project links {found.Count} other {(found.Count == 1 ? "model" : "models")} ({string.Join(", ", found.Select(f => Path.GetFileNameWithoutExtension(f.File)))}). Open those too? That takes longer and needs more memory."),
                    T("Gekoppelde modellen", "Linked models")))
                links = found;
        }
        _window.Busy(T("Bestand lezen en vormen herkennen…", "Reading the file and recognising shapes…"));
        try
        {
            var (doc, note) = await Task.Run(() => ModelFiles.Load(path, links));
            Adopt(doc, path, note);
        }
        catch (Exception ex) when (ModelFiles.IsFileError(ex))
        {
            ShowToast(T("😕 Dit bestand lukt niet", "😕 This file does not work"), ex.Message);
        }
        finally { _window.Busy(null); }
    }

    /// <summary>💾: where to save asked, and saved there.</summary>
    public async Task SaveDialog()
    {
        if (_doc == null) return;
        if (_doc.Bodies.Count == 0)
        {
            StatusText.Text = T("💾 Er is nog niets om op te slaan: teken eerst een vlak of blok.", "💾 There is nothing to save yet: draw a face or block first.");
            return;
        }
        // (the name it has now, in its own folder: the user decides on another; the dialog asks before overwriting)
        // (STEP first: the main format; the kind the file has now chosen)
        int kind = _path == null ? 1 : _path.EndsWith(".rsdocx", StringComparison.OrdinalIgnoreCase) ? 2 : _path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        if (await _window.AskSavePath(Path.GetFileNameWithoutExtension(_path ?? "model"), _path != null ? Path.GetDirectoryName(Path.GetFullPath(_path)) : null, kind) is { } path) Save(path);
    }
    public void Save(string path)
    {
        if (_doc == null) return;
        try
        {
            if (path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(path, _doc.ToStl());
            else if (IsStep(path)) File.WriteAllText(path, _doc.ToStep(Path.GetFileName(path)));
            else _doc.ToRsDocx().Save(path);
            _path = path; FileText.Text = Path.GetFileName(path); _savedDoc = _doc;
            if (_doc.Colors.Any(c => c.Count > 0) && path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast(T("💾 Opgeslagen, zonder kleuren", "💾 Saved, without colours"), Path.GetFileName(path) + T(": een STL kan geen kleuren bevatten. Sla op als .rsdocx om ze te bewaren (DesignSpark ziet ze dan ook).", ": an STL cannot hold colours. Save as .rsdocx to keep them (DesignSpark sees them too)."));
                Log($"saved {path} (colours left out)");
                return;
            }
            if (_doc.Groups.Count > 0 && path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast(T("💾 Opgeslagen, zonder groepen", "💾 Saved, without groups"), Path.GetFileName(path) + T(": een STL kan geen groepen of componenten bevatten. Sla op als STEP of .rsdocx om ze te houden.", ": an STL cannot hold groups or components. Save as STEP or .rsdocx to keep them."));
                Log($"saved {path} (groups left out)");
                return;
            }
            ShowToast(T("💾 Opgeslagen", "💾 Saved"), Path.GetFileName(path));
            Log($"saved {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ShowToast(T("😕 Opslaan lukt niet", "😕 Saving does not work"), ex.Message);
        }
    }

    // ------------------------------------------------------------------ undo

    private void Commit(SketchDocument before, SketchDocument after)
    {
        _doc = _history.Commit(before, after, _openGroup);
        _multi = null;
        _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null;
        UpdateUndoButtons();
    }

    public void Undo()
    {
        if (!_history.CanUndo || _doc == null) return;
        EndDrag(); EndDraw(); EndOffset(); EndRound(); EndRotate(); EndScale(); EndPolygon(); EndArc(); EndSweep(); EndRotRect(); EndFollow(); EndGround(); _unionFirst = null; _mirrorBody = null;
        _doc = _history.Undo(_doc)!;
        if (_openGroup is { } og && _doc.Group(og) == null) _openGroup = null;
        _pickedUnits.Clear();
        _last = null; _lastMove = null; _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null; _selected = null; _hover = null;
        Render();
        UpdateUndoButtons();
        StatusText.Text = T("↩ Ongedaan gemaakt.", "↩ Undone.");
        Log("undo");
    }

    public void Redo()
    {
        if (!_history.CanRedo || _doc == null) return;
        EndDrag(); EndDraw(); EndOffset(); EndRound(); EndRotate(); EndScale(); EndPolygon(); EndArc(); EndSweep(); EndRotRect(); EndFollow(); EndGround(); _unionFirst = null; _mirrorBody = null;
        _doc = _history.Redo(_doc)!;
        if (_openGroup is { } og && _doc.Group(og) == null) _openGroup = null;
        _pickedUnits.Clear();
        _last = null; _lastMove = null; _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null; _selected = null; _hover = null;
        Render();
        UpdateUndoButtons();
        StatusText.Text = T("↪ Opnieuw gedaan.", "↪ Redone.");
        Log("redo");
    }

    private void UpdateUndoButtons()
    {
        UndoButton.On = _history.CanUndo;
        RedoButton.On = _history.CanRedo;
    }

    // ------------------------------------------------------------------ showing the model

    public static readonly Color FrontColor = Color.FromRgb(0xF3, 0xF0, 0xE8);
    public static readonly Color BackColor = Color.FromRgb(0x8E, 0x9B, 0xB8);
    public static readonly Color EdgeColor = Color.FromRgb(0x26, 0x2B, 0x36);
    public static readonly Color HoverColor = Color.FromRgb(0xFA, 0xCC, 0x15);
    public static readonly Color SelectColor = Color.FromRgb(0x25, 0x63, 0xEB);

    private static Point3D P(Vec3 v) => new(v.X * Display, v.Y * Display, v.Z * Display);
    private static Vector3D D(Vec3 v) => new(v.X, v.Y, v.Z);

    private void Render(bool zoom = false) => Render(_doc, zoom);

    /// <summary>The document as triangles and edges, the bodies not hidden each as <see cref="ViewOf"/> shows it; the
    /// window draws it (<see cref="SceneChanged"/>).</summary>
    private void Render(SketchDocument? doc, bool zoom = false)
    {
        _meshes.Clear();
        _shown = doc;
        RenderLoose(doc);
        if (doc == null) { Shown = null; SceneChanged?.Invoke(); ClearHighlight(); return; }
        var meshes = new List<ShownMesh>();
        double x0 = double.MaxValue, x1 = double.MinValue;
        var pts = new Point3DCollection();
        for (int b = 0; b < doc.Bodies.Count; b++)
        {
            if (!Drawn(doc.Bodies[b])) continue;
            var v = ViewOf(doc, b);
            foreach (var mesh in v.Meshes)
            {
                meshes.Add(mesh);
                if (!mesh.Faded) _meshes.Add(mesh.Pick);
            }
            foreach (var p in v.Edges) pts.Add(p);
            x0 = Math.Min(x0, v.Low); x1 = Math.Max(x1, v.High);
        }
        if (x1 > x0) _modelSize = (x1 - x0) * Display;
        Shown = new ShownModel(meshes, pts);
        SceneChanged?.Invoke();
        RenderSectionCut(doc);
        RenderGround(doc);
        if (ReferenceEquals(doc, _doc) && !ReferenceEquals(doc, _treeDoc)) RefreshStructure();
        UpdateHighlight();
        if (zoom)
        {
            // from the front left, a little above (as SketchUp opens a model), then everything in view
            var cam = View.Camera; cam.Set(cam.Position, new Vector3D(1, 1.4, -0.9), new Vector3D(0, 0, 1));
            ZoomAll(0);
        }
    }

    /// <summary>A body as shown: its meshes, its edges' points (two per segment) and its lowest and highest
    /// coordinate; with what it was made for (its colours, whether outside the open group).</summary>
    private sealed record BodyView(IReadOnlyList<ShownMesh> Meshes, Point3D[] Edges, double Low, double High, IReadOnlyDictionary<int, uint>? Colors, bool Outside);

    /// <summary>Whether two bodies' colours are the same (painting copies them all: compared by what is in them).</summary>
    private static bool SameColors(IReadOnlyDictionary<int, uint>? a, IReadOnlyDictionary<int, uint>? b) =>
        ReferenceEquals(a, b) || (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || b == null || a.All(kv => b.TryGetValue(kv.Key, out uint c) && c == kv.Value));

    /// <summary>The bodies as shown last, kept with the bodies themselves (let go with them): hiding or showing some
    /// of them, or changing one, makes only what changed anew.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BrepBody, BodyView> _views = new();

    /// <summary>
    /// Body <paramref name="b"/> as shown (per colour one mesh of triangles: flat faces flat, round faces shaded smooth;
    /// outside the open group faded, not to be picked, without edges; its edges not between two round faces: those are
    /// soft, as in SketchUp); the one made before when the body, its colours and its place in or outside the open group
    /// are the same and nothing is cut away by a section.
    /// </summary>
    private BodyView ViewOf(SketchDocument doc, int b)
    {
        var body = doc.Bodies[b];
        var colors = b < doc.Colors.Count ? doc.Colors[b] : null;
        bool outside = Outside(b);
        if (_section == null && _views.TryGetValue(body, out var known) && SameColors(known.Colors, colors) && known.Outside == outside)
        {
            if (known.Meshes.All(m => m.Body == b)) return known;
            // (the body has another number now: the same triangles, numbered so)
            known = known with { Meshes = known.Meshes.Select(m => new ShownMesh(m.Positions, m.Normals, m.Pick.FaceOfTriangle as int[] ?? m.Pick.FaceOfTriangle.ToArray(), b, m.Color, m.Faded)).ToList() };
            _views.AddOrUpdate(body, known);
            return known;
        }
        double x0 = double.MaxValue, x1 = double.MinValue;
        // one mesh per colour painted on the body (null: the standard colour)
        var byColor = new Dictionary<uint, (List<System.Numerics.Vector3> Pos, List<System.Numerics.Vector3> Nor, List<int> Map)>();
        const uint Standard = 0;
        for (int f = 0; f < body.Faces.Count; f++)
        {
            var face = body.Faces[f];
            uint key = doc.ColorOf(new FaceRef(b, f)) ?? Standard;
            if (!byColor.TryGetValue(key, out var mm)) byColor[key] = mm = (new(), new(), new List<int>());
            var faceTris = doc.TrianglesOf(new FaceRef(b, f));
            // (a free-form face has no normal of its own here: the mean of its triangles round each point)
            var smooth = face.Surface is BrepNurbsSurface ? FaceGeometry.SmoothNormals(faceTris) : null;
            foreach (var (a, bb, c) in Clipped(faceTris))
            {
                foreach (var p in new[] { a, bb, c })
                {
                    mm.Pos.Add(F(P(p)));
                    var n = smooth?.Invoke(p) ?? NormalAt(face, p, a, bb, c);
                    mm.Nor.Add(new System.Numerics.Vector3((float)n.X, (float)n.Y, (float)n.Z));
                    x0 = Math.Min(x0, Math.Min(p.X, Math.Min(p.Y, p.Z))); x1 = Math.Max(x1, Math.Max(p.X, Math.Max(p.Y, p.Z)));
                }
                mm.Map.Add(f);
            }
        }
        var meshes = new List<ShownMesh>();
        foreach (var (key, (pos, nor, map)) in byColor)
            if (pos.Count > 0) meshes.Add(new ShownMesh(pos.ToArray(), nor.ToArray(), map.ToArray(), b, key == Standard ? null : Argb(key), outside));
        var edges = new List<Point3D>();
        if (!outside)
        {
            var curvedUses = new int[body.Edges.Count];
            foreach (var face in body.Faces.Where(f => f.Surface is not BrepPlane))
                foreach (var c in face.Loops.SelectMany(l => l)) curvedUses[c.Edge]++;
            for (int e = 0; e < body.Edges.Count; e++)
            {
                if (curvedUses[e] >= 2) continue;
                var line = FaceGeometry.EdgePoints(body, body.Edges[e]);
                for (int i = 0; i + 1 < line.Count; i++)
                    if (ClippedSegment(line[i], line[i + 1]) is { } seg) { edges.Add(P(seg.A)); edges.Add(P(seg.B)); }
            }
        }
        var view = new BodyView(meshes, edges.ToArray(), x0, x1, colors, outside);
        if (_section == null) _views.AddOrUpdate(body, view);
        return view;
    }

    /// <summary>Whether body <paramref name="b"/> lies outside the open group (shown faded, not to be picked); by the
    /// document worked on, so a preview (made without the groups) shows the same.</summary>
    private bool Outside(int b) => _openGroup != null && _doc != null && b < _doc.Bodies.Count && _doc.UnitOf(b, _openGroup) == null;

    /// <summary>The outward normal to shade a point of a face with: of the surface where it is round, of the plane or
    /// the triangle otherwise.</summary>
    private static Vec3 NormalAt(BrepFace face, Vec3 p, Vec3 a, Vec3 b, Vec3 c) =>
        FaceGeometry.NormalAt(face, p) ?? (b - a).Cross(c - a).Normalized();

    private static System.Numerics.Vector3 F(Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>The highlights shown now (in <see cref="ViewHost.Children"/>).</summary>
    private readonly List<object> _highlights = new();

    /// <summary>A picked group of more faces than this gets a frame round it instead of a tint (as in SketchUp): a
    /// building's worth of faces tinted anew at every mouse move would hold up the view.</summary>
    private const int TintedFaces = 20_000;

    private void ClearHighlight()
    {
        foreach (var h in _highlights) View.Children.Remove(h);
        _highlights.Clear();
    }

    private void UpdateHighlight()
    {
        ClearHighlight();
        if (_doc == null) return;
        var doc = _dragFace != null && _dragValid ? _preview ?? _doc : _doc;
        // (the faces tinted, all of one colour in one set; on the faces themselves: the view draws the model's faces a
        // little behind, so the tint lies in front from wherever it is seen, and the camera moving changes nothing)
        var tints = new Dictionary<(Color, double), List<Point3D>>();
        void Add(FaceRef f, Color color, double opacity)
        {
            if (f.Body >= doc.Bodies.Count || f.Face < 0 || f.Face >= doc.Bodies[f.Body].Faces.Count) return;
            if (!tints.TryGetValue((color, opacity), out var pos)) tints[(color, opacity)] = pos = new List<Point3D>();
            foreach (var (a, b, c) in doc.TrianglesOf(f)) { pos.Add(P(a)); pos.Add(P(b)); pos.Add(P(c)); }
        }
        if (_selected is { } s) Add(s, SelectColor, 0.35);
        // (a picked group or part: its faces tinted, or a frame round it when they are very many)
        var pickedBodies = _pickedUnits.SelectMany(u => _doc.BodiesOf(u)).Where(pb => pb < doc.Bodies.Count).Distinct().ToList();
        if (pickedBodies.Sum(pb => (long)doc.Bodies[pb].Faces.Count) > TintedFaces)
        {
            var lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); var hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (int pb in pickedBodies)
                foreach (var v in doc.Bodies[pb].Vertices)
                {
                    lo = new Vec3(Math.Min(lo.X, v.X), Math.Min(lo.Y, v.Y), Math.Min(lo.Z, v.Z));
                    hi = new Vec3(Math.Max(hi.X, v.X), Math.Max(hi.Y, v.Y), Math.Max(hi.Z, v.Z));
                }
            var frame = new Point3DCollection();
            Vec3 Corner(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
            for (int k = 0; k < 8; k++)
                foreach (int bit in new[] { 1, 2, 4 })
                    if ((k & bit) == 0) { frame.Add(P(Corner(k))); frame.Add(P(Corner(k | bit))); }
            var box = new LineSet { Points = frame, Color = SelectColor, Thickness = 2 };
            _highlights.Add(box);
            View.Children.Add(box);
        }
        else
            foreach (int pb in pickedBodies)
                for (int pf2 = 0; pf2 < doc.Bodies[pb].Faces.Count; pf2++) Add(new FaceRef(pb, pf2), SelectColor, 0.25);
        if (_multi is { } mu) foreach (int mf in mu.Faces) if (_selected is not { } sel || sel != new FaceRef(mu.Body, mf)) Add(new FaceRef(mu.Body, mf), SelectColor, 0.35);
        if (_dragFace is { } dfc && _dragValid && _previewFace is { } pf) Add(pf, SelectColor, 0.3);
        else if (_drawFace is { } df) Add(df, SelectColor, 0.15);
        else if (_offsetFace is { } of) Add(of, SelectColor, 0.15);
        else if (_hover is { } h && h != _selected) Add(h, HoverColor, 0.4);
        if (_dragFace != null && _snapBase is { } sb)
            Add(_previewMap != null && _dragValid && sb.Body == _dragFace.Value.Body && sb.Face < _previewMap.Count ? new FaceRef(sb.Body, _previewMap[sb.Face]) : sb, HoverColor, 0.45);
        foreach (var ((color, opacity), pos) in tints)
        {
            if (pos.Count == 0) continue;
            var set = new FaceSet { Triangles = pos, Color = color, Opacity = opacity };
            _highlights.Add(set);
            View.Children.Add(set);
        }
        // (the structure tree shows what is picked too)
        int? pickedGroup = _pickedUnits.Count == 1 ? _pickedUnits[0].Group : null;
        if (pickedGroup != null)
        {
            if (pickedGroup != _treeGroup) { _treeGroup = pickedGroup; _treePicked = (null, null); _window.StructurePickGroup(pickedGroup.Value); }
            return;
        }
        _treeGroup = null;
        (int?, int?) picked = _selected is { } ps ? (ps.Body, ps.Face) : _multi is { } pm && ReferenceEquals(doc, _doc) && pm.Body < doc.Bodies.Count && pm.Faces.Count == doc.Bodies[pm.Body].Faces.Count ? (pm.Body, null) : (null, null);
        if (picked != _treePicked) { _treePicked = picked; _window.StructurePick(picked.Item1, picked.Item2); }
    }

    // ------------------------------------------------------------------ the mouse (as the window passes it on)

    private Point? _orbitFrom;

    /// <summary>How often the camera changed (for the test aid).</summary>
    private int _cameraChanges;
    private Point3D _orbitPivot;

    /// <summary>The modifier keys the window saw with the last mouse or key event.</summary>
    private ModifierKeys _mods;

    /// <summary>Where the mouse was last in the view (for a key that takes it into account).</summary>
    private Point _mouseAt;

    /// <summary>
    /// A mouse button down in the view: the middle one (with Shift: pan) turns the view round the point under it; the left
    /// one is a click for the tool (a double click with ↖ opens or closes a group). Whether it was used.
    /// </summary>
    public bool MouseDown(MouseButton button, Point pos, ModifierKeys mods, int clickCount = 1)
    {
        _mods = mods; _mouseAt = pos;
        if (button == MouseButton.Left)
        {
            // (a double click with ↖: a group opened, or beside it closed)
            if (clickCount == 2 && DoubleClickAt(pos)) return true;
            return PressAt(pos);
        }
        if (button != MouseButton.Middle) return false;
        var cam = View.Camera;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            // pan: the point of the model under the mouse (or the middle of the view) keeps under it
            var grab = FaceAt(pos)?.Point ?? cam.Position + cam.LookDirection;
            var look = cam.LookDirection; look.Normalize();
            _panDepth = Vector3D.DotProduct(grab - cam.Position, look);
            _panFrom = pos;
            View.CaptureMouse();
            return true;
        }
        // around the point of the model under the mouse, or else the middle of the view
        _orbitPivot = FaceAt(pos)?.Point ?? cam.Position + cam.LookDirection;
        _orbitFrom = pos;
        View.CaptureMouse();
        return true;
    }

    /// <summary>The mouse moved in the view: turning or panning the view, else on to the tool.</summary>
    public void MouseMove(Point pos, ModifierKeys mods)
    {
        _mods = mods; _mouseAt = pos;
        if (_panFrom is { } pf)
        {
            Pan(pos.X - pf.X, pos.Y - pf.Y);
            _panFrom = pos;
            return;
        }
        if (_orbitFrom is { } from)
        {
            _orbitFrom = pos;
            Orbit(pos.X - from.X, pos.Y - from.Y);
            return;
        }
        if (_doc != null) MouseMoveAt(pos);
    }

    /// <summary>A mouse button up: a drag the left button started set, or turning/panning the view ended.</summary>
    public bool MouseUp(MouseButton button, Point pos, ModifierKeys mods)
    {
        _mods = mods;
        if (button == MouseButton.Left && _pressAt != null) return ReleaseAt(pos);
        if (button != MouseButton.Middle || (_panFrom == null && _orbitFrom == null)) return false;
        _panFrom = null; _orbitFrom = null;
        View.ReleaseMouseCapture();
        return true;
    }

    /// <summary>The mouse left the view: nothing under it.</summary>
    public void MouseLeave()
    {
        if (_hover != null && _dragFace == null) { _hover = null; UpdateHighlight(); }
    }

    /// <summary>
    /// Turns the view as the mouse moved (pixels) around the pivot, freely all the way round (as DesignSpark): left/right
    /// around the screen's up, up/down around its sideways direction; the camera's up turns along, so over the top the
    /// model simply goes on turning and left/right stays left/right as seen.
    /// </summary>
    private void Orbit(double dx, double dy)
    {
        if (View.Camera.Pose.Orbit(_orbitPivot, dx, dy) is { } pose) SetCamera(pose);
    }

    /// <summary>The camera set, unless that would make it unusable (not a number, no direction, up along the look):
    /// then left as it was.</summary>
    private void SetCamera(CameraPose pose)
    {
        if (!pose.IsUsable) return;
        View.Camera.Set(pose.Position, pose.Look, pose.Up);
    }

    private void SetCamera(Point3D position, Vector3D look, Vector3D up) => SetCamera(new CameraPose(position, look, up));

    /// <summary>Whether the camera can be used (a number everywhere, a direction to look in, up not along it).</summary>
    private bool CameraOk() => View.Camera.Pose.IsUsable;

    /// <summary>The model moved along with the mouse (pixels), at the depth of the point grabbed.</summary>
    private void Pan(double dx, double dy)
    {
        if (View.Frame is { } frame && View.Camera.Pose.Pan(frame, _panDepth, dx, dy) is { } pose) SetCamera(pose);
    }

    private Point? _panFrom;
    private double _panDepth;

    /// <summary>The wheel turned (120 a notch) with the mouse at that point: toward the point under it (the model there, or
    /// else a point as far as the middle of the view), a notch a fifth of the way closer, or back.</summary>
    public void MouseWheel(Point pos, double delta)
    {
        var cam = View.Camera;
        Point3D target;
        if (FaceAt(pos)?.Point is { } hit) target = hit;
        else if (Viewport3DHelper.GetRay(View, pos) is { } ray) target = ray.Origin + ray.Direction * cam.LookDirection.Length;
        else return;
        SetCamera(cam.Pose.Zoom(target, delta));
    }

    // ------------------------------------------------------------------ picking

    private (FaceRef Face, Point3D Point)? FaceAt(Point pos)
    {
        foreach (var (_, map, body, tri, at) in Hits(pos))
            if (tri < map.Count) return (new FaceRef(body, map[tri]), at);
        return null;
    }

    /// <summary>The shown faces under a point of the screen, nearest first: its mesh, the face per triangle, the body,
    /// the triangle hit and where (see <see cref="Picking"/>).</summary>
    private IEnumerable<(PickMesh Mesh, IReadOnlyList<int> Map, int Body, int Triangle, Point3D At)> Hits(Point pos)
    {
        if (View.Frame is not { } frame) return [];
        return Picking.Hits(_meshes, frame.Ray(new ScreenPoint(pos.X, pos.Y))).Select(h => (h.Mesh, h.Mesh.FaceOfTriangle, h.Mesh.Body, h.Triangle, new Point3D(h.At.X, h.At.Y, h.At.Z)));
    }

    private bool IsPlanar(FaceRef f) => _doc != null && _doc.Bodies[f.Body].Faces[f.Face].Surface is BrepPlane;

    private bool IsCylinder(FaceRef f) => _doc != null && HasRadius(_doc, f);

    /// <summary>What ⇕ can take: a flat face, or a cylinder (its radius; as DesignSpark).</summary>
    private bool Pushable(FaceRef f) => IsPlanar(f) || IsCylinder(f);

    // ------------------------------------------------------------------ tools and mouse

    private bool PushMode => Tool == Tool.PushPull;
    private bool RectMode => Tool == Tool.Rectangle;
    private bool LineMode => Tool == Tool.Line;
    private bool CircleMode => Tool == Tool.Circle;
    private bool EraserMode => Tool == Tool.Eraser;
    private bool MoveMode => Tool == Tool.Move;
    private bool TapeMode => Tool == Tool.Tape;
    private bool PaintMode => Tool == Tool.Paint;
    private bool OffsetMode => Tool == Tool.Offset;
    private bool RoundMode => Tool == Tool.Round;
    private bool RotateMode => Tool == Tool.Rotate;
    private bool ScaleMode => Tool == Tool.Scale;
    private bool LassoMode => Tool == Tool.Lasso;
    private bool MirrorMode => Tool == Tool.Mirror;
    private bool FollowMode => Tool == Tool.FollowMe;
    private bool FreehandMode => Tool == Tool.Freehand;
    private bool ZoomMode => Tool == Tool.Zoom;
    private bool ZoomWindowMode => Tool == Tool.ZoomWindow;
    private bool PositionCameraMode => Tool == Tool.PositionCamera;
    private bool LookAroundMode => Tool == Tool.LookAround;
    private bool WalkMode => Tool == Tool.Walk;
    private bool ProtractorMode => Tool == Tool.Protractor;
    private bool SectionMode => Tool == Tool.Section;
    private bool TagMode => Tool == Tool.Hide;
    private bool DimensionMode => Tool == Tool.Dimension;
    private bool TextMode => Tool == Tool.Text;
    private bool AxesMode => Tool == Tool.Axes;
    private bool Text3DMode => Tool == Tool.Text3D;
    private bool UnionMode => Tool == Tool.Union;

    /// <summary>The boolean tool chosen (∪ ∖ ∩ ✂ ⊘), or null.</summary>
    private BoolTool? BoolMode => UnionMode ? BoolTool.Union : Tool == Tool.Subtract ? BoolTool.Subtract : Tool == Tool.Intersect ? BoolTool.Intersect
        : Tool == Tool.Trim ? BoolTool.Trim : Tool == Tool.Split ? BoolTool.Split : null;
    private bool PolygonMode => Tool == Tool.Polygon;
    private bool ArcMode => Tool == Tool.Arc;
    private bool Arc3Mode => Tool == Tool.Arc3;
    private bool CentreArcMode => Tool == Tool.CentreArc;
    private bool PieMode => Tool == Tool.Pie;
    private bool RotRectMode => Tool == Tool.RotatedRectangle;

    /// <summary>Drawing on a face: rectangle, line or circle.</summary>
    private bool DrawMode => RectMode || LineMode || CircleMode;

    /// <summary>Tools that work on planar faces only.</summary>
    private bool PlanarMode => PushMode || DrawMode || OffsetMode || MirrorMode || PolygonMode || ArcMode || Arc3Mode || FollowMode || FreehandMode || CentreArcMode || PieMode || RotRectMode;

    /// <summary>Another tool: what was going on stopped, the status bar says what it does (choosing the tool it has:
    /// nothing).</summary>
    public void SetTool(Tool tool)
    {
        if (Tool == tool) return;
        Tool = tool;
        _restEdge = null;
        ToolChanged?.Invoke();
        PaintPanel.On = PaintMode;
        if (PaintMode) SetPaint(_paint); else _sampleOnce = false;
        EndDrag(); EndDraw(); EndOffset(); EndRound(); EndRotate(); EndScale(); EndPolygon(); EndArc(); EndSweep(); EndRotRect(); EndFollow(); EndFreehand(); EndView(); EndProtractor(); EndSection(); EndDimension(); EndNoteBox(null); _axStage = 0; _grip = null; _gripHover = null; _letters = null; _unionFirst = null; _mirrorBody = null; HideTag(); MeasureUnit.Text = "mm";
        Render();
        if (!LassoMode) EndLasso();
        StatusText.Text = MirrorMode ? T("⇋ Spiegelen: klik een plat vlak; het lichaam spiegelt erin. Ctrl+klik: een gespiegelde kopie erbij.", "⇋ Mirror: click a flat face; the body is mirrored in it. Ctrl+click: add a mirrored copy.")
            : LassoMode ? T("➰ Lasso: klik, trek een lus om vlakken heen en klik nog eens. Shift: erbij · Ctrl: eraf · Esc: keuze leeg.", "➰ Lasso: click, draw a loop round faces and click again. Shift: add · Ctrl: remove · Esc: clear the selection.")
            : ScaleMode ? T("⇲ Schalen: klik het punt dat blijft staan (op het lichaam). Ctrl: een geschaalde kopie.", "⇲ Scale: click the point that stays put (on the body). Ctrl: a scaled copy.")
            : RotateMode ? T("↻ Draaien: klik een vlak voor het draaipunt (de as staat er haaks op; pijltjes: rode, groene of blauwe as). Ctrl: een gedraaide kopie.", "↻ Rotate: click a face for the pivot (the axis is square to it; arrow keys: red, green or blue axis). Ctrl: a rotated copy.")
            : RoundMode ? T("◠ Afronden: wijs een scherpe rechte rand aan en klik; beweeg voor de straal en klik, of typ de straal + Enter.", "◠ Round: point at a sharp straight edge and click; move for the radius and click, or type the radius + Enter.")
            : OffsetMode ? T("▣ Offset: klik een plat vlak en beweeg de muis naar binnen; klik om vast te zetten. Of typ de afstand + Enter.", "▣ Offset: click a flat face and move the mouse inwards; click to set it. Or type the distance + Enter.")
            : PushMode ? T("⇕ Push/Pull: klik een plat vlak, beweeg de muis en klik nog eens. Of typ de afstand en Enter.", "⇕ Push/Pull: click a flat face, move the mouse and click again. Or type the distance and Enter.")
            : RectMode ? T("▭ Rechthoek: klik een hoek op een plat vlak en dan de andere hoek. Of typ breedte;hoogte + Enter.", "▭ Rectangle: click a corner on a flat face and then the other corner. Or type width;height + Enter.")
            : LineMode ? T("╱ Lijn: klik punt na punt op een plat vlak. Terug op het beginpunt: een eigen vlak; van rand tot rand: het vlak wordt gedeeld. Pijltjes: rood, groen, blauw.", "╱ Line: click point after point on a flat face. Back on the start point: a face of its own; from edge to edge: the face is divided. Arrow keys: red, green, blue.")
            : CircleMode ? T("◯ Cirkel: klik het middelpunt op een plat vlak en dan de rand. Of typ de straal + Enter.", "◯ Circle: click the centre on a flat face and then the edge. Or type the radius + Enter.")
            : CentreArcMode ? T("◜ Boog vanaf het middelpunt: klik het middelpunt op een plat vlak, dan het beginpunt, en beweeg voor de hoek.", "◜ Arc from the centre: click the centre on a flat face, then the start point, and move for the angle.")
            : PieMode ? T("◔ Taartpunt: klik het middelpunt op een plat vlak, dan het beginpunt, en beweeg voor de hoek.", "◔ Pie: click the centre on a flat face, then the start point, and move for the angle.")
            : RotRectMode ? T("◇ Rechthoek onder een hoek: klik een hoek op een plat vlak, dan de tweede hoek (de eerste zijde), en beweeg voor de breedte.", "◇ Rotated rectangle: click a corner on a flat face, then the second corner (the first side), and move for the width.")
            : Text3DMode ? T("A 3D-tekst: klik een punt op een plat vlak (links onder van de tekst). Typ eerst een letterhoogte (mm) + Enter als je die wilt kiezen.", "A 3D Text: click a point on a flat face (bottom left of the text). Type a letter height (mm) + Enter first if you want to choose it.")
            : AxesMode ? T("✛ Assen: klik de nieuwe oorsprong (op een lege plek: de gewone assen terug).", "✛ Axes: click the new origin (on an empty spot: the usual axes back).")
            : DimensionMode ? T("↔ Maatlijn: klik het eerste punt (of een maat om hem weg te halen).", "↔ Dimension: click the first point (or a dimension to remove it).")
            : TextMode ? T("💬 Tekstlabel: klik een punt op het model (of een label om het weg te halen).", "💬 Text: click a point on the model (or a label to remove it).")
            : TagMode ? T("🏷 Verbergen: klik een deel om het te verbergen; klik op een lege plek om alles weer te tonen.", "🏷 Hide: click a part to hide it; click an empty spot to show everything again.")
            : SectionMode ? (_section != null ? T("⊟ Klik een plat vlak voor een nieuwe doorsnede, of Esc om de doorsnede weg te halen.", "⊟ Click a flat face for a new section, or Esc to remove the section.")
                : T("⊟ Doorsnede: klik het platte vlak waar het snijvlak begint.", "⊟ Section: click the flat face where the cutting plane starts."))
            : ProtractorMode ? T("∡ Gradenboog: klik het middelpunt op een vlak.", "∡ Protractor: click the centre on a face.")
            : ZoomMode ? T("🔍 Zoomen: klik, en beweeg omhoog (in) of omlaag (uit); klik om te stoppen.", "🔍 Zoom: click, and move up (in) or down (out); click to stop.")
            : ZoomWindowMode ? T("🔎 Zoomvenster: klik een hoek van het kader.", "🔎 Zoom Window: click a corner of the frame.")
            : PositionCameraMode ? T("📷 Camera plaatsen: klik waar je wilt staan.", "📷 Position Camera: click where you want to stand.")
            : LookAroundMode ? T("👁 Rondkijken: klik en beweeg om je hoofd te draaien.", "👁 Look Around: click and move to turn your head.")
            : WalkMode ? T("🚶 Lopen: ↑/↓ vooruit/achteruit, ←/→ draaien, PageUp/PageDown omhoog/omlaag; klik en beweeg om rond te kijken.", "🚶 Walk: ↑/↓ forward/back, ←/→ turn, PageUp/PageDown up/down; click and move to look around.")
            : FreehandMode ? T("✎ Uit de vrije hand: klik op een plat vlak om te beginnen.", "✎ Freehand: click on a flat face to start.")
            : WorkPlaneMode ? "⊞" + PlaneHelp()
            : FollowMode ? T("↝ Ronddraaien: klik het platte vlak dat eruit moet draaien.", "↝ Follow Me: click the flat face that is to turn out.")
            : BoolMode is { } bm ? FirstAsk(bm)
            : Arc3Mode ? T("⌓ Boog door drie punten: klik het beginpunt op een plat vlak, dan een punt op de boog, dan het eindpunt.", "⌓ 3 point arc: click the start point on a flat face, then a point on the arc, then the end point.")
            : ArcMode ? T("⌒ Boog: klik het beginpunt op een plat vlak, dan het eindpunt, en beweeg voor de bolling. Van rand tot rand deelt hij het vlak.", "⌒ Arc: click the start point on a flat face, then the end point, and move for the bulge. From edge to edge it divides the face.")
            : PolygonMode ? T($"⬡ Veelhoek ({_polySides} hoeken; ↑/↓: meer/minder): klik het middelpunt op een plat vlak, beweeg en klik. Of typ de straal + Enter.", $"⬡ Polygon ({_polySides} sides; ↑/↓: more/fewer): click the centre on a flat face, move and click. Or type the radius + Enter.")
            : PaintMode ? T("🎨 Verfemmer: kies een kleur en klik een vlak. Ctrl: het hele lichaam · Shift: alle vlakken in dezelfde kleur · Alt: kleur overnemen.", "🎨 Paint bucket: choose a colour and click a face. Ctrl: the whole body · Shift: all faces of the same colour · Alt: pick up a colour.")
            : TapeMode ? T("📏 Meetlint: klik een punt en beweeg om te meten; klik een rand en trek eraf voor een hulplijn. Delete: alle hulplijnen weg.", "📏 Tape measure: click a point and move to measure; click an edge and pull away for a guide line. Delete: remove all guide lines.")
            : MoveMode ? T("✥ Verplaatsen: klik een hoekpunt, rand of vlak, beweeg en klik nog eens; wat eraan vastzit buigt mee. Shift: het hele lichaam · Ctrl: een kopie.", "✥ Move: click a vertex, edge or face, move and click again; what is attached to it follows. Shift: the whole body · Ctrl: a copy.")
            : EraserMode ? T("⌫ Gum: klik een getekende lijn, rechthoek of cirkel; de vlakken worden weer één.", "⌫ Eraser: click a drawn line, rectangle or circle; the faces become one again.")
            : T("↖ Selecteren: klik een vlak.", "↖ Select: click a face.");
    }

    private void MouseMoveAt(Point pos)
    {
        _mouseAt = pos;
        if (_doc == null) return;
        UpdateMarks(pos);
        // (the edge the mouse rests on: to line up with later, see InferDirs)
        if (Tool is Tool.Line or Tool.Move or Tool.Tape) RestOnEdge(pos);
        if (_movePick != null) { MoveTo(pos); return; }
        if (_dragFace != null) { DragTo(pos); return; }
        if (_drawFace != null) { DrawTo(pos); return; }
        if (_gKind != null) { GroundTo(pos); return; }
        if (WorkPlaneMode) { GripMove(pos); if (_grip != null) return; }
        if (_offsetFace != null) { OffsetTo(pos); return; }
        if (_roundEdge != null) { RoundTo(pos); return; }
        if (_rotStage > 0) { RotateTo(pos); return; }
        if (_scStage > 0) { ScaleTo(pos); return; }
        if (_lasso != null) { LassoTo(pos); return; }
        if (_polyFace != null) { PolygonTo(pos); return; }
        if (_arcFace != null) { ArcTo(pos); return; }
        if (_swFace != null) { SweepTo(pos); return; }
        if (_rrFace != null) { RotRectTo(pos); return; }
        if (_fmFace != null) { FollowTo(pos); return; }
        if (_fhFace != null) { FreehandTo(pos); return; }
        if (_viewAnchor != null) { ViewTo(pos); return; }
        if (_prStage > 0) { ProtractorTo(pos); return; }
        if (_secFace != null) { SectionTo(pos); return; }
        if (_dimStage > 0) { DimensionTo(pos); return; }
        var hit = FaceAt(pos);
        FaceRef? h = hit?.Face;
        if (h is { } hf && PlanarMode && !(PushMode ? Pushable(hf) : IsPlanar(hf))) h = null;
        if (h != _hover) { _hover = h; UpdateHighlight(); }
        if (DrawMode) HoverDraw(h, h != null ? hit?.Point : null, pos);
        if (EraserMode) HoverErase(hit, pos);
        if (MoveMode) HoverMove(hit, pos);
        // ⇕ over a rounding, pin or hole: its radius next to the mouse (as DesignSpark)
        if (PushMode && h is { } rh && HasRadius(_doc, rh)) ShowTag("R " + Mm(RadiusOf(_doc, rh)) + T(" mm", " mm"), pos);
        else HideTag();
        // ⇕ close to a sharp edge: a click rounds it (as DesignSpark's pull on an edge)
        if (PushMode)
        {
            if (SharpEdgeNear(hit, pos) is { } se) { ShowRoundEdges(se.Body, se.Edge); StatusText.Text = T("⇕ Klik en sleep om deze rand af te ronden (of typ de straal + Enter na de klik).", "⇕ Click and drag to round this edge (or type the radius + Enter after the click)."); }
            else if (_roundSel == null && _sketch != null && _roundEdge == null) ShowSketch([]);
        }
        if (TapeMode) HoverTape(hit, pos);
        if (RoundMode) HoverRound(hit, pos);
    }

    // ------------------------------------------------------------------ groups (as SketchUp's)

    /// <summary>Tools that change faces: inside a group only (a closed one is taken as a whole).</summary>
    private bool EditsFaces => PushMode || DrawMode || PolygonMode || ArcMode || Arc3Mode || CentreArcMode || PieMode || RotRectMode
        || FreehandMode || OffsetMode || RoundMode || EraserMode || FollowMode || BoolMode != null;

    /// <summary>
    /// A click where a group is (as SketchUp): with ↖ the whole group picked (Ctrl: a group or body added to what is
    /// picked, or taken off it); a tool that changes faces does nothing on a closed group (it says to open it); moving,
    /// turning, scaling, mirroring and painting take the whole group (see <see cref="Whole"/>). Whether it was used.
    /// </summary>
    private bool GroupClick(Point pos)
    {
        if (_doc is not { } doc) return false;
        bool select = Tool == Tool.Select;
        var unit = FaceAt(pos) is { } h ? doc.UnitOf(h.Face.Body, _openGroup) : null;
        if (select && (Mods & ModifierKeys.Control) != 0 && unit is { } cu)
        {
            // (the body picked with a plain click before goes along)
            if (_pickedUnits.Count == 0 && (_selected?.Body ?? _multi?.Body) is int sb && doc.UnitOf(sb, _openGroup) is { } su && su != cu) _pickedUnits.Add(su);
            if (!_pickedUnits.Remove(cu)) _pickedUnits.Add(cu);
            _selected = null; _multi = null; UpdateHighlight();
            StatusText.Text = T($"📦 {_pickedUnits.Count} gekozen. Ctrl+G maakt er een groep van; Ctrl+klik: erbij of eraf.", $"📦 {_pickedUnits.Count} picked. Ctrl+G makes a group of them; Ctrl+click: add or take off.");
            return true;
        }
        if (unit is not { Group: { } g })
        {
            if (select && _pickedUnits.Count > 0) { _pickedUnits.Clear(); UpdateHighlight(); }
            return false;
        }
        string name = doc.GroupName(g);
        bool instance = doc.Group(g)?.Component != null;
        if (select)
        {
            _pickedUnits.Clear(); _pickedUnits.Add(unit.Value);
            _selected = null; _multi = null; UpdateHighlight();
            StatusText.Text = instance
                ? T($"{InstanceLabel(doc, g)}: dubbelklik om erin te werken (alle exemplaren veranderen mee) · Ctrl+Shift+G maakt er losse lichamen van.", $"{InstanceLabel(doc, g)}: double click to work in it (all instances change along) · Ctrl+Shift+G makes it loose bodies.")
                : T($"📦 {name} ({doc.Members(g).Count} lichamen): dubbelklik om erin te werken · Ctrl+Shift+G heft de groep op.", $"📦 {name} ({doc.Members(g).Count} bodies): double click to work in it · Ctrl+Shift+G explodes the group.");
            return true;
        }
        if (EditsFaces)
        {
            StatusText.Text = instance
                ? T($"🧩 {name} is een component: dubbelklik erop met ↖ om erin te werken.", $"🧩 {name} is a component: double click it with ↖ to work in it.")
                : T($"📦 {name} is een groep: dubbelklik erop met ↖ om erin te werken.", $"📦 {name} is a group: double click it with ↖ to work in it.");
            return true;
        }
        return false;
    }

    /// <summary>A double click (with ↖): a closed group opened, beside the open group (on nothing, or outside it) that one
    /// closed. Whether it was used.</summary>
    private bool DoubleClickAt(Point pos)
    {
        if (_doc is not { } doc || Tool != Tool.Select || ActionBusy) return false;
        var unit = FaceAt(pos) is { } h ? doc.UnitOf(h.Face.Body, _openGroup) : null;
        if (unit is { Group: { } g }) { OpenGroup(g); return true; }
        if (unit == null && _openGroup != null) { CloseGroup(); return true; }
        return false;
    }

    private void OpenGroup(int g)
    {
        _openGroup = g; _pickedUnits.Clear(); _selected = null; _multi = null; _hover = null;
        Render();
        // (in the structure tree: unfolded to it)
        _treeGroup = g; _window.StructurePickGroup(g);
        int? ig = _doc!.InstanceAround(g);
        int copies = ig is { } i0 ? _doc.Instances(_doc.Group(i0)!.Component!.Value).Count : 0;
        StatusText.Text = copies > 1
            ? T($"🧩 In {_doc.GroupName(g)}: wat je verandert, verandert in alle {copies} exemplaren van {_doc.GroupName(ig!.Value)}. Esc of dubbelklik ernaast sluit hem.", $"🧩 In {_doc.GroupName(g)}: what you change, changes in all {copies} instances of {_doc.GroupName(ig!.Value)}. Esc or a double click beside it closes it.")
            : T($"📦 In {_doc.GroupName(g)}: wat je nu maakt komt erin. Esc of dubbelklik ernaast sluit hem.", $"📦 In {_doc.GroupName(g)}: what you make now goes into it. Esc or a double click beside it closes it.");
        Log($"open group {g}");
    }

    /// <summary>The open group closed (one level: the group it is in is open then).</summary>
    private void CloseGroup()
    {
        if (_openGroup is not { } g || _doc == null) return;
        _openGroup = _doc.Group(g)?.Parent;
        _pickedUnits.Clear(); _pickedUnits.Add(GroupUnit.OfGroup(g)); _selected = null; _multi = null; _hover = null;
        Render();
        string icon = _doc.Group(g)?.Component != null ? "🧩" : "📦";
        StatusText.Text = _openGroup is { } up ? T($"{icon} Terug in {_doc.GroupName(up)}.", $"{icon} Back in {_doc.GroupName(up)}.")
            : icon == "🧩" ? T($"🧩 {_doc.GroupName(g)} gesloten.", $"🧩 {_doc.GroupName(g)} closed.") : T("📦 Groep gesloten.", "📦 Group closed.");
        Log($"close group {g}");
    }

    /// <summary>Ctrl+G: a group of what is picked (or of the body picked); Ctrl+K (<paramref name="component"/>): a
    /// component.</summary>
    private void MakeGroupOfPicked(bool component = false)
    {
        if (_doc is not { } before) return;
        var units = _pickedUnits.ToList();
        if (units.Count == 0 && (_selected?.Body ?? _multi?.Body) is int b && before.UnitOf(b, _openGroup) is { } u) units.Add(u);
        try
        {
            var (doc, g) = component ? before.MakeComponent(units) : before.MakeGroup(units);
            Commit(before, doc);
            _pickedUnits.Clear(); _pickedUnits.Add(GroupUnit.OfGroup(g)); _selected = null; _multi = null;
            Render();
            StatusText.Text = component
                ? T($"🧩 {doc.GroupName(g)} gemaakt ({doc.Members(g).Count} lichamen). Kopieer hem (✥ met Ctrl): wat je in één exemplaar verandert, verandert in alle.", $"🧩 {doc.GroupName(g)} made ({doc.Members(g).Count} bodies). Copy it (✥ with Ctrl): what you change in one instance changes in all.")
                : T($"📦 {doc.GroupName(g)} gemaakt ({doc.Members(g).Count} lichamen). Dubbelklik om erin te werken.", $"📦 {doc.GroupName(g)} made ({doc.Members(g).Count} bodies). Double click to work in it.");
            Log($"make {(component ? "component" : "group")} {g} of {string.Join(",", units.Select(x => x.Group is { } gg ? $"group {gg}" : $"body {x.Body}"))}");
        }
        catch (PushPullException ex) { StatusText.Text = "⛔ " + ex.Message; }
    }

    /// <summary>A component instance made unique: a component of its own, no longer changing with the others.</summary>
    private void MakeUnique(int g)
    {
        if (_doc is not { } before) return;
        try
        {
            string name = before.GroupName(g);
            var doc = before.MakeUnique(g);
            Commit(before, doc);
            Render();
            StatusText.Text = T($"🧩 Dit exemplaar van {name} is nu {doc.GroupName(g)}: het verandert niet meer met de andere mee.", $"🧩 This instance of {name} is now {doc.GroupName(g)}: it no longer changes with the others.");
            Log($"make unique {g}");
        }
        catch (PushPullException ex) { StatusText.Text = "⛔ " + ex.Message; }
    }

    /// <summary>What the status bar says of a component instance: "🧩 Poot (3 exemplaren)".</summary>
    private string InstanceLabel(SketchDocument doc, int g) =>
        $"🧩 {doc.GroupName(g)} ({T($"{doc.Instances(doc.Group(g)!.Component!.Value).Count} exemplaren", $"{doc.Instances(doc.Group(g)!.Component!.Value).Count} instances")})";

    /// <summary>Ctrl+Shift+G: the group picked (or else the open one) taken away; what was in it stays, picked.</summary>
    private void UngroupPicked()
    {
        if (_doc is not { } before) return;
        int? g = _pickedUnits.Count == 1 && _pickedUnits[0].Group is { } pg ? pg : _openGroup;
        if (g is not { } id) { StatusText.Text = T("📦 Kies eerst een groep (klik erop met ↖).", "📦 First pick a group (click it with ↖)."); return; }
        string name = before.GroupName(id);
        if (_openGroup == id) _openGroup = before.Group(id)?.Parent;
        var doc = before.Ungroup(id);
        Commit(before, doc);
        _pickedUnits.Clear();
        foreach (var x in before.Members(id).Select(b => doc.UnitOf(b, _openGroup)).OfType<GroupUnit>().Distinct()) _pickedUnits.Add(x);
        Render();
        StatusText.Text = T($"📦 {name} opgeheven.", $"📦 {name} exploded.");
        Log($"explode group {id}");
    }

    /// <summary>A whole-body change (turn, scale, mirror, move; with whether to copy) of body <paramref name="body"/>: of
    /// its whole group when it lies in a closed one. <paramref name="motion"/>: the same change as a placement, for the
    /// component instances in the group (null: none, such as scaling along one axis).</summary>
    private SketchDocument Whole(SketchDocument d, int body, bool copy, Func<SketchDocument, int, bool, (SketchDocument Doc, int Body)> op, Placement? motion) =>
        WholeOf(d, body, copy, op, motion).Doc;

    /// <summary>As <see cref="Whole"/>, with what was changed: the group (the copy of it) when a closed group was taken,
    /// else the body (the copy of it).</summary>
    private (SketchDocument Doc, GroupUnit Unit) WholeOf(SketchDocument d, int body, bool copy, Func<SketchDocument, int, bool, (SketchDocument Doc, int Body)> op, Placement? motion)
    {
        if (d.UnitOf(body, _openGroup) is { Group: { } g })
        {
            var (doc, ng) = d.TransformGroup(g, op, copy, motion);
            return (doc, GroupUnit.OfGroup(ng));
        }
        var (bd, nb) = op(d, body, copy);
        return (bd, GroupUnit.OfBody(nb));
    }

    /// <summary>The group a body is taken with (it lies in a closed one), or null.</summary>
    private int? GroupTaken(int body) => _doc?.UnitOf(body, _openGroup)?.Group;

    /// <summary>The left button down at a point of the view; whether it was used.</summary>
    private bool PressAt(Point at)
    {
        // (an empty window: drawing on the ground starts a new document)
        if (_doc == null && DrawMode && GroundAt(at) != null) NewDocument();
        bool before = ActionBusy;
        bool used = _doc != null && ClickAt(at);
        // (a press that started something: let go after moving, and it is set there, as a drag; a plain click goes on as
        // click, move, click)
        _pressAt = !before && ActionBusy ? at : null;
        if (_pressAt != null) View.CaptureMouse();
        return used;
    }

    /// <summary>The left button up at a point of the view; whether it set what a press started (a drag).</summary>
    private bool ReleaseAt(Point up)
    {
        if (_pressAt is not { } down) return false;
        _pressAt = null;
        View.ReleaseMouseCapture();
        // (dragged far enough to mean it: the release is the click that sets it)
        return (up - down).Length > 6 && ActionBusy && _doc != null && ClickAt(up);
    }

    /// <summary>Where the left button went down to start an action (for dragging), or null.</summary>
    private Point? _pressAt;

    /// <summary>Whether an action is in progress that a next click finishes or takes on (push/pull, drawing, moving, the
    /// work plane's handle, …).</summary>
    private bool ActionBusy => _dragFace != null || _drawFace != null || _gKind != null || _grip != null || _movePick != null
        || _offsetFace != null || _roundEdge != null || _rotStage > 0 || _scStage > 0 || _polyFace != null || _arcFace != null
        || _swFace != null || _rrFace != null || _fmFace != null || _fhFace != null || _secFace != null || _dimStage > 0
        || _prStage > 0 || _tapeStart != null;

    /// <summary>A left click at a point of the view; whether it was used (not passed on to the view).</summary>
    private bool ClickAt(Point pos)
    {
        bool handled = false;
        if (!ActionBusy && GroupClick(pos)) return true;
        if (_movePick != null) { handled = true; MoveTo(pos); FinishMove(); return handled; }
        if (_dragFace != null) { handled = true; Finish(_dragDist); return handled; }
        if (_drawFace != null) { handled = true; DrawTo(pos); FinishDraw(); return handled; }
        if (_gKind != null) { handled = true; GroundClick(pos); return handled; }
        if (WorkPlaneMode) { handled = true; if (!GripClick(pos)) WorkPlaneClick(pos); return handled; }
        if (_offsetFace != null) { handled = true; OffsetTo(pos); FinishOffset(); return handled; }
        if (_roundEdge != null) { handled = true; RoundTo(pos); FinishRound(); return handled; }
        if (_rotStage > 0) { handled = true; RotateClick(pos); return handled; }
        if (_scStage > 0) { handled = true; ScaleClick(pos); return handled; }
        if (LassoMode) { handled = true; if (_lasso == null) StartLasso(pos); else FinishLasso(); return handled; }
        if (MirrorMode) { handled = true; MirrorAt(FaceAt(pos)); return handled; }
        if (_polyFace != null) { handled = true; PolygonTo(pos); FinishPolygon(); return handled; }
        if (PolygonMode) { handled = true; StartPolygon(FaceOrLoose(pos)); return handled; }
        if (_arcFace != null) { handled = true; ArcClick(pos); return handled; }
        if (ArcMode || Arc3Mode) { handled = true; StartArc(FaceOrLoose(pos), pos); return handled; }
        if (_swFace != null) { handled = true; SweepClick(pos); return handled; }
        if (CentreArcMode || PieMode) { handled = true; StartSweep(FaceOrLoose(pos), pos); return handled; }
        if (_rrFace != null) { handled = true; RotRectClick(pos); return handled; }
        if (RotRectMode) { handled = true; StartRotRect(FaceOrLoose(pos), pos); return handled; }
        if (_fmFace != null) { handled = true; FollowClick(pos); return handled; }
        if (FollowMode) { handled = true; StartFollow(FaceAt(pos)); return handled; }
        if (_fhFace != null) { handled = true; FreehandTo(pos); FinishFreehand(pos); return handled; }
        if (FreehandMode) { handled = true; StartFreehand(FaceOrLoose(pos), pos); return handled; }
        if (ZoomMode || ZoomWindowMode || LookAroundMode || WalkMode) { handled = true; ViewClick(pos); return handled; }
        if (PositionCameraMode) { handled = true; PlaceCamera(pos); return handled; }
        if (ProtractorMode) { handled = true; ProtractorClick(pos); return handled; }
        if (_secFace != null) { handled = true; SectionTo(pos); SetSection(_secDist); return handled; }
        if (SectionMode) { handled = true; StartSection(FaceAt(pos)); return handled; }
        if (TagMode) { handled = true; HideAt(FaceAt(pos)); return handled; }
        if (DimensionMode) { handled = true; DimensionClick(pos); return handled; }
        if (TextMode) { handled = true; TextClick(pos); return handled; }
        if (AxesMode) { handled = true; AxesClick(pos); return handled; }
        if (Text3DMode) { handled = true; Text3DClick(pos); return handled; }
        if (BoolMode is { } bop) { handled = true; BoolClick(bop, FaceAt(pos)); return handled; }
        if (ScaleMode) { handled = true; StartScale(FaceAt(pos)); return handled; }
        if (RotateMode) { handled = true; StartRotate(FaceAt(pos), pos); return handled; }
        if (PushMode && SharpEdgeNear(FaceAt(pos), pos) is { } pe)
        {
            // ⇕ on a sharp edge: round it, the radius with the mouse
            handled = true;
            _roundSel = (pe.Body, [pe.Edge]);
            _roundEdge = pe; _roundRadius = 0; _roundOk = false; _typed = "";
            _last = null; _lastMove = null; _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null; _selected = null; _hover = null;
            UpdateHighlight();
            ShowRoundEdges();
            MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = "";
            StatusText.Text = T("⇕ Beweeg de muis van de rand af voor de straal; klik om vast te zetten, of typ de straal + Enter. Esc breekt af.", "⇕ Move the mouse away from the edge for the radius; click to set it, or type the radius + Enter. Esc cancels.");
            return handled;
        }
        if (RoundMode) { handled = true; StartRound(FaceAt(pos), pos); return handled; }
        var hit = FaceAt(pos);
        if (OffsetMode)
        {
            if (hit is not { } oh) return handled;
            handled = true;
            if (!IsPlanar(oh.Face)) { StatusText.Text = T("▣ Offset kan alleen op platte vlakken.", "▣ Offset only works on flat faces."); return handled; }
            StartOffset(oh.Face);
            return handled;
        }
        if (EraserMode) { handled = true; EraseAt(FaceAt(pos), pos); return handled; }
        if (PaintMode) { handled = true; PaintAt(FaceAt(pos)); return handled; }
        if (TapeMode) { handled = true; if (_tapeStart == null) StartTape(FaceAt(pos), pos); else FinishTape(FaceAt(pos), pos); return handled; }
        if (MoveMode) { handled = true; StartMove(FaceAt(pos), pos); return handled; }
        if (DrawMode)
        {
            // (not on a face: on the ground)
            // (beside every face: on a loose line's snap point there, else on the work plane)
            if (hit is null && LooseHit(pos) is { } lh) hit = lh;
            // (a work plane set with ⊞ goes before the model, as DesignSpark's sketch plane, until ⊞ on empty puts it back on
            // the ground; a face lying in it is still drawn on as a face)
            if (hit is { } fh && OnSetPlane(fh.Point, pos)) hit = null;
            if (hit is not { } dh) { if (GroundAt(pos) != null) { handled = true; GroundClick(pos); } return handled; }
            handled = true;
            if (!IsPlanar(dh.Face)) { StatusText.Text = T("Tekenen kan (nog) alleen op platte vlakken.", "Drawing only works on flat faces (for now)."); return handled; }
            StartDraw(dh.Face, V(dh.Point), (Point?)pos);
            return handled;
        }
        if (!PushMode)
        {
            // (Shift: a face in or out of the choice; a plain click: just that face)
            if ((Mods & ModifierKeys.Shift) != 0 && hit is { } sh)
            {
                ToggleChosen(sh.Face);
                return handled;
            }
            _selected = hit?.Face;
            _multi = hit is { } one ? (one.Face.Body, new HashSet<int> { one.Face.Face }) : null;
            UpdateHighlight();
            if (_selected is { } s) StatusText.Text = Describe(s);
            return handled;
        }
        if (hit is not { } h) return handled;
        if (!Pushable(h.Face)) { StatusText.Text = T("⇕ Alleen platte vlakken en cilinders kunnen (nog) geduwd of getrokken worden.", "⇕ Only flat faces and cylinders can be pushed or pulled (for now)."); return handled; }
        handled = true;
        StartDrag(h.Face, h.Point);
        return handled;
    }

    private string Describe(FaceRef f)
    {
        var face = _doc!.Bodies[f.Body].Faces[f.Face];
        double area = _doc.TrianglesOf(f).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length / 2) * Display * Display;
        string kind = face.Surface switch
        {
            BrepPlane => T("plat vlak", "flat face"),
            BrepCylinder c => T($"cilinder Ø {c.Radius * 2 * Display:0.##} mm", $"cylinder Ø {c.Radius * 2 * Display:0.##} mm"),
            BrepSphere s => T($"bol Ø {s.Radius * 2 * Display:0.##} mm", $"sphere Ø {s.Radius * 2 * Display:0.##} mm"),
            BrepCone => T("kegel", "cone"),
            BrepTorus => T("torus", "torus"),
            _ => T("vlak", "face"),
        };
        return T($"↖ {kind}, {area:0.##} mm² (vlak {f.Face})", $"↖ {kind}, {area:0.##} mm² (face {f.Face})");
    }

    // ------------------------------------------------------------------ drawing: rectangle and line

    private static Vec3 V(Point3D p) => new(p.X / Display, p.Y / Display, p.Z / Display);

    // ------------------------------------------------------------------ structure tree (the window shows it; what it does is here)

    private SketchDocument? _treeDoc;
    private (int?, int?) _treePicked;
    private (int, int, int, int) _treeCounts = (-1, -1, -1, -1);
    private readonly HashSet<SketchKind> _sketchHidden = new();

    /// <summary>Whether a kind of drawing aid is shown (its box in the tree ticked).</summary>
    public bool SketchVisible(SketchKind k) => !_sketchHidden.Contains(k);

    /// <summary>The document worked on (for the structure tree).</summary>
    public SketchDocument? Document => _doc;

    /// <summary>Whether body <paramref name="b"/> is shown (its box in the tree ticked).</summary>
    public bool IsBodyShown(int b) => _doc == null || b >= _doc.Bodies.Count || !_hidden.Contains(_doc.Bodies[b]);

    /// <summary>How many there are of each kind of drawing aid (the tree's sketch branch).</summary>
    public Dictionary<SketchKind, int> SketchCounts => new()
    {
        [SketchKind.Loose] = _treeCounts.Item1, [SketchKind.Guides] = _treeCounts.Item2, [SketchKind.Dimensions] = _treeCounts.Item3, [SketchKind.Notes] = _treeCounts.Item4,
    };

    /// <summary>The storeys of the document opened (see <see cref="SketchDocument.Levels"/>), each shown or not.</summary>
    private IReadOnlyList<SketchLevel> _levels = [];
    private readonly HashSet<SketchLevel> _levelsOff = new(ReferenceEqualityComparer.Instance);

    /// <summary>The bodies on the storeys switched off (not drawn, as the bodies hidden in the tree).</summary>
    private readonly HashSet<BrepBody> _levelHidden = new(ReferenceEqualityComparer.Instance);

    /// <summary>The storeys of the document opened, each model's from the bottom up (for the list under the tree).</summary>
    public IReadOnlyList<SketchLevel> Levels => _levels;

    /// <summary>Whether a storey's box in the list is ticked.</summary>
    public bool IsLevelShown(SketchLevel level) => !_levelsOff.Contains(level);

    private void SetLevels(IReadOnlyList<SketchLevel> levels)
    {
        _levels = levels; _levelsOff.Clear(); _levelHidden.Clear();
        _window.LevelsChanged();
    }

    /// <summary>A storey's box in the list: its bodies shown or hidden (besides what the tree hides).</summary>
    public void SetLevelShown(SketchLevel level, bool shown)
    {
        if (shown) _levelsOff.Remove(level); else _levelsOff.Add(level);
        _levelHidden.Clear();
        foreach (var off in _levelsOff) _levelHidden.UnionWith(off.Bodies);
        Render();
        _window.LevelsChanged();
        StatusText.Text = T($"🏢 {level.Name} ({level.Model}) {(shown ? "weer zichtbaar" : "verborgen")}.", $"🏢 {level.Name} ({level.Model}) {(shown ? "shown again" : "hidden")}.");
        Log($"level {level.Model} / {level.Name}: {(shown ? "shown" : "hidden")}");
    }

    /// <summary>Whether a body is drawn: not hidden in the tree, nor on a storey switched off.</summary>
    private bool Drawn(BrepBody body) => !_hidden.Contains(body) && !_levelHidden.Contains(body);

    /// <summary>Bodies' boxes in the tree (one body's, or a group's all at once): shown or hidden, the view drawn
    /// again once.</summary>
    public void SetBodyShown(IReadOnlyList<int> bodies, bool shown)
    {
        if (_doc is not { } doc) return;
        var these = bodies.Where(b => b < doc.Bodies.Count).ToList();
        if (these.Count == 0) return;
        foreach (int b in these)
            if (shown) _hidden.Remove(doc.Bodies[b]); else _hidden.Add(doc.Bodies[b]);
        Render();
        string what = these.Count == 1 ? doc.BodyName(these[0]) : T($"{these.Count} lichamen", $"{these.Count} bodies");
        StatusText.Text = T($"🌲 {what} {(shown ? "weer zichtbaar" : "verborgen")}.", $"🌲 {what} {(shown ? "shown again" : "hidden")}.");
    }

    /// <summary>A body renamed in the tree.</summary>
    public void RenameBody(int b, string name)
    {
        if (_doc is not { } before || b >= before.Bodies.Count) return;
        string old = before.BodyName(b);
        Commit(before, before.Rename(b, name));
        RefreshStructure();
        StatusText.Text = T($"🌲 {old} heet nu {_doc!.BodyName(b)}.", $"🌲 {old} is now called {_doc!.BodyName(b)}.");
        Log($"rename body {b}: {_doc.BodyName(b)}");
    }

    /// <summary>A group picked in the tree.</summary>
    public void PickGroup(int g)
    {
        if (_doc?.Group(g) == null) return;
        _pickedUnits.Clear(); _pickedUnits.Add(GroupUnit.OfGroup(g)); _treeGroup = g;
        _selected = null; _multi = null;
        UpdateHighlight();
        StatusText.Text = T($"🌲 📦 {_doc.GroupName(g)} ({_doc.Members(g).Count} lichamen).", $"🌲 📦 {_doc.GroupName(g)} ({_doc.Members(g).Count} bodies).");
    }

    /// <summary>A group renamed in the tree.</summary>
    public void RenameGroup(int g, string name)
    {
        if (_doc is not { } before || before.Group(g) == null) return;
        string old = before.GroupName(g);
        Commit(before, before.RenameGroup(g, name));
        RefreshStructure();
        StatusText.Text = T($"🌲 {old} heet nu {_doc!.GroupName(g)}.", $"🌲 {old} is now called {_doc!.GroupName(g)}.");
        Log($"rename group {g}: {_doc.GroupName(g)}");
    }

    /// <summary>The tree's menu: a group opened or exploded, or a group made (of what is picked, or of body <paramref name="b"/>).</summary>
    public void GroupMenu(GroupAction what, int? g, int? b)
    {
        if (_doc == null) return;
        switch (what)
        {
            case GroupAction.Open when g is { } og:
                // (a group inside another: those round it open too)
                OpenGroup(og);
                break;
            case GroupAction.Explode when g is { } eg:
                _pickedUnits.Clear(); _pickedUnits.Add(GroupUnit.OfGroup(eg));
                UngroupPicked();
                break;
            case GroupAction.Make or GroupAction.MakeComponent:
                // (the body the menu was on, with what is picked when it lies in the same group)
                if (b is int mb && mb < _doc.Bodies.Count)
                {
                    var unit = GroupUnit.OfBody(mb);
                    if (!_pickedUnits.Contains(unit)) { if (_pickedUnits.Any(u => (u.Group is { } ug ? _doc.Group(ug)?.Parent : _doc.GroupOf(u.Body!.Value)) != _doc.GroupOf(mb))) _pickedUnits.Clear(); _pickedUnits.Add(unit); }
                }
                MakeGroupOfPicked(what == GroupAction.MakeComponent);
                break;
            case GroupAction.MakeUnique when g is { } ug:
                MakeUnique(ug);
                break;
        }
    }

    /// <summary>A kind of drawing aid shown or hidden in the tree.</summary>
    public void SetSketchShown(SketchKind k, bool shown)
    {
        if (shown) _sketchHidden.Remove(k); else _sketchHidden.Add(k);
        RenderLoose(_shown ?? _doc); RenderGuides(); PlaceNotes();
    }

    /// <summary>A body (<paramref name="f"/> null) or a face of it picked in the tree.</summary>
    public void PickInTree(int b, int? f)
    {
        if (_doc == null || b >= _doc.Bodies.Count) return;
        if (f is int face) { _selected = new FaceRef(b, face); _multi = null; }
        else { _selected = null; _multi = (b, Enumerable.Range(0, _doc.Bodies[b].Faces.Count).ToHashSet()); }
        _treePicked = (b, f);
        UpdateHighlight();
        StatusText.Text = f is int ff ? T($"🌲 {_doc.BodyName(b)}, vlak {ff}.", $"🌲 {_doc.BodyName(b)}, face {ff}.")
            : T($"🌲 {_doc.BodyName(b)} ({_doc.Bodies[b].Faces.Count} vlakken).", $"🌲 {_doc.BodyName(b)} ({_doc.Bodies[b].Faces.Count} faces).");
    }

    /// <summary>The tree made anew for the document as it is now.</summary>
    private void RefreshStructure()
    {
        _treeDoc = _doc;
        _treeCounts = Counts();
        _treePicked = (null, null);
        _window.StructureChanged();
    }

    private (int, int, int, int) Counts() => (_doc?.Lines.Count ?? 0, _guides.Count, _dims.Count, _notes.Count);

    /// <summary>Made anew when the number of drawing aids changed (cheap otherwise: this runs as the camera moves).</summary>
    private void RefreshStructureCounts()
    {
        if (Counts() != _treeCounts) RefreshStructure();
    }

    // ------------------------------------------------------------------ language

    /// <summary>🌐: the other language, at once (what the status bar says changes with the next step), and kept.</summary>
    public void SwitchLanguage()
    {
        Lang.English = !Lang.English;
        Settings.SaveEnglish(Lang.English);
        StatusText.Text = T("Taal: Nederlands.", "Language: English.");
        Log($"language: {(Lang.English ? "en" : "nl")}");
    }

    // ------------------------------------------------------------------ merge (samenvoegen)

    /// <summary>Whether ⇕ joins what it pulls against another part with it (the box bottom right; kept as set).</summary>
    private bool Join => JoinBox.On;

    /// <summary>A text in the language chosen (see <see cref="Lang"/>).</summary>
    private static string T(string nl, string en) => Lang.T(nl, en);


    /// <summary>The direction of a rectangle's first side on a face with normal n: the world axis that lies most in its
    /// plane (as SketchUp lines rectangles up with the axes).</summary>
    private static Vec3 RectAxis(Vec3 n)
    {
        var a = new[] { _axisRed, _axisGreen, _axisBlue }.MinBy(x => Math.Abs(x.Dot(n)));
        return (a - n * a.Dot(n)).Normalized();
    }

    private Vec3 NormalOf(FaceRef f) => FaceGeometry.Normal(_doc!.Bodies[f.Body].Faces[f.Face]);

    /// <summary>Where the ray under the mouse meets the plane of a face (metres).</summary>
    private Vec3? OnPlane(FaceRef f, Point pos)
    {
        var face = _doc!.Bodies[f.Body].Faces[f.Face];
        var n = FaceGeometry.Normal(face); var o = ((BrepPlane)face.Surface).Origin;
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return null;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        double den = dir.Dot(n);
        if (Math.Abs(den) < 1e-9) return null;
        return org + dir * ((o - org).Dot(n) / den);
    }

    /// <summary>The point on the face's outer edge to snap to near the mouse (a corner or a middle within 12 pixels,
    /// otherwise the nearest point on an edge within 20 pixels), with what it is.</summary>
    private (Vec3 Point, Snap Kind)? EdgeSnap(FaceRef f, Vec3 at, Point pos)
    {
        var body = _doc!.Bodies[f.Body];
        var n = NormalOf(f);
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        double pxPerMm = (Screen(at + RectAxis(n) * 0.001) - Screen(at)).Length;
        double tol = pxPerMm > 1e-6 ? 12 / pxPerMm * 0.001 : 0.001;
        if (body.Faces[f.Face].Surface is BrepPlane epl && _guides.Count > 0)
        {
            var crossings = _guides.Where(g => Guides.InPlane(g, epl.Origin, n))
                .SelectMany(g => PlaneFaces(f).SelectMany(pf => body.Faces[pf].Loops[0]).Select(c => body.Edges[c.Edge]).Where(e => e.Curve is BrepLine && e.Start >= 0)
                    .Select(e => Guides.MeetSegment(g, body.Vertices[e.Start], body.Vertices[e.End])))
                .Where(q => q != null).Select(q => q!.Value).Select(q => (Q: q, Px: (Screen(q) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
            if (crossings.Count > 0) return (crossings[0].Q, Snap.GuideCrossing);
        }
        if (body.Faces[f.Face].Surface is BrepPlane cpl && CentreSnap(n, cpl.Origin, pos) is { } cs) return (cs, Snap.Centre);
        var loose = body.Faces[f.Face].Surface is BrepPlane lpl ? LooseSnap(n, lpl.Origin, at, pos) : null;
        if (loose is { Kind: Snap.LooseEnd or Snap.LooseMiddle } lk) return (lk.Point, lk.Kind);
        var onEdge = PushPullMe.Core.Drawing.NearestOnEdges(body, PlaneFaces(f), at, tol) is { } s && (Screen(s.Point) - pos).Length < 20 ? s : ((Vec3 Point, Snap Kind)?)null;
        if (loose is { } lo && (onEdge is not { } oe || (Screen(lo.Point) - pos).Length < (Screen(oe.Point) - pos).Length)) return (lo.Point, lo.Kind);
        return onEdge;
    }

    /// <summary>The faces of f's body in f's plane (f first): a line may run across all of them, and snaps to their
    /// edges.</summary>
    private List<int> PlaneFaces(FaceRef f) => PushPullMe.Core.Drawing.CoplanarFaces(_doc!.Bodies[f.Body], f.Face);

    /// <summary>Whether q lies on an edge of a face in f's plane.</summary>
    private bool OnPlaneEdge(FaceRef f, Vec3 q) =>
        PushPullMe.Core.Drawing.NearestOnEdges(_doc!.Bodies[f.Body], PlaneFaces(f), q, 0) is { } e && (e.Point - q).Length < 1e-6;

    /// <summary>Before the first click: the face and the point under the mouse (for a typed start).</summary>
    private (FaceRef Face, Vec3 Point)? _drawHover;

    /// <summary>A line: the direction of the edge it starts on (to snap square to it).</summary>
    private Vec3? _drawEdgeDir;

    /// <summary>Before the first click, with ▭ or ╱: what a typed size would mean, shown in the box and the status bar.</summary>
    private void HoverDraw(FaceRef? h, Point3D? at, Point pos)
    {
        _drawHover = h is { } hf && at is { } a ? (hf, V(a)) : null;
        if (_drawHover is not { } dh) { ShowSketch([]); return; }
        var body = _doc!.Bodies[dh.Face.Body];
        var n = NormalOf(dh.Face);
        if (_typed.Length > 0) return;
        if (CircleMode)
        {
            ShowSketch([dh.Point], n);
            MeasureLabel.Text = T("Straal", "Radius");
            MeasureBox.Text = "";
            StatusText.Text = T("◯ Klik het middelpunt van de cirkel.", "◯ Click the centre of the circle.");
            return;
        }
        if (RectMode)
        {
            // the corner the first corner is measured from
            var c = PushPullMe.Core.Drawing.NearestCorner(body, dh.Face.Face, dh.Point);
            var u = RectAxis(n); var v = n.Cross(u);
            ShowSketch([c], n);
            MeasureLabel.Text = T("Vanaf hoek", "From corner");
            MeasureBox.Text = $"{Mm(Math.Abs((dh.Point - c).Dot(u)))};{Mm(Math.Abs((dh.Point - c).Dot(v)))}";
            StatusText.Text = T("▭ Klik de eerste hoek, of typ x;y + Enter: zo ver van de gemarkeerde hoek van het vlak.", "▭ Click the first corner, or type x;y + Enter: that far from the marked corner of the face.");
            return;
        }
        var start = LineCandidate(dh.Face, dh.Point, pos);
        ShowSketch([start.P], n);
        if (start.Label != null && PushPullMe.Core.Drawing.EdgeAt(body, dh.Face.Face, start.P) is { } edge && start.OnEdge)
        {
            MeasureLabel.Text = T("Vanaf hoek", "From corner");
            MeasureBox.Text = Mm((start.P - edge.Corner).Length);
            StatusText.Text = T($"╱ {start.Label} · klik om de lijn hier te beginnen, of typ een afstand + Enter: zo ver van de dichtstbijzijnde hoek langs deze rand.", $"╱ {start.Label} · click to start the line here, or type a distance + Enter: that far from the nearest corner along this edge.");
        }
        else
        {
            MeasureBox.Text = "";
            StatusText.Text = T($"╱ {(start.Label != null ? start.Label + " · " : "")}klik waar de lijn begint (op een rand, of ergens op het vlak).", $"╱ {(start.Label != null ? start.Label + " · " : "")}click where the line starts (on an edge, or anywhere on the face).");
        }
    }

    private void StartDraw(FaceRef f, Vec3 at, Point? pos)
    {
        var body = _doc!.Bodies[f.Body];
        _linePts.Clear(); _lineAxis = null;
        if (LineMode && pos is { } ps) at = LineCandidate(f, at, ps).P;
        if ((RectMode || CircleMode) && pos is { } gp && body.Faces[f.Face].Surface is BrepPlane gpl && GuideSnap(at, gp, NormalOf(f), gpl.Origin) is { } gs) at = gs.Point;
        _drawFace = f; _drawStart = at; _drawEnd = at; _drawEndOk = false; _typed = "";
        var n = NormalOf(f);
        _drawU = RectAxis(n);
        _drawEdgeDir = LineMode && PushPullMe.Core.Drawing.EdgeAt(body, f.Face, at) is { } e ? (e.Other - e.Corner).Normalized() : null;
        _selected = null; _hover = null; _last = null; _drawHover = null;
        UpdateHighlight();
        MeasureLabel.Text = RectMode ? T("Maten", "Size") : CircleMode ? T("Straal", "Radius") : T("Lengte", "Length");
        MeasureBox.Text = "";
        StatusText.Text = RectMode ? T("▭ Klik de andere hoek, of typ breedte;hoogte + Enter. Esc breekt af.", "▭ Click the other corner, or type width;height + Enter. Esc cancels.")
            : CircleMode ? T("◯ Klik de rand van de cirkel, of typ de straal + Enter. Esc breekt af.", "◯ Click the edge of the circle, or type the radius + Enter. Esc cancels.")
            : T("╱ Klik op een andere rand van het vlak, of typ een lengte + Enter. Esc breekt af.", "╱ Click another edge of the face, or type a length + Enter. Esc cancels.");
        ShowSketch([at], n);
        if (LineMode) { _linePts.Add(at); StatusText.Text = T("╱ Klik het volgende punt. Sluit op het beginpunt voor een vlak, of eindig op een rand om het vlak te delen. Typ een lengte + Enter. Esc: klaar.", "╱ Click the next point. Close on the start point for a face, or end on an edge to divide the face. Type a length + Enter. Esc: done."); }
    }

    /// <summary>The rectangle from the first corner to <paramref name="end"/>: its corners, and its width and height along
    /// its sides (signed).</summary>
    private (List<Vec3> Corners, double W, double H) Rect(Vec3 end)
    {
        var v = NormalOf(_drawFace!.Value).Cross(_drawU);
        var d = end - _drawStart;
        double w = d.Dot(_drawU), h = d.Dot(v);
        return ([_drawStart, _drawStart + _drawU * w, _drawStart + _drawU * w + v * h, _drawStart + v * h], w, h);
    }

    private static readonly Color InferColor = Color.FromRgb(0xD9, 0x46, 0xEF);

    /// <summary>The points of the line drawn so far (on <see cref="_drawFace"/>), and the axis held by an arrow key.</summary>
    private readonly List<Vec3> _linePts = new();

    /// <summary>The loose lines this line has left so far (they become edges when it closes or reaches the edge).</summary>
    private readonly List<SketchLine> _chainLines = new();
    private int? _lineAxis;
    private Color? _lineColor;

    /// <summary>
    /// The next point of a line, as SketchUp infers it from the mouse (p on the face's plane): the first point (closing
    /// the shape), a corner or middle of an edge, where guide lines cross; from the last point along an axis (red, green,
    /// blue; or the axis held by an arrow key), parallel or square to an edge of the face (magenta), on to the edge
    /// across when that is near; a point on an edge or a guide line; else the point itself. With what it is, its colour
    /// and whether it lies on the face's outer edge.
    /// </summary>
    private (Vec3 P, string? Label, Color? Color, bool OnEdge) LineCandidate(FaceRef f, Vec3 p, Point pos)
    {
        var body = _doc!.Bodies[f.Body];
        var face = body.Faces[f.Face];
        var n = NormalOf(f); var o = ((BrepPlane)face.Surface).Origin;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        bool OnEdge(Vec3 q) => OnPlaneEdge(f, q);
        var plane = PlaneFaces(f);
        // the edges to line up with: of the face the line is on, and of the face under the mouse (in the same plane)
        var lineUp = new List<int> { f.Face };
        if (FaceAt(pos) is { } under && under.Face.Body == f.Body && under.Face.Face != f.Face && plane.Contains(under.Face.Face)) lineUp.Add(under.Face.Face);
        if (_linePts.Count >= 3 && (Screen(_linePts[0]) - pos).Length < 12) return (_linePts[0], Snap.CloseShape.Name(), null, false);
        var es = EdgeSnap(f, p, pos);
        if (es is { Kind: Snap.Centre or Snap.LooseEnd or Snap.LooseMiddle } pc) return (pc.Point, pc.Kind.Name(), null, OnEdge(pc.Point));
        if (es is { Kind: Snap.Endpoint or Snap.Midpoint or Snap.GuideCrossing } pe) return (pe.Point, pe.Kind.Name(), null, true);
        if (GuideSnap(p, pos, n, o) is { Kind: Snap.GuideCrossing } gx) return (gx.Point, gx.Kind.Name(), null, OnEdge(gx.Point));
        if (_linePts.Count > 0)
        {
            var last = _linePts[^1];
            var d = p - last;
            var dirs = new List<(Vec3 Dir, string Name, Color Color)>();
            if (_lineAxis is int held) { if (Math.Abs(Axes[held].Dir.Dot(n)) < 1e-6) dirs.Add((Axes[held].Dir, T("langs de ", "along the ") + Axes[held].Name, Axes[held].Color)); }
            else
            {
                // the axes, the edges of the faces (and of the lines drawn so far), and the edge rested on last
                var edges = lineUp.SelectMany(lf => body.Faces[lf].Loops).SelectMany(l => l).Select(c => body.Edges[c.Edge]).Where(e => e.Curve is BrepLine && e.Start >= 0)
                    .Select(e => (body.Vertices[e.End] - body.Vertices[e.Start]).Normalized())
                    .Concat(_linePts.Zip(_linePts.Skip(1)).Select(s => (s.Second - s.First).Normalized()));
                dirs.AddRange(InferDirs(n, edges).Select(x => (x.Dir, x.Name, x.Color)));
            }
            if (d.Length > 1e-9)
                foreach (var (dir, name, color) in dirs)
                {
                    double along = d.Dot(dir);
                    if (_lineAxis == null && Math.Abs(along) < d.Length * Math.Cos(3 * Math.PI / 180)) continue;
                    var sdir = dir * (along < 0 ? -1 : 1);
                    var q = last + dir * (Math.Round(along * Display * 10) / 10 / Display);
                    // on to the edge across, when the mouse is near it, or when the rounded length is within a rounding step of it
                    if (PushPullMe.Core.Drawing.RayToEdges(body, plane, last, sdir) is { } across
                        && ((Screen(across) - pos).Length < 15 || Math.Abs((q - last).Length - (across - last).Length) < 0.06 / Display))
                        return (across, name + T(", tot de rand", ", to the edge"), color, true);
                    return (q, name, color, OnEdge(q));
                }
        }
        if (es is { } on) return (on.Point, on.Kind.Name(), null, on.Kind != Snap.OnLoose || OnEdge(on.Point));
        if (GuideSnap(p, pos, n, o) is { } g) return (g.Point, g.Kind.Name(), null, OnEdge(g.Point));
        return (p, null, null, OnEdge(p));
    }

    private void DrawTo(Point pos)
    {
        if (_drawFace is not { } f || _typed.Length > 0) return;
        if (LineMode && _linePts.Count == 1 && FaceAt(pos) is { } under && under.Face != f && IsPlanar(under.Face)
            && !(under.Face.Body == f.Body && PlaneFaces(f).Contains(under.Face.Face)) && OnPlaneEdge(under.Face, _linePts[0]))
        {
            // the start lies on an edge of the face under the mouse too, in another plane: draw on that one
            _drawFace = f = under.Face;
            _drawEdgeDir = PushPullMe.Core.Drawing.EdgeAt(_doc!.Bodies[f.Body], f.Face, _linePts[0]) is { } e ? (e.Other - e.Corner).Normalized() : null;
            UpdateHighlight();
        }
        if (OnPlane(f, pos) is not { } p) return;
        var n = NormalOf(f);
        if ((RectMode || CircleMode) && _doc!.Bodies[f.Body].Faces[f.Face].Surface is BrepPlane dpl && GuideSnap(p, pos, n, dpl.Origin) is { } dgs) p = dgs.Point;
        if (CircleMode)
        {
            _drawEnd = p;
            double r = (p - _drawStart).Length;
            _drawEndOk = r > 1e-7;
            var pts = Enumerable.Range(0, 60).Select(i => _drawStart + (_drawU * Math.Cos(i * Math.PI / 30) + n.Cross(_drawU) * Math.Sin(i * Math.PI / 30)) * r).ToList();
            ShowSketch(_drawEndOk ? pts : [_drawStart], n, closed: true);
            MeasureBox.Text = Mm(r);
            return;
        }
        if (RectMode)
        {
            _drawEnd = p;
            var (corners, w, h) = Rect(p);
            _drawEndOk = Math.Abs(w) > 1e-7 && Math.Abs(h) > 1e-7;
            ShowSketch(corners, n, closed: true);
            MeasureBox.Text = $"{Mm(Math.Abs(w))};{Mm(Math.Abs(h))}";
            return;
        }
        // the next point of the line, as inferred
        var c = LineCandidate(f, p, pos);
        _drawEnd = c.P; _drawEndOk = true; _lineColor = c.Color;
        var shown = _linePts.Append(c.P).ToList();
        ShowSketch(shown, n, color: c.Color);
        double len = (c.P - _linePts[^1]).Length;
        MeasureBox.Text = Mm(len);
        StatusText.Text = T($"╱ {Mm(len)} mm{(c.Label != null ? " · " + c.Label : "")} · klik voor het volgende punt, of typ een lengte + Enter. Esc: klaar.", $"╱ {Mm(len)} mm{(c.Label != null ? " · " + c.Label : "")} · click for the next point, or type a length + Enter. Esc: done.");
    }

    /// <summary>
    /// A point added to the line: back on the first point it closes into a face of its own; from the outer edge to it
    /// again it divides the face; otherwise the line goes on from there.
    /// </summary>
    private void AddLinePoint(Vec3 c)
    {
        if (_drawFace is not { } f || _doc is not { } before || _linePts.Count == 0) return;
        if ((c - _linePts[^1]).Length < 1e-9) return;
        var body = before.Bodies[f.Body];
        bool OnEdge(Vec3 q) => OnPlaneEdge(f, q);
        var plane = PlaneFaces(f);
        var pts = _linePts.Append(c).ToList();
        bool closed = pts.Count >= 4 && (c - pts[0]).Length < 1e-9;
        // (a point on the edge of a hole only touches it: the line goes on through it)
        bool across = OnEdge(pts[0]) && OnEdge(c) && !PushPullMe.Core.Drawing.OnHoleEdge(body, plane, c) && (pts.Count > 2 || !plane.Any(pf => SameEdge(body, pf, pts[0], c)));
        if (!closed && !across)
        {
            // as SketchUp: the piece stays, as a loose line (until the shape closes or reaches the edge: then a face)
            var seg = new SketchLine(_linePts[^1], c);
            // (ending on the end of an earlier line closes a ring: then the line stops, as when it closes on itself)
            bool onEnd = before.Lines.Any(l => (l.Start - c).Length < 1e-7 || (l.End - c).Length < 1e-7);
            if (CloseWithLoose(before, before.AddLines([seg]), f, c, onEnd)) return;
            Commit(before, before.AddLines([seg]));
            _chainLines.Add(seg);
            _linePts.Add(c);
            Render();
            ShowSketch([c], NormalOf(f));
            StatusText.Text = T($"╱ {_chainLines.Count} {(_chainLines.Count == 1 ? "lijn" : "lijnen")} · klik het volgende punt. Sluit op het beginpunt voor een vlak, of eindig op een rand om het vlak te delen. Esc: klaar.", $"╱ {_chainLines.Count} {(_chainLines.Count == 1 ? "line" : "lines")} · click the next point. Close on the start point for a face, or end on an edge to divide the face. Esc: done.");
            Log(string.Create(CultureInfo.InvariantCulture, $"loose line ({seg.Start.X * Display:0.####},{seg.Start.Y * Display:0.####},{seg.Start.Z * Display:0.####}) → ({c.X * Display:0.####},{c.Y * Display:0.####},{c.Z * Display:0.####})"));
            return;
        }
        try
        {
            // (the loose pieces of this line become the new edges)
            var (doc, nf) = before.RemoveLines(_chainLines).DrawPath(f, pts);
            EndDraw();
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            StatusText.Text = closed ? T("✅ Vlak getekend. Kies ⇕ (P) om het eruit te trekken of in te duwen.", "✅ Face drawn. Choose ⇕ (P) to pull it out or push it in.")
                : T("✅ Het vlak is gedeeld. Teken verder vanaf hier, of Esc (en ⇕ P om een deel te verschuiven).", "✅ The face is divided. Draw on from here, or Esc (and ⇕ P to move a part).");
            if (!closed)
            {
                // as SketchUp: the line goes on from where it ended (on the same plane; f keeps its number)
                _drawFace = f; _drawStart = c; _drawEnd = c; _drawEndOk = false; _typed = "";
                _linePts.Add(c);
                MeasureLabel.Text = T("Lengte", "Length");
                ShowSketch(_linePts, NormalOf(f));
            }
            Log($"path on face {f.Face} ({string.Join(") (", pts.Select(q => string.Create(CultureInfo.InvariantCulture, $"{q.X * Display:0.####},{q.Y * Display:0.####},{q.Z * Display:0.####}")))}): ok, new face {nf.Face}");
        }
        catch (PushPullException ex)
        {
            // (the line stays as it was: the point can be put elsewhere)
            // no face (it runs outside the face, say): the piece stays as a loose line all the same
            var seg = new SketchLine(_linePts[^1], c);
            if (CloseWithLoose(before, before.AddLines([seg]), f, c, closed)) return;
            if (closed) EndDraw();
            Commit(before, before.AddLines([seg]));
            if (!closed) { _chainLines.Add(seg); _linePts.Add(c); }
            Render();
            if (!closed) ShowSketch([c], NormalOf(f));
            StatusText.Text = T($"╱ Geen vlak geworden ({ex.Message.TrimEnd('.')}); de lijnen blijven staan.", $"╱ No face came of it ({ex.Message.TrimEnd('.')}); the lines stay.") + (closed ? "" : T(" Klik het volgende punt, of Esc: klaar.", " Click the next point, or Esc: done."));
            Log($"path on face {f.Face} ({string.Join(") (", pts.Select(q => string.Create(CultureInfo.InvariantCulture, $"{q.X * Display:0.####},{q.Y * Display:0.####},{q.Z * Display:0.####}")))}): {ex.Message}");
        }
    }

    /// <summary>
    /// A line piece just added (<paramref name="withPiece"/>) that closes a shape with loose lines drawn before (or with
    /// the face's edges through them): the shape becomes a face, as in SketchUp, and the line goes on from
    /// <paramref name="c"/> (unless <paramref name="end"/>). False when nothing closes.
    /// </summary>
    private bool CloseWithLoose(SketchDocument before, SketchDocument withPiece, FaceRef f, Vec3 c, bool end = false)
    {
        var (doc, made) = withPiece.CloseLooseLines(f);
        if (made.Count == 0) return false;
        EndDraw();
        Commit(before, doc);
        _last = null; _selected = made[^1]; _lastDrawn = made[^1];
        Render();
        if (!end)
        {
            _drawFace = f; _drawStart = c; _drawEnd = c; _drawEndOk = false; _typed = "";
            _linePts.Add(c);
            MeasureLabel.Text = T("Lengte", "Length");
            ShowSketch([c], NormalOf(f));
        }
        StatusText.Text = T($"✅ Met de lijnen die er al lagen {(made.Count == 1 ? "is een vlak" : $"zijn {made.Count} vlakken")} gesloten.", $"✅ With the lines already there {(made.Count == 1 ? "a face is" : $"{made.Count} faces are")} closed.") + (end ? "" : T(" Teken verder vanaf hier, of Esc.", " Draw on from here, or Esc."));
        Log($"loose lines closed on face {f.Face}: {made.Count} new face(s), {doc.Lines.Count} loose left");
        return true;
    }

    /// <summary>Whether two points lie on the same straight edge of the face's outer loop.</summary>
    private static bool SameEdge(BrepBody body, int face, Vec3 a, Vec3 b) =>
        body.Faces[face].Loops[0].Select(c => body.Edges[c.Edge]).Where(e => e.Curve is BrepLine && e.Start >= 0).Any(e =>
        {
            Vec3 s0 = body.Vertices[e.Start], ab = body.Vertices[e.End] - s0;
            bool On(Vec3 q) { double t = (q - s0).Dot(ab) / ab.Dot(ab); return t >= -1e-9 && t <= 1 + 1e-9 && (s0 + ab * t - q).Length < 1e-6; }
            return On(a) && On(b);
        });

    private void FinishDraw()
    {
        if (_drawFace is not { } f || _doc is not { } before) return;
        if (LineMode) { AddLinePoint(_drawEnd); return; }
        if ((RectMode || CircleMode) && !_drawEndOk) return;
        var (a, c, u, kind) = (_drawStart, _drawEnd, _drawU, Kind);
        EndDraw();
        DrawShape(before, f, a, c, u, kind);
    }

    /// <summary>
    /// A typed size while drawing. ▭ before the first click: x;y from the face's corner nearest the mouse (the first
    /// corner); after it: breedte;hoogte. ╱ before the first click: the distance from the nearest corner along the edge
    /// under the mouse (the start); after it: the length (the line must end on an edge).
    /// </summary>
    private void TypedDraw(string typed)
    {
        var parts = typed.Split(';');
        double b = 0;
        bool two = TryMm(parts[0], out double a) & (parts.Length == 2 && TryMm(parts[1], out b));
        if (CircleMode)
        {
            // the radius, after the centre is clicked
            if (_drawFace is not { } cf) { StatusText.Text = T("◯ Klik eerst het middelpunt, en typ dan de straal.", "◯ Click the centre first, then type the radius."); return; }
            if (parts.Length != 1 || a <= 0) { StatusText.Text = T("◯ Typ de straal in mm + Enter.", "◯ Type the radius in mm + Enter."); return; }
            var (cs, cu) = (_drawStart, _drawU);
            EndDraw();
            DrawShape(_doc!, cf, cs, cs + cu * (a / Display), cu, DrawKind.Circle);
            return;
        }
        if (_drawFace is not { } f)
        {
            if (_drawHover is not { } dh) return;
            var body = _doc!.Bodies[dh.Face.Body];
            var n = NormalOf(dh.Face);
            if (RectMode)
            {
                if (!two || a < 0 || b < 0) { StatusText.Text = T("▭ Typ x;y in mm, bijvoorbeeld 10;5, en Enter.", "▭ Type x;y in mm, for example 10;5, and Enter."); return; }
                var c = PushPullMe.Core.Drawing.NearestCorner(body, dh.Face.Face, dh.Point);
                var u = RectAxis(n); var v = n.Cross(u);
                // into the face, whichever way that is from the corner
                var start = new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) }
                    .Select(sg => c + u * (sg.Item1 * a / Display) + v * (sg.Item2 * b / Display))
                    .Cast<Vec3?>().FirstOrDefault(pt => PushPullMe.Core.Drawing.Contains(body, dh.Face.Face, pt!.Value));
                if (start is not { } st) { StatusText.Text = T("▭ Zo ver van de hoek ligt geen punt van dit vlak.", "▭ No point of this face lies that far from the corner."); return; }
                StartDraw(dh.Face, st, null);
                StatusText.Text = T($"▭ Eerste hoek op {typed} mm van de hoek. Klik de andere hoek, of typ breedte;hoogte + Enter.", $"▭ First corner {typed} mm from the corner. Click the other corner, or type width;height + Enter.");
                return;
            }
            if (PushPullMe.Core.Drawing.EdgeAt(body, dh.Face.Face, dh.Point) is not { } edge || parts.Length != 1 || a <= 0)
            { StatusText.Text = T("╱ Wijs een rand aan en typ een afstand in mm + Enter.", "╱ Point at an edge and type a distance in mm + Enter."); return; }
            var along = edge.Other - edge.Corner;
            if (a / Display >= along.Length) { StatusText.Text = T($"╱ Deze rand is maar {Mm(along.Length)} mm lang.", $"╱ This edge is only {Mm(along.Length)} mm long."); return; }
            StartDraw(dh.Face, edge.Corner + along.Normalized() * (a / Display), null);
            StatusText.Text = T($"╱ Lijn begint {typed} mm van de hoek. Klik op een andere rand, of typ een lengte + Enter.", $"╱ The line starts {typed} mm from the corner. Click another edge, or type a length + Enter.");
            return;
        }
        if (RectMode)
        {
            if (!two || a <= 0 || b <= 0) { StatusText.Text = T("▭ Typ breedte;hoogte in mm, bijvoorbeeld 30;20, en Enter.", "▭ Type width;height in mm, for example 30;20, and Enter."); return; }
            // in the directions the mouse is pulling the rectangle
            var (_, cw, ch) = Rect(_drawEnd);
            var c = _drawStart + _drawU * (a / Display * (cw < 0 ? -1 : 1)) + NormalOf(f).Cross(_drawU) * (b / Display * (ch < 0 ? -1 : 1));
            var (st, u) = (_drawStart, _drawU);
            EndDraw();
            DrawShape(_doc!, f, st, c, u, DrawKind.Rectangle);
            return;
        }
        if (parts.Length != 1 || a <= 0) { StatusText.Text = T("╱ Typ een lengte in mm + Enter.", "╱ Type a length in mm + Enter."); return; }
        // along the way the line is going now (the axis held, or towards the point under the mouse)
        var lastPt = _linePts[^1];
        var dirTyped = _lineAxis is int la ? Axes[la].Dir * ((_drawEnd - lastPt).Dot(Axes[la].Dir) < 0 ? -1 : 1) : (_drawEnd - lastPt);
        if (dirTyped.Length < 1e-9) { StatusText.Text = T("╱ Beweeg de muis eerst in de richting van de lijn.", "╱ Move the mouse in the direction of the line first."); return; }
        AddLinePoint(lastPt + dirTyped.Normalized() * (a / Display));
    }

    /// <summary>What is being drawn: "rect", "line" or "circle".</summary>
    private DrawKind Kind => RectMode ? DrawKind.Rectangle : CircleMode ? DrawKind.Circle : DrawKind.Line;

    /// <summary>What ▭, ╱ and ◯ draw.</summary>
    private enum DrawKind { Rectangle, Line, Circle }

    /// <summary>A rectangle (corners a and c, first side along u), a line (a to c) or a circle (centre a, through c)
    /// drawn on face f of <paramref name="before"/>.</summary>
    private void DrawShape(SketchDocument before, FaceRef f, Vec3 a, Vec3 c, Vec3 u, DrawKind kind)
    {
        static string P(Vec3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X * Display:0.####},{v.Y * Display:0.####},{v.Z * Display:0.####}");
        string where = $"{kind} on face {f.Face} ({P(a)}) ({P(c)})";
        try
        {
            var (doc, nf) = kind switch
            {
                DrawKind.Rectangle => before.DrawRectangle(f, a, c, u),
                DrawKind.Circle => before.DrawCircle(f, a, (c - a).Length),
                _ => before.DrawLine(f, a, c),
            };
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            StatusText.Text = kind switch
            {
                DrawKind.Rectangle => T("✅ Rechthoek getekend. Kies ⇕ (P) om hem eruit te trekken of in te duwen.", "✅ Rectangle drawn. Choose ⇕ (P) to pull it out or push it in."),
                DrawKind.Circle => T($"✅ Cirkel getekend (straal {Mm((c - a).Length)} mm). Kies ⇕ (P): eruit trekken geeft een staaf, tot de achterkant duwen een gat.", $"✅ Circle drawn (radius {Mm((c - a).Length)} mm). Choose ⇕ (P): pulling it out gives a rod, pushing it through to the back a hole."),
                _ => T("✅ Het vlak is in tweeën gedeeld. Kies ⇕ (P) om een deel te verschuiven.", "✅ The face is divided in two. Choose ⇕ (P) to move a part."),
            };
            Log($"{where}: ok, new face {nf.Face}");
        }
        catch (PushPullException ex)
        {
            // (sticking out over the face's edge: as loose lines in its plane, they divide the face and close the rest
            // with its edges, a face of its own beside it; as SketchUp)
            if (kind != DrawKind.Line && OverTheEdge(before, f, a, c, u, kind) is { } over)
            {
                Commit(before, over.Doc);
                _last = null; _selected = over.Face; _lastDrawn = over.Face;
                Render();
                StatusText.Text = T("✅ Getekend; het steekt over de rand: het vlak is gedeeld en wat erbuiten ligt is een eigen vlak.", "✅ Drawn; it sticks out over the edge: the face is divided and what lies outside is a face of its own.");
                Log($"{where}: over the edge, {over.Doc.Bodies.Count} bodies");
                return;
            }
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log($"{where}: {ex.Message}");
        }
    }

    /// <summary>A rectangle or circle on face f that sticks out over its edge, drawn as loose lines in the face's plane
    /// (dividing the faces there, the rest a face of its own); null when that closes nothing.</summary>
    private static (SketchDocument Doc, FaceRef Face)? OverTheEdge(SketchDocument before, FaceRef f, Vec3 a, Vec3 c, Vec3 u, DrawKind kind)
    {
        var face = before.Bodies[f.Body].Faces[f.Face];
        if (face.Surface is not BrepPlane pl) return null;
        var n = FaceGeometry.Normal(face);
        List<SketchLine> lines;
        if (kind == DrawKind.Circle)
        {
            double r = (c - a).Length;
            if (r < 1e-9) return null;
            var start = a + (c - a).Normalized() * r;
            lines = [new SketchLine(start, start, a, n)];
        }
        else
        {
            var v = n.Cross(u); var d = c - a;
            double du = d.Dot(u), dv = d.Dot(v);
            if (Math.Abs(du) < 1e-9 || Math.Abs(dv) < 1e-9) return null;
            Vec3 p1 = a + u * du, p3 = a + v * dv;
            lines = [new SketchLine(a, p1), new SketchLine(p1, c), new SketchLine(c, p3), new SketchLine(p3, a)];
        }
        try
        {
            var (doc, made) = before.AddLines(lines).CloseLooseLines(f);
            var (doc2, made2) = doc.CloseLooseLinesOnPlane(n, pl.Origin);
            made.AddRange(made2);
            // (lines left over: it did not close as meant)
            if (made.Count == 0 || doc2.Lines.Count > before.Lines.Count) return null;
            return (doc2, made[^1]);
        }
        catch (PushPullException) { return null; }
    }

    private void EndDraw()
    {
        _drawFace = null; _drawEndOk = false; _drawEdgeDir = null;
        _linePts.Clear(); _lineAxis = null; _chainLines.Clear();
        ShowSketch([]);
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
    }

    /// <summary>
    /// A point moved a little toward the camera (a fraction of its distance), along the ray through it: it stays exactly
    /// where it was on the screen, but in front of the face it lies on. (Lifting along the face's normal shifted it
    /// sideways when seen at an angle: millimetres on a large model.)
    /// </summary>
    private Point3D TowardEye(Point3D p, double fraction)
    {
        return p + (View.Camera.Position - p) * fraction;
    }

    /// <summary>The last sketch shown (to show it again when the camera moves).</summary>
    private (IReadOnlyList<Vec3> Pts, bool Closed, Color? Color)? _sketchShown;

    /// <summary>What is being drawn, as lines (a single point as a small cross), moved a little toward the camera so the
    /// face (and its highlight) does not hide them. (n: the face's normal; no longer used.)</summary>
    private void ShowSketch(IReadOnlyList<Vec3> pts, Vec3 n = default, bool closed = false, Color? color = null)
    {
        if (_sketch != null) { View.Children.Remove(_sketch); _sketch = null; }
        _sketchShown = pts.Count == 0 ? null : (pts.ToList(), closed, color);
        if (pts.Count == 0) return;
        Point3D Q(Vec3 v) => TowardEye(P(v), 2e-3);
        var lines = new Point3DCollection();
        if (pts.Count == 1)
        {
            double s = _modelSize * 0.01;
            var c = Q(pts[0]);
            foreach (var d in new[] { new Vector3D(s, 0, 0), new Vector3D(0, s, 0), new Vector3D(0, 0, s) }) { lines.Add(c - d); lines.Add(c + d); }
        }
        else
            for (int i = 0; i < (closed ? pts.Count : pts.Count - 1); i++) { lines.Add(Q(pts[i])); lines.Add(Q(pts[(i + 1) % pts.Count])); }
        _sketch = new LineSet { Points = lines, Color = color ?? SelectColor, Thickness = color == null ? 2.5 : 4 };
        View.Children.Add(_sketch);
    }

    /// <summary>The face the last rectangle or line made (for the test aid).</summary>
    private FaceRef? _lastDrawn;

    // ------------------------------------------------------------------ move (as in SketchUp)

    private static readonly Color AxisX = Color.FromRgb(0xE1, 0x1D, 0x48), AxisY = Color.FromRgb(0x16, 0xA3, 0x4A), AxisZ = Color.FromRgb(0x25, 0x63, 0xEB);
    /// <summary>The drawing axes (as SketchUp's; set with ✛): red, green and blue, square to each other, through an origin.</summary>
    private static Vec3 _axisRed = new(1, 0, 0), _axisGreen = new(0, 1, 0), _axisBlue = new(0, 0, 1), _axisOrigin;

    /// <summary>The axes with their names (in the language chosen now) and colours.</summary>
    private static (Vec3 Dir, string Name, Color Color)[] Axes =>
    [
        (_axisRed, T("rode as", "red axis"), AxisX), (_axisGreen, T("groene as", "green axis"), AxisY), (_axisBlue, T("blauwe as", "blue axis"), AxisZ),
    ];

    // a move in progress: what is picked up (body, selection, a word for it), where it was grabbed, the document it
    // started from, the axis it is held to (by an arrow key), the offset now and whether that works
    private (int Body, MoveSelection Pick, string What)? _movePick;

    /// <summary>The group moved as a whole (picked in a closed one), or null.</summary>
    private int? _moveGroup;
    private Vec3 _moveGrab;
    private SketchDocument? _moveBase;
    private int? _moveAxis;
    private Vec3 _moveOffset;
    private bool _moveValid;

    /// <summary>The modifier keys held (or, in the test aid, the ones it plays).</summary>
    private ModifierKeys Mods => _simMods ?? _mods;
    private ModifierKeys? _simMods;

    /// <summary>The last move: typing a distance right after it changes it (as with Push/Pull).</summary>
    private (SketchDocument Before, int Body, MoveSelection Pick, Vec3 Dir, int? Group)? _lastMove;

    /// <summary>What the mouse points at to move: a corner of the face under it (within 10 pixels), else an edge of it
    /// (within 8), else the face; with the point it is grabbed at.</summary>
    private (int Body, MoveSelection Pick, string What, Vec3 Grab)? MovePickAt((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return null;
        // (in a closed group: the whole group, Ctrl a copy of it)
        if (GroupTaken(h.Face.Body) is { } mg)
            return (h.Face.Body, (Mods & ModifierKeys.Control) != 0 ? MoveSelection.Copy() : MoveSelection.WholeBody(),
                (Mods & ModifierKeys.Control) != 0 ? T($"geheel ({_doc.GroupName(mg)}, als kopie)", $"whole ({_doc.GroupName(mg)}, as a copy)") : T($"geheel ({_doc.GroupName(mg)})", $"whole ({_doc.GroupName(mg)})"), V(h.Point));
        // Ctrl: a copy of the whole body (as SketchUp's Move with Ctrl); Shift: the whole body
        if ((Mods & ModifierKeys.Control) != 0) return (h.Face.Body, MoveSelection.Copy(), T("lichaam (als kopie)", "body (as a copy)"), V(h.Point));
        if ((Mods & ModifierKeys.Shift) != 0) return (h.Face.Body, MoveSelection.WholeBody(), T("lichaam", "body"), V(h.Point));
        // (a face of the choice, with more chosen: all of them together)
        if (_multi is { } mm && mm.Body == h.Face.Body && mm.Faces.Count > 1 && mm.Faces.Contains(h.Face.Face))
            return (mm.Body, new MoveSelection(mm.Faces.ToList(), [], []), T($"geheel van {mm.Faces.Count} vlakken", $"group of {mm.Faces.Count} faces"), V(h.Point));
        var body = _doc.Bodies[h.Face.Body];
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var corners = body.Faces[h.Face.Face].Loops.SelectMany(l => l).Select(c => body.Edges[c.Edge]).Where(e => e.Start >= 0)
            .SelectMany(e => new[] { e.Start, e.End }).Distinct()
            .Select(v => (V: v, Px: (Screen(body.Vertices[v]) - pos).Length)).Where(x => x.Px < 10).OrderBy(x => x.Px).ToList();
        if (corners.Count > 0) return (h.Face.Body, MoveSelection.Vertex(corners[0].V), T("hoekpunt", "vertex"), body.Vertices[corners[0].V]);
        if (EdgeAt(hit, pos) is { } e && ScreenDistanceToEdge(body, e.Edge, pos) < 8)
        {
            var ed = body.Edges[e.Edge];
            var pts = FaceGeometry.EdgePoints(body, ed);
            var grab = pts.MinBy(p => (Screen(p) - pos).Length);
            return (e.Body, MoveSelection.Edge(e.Edge), T("rand", "edge"), grab);
        }
        return (h.Face.Body, MoveSelection.Face(h.Face.Face), T("vlak", "face"), V(h.Point));
    }

    private double ScreenDistanceToEdge(BrepBody body, int edge, Point pos)
    {
        var pts = FaceGeometry.EdgePoints(body, body.Edges[edge]).Select(p => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(p))).ToList();
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var ab = pts[i + 1] - pts[i]; double l2 = ab.LengthSquared;
            double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - pts[i], ab) / l2, 0, 1);
            best = Math.Min(best, (pts[i] + ab * t - pos).Length);
        }
        return best;
    }

    /// <summary>What is picked up shown: a corner as a small cross, an edge as a line, a face lit up.</summary>
    private void ShowPick(int body, MoveSelection pick, Vec3 grab)
    {
        var b = (_shown ?? _doc)!.Bodies[body];
        if (pick.Vertices.Count > 0) ShowSketch([grab]);
        else if (pick.Edges.Count > 0) ShowSketch(FaceGeometry.EdgePoints(b, b.Edges[pick.Edges.First()]), default, color: SelectColor);
        else ShowSketch([]);
    }

    private void HoverMove((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (MovePickAt(hit, pos) is not { } p) { ShowSketch([]); StatusText.Text = T("✥ Wijs een hoekpunt, rand of vlak aan om te verplaatsen.", "✥ Point at a vertex, edge or face to move."); return; }
        if (p.Pick.Faces.Count == 0 && _hover != null) { _hover = null; UpdateHighlight(); }
        ShowPick(p.Body, p.Pick, p.Grab);
        StatusText.Text = T($"✥ Klik om dit {p.What} op te pakken.{(p.Pick.Whole == MoveWhole.None ? " Shift: het hele lichaam · Ctrl: een kopie." : "")}", $"✥ Click to pick up this {p.What}.{(p.Pick.Whole == MoveWhole.None ? " Shift: the whole body · Ctrl: a copy." : "")}");
    }

    private void StartMove((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (MovePickAt(hit, pos) is not { } p || _doc == null) return;
        _movePick = (p.Body, p.Pick, p.What); _moveGrab = p.Grab; _moveBase = _doc; _moveAxis = null; _moveGroup = GroupTaken(p.Body);
        _moveOffset = default; _moveValid = false; _typed = ""; _last = null; _lastMove = null; _selected = null;
        if (p.Pick.Faces.Count > 0) _hover = new FaceRef(p.Body, p.Pick.Faces.First());
        UpdateHighlight();
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm"; MeasureBox.Text = "0";
        StatusText.Text = T($"✥ Beweeg om het {p.What} te verplaatsen; klik om neer te zetten, of typ de afstand + Enter. Pijltjes: rood, groen, blauw. Esc breekt af.", $"✥ Move to shift the {p.What}; click to put it down, or type the distance + Enter. Arrow keys: red, green, blue. Esc cancels.");
    }

    private void EndMove()
    {
        _movePick = null; _moveBase = null; _moveAxis = null; _moveValid = false; _moveGroup = null;
        ShowSketch([]);
    }

    /// <summary>
    /// The offset for the mouse at <paramref name="pos"/>: onto a corner close by (within 12 pixels; "naar dit punt"),
    /// else along the axis held by an arrow key, or the axis the mouse is close to (within 15 pixels), else free in the
    /// plane across the view through the grab point; with what it snapped to.
    /// </summary>
    private (Vec3 Offset, string? Snap, InferDir? Along)? MoveOffsetAt(Point pos)
    {
        if (_moveBase == null || _movePick is not { } mp) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return null;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        // a corner close by (not one that is moved along)
        if (_moveAxis == null)
        {
            var body = _moveBase.Bodies[mp.Body];
            // (a group moved: none of the corners of its bodies)
            var groupMoving = _moveGroup is { } mvg && mp.Pick.Whole == MoveWhole.Body ? _moveBase.Members(mvg).ToHashSet() : [];
            // (the whole body: none of its corners; a copy: the original stays, its corners are fine)
            var moving = mp.Pick.Whole == MoveWhole.Body ? Enumerable.Range(0, body.Vertices.Count).ToHashSet() : mp.Pick.Vertices.Concat(mp.Pick.Edges.Select(e => body.Edges[e]).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End }))
                .Concat(mp.Pick.Faces.SelectMany(f => body.Faces[f].Loops.SelectMany(l => l)).Select(c => body.Edges[c.Edge]).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End })).ToHashSet();
            (Vec3 V, double Px)? near = null;
            for (int bi = 0; bi < _moveBase.Bodies.Count; bi++)
                for (int vi = 0; vi < _moveBase.Bodies[bi].Vertices.Count; vi++)
                {
                    if ((bi == mp.Body && moving.Contains(vi)) || groupMoving.Contains(bi)) continue;
                    var v = _moveBase.Bodies[bi].Vertices[vi];
                    double px = (Screen(v) - pos).Length;
                    if (px < 12 && (near == null || px < near.Value.Px)) near = (v, px);
                }
            if (near is { } nv) return (nv.V - _moveGrab, T("naar dit punt", "to this point"), null);
            if (GuideCrossingNear(pos) is { } gc) return (gc - _moveGrab, T("naar het snijpunt met de hulplijn", "to the intersection with the guide line"), null);
        }
        // the point on the line through the grab point along a direction nearest to the mouse ray
        Vec3? OnLine(Vec3 d)
        {
            double b = d.Dot(dir);
            if (1 - b * b < 1e-9) return null;
            var w0 = _moveGrab - org;
            double t = (b * w0.Dot(dir) - w0.Dot(d)) / (1 - b * b);
            return _moveGrab + d * t;
        }
        Vec3 Round(Vec3 v, Vec3 d) { double l = Math.Round(v.Dot(d) * Display * 10) / 10 / Display; return d * l; }
        if (_moveAxis is int held && OnLine(Axes[held].Dir) is { } ph)
            return (Round(ph - _moveGrab, Axes[held].Dir), null, new InferDir(Axes[held].Dir, T("langs de ", "along the ") + Axes[held].Name, Axes[held].Color));
        // along an axis, or parallel to the edge rested on last
        if (InferFrom(_moveGrab, pos, null, InferDirs(null, [])) is { } inf) return (inf.P - _moveGrab, null, inf.Dir);
        // free: in the plane across the view
        double den = dir.Dot(dir);
        var look = View.Camera is ProjectionCamera cam ? new Vec3(cam.LookDirection.X, cam.LookDirection.Y, cam.LookDirection.Z).Normalized() : dir;
        double dn = dir.Dot(look);
        if (Math.Abs(dn) < 1e-9) return null;
        var hitP = org + dir * ((_moveGrab - org).Dot(look) / dn);
        var free = hitP - _moveGrab;
        free = new Vec3(Math.Round(free.X * Display * 10) / 10 / Display, Math.Round(free.Y * Display * 10) / 10 / Display, Math.Round(free.Z * Display * 10) / 10 / Display);
        return (free, null, null);
    }

    private void MoveTo(Point pos)
    {
        if (_movePick == null || _typed.Length > 0) return;
        if (MoveOffsetAt(pos) is not { } m) return;
        if ((m.Offset - _moveOffset).Length < 1e-12 && _moveValid) return;
        PreviewMove(m.Offset, m.Snap, m.Along);
    }

    private void PreviewMove(Vec3 offset, string? snap, InferDir? along)
    {
        if (_movePick is not { } mp || _moveBase == null) return;
        _moveOffset = offset;
        MeasureBox.Text = Mm(offset.Length);
        // the direction it runs along, as a coloured guide line
        if (along is { } a)
        {
            var d = a.Dir * Math.Max(offset.Length, 0.005);
            ShowSketch([_moveGrab - d, _moveGrab + offset + d], default, color: a.Color);
        }
        else ShowSketch([_moveGrab, _moveGrab + offset]);
        if (offset.Length < 1e-9) { _moveValid = false; Render(_moveBase); return; }
        try
        {
            var doc = MoveDoc(_moveBase, mp.Body, mp.Pick, offset, _moveGroup, checkCrossing: false);
            _moveValid = true;
            Render(doc);
            StatusText.Text = T($"✥ {Mm(offset.Length)} mm{(along is { } ax ? " " + ax.Name : "")}{(snap != null ? " · 🎯 " + snap : "")} · klik om neer te zetten, of typ de afstand + Enter. Esc breekt af.", $"✥ {Mm(offset.Length)} mm{(along is { } ax2 ? " " + ax2.Name : "")}{(snap != null ? " · 🎯 " + snap : "")} · click to put it down, or type the distance + Enter. Esc cancels.");
        }
        catch (PushPullException ex) { _moveValid = false; StatusText.Text = "⛔ " + ex.Message; }
    }

    private void FinishMove()
    {
        if (_movePick is not { } mp || _moveBase is not { } before) return;
        var offset = _moveOffset; int? group = _moveGroup;
        EndMove();
        if (offset.Length < 1e-9) { Render(); return; }
        ApplyMove(before, mp.Body, mp.Pick, offset, replaceLast: false, group);
    }

    /// <summary>A move (see <see cref="SketchDocument.Move(int, MoveSelection, Vec3, bool)"/>), of a whole group when
    /// <paramref name="group"/> is set.</summary>
    private static SketchDocument MoveDoc(SketchDocument d, int body, MoveSelection pick, Vec3 offset, int? group, bool checkCrossing) =>
        group is { } g ? d.TransformGroup(g, (x, b, c) => x.MoveBody(b, offset, c), pick.Whole == MoveWhole.Copy, Placement.Move(offset)).Doc
            : d.Move(body, pick, offset, checkCrossing).Doc;

    private void ApplyMove(SketchDocument before, int body, MoveSelection pick, Vec3 offset, bool replaceLast, int? group = null)
    {
        string what = pick.Whole == MoveWhole.Copy ? "copy" : pick.Whole == MoveWhole.Body ? "all"
            : pick.Faces.Count > 1 ? $"faces {string.Join(",", pick.Faces)}"
            : pick.Vertices.Count > 0 ? $"vertex {pick.Vertices.First()}" : pick.Edges.Count > 0 ? $"edge {pick.Edges.First()}" : $"face {pick.Faces.First()}";
        string off = string.Create(CultureInfo.InvariantCulture, $"{offset.X * Display:0.###},{offset.Y * Display:0.###},{offset.Z * Display:0.###}");
        try
        {
            var doc = MoveDoc(before, body, pick, offset, group, checkCrossing: true);
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _lastMove = (before, body, pick, offset.Normalized(), group);
            _last = null; _selected = null;
            Render();
            MeasureBox.Text = Mm(offset.Length);
            StatusText.Text = T($"✅ {(pick.Whole == MoveWhole.Copy ? "Gekopieerd" : "Verplaatst")}: {Mm(offset.Length)} mm. Typ een andere afstand + Enter om het aan te passen.", $"✅ {(pick.Whole == MoveWhole.Copy ? "Copied" : "Moved")}: {Mm(offset.Length)} mm. Type another distance + Enter to change it.");
            Log($"move {what} of body {body} by ({off}) mm: ok, {doc.Bodies[body].Faces.Count} faces");
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log($"move {what} of body {body} by ({off}) mm: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ the tape measure and guide lines

    private static readonly Color GuideColor = Color.FromRgb(0x94, 0xA3, 0xB8);
    private readonly List<Guide> _guides = new();
    private LineSet? _guideVisual;

    // a tape measure in progress: where it starts, and (from an edge) the edge's line and the face it was picked on
    private Vec3? _tapeStart;
    private (Vec3 A, Vec3 Dir, FaceRef Face)? _tapeEdge;
    private Vec3 _tapeOffset;

    /// <summary>The guide lines as dashed lines, long enough to cross the whole model.</summary>
    private void RenderGuides()
    {
        if (_guideVisual != null) { View.Children.Remove(_guideVisual); _guideVisual = null; }
        RefreshStructureCounts();
        if (_guides.Count == 0 || !SketchVisible(SketchKind.Guides)) return;
        var pts = new Point3DCollection();
        double len = Math.Max(_modelSize, 10) * 1.5 / Display, dash = len / 60;
        foreach (var g in _guides)
            for (double t = -len; t < len; t += 2 * dash) { pts.Add(P(g.Point + g.Dir * t)); pts.Add(P(g.Point + g.Dir * (t + dash))); }
        _guideVisual = new LineSet { Points = pts, Color = GuideColor, Thickness = 1.2 };
        View.Children.Add(_guideVisual);
    }

    private void ClearGuides()
    {
        _guides.Clear();
        RenderGuides();
    }

    /// <summary>
    /// A point to snap to on the plane through <paramref name="o"/> with normal <paramref name="n"/>, near the mouse:
    /// where guide lines meet each other or an edge of a face in that plane (within 12 pixels), else the nearest point on
    /// a guide line in that plane (within 8 pixels); with what it is.
    /// </summary>
    private (Vec3 Point, Snap Kind)? GuideSnap(Vec3 p, Point pos, Vec3 n, Vec3 o)
    {
        if (_doc == null) return null;
        if (CentreSnap(n, o, pos) is { } cs) return (cs, Snap.Centre);
        if (_guides.Count == 0) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var inPlane = _guides.Where(g => Guides.InPlane(g, o, n)).ToList();
        var crossings = new List<Vec3>();
        for (int i = 0; i < inPlane.Count; i++)
            for (int j = i + 1; j < inPlane.Count; j++)
                if (Guides.Meet(inPlane[i].Point, inPlane[i].Dir, inPlane[j].Point, inPlane[j].Dir) is { } m) crossings.Add(m);
        foreach (var b in _doc.Bodies)
            foreach (var e in b.Edges.Where(e => e.Curve is BrepLine && e.Start >= 0))
            {
                Vec3 a = b.Vertices[e.Start], c = b.Vertices[e.End];
                if (Math.Abs((a - o).Dot(n)) > Guides.Tol || Math.Abs((c - o).Dot(n)) > Guides.Tol) continue;
                foreach (var g in inPlane) if (Guides.MeetSegment(g, a, c) is { } m) crossings.Add(m);
            }
        var best = crossings.Select(q => (Q: q, Px: (Screen(q) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
        if (best.Count > 0) return (best[0].Q, Snap.GuideCrossing);
        var on = inPlane.Select(g => Guides.Closest(g, p)).Select(q => (Q: q, Px: (Screen(q) - pos).Length)).Where(x => x.Px < 8).OrderBy(x => x.Px).ToList();
        return on.Count > 0 ? (on[0].Q, Snap.OnGuide) : null;
    }

    // ------------------------------------------------------------------ loose lines

    private LineSet? _looseVisual, _looseConstruction;

    /// <summary>The loose lines, as edges (construction lines dashed, in the guide colour), just in front of the faces as
    /// seen from the camera (they mostly lie on one).</summary>
    private void RenderLoose(SketchDocument? doc)
    {
        if (_looseVisual != null) { View.Children.Remove(_looseVisual); _looseVisual = null; }
        if (_looseConstruction != null) { View.Children.Remove(_looseConstruction); _looseConstruction = null; }
        if (doc == null || doc.Lines.Count == 0 || !SketchVisible(SketchKind.Loose)) return;
        var solid = new Point3DCollection(); var dashed = new Point3DCollection();
        foreach (var l in doc.Lines)
        {
            var pts = l.Points();
            if (!l.Construction)
                for (int i = 0; i + 1 < pts.Count; i++) { solid.Add(TowardEye(P(pts[i]), 2e-3)); solid.Add(TowardEye(P(pts[i + 1]), 2e-3)); }
            else
            {
                // dashes about 1/60 of the model across
                double dash = Math.Max(_modelSize, 10) / 60 / Display;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    var (a, b) = (pts[i], pts[i + 1]); double len = (b - a).Length;
                    for (double t = 0; t < len; t += 2 * dash) { dashed.Add(TowardEye(P(a + (b - a) * (t / len)), 2e-3)); dashed.Add(TowardEye(P(a + (b - a) * (Math.Min(t + dash, len) / len)), 2e-3)); }
                }
            }
        }
        if (solid.Count > 0) View.Children.Add(_looseVisual = new LineSet { Points = solid, Color = EdgeColor, Thickness = 1.6 });
        if (dashed.Count > 0) View.Children.Add(_looseConstruction = new LineSet { Points = dashed, Color = GuideColor, Thickness = 1.4 });
    }

    /// <summary>The loose line under the mouse (within 6 pixels on screen): its index.</summary>
    private int? LooseAt(Point pos)
    {
        if (_doc == null) return null;
        var mat = Viewport3DHelper.GetTotalTransform(View.Viewport);
        Point Screen(Vec3 v) { var q = mat.Transform(P(v)); return new Point(q.X, q.Y); }
        (int I, double D)? best = null;
        for (int i = 0; i < _doc.Lines.Count; i++)
        {
            var s = _doc.Lines[i].Points().Select(Screen).ToList();
            for (int k = 0; k + 1 < s.Count; k++)
            {
                Point a = s[k], b = s[k + 1]; var ab = b - a; double l2 = ab.LengthSquared;
                double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - a, ab) / l2, 0, 1);
                double d = (a + ab * t - pos).Length;
                if (d < 6 && (best == null || d < best.Value.D)) best = (i, d);
            }
        }
        return best?.I;
    }

    /// <summary>A point of a loose line in the plane through o with normal n near the mouse: its end or middle (within 12
    /// pixels), else the nearest point on it (within 20 pixels); with what it is.</summary>
    private (Vec3 Point, Snap Kind)? LooseSnap(Vec3 n, Vec3 o, Vec3 at, Point pos)
    {
        if (_doc == null || _doc.Lines.Count == 0) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        bool InPlane(Vec3 q) => Math.Abs((q - o).Dot(n)) < 1e-7;
        var inPlane = _doc.Lines.Where(l => InPlane(l.Start) && InPlane(l.End)).ToList();
        var key = inPlane.SelectMany(l => new[] { (P: l.Start, K: Snap.LooseEnd), (P: l.End, K: Snap.LooseEnd), (P: l.Middle, K: Snap.LooseMiddle) })
            .Select(x => (x.P, x.K, Px: (Screen(x.P) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
        if (key.Count > 0) return (key[0].P, key[0].K);
        (Vec3 P, double Px)? best = null;
        foreach (var l in inPlane)
        {
            var pts = l.Points();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var (a, b) = (pts[i], pts[i + 1]); var ab = b - a;
                double t = Math.Clamp((at - a).Dot(ab) / Math.Max(ab.Dot(ab), 1e-30), 0, 1);
                var q = a + ab * t; double px = (Screen(q) - pos).Length;
                if (px < 20 && (best == null || px < best.Value.Px)) best = (q, px);
            }
        }
        return best is { } bp ? (bp.P, Snap.OnLoose) : null;
    }

    // ------------------------------------------------------------------ snap marks (as DesignSpark)

    /// <summary>The centre of a circle or arc of the model in the plane through o with normal n, within 12 pixels of the
    /// mouse.</summary>
    private Vec3? CentreSnap(Vec3 n, Vec3 o, Point pos)
    {
        if (_doc == null) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        (Vec3 C, double Px)? best = null;
        foreach (var l in _doc.Lines)
            if (l.Centre is { } lc && l.Axis is { } la && Math.Abs(la.Normalized().Dot(n)) > 0.9999 && Math.Abs((lc - o).Dot(n)) < 1e-7)
            {
                double px = (Screen(lc) - pos).Length;
                if (px < 12 && (best == null || px < best.Value.Px)) best = (lc, px);
            }
        foreach (var b in _doc.Bodies)
            foreach (var e in b.Edges)
                if (e.Curve is BrepCircle c && Math.Abs(c.Normal.Normalized().Dot(n)) > 0.9999 && Math.Abs((c.Centre - o).Dot(n)) < 1e-7)
                {
                    double px = (Screen(c.Centre) - pos).Length;
                    if (px < 12 && (best == null || px < best.Value.Px)) best = (c.Centre, px);
                }
        return best?.C;
    }

    private LineSet? _marks;
    private static readonly Color MarkColor = Color.FromRgb(0x1E, 0x29, 0x3B);

    /// <summary>
    /// The edge under the mouse (of the face under it, or of the faces in the plane being drawn on) gets its snap points
    /// marked: dots on its ends, a triangle on the middle of a straight edge, a cross on the centre of a circle or arc
    /// (also shown when the mouse is near that centre).
    /// </summary>
    private void UpdateMarks(Point pos)
    {
        var marks = new List<(Vec3 P, Mark Kind)>();
        if (_doc != null && _movePick == null && _dragFace == null && _lasso == null)
        {
            var faces = new List<FaceRef>();
            if ((_drawFace ?? _arcFace ?? _swFace ?? _polyFace ?? _rrFace ?? _offsetFace) is { } af && IsPlanar(af))
                faces.AddRange(PlaneFaces(af).Select(g => new FaceRef(af.Body, g)));
            if (FaceAt(pos) is { } hit) faces.Add(hit.Face);
            // (a face of what is shown just before the document changed may no longer exist)
            faces.RemoveAll(f => f.Body >= _doc.Bodies.Count || f.Face >= _doc.Bodies[f.Body].Faces.Count);
            var mat = Viewport3DHelper.GetTotalTransform(View.Viewport);
            Point Screen(Vec3 v) { var q = mat.Transform(P(v)); return new Point(q.X, q.Y); }
            double Dist(IReadOnlyList<Vec3> along, bool closed)
            {
                double d = double.MaxValue;
                var s = along.Select(Screen).ToList();
                for (int i = 0; i < (closed ? s.Count : s.Count - 1); i++)
                {
                    Point a = s[i], b = s[(i + 1) % s.Count]; var ab = b - a; double l2 = ab.LengthSquared;
                    double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - a, ab) / l2, 0, 1);
                    d = Math.Min(d, (a + ab * t - pos).Length);
                }
                return d;
            }
            (double D, List<(Vec3, Mark)> Marks)? best = null;
            foreach (var (body, e) in faces.Distinct().SelectMany(f => _doc.Bodies[f.Body].Faces[f.Face].Loops.SelectMany(l => l)
                         .Select(c => (Body: _doc.Bodies[f.Body], E: _doc.Bodies[f.Body].Edges[c.Edge]))).Distinct())
            {
                List<(Vec3, Mark)> m; double d;
                if (e.Curve is BrepLine && e.Start >= 0)
                {
                    Vec3 a = body.Vertices[e.Start], b = body.Vertices[e.End];
                    d = Dist([a, b], false);
                    m = [(a, Mark.Dot), (b, Mark.Dot), ((a + b) * 0.5, Mark.Triangle)];
                }
                else if (e.Curve is BrepCircle c)
                {
                    bool ring = e.Start < 0 || e.Start == e.End;
                    var a = e.Start >= 0 ? body.Vertices[e.Start] : c.Centre + c.XAxis.Normalized() * c.Radius;
                    var b = e.Start >= 0 ? body.Vertices[e.End] : a;
                    d = Math.Min(Dist(PushPullMe.Core.Drawing.ArcPoints(c, a, b, 32), ring), (Screen(c.Centre) - pos).Length < 12 ? 0 : double.MaxValue);
                    m = ring ? [(c.Centre, Mark.Cross)] : [(a, Mark.Dot), (b, Mark.Dot), (c.Centre, Mark.Cross)];
                }
                else continue;
                if (d < 8 && (best == null || d < best.Value.D)) best = (d, m);
            }
            foreach (var l in _doc.Lines)
            {
                double d = Dist(l.Points(), false);
                if (l.Centre is { } lc && (Screen(lc) - pos).Length < 12) d = 0;
                List<(Vec3, Mark)> m = l.Centre is { } c ? [(l.Start, Mark.Dot), (l.End, Mark.Dot), (c, Mark.Cross)] : [(l.Start, Mark.Dot), (l.End, Mark.Dot), (l.Middle, Mark.Triangle)];
                if (d < 8 && (best == null || d < best.Value.D)) best = (d, m);
            }
            if (best is { } bm) marks.AddRange(bm.Marks);
        }
        ShowMarks(marks);
    }

    /// <summary>A snap mark: a dot on an end, a triangle on a middle, a cross on a centre.</summary>
    private enum Mark { Dot, Triangle, Cross }

    private void ShowMarks(List<(Vec3 P, Mark Kind)> marks)
    {
        if (_marks != null) { View.Children.Remove(_marks); _marks = null; }
        if (marks.Count == 0 || View.Camera is not ProjectionCamera cam) return;
        var look = cam.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, cam.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, look); up.Normalize();
        var mat = Viewport3DHelper.GetTotalTransform(View.Viewport);
        var lines = new Point3DCollection();
        foreach (var (p, kind) in marks)
        {
            var q = TowardEye(P(p), 0.05); // (well toward the eye along the ray: the same spot on screen, not half under the face)
            var s0 = mat.Transform(q); var s1 = mat.Transform(q + right);
            double px = Math.Sqrt((s1.X - s0.X) * (s1.X - s0.X) + (s1.Y - s0.Y) * (s1.Y - s0.Y));
            if (px < 1e-9) continue;
            double u = 1 / px; // model units per pixel here
            void L(double x0, double y0, double x1, double y1) { lines.Add(q + right * (x0 * u) + up * (y0 * u)); lines.Add(q + right * (x1 * u) + up * (y1 * u)); }
            switch (kind)
            {
                case Mark.Dot: // a small filled square
                    for (double y = -2; y <= 2; y += 1) L(-2, y, 2, y);
                    break;
                case Mark.Triangle:
                    L(-4, -3, 4, -3); L(4, -3, 0, 4); L(0, 4, -4, -3);
                    break;
                default: // Mark.Cross
                    L(-6, 0, 6, 0); L(0, -6, 0, 6);
                    break;
            }
        }
        _marks = new LineSet { Points = lines, Color = MarkColor, Thickness = 1.5 };
        View.Children.Add(_marks);
    }

    /// <summary>Where guide lines meet each other or a straight edge, near the mouse on screen (within 12 pixels).</summary>
    private Vec3? GuideCrossingNear(Point pos)
    {
        if (_guides.Count == 0 || _doc == null) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var pts = new List<Vec3>();
        for (int i = 0; i < _guides.Count; i++)
            for (int j = i + 1; j < _guides.Count; j++)
                if (Guides.Meet(_guides[i].Point, _guides[i].Dir, _guides[j].Point, _guides[j].Dir) is { } m) pts.Add(m);
        foreach (var b in _doc.Bodies)
            foreach (var e in b.Edges.Where(e => e.Curve is BrepLine && e.Start >= 0))
                foreach (var g in _guides)
                    if (Guides.MeetSegment(g, b.Vertices[e.Start], b.Vertices[e.End]) is { } m) pts.Add(m);
        var best = pts.Select(q => (Q: q, Px: (Screen(q) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
        return best.Count > 0 ? best[0].Q : null;
    }

    /// <summary>A guide line near the mouse on screen (within 6 pixels): its index.</summary>
    private int? GuideAt(Point pos)
    {
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        double len = Math.Max(_modelSize, 10) * 1.5 / Display;
        (int, double)? best = null;
        for (int i = 0; i < _guides.Count; i++)
        {
            Point a = Screen(_guides[i].Point - _guides[i].Dir * len), b = Screen(_guides[i].Point + _guides[i].Dir * len);
            var ab = b - a; double l2 = ab.LengthSquared;
            double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - a, ab) / l2, 0, 1);
            double d = (a + ab * t - pos).Length;
            if (d < 6 && (best == null || d < best.Value.Item2)) best = (i, d);
        }
        return best?.Item1;
    }

    /// <summary>Where the tape ends: a corner or guide line (see <see cref="TapePoint"/>), else from the start along an
    /// axis or parallel to the edge rested on last (see <see cref="InferFrom"/>), else where the mouse hits the model.</summary>
    private (Vec3 Point, Snap? Kind, InferDir? Along)? TapeEnd((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        var tp = TapePoint(hit, pos);
        if (tp is { Kind: not null } || _tapeStart is not { } start) return tp is { } t ? (t.Point, t.Kind, null) : null;
        if (InferFrom(start, pos, null, InferDirs(null, [])) is { } inf) return (inf.P, null, inf.Dir);
        return tp is { } f ? (f.Point, null, null) : null;
    }

    /// <summary>The point the tape points at: a corner close by (12 pixels), else where the mouse hits the model.</summary>
    private (Vec3 Point, Snap? Kind)? TapePoint((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_doc == null) return null;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        (Vec3 V, double Px)? near = null;
        foreach (var b in _doc.Bodies)
            foreach (var v in b.Vertices)
            {
                double px = (Screen(v) - pos).Length;
                if (px < 12 && (near == null || px < near.Value.Px)) near = (v, px);
            }
        if (near is { } nv) return (nv.V, Snap.Vertex);
        if (hit is not { } h) return null;
        var p = V(h.Point);
        var face = _doc.Bodies[h.Face.Body].Faces[h.Face.Face];
        if (face.Surface is BrepPlane pl && GuideSnap(p, pos, FaceGeometry.Normal(face), pl.Origin) is { } g) return g;
        return (p, null);
    }

    private void HoverTape((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_tapeStart != null) { TapeTo(hit, pos); return; }
        if (MovePickAt(hit, pos) is { Pick.Edges.Count: > 0 } p && _doc!.Bodies[p.Body].Edges[p.Pick.Edges.First()] is { Curve: BrepLine } edge)
        {
            ShowSketch([_doc.Bodies[p.Body].Vertices[edge.Start], _doc.Bodies[p.Body].Vertices[edge.End]], default, color: SelectColor);
            StatusText.Text = T("📏 Klik om vanaf deze rand een hulplijn te trekken (evenwijdig eraan).", "📏 Click to pull a guide line off this edge (parallel to it).");
            return;
        }
        var tp = TapePoint(hit, pos);
        ShowSketch(tp is { } t ? [t.Point] : []);
        StatusText.Text = tp is { Kind: { } k } ? T($"📏 {k.Name()} · klik om vanaf hier te meten.", $"📏 {k.Name()} · click to measure from here.") : T("📏 Klik een punt om te meten, of een rand voor een hulplijn. Delete: alle hulplijnen weg.", "📏 Click a point to measure, or an edge for a guide line. Delete: remove all guide lines.");
    }

    private void StartTape((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_doc == null) return;
        _typed = "";
        if (MovePickAt(hit, pos) is { Pick.Edges.Count: > 0 } p && _doc.Bodies[p.Body].Edges[p.Pick.Edges.First()] is { Curve: BrepLine } edge && hit is { } h)
        {
            var b = _doc.Bodies[p.Body];
            _tapeEdge = (b.Vertices[edge.Start], (b.Vertices[edge.End] - b.Vertices[edge.Start]).Normalized(), h.Face);
            _tapeStart = p.Grab; _tapeOffset = default;
            MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
            StatusText.Text = T("📏 Trek de hulplijn van de rand af; klik om hem te plaatsen, of typ de afstand + Enter. Esc breekt af.", "📏 Pull the guide line off the edge; click to place it, or type the distance + Enter. Esc cancels.");
            return;
        }
        if (TapePoint(hit, pos) is not { } tp) return;
        _tapeStart = tp.Point; _tapeEdge = null;
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
        StatusText.Text = T("📏 Beweeg naar het punt om te meten en klik. Esc breekt af.", "📏 Move to the point to measure and click. Esc cancels.");
    }

    private void TapeTo((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_tapeStart is not { } start || _typed.Length > 0) return;
        if (_tapeEdge is { } te)
        {
            // the point under the mouse: on the model, or else on the plane of the face the edge was picked on
            Vec3? p = hit is { } h ? V(h.Point) : OnPlane(te.Face, pos);
            if (p is not { } pp) return;
            var (g, off) = Guides.Parallel(te.A, te.Dir, pp);
            off = off.Length > 0 ? off.Normalized() * (Math.Round(off.Length * Display * 10) / 10 / Display) : off;
            _tapeOffset = off;
            g = new Guide(te.A + off, te.Dir);
            double len = Math.Max(_modelSize, 10) * 1.5 / Display;
            ShowSketch([g.Point - g.Dir * len, g.Point + g.Dir * len], default, color: GuideColor);
            MeasureBox.Text = Mm(off.Length);
            StatusText.Text = T($"📏 Hulplijn op {Mm(off.Length)} mm van de rand · klik om hem te plaatsen, of typ de afstand + Enter. Esc breekt af.", $"📏 Guide line {Mm(off.Length)} mm from the edge · click to place it, or type the distance + Enter. Esc cancels.");
            return;
        }
        if (TapeEnd(hit, pos) is not { } tp) return;
        ShowSketch([start, tp.Point], default, color: tp.Along?.Color);
        MeasureBox.Text = Mm((tp.Point - start).Length);
        string along = tp.Along is { } al ? " · " + al.Name : "";
        StatusText.Text = T($"📏 {Units.Measure(tp.Point - start)}{along}{(tp.Kind is { } tk ? " · 🎯 " + tk.Name() : "")} · klik om de maat vast te houden.", $"📏 {Units.Measure(tp.Point - start)}{along}{(tp.Kind is { } tk2 ? " · 🎯 " + tk2.Name() : "")} · click to keep the measurement.");
    }

    private void FinishTape((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_tapeStart is not { } start) return;
        if (_tapeEdge is { } te)
        {
            if (_tapeOffset.Length < 1e-9) { StatusText.Text = T("📏 Trek de hulplijn eerst een stukje van de rand af.", "📏 Pull the guide line a little way off the edge first."); return; }
            AddGuide(new Guide(te.A + _tapeOffset, te.Dir), _tapeOffset.Length);
            EndTape();
            return;
        }
        if (TapeEnd(hit, pos) is not { } tp) return;
        EndTape();
        ShowSketch([start, tp.Point], default, color: tp.Along?.Color);
        MeasureBox.Text = Mm((tp.Point - start).Length);
        StatusText.Text = T($"📏 Gemeten: {Units.Measure(tp.Point - start)}.", $"📏 Measured: {Units.Measure(tp.Point - start)}.");
        Log($"measured {Units.Measure(tp.Point - start)}");
    }

    /// <summary>A typed distance with the tape from an edge: the guide that far off, the way it is being pulled.</summary>
    private void TypedTape(double mm)
    {
        if (_tapeEdge is not { } te) return;
        if (_tapeOffset.Length < 1e-12) { StatusText.Text = T("📏 Trek eerst een stukje de kant op waar de hulplijn moet komen.", "📏 First pull a little way towards where the guide line is to go."); return; }
        var off = _tapeOffset.Normalized() * (Math.Abs(mm) / Display);
        AddGuide(new Guide(te.A + off, te.Dir), off.Length);
        EndTape();
    }

    private void AddGuide(Guide g, double offset)
    {
        _guides.Add(g);
        RenderGuides();
        ShowSketch([]);
        MeasureBox.Text = Mm(offset);
        StatusText.Text = T($"✅ Hulplijn op {Mm(offset)} mm. Rechthoek, cirkel, lijn en verplaatsen klikken erop vast.", $"✅ Guide line at {Mm(offset)} mm. Rectangle, circle, line and move snap to it.");
        Log($"guide at {Mm(offset)} mm");
    }

    private void EndTape()
    {
        _tapeStart = null; _tapeEdge = null; _tapeOffset = default;
        ShowSketch([]);
    }

    // ------------------------------------------------------------------ the paint bucket

    public static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    /// <summary>The palette: name and colour (null: the standard colour, the paint taken off).</summary>
    public static (string Name, uint? Color)[] Palette =>
    [
        (T("standaard (geen kleur)", "standard (no colour)"), null),
        (T("wit", "white"), 0xFFF8FAFC), (T("lichtgrijs", "light grey"), 0xFFCBD5E1), (T("grijs", "grey"), 0xFF64748B), (T("antraciet", "anthracite"), 0xFF1E293B), (T("zwart", "black"), 0xFF0F172A),
        (T("rood", "red"), 0xFFE11D48), (T("oranje", "orange"), 0xFFF97316), (T("geel", "yellow"), 0xFFFACC15), (T("lichtgroen", "light green"), 0xFF84CC16), (T("groen", "green"), 0xFF16A34A),
        (T("turkoois", "turquoise"), 0xFF14B8A6), (T("lichtblauw", "light blue"), 0xFF38BDF8), (T("blauw", "blue"), 0xFF2563EB), (T("paars", "purple"), 0xFF7C3AED), (T("roze", "pink"), 0xFFEC4899),
        (T("bruin", "brown"), 0xFF92400E), (T("hout", "wood"), 0xFFC8A27A),
    ];

    /// <summary>The colour the bucket holds (null: none, taking paint off).</summary>
    private uint? _paint = 0xFFE11D48;

    /// <summary>The colour the bucket holds (null: none, taking paint off).</summary>
    public uint? Paint => _paint;

    /// <summary>The bucket's colour changed (the window marks its swatch).</summary>
    public event Action? PaintChanged;

    /// <summary>A colour for the bucket (a swatch clicked, or taken up from a face).</summary>
    public void SetPaint(uint? color)
    {
        _paint = color;
        PaintChanged?.Invoke();
        PaintName.Text = Palette.First(p => p.Color == color).Name;
        if (EraserMode || !PaintMode) return;
        StatusText.Text = T($"🎨 Verf: {PaintName.Text}. Klik een vlak · Ctrl: het hele lichaam · Shift: alle vlakken in dezelfde kleur · Alt: kleur overnemen.", $"🎨 Paint: {PaintName.Text}. Click a face · Ctrl: the whole body · Shift: all faces of the same colour · Alt: pick up a colour.");
    }

    /// <summary>The pipette (💧): the next click with the bucket takes up a colour (as Alt+click).</summary>
    private bool _sampleOnce;

    /// <summary>💧: the next click takes up a colour.</summary>
    public void Sample()
    {
        SetTool(Tool.Paint);
        _sampleOnce = true;
        StatusText.Text = T("💧 Pipet: klik een vlak om zijn kleur over te nemen.", "💧 Eyedropper: click a face to pick up its colour.");
    }

    /// <summary>⛶: the whole model in view.</summary>
    public void ZoomExtents() => ZoomAll(300);

    /// <summary>The view cube clicked: the model seen from <paramref name="from"/> (a side, an edge or a corner of the
    /// cube: x right, y back, z up): the camera turned round the point it looks at, as far from it as it was (the zoom
    /// left as it is).</summary>
    public void ViewFrom(Vector3D from, string name)
    {
        if (from.Length < 1e-9) return;
        from.Normalize();
        var cam = View.Camera;
        var target = cam.Position + cam.LookDirection;
        var look = from * -cam.LookDirection.Length;
        // (up the z axis, unless looking straight down or up: then the front at the bottom, or at the top from below)
        SetCamera(target - look, look, Math.Abs(from.Z) > 0.999 ? new Vector3D(0, from.Z > 0 ? 1 : -1, 0) : new Vector3D(0, 0, 1));
        StatusText.Text = T($"🧊 Aanzicht: {name}.", $"🧊 View: {name}.");
        Log($"view from {name}");
    }

    /// <summary>Everything shown in view, looking the way the camera looks: the camera put back along its look until the
    /// corners of the box round the model just fit (90 % of the narrower side).</summary>
    private void ZoomAll(double ms)
    {
        if (Picking.Bounds(_meshes) is not { } box || View.Camera is not PerspectiveCamera cam) return;
        var lo = new Point3D(box.Min.X, box.Min.Y, box.Min.Z);
        var hi = new Point3D(box.Max.X, box.Max.Y, box.Max.Z);
        var centre = lo + (hi - lo) * 0.5;
        // (the way the camera looks; from the front left, a little above, when it has none that can be used)
        var f = CameraOk() ? cam.LookDirection : new Vector3D(1, 1.4, -0.9); f.Normalize();
        var r = Vector3D.CrossProduct(f, new Vector3D(0, 0, 1)); if (r.Length < 1e-9) r = new Vector3D(1, 0, 0); r.Normalize();
        var u = Vector3D.CrossProduct(r, f);
        var (tx, ty) = ViewReach(cam);
        double far = ViewAngles.FitDistance(BoxCorners(lo, hi), Raw(centre), Raw(f), tx, ty);
        // (up the z axis, unless looking straight down or up)
        cam.Set(centre - f * far, f * far, Math.Abs(f.Z) > 0.999 ? u : new Vector3D(0, 0, 1));
    }

    /// <summary>The frame dragged with 🔎 fills the view: the camera turned to its middle (the model there, or as far as
    /// the camera looks) and moved closer in proportion.</summary>
    private void ZoomToRectangle(Rect r)
    {
        if (View.Frame is not { } f) return;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var ray = f.Ray(c);
        double dist = FaceAt(c) is { } h ? ((Vec3)h.Point - f.Eye).Length : View.Camera.LookDirection.Length;
        double s = Math.Max(r.Width / f.Width, r.Height / f.Height);
        var target = ray.At(dist);
        var look = ray.Direction * (dist * s);
        SetCamera(target - look, look, View.Camera.UpDirection);
    }

    /// <summary>The tangents of half the view across and up.</summary>
    private (double Tx, double Ty) ViewReach(PerspectiveCamera cam)
    {
        double w = View.ActualWidth > 0 ? View.ActualWidth : 1000, h = View.ActualHeight > 0 ? View.ActualHeight : 700;
        double t = Math.Tan(cam.FieldOfView * Math.PI / 360);
        return Viewport3DHelper.Reach(View) ?? (t * w / h, t);
    }

    /// <summary>A point or direction of the 3D view as it is (no change of unit), for the sums in ViewAngles.</summary>
    private static Vec3 Raw(Point3D p) => new(p.X, p.Y, p.Z);
    private static Vec3 Raw(Vector3D v) => new(v.X, v.Y, v.Z);

    /// <summary>The eight corners of a box.</summary>
    private static List<Vec3> BoxCorners(Point3D lo, Point3D hi) =>
        new[] { lo.X, hi.X }.SelectMany(x => new[] { lo.Y, hi.Y }.SelectMany(y => new[] { lo.Z, hi.Z }.Select(z => new Vec3(x, y, z)))).ToList();

    /// <summary>A click with the bucket: the face painted (Ctrl the whole body, Shift every face of the same colour in the
    /// body), or with Alt its colour taken up.</summary>
    private void PaintAt((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc is not { } before) return;
        var mods = Mods;
        if ((mods & ModifierKeys.Alt) != 0 || _sampleOnce)
        {
            _sampleOnce = false;
            SetPaint(before.ColorOf(h.Face));
            StatusText.Text = T($"🎨 Kleur overgenomen: {PaintName.Text}.", $"🎨 Colour picked up: {PaintName.Text}.");
            return;
        }
        var body = before.Bodies[h.Face.Body];
        IEnumerable<FaceRef> faces = GroupTaken(h.Face.Body) is { } pg
            ? before.Members(pg).SelectMany(b => Enumerable.Range(0, before.Bodies[b].Faces.Count).Select(f => new FaceRef(b, f)))
            : (mods & ModifierKeys.Control) != 0
            ? Enumerable.Range(0, body.Faces.Count).Select(f => new FaceRef(h.Face.Body, f))
            : (mods & ModifierKeys.Shift) != 0
                ? Enumerable.Range(0, body.Faces.Count).Select(f => new FaceRef(h.Face.Body, f)).Where(f => before.ColorOf(f) == before.ColorOf(h.Face))
                : _multi is { } mp && mp.Body == h.Face.Body && mp.Faces.Contains(h.Face.Face) ? mp.Faces.Select(f => new FaceRef(mp.Body, f)) : [h.Face];
        var list = faces.ToList();
        if (list.All(f => before.ColorOf(f) == _paint)) return;
        var doc = before.Paint(list, _paint);
        Commit(before, doc);
        _last = null; _lastMove = null;
        Render();
        StatusText.Text = T($"✅ {(list.Count == 1 ? "Vlak" : $"{list.Count} vlakken")} geverfd: {PaintName.Text}.", $"✅ {(list.Count == 1 ? "Face" : $"{list.Count} faces")} painted: {PaintName.Text}.");
        Log($"paint {list.Count} face(s) of body {h.Face.Body} {(_paint is { } c ? $"#{c:X8}" : "none")}");
    }

    // ------------------------------------------------------------------ the eraser

    private static readonly Color EraseColor = Color.FromRgb(0xE1, 0x1D, 0x48);

    /// <summary>The edge of the face under the mouse nearest to it on screen (within 10 pixels): body, edge.</summary>
    private (int Body, int Edge)? EdgeAt((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return null;
        var body = _doc.Bodies[h.Face.Body];
        (int, double)? best = null;
        foreach (var c in body.Faces[h.Face.Face].Loops.SelectMany(l => l))
        {
            var pts = FaceGeometry.EdgePoints(body, body.Edges[c.Edge]).Select(p => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(p))).ToList();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var (a, b) = (pts[i], pts[i + 1]);
                var ab = b - a; double l2 = ab.LengthSquared;
                double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - a, ab) / l2, 0, 1);
                double d = (a + ab * t - pos).Length;
                if (d < 10 && (best == null || d < best.Value.Item2)) best = (c.Edge, d);
            }
        }
        return best is { } bb ? (h.Face.Body, bb.Item1) : null;
    }

    private void HoverErase((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (GuideAt(pos) is int gi)
        {
            double len = Math.Max(_modelSize, 10) * 1.5 / Display;
            ShowSketch([_guides[gi].Point - _guides[gi].Dir * len, _guides[gi].Point + _guides[gi].Dir * len], default, color: EraseColor);
            StatusText.Text = T("⌫ Klik om deze hulplijn weg te halen.", "⌫ Click to remove this guide line.");
            return;
        }
        if (LooseAt(pos) is int li)
        {
            ShowSketch(_doc!.Lines[li].Points(), default, color: EraseColor);
            StatusText.Text = T("⌫ Klik om deze losse lijn weg te halen.", "⌫ Click to remove this loose line.");
            return;
        }
        if (EdgeAt(hit, pos) is not { } e) { ShowSketch([]); StatusText.Text = T("⌫ Wijs een getekende lijn, rechthoek of cirkel aan.", "⌫ Point at a drawn line, rectangle or circle."); return; }
        var body = _doc!.Bodies[e.Body];
        var face = body.Faces[hit!.Value.Face.Face];
        ShowSketch(FaceGeometry.EdgePoints(body, body.Edges[e.Edge]), face.Surface is BrepPlane ? FaceGeometry.Normal(face) : default, color: EraseColor);
        StatusText.Text = T("⌫ Klik om deze rand te wissen.", "⌫ Click to erase this edge.");
    }

    private void EraseAt((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (GuideAt(pos) is int gi) { _guides.RemoveAt(gi); RenderGuides(); ShowSketch([]); StatusText.Text = "✅ Hulplijn weggehaald."; Log("guide erased"); return; }
        if (LooseAt(pos) is int li && _doc is { } lbefore)
        {
            Commit(lbefore, lbefore.RemoveLines([lbefore.Lines[li]]));
            ShowSketch([]);
            Render();
            StatusText.Text = T("✅ Losse lijn weggehaald.", "✅ Loose line removed.");
            Log($"loose line {li} erased");
            return;
        }
        if (EdgeAt(hit, pos) is not { } e || _doc is not { } before) return;
        try
        {
            var doc = before.Erase(e.Body, e.Edge);
            Commit(before, doc);
            _last = null; _lastMove = null; _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null; _selected = null; _hover = null;
            ShowSketch([]);
            Render();
            StatusText.Text = T("✅ Gewist: de vlakken zijn weer één.", "✅ Erased: the faces are one again.");
            Log($"erase edge {e.Edge} of body {e.Body}: ok, {doc.Bodies[e.Body].Faces.Count} faces");
        }
        catch (PushPullException ex)
        {
            StatusText.Text = "⛔ " + ex.Message;
            Log($"erase edge {e.Edge} of body {e.Body}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ push/pull

    private SketchDocument? _preview;
    private FaceRef? _previewFace;

    /// <summary>While dragging: the distances (mm) to faces on the back side, right across the face and facing away
    /// from it (pushed in that far it goes through: a hole).</summary>
    private List<double> _through = new();

    private List<double> ThroughDepths(FaceRef f)
    {
        var body = _doc!.Bodies[f.Body];
        var face = body.Faces[f.Face];
        var n = FaceGeometry.Normal(face); var o = ((BrepPlane)face.Surface).Origin;
        var tris = _doc.TrianglesOf(f);
        if (tris.Count == 0) return [];
        var mid = tris.Select(t => (t.A + t.B + t.C) * (1.0 / 3)).Aggregate((a, b) => a + b) * (1.0 / tris.Count);
        var depths = new List<double>();
        for (int g = 0; g < body.Faces.Count; g++)
        {
            if (g == f.Face || body.Faces[g].Surface is not BrepPlane pg || FaceGeometry.Normal(body.Faces[g]).Dot(n) > -0.999) continue;
            double d = (o - pg.Origin).Dot(n);
            if (d < 1e-6) continue;
            if (PushPullMe.Core.Drawing.Contains(body, g, mid - n * d)) depths.Add(d * Display);
        }
        return depths.Distinct().Order().ToList();
    }

    private void StartDrag(FaceRef f, Point3D at)
    {
        var face = _doc!.Bodies[f.Body].Faces[f.Face];
        _through = face.Surface is BrepPlane ? ThroughDepths(f) : [];
        _dragFace = f; _dragStart = at; _dragBase = _doc; _dragDist = 0; _dragValid = false; _typed = "";
        Vec3 n;
        if (face.Surface is BrepSphere sp) n = (V(at) - sp.Centre).Normalized() * (face.Reversed ? -1 : 1);
        else if (face.Surface is BrepTorus tr)
        {
            // away from the tube's centre circle
            var ax = tr.Axis.Normalized(); var p = V(at);
            var w = p - tr.Centre; w -= ax * w.Dot(ax);
            var ring = tr.Centre + w.Normalized() * tr.MajorRadius;
            n = (p - ring).Normalized() * (face.Reversed ? -1 : 1);
        }
        else if (face.Surface is BrepCylinder cy)
        {
            var axis = cy.Axis.Normalized(); var p = V(at);
            var foot = cy.Origin + axis * (p - cy.Origin).Dot(axis);
            n = (p - foot).Normalized() * (face.Reversed ? -1 : 1);
        }
        else n = FaceGeometry.Normal(face);
        _dragNormal = new Vector3D(n.X, n.Y, n.Z);
        _dragTurn = face.Surface is BrepPlane ? PushPullMe.Core.Revolve.EndAxis(_doc.Bodies[f.Body], f.Face) : null;
        _dragTurnAngle = 0;
        if (_dragTurn != null) _through = [];
        _selected = null;
        _dragRadius = HasRadius(_doc, f);
        MeasureLabel.Text = _dragRadius ? T("Straal", "Radius") : T("Afstand", "Distance");
        MeasureBox.Text = _dragRadius ? Mm(RadiusOf(_doc, f)) : "0";
        StatusText.Text = _dragRadius
            ? T($"⇕ {CylinderText(_doc, f)} · beweeg de muis om de straal te veranderen; klik om vast te zetten, of typ de straal + Enter. Esc breekt af.", $"⇕ {CylinderText(_doc, f)} · move the mouse to change the radius; click to set it, or type the radius + Enter. Esc cancels.")
            : face.Surface is BrepCylinder
            ? T($"⇕ {CylinderText(_doc, f)} · beweeg de muis om de straal te veranderen; klik om vast te zetten, of typ de afstand + Enter. Esc breekt af.", $"⇕ {CylinderText(_doc, f)} · move the mouse to change the radius; click to set it, or type the distance + Enter. Esc cancels.")
            : T("⇕ Beweeg de muis om het vlak te verschuiven; klik om vast te zetten, of typ de afstand + Enter. Esc breekt af.", "⇕ Move the mouse to shift the face; click to set it, or type the distance + Enter. Esc cancels.");
    }

    private void EndDrag()
    {
        HideTag();
        if (_movePick != null) EndMove();
        if (_tapeStart != null) EndTape();
        _dragFace = null; _dragBase = null; _preview = null; _previewFace = null; _dragValid = false;
        _previewMap = null; _previewNew.Clear(); _snapBase = null;
    }

    /// <summary>The distance (mm) along the face's normal nearest to the mouse ray (the line through where the face was
    /// grabbed, along its normal, against the ray under the mouse).</summary>
    private double? DistanceAt(Point pos)
    {
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return null;
        if (_dragTurn is { } tn)
        {
            // the end of a turned piece: the angle round its axis where the mouse is (on the plane square to the axis
            // through where it was grabbed), as the way of its middle along the turn
            var a = tn.Dir.Normalized(); var start = V(_dragStart);
            var org = V(ray.Origin); var rd = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
            double den = rd.Dot(a);
            if (Math.Abs(den) < 1e-6) return null;
            var q = org + rd * ((start - org).Dot(a) / den);
            var foot = tn.Point + a * (start - tn.Point).Dot(a);
            Vec3 r0 = start - foot, r1 = q - foot;
            if (r0.Length < 1e-12 || r1.Length < 1e-12) return null;
            double ang = Math.Atan2(r0.Cross(r1).Dot(a), r0.Dot(r1));
            // (on from where it was: past half a turn too)
            ang += 2 * Math.PI * Math.Round((_dragTurnAngle - ang) / (2 * Math.PI));
            _dragTurnAngle = ang;
            return ang * tn.Radius * Display;
        }
        var dir = ray.Direction; dir.Normalize();
        double b = Vector3D.DotProduct(_dragNormal, dir);
        if (1 - b * b < 1e-6) return null;
        var w0 = _dragStart - ray.Origin;
        double d = Vector3D.DotProduct(_dragNormal, w0), e = Vector3D.DotProduct(dir, w0);
        return (b * e - d) / (1 - b * b);
    }

    private void DragTo(Point pos)
    {
        if (_dragFace is not { } f || _dragBase == null || _typed.Length > 0) return;
        double mm;
        string? flush = null;
        var snap = FlushAt(pos);
        if (snap is { } s) { mm = s.Mm; flush = s.Label; _snapBase = s.Face; }
        else
        {
            _snapBase = null;
            if (DistanceAt(pos) is not double m) return;
            // steps of 0.1 mm (finer than that is typed); close to the back side right across: through to it (a hole)
            mm = Math.Round(m * 10) / 10;
            if (_dragRadius)
                try
                {
                    // (a pin, hole or rounding: steps of 0.1 mm in its radius)
                    var rb = _dragBase.Bodies[f.Body];
                    double radius = Math.Round(PushPullMe.Core.PushPull.RadiusAfter(rb, f.Face, m / Display) * Display * 10) / 10;
                    mm = PushPullMe.Core.PushPull.DistanceForRadius(rb, f.Face, radius / Display) * Display;
                }
                catch (PushPullException) { }
            foreach (double depth in _through)
                if (Math.Abs(m + depth) < Math.Max(0.4, 0.03 * depth)) { mm = -depth; flush = T("door tot de achterkant (een gat)", "through to the back (a hole)"); break; }
        }
        if (Math.Abs(mm / Display - _dragDist) < 1e-12 && (_preview != null || Math.Abs(mm) < 1e-9)) { UpdateHighlight(); ShowDragStatus(flush); ShowRadiusTag(pos); return; }
        Preview(mm / Display, flush);
        ShowRadiusTag(pos);
    }

    /// <summary>
    /// "Gelijk met dit vlak / dit punt": under the mouse a corner (within 12 pixels) or a face parallel to the one being
    /// moved (not that face itself, nor a side face it just made); the distance (mm) that brings the face level with it,
    /// and that face in the document the drag started from.
    /// </summary>
    private (double Mm, string Label, FaceRef Face)? FlushAt(Point pos)
    {
        if (_dragFace is not { } f || _dragBase == null || _shown == null || _dragTurn != null) return null;
        var baseFace = _dragBase.Bodies[f.Body].Faces[f.Face];
        if (baseFace.Surface is not BrepPlane) return null;
        var origin = ((BrepPlane)baseFace.Surface).Origin;
        var n = FaceGeometry.Normal(baseFace);
        // (what is shown is the preview only while it is valid; its face numbers differ from the document's)
        var map = _preview != null && ReferenceEquals(_shown, _preview) ? _previewMap : null;
        if (map != null && f.Face >= map.Count) return null;
        int moving = map != null ? map[f.Face] : f.Face;
        // (the face can be gone in the preview: merged away)
        if (f.Body >= _shown.Bodies.Count || moving < 0 || moving >= _shown.Bodies[f.Body].Faces.Count) return null;
        foreach (var m in Hits(pos))
        {
            if (m.Triangle >= m.Map.Count) continue;
            int shownFace = m.Map[m.Triangle];
            // only what is in front counts: the face being moved (or a side face it made) hides what is behind it; a face
            // of another part counts as well (blocks made apart, side by side)
            bool own = m.Body == f.Body;
            if (own && (shownFace == moving || (map != null && _previewNew.Contains(shownFace)))) return null;
            if (m.Body >= _shown.Bodies.Count) return null;
            var body = _shown.Bodies[m.Body];
            if (shownFace >= body.Faces.Count) return null;
            var face = body.Faces[shownFace];
            int baseIndex = !own || map == null ? shownFace : IndexOf(map, shownFace);
            if (baseIndex < 0) return null;
            // a corner of it close to the mouse (not one that moves along)
            var movingVerts = !own || map == null ? [] : body.Faces[moving].Loops.SelectMany(l => l).Select(c => body.Edges[c.Edge]).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End }).ToHashSet();
            var corners = face.Loops.SelectMany(l => l).Select(c => body.Edges[c.Edge]).Where(e => e.Start >= 0).SelectMany(e => new[] { e.Start, e.End })
                .Distinct().Where(v => !movingVerts.Contains(v)).Select(v => body.Vertices[v]).ToList();
            var near = corners.Select(v => (V: v, Px: (Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v)) - pos).Length)).Where(x => x.Px < 12).OrderBy(x => x.Px).ToList();
            if (near.Count > 0) return ((near[0].V - origin).Dot(n) * Display, T("gelijk met dit punt", "level with this point"), new FaceRef(m.Body, baseIndex));
            if (face.Surface is BrepPlane pl && Math.Abs(pl.Normal.Normalized().Dot(n)) > 1 - 1e-6)
                return ((pl.Origin - origin).Dot(n) * Display, T("gelijk met dit vlak", "level with this face"), new FaceRef(m.Body, baseIndex));
            return null;
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<int> map, int value)
    {
        for (int i = 0; i < map.Count; i++) if (map[i] == value) return i;
        return -1;
    }

    private void ShowDragStatus(string? flush)
    {
        if (!_dragValid) return;
        string cyl = _preview != null && _previewFace is { } pf && _preview.Bodies[pf.Body].Faces[pf.Face].Surface is BrepCylinder ? " · " + CylinderText(_preview, pf) : "";
        StatusText.Text = _dragRadius
            ? T($"⇕ {cyl.TrimStart(' ', '·')} · klik om vast te zetten, of typ de straal + Enter. Esc breekt af.", $"⇕ {cyl.TrimStart(' ', '·')} · click to set it, or type the radius + Enter. Esc cancels.")
            : T($"⇕ {_dragDist * Display:0.##} mm{cyl}{(flush != null ? " · 🎯 " + flush : "")} · klik om vast te zetten, of typ de afstand + Enter. Esc breekt af.", $"⇕ {_dragDist * Display:0.##} mm{cyl}{(flush != null ? " · 🎯 " + flush : "")} · click to set it, or type the distance + Enter. Esc cancels.");
    }

    /// <summary>While a rounding, pin or hole is dragged: its new radius next to the mouse.</summary>
    private void ShowRadiusTag(Point pos)
    {
        if (!_dragRadius || _dragFace is not { } f || _dragBase == null) { HideTag(); return; }
        string r = RadiusText(_dragBase, f, _dragDist);
        if (r.Length > 0 && _dragValid) ShowTag("R " + r + T(" mm", " mm"), pos); else HideTag();
    }

    private static double RadiusOf(SketchDocument doc, FaceRef f) => doc.Bodies[f.Body].Faces[f.Face].Surface switch
    {
        BrepCylinder c => c.Radius,
        BrepSphere s => s.Radius,
        BrepTorus t => t.MinorRadius,
        _ => 0,
    };

    /// <summary>The radius (mm, as text) a cylinder face gets when pushed/pulled over dist; empty when that cannot be told.</summary>
    private static string RadiusText(SketchDocument doc, FaceRef f, double dist)
    {
        try { return Mm(PushPullMe.Core.PushPull.RadiusAfter(doc.Bodies[f.Body], f.Face, dist)); }
        catch (PushPullException) { return ""; }
    }

    /// <summary>A cylinder face in words: a rounding and its radius, or a hole or a rod and its diameter.</summary>
    private static string CylinderText(SketchDocument doc, FaceRef f)
    {
        var face = doc.Bodies[f.Body].Faces[f.Face];
        if (face.Surface is BrepSphere sp) return T($"bolhoek R {sp.Radius * Display:0.##} mm", $"ball corner R {sp.Radius * Display:0.##} mm");
        if (face.Surface is BrepTorus tr) return T($"hoekstuk R {tr.MinorRadius * Display:0.##} mm", $"corner piece R {tr.MinorRadius * Display:0.##} mm");
        if (face.Surface is not BrepCylinder c) return "";
        if (PushPullMe.Core.PushPull.IsRounding(doc.Bodies[f.Body], f.Face)) return T($"ronding R {c.Radius * Display:0.##} mm", $"rounding R {c.Radius * Display:0.##} mm");
        return T($"{(face.Reversed ? "gat" : "cilinder")} R {c.Radius * Display:0.##} mm (Ø {c.Radius * 2 * Display:0.##})", $"{(face.Reversed ? "hole" : "cylinder")} R {c.Radius * Display:0.##} mm (Ø {c.Radius * 2 * Display:0.##})");
    }

    private void Preview(double dist, string? flush = null)
    {
        if (_dragFace is not { } f || _dragBase == null) return;
        _dragDist = dist;
        MeasureBox.Text = _dragRadius ? RadiusText(_dragBase, f, dist) : (dist * Display).ToString("0.##", CultureInfo.CurrentCulture);
        if (Math.Abs(dist) < 1e-9) { _preview = null; _previewMap = null; _dragValid = false; Render(_dragBase); return; }
        try
        {
            var (doc, r) = _dragBase.PushPull(f, dist, checkCrossing: false);
            if (r.FaceMap[f.Face] < 0 && !r.Punched) throw new PushPullException(T("Zo verdwijnt het vlak helemaal.", "That way the face disappears completely."));
            _preview = doc; _previewFace = new FaceRef(f.Body, r.FaceMap[f.Face]); _dragValid = true;
            _previewMap = r.FaceMap; _previewNew = r.NewFaces.ToHashSet();
            Render(doc);
            ShowDragStatus(flush);
        }
        catch (PushPullException ex)
        {
            _dragValid = false;
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    /// <summary>The push/pull done for good (with the check that nothing runs into another part).</summary>
    private void Finish(double dist)
    {
        if (_dragFace is not { } f || _dragBase is not { } before) return;
        var start = before;
        EndDrag(); EndDraw();
        _typed = "";
        if (Math.Abs(dist) < 1e-9) { Render(); return; }
        Apply(start, f, dist, replaceLast: false);
    }

    private void Apply(SketchDocument before, FaceRef f, double dist, bool replaceLast)
    {
        try
        {
            var (doc, r) = before.PushPull(f, dist, join: Join);
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _last = (before, f, dist);
            _selected = r.FaceMap[f.Face] >= 0 ? new FaceRef(f.Body, r.FaceMap[f.Face]) : null;
            Render();
            bool rounding = HasRadius(before, f); // (pin, hole or rounding: shown by its radius)
            MeasureLabel.Text = rounding ? T("Straal", "Radius") : T("Afstand", "Distance");
            MeasureBox.Text = rounding ? RadiusText(before, f, dist) : (dist * Display).ToString("0.##", CultureInfo.CurrentCulture);
            StatusText.Text = rounding
                ? T($"✅ Nu {CylinderText(doc, new FaceRef(f.Body, Math.Max(0, r.FaceMap[f.Face])))}. Typ een andere straal + Enter om het aan te passen.", $"✅ Now {CylinderText(doc, new FaceRef(f.Body, Math.Max(0, r.FaceMap[f.Face])))}. Type another radius + Enter to change it.")
                : before.Bodies[f.Body].Faces[f.Face].Surface is BrepCylinder
                ? T($"✅ {(dist > 0 ? "Getrokken" : "Geduwd")}: {Math.Abs(dist) * Display:0.##} mm, nu {CylinderText(doc, f)}. Typ een andere afstand + Enter om het aan te passen.", $"✅ {(dist > 0 ? "Pulled" : "Pushed")}: {Math.Abs(dist) * Display:0.##} mm, now {CylinderText(doc, f)}. Type another distance + Enter to change it.")
                : r.Punched ? T($"✅ Doorgeduwd: er gaat nu een gat doorheen ({Math.Abs(dist) * Display:0.##} mm).", $"✅ Pushed through: there is a hole through it now ({Math.Abs(dist) * Display:0.##} mm).")
                : T($"✅ {(dist > 0 ? "Getrokken" : "Geduwd")}: {Math.Abs(dist) * Display:0.##} mm. Typ een andere afstand + Enter om het aan te passen.", $"✅ {(dist > 0 ? "Pulled" : "Pushed")}: {Math.Abs(dist) * Display:0.##} mm. Type another distance + Enter to change it.");
            // (a piece of its own: the body would have touched itself only along an edge, as DesignSpark makes it)
            if (doc.Bodies.Count > before.Bodies.Count && !before.Bodies[f.Body].Sheet)
            {
                StatusText.Text = T($"✅ Getrokken als apart deel ({Math.Abs(dist) * Display:0.##} mm): als deel van het blok zou het dat alleen langs een rand raken, en dat kan een massief blok niet zijn.", $"✅ Pulled as a part of its own ({Math.Abs(dist) * Display:0.##} mm): as part of the block it would touch it only along an edge, which a solid block cannot do.");
                _selected = null;
            }
            Log($"pushpull face {f.Face} {dist * Display:0.###} mm: ok, {r.Body.Faces.Count} faces, {doc.Bodies.Count} bodies");
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log($"pushpull face {f.Face} {dist * Display:0.###} mm: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ arc from its centre, and pie (as in SketchUp)

    /// <summary>A sweep round a centre (◜ an arc, ◔ a pie): the centre set (stage 1), the start too (stage 2: the radius),
    /// the angle now (radians, counter-clockwise round the face's normal).</summary>
    private FaceRef? _swFace;
    private int _swStage;
    private Vec3 _swCentre, _swStart, _swDir;
    private double _swAngle;
    private string SweepIcon => PieMode ? "◔" : "◜";

    private void StartSweep((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T($"{SweepIcon} Dat kan alleen op een plat vlak.", $"{SweepIcon} That only works on a flat face."); return; }
        _swFace = h.Face; _swStage = 1; _swAngle = 0; _swDir = default; _typed = "";
        _swCentre = ArcPoint(h.Face, pos) ?? V(h.Point);
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = "";
        ShowSketch([_swCentre], NormalOf(h.Face));
        StatusText.Text = T($"{SweepIcon} Klik het beginpunt (de straal), of typ de straal + Enter. Esc breekt af.", $"{SweepIcon} Click the start point (the radius), or type the radius + Enter. Esc cancels.");
    }

    private void EndSweep()
    {
        if (_swFace == null) return;
        _swFace = null; _swStage = 0;
        MeasureUnit.Text = "mm";
        ShowSketch([]);
    }

    /// <summary>The point at an angle round the centre from the start (counter-clockwise round n).</summary>
    private Vec3 SweepPoint(Vec3 n, double angle) => new BrepTransform.Turn(_swCentre, n, angle).Point(_swStart);

    private void SweepTo(Point pos)
    {
        if (_swFace is not { } f || _doc == null || _typed.Length > 0) return;
        var n = NormalOf(f);
        if (_swStage == 1)
        {
            if (ArcPoint(f, pos) is not { } p) return;
            var d = p - _swCentre; d -= n * d.Dot(n);
            if (d.Length < 1e-9) return;
            _swDir = d.Normalized();
            ShowSketch([_swCentre, _swCentre + d], n);
            MeasureBox.Text = Mm(d.Length);
            return;
        }
        if (OnPlane(f, pos) is not { } q) return;
        var v = q - _swCentre; v -= n * v.Dot(n);
        if (v.Length < 1e-9) return;
        var r0 = (_swStart - _swCentre).Normalized();
        double deg = Math.Atan2(r0.Cross(v.Normalized()).Dot(n), r0.Dot(v.Normalized())) * 180 / Math.PI;
        // (past half a turn the way it went: on round, up to almost a whole one)
        if (_swAngle * 180 / Math.PI > 90 && deg < 0) deg += 360;
        if (_swAngle * 180 / Math.PI < -90 && deg > 0) deg -= 360;
        double snapped = Math.Round(deg / 15) * 15;
        deg = Math.Abs(deg - snapped) < 2.5 ? snapped : Math.Round(deg * 2) / 2;
        if (Math.Abs(deg) >= 359.5) deg = 359.5 * Math.Sign(deg);
        _swAngle = deg * Math.PI / 180;
        MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = Math.Abs(deg).ToString("0.#", CultureInfo.CurrentCulture);
        if (Math.Abs(deg) < 0.4) { ShowSketch([_swCentre, _swStart], n); return; }
        var arc = Enumerable.Range(0, 49).Select(k => SweepPoint(n, _swAngle * k / 48)).ToList();
        ShowSketch(PieMode ? arc.Prepend(_swCentre).Append(_swCentre).ToList() : arc, n);
        StatusText.Text = T($"{SweepIcon} {Math.Abs(deg).ToString("0.#", CultureInfo.CurrentCulture)}°, straal {Mm((_swStart - _swCentre).Length)} mm · klik, of typ de hoek + Enter. Esc breekt af.", $"{SweepIcon} {Math.Abs(deg).ToString("0.#", CultureInfo.CurrentCulture)}°, radius {Mm((_swStart - _swCentre).Length)} mm · click, or type the angle + Enter. Esc cancels.");
    }

    private void SweepClick(Point pos)
    {
        if (_swFace is not { } f || _doc is not { } before) return;
        if (_swStage == 1)
        {
            if (ArcPoint(f, pos) is not { } p) return;
            var n = NormalOf(f);
            var d = p - _swCentre; d -= n * d.Dot(n);
            if (d.Length < 1e-6) return;
            _swStart = _swCentre + d; _swStage = 2; _swAngle = 0;
            StatusText.Text = T($"{SweepIcon} Beweeg voor de hoek en klik, of typ de hoek + Enter. Esc breekt af.", $"{SweepIcon} Move for the angle and click, or type the angle + Enter. Esc cancels.");
            return;
        }
        SweepTo(pos);
        if (Math.Abs(_swAngle) < 1e-6) return;
        var (c, s0, ang, pie) = (_swCentre, _swStart, _swAngle, PieMode);
        EndSweep();
        ApplySweep(before, f, c, s0, ang, pie);
    }

    private void ApplySweep(SketchDocument before, FaceRef f, Vec3 centre, Vec3 start, double angle, bool pie)
    {
        var n = NormalOf(f);
        var t = new BrepTransform.Turn(centre, n, angle);
        var end = t.Point(start);
        var through = new BrepTransform.Turn(centre, n, angle / 2).Point(start);
        string what = pie ? "pie" : "centre arc";
        if (!pie && !(OnPlaneEdge(f, start) && OnPlaneEdge(f, end)))
        {
            AddLooseArc(before, f, start, end, through);
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = Math.Abs(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture);
            return;
        }
        try
        {
            var (doc, nf) = pie ? before.DrawPie(f, centre, start, end, through) : before.DrawArc(f, start, end, through);
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = Math.Abs(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture);
            StatusText.Text = T($"✅ {(pie ? "Taartpunt" : "Boog")} getekend ({Math.Abs(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture)}°). Kies ⇕ (P) om hem eruit te trekken of in te duwen.", $"✅ {(pie ? "Pie" : "Arc")} drawn ({Math.Abs(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture)}°). Choose ⇕ (P) to pull it out or push it in.");
            Log(string.Create(CultureInfo.InvariantCulture, $"{(pie ? "pie" : "centre arc")} on face {f.Face} r {(start - centre).Length * Display:0.###} mm {angle * 180 / Math.PI:0.#}°: ok, new face {nf.Face}"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"{what} on face {f.Face} {angle * 180 / Math.PI:0.#}°: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ booleans: union, subtract, intersect, trim, split

    private enum BoolTool { Union, Subtract, Intersect, Trim, Split }

    /// <summary>Outer Shell: every two parts that can be united, united, until none can.</summary>
    public void OuterShell()
    {
        if (_doc is not { } before) return;
        var doc = before; int unions = 0;
        for (bool again = true; again;)
        {
            again = false;
            for (int i = 0; i < doc.Bodies.Count && !again; i++)
                for (int j = i + 1; j < doc.Bodies.Count && !again; j++)
                {
                    try { doc = doc.Combine(i, j, Booleans.Op.Union).Doc; unions++; again = true; }
                    catch (PushPullException) { }
                }
        }
        if (unions == 0)
        {
            StatusText.Text = T("◫ Er waren geen delen die samen konden (ze moeten overlappen of met een vlak raken).", "◫ There were no parts that could go together (they must overlap or touch with a face).");
            return;
        }
        Commit(before, doc);
        _last = null; _lastMove = null; _selected = null; _hover = null;
        Render();
        StatusText.Text = T($"✅ Buitenschil: {before.Bodies.Count} delen werden {doc.Bodies.Count}.", $"✅ Outer shell: {before.Bodies.Count} parts became {doc.Bodies.Count}.");
        Log($"outer shell: {before.Bodies.Count} bodies -> {doc.Bodies.Count}");
    }

    private static string Icon(BoolTool t) => t switch { BoolTool.Union => "∪", BoolTool.Subtract => "∖", BoolTool.Intersect => "∩", BoolTool.Trim => "✂", _ => "⊘" };

    /// <summary>What the tool asks for its first click.</summary>
    private static string FirstAsk(BoolTool t) => t switch
    {
        BoolTool.Union => T("∪ Verenigen: klik het eerste deel.", "∪ Union: click the first part."),
        BoolTool.Subtract => T("∖ Aftrekken: klik het deel waar iets af moet.", "∖ Subtract: click the part to take something from."),
        BoolTool.Intersect => T("∩ Doorsnede: klik het eerste deel.", "∩ Intersect: click the first part."),
        BoolTool.Trim => T("✂ Wegsnijden: klik het deel dat bijgesneden wordt.", "✂ Trim: click the part to be trimmed."),
        _ => T("⊘ Splitsen: klik het eerste deel.", "⊘ Split: click the first part."),
    };

    /// <summary>The first body picked (by the face clicked on it).</summary>
    private FaceRef? _unionFirst;

    private void BoolClick(BoolTool tool, (FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc is not { } before) return;
        string icon = Icon(tool);
        // (a face without thickness takes no part: said at once, not after the second click)
        if (before.Bodies[h.Face.Body].Sheet)
        {
            string why = T("Dit is een vlak zonder dikte; trek het eerst uit (⇕) tot een blok.", "This is a face without thickness; pull it out first (⇕) into a block.");
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), why);
            StatusText.Text = icon + " ⛔ " + why;
            return;
        }
        if (_unionFirst is not { } first)
        {
            _unionFirst = h.Face; _selected = h.Face; UpdateHighlight();
            StatusText.Text = icon + " " + tool switch
            {
                BoolTool.Subtract => T("Klik het deel dat eraf gaat. Esc breekt af.", "Click the part that goes. Esc cancels."),
                BoolTool.Trim => T("Klik het deel dat snijdt (het blijft staan). Esc breekt af.", "Click the part that cuts (it stays). Esc cancels."),
                _ => T("Klik het tweede deel (dat het eerste overlapt of met een vlak raakt). Esc breekt af.", "Click the second part (one that overlaps the first or touches it with a face). Esc cancels."),
            };
            return;
        }
        if (h.Face.Body == first.Body) { StatusText.Text = icon + " " + T("Dat is hetzelfde deel; klik een ander deel.", "That is the same part; click another part."); return; }
        _unionFirst = null;
        try
        {
            var (doc, made) = tool switch
            {
                BoolTool.Union => before.Combine(first.Body, h.Face.Body, Booleans.Op.Union),
                BoolTool.Subtract => before.Combine(first.Body, h.Face.Body, Booleans.Op.Subtract),
                BoolTool.Intersect => before.Combine(first.Body, h.Face.Body, Booleans.Op.Intersect),
                BoolTool.Trim => before.Combine(first.Body, h.Face.Body, Booleans.Op.Subtract, keepSecond: true),
                _ => before.Split(first.Body, h.Face.Body),
            };
            Commit(before, doc);
            _last = null; _lastMove = null; _selected = null; _hover = null;
            Render();
            int faces = made.Sum(b => doc.Bodies[b].Faces.Count);
            StatusText.Text = "✅ " + (made.Count == 1
                ? T($"Klaar: één deel met {faces} vlakken.", $"Done: one part with {faces} faces.")
                : T($"Klaar: {made.Count} delen.", $"Done: {made.Count} parts."));
            Log(string.Create(CultureInfo.InvariantCulture, $"{tool} of bodies {first.Body} and {h.Face.Body}: ok, {made.Count} part(s), {faces} faces, volume {doc.Volume() * 1e9:0.###} mm³"));
        }
        catch (PushPullException ex)
        {
            _selected = null; UpdateHighlight();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log($"{tool} of bodies {first.Body} and {h.Face.Body}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ looking: zoom, zoom window, position camera, look around, walk

    /// <summary>Where a zoom, zoom window or look around started (on screen), and the camera then.</summary>
    private Point? _viewAnchor;
    private Point3D _viewPos; private Vector3D _viewLook, _viewUp;

    /// <summary>Where the camera was put to stand (the eye above it), and the eye height (scene units, mm).</summary>
    private Point3D? _camStand;
    private double _eyeHeight;

    private void EndView()
    {
        _viewAnchor = null;
        SetLasso([]);
    }

    private void ViewClick(Point pos)
    {
        if (View.Camera is not ProjectionCamera cam) return;
        if (_viewAnchor is { } start)
        {
            if (ZoomWindowMode)
            {
                var r = new Rect(start, pos);
                EndView();
                if (r.Width > 4 && r.Height > 4) ZoomToRectangle(r);
                StatusText.Text = T("🔎 Ingezoomd. Klik weer een hoek voor een volgend kader.", "🔎 Zoomed in. Click a corner again for another frame.");
                return;
            }
            EndView();
            StatusText.Text = T("Gestopt. Klik om verder te gaan.", "Stopped. Click to go on.");
            return;
        }
        _viewAnchor = pos; _viewPos = cam.Position; _viewLook = cam.LookDirection; _viewUp = cam.UpDirection;
        if (ZoomWindowMode)
        {
            SetLasso(Enumerable.Repeat(pos, 5));
            StatusText.Text = T("🔎 Klik de andere hoek van het kader. Esc breekt af.", "🔎 Click the other corner of the frame. Esc cancels.");
        }
        else StatusText.Text = ZoomMode ? T("🔍 Beweeg omhoog (in) of omlaag (uit); klik om te stoppen.", "🔍 Move up (in) or down (out); click to stop.")
            : T("👁 Beweeg om rond te kijken; klik om te stoppen.", "👁 Move to look around; click to stop.");
    }

    private void ViewTo(Point pos)
    {
        if (_viewAnchor is not { } start || View.Camera is not ProjectionCamera cam) return;
        if (ZoomWindowMode)
        {
            Point a = start, b = pos;
            SetLasso([a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a]);
            return;
        }
        if (ZoomMode)
        {
            // up: closer to the point looked at; down: further
            double f = Math.Exp((pos.Y - start.Y) / 200);
            var target = _viewPos + _viewLook;
            cam.LookDirection = _viewLook * f; cam.Position = target - _viewLook * f;
            return;
        }
        // look around (and walk): turn the head round the vertical and round the sideways axis, standing still
        var up = new Vector3D(0, 0, 1);
        var side = Vector3D.CrossProduct(_viewLook, up); side.Normalize();
        // (first round the vertical, then round the sideways axis as it was)
        Vector3D look = CameraPose.Rotate(CameraPose.Rotate(_viewLook, up, -(pos.X - start.X) * 0.3), side, -(pos.Y - start.Y) * 0.3);
        // (not straight up or down)
        var flat = new Vector3D(look.X, look.Y, 0);
        if (flat.Length < 0.05 * look.Length) return;
        cam.Position = _viewPos; cam.LookDirection = look; cam.UpDirection = up;
    }

    /// <summary>The camera put where the mouse is (on a face, else on the ground), the eye at eye height above it, looking
    /// level the way it looked; then on to looking around.</summary>
    private void PlaceCamera(Point pos)
    {
        Point3D? at = FaceAt(pos)?.Point;
        if (at == null && Viewport3DHelper.GetRay(View.Viewport, pos) is { } ray && Math.Abs(ray.Direction.Z) > 1e-9)
        {
            double t = -ray.Origin.Z / ray.Direction.Z;
            if (t > 0) at = ray.Origin + ray.Direction * t;
        }
        if (at is not { } p) return;
        if (_eyeHeight <= 0) _eyeHeight = Math.Max(_modelSize, 10) / 5;
        StandAt(p);
    }

    private void StandAt(Point3D p)
    {
        if (View.Camera is not ProjectionCamera cam) return;
        _camStand = p;
        // level, toward the middle of the model (else the way it looked)
        var look = cam.LookDirection;
        if (_doc is { Bodies.Count: > 0 } d)
        {
            var vs = d.Bodies.SelectMany(b => b.Vertices).Select(P).ToList();
            if (vs.Count > 0)
            {
                var mid = new Point3D((vs.Min(v => v.X) + vs.Max(v => v.X)) / 2, (vs.Min(v => v.Y) + vs.Max(v => v.Y)) / 2, 0);
                var to = mid - p; to.Z = 0;
                if (to.Length > Math.Max(_modelSize, 10) / 20) look = to;
            }
        }
        look.Z = 0;
        if (look.Length < 1e-9) look = new Vector3D(0, 1, 0);
        look.Normalize();
        cam.Position = p + new Vector3D(0, 0, _eyeHeight);
        cam.LookDirection = look * Math.Max(_modelSize, 10) / 2;
        cam.UpDirection = new Vector3D(0, 0, 1);
        MeasureLabel.Text = T("Ooghoogte", "Eye height"); MeasureBox.Text = _eyeHeight.ToString("0.#", CultureInfo.CurrentCulture);
        StatusText.Text = T($"📷 Je staat hier, ogen op {_eyeHeight.ToString("0.#", CultureInfo.CurrentCulture)} mm. Typ een andere ooghoogte + Enter, of kies 👁 om rond te kijken of 🚶 om te lopen.",
            $"📷 You stand here, eyes at {_eyeHeight.ToString("0.#", CultureInfo.CurrentCulture)} mm. Type another eye height + Enter, or pick 👁 to look around or 🚶 to walk.");
        Log(string.Create(CultureInfo.InvariantCulture, $"camera placed at ({p.X:0.###},{p.Y:0.###},{p.Z:0.###}) eye {_eyeHeight:0.###}"));
    }

    /// <summary>Walking: a step forward or back (level), a turn, up or down.</summary>
    private bool WalkKey(Key key)
    {
        if (View.Camera is not ProjectionCamera cam) return false;
        double step = Math.Max(_modelSize, 10) / 40;
        var flat = new Vector3D(cam.LookDirection.X, cam.LookDirection.Y, 0);
        if (flat.Length < 1e-9) flat = new Vector3D(0, 1, 0);
        flat.Normalize();
        switch (key)
        {
            case Key.Up: cam.Position += flat * step; break;
            case Key.Down: cam.Position -= flat * step; break;
            case Key.PageUp: cam.Position += new Vector3D(0, 0, step); break;
            case Key.PageDown: cam.Position -= new Vector3D(0, 0, step); break;
            case Key.Left or Key.Right:
                cam.LookDirection = CameraPose.Rotate(cam.LookDirection, new Vec3(0, 0, 1), key == Key.Left ? 5 : -5);
                break;
            default: return false;
        }
        cam.UpDirection = new Vector3D(0, 0, 1);
        return true;
    }

    // ------------------------------------------------------------------ protractor (as in SketchUp): a guide line at an angle

    /// <summary>The protractor: stage 1 (centre placed: the base line next), 2 (the angle); the base direction and the
    /// angle now (radians, counter-clockwise round the face's normal). Its centre and plane are kept in the rotate
    /// tool's fields, whose protractor it shows.</summary>
    private int _prStage;
    private Vec3 _prRef;
    private double _prAngle;

    private void EndProtractor()
    {
        if (_prStage == 0) return;
        _prStage = 0;
        ShowSketch([]);
    }

    private void ProtractorClick(Point pos)
    {
        if (_doc == null) return;
        if (_prStage == 0)
        {
            if (FaceAt(pos) is not { } h) return;
            if (!IsPlanar(h.Face)) { StatusText.Text = T("∡ Zet de gradenboog op een plat vlak.", "∡ Put the protractor on a flat face."); return; }
            var p = V(h.Point);
            _rotCentre = EdgeSnap(h.Face, p, pos) is { } s ? s.Point : p;
            _rotAxis = NormalOf(h.Face);
            _prStage = 1; _typed = "";
            ShowProtractor(null, null);
            StatusText.Text = T("∡ Klik een punt voor de beginlijn (0°). Esc breekt af.", "∡ Click a point for the base line (0°). Esc cancels.");
            return;
        }
        if (_prStage == 1)
        {
            if (OnRotatePlane(pos) is not { } q) return;
            var v = q - _rotCentre; v -= _rotAxis * v.Dot(_rotAxis);
            if (v.Length < 1e-9) return;
            _prRef = v.Normalized(); _prStage = 2; _prAngle = 0;
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = "";
            StatusText.Text = T("∡ Beweeg voor de hoek (stappen van 15°) en klik, of typ de hoek + Enter. Esc breekt af.", "∡ Move for the angle (steps of 15°) and click, or type the angle + Enter. Esc cancels.");
            return;
        }
        ProtractorTo(pos);
        AddProtractorGuide(_prAngle);
    }

    private void ProtractorTo(Point pos)
    {
        if (_prStage == 0 || _typed.Length > 0 || OnRotatePlane(pos) is not { } q) return;
        var v = q - _rotCentre; v -= _rotAxis * v.Dot(_rotAxis);
        if (v.Length < 1e-9) return;
        v = v.Normalized();
        if (_prStage == 1) { ShowProtractor(v, null); return; }
        double deg = Math.Atan2(_prRef.Cross(v).Dot(_rotAxis), _prRef.Dot(v)) * 180 / Math.PI;
        double snapped = Math.Round(deg / 15) * 15;
        deg = Math.Abs(deg - snapped) < 2.5 ? snapped : Math.Round(deg * 2) / 2;
        _prAngle = deg * Math.PI / 180;
        MeasureBox.Text = deg.ToString("0.#", CultureInfo.CurrentCulture);
        ShowProtractor(_prRef, new BrepTransform.Turn(new Vec3(0, 0, 0), _rotAxis, _prAngle).Dir(_prRef));
        StatusText.Text = T($"∡ {deg.ToString("0.#", CultureInfo.CurrentCulture)}° · klik voor de hulplijn, of typ de hoek + Enter.", $"∡ {deg.ToString("0.#", CultureInfo.CurrentCulture)}° · click for the guide line, or type the angle + Enter.");
    }

    private void AddProtractorGuide(double angle)
    {
        var dir = new BrepTransform.Turn(new Vec3(0, 0, 0), _rotAxis, angle).Dir(_prRef).Normalized();
        _guides.Add(new Guide(_rotCentre, dir));
        RenderGuides();
        EndProtractor();
        double deg = angle * 180 / Math.PI;
        StatusText.Text = T($"✅ Hulplijn onder {deg.ToString("0.#", CultureInfo.CurrentCulture)}°. Klik een nieuw middelpunt voor de volgende.", $"✅ Guide line at {deg.ToString("0.#", CultureInfo.CurrentCulture)}°. Click a new centre for the next one.");
        Log(string.Create(CultureInfo.InvariantCulture, $"protractor guide at {deg:0.#}° through ({_rotCentre.X * Display:0.###},{_rotCentre.Y * Display:0.###},{_rotCentre.Z * Display:0.###})"));
    }

    // ------------------------------------------------------------------ notes: text labels and dimensions (only in the view)

    /// <summary>A text label at a point; a dimension between two points, its line set off by an offset.</summary>
    private sealed record Note(Vec3 At, string Text);
    private sealed record Dimension(Vec3 A, Vec3 B, Vec3 Off);

    private readonly List<Note> _notes = new();
    private readonly List<Dimension> _dims = new();
    private LineSet? _dimLines;

    /// <summary>A dimension being placed: stage 1 (the first point set), 2 (both: the offset now).</summary>
    private int _dimStage;
    private Vec3 _dimA, _dimB, _dimOff;

    /// <summary>Where the label being typed goes (or, for 3D text, on which face).</summary>
    private Vec3 _noteAt;
    private FaceRef? _noteFace3D;

    /// <summary>The labels and the dimensions' texts where their points are on screen now; the dimension lines.</summary>
    private void PlaceNotes()
    {
        _labels.Clear();
        if (_dimLines != null) { View.Children.Remove(_dimLines); _dimLines = null; }
        RefreshStructureCounts();
        foreach (var n in SketchVisible(SketchKind.Notes) ? _notes : []) _labels.Add(new ViewLabel(LabelKind.Note, n.Text, Screen2(n.At)));
        var pts = new Point3DCollection();
        void Dim(Vec3 a, Vec3 b, Vec3 off, bool final)
        {
            // the extension lines, the dimension line and small ticks at its ends
            foreach (var (p, q) in new[] { (a, a + off * 1.08), (b, b + off * 1.08), (a + off, b + off) }) { pts.Add(TowardEye(P(p), 2e-3)); pts.Add(TowardEye(P(q), 2e-3)); }
            var along = (b - a).Normalized(); var tick = (along + off.Normalized()) * ((b - a).Length * 0.03);
            foreach (var e in new[] { a + off, b + off }) { pts.Add(TowardEye(P(e - tick), 2e-3)); pts.Add(TowardEye(P(e + tick), 2e-3)); }
            _labels.Add(new ViewLabel(LabelKind.Dimension, Mm((b - a).Length) + " mm", Screen2((a + b) * 0.5 + off), final ? 1 : 0.8));
        }
        foreach (var d in SketchVisible(SketchKind.Dimensions) ? _dims : []) Dim(d.A, d.B, d.Off, true);
        if (_dimStage == 2) Dim(_dimA, _dimB, _dimOff, false);
        if (pts.Count > 0) View.Children.Add(_dimLines = new LineSet { Points = pts, Color = EdgeColor, Thickness = 1.3 });
        _window.LabelsChanged();
    }

    private void EndDimension()
    {
        if (_dimStage == 0) return;
        _dimStage = 0;
        ShowSketch([]);
        PlaceNotes();
    }

    /// <summary>A point for a dimension: a corner or an edge close by, else where the mouse hits the model.</summary>
    private Vec3? DimPoint(Point pos)
    {
        var hit = FaceAt(pos);
        if (hit is { } h && IsPlanar(h.Face) && EdgeSnap(h.Face, V(h.Point), pos) is { } s) return s.Point;
        return TapePoint(hit, pos)?.Point;
    }

    private void DimensionClick(Point pos)
    {
        if (_doc == null) return;
        if (_dimStage == 0)
        {
            // a dimension's text clicked: away with it
            int near = _dims.FindIndex(d => (Screen2((d.A + d.B) * 0.5 + d.Off) - pos).Length < 14);
            if (near >= 0) { _dims.RemoveAt(near); PlaceNotes(); StatusText.Text = T("↔ Maat weggehaald.", "↔ Dimension removed."); return; }
            if (DimPoint(pos) is not { } a) return;
            _dimA = a; _dimStage = 1;
            ShowSketch([a]);
            StatusText.Text = T("↔ Klik het tweede punt. Esc breekt af.", "↔ Click the second point. Esc cancels.");
            return;
        }
        if (_dimStage == 1)
        {
            if (DimPoint(pos) is not { } b || (b - _dimA).Length < 1e-7) return;
            _dimB = b; _dimStage = 2; _dimOff = default;
            StatusText.Text = T($"↔ {Mm((b - _dimA).Length)} mm · beweeg om de maatlijn ervan af te zetten en klik.", $"↔ {Mm((b - _dimA).Length)} mm · move to set the dimension line off and click.");
            return;
        }
        DimensionTo(pos);
        _dims.Add(new Dimension(_dimA, _dimB, _dimOff));
        Log(string.Create(CultureInfo.InvariantCulture, $"dimension {(_dimB - _dimA).Length * Display:0.###} mm"));
        StatusText.Text = T($"✅ Maat {Mm((_dimB - _dimA).Length)} mm. Klik het eerste punt van de volgende.", $"✅ Dimension {Mm((_dimB - _dimA).Length)} mm. Click the first point of the next one.");
        EndDimension();
    }

    private void DimensionTo(Point pos)
    {
        if (_dimStage == 1) { if (DimPoint(pos) is { } b) ShowSketch([_dimA, b]); return; }
        if (_dimStage != 2 || View.Camera is not ProjectionCamera cam) return;
        // the offset: along an axis square to A–B (as SketchUp), in the plane through A–B that faces the camera most;
        // as far as the mouse is on that plane
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return;
        var look = new Vec3(cam.LookDirection.X, cam.LookDirection.Y, cam.LookDirection.Z).Normalized();
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        var ab = (_dimB - _dimA).Normalized();
        // (the axis whose direction on screen lies most along the way the mouse is from the line A–B)
        Point S(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var mid = (_dimA + _dimB) * 0.5; var sm = S(mid);
        var sab = S(_dimB) - S(_dimA); if (sab.Length > 1e-9) sab.Normalize();
        var m = pos - sm; m -= sab * Vector.Multiply(m, sab);
        var cands = Axes.Select(x => x.Dir - ab * x.Dir.Dot(ab)).Where(u => u.Length > 0.3).Select(u => u.Normalized()).ToList();
        var best = m.Length < 3 ? cands.MaxBy(u => Math.Abs(ab.Cross(u).Normalized().Dot(look)))
            : cands.MaxBy(u => { var su = S(mid + u * (_dimB - _dimA).Length) - sm; return su.Length < 1e-9 ? 0 : Math.Abs(Vector.Multiply(su, m)) / su.Length; });
        var n = ab.Cross(best).Normalized();
        double den = dir.Dot(n);
        if (Math.Abs(den) < 1e-9) return;
        var q = org + dir * ((_dimA - org).Dot(n) / den);
        _dimOff = best * (q - _dimA).Dot(best);
        ShowSketch([]);
        PlaceNotes();
    }

    private void TextClick(Point pos)
    {
        if (_doc == null) return;
        var here = pos;
        int near = _notes.FindIndex(n => { var a = Screen2(n.At); return (new Point(a.X + 40, a.Y - 30) - here).Length < 30 || (a - here).Length < 10; });
        if (near >= 0) { _notes.RemoveAt(near); PlaceNotes(); StatusText.Text = T("💬 Label weggehaald.", "💬 Label removed."); return; }
        var hit = FaceAt(pos);
        if (TapePoint(hit, pos) is not { } p) return;
        EndNoteBox(null);
        _noteAt = p.Point;
        var at = Screen2(_noteAt);
        NoteBox = new Point(at.X + 28, at.Y - 40);
        _window.LabelsChanged();
        StatusText.Text = T("💬 Typ de tekst en druk op Enter (Esc: laat maar).", "💬 Type the text and press Enter (Esc: never mind).");
    }

    /// <summary>The label box closed: its text a label (not empty), or nothing (null: Esc, or another tool).</summary>
    public void EndNoteBox(string? typed)
    {
        if (NoteBox == null) return;
        NoteBox = null;
        _window.LabelsChanged();
        if (_noteFace3D is { } f3)
        {
            _noteFace3D = null;
            if (typed?.Trim() is { Length: > 0 } t3) Make3DText(f3, _noteAt, t3);
            return;
        }
        if (typed?.Trim() is { Length: > 0 } text)
        {
            _notes.Add(new Note(_noteAt, text));
            StatusText.Text = T("✅ Label geplaatst. Klik een punt voor het volgende.", "✅ Label placed. Click a point for the next one.");
            Log($"text label \"{text}\"");
        }
        PlaceNotes();
    }

    /// <summary>For the test aid: a label typed.</summary>
    private void TypeNote(string text)
    {
        EndNoteBox(text);
    }

    // ------------------------------------------------------------------ 3D text (as SketchUp's): letters as faces

    /// <summary>The letter height (metres; 0: a fifth of the face), and the letter faces just made (to pull out).</summary>
    private double _letterHeight;
    private List<FaceRef>? _letters;

    private void Text3DClick(Point pos)
    {
        if (_doc == null || FaceAt(pos) is not { } h) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("A 3D-tekst kan alleen op een plat vlak.", "A 3D text only works on a flat face."); return; }
        var p = V(h.Point);
        TextClickBox(EdgeSnap(h.Face, p, pos) is { } s ? s.Point : p, h.Face);
    }

    private void TextClickBox(Vec3 at, FaceRef f)
    {
        EndNoteBox(null);
        _noteAt = at; _noteFace3D = f; _letters = null;
        var sp = Screen2(at);
        NoteBox = new Point(sp.X + 10, sp.Y - 34);
        _window.LabelsChanged();
        StatusText.Text = T("A Typ de tekst en druk op Enter (Esc: laat maar).", "A Type the text and press Enter (Esc: never mind).");
    }

    /// <summary>The outlines of the text's letters (font units scaled to <paramref name="height"/>, y up), each letter
    /// part's outer outline with the holes in it.</summary>
    private List<(List<(double X, double Y)> Outer, List<List<(double X, double Y)>> Holes)> LetterOutlines(string text, double height)
    {
        // (the window's font, as loops a capital high; the height asked is that of a capital)
        var raw = _window.TextOutlines(text) ?? [];
        double Area(List<(double X, double Y)> l) { double a = 0; for (int i = 0; i < l.Count; i++) { var (x0, y0) = l[i]; var (x1, y1) = l[(i + 1) % l.Count]; a += x0 * y1 - x1 * y0; } return a / 2; }
        bool In(List<(double X, double Y)> l, (double X, double Y) q)
        {
            bool inside = false;
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                if ((l[i].Y > q.Y) != (l[j].Y > q.Y) && q.X < (l[j].X - l[i].X) * (q.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) inside = !inside;
            return inside;
        }
        var loops = new List<List<(double X, double Y)>>();
        foreach (var fig in raw)
        {
            var clean = new List<(double X, double Y)>();
            foreach (var (x, y) in fig) { var q = (X: x * height, Y: y * height); if (clean.Count == 0 || Math.Abs(clean[^1].X - q.X) + Math.Abs(clean[^1].Y - q.Y) > 1e-9) clean.Add(q); }
            if (clean.Count > 1 && Math.Abs(clean[0].X - clean[^1].X) + Math.Abs(clean[0].Y - clean[^1].Y) < 1e-9) clean.RemoveAt(clean.Count - 1);
            if (clean.Count >= 3 && Math.Abs(Area(clean)) > 1e-12) loops.Add(clean);
        }
        // an outline inside an odd number of others is a hole (of the smallest one round it); the rest are letter parts.
        // (Each turned counter-clockwise, holes clockwise, whichever way the font runs them.)
        List<(double X, double Y)> Turned(List<(double X, double Y)> l, bool ccw) => (Area(l) > 0) == ccw ? l : Enumerable.Reverse(l).ToList();
        int Depth(List<(double X, double Y)> l) => loops.Count(o => !ReferenceEquals(o, l) && Math.Abs(Area(o)) > Math.Abs(Area(l)) && In(o, l[0]));
        var outers = loops.Where(l => Depth(l) % 2 == 0).OrderBy(l => Math.Abs(Area(l)))
            .Select(l => (Outer: Turned(l, true), Holes: new List<List<(double X, double Y)>>(), Raw: l)).ToList();
        foreach (var hole in loops.Where(l => Depth(l) % 2 == 1))
            if (outers.FirstOrDefault(o => Math.Abs(Area(o.Raw)) > Math.Abs(Area(hole)) && In(o.Raw, hole[0])) is { Outer: not null } o) o.Holes.Add(Turned(hole, false));
        return outers.Select(o => (o.Outer, o.Holes)).ToList();
    }

    private void Make3DText(FaceRef f, Vec3 at, string text)
    {
        if (_doc is not { } before) return;
        var n = NormalOf(f); var x = RectAxis(n); var y = n.Cross(x);
        double height = _letterHeight > 0 ? _letterHeight : Math.Max(FaceSize(before, f) / 5, 1e-3);
        double tol = height / 300;
        var doc = before; var letters = new List<FaceRef>(); int failed = 0;
        var outlines = LetterOutlines(text, height);
        Log(string.Create(CultureInfo.InvariantCulture, $"3D text outlines: {outlines.Count} parts, {outlines.Sum(o => o.Holes.Count)} holes, x {outlines.Min(o => o.Outer.Min(q => q.X)) * Display:0.##}..{outlines.Max(o => o.Outer.Max(q => q.X)) * Display:0.##} y {outlines.Min(o => o.Outer.Min(q => q.Y)) * Display:0.##}..{outlines.Max(o => o.Outer.Max(q => q.Y)) * Display:0.##} mm, points {outlines.Sum(o => o.Outer.Count)}"));
        foreach (var (outer, holes) in outlines)
        {
            Vec3 P3((double X, double Y) q) => at + x * q.X + y * q.Y;
            var o = PushPullMe.Core.Drawing.Simplify(outer.Append(outer[0]).Select(P3).ToList(), tol);
            try
            {
                var (d1, letter) = doc.DrawPath(f, o.Take(o.Count - 1).Append(o[0]).ToList());
                foreach (var h in holes)
                {
                    var hp = PushPullMe.Core.Drawing.Simplify(h.Append(h[0]).Select(P3).ToList(), tol);
                    (d1, _) = d1.DrawPath(letter, hp.Take(hp.Count - 1).Append(hp[0]).ToList());
                }
                doc = d1; letters.Add(letter);
            }
            catch (PushPullException) { failed++; }
        }
        if (letters.Count == 0)
        {
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), T("De tekst past niet op dit vlak (of raakt een rand).", "The text does not fit on this face (or touches an edge)."));
            return;
        }
        Commit(before, doc);
        _last = null; _selected = null; _letters = letters;
        Render();
        MeasureLabel.Text = T("Dikte", "Thickness"); MeasureBox.Text = "";
        StatusText.Text = T($"✅ Tekst als {letters.Count} vlakken{(failed > 0 ? $" ({failed} letterdelen pasten niet)" : "")}. Typ een dikte (mm) + Enter om ze eruit te trekken (min: erin).",
            $"✅ Text as {letters.Count} faces{(failed > 0 ? $" ({failed} letter parts did not fit)" : "")}. Type a thickness (mm) + Enter to pull them out (minus: in).");
        Log($"3D text \"{text}\" on face {f.Face}: {letters.Count} faces, {failed} failed");
    }

    /// <summary>Roughly how big a face is (the longest side of the box round it).</summary>
    private static double FaceSize(SketchDocument doc, FaceRef f)
    {
        var pts = doc.TrianglesOf(f).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
        if (pts.Count == 0) return 0.01;
        return Math.Max(pts.Max(p => p.X) - pts.Min(p => p.X), Math.Max(pts.Max(p => p.Y) - pts.Min(p => p.Y), pts.Max(p => p.Z) - pts.Min(p => p.Z)));
    }

    /// <summary>The letters just made, all pulled out (or pushed in) by the same distance.</summary>
    private void ExtrudeLetters(double distance)
    {
        if (_doc is not { } before || _letters is not { } letters) return;
        var doc = before; int done = 0;
        var faces = letters.ToList();
        for (int i = 0; i < faces.Count; i++)
        {
            try
            {
                var (d, r) = doc.PushPull(faces[i], distance, join: Join);
                doc = d; done++;
                // (the other letters under their numbers now)
                for (int j = i + 1; j < faces.Count; j++)
                    if (faces[j].Body == faces[i].Body && faces[j].Face < r.FaceMap.Count) faces[j] = new FaceRef(faces[j].Body, r.FaceMap[faces[j].Face]);
            }
            catch (PushPullException) { }
        }
        if (done == 0) { StatusText.Text = T("⛔ De letters konden niet zo ver.", "⛔ The letters could not go that far."); return; }
        Commit(before, doc);
        _letters = null; _last = null;
        Render();
        StatusText.Text = T($"✅ {done} letterdelen {Mm(Math.Abs(distance))} mm {(distance > 0 ? "eruit getrokken" : "ingeduwd")}.", $"✅ {done} letter parts {(distance > 0 ? "pulled out" : "pushed in")} {Mm(Math.Abs(distance))} mm.");
        Log(string.Create(CultureInfo.InvariantCulture, $"3D text extruded {distance * Display:0.###} mm: {done} of {letters.Count}"));
    }

    // ------------------------------------------------------------------ axes (as SketchUp's): the drawing axes moved and turned

    /// <summary>Setting the axes: stage 1 (origin set: the red direction next), 2 (the green); the new red direction.</summary>
    private int _axStage;
    private Vec3 _axNewOrigin, _axNewRed;
    private readonly List<LineSet> _axesVisual = new();

    private void AxesClick(Point pos)
    {
        var at = DimPoint(pos);
        if (_axStage == 0)
        {
            if (at is not { } o)
            {
                (_axisRed, _axisGreen, _axisBlue, _axisOrigin) = (new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), default);
                RenderAxes();
                StatusText.Text = T("✛ De gewone assen zijn terug.", "✛ The usual axes are back.");
                return;
            }
            _axNewOrigin = o; _axStage = 1;
            ShowSketch([o]);
            StatusText.Text = T("✛ Klik een punt in de richting van de rode as. Esc breekt af.", "✛ Click a point in the direction of the red axis. Esc cancels.");
            return;
        }
        if (at is not { } p || (p - _axNewOrigin).Length < 1e-7) return;
        if (_axStage == 1)
        {
            _axNewRed = (p - _axNewOrigin).Normalized(); _axStage = 2;
            ShowSketch([_axNewOrigin, p], default, color: AxisX);
            StatusText.Text = T("✛ Klik een punt aan de kant van de groene as. Esc breekt af.", "✛ Click a point on the side of the green axis. Esc cancels.");
            return;
        }
        var g = p - _axNewOrigin; g -= _axNewRed * g.Dot(_axNewRed);
        if (g.Length < 1e-7) return;
        _axisRed = _axNewRed; _axisGreen = g.Normalized(); _axisBlue = _axisRed.Cross(_axisGreen).Normalized(); _axisOrigin = _axNewOrigin;
        _axStage = 0;
        ShowSketch([]);
        RenderAxes();
        StatusText.Text = T("✅ Nieuwe assen. Vastklikken, rechthoeken en de pijltjes volgen ze; klik met ✛ op een lege plek voor de gewone assen.", "✅ New axes. Snapping, rectangles and the arrow keys follow them; click an empty spot with ✛ for the usual axes.");
        Log(string.Create(CultureInfo.InvariantCulture, $"axes: red ({_axisRed.X:0.###},{_axisRed.Y:0.###},{_axisRed.Z:0.###}) green ({_axisGreen.X:0.###},{_axisGreen.Y:0.###},{_axisGreen.Z:0.###})"));
    }

    /// <summary>The axes drawn (only when they are not the usual ones): coloured lines through their origin.</summary>
    private void RenderAxes()
    {
        foreach (var v in _axesVisual) View.Children.Remove(v);
        _axesVisual.Clear();
        if ((_axisRed - new Vec3(1, 0, 0)).Length < 1e-12 && (_axisGreen - new Vec3(0, 1, 0)).Length < 1e-12 && _axisOrigin.Length < 1e-12) return;
        double len = Math.Max(_modelSize, 10) * 0.6 / Display;
        foreach (var (dir, _, color) in Axes)
        {
            var v = new LineSet { Points = new Point3DCollection { P(_axisOrigin), P(_axisOrigin + dir * len) }, Color = color, Thickness = 2 };
            View.Children.Add(v); _axesVisual.Add(v);
        }
    }

    // ------------------------------------------------------------------ hiding parts (a simple Tag)

    /// <summary>Bodies not shown (as they are: one that changes is shown again).</summary>
    private readonly HashSet<BrepBody> _hidden = new(ReferenceEqualityComparer.Instance);

    private void HideAt((FaceRef Face, Point3D Point)? hit)
    {
        if (_doc == null) return;
        if (hit is not { } h)
        {
            int n = _hidden.Count;
            _hidden.Clear();
            Render(); RefreshStructure();
            StatusText.Text = n == 0 ? T("🏷 Er was niets verborgen.", "🏷 Nothing was hidden.") : T($"🏷 Alles weer zichtbaar ({n} {(n == 1 ? "deel" : "delen")}).", $"🏷 Everything shown again ({n} {(n == 1 ? "part" : "parts")}).");
            return;
        }
        _hidden.Add(_doc.Bodies[h.Face.Body]);
        _selected = null; _hover = null;
        Render(); RefreshStructure();
        StatusText.Text = T($"🏷 Verborgen ({_hidden.Count} in totaal). Klik op een lege plek om alles weer te tonen.", $"🏷 Hidden ({_hidden.Count} in all). Click an empty spot to show everything again.");
        Log($"hide body {h.Face.Body}");
    }

    // ------------------------------------------------------------------ section plane (shown only): the model cut open

    /// <summary>The section: a plane through P; what lies on the side its normal N points to is left out.</summary>
    private (Vec3 P, Vec3 N)? _section;
    private LineSet? _sectionCut;

    /// <summary>Placing a section: on this face, from this point (scene) along its normal, the depth now (metres).</summary>
    private FaceRef? _secFace;
    private Vec3 _secOrigin, _secNormal;
    private double _secDist;

    private void StartSection((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("⊟ Zet het snijvlak op een plat vlak.", "⊟ Put the cutting plane on a flat face."); return; }
        _secFace = h.Face; _secOrigin = V(h.Point); _secNormal = NormalOf(h.Face); _secDist = 0; _typed = "";
        // (for DistanceAt: along the face's normal from the point clicked)
        _dragStart = h.Point; _dragNormal = new Vector3D(_secNormal.X, _secNormal.Y, _secNormal.Z);
        MeasureLabel.Text = T("Diepte", "Depth"); MeasureBox.Text = "0";
        StatusText.Text = T("⊟ Beweeg om het snijvlak het model in te schuiven; klik, of typ de diepte + Enter. Esc breekt af.", "⊟ Move to slide the cutting plane into the model; click, or type the depth + Enter. Esc cancels.");
    }

    private void EndSection()
    {
        _secFace = null;
    }

    private void SectionTo(Point pos)
    {
        if (_secFace == null || _typed.Length > 0 || DistanceAt(pos) is not double m) return;
        // into the model: against the normal, in steps of 0.1 mm
        double depth = Math.Max(0, Math.Round(-m * 10) / 10) / Display;
        _secDist = depth;
        _section = (_secOrigin - _secNormal * depth, _secNormal);
        MeasureBox.Text = Mm(depth);
        Render();
    }

    private void SetSection(double depth)
    {
        if (_secFace == null) return;
        _section = (_secOrigin - _secNormal * depth, _secNormal);
        EndSection();
        Render();
        StatusText.Text = T($"✅ Doorsnede op {Mm(depth)} mm diep. Klik een ander vlak voor een nieuwe, of Esc om hem weg te halen.", $"✅ Section {Mm(depth)} mm deep. Click another face for a new one, or Esc to remove it.");
        Log(string.Create(CultureInfo.InvariantCulture, $"section at depth {depth * Display:0.###} mm"));
    }

    /// <summary>The triangles as far as they lie behind the section (cut at it), or all of them.</summary>
    private IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> Clipped(IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> tris)
    {
        if (_section is not { } s) { foreach (var t in tris) yield return t; yield break; }
        foreach (var (a, b, c) in tris)
        {
            var poly = new List<Vec3>();
            var pts = new[] { a, b, c };
            for (int i = 0; i < 3; i++)
            {
                Vec3 p = pts[i], q = pts[(i + 1) % 3];
                double dp = (p - s.P).Dot(s.N), dq = (q - s.P).Dot(s.N);
                if (dp <= 0) poly.Add(p);
                if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0)) poly.Add(p + (q - p) * (dp / (dp - dq)));
            }
            for (int i = 1; i + 1 < poly.Count; i++) yield return (poly[0], poly[i], poly[i + 1]);
        }
    }

    /// <summary>The part of a segment behind the section (or all of it), or null.</summary>
    private (Vec3 A, Vec3 B)? ClippedSegment(Vec3 a, Vec3 b)
    {
        if (_section is not { } s) return (a, b);
        double da = (a - s.P).Dot(s.N), db = (b - s.P).Dot(s.N);
        if (da > 0 && db > 0) return null;
        if (da <= 0 && db <= 0) return (a, b);
        var x = a + (b - a) * (da / (da - db));
        return da <= 0 ? (a, x) : (x, b);
    }

    /// <summary>Where the section cuts the model: thick lines.</summary>
    private void RenderSectionCut(SketchDocument doc)
    {
        if (_sectionCut != null) { View.Children.Remove(_sectionCut); _sectionCut = null; }
        if (_section is not { } s) return;
        var pts = new Point3DCollection();
        for (int b = 0; b < doc.Bodies.Count; b++)
            for (int f = 0; f < doc.Bodies[b].Faces.Count; f++)
                foreach (var (A, B, C) in doc.TrianglesOf(new FaceRef(b, f)))
                {
                    var tp = new[] { A, B, C }; var cut = new List<Vec3>();
                    for (int i = 0; i < 3; i++)
                    {
                        Vec3 p = tp[i], q = tp[(i + 1) % 3];
                        double dp = (p - s.P).Dot(s.N), dq = (q - s.P).Dot(s.N);
                        if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0)) cut.Add(p + (q - p) * (dp / (dp - dq)));
                    }
                    if (cut.Count == 2) { pts.Add(TowardEye(P(cut[0]), 2e-3)); pts.Add(TowardEye(P(cut[1]), 2e-3)); }
                }
        if (pts.Count == 0) return;
        _sectionCut = new LineSet { Points = pts, Color = EdgeColor, Thickness = 3.5 };
        View.Children.Add(_sectionCut);
    }

    // ------------------------------------------------------------------ freehand (as in SketchUp)

    /// <summary>A freehand track being drawn: on this face's plane, its points so far.</summary>
    private FaceRef? _fhFace;
    private readonly List<Vec3> _fhPts = new();

    private void StartFreehand((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("✎ Uit de vrije hand tekenen kan alleen op een plat vlak.", "✎ Freehand drawing only works on a flat face."); return; }
        _fhFace = h.Face; _fhPts.Clear();
        _fhPts.Add(EdgeSnap(h.Face, V(h.Point), pos) is { } s ? s.Point : V(h.Point));
        _selected = null; _hover = null; UpdateHighlight();
        StatusText.Text = T("✎ Beweeg om te tekenen; klik om te eindigen (terug bij het begin: een vlak). Esc breekt af.", "✎ Move to draw; click to finish (back at the start: a face). Esc cancels.");
    }

    private void EndFreehand()
    {
        if (_fhFace == null) return;
        _fhFace = null; _fhPts.Clear();
        ShowSketch([]);
    }

    /// <summary>The track follows the mouse: a point added once it is a few pixels from the last.</summary>
    private void FreehandTo(Point pos)
    {
        if (_fhFace is not { } f || OnPlane(f, pos) is not { } p) return;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        if ((Screen(_fhPts[^1]) - pos).Length >= 4) _fhPts.Add(p);
        bool back = _fhPts.Count >= 4 && (Screen(_fhPts[0]) - pos).Length < 12;
        ShowSketch(back ? _fhPts.Append(_fhPts[0]).ToList() : _fhPts.Append(p).ToList(), NormalOf(f));
        StatusText.Text = back ? T("✎ Terug bij het begin: klik voor een vlak.", "✎ Back at the start: click for a face.")
            : T("✎ Beweeg om te tekenen; klik om te eindigen (terug bij het begin: een vlak). Esc breekt af.", "✎ Move to draw; click to finish (back at the start: a face). Esc cancels.");
    }

    private void FinishFreehand(Point pos)
    {
        if (_fhFace is not { } f || _doc is not { } before) return;
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var pts = _fhPts.ToList();
        // (the end on an edge or corner close by, as the start)
        if (OnPlane(f, pos) is { } end && EdgeSnap(f, end, pos) is { } es) pts.Add(es.Point);
        EndFreehand();
        // fewer points where the track is nearly straight (within a pixel and a half)
        double pxPerM = (Screen(pts[0] + RectAxis(NormalOf(f)) * 0.001) - Screen(pts[0])).Length * 1000;
        pts = PushPullMe.Core.Drawing.Simplify(pts, pxPerM > 0 ? 1.5 / pxPerM : 1e-4);
        if (pts.Count < 2) return;
        bool closed = pts.Count >= 4 && (Screen(pts[^1]) - Screen(pts[0])).Length < 12;
        try
        {
            if (closed)
            {
                pts[^1] = pts[0];
                try
                {
                    var (doc, nf) = before.DrawPath(f, pts);
                    Commit(before, doc);
                    _last = null; _selected = nf; _lastDrawn = nf;
                    Render();
                    StatusText.Text = T($"✅ Vlak getekend uit de vrije hand ({pts.Count - 1} hoeken). Kies ⇕ (P) om het eruit te trekken of in te duwen.", $"✅ Freehand face drawn ({pts.Count - 1} corners). Pick ⇕ (P) to pull it out or push it in.");
                    Log($"freehand on face {f.Face}: closed, {pts.Count - 1} corners, new face {nf.Face}");
                    return;
                }
                catch (PushPullException) { }
            }
            // loose lines (they may close a face with what is there)
            var lines = Enumerable.Range(0, pts.Count - 1).Select(i => new SketchLine(pts[i], pts[i + 1])).ToList();
            var (d2, made) = before.AddLines(lines).CloseLooseLines(f);
            Commit(before, d2);
            _last = null;
            if (made.Count > 0) { _selected = made[^1]; _lastDrawn = made[^1]; }
            Render();
            StatusText.Text = made.Count > 0
                ? T($"✅ Uit de vrije hand getekend; {(made.Count == 1 ? "een vlak" : $"{made.Count} vlakken")} gesloten.", $"✅ Drawn freehand; {(made.Count == 1 ? "a face" : $"{made.Count} faces")} closed.")
                : T($"✅ Uit de vrije hand getekend: {lines.Count} losse lijnen.", $"✅ Drawn freehand: {lines.Count} loose lines.");
            Log($"freehand on face {f.Face}: {lines.Count} loose lines, {made.Count} face(s) closed");
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ follow me: a face turned out round an axis

    /// <summary>Turning a face out: the face (stage 1: picking the axis, 2: the angle), the axis (a point and the
    /// direction that turns the face out), the angle now, the document it started from.</summary>
    private FaceRef? _fmFace;
    private int _fmStage;
    private Vec3 _fmAxisP, _fmAxisDir, _fmRef;
    private double _fmAngle;
    private bool _fmOk;
    private SketchDocument? _fmBase;

    private void StartFollow((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("↝ Ronddraaien kan alleen met een plat vlak.", "↝ Follow Me only works with a flat face."); return; }
        _fmFace = h.Face; _fmStage = 1; _fmBase = _doc; _typed = "";
        _selected = h.Face; _hover = null; UpdateHighlight();
        StatusText.Text = T("↝ Klik de as (een rechte rand, losse lijn of hulplijn in het vlak van dit vlak), of een vlak waarvan de rand het pad is. Esc breekt af.", "↝ Click the axis (a straight edge, loose line or guide line in the plane of this face), or a face whose edge is the path. Esc cancels.");
    }

    private void EndFollow()
    {
        if (_fmFace == null) return;
        _fmFace = null; _fmStage = 0; _fmBase = null; _fmOk = false;
        ShowSketch([]);
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
    }

    /// <summary>A straight line in the face's plane near the mouse (within 8 pixels): an edge of a body, a loose line or a
    /// guide line; its two points (a guide: two points on it).</summary>
    private (Vec3 A, Vec3 B)? AxisAt(FaceRef f, Point pos)
    {
        if (_doc == null) return null;
        var face = _doc.Bodies[f.Body].Faces[f.Face];
        var n = FaceGeometry.Normal(face); var o = ((BrepPlane)face.Surface).Origin;
        bool In(Vec3 q) => Math.Abs((q - o).Dot(n)) < 1e-7;
        var mat = Viewport3DHelper.GetTotalTransform(View.Viewport);
        Point Screen(Vec3 v) { var q = mat.Transform(P(v)); return new Point(q.X, q.Y); }
        var cands = new List<(Vec3, Vec3)>();
        foreach (var b in _doc.Bodies)
            foreach (var e in b.Edges.Where(e => e.Curve is BrepLine && e.Start >= 0))
                if (In(b.Vertices[e.Start]) && In(b.Vertices[e.End])) cands.Add((b.Vertices[e.Start], b.Vertices[e.End]));
        foreach (var l in _doc.Lines.Where(l => !l.IsArc && In(l.Start) && In(l.End))) cands.Add((l.Start, l.End));
        double len = Math.Max(_modelSize, 10) * 1.5 / Display;
        foreach (var g in _guides.Where(g => Guides.InPlane(g, o, n))) cands.Add((g.Point - g.Dir * len, g.Point + g.Dir * len));
        (Vec3, Vec3)? best = null; double bd = 8;
        foreach (var (a, b) in cands)
        {
            Point sa = Screen(a), sb = Screen(b); var ab = sb - sa; double l2 = ab.LengthSquared;
            double t = l2 == 0 ? 0 : Math.Clamp(Vector.Multiply(pos - sa, ab) / l2, 0, 1);
            double d = (sa + ab * t - pos).Length;
            if (d < bd) { bd = d; best = (a, b); }
        }
        return best;
    }

    /// <summary>The middle of the face (the average of its triangles' centres, by area).</summary>
    private Vec3 FaceMiddle(SketchDocument doc, FaceRef f)
    {
        var tris = doc.TrianglesOf(f);
        double w = 0; var sum = new Vec3(0, 0, 0);
        foreach (var (a, b, c) in tris) { double ar = (b - a).Cross(c - a).Length; sum += (a + b + c) * (ar / 3); w += ar; }
        return w > 0 ? sum * (1 / w) : tris[0].A;
    }

    private void FollowTo(Point pos)
    {
        if (_fmFace is not { } f || _fmBase is not { } baseDoc) return;
        if (_fmStage == 1)
        {
            if (AxisAt(f, pos) is { } ax) { ShowSketch([ax.A, ax.B], default, color: AxisZ); StatusText.Text = T("↝ Klik om deze lijn als as te nemen.", "↝ Click to take this line as the axis."); }
            else { ShowSketch([]); StatusText.Text = T("↝ Klik de as (een rechte rand, losse lijn of hulplijn in het vlak van dit vlak), of een vlak waarvan de rand het pad is. Esc breekt af.", "↝ Click the axis (a straight edge, loose line or guide line in the plane of this face), or a face whose edge is the path. Esc cancels."); }
            return;
        }
        if (_typed.Length > 0) return;
        // the angle: where the mouse is on the plane square to the axis through the face's middle
        var mid = FaceMiddle(baseDoc, f);
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        double den = dir.Dot(_fmAxisDir);
        Vec3 q;
        if (Math.Abs(den) > 1e-6) q = org + dir * ((mid - org).Dot(_fmAxisDir) / den);
        else if (OnPlane(f, pos) is { } qp) q = qp; else return;
        var foot = _fmAxisP + _fmAxisDir * (mid - _fmAxisP).Dot(_fmAxisDir);
        var v = q - foot; v -= _fmAxisDir * v.Dot(_fmAxisDir);
        if (v.Length < 1e-9) return;
        v = v.Normalized();
        double deg = Math.Atan2(_fmRef.Cross(v).Dot(_fmAxisDir), _fmRef.Dot(v)) * 180 / Math.PI;
        if (deg < 0) deg += 360;
        double snapped = Math.Round(deg / 15) * 15;
        deg = Math.Abs(deg - snapped) < 2.5 ? snapped : Math.Round(deg * 2) / 2;
        // (close to a whole turn: that)
        if (deg > 352.5) deg = 360;
        if (deg <= 0) { Render(baseDoc); _fmOk = false; return; }
        _fmAngle = deg * Math.PI / 180;
        MeasureBox.Text = deg.ToString("0.#", CultureInfo.CurrentCulture);
        try
        {
            var (doc, _) = baseDoc.Revolve(f, _fmAxisP, _fmAxisDir, _fmAngle);
            _fmOk = true;
            Render(doc);
            StatusText.Text = T($"↝ {deg.ToString("0.#", CultureInfo.CurrentCulture)}°{(Math.Abs(deg - snapped) < 1e-9 ? " · 🎯 15°-stap" : "")} · klik om vast te zetten, of typ de hoek + Enter. Esc breekt af.", $"↝ {deg.ToString("0.#", CultureInfo.CurrentCulture)}°{(Math.Abs(deg - snapped) < 1e-9 ? " · 🎯 15° step" : "")} · click to set it, or type the angle + Enter. Esc cancels.");
        }
        catch (PushPullException ex) { _fmOk = false; Render(baseDoc); StatusText.Text = "⛔ " + ex.Message; }
    }

    private void FollowClick(Point pos)
    {
        if (_fmFace is not { } f || _fmBase is not { } baseDoc) return;
        if (_fmStage == 1)
        {
            if (AxisAt(f, pos) is not { } ax)
            {
                // no axis there: a face, whose edge is the path to sweep the profile along
                if (FaceAt(pos) is { } h && h.Face != f && IsPlanar(h.Face)) { EndFollow(); ApplyFollowPath(baseDoc, f, h.Face); }
                return;
            }
            try
            {
                var a = PushPullMe.Core.Revolve.OutwardAxis(baseDoc.Bodies[f.Body], f.Face, ax.A, ax.B - ax.A);
                _fmAxisP = ax.A; _fmAxisDir = a.Normalized();
                var mid = FaceMiddle(baseDoc, f);
                var r = mid - ax.A; r -= _fmAxisDir * r.Dot(_fmAxisDir);
                _fmRef = r.Length > 1e-9 ? r.Normalized() : NormalOf(f).Cross(_fmAxisDir).Normalized();
                _fmStage = 2; _fmAngle = 0; _fmOk = false;
                ShowSketch([ax.A, ax.B], default, color: AxisZ);
                MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = "";
                StatusText.Text = T("↝ Beweeg om het vlak eruit te draaien (stappen van 15°) en klik, of typ de hoek + Enter. Esc breekt af.", "↝ Move to turn the face out (steps of 15°) and click, or type the angle + Enter. Esc cancels.");
            }
            catch (PushPullException ex) { StatusText.Text = "⛔ " + ex.Message; }
            return;
        }
        FollowTo(pos);
        if (!_fmOk) return;
        var (p, dir, ang) = (_fmAxisP, _fmAxisDir, _fmAngle);
        EndFollow();
        ApplyFollow(baseDoc, f, p, dir, ang);
    }

    /// <summary>The profile swept along the edge of the path face (cut away where it lies in the body).</summary>
    private void ApplyFollowPath(SketchDocument before, FaceRef profile, FaceRef path)
    {
        try
        {
            var (doc, made) = before.FollowPath(profile, path);
            Commit(before, doc);
            _last = null; _selected = null; _hover = null;
            Render();
            bool cut = doc.Volume() < before.Volume();
            StatusText.Text = cut ? T("✅ Langs de rand gevolgd en weggesneden (een afschuining of groef rondom).", "✅ Followed along the edge and cut away (a chamfer or groove all round).")
                : T("✅ Langs de rand gevolgd (een lijst rondom).", "✅ Followed along the edge (a rim all round).");
            Log(string.Create(CultureInfo.InvariantCulture, $"follow me: profile face {profile.Face} along face {path.Face}: ok, {(cut ? "cut" : "added")}, volume {doc.Volume() * 1e9:0.###} mm³"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log($"follow me along face {path.Face}: {ex.Message}");
        }
    }

    private void ApplyFollow(SketchDocument before, FaceRef f, Vec3 p, Vec3 dir, double angle)
    {
        try
        {
            var (doc, cap) = before.Revolve(f, p, dir, angle);
            Commit(before, doc);
            _last = null; _selected = cap;
            Render();
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = (angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture);
            StatusText.Text = T($"✅ Rondgedraaid: {(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture)}°.", $"✅ Turned: {(angle * 180 / Math.PI).ToString("0.#", CultureInfo.CurrentCulture)}°.");
            Log(string.Create(CultureInfo.InvariantCulture, $"follow me face {f.Face} round ({p.X * Display:0.###},{p.Y * Display:0.###},{p.Z * Display:0.###}) along ({dir.X:0.###},{dir.Y:0.###},{dir.Z:0.###}) {angle * 180 / Math.PI:0.#}°: ok, {doc.Bodies[f.Body].Faces.Count} faces, volume {doc.Volume() * 1e9:0.###} mm³"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"follow me face {f.Face} {angle * 180 / Math.PI:0.#}°: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ rotated rectangle (as in SketchUp)

    /// <summary>A rectangle at any angle: its first corner (stage 1), the second (stage 2: the first side), and its width
    /// now (signed: to the left of A → B seen along the normal).</summary>
    private FaceRef? _rrFace;
    private int _rrStage;
    private Vec3 _rrA, _rrB;
    private double _rrWidth;

    private List<Vec3> RotRectCorners(Vec3 a, Vec3 b, double w, Vec3 n)
    {
        var side = n.Cross(b - a).Normalized() * w;
        return [a, b, b + side, a + side];
    }

    private void StartRotRect((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("◇ Dat kan alleen op een plat vlak.", "◇ That only works on a flat face."); return; }
        _rrFace = h.Face; _rrStage = 1; _rrWidth = 0; _typed = "";
        _rrA = ArcPoint(h.Face, pos) ?? V(h.Point);
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = T("Lengte", "Length"); MeasureBox.Text = "";
        ShowSketch([_rrA], NormalOf(h.Face));
        StatusText.Text = T("◇ Klik de tweede hoek (de eerste zijde). Esc breekt af.", "◇ Click the second corner (the first side). Esc cancels.");
    }

    private void EndRotRect()
    {
        if (_rrFace == null) return;
        _rrFace = null; _rrStage = 0;
        ShowSketch([]);
    }

    private void RotRectTo(Point pos)
    {
        if (_rrFace is not { } f || _doc == null || _typed.Length > 0) return;
        var n = NormalOf(f);
        if (_rrStage == 1)
        {
            if (ArcPoint(f, pos) is { } p) { ShowSketch([_rrA, p], n); MeasureBox.Text = Mm((p - _rrA).Length); }
            return;
        }
        if (OnPlane(f, pos) is not { } q) return;
        var w = n.Cross(_rrB - _rrA).Normalized();
        double width = Math.Round((q - _rrA).Dot(w) * Display * 10) / 10 / Display;
        if (Math.Abs(width) < 1e-7) { ShowSketch([_rrA, _rrB], n); return; }
        _rrWidth = width;
        ShowSketch(RotRectCorners(_rrA, _rrB, width, n), n, closed: true);
        MeasureLabel.Text = T("Breedte", "Width"); MeasureBox.Text = Mm(Math.Abs(width));
        StatusText.Text = T($"◇ {Mm((_rrB - _rrA).Length)} × {Mm(Math.Abs(width))} mm · klik, of typ de breedte + Enter. Esc breekt af.", $"◇ {Mm((_rrB - _rrA).Length)} × {Mm(Math.Abs(width))} mm · click, or type the width + Enter. Esc cancels.");
    }

    private void RotRectClick(Point pos)
    {
        if (_rrFace is not { } f || _doc is not { } before) return;
        if (_rrStage == 1)
        {
            if (ArcPoint(f, pos) is not { } p || (p - _rrA).Length < 1e-6) return;
            _rrB = p; _rrStage = 2; _rrWidth = 0;
            StatusText.Text = T("◇ Beweeg voor de breedte en klik, of typ de breedte + Enter. Esc breekt af.", "◇ Move for the width and click, or type the width + Enter. Esc cancels.");
            return;
        }
        RotRectTo(pos);
        if (Math.Abs(_rrWidth) < 1e-7) return;
        var (a, b, w) = (_rrA, _rrB, _rrWidth);
        EndRotRect();
        ApplyRotRect(before, f, a, b, w);
    }

    private void ApplyRotRect(SketchDocument before, FaceRef f, Vec3 a, Vec3 b, double width)
    {
        var corners = RotRectCorners(a, b, width, NormalOf(f));
        try
        {
            var (doc, nf) = before.DrawPath(f, corners.Append(corners[0]).ToList());
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            MeasureLabel.Text = T("Breedte", "Width"); MeasureBox.Text = Mm(Math.Abs(width));
            StatusText.Text = T($"✅ Rechthoek {Mm((b - a).Length)} × {Mm(Math.Abs(width))} mm getekend. Kies ⇕ (P) om hem eruit te trekken of in te duwen.", $"✅ Rectangle {Mm((b - a).Length)} × {Mm(Math.Abs(width))} mm drawn. Choose ⇕ (P) to pull it out or push it in.");
            Log(string.Create(CultureInfo.InvariantCulture, $"rotated rectangle {(b - a).Length * Display:0.###} x {Math.Abs(width) * Display:0.###} mm on face {f.Face}: ok, new face {nf.Face}"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ arc (as SketchUp's 2 point arc)

    /// <summary>The arc being drawn: on this face, from A (stage 1), to B (stage 2), bulging this far (metres, signed:
    /// to the left of A → B seen along the normal) from the middle of A–B.</summary>
    private FaceRef? _arcFace;
    private int _arcStage;
    private Vec3 _arcA, _arcB;
    private double _arcBulge;
    /// <summary>Drawing a 3 point arc: then B is the point the arc goes through, and the mouse gives its end (C).</summary>
    private bool _arc3;
    private Vec3? _arcC;

    /// <summary>The point under the mouse on the face's plane, snapped to a corner, middle or edge as the line does.</summary>
    private Vec3? ArcPoint(FaceRef f, Point pos)
    {
        if (OnPlane(f, pos) is not { } p) return null;
        return EdgeSnap(f, p, pos) is { } s ? s.Point : p;
    }

    private void StartArc((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("⌒ Een boog tekenen kan alleen op een plat vlak.", "⌒ An arc can only be drawn on a flat face."); return; }
        _arcFace = h.Face; _arcStage = 1; _typed = ""; _arc3 = Arc3Mode; _arcC = null;
        _arcA = ArcPoint(h.Face, pos) ?? V(h.Point);
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = _arc3 ? T("Lengte", "Length") : T("Bolling", "Bulge"); MeasureBox.Text = "";
        ShowSketch([_arcA], NormalOf(h.Face));
        StatusText.Text = _arc3 ? T("⌓ Klik een punt waar de boog doorheen gaat. Esc breekt af.", "⌓ Click a point the arc goes through. Esc cancels.") : T("⌒ Klik het eindpunt van de boog. Esc breekt af.", "⌒ Click the end point of the arc. Esc cancels.");
    }

    private void EndArc()
    {
        if (_arcFace == null) return;
        _arcFace = null; _arcStage = 0;
        ShowSketch([]);
    }

    private void ArcTo(Point pos)
    {
        if (_arcFace is not { } f || _doc == null || _typed.Length > 0) return;
        var n = NormalOf(f);
        if (_arcStage == 1)
        {
            if (ArcPoint(f, pos) is { } p) { ShowSketch([_arcA, p], n); MeasureBox.Text = Mm((p - _arcA).Length); }
            return;
        }
        if (_arc3)
        {
            // the end under the mouse; the arc from A through B to it
            _arcC = null;
            if (ArcPoint(f, pos) is not { } e) return;
            if (!Bends(_arcA, _arcB, e, n)) { ShowSketch([_arcA, _arcB, e], n); StatusText.Text = T("⌓ De drie punten liggen (bijna) op een lijn; beweeg verder opzij.", "⌓ The three points lie (almost) on a line; move further aside."); return; }
            _arcC = e;
            var c3 = PushPullMe.Core.Drawing.ArcThrough(_arcA, _arcB, e, n);
            ShowSketch(PushPullMe.Core.Drawing.ArcPoints(c3, _arcA, e), n);
            MeasureBox.Text = Mm((e - _arcA).Length);
            StatusText.Text = T($"⌓ Straal {Mm(c3.Radius)} mm · klik het eindpunt. Esc breekt af.", $"⌓ Radius {Mm(c3.Radius)} mm · click the end point. Esc cancels.");
            return;
        }
        if (OnPlane(f, pos) is not { } q) return;
        var chord = _arcB - _arcA;
        var w = n.Cross(chord).Normalized();
        double bulge = (q - (_arcA + _arcB) * 0.5).Dot(w);
        // near a half circle: that
        double half = chord.Length / 2;
        bool semi = Math.Abs(Math.Abs(bulge) - half) < 0.06 * half;
        bulge = semi ? half * Math.Sign(bulge) : Math.Round(bulge * Display * 10) / 10 / Display;
        if (Math.Abs(bulge) < 1e-7) { ShowSketch([_arcA, _arcB], n); return; }
        _arcBulge = bulge;
        var through = (_arcA + _arcB) * 0.5 + w * bulge;
        var c = PushPullMe.Core.Drawing.ArcThrough(_arcA, through, _arcB, n);
        ShowSketch(PushPullMe.Core.Drawing.ArcPoints(c, _arcA, _arcB), n);
        MeasureBox.Text = Mm(Math.Abs(bulge));
        StatusText.Text = T($"⌒ Bolling {Mm(Math.Abs(bulge))} mm, straal {Mm(c.Radius)} mm{(semi ? " · 🎯 halve cirkel" : "")} · klik, of typ de bolling + Enter. Esc breekt af.", $"⌒ Bulge {Mm(Math.Abs(bulge))} mm, radius {Mm(c.Radius)} mm{(semi ? " · 🎯 half circle" : "")} · click, or type the bulge + Enter. Esc cancels.");
    }

    private void ArcClick(Point pos)
    {
        if (_arcFace is not { } f || _doc is not { } before) return;
        if (_arcStage == 1)
        {
            if (ArcPoint(f, pos) is not { } p || (p - _arcA).Length < 1e-6) return;
            _arcB = p; _arcStage = 2; _arcBulge = 0;
            StatusText.Text = _arc3 ? T("⌓ Klik het eindpunt van de boog. Esc breekt af.", "⌓ Click the end point of the arc. Esc cancels.") : T("⌒ Beweeg voor de bolling en klik, of typ de bolling + Enter. Esc breekt af.", "⌒ Move for the bulge and click, or type the bulge + Enter. Esc cancels.");
            return;
        }
        if (_arc3)
        {
            ArcTo(pos);
            if (_arcC is not { } end) return;
            var (a3, b3) = (_arcA, _arcB);
            EndArc();
            ApplyArcThrough(before, f, a3, end, b3, "");
            return;
        }
        ArcTo(pos);
        if (Math.Abs(_arcBulge) < 1e-7) return;
        var (a, b, bulge) = (_arcA, _arcB, _arcBulge);
        EndArc();
        ApplyArc(before, f, a, b, bulge);
    }

    /// <summary>Whether a, b and c bend enough to make an arc (not on one line).</summary>
    private static bool Bends(Vec3 a, Vec3 b, Vec3 c, Vec3 n)
    {
        double ab = (b - a).Length, bc = (c - b).Length, ac = (c - a).Length;
        if (ab < 1e-6 || bc < 1e-6 || ac < 1e-6) return false;
        return Math.Abs((b - a).Cross(c - a).Dot(n)) / (ab * ac) > 0.01;
    }

    private void ApplyArc(SketchDocument before, FaceRef f, Vec3 a, Vec3 b, double bulge)
    {
        var n = NormalOf(f);
        ApplyArcThrough(before, f, a, b, (a + b) * 0.5 + n.Cross(b - a).Normalized() * bulge, Mm(Math.Abs(bulge)));
    }

    private void ApplyArcThrough(SketchDocument before, FaceRef f, Vec3 a, Vec3 b, Vec3 through, string bulgeText)
    {
        // not from edge to edge: a loose arc (as SketchUp), a face once it closes a shape
        if (!(OnPlaneEdge(f, a) && OnPlaneEdge(f, b)))
        {
            AddLooseArc(before, f, a, b, through);
            MeasureLabel.Text = bulgeText.Length > 0 ? T("Bolling", "Bulge") : T("Lengte", "Length"); MeasureBox.Text = bulgeText.Length > 0 ? bulgeText : Mm((b - a).Length);
            return;
        }
        try
        {
            var (doc, nf) = before.DrawArc(f, a, b, through);
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            MeasureLabel.Text = bulgeText.Length > 0 ? T("Bolling", "Bulge") : T("Lengte", "Length"); MeasureBox.Text = bulgeText.Length > 0 ? bulgeText : Mm((b - a).Length);
            StatusText.Text = T("✅ Boog getekend. Kies ⇕ (P) om een deel eruit te trekken of in te duwen (zijn zijkant wordt rond).", "✅ Arc drawn. Choose ⇕ (P) to pull a part out or push it in (its side becomes round).");
            Log(string.Create(CultureInfo.InvariantCulture, $"arc on face {f.Face} ({a.X * Display:0.###},{a.Y * Display:0.###},{a.Z * Display:0.###}) → ({b.X * Display:0.###},{b.Y * Display:0.###},{b.Z * Display:0.###}) through ({through.X * Display:0.###},{through.Y * Display:0.###},{through.Z * Display:0.###}): ok, new face {nf.Face}"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"arc on face {f.Face} ({a.X * Display:0.###},{a.Y * Display:0.###},{a.Z * Display:0.###}) → ({b.X * Display:0.###},{b.Y * Display:0.###},{b.Z * Display:0.###}) through ({through.X * Display:0.###},{through.Y * Display:0.###},{through.Z * Display:0.###}): {ex.Message}"));
        }
    }

    /// <summary>An arc from a to b by way of <paramref name="through"/> as a loose arc on f's plane; with the loose lines
    /// and edges there it may close a shape: then that becomes a face.</summary>
    private void AddLooseArc(SketchDocument before, FaceRef f, Vec3 a, Vec3 b, Vec3 through)
    {
        try
        {
            var c = PushPullMe.Core.Drawing.ArcThrough(a, through, b, NormalOf(f));
            var with = before.AddLines([new SketchLine(a, b, c.Centre, c.Normal)]);
            var (doc, made) = with.CloseLooseLines(f);
            Commit(before, doc);
            _last = null;
            if (made.Count > 0) { _selected = made[^1]; _lastDrawn = made[^1]; }
            Render();
            StatusText.Text = made.Count > 0
                ? T($"✅ Boog getekend; met de lijnen die er al lagen {(made.Count == 1 ? "is een vlak" : $"zijn {made.Count} vlakken")} gesloten. Kies ⇕ (P) om het eruit te trekken of in te duwen.", $"✅ Arc drawn; with the lines already there {(made.Count == 1 ? "a face is" : $"{made.Count} faces are")} closed. Choose ⇕ (P) to pull it out or push it in.")
                : T($"✅ Losse boog getekend (straal {Mm(c.Radius)} mm). Sluit hem met lijnen of randen tot een vorm, dan wordt het een vlak.", $"✅ Loose arc drawn (radius {Mm(c.Radius)} mm). Close it into a shape with lines or edges and it becomes a face.");
            Log(string.Create(CultureInfo.InvariantCulture, $"loose arc on face {f.Face} ({a.X * Display:0.###},{a.Y * Display:0.###},{a.Z * Display:0.###}) → ({b.X * Display:0.###},{b.Y * Display:0.###},{b.Z * Display:0.###}) r {c.Radius * Display:0.###}: {made.Count} face(s) closed"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ polygon (as in SketchUp)

    /// <summary>The polygon being drawn: on this face, round this centre; its first corner the way of <see cref="_polyDir"/>
    /// (unit) at <see cref="_polyRadius"/>; how many corners.</summary>
    private FaceRef? _polyFace;
    private Vec3 _polyCentre, _polyDir;
    private double _polyRadius;
    private int _polySides = 6;

    /// <summary>The corners of a regular polygon on a face with normal n: the first at centre + dir·r.</summary>
    private List<Vec3> PolygonCorners(Vec3 centre, Vec3 dir, double r, Vec3 n)
    {
        var u = (dir - n * dir.Dot(n)).Normalized(); var v = n.Cross(u);
        return Enumerable.Range(0, _polySides).Select(k => centre + (u * Math.Cos(2 * Math.PI * k / _polySides) + v * Math.Sin(2 * Math.PI * k / _polySides)) * r).ToList();
    }

    private void StartPolygon((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc == null) return;
        if (!IsPlanar(h.Face)) { StatusText.Text = T("⬡ Een veelhoek tekenen kan alleen op een plat vlak.", "⬡ A polygon can only be drawn on a flat face."); return; }
        var n = NormalOf(h.Face);
        var p = V(h.Point);
        // (on a guide line's crossing or an edge's middle or corner, as the rectangle and circle do)
        if (_doc.Bodies[h.Face.Body].Faces[h.Face.Face].Surface is BrepPlane pl && GuideSnap(p, _mouseAt, n, pl.Origin) is { } gs) p = gs.Point;
        _polyFace = h.Face; _polyCentre = p; _polyDir = default; _polyRadius = 0; _typed = "";
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = "";
        ShowSketch([p], n);
        StatusText.Text = T($"⬡ Veelhoek ({_polySides} hoeken; ↑/↓: meer/minder): beweeg voor de grootte en de stand, klik; of typ de straal + Enter. Esc breekt af.", $"⬡ Polygon ({_polySides} sides; ↑/↓: more/fewer): move for the size and the turn, click; or type the radius + Enter. Esc cancels.");
    }

    private void EndPolygon()
    {
        if (_polyFace == null) return;
        _polyFace = null;
        ShowSketch([]);
    }

    private void PolygonTo(Point pos)
    {
        if (_polyFace is not { } f || _doc == null || _typed.Length > 0) return;
        if (OnPlane(f, pos) is not { } p) return;
        var n = NormalOf(f);
        var d = p - _polyCentre; d -= n * d.Dot(n);
        double r = Math.Round(d.Length * Display * 10) / 10 / Display;
        if (r < 1e-7) return;
        _polyDir = d.Normalized(); _polyRadius = r;
        ShowSketch(PolygonCorners(_polyCentre, _polyDir, r, n), n, closed: true);
        MeasureBox.Text = Mm(r);
        StatusText.Text = T($"⬡ {_polySides} hoeken, straal {Mm(r)} mm (zijde {Mm(2 * r * Math.Sin(Math.PI / _polySides))} mm) · klik, of typ de straal + Enter. ↑/↓: meer/minder hoeken. Esc breekt af.", $"⬡ {_polySides} sides, radius {Mm(r)} mm (side {Mm(2 * r * Math.Sin(Math.PI / _polySides))} mm) · click, or type the radius + Enter. ↑/↓: more/fewer sides. Esc cancels.");
    }

    private void FinishPolygon()
    {
        if (_polyFace is not { } f || _doc is not { } before || _polyRadius < 1e-7) return;
        var (c, d, r) = (_polyCentre, _polyDir, _polyRadius);
        EndPolygon();
        ApplyPolygon(before, f, c, d, r);
    }

    private void ApplyPolygon(SketchDocument before, FaceRef f, Vec3 centre, Vec3 dir, double r)
    {
        var corners = PolygonCorners(centre, dir, r, NormalOf(f));
        try
        {
            // (a closed path: a face of its own; across other faces in the plane as the line does)
            var (doc, nf) = before.DrawPath(f, corners.Append(corners[0]).ToList());
            Commit(before, doc);
            _last = null; _selected = nf; _lastDrawn = nf;
            Render();
            MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = Mm(r);
            StatusText.Text = T($"✅ Veelhoek ({_polySides} hoeken, straal {Mm(r)} mm) getekend. Kies ⇕ (P) om hem eruit te trekken of in te duwen.", $"✅ Polygon ({_polySides} sides, radius {Mm(r)} mm) drawn. Choose ⇕ (P) to pull it out or push it in.");
            Log(string.Create(CultureInfo.InvariantCulture, $"polygon {_polySides} corners r {r * Display:0.###} mm on face {f.Face}: ok, new face {nf.Face}"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"polygon {_polySides} corners r {r * Display:0.###} mm on face {f.Face}: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ mirror

    /// <summary>A body whose face clicked with ⇋ was round (a free form has no flat face to mirror in): it waits for the
    /// plane to mirror it in; and whether that is to be a copy.</summary>
    private int? _mirrorBody;
    private bool _mirrorCopy;

    /// <summary>
    /// ⇋: the body of the flat face clicked mirrored in that face's plane (Ctrl: a mirrored copy added). A round face
    /// clicked: its body waits for the plane, a flat face of any body, or (arrow keys) the plane through its middle
    /// square to red, green or blue.
    /// </summary>
    private void MirrorAt((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc is not { } before) return;
        var face = before.Bodies[h.Face.Body].Faces[h.Face.Face];
        if (_mirrorBody is int waiting)
        {
            if (face.Surface is not BrepPlane wp) { StatusText.Text = T("⇋ Klik een plat vlak (van welk lichaam ook) om in te spiegelen. Of klap hem om door zijn eigen midden: → in de rode richting, ← in de groene, ↑ boven-onder (blauw). Esc breekt af.", "⇋ Click a flat face (of any body) to mirror in. Or flip it through its own middle: → along red, ← along green, ↑ top to bottom (blue). Esc cancels."); return; }
            MirrorBodyIn(waiting, wp.Origin, FaceGeometry.Normal(face), $"face {h.Face.Face} of body {h.Face.Body}");
            return;
        }
        if (face.Surface is not BrepPlane pl)
        {
            _mirrorBody = h.Face.Body; _mirrorCopy = (Mods & ModifierKeys.Control) != 0;
            _selected = h.Face; UpdateHighlight();
            StatusText.Text = T("⇋ Waarin spiegelen? Klik een plat vlak (van welk lichaam ook). Of klap hem om door zijn eigen midden: → in de rode richting, ← in de groene, ↑ boven-onder (blauw). Esc breekt af.", "⇋ Mirror in what? Click a flat face (of any body). Or flip it through its own middle: → along red, ← along green, ↑ top to bottom (blue). Esc cancels.");
            return;
        }
        _mirrorCopy = (Mods & ModifierKeys.Control) != 0;
        MirrorBodyIn(h.Face.Body, pl.Origin, FaceGeometry.Normal(face), $"face {h.Face.Face}");
    }

    /// <summary>A body mirrored in the plane through <paramref name="origin"/> square to <paramref name="n"/> (a copy when
    /// that was asked for); <paramref name="what"/>: that plane, for the log.</summary>
    private void MirrorBodyIn(int body, Vec3 origin, Vec3 n, string what)
    {
        if (_doc is not { } before) return;
        bool copy = _mirrorCopy;
        _mirrorBody = null;
        try
        {
            var doc = Whole(before, body, copy, (md, mb, mc) => md.MirrorBody(mb, origin, n, mc), Placement.Mirror(origin, n));
            Commit(before, doc);
            _last = null; _lastMove = null; _selected = null; _hover = null;
            Render();
            StatusText.Text = copy ? T("✅ Gespiegelde kopie erbij.", "✅ Mirrored copy added.") : T("✅ Gespiegeld.", "✅ Mirrored.");
            Log(string.Create(CultureInfo.InvariantCulture, $"mirror body {body}{(copy ? " (copy)" : "")} in {what}: ok"));
        }
        catch (PushPullException ex)
        {
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ the choice and the lasso

    /// <summary>The chosen faces (of one body): with the lasso or Shift+click; ✥ moves them together, 🎨 paints them all.</summary>
    private (int Body, HashSet<int> Faces)? _multi;

    /// <summary>The lasso being drawn (screen points), or null.</summary>
    private List<Point>? _lasso;

    private void ToggleChosen(FaceRef f)
    {
        if (_multi is not { } m || m.Body != f.Body) m = (f.Body, new HashSet<int>());
        if (!m.Faces.Remove(f.Face)) m.Faces.Add(f.Face);
        _multi = m.Faces.Count > 0 ? m : null;
        _selected = null;
        UpdateHighlight();
        StatusText.Text = ChoiceText();
    }

    private string ChoiceText() => _multi is { } m
        ? T($"✔ {m.Faces.Count} {(m.Faces.Count == 1 ? "vlak" : "vlakken")} gekozen · ✥ verplaatst ze samen, 🎨 verft ze in één keer · Esc: keuze leeg.", $"✔ {m.Faces.Count} {(m.Faces.Count == 1 ? "face" : "faces")} selected · ✥ moves them together, 🎨 paints them at once · Esc: clear the selection.")
        : T("Niets gekozen.", "Nothing selected.");

    private void StartLasso(Point pos)
    {
        _lasso = [pos];
        SetLasso([pos]);
        StatusText.Text = T("➰ Trek de lus om de vlakken en klik om hem te sluiten. Esc breekt af.", "➰ Draw the loop round the faces and click to close it. Esc cancels.");
    }

    private void LassoTo(Point pos)
    {
        if (_lasso == null) return;
        if ((pos - _lasso[^1]).Length < 4) return;
        _lasso.Add(pos);
        _lassoLine.Add(pos); _window.LabelsChanged();
    }

    private void EndLasso()
    {
        _lasso = null;
        SetLasso([]);
    }

    /// <summary>The lasso closed: the faces whose middle (on screen) lies inside it are chosen (Shift: added, Ctrl:
    /// taken out); of one body, the one with the most.</summary>
    private void FinishLasso()
    {
        if (_lasso is not { } poly || _doc == null) return;
        EndLasso();
        if (poly.Count < 3) { StatusText.Text = T("➰ Trek een lus: klik, beweeg om de vlakken heen en klik nog eens.", "➰ Draw a loop: click, move round the faces and click again."); return; }
        bool Inside(Point p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                    inside = !inside;
            return inside;
        }
        var hits = new List<FaceRef>();
        for (int b = 0; b < _doc.Bodies.Count; b++)
            for (int f = 0; f < _doc.Bodies[b].Faces.Count; f++)
            {
                var tris = _doc.TrianglesOf(new FaceRef(b, f));
                if (tris.Count == 0) continue;
                // the middle: weighted by area
                Vec3 sum = new(0, 0, 0); double area = 0;
                foreach (var (a, bb, c) in tris) { double ar = (bb - a).Cross(c - a).Length; sum += (a + bb + c) * (ar / 3); area += ar; }
                if (area <= 0) continue;
                var mid = sum * (1 / area);
                if (Inside(Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(mid)))) hits.Add(new FaceRef(b, f));
            }
        var mods = Mods;
        if (hits.Count == 0 && (mods & (ModifierKeys.Shift | ModifierKeys.Control)) == 0) { _multi = null; UpdateHighlight(); StatusText.Text = T("➰ Geen vlak binnen de lus.", "➰ No face inside the loop."); return; }
        int body = _multi is { } cur && (mods & (ModifierKeys.Shift | ModifierKeys.Control)) != 0 ? cur.Body
            : hits.GroupBy(h => h.Body).OrderByDescending(g => g.Count()).First().Key;
        var set = (mods & (ModifierKeys.Shift | ModifierKeys.Control)) != 0 && _multi is { } c0 && c0.Body == body ? new HashSet<int>(c0.Faces) : new HashSet<int>();
        foreach (var h in hits.Where(h => h.Body == body))
            if ((mods & ModifierKeys.Control) != 0) set.Remove(h.Face); else set.Add(h.Face);
        _multi = set.Count > 0 ? (body, set) : null;
        _selected = null;
        UpdateHighlight();
        StatusText.Text = ChoiceText() + (hits.Any(h => h.Body != body) ? T(" (alleen van één lichaam)", " (of one body only)") : "");
        Log($"lasso: {set.Count} face(s) of body {body}");
    }

    // ------------------------------------------------------------------ scale (as in SketchUp)

    // a scale in progress: 1 = the fixed point is set, 2 = the point pulled at too; the body (a copy?), only along an axis?,
    // the factor now and whether it works, and the document it started from
    private int _scStage;
    private int _scBody;
    private bool _scCopy, _scOk;
    private Vec3 _scFixed, _scGrab;
    private Vec3? _scAxis;
    private double _scFactor = 1;
    private SketchDocument? _scBase;

    /// <summary>The last scale (to change its factor by typing another one).</summary>
    private (SketchDocument Before, int Body, Vec3 Fixed, Vec3? Along, bool Copy)? _lastScale;

    private void StartScale((FaceRef Face, Point3D Point)? hit)
    {
        if (hit is not { } h || _doc == null) return;
        _scBody = h.Face.Body; _scCopy = (Mods & ModifierKeys.Control) != 0;
        _scFixed = V(h.Point); _scAxis = null; _scFactor = 1; _scOk = false;
        _scBase = _doc; _scStage = 1; _lastScale = null; _last = null; _lastMove = null; _lastRotate = null; _typed = "";
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = T("Factor", "Factor"); MeasureUnit.Text = "×"; MeasureBox.Text = "";
        ShowSketch([_scFixed]);
        StatusText.Text = T($"⇲ Schalen{(_scCopy ? " (kopie)" : "")}: dit punt blijft staan. Klik het punt waaraan je trekt, of typ de factor + Enter. Esc breekt af.", $"⇲ Scale{(_scCopy ? " (copy)" : "")}: this point stays put. Click the point you pull at, or type the factor + Enter. Esc cancels.");
    }

    private void EndScale()
    {
        if (_scStage == 0) return;
        _scStage = 0; _scOk = false; _scBase = null;
        MeasureUnit.Text = "mm";
        ShowSketch([]);
    }

    /// <summary>The point under the mouse: on the model, else on the plane across the view through the fixed point.</summary>
    private Vec3? ScalePoint(Point pos)
    {
        if (FaceAt(pos) is { } h) return V(h.Point);
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null || View.Camera is not ProjectionCamera cam) return null;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        var look = new Vec3(cam.LookDirection.X, cam.LookDirection.Y, cam.LookDirection.Z).Normalized();
        double dn = dir.Dot(look);
        return Math.Abs(dn) < 1e-9 ? null : org + dir * ((_scFixed - org).Dot(look) / dn);
    }

    /// <summary>The point on the line through <paramref name="a"/> along <paramref name="d"/> nearest to the mouse ray.</summary>
    private Vec3? NearestOnLine(Point pos, Vec3 a, Vec3 d)
    {
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return null;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        d = d.Normalized();
        double b = d.Dot(dir);
        if (1 - b * b < 1e-9) return null;
        var w0 = a - org;
        double t = (b * w0.Dot(dir) - w0.Dot(d)) / (1 - b * b);
        return a + d * t;
    }

    private void ScaleTo(Point pos)
    {
        if (_scStage == 0 || _scBase == null || _typed.Length > 0) return;
        if (_scStage == 1)
        {
            if (ScalePoint(pos) is { } p) ShowSketch([_scFixed, p]);
            return;
        }
        // the factor: how far the mouse is from the fixed point compared with the point pulled at (along the axis held)
        var dir = _scAxis ?? (_scGrab - _scFixed);
        double baseLen = (_scGrab - _scFixed).Dot(dir.Normalized());
        if (Math.Abs(baseLen) < 1e-9) { StatusText.Text = T("⇲ Het punt waaraan je trekt ligt niet langs deze as; kies een andere (pijltjes) of typ de factor.", "⇲ The point you pull at does not lie along this axis; choose another (arrow keys) or type the factor."); return; }
        if (NearestOnLine(pos, _scGrab, dir) is not { } q) return;
        double f = (q - _scFixed).Dot(dir.Normalized()) / baseLen;
        f = Math.Max(0.01, Math.Round(f * 100) / 100);
        if (Math.Abs(f - _scFactor) < 1e-12 && _scOk) return;
        _scFactor = f;
        MeasureBox.Text = f.ToString("0.##", CultureInfo.CurrentCulture);
        var axisName = _scAxis is { } ax ? Axes.First(a => a.Dir.Dot(ax) > 0.99).Name : null;
        var axisColor = _scAxis is { } ax2 ? Axes.First(a => a.Dir.Dot(ax2) > 0.99).Color : (Color?)null;
        ShowSketch([_scFixed, _scFixed + (_scGrab - _scFixed) * f], default, color: axisColor);
        try
        {
            var doc = Whole(_scBase, _scBody, _scCopy, (sd, sb, sc) => sd.ScaleBody(sb, _scFixed, f, _scAxis, sc), _scAxis == null ? Placement.Scaled(_scFixed, f) : null);
            _scOk = true;
            Render(doc);
            StatusText.Text = T($"⇲ {f.ToString("0.##", CultureInfo.CurrentCulture)}×{(axisName != null ? " langs de " + axisName : "")} · klik om vast te zetten, of typ de factor + Enter. Pijltjes: langs één as. Esc breekt af.", $"⇲ {f.ToString("0.##", CultureInfo.CurrentCulture)}×{(axisName != null ? " along the " + axisName : "")} · click to set it, or type the factor + Enter. Arrow keys: along one axis. Esc cancels.");
        }
        catch (PushPullException ex) { _scOk = false; Render(_scBase); StatusText.Text = "⛔ " + ex.Message; }
    }

    private void ScaleClick(Point pos)
    {
        if (_scStage == 1)
        {
            if (ScalePoint(pos) is not { } p || (p - _scFixed).Length < 1e-6) return;
            _scGrab = p; _scStage = 2;
            StatusText.Text = T("⇲ Beweeg om te schalen; klik om vast te zetten, of typ de factor + Enter. Pijltjes: langs één as. Esc breekt af.", "⇲ Move to scale; click to set it, or type the factor + Enter. Arrow keys: along one axis. Esc cancels.");
            return;
        }
        ScaleTo(pos);
        if (!_scOk || Math.Abs(_scFactor - 1) < 1e-9 || _scBase is not { } before) return;
        var (b, fx, f, ax, copy) = (_scBody, _scFixed, _scFactor, _scAxis, _scCopy);
        EndScale();
        ApplyScale(before, b, fx, f, ax, copy, replaceLast: false);
    }

    private void ApplyScale(SketchDocument before, int body, Vec3 fixedPoint, double factor, Vec3? along, bool copy, bool replaceLast)
    {
        try
        {
            var doc = Whole(before, body, copy, (sd, sb, sc) => sd.ScaleBody(sb, fixedPoint, factor, along, sc), along == null ? Placement.Scaled(fixedPoint, factor) : null);
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _lastScale = (before, body, fixedPoint, along, copy);
            _last = null; _lastMove = null; _selected = null;
            Render();
            MeasureLabel.Text = T("Factor", "Factor"); MeasureUnit.Text = "×"; MeasureBox.Text = factor.ToString("0.###", CultureInfo.CurrentCulture);
            StatusText.Text = T($"✅ {(copy ? "Geschaalde kopie" : "Geschaald")}: {factor.ToString("0.###", CultureInfo.CurrentCulture)}×. Typ een andere factor + Enter om het aan te passen.", $"✅ {(copy ? "Scaled copy" : "Scaled")}: {factor.ToString("0.###", CultureInfo.CurrentCulture)}×. Type another factor + Enter to change it.");
            Log(string.Create(CultureInfo.InvariantCulture, $"scale body {body}{(copy ? " (copy)" : "")} {factor:0.###}x{(along is { } a ? $" along ({a.X:0},{a.Y:0},{a.Z:0})" : "")} from ({fixedPoint.X * Display:0.###},{fixedPoint.Y * Display:0.###},{fixedPoint.Z * Display:0.###}): ok"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ rotate (as in SketchUp)

    // a turn in progress: 1 = the centre is set (its axis), 2 = the start direction too; the body (a copy?), the axis
    // square to the face clicked (for ↓), the angle now and whether it works, and the document it started from
    private int _rotStage;
    private int _rotBody;
    private bool _rotCopy, _rotOk;
    private Vec3 _rotCentre, _rotAxis, _rotFaceAxis, _rotRef;
    private double _rotAngle;
    private SketchDocument? _rotBase;
    /// <summary>Not the whole body but this face, tilted round one of its edges (clicked near it).</summary>
    private FaceRef? _rotTilt;

    /// <summary>The last turn (to change its angle by typing another one).</summary>
    private (SketchDocument Before, int Body, Vec3 Centre, Vec3 Axis, bool Copy, double Sign, FaceRef? Tilt)? _lastRotate;

    private void StartRotate((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (hit is not { } h || _doc == null) return;
        var face = _doc.Bodies[h.Face.Body].Faces[h.Face.Face];
        // close to a straight edge of a flat face: that face tilts round the edge (its neighbours follow)
        _rotTilt = null;
        if (face.Surface is BrepPlane && GroupTaken(h.Face.Body) == null && EdgeAt(hit, pos) is { } he && he.Body == h.Face.Body
            && _doc.Bodies[he.Body].Edges[he.Edge] is { Curve: BrepLine, Start: >= 0 } hinge && ScreenDistanceToEdge(_doc.Bodies[he.Body], he.Edge, pos) < 8)
        {
            var bd = _doc.Bodies[he.Body];
            Vec3 a = bd.Vertices[hinge.Start], ax = (bd.Vertices[hinge.End] - a).Normalized(), n = FaceGeometry.Normal(face);
            // the start direction: from the edge into the face
            var into = n.Cross(ax);
            var mid = FaceGeometry.LoopPoints(bd, face.Loops[0]).Aggregate(new Vec3(0, 0, 0), (s, q) => s + q) * (1.0 / face.Loops[0].Count);
            if (into.Dot(mid - a) < 0) into = into * -1;
            _rotTilt = h.Face; _rotBody = h.Face.Body; _rotCopy = false;
            // (on the edge where it was clicked: the mouse turns it in the plane there)
            _rotCentre = a + ax * (V(h.Point) - a).Dot(ax); _rotAxis = ax; _rotFaceAxis = ax; _rotRef = into.Normalized();
            _rotBase = _doc; _lastRotate = null; _last = null; _lastMove = null; _typed = "";
            _selected = null; _hover = null; UpdateHighlight();
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = "";
            _rotStage = 2; _rotOk = false; _rotAngle = 0;
            ShowProtractor(_rotRef, null);
            StatusText.Text = T("↻ Kantelen om deze rand: beweeg (stappen van 15°) en klik, of typ de hoek + Enter. Esc breekt af.", "↻ Tilt round this edge: move (steps of 15°) and click, or type the angle + Enter. Esc cancels.");
            return;
        }
        _rotBody = h.Face.Body; _rotCopy = (Mods & ModifierKeys.Control) != 0;
        _rotCentre = V(h.Point);
        _rotFaceAxis = face.Surface is BrepPlane ? FaceGeometry.Normal(face) : Axes[2].Dir;
        _rotBase = _doc; _lastRotate = null; _last = null; _lastMove = null; _typed = "";
        _selected = null; _hover = null; UpdateHighlight();
        MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = "";
        SetRotateAxis(_rotFaceAxis);
    }

    private void SetRotateAxis(Vec3 axis)
    {
        _rotAxis = axis.Normalized(); _rotStage = 1; _rotOk = false; _rotAngle = 0;
        if (_rotBase != null) Render(_rotBase);
        string which = Axes.FirstOrDefault(a => Math.Abs(a.Dir.Dot(_rotAxis)) > 1 - 1e-9).Name ?? T("as haaks op het vlak", "axis square to the face");
        StatusText.Text = T($"↻ Draaien{(_rotCopy ? " (kopie)" : "")} om de {which}: klik de beginrichting, of typ de hoek + Enter. Pijltjes: andere as. Esc breekt af.", $"↻ Rotate{(_rotCopy ? " (copy)" : "")} round the {which}: click the start direction, or type the angle + Enter. Arrow keys: another axis. Esc cancels.");
        ShowProtractor(null, null);
    }

    private void EndRotate()
    {
        if (_rotStage == 0) return;
        _rotStage = 0; _rotOk = false; _rotBase = null; _rotTilt = null;
        MeasureUnit.Text = "mm";
        ShowSketch([]);
    }

    /// <summary>Where the ray under the mouse meets the plane through the centre square to the axis.</summary>
    private Vec3? OnRotatePlane(Point pos)
    {
        var ray = Viewport3DHelper.GetRay(View.Viewport, pos);
        if (ray == null) return null;
        var org = V(ray.Origin); var dir = new Vec3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z).Normalized();
        double den = dir.Dot(_rotAxis);
        if (Math.Abs(den) < 1e-9) return null;
        return org + dir * ((_rotCentre - org).Dot(_rotAxis) / den);
    }

    /// <summary>The protractor: a circle round the centre in the plane of the turn, with the start direction and the one
    /// now.</summary>
    private void ShowProtractor(Vec3? start, Vec3? now)
    {
        Point Screen(Vec3 v) => Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(v));
        var u = Math.Abs(_rotAxis.Dot(new Vec3(1, 0, 0))) < 0.9 ? new Vec3(1, 0, 0).Cross(_rotAxis).Normalized() : new Vec3(0, 1, 0).Cross(_rotAxis).Normalized();
        var w = _rotAxis.Cross(u);
        double pxPerM = (Screen(_rotCentre + u * 0.001) - Screen(_rotCentre)).Length * 1000;
        double r = pxPerM > 1e-6 ? 70 / pxPerM : _modelSize / Display * 0.1;
        var segs = new List<Vec3>();
        for (int i = 0; i < 48; i++)
        {
            segs.Add(_rotCentre + (u * Math.Cos(i * Math.PI / 24) + w * Math.Sin(i * Math.PI / 24)) * r);
            segs.Add(_rotCentre + (u * Math.Cos((i + 1) * Math.PI / 24) + w * Math.Sin((i + 1) * Math.PI / 24)) * r);
        }
        if (start is { } s0) { segs.Add(_rotCentre); segs.Add(_rotCentre + s0 * (r * 1.3)); }
        if (now is { } n0) { segs.Add(_rotCentre); segs.Add(_rotCentre + n0 * (r * 1.3)); }
        if (_sketch != null) { View.Children.Remove(_sketch); _sketch = null; }
        _sketchShown = null;
        var axisColor = Axes.FirstOrDefault(a => Math.Abs(a.Dir.Dot(_rotAxis)) > 1 - 1e-9).Color;
        _sketch = new LineSet { Points = new Point3DCollection(segs.Select(v => TowardEye(P(v), 2e-3))), Color = axisColor == default ? SelectColor : axisColor, Thickness = 2.5 };
        View.Children.Add(_sketch);
    }

    private void RotateTo(Point pos)
    {
        if (_rotStage == 0 || _rotBase == null || _typed.Length > 0) return;
        if (OnRotatePlane(pos) is not { } p) return;
        var v = p - _rotCentre; v -= _rotAxis * v.Dot(_rotAxis);
        if (v.Length < 1e-9) return;
        v = v.Normalized();
        if (_rotStage == 1) { ShowProtractor(v, null); return; }
        // the angle from the start direction, counter-clockwise round the axis; near a multiple of 15°: that
        double deg = Math.Atan2(_rotRef.Cross(v).Dot(_rotAxis), _rotRef.Dot(v)) * 180 / Math.PI;
        double snapped = Math.Round(deg / 15) * 15;
        deg = Math.Abs(deg - snapped) < 2.5 ? snapped : Math.Round(deg * 2) / 2;
        _rotAngle = deg * Math.PI / 180;
        MeasureBox.Text = deg.ToString("0.#", CultureInfo.CurrentCulture);
        ShowProtractor(_rotRef, new BrepTransform.Turn(new Vec3(0, 0, 0), _rotAxis, _rotAngle).Dir(_rotRef));
        try
        {
            var doc = _rotTilt is { } tf ? _rotBase.Tilt(tf, _rotCentre, _rotAxis, _rotAngle, checkCrossing: false).Doc
                : Whole(_rotBase, _rotBody, _rotCopy, (rd, rb, rc) => rd.RotateBody(rb, _rotCentre, _rotAxis, _rotAngle, rc), Placement.Turn(_rotCentre, _rotAxis, _rotAngle));
            _rotOk = true;
            Render(doc);
            StatusText.Text = T($"↻ {deg.ToString("0.#", CultureInfo.CurrentCulture)}°{(Math.Abs(deg - snapped) < 1e-9 && deg != 0 ? " · 🎯 15°-stap" : "")} · klik om vast te zetten, of typ de hoek + Enter. Esc breekt af.", $"↻ {deg.ToString("0.#", CultureInfo.CurrentCulture)}°{(Math.Abs(deg - snapped) < 1e-9 && deg != 0 ? " · 🎯 15° step" : "")} · click to set it, or type the angle + Enter. Esc cancels.");
        }
        catch (PushPullException ex) { _rotOk = false; StatusText.Text = "⛔ " + ex.Message; }
    }

    private void RotateClick(Point pos)
    {
        if (_rotStage == 1)
        {
            if (OnRotatePlane(pos) is not { } p) return;
            var v = p - _rotCentre; v -= _rotAxis * v.Dot(_rotAxis);
            if (v.Length < 1e-7) return;
            _rotRef = v.Normalized(); _rotStage = 2;
            StatusText.Text = T("↻ Beweeg om te draaien; klik om vast te zetten, of typ de hoek + Enter. Esc breekt af.", "↻ Move to rotate; click to set it, or type the angle + Enter. Esc cancels.");
            return;
        }
        RotateTo(pos);
        if (!_rotOk || Math.Abs(_rotAngle) < 1e-9 || _rotBase is not { } before) return;
        var (b, c, a, ang, copy, tilt) = (_rotBody, _rotCentre, _rotAxis, _rotAngle, _rotCopy, _rotTilt);
        EndRotate();
        ApplyRotate(before, b, c, a, ang, copy, replaceLast: false, tilt);
    }

    private void ApplyRotate(SketchDocument before, int body, Vec3 centre, Vec3 axis, double angle, bool copy, bool replaceLast, FaceRef? tilt = null)
    {
        double deg = angle * 180 / Math.PI;
        try
        {
            var doc = tilt is { } tf ? before.Tilt(tf, centre, axis, angle).Doc : Whole(before, body, copy, (rd, rb, rc) => rd.RotateBody(rb, centre, axis, angle, rc), Placement.Turn(centre, axis, angle));
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _lastRotate = (before, body, centre, axis, copy, Math.Sign(angle) == 0 ? 1 : Math.Sign(angle), tilt);
            _last = null; _lastMove = null; _selected = null;
            Render();
            MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = deg.ToString("0.#", CultureInfo.CurrentCulture);
            StatusText.Text = T($"✅ {(tilt != null ? "Gekanteld" : copy ? "Gedraaide kopie" : "Gedraaid")}: {deg.ToString("0.#", CultureInfo.CurrentCulture)}°. Typ een andere hoek + Enter om het aan te passen.", $"✅ {(tilt != null ? "Tilted" : copy ? "Rotated copy" : "Rotated")}: {deg.ToString("0.#", CultureInfo.CurrentCulture)}°. Type another angle + Enter to change it.");
            Log(string.Create(CultureInfo.InvariantCulture, $"rotate {(tilt is { } lt ? $"face {lt.Face} of" : "")} body {body}{(copy ? " (copy)" : "")} {deg:0.###}° round ({axis.X:0.###},{axis.Y:0.###},{axis.Z:0.###}) at ({centre.X * Display:0.###},{centre.Y * Display:0.###},{centre.Z * Display:0.###}): ok"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ rounding sharp edges (as DesignSpark)

    /// <summary>The edges chosen to round (one body), before the radius is given.</summary>
    private (int Body, List<int> Edges)? _roundSel;

    /// <summary>The edge clicked to give the radius with the mouse (the chosen ones go with it), the radius now (metres)
    /// and whether it works.</summary>
    private (int Body, int Edge)? _roundEdge;
    private double _roundRadius;
    private bool _roundOk;

    /// <summary>The last rounding made (to change its radius by typing another one).</summary>
    private (SketchDocument Before, int Body, List<int> Edges, double Radius)? _lastRound;

    /// <summary>The chosen edges (and the one under the mouse) shown.</summary>
    private void ShowRoundEdges(int? hoverBody = null, int? hoverEdge = null)
    {
        if (_doc == null) return;
        var lines = new List<Vec3>();
        void AddEdge(int body, int edge)
        {
            var pts = FaceGeometry.EdgePoints(_doc.Bodies[body], _doc.Bodies[body].Edges[edge]);
            for (int i = 0; i + 1 < pts.Count; i++) { lines.Add(pts[i]); lines.Add(pts[i + 1]); }
        }
        if (_roundSel is { } sel) foreach (int e in sel.Edges) AddEdge(sel.Body, e);
        if (hoverBody is int hb && hoverEdge is int he) AddEdge(hb, he);
        if (_sketch != null) { View.Children.Remove(_sketch); _sketch = null; }
        _sketchShown = null;
        if (lines.Count == 0) return;
        var pts3 = new Point3DCollection(lines.Select(v => TowardEye(P(v), 2e-3)));
        _sketch = new LineSet { Points = pts3, Color = SelectColor, Thickness = 3.5 };
        View.Children.Add(_sketch);
    }

    private string RoundSelText() => _roundSel is { } sel && sel.Edges.Count > 0
        ? T($"{sel.Edges.Count} {(sel.Edges.Count == 1 ? "rand" : "randen")} gekozen · klik een rand om de straal met de muis te geven, of typ de straal + Enter · ", $"{sel.Edges.Count} {(sel.Edges.Count == 1 ? "edge" : "edges")} selected · click an edge to give the radius with the mouse, or type the radius + Enter · ")
        : "";

    private void HoverRound((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        var e = EdgeAt(hit, pos);
        ShowRoundEdges(e?.Body, e?.Edge);
        StatusText.Text = "◠ " + RoundSelText() + (e is { } he && _doc!.Bodies[he.Body].Edges[he.Edge].Curve is not BrepLine
            ? T("alleen rechte randen kunnen (nog) afgerond worden.", "only straight edges can be rounded (for now).")
            : T("klik een rand · Shift: rand erbij/eraf · Ctrl: alle randen van het vlak · Ctrl+Shift: alle randen van het lichaam.", "click an edge · Shift: add/remove an edge · Ctrl: all edges of the face · Ctrl+Shift: all edges of the body."));
    }

    /// <summary>A click with ◠: Shift toggles an edge, Ctrl takes all edges of the face (with Shift: of the body), a plain
    /// click on an edge starts giving the radius with the mouse.</summary>
    private void StartRound((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_doc == null || hit is not { } h) return;
        var body = _doc.Bodies[h.Face.Body];
        bool shift = (Mods & ModifierKeys.Shift) != 0, ctrl = (Mods & ModifierKeys.Control) != 0;
        bool Straight(int e) => body.Edges[e].Curve is BrepLine && body.Edges[e].Start >= 0;
        var sel = _roundSel is { } s0 && s0.Body == h.Face.Body ? s0.Edges : [];
        if (ctrl)
        {
            var add = (shift ? Enumerable.Range(0, body.Edges.Count) : body.Faces[h.Face.Face].Loops.SelectMany(l => l).Select(c => c.Edge)).Where(Straight);
            _roundSel = (h.Face.Body, sel.Union(add).ToList());
            _lastRound = null;
            ShowRoundEdges();
            StatusText.Text = "◠ " + RoundSelText();
            return;
        }
        if (EdgeAt(hit, pos) is not { } e) return;
        if (!Straight(e.Edge)) { StatusText.Text = T("◠ Alleen een rechte rand kan (nog) afgerond worden.", "◠ Only a straight edge can be rounded (for now)."); return; }
        if (shift)
        {
            var list = sel.ToList();
            if (!list.Remove(e.Edge)) list.Add(e.Edge);
            _roundSel = (e.Body, list);
            _lastRound = null;
            ShowRoundEdges();
            StatusText.Text = "◠ " + (list.Count > 0 ? RoundSelText() : T("Geen randen gekozen.", "No edges selected."));
            return;
        }
        if (!sel.Contains(e.Edge)) _roundSel = (e.Body, sel.Append(e.Edge).ToList());
        _roundEdge = e; _roundRadius = 0; _roundOk = false; _typed = "";
        _last = null; _lastMove = null; _lastOffset = null; _lastRound = null; _lastRotate = null; _lastScale = null; _selected = null; _hover = null;
        UpdateHighlight();
        ShowRoundEdges();
        MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = "";
        StatusText.Text = T("◠ Beweeg de muis van de rand af voor de straal; klik om vast te zetten, of typ de straal + Enter. Esc breekt af.", "◠ Move the mouse away from the edge for the radius; click to set it, or type the radius + Enter. Esc cancels.");
    }

    /// <summary>A sharp straight edge between two flat faces within 6 pixels of the mouse (for ⇕: a click rounds it).</summary>
    private (int Body, int Edge)? SharpEdgeNear((FaceRef Face, Point3D Point)? hit, Point pos)
    {
        if (_doc == null || EdgeAt(hit, pos) is not { } e) return null;
        var body = _doc.Bodies[e.Body];
        if (body.Edges[e.Edge].Curve is not BrepLine || ScreenDistanceToEdge(body, e.Edge, pos) > 6) return null;
        var faces = Enumerable.Range(0, body.Faces.Count).Where(f => body.Faces[f].Loops.Any(l => l.Any(c => c.Edge == e.Edge))).ToList();
        if (faces.Count != 2 || faces.Any(f => body.Faces[f].Surface is not BrepPlane)) return null;
        double cos = FaceGeometry.Normal(body.Faces[faces[0]]).Dot(FaceGeometry.Normal(body.Faces[faces[1]]));
        return cos > 1 - 1e-6 ? null : e;
    }

    private void EndRound()
    {
        HideTag();
        _roundEdge = null; _roundOk = false; _roundSel = null;
        ShowSketch([]);
    }

    /// <summary>The radius as far as the mouse (on the face under it) is from the clicked edge, in steps of 0.1 mm;
    /// shown.</summary>
    private void RoundTo(Point pos)
    {
        if (_roundEdge is not { } re || _roundSel is not { } sel || _doc == null || _typed.Length > 0) return;
        var body = _doc.Bodies[re.Body];
        var ed = body.Edges[re.Edge];
        Vec3 a = body.Vertices[ed.Start], ab = body.Vertices[ed.End] - a;
        if (FaceAt(pos) is not { } h) return;
        var p = V(h.Point);
        double t = (p - a).Dot(ab) / ab.Dot(ab);
        double r = Math.Round((a + ab * t - p).Length * Display * 10) / 10 / Display;
        if (r < 1e-7) { _roundOk = false; Render(); MeasureBox.Text = "0"; return; }
        if (Math.Abs(r - _roundRadius) < 1e-12 && _roundOk) return;
        _roundRadius = r;
        MeasureBox.Text = Mm(r);
        try
        {
            var (doc, _) = _doc.RoundEdges(sel.Body, sel.Edges, r);
            _roundOk = true;
            Render(doc);
            StatusText.Text = T($"◠ Straal {Mm(r)} mm · klik om vast te zetten, of typ de straal + Enter. Esc breekt af.", $"◠ Radius {Mm(r)} mm · click to set it, or type the radius + Enter. Esc cancels.");
            ShowTag("R " + Mm(r) + T(" mm", " mm"), pos);
        }
        catch (PushPullException ex) { _roundOk = false; Render(); StatusText.Text = "⛔ " + ex.Message; }
    }

    private void FinishRound()
    {
        if (_roundSel is not { } sel || _doc is not { } before || !_roundOk) return;
        double r = _roundRadius;
        EndRound();
        ApplyRound(before, sel.Body, sel.Edges, r, replaceLast: false);
    }

    private void ApplyRound(SketchDocument before, int body, List<int> edges, double radius, bool replaceLast)
    {
        string which = edges.Count == 1 ? $"edge {edges[0]}" : $"{edges.Count} edges";
        try
        {
            var (doc, faces) = before.RoundEdges(body, edges, radius);
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _last = null; _lastMove = null;
            _lastRound = (before, body, edges, radius);
            _selected = faces[0];
            Render();
            MeasureLabel.Text = T("Straal", "Radius"); MeasureBox.Text = Mm(radius);
            StatusText.Text = T($"✅ {(edges.Count == 1 ? "Afgerond" : $"{edges.Count} randen afgerond")} met R {Mm(radius)} mm. Typ een andere straal + Enter om het aan te passen.", $"✅ {(edges.Count == 1 ? "Rounded" : $"{edges.Count} edges rounded")} with R {Mm(radius)} mm. Type another radius + Enter to change it.");
            Log(string.Create(CultureInfo.InvariantCulture, $"round {which} of body {body} r {radius * Display:0.###} mm: ok, {faces.Count} new faces"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"round {which} of body {body} r {radius * Display:0.###} mm: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ offset (as in SketchUp)

    /// <summary>The face being offset (after the first click), the distance now (metres) and whether it works.</summary>
    private FaceRef? _offsetFace;
    private double _offsetDist;
    private bool _offsetOk;

    /// <summary>The last offset (to change it by typing another distance).</summary>
    private (SketchDocument Before, FaceRef Face, double Dist)? _lastOffset;

    private void StartOffset(FaceRef f)
    {
        _offsetFace = f; _offsetDist = 0; _offsetOk = false; _typed = "";
        _selected = null; _hover = null; _lastOffset = null;
        UpdateHighlight();
        MeasureLabel.Text = T("Afstand", "Distance"); MeasureUnit.Text = "mm";
        MeasureBox.Text = "0";
        StatusText.Text = T("▣ Beweeg de muis naar binnen; klik om vast te zetten, of typ de afstand + Enter. Esc breekt af.", "▣ Move the mouse inwards; click to set it, or type the distance + Enter. Esc cancels.");
    }

    private void EndOffset()
    {
        if (_offsetFace == null) return;
        _offsetFace = null; _offsetOk = false;
        ShowSketch([]);
    }

    /// <summary>The offset as far in as the mouse is from the face's outer edge (in steps of 0.1 mm), shown as a sketch.</summary>
    private void OffsetTo(Point pos)
    {
        if (_offsetFace is not { } f || _doc == null || _typed.Length > 0) return;
        if (OnPlane(f, pos) is not { } p) return;
        var body = _doc.Bodies[f.Body];
        var face = body.Faces[f.Face];
        _offsetOk = false;
        // (outside the face: outward, onto the face round it)
        bool outward = !PushPullMe.Core.Drawing.Contains(body, f.Face, p);
        var outline = FaceGeometry.LoopPoints(body, face.Loops[0]);
        double d = double.MaxValue;
        for (int i = 0; i < outline.Count; i++)
        {
            Vec3 a = outline[i], ab = outline[(i + 1) % outline.Count] - a;
            double t = ab.Dot(ab) < 1e-24 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0, 1);
            d = Math.Min(d, (a + ab * t - p).Length);
        }
        _offsetDist = Math.Round(d * Display * 10) / 10 / Display * (outward ? -1 : 1);
        MeasureBox.Text = Mm(_offsetDist);
        try
        {
            var (corners, circle) = PushPullMe.Core.Drawing.OffsetOutline(body, f.Face, _offsetDist);
            if (outward) PushPullMe.Core.Drawing.OffsetTarget(body, f.Face, corners, circle);
            var n = NormalOf(f);
            var pts = corners ?? Enumerable.Range(0, 60).Select(i => circle!.Centre + (circle.XAxis * Math.Cos(i * Math.PI / 30) + circle.Normal.Cross(circle.XAxis) * Math.Sin(i * Math.PI / 30)) * circle.Radius).ToList();
            ShowSketch(pts, n, closed: true);
            _offsetOk = true;
            StatusText.Text = outward
                ? T($"▣ {Mm(-_offsetDist)} mm naar buiten · klik om vast te zetten, of typ de afstand (min) + Enter. Esc breekt af.", $"▣ {Mm(-_offsetDist)} mm outwards · click to set it, or type the distance (minus) + Enter. Esc cancels.")
                : T($"▣ {Mm(_offsetDist)} mm naar binnen · klik om vast te zetten, of typ de afstand + Enter. Esc breekt af.", $"▣ {Mm(_offsetDist)} mm inwards · click to set it, or type the distance + Enter. Esc cancels.");
        }
        catch (PushPullException ex)
        {
            ShowSketch([]);
            StatusText.Text = "⛔ " + ex.Message;
        }
    }

    private void FinishOffset()
    {
        if (_offsetFace is not { } f || _doc is not { } before) return;
        if (!_offsetOk) return;
        double dist = _offsetDist;
        EndOffset();
        ApplyOffset(before, f, dist, replaceLast: false);
    }

    private void ApplyOffset(SketchDocument before, FaceRef f, double dist, bool replaceLast)
    {
        try
        {
            var (doc, inner) = before.Offset(f, dist);
            if (replaceLast) { _doc = _history.Replace(before, doc, _openGroup); UpdateUndoButtons(); }
            else Commit(before, doc);
            _last = null; _lastMove = null;
            _lastOffset = (before, f, dist);
            _selected = inner; _lastDrawn = inner;
            Render();
            MeasureBox.Text = Mm(dist);
            StatusText.Text = T($"✅ Nieuwe rand {Mm(dist)} mm naar binnen. Typ een andere afstand + Enter om het aan te passen, of kies ⇕ (P) om het binnenvlak te verschuiven.", $"✅ New edge {Mm(dist)} mm inwards. Type another distance + Enter to change it, or choose ⇕ (P) to move the inner face.");
            Log(string.Create(CultureInfo.InvariantCulture, $"offset face {f.Face} {dist * Display:0.###} mm: ok, new face {inner.Face}"));
        }
        catch (PushPullException ex)
        {
            Render();
            ShowToast(T("⛔ Dat lukt niet", "⛔ That does not work"), ex.Message);
            StatusText.Text = "⛔ " + ex.Message;
            Log(string.Create(CultureInfo.InvariantCulture, $"offset face {f.Face} {dist * Display:0.###} mm: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ keyboard: tools, undo, the measurements box

    /// <summary>Text typed in the window (not in a box of its own): for the measurements box; whether it was taken.</summary>
    public bool TextInput(string text) => text.Length == 1 && TypeChar(text[0]);

    /// <summary>A typed character for the measurements box; whether it was taken.</summary>
    private bool TypeChar(char ch)
    {
        if (_doc == null) return false;
        // drawing: before the first click (over a face) or after it
        bool drawing = DrawMode && (_drawFace != null || _drawHover != null || _gKind != null);
        // (the work plane: a shift along its normal, either way, or x;y within it)
        if (WorkPlaneMode && (char.IsDigit(ch) || ch is ',' or '.' || (ch == '-' && (_typed.Length == 0 || _typed.EndsWith(';'))) || (ch == ';' && !_typed.Contains(';'))))
        {
            _typed += ch;
            MeasureBox.Text = _typed;
            return true;
        }
        if (char.IsDigit(ch) || ch == ',' || ch == '.' || (ch == '-' && _typed.Length == 0 && !drawing) || (ch == ';' && drawing && RectMode && !_typed.Contains(';')))
        {
            if (_dragFace == null && _last == null && _movePick == null && _lastMove == null && _tapeEdge == null && _offsetFace == null && _lastOffset == null && _roundEdge == null && _roundSel == null && _lastRound == null && _rotStage == 0 && _lastRotate == null && _scStage == 0 && _lastScale == null && _polyFace == null && !(_arcStage == 2 && !_arc3) && _swFace == null && _rrStage != 2 && _fmStage != 2 && _prStage != 2 && _secFace == null && !Text3DMode && !(PositionCameraMode && _camStand != null) && !drawing) return false;
            _typed += ch;
            MeasureBox.Text = _typed;
            return true;
        }
        return false;
    }

    /// <summary>A key down in the window (<paramref name="inTextBox"/>: while a box of the window has the typing, a label
    /// or a name in the structure tree: then only Ctrl+Z, Y, O and S); whether it was used.</summary>
    public bool KeyDown(Key key, ModifierKeys mods, bool inTextBox = false)
    {
        _mods = mods;
        bool handled = false;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        if (ctrl && key == Key.Z) { Undo(); return true; }
        if (ctrl && key == Key.Y) { Redo(); handled = true; return handled; }
        if (ctrl && key == Key.O) { _ = OpenDialog(); handled = true; return handled; }
        if (ctrl && key == Key.S) { _ = SaveDialog(); handled = true; return handled; }
        if ((Mods & ModifierKeys.Control) != 0 && key == Key.G && !inTextBox)
        {
            if ((Mods & ModifierKeys.Shift) != 0) UngroupPicked(); else MakeGroupOfPicked();
            return true;
        }
        if (ctrl && key == Key.K && !inTextBox) { MakeGroupOfPicked(component: true); return true; }
        // (typing a label or a name in the structure tree: the keys are the box's)
        if (inTextBox) return handled;
        if (ctrl) return false;
        if (WalkMode && WalkKey(key)) { handled = true; return handled; }
        if (WorkPlaneMode && WorkPlaneKey(key)) { handled = true; return handled; }
        switch (key)
        {
            case Key.Escape:
                if (WorkPlaneMode && GripCancel()) { _typed = ""; MeasureBox.Text = ""; }
                else if (_gKind != null)
                {
                    bool line = _gKind == GroundKind.Line && _gPts.Count > 1;
                    EndGround(); _typed = ""; MeasureBox.Text = "";
                    StatusText.Text = line ? T("Klaar met de lijn op de grond.", "Done with the line on the ground.") : T("Afgebroken.", "Cancelled.");
                }
                else if (_dragFace != null) { EndDrag(); _typed = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_movePick != null) { EndMove(); _typed = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_tapeStart != null) { EndTape(); _typed = ""; MeasureBox.Text = ""; StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_offsetFace != null) { EndOffset(); _typed = ""; MeasureBox.Text = ""; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_swFace != null || _rrFace != null) { EndSweep(); EndRotRect(); EndFollow(); _typed = ""; MeasureBox.Text = ""; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_arcFace != null) { EndArc(); _typed = ""; MeasureBox.Text = ""; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_polyFace != null) { EndPolygon(); _typed = ""; MeasureBox.Text = ""; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_scStage > 0) { EndScale(); _typed = ""; MeasureBox.Text = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_rotStage > 0) { EndRotate(); _typed = ""; MeasureBox.Text = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_roundEdge != null || _roundSel != null) { EndRound(); _typed = ""; MeasureBox.Text = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_drawFace != null)
                {
                    // (a line: only the piece at the mouse goes; the ones clicked stay, as loose lines, as in SketchUp)
                    int kept = LineMode ? _chainLines.Count : 0;
                    EndDraw(); _typed = ""; MeasureBox.Text = ""; UpdateHighlight();
                    StatusText.Text = LineMode ? (kept > 0 ? T($"Klaar met de lijn: {kept} {(kept == 1 ? "losse lijn" : "losse lijnen")}. Ctrl+Z haalt er één weg.", $"Done with the line: {kept} {(kept == 1 ? "loose line" : "loose lines")}. Ctrl+Z removes one.") : T("Klaar met de lijn.", "Done with the line.")) : T("Afgebroken.", "Cancelled.");
                }
                else if (_lasso != null) { EndLasso(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_fmFace != null) { EndFollow(); _typed = ""; MeasureBox.Text = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_fhFace != null) { EndFreehand(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_viewAnchor != null) { EndView(); StatusText.Text = T("Gestopt.", "Stopped."); }
                else if (_prStage > 0) { EndProtractor(); _typed = ""; MeasureBox.Text = ""; StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_dimStage > 0) { EndDimension(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_axStage > 0) { _axStage = 0; ShowSketch([]); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_secFace != null) { EndSection(); _section = null; _typed = ""; Render(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (SectionMode && _section != null) { _section = null; Render(); StatusText.Text = T("⊟ Doorsnede weggehaald.", "⊟ Section removed."); }
                else if (_unionFirst != null) { _unionFirst = null; _selected = null; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_mirrorBody != null) { _mirrorBody = null; _selected = null; UpdateHighlight(); StatusText.Text = T("Afgebroken.", "Cancelled."); }
                else if (_typed.Length > 0) { _typed = ""; MeasureBox.Text = ""; }
                else if (_multi != null) { _multi = null; _selected = null; UpdateHighlight(); StatusText.Text = T("Keuze leeg.", "Selection cleared."); }
                else if (_pickedUnits.Count > 0) { _pickedUnits.Clear(); UpdateHighlight(); StatusText.Text = T("Keuze leeg.", "Selection cleared."); }
                else if (_openGroup != null) CloseGroup();
                handled = true;
                break;
            case Key.Back when _typed.Length > 0:
                _typed = _typed[..^1];
                MeasureBox.Text = _typed;
                handled = true;
                break;
            case Key.Enter when _typed.Length > 0:
                TypedDistance();
                handled = true;
                break;
            case Key.P when _typed.Length == 0:
                SetTool(Tool.PushPull); handled = true;
                break;
            case Key.R when _typed.Length == 0:
                SetTool(Tool.Rectangle); handled = true;
                break;
            case Key.C when _typed.Length == 0:
                SetTool(Tool.Circle); handled = true;
                break;
            case Key.B when _typed.Length == 0:
                SetTool(Tool.Paint); handled = true;
                break;
            case Key.T when _typed.Length == 0:
                SetTool(Tool.Tape); handled = true;
                break;
            case Key.Delete when TapeMode && _typed.Length == 0:
                ClearGuides(); EndTape();
                StatusText.Text = T("🧹 Alle hulplijnen zijn weg.", "🧹 All guide lines are gone.");
                Log("guides cleared");
                handled = true;
                break;
            case Key.S when _typed.Length == 0:
                SetTool(Tool.Scale); handled = true;
                break;
            case Key.Up or Key.Down when PolygonMode:
                // more or fewer corners (3 to 24)
                _polySides = Math.Clamp(_polySides + (key == Key.Up ? 1 : -1), 3, 24);
                if (_polyFace != null) PolygonTo(_mouseAt);
                else StatusText.Text = T($"⬡ Veelhoek ({_polySides} hoeken; ↑/↓: meer/minder): klik het middelpunt op een plat vlak.", $"⬡ Polygon ({_polySides} sides; ↑/↓: more/fewer): click the centre on a flat face.");
                handled = true;
                break;
            case Key.Right or Key.Left or Key.Up when _mirrorBody != null:
            {
                // the plane through the middle of the body, square to red, green or blue
                int body = _mirrorBody.Value;
                var dir = Axes[key switch { Key.Right => 0, Key.Left => 1, _ => 2 }].Dir;
                // (of the whole group when it is in a closed one)
                var those = GroupTaken(body) is { } mgr ? _doc!.Members(mgr).ToHashSet() : [body];
                var pts = _doc!.AllTriangles().Where(x => those.Contains(x.Face.Body)).SelectMany(x => x.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
                var lo = new Vec3(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)); var hi = new Vec3(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z));
                MirrorBodyIn(body, (lo + hi) * 0.5, dir, "its middle");
                handled = true;
                break;
            }
            case Key.Right or Key.Left or Key.Up or Key.Down when _scStage > 0:
                // only along the red, green or blue axis; down: all directions again
                _scAxis = key switch { Key.Right => Axes[0].Dir, Key.Left => Axes[1].Dir, Key.Up => Axes[2].Dir, _ => null };
                ScaleTo(_mouseAt);
                handled = true;
                break;
            case Key.Q when _typed.Length == 0:
                SetTool(Tool.Rotate); handled = true;
                break;
            case Key.Right or Key.Left or Key.Up or Key.Down when _rotStage > 0 && _rotTilt == null:
                // the axis to turn round (as SketchUp: right red, left green, up blue), down: square to the face again
                SetRotateAxis(key switch { Key.Right => Axes[0].Dir, Key.Left => Axes[1].Dir, Key.Up => Axes[2].Dir, _ => _rotFaceAxis });
                handled = true;
                break;
            case Key.K when _typed.Length == 0:
                SetTool(Tool.Round); handled = true;
                break;
            case Key.F when _typed.Length == 0:
                SetTool(Tool.Offset); handled = true;
                break;
            case Key.M when _typed.Length == 0:
                SetTool(Tool.Move); handled = true;
                break;
            case Key.Right or Key.Left or Key.Up or Key.Down when LineMode && _drawFace != null:
                _lineAxis = key switch { Key.Right => _lineAxis == 0 ? null : 0, Key.Left => _lineAxis == 1 ? null : 1, Key.Up => _lineAxis == 2 ? null : 2, _ => null };
                DrawTo(_mouseAt);
                handled = true;
                break;
            case Key.Right or Key.Left or Key.Up or Key.Down when _movePick != null:
                // an axis to hold to (as in SketchUp: right red, left green, up blue), down lets go
                _moveAxis = key switch { Key.Right => _moveAxis == 0 ? null : 0, Key.Left => _moveAxis == 1 ? null : 1, Key.Up => _moveAxis == 2 ? null : 2, _ => null };
                MoveTo(_mouseAt);
                handled = true;
                break;
            case Key.E when _typed.Length == 0:
                SetTool(Tool.Eraser); handled = true;
                break;
            case Key.G when _typed.Length == 0:
                SetTool(Tool.Polygon); handled = true;
                break;
            case Key.A when _typed.Length == 0:
                SetTool(Tool.Arc); handled = true;
                break;
            case Key.L when _typed.Length == 0:
                SetTool(Tool.Line); handled = true;
                break;
            case Key.Space when _typed.Length == 0:
                SetTool(Tool.Select); handled = true;
                break;
        }
        return handled;
    }

    /// <summary>A typed distance: for the push/pull in progress (in the direction it was moving), or to change the last
    /// one.</summary>
    private void TypedDistance()
    {
        if (_gKind != null) { string gt = _typed; _typed = ""; GroundTyped(gt); return; }
        if (WorkPlaneMode) { string wt = _typed; _typed = ""; if (!GripTyped(wt)) WorkPlaneTyped(wt); return; }
        if (DrawMode && (_drawFace != null || _drawHover != null))
        {
            string typed = _typed;
            _typed = "";
            TypedDraw(typed);
            return;
        }
        string t = _typed.Replace(',', '.');
        _typed = "";
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) || mm == 0) { MeasureBox.Text = ""; return; }
        double dist = mm / Display;
        if (_tapeEdge != null) { TypedTape(mm); return; }
        if (_swFace is { } sface && _doc is { } sdoc)
        {
            if (_swStage == 1)
            {
                // the radius: the start the way the mouse points (or along the face's axis)
                var dir = _swDir.Length > 1e-12 ? _swDir : RectAxis(NormalOf(sface));
                _swStart = _swCentre + dir * (Math.Abs(mm) / Display); _swStage = 2; _swAngle = 0;
                StatusText.Text = T($"{SweepIcon} Beweeg voor de hoek en klik, of typ de hoek + Enter. Esc breekt af.", $"{SweepIcon} Move for the angle and click, or type the angle + Enter. Esc cancels.");
                MeasureLabel.Text = T("Hoek", "Angle"); MeasureUnit.Text = "°"; MeasureBox.Text = "";
                return;
            }
            double ang = Math.Abs(mm) * Math.PI / 180 * (_swAngle < 0 ? -1 : 1) * (mm < 0 ? -1 : 1);
            var (sc, ss, pie) = (_swCentre, _swStart, PieMode);
            EndSweep();
            ApplySweep(sdoc, sface, sc, ss, ang, pie);
            return;
        }
        if (_secFace != null)
        {
            SetSection(Math.Abs(mm) / Display);
            return;
        }
        if (Text3DMode)
        {
            // after the letters: their thickness (pulled out); before: the letter height
            if (_letters != null) ExtrudeLetters(mm / Display);
            else { _letterHeight = Math.Abs(mm) / Display; StatusText.Text = T($"A Letterhoogte {Mm(_letterHeight)} mm. Klik een punt op een plat vlak.", $"A Letter height {Mm(_letterHeight)} mm. Click a point on a flat face."); }
            return;
        }
        if (_prStage == 2)
        {
            double sign = _prAngle < 0 ? -1 : 1;
            AddProtractorGuide(Math.Abs(mm) * Math.PI / 180 * sign * (mm < 0 ? -1 : 1));
            return;
        }
        if (PositionCameraMode && _camStand is { } stand)
        {
            _eyeHeight = Math.Abs(mm);
            StandAt(stand);
            return;
        }
        if (_fmFace is { } fface && _fmStage == 2 && _fmBase is { } fdoc)
        {
            var (fp, fa) = (_fmAxisP, _fmAxisDir);
            EndFollow();
            ApplyFollow(fdoc, fface, fp, fa, Math.Abs(mm) * Math.PI / 180);
            return;
        }
        if (_rrFace is { } rface && _rrStage == 2 && _doc is { } rdoc)
        {
            double wdt = Math.Abs(mm) / Display * (_rrWidth < 0 ? -1 : 1) * (mm < 0 ? -1 : 1);
            var (rrA, rrB) = (_rrA, _rrB);
            EndRotRect();
            ApplyRotRect(rdoc, rface, rrA, rrB, wdt);
            return;
        }
        if (_arcFace is { } aface && _arcStage == 2 && !_arc3 && _doc is { } adoc)
        {
            // the bulge (mm), on the side the mouse is on now
            double bulge = Math.Abs(mm) / Display * (_arcBulge < 0 ? -1 : 1) * (mm < 0 ? -1 : 1);
            var (aa, ab) = (_arcA, _arcB);
            EndArc();
            ApplyArc(adoc, aface, aa, ab, bulge);
            return;
        }
        if (_polyFace is { } pface && _doc is { } pdoc)
        {
            // the radius (mm), the first corner the way the mouse points now (or along the face's axis)
            var dir = _polyDir.Length > 1e-12 ? _polyDir : RectAxis(NormalOf(pface));
            var c = _polyCentre;
            EndPolygon();
            ApplyPolygon(pdoc, pface, c, dir, Math.Abs(mm) / Display);
            return;
        }
        if (_scStage > 0 && _scBase is { } scBefore)
        {
            var (sb, sf, sa, sc) = (_scBody, _scFixed, _scAxis, _scCopy);
            EndScale();
            ApplyScale(scBefore, sb, sf, Math.Abs(mm), sa, sc, replaceLast: false);
            return;
        }
        if (_dragFace == null && _movePick == null && _lastScale is { } lsc)
        {
            ApplyScale(lsc.Before, lsc.Body, lsc.Fixed, Math.Abs(mm), lsc.Along, lsc.Copy, replaceLast: true);
            return;
        }
        if (_rotStage > 0 && _rotBase is { } rotBefore)
        {
            // degrees (the way the mouse turns it now, if it does)
            double ang = Math.Abs(mm) * Math.PI / 180 * (_rotStage == 2 && _rotAngle < 0 ? -1 : 1) * (mm < 0 ? -1 : 1);
            var (rb0, rc, ra, rcopy, rtilt) = (_rotBody, _rotCentre, _rotAxis, _rotCopy, _rotTilt);
            EndRotate();
            ApplyRotate(rotBefore, rb0, rc, ra, ang, rcopy, replaceLast: false, rtilt);
            return;
        }
        if (_dragFace == null && _movePick == null && _lastRotate is { } lrot)
        {
            ApplyRotate(lrot.Before, lrot.Body, lrot.Centre, lrot.Axis, Math.Abs(mm) * Math.PI / 180 * lrot.Sign * (mm < 0 ? -1 : 1), lrot.Copy, replaceLast: true, lrot.Tilt);
            return;
        }
        if (_roundSel is { } rs && rs.Edges.Count > 0 && _doc is { } rb) { EndRound(); ApplyRound(rb, rs.Body, rs.Edges, Math.Abs(dist), replaceLast: false); return; }
        if (_dragFace == null && _movePick == null && _lastRound is { } lr) { ApplyRound(lr.Before, lr.Body, lr.Edges, Math.Abs(dist), replaceLast: true); return; }
        if (_offsetFace is { } of && _doc is { } ob) { EndOffset(); ApplyOffset(ob, of, Math.Abs(dist), replaceLast: false); return; }
        if (_dragFace == null && _movePick == null && _lastOffset is { } lo) { ApplyOffset(lo.Before, lo.Face, Math.Abs(dist), replaceLast: true); return; }
        if (_movePick is { } mp && _moveBase is { } mb)
        {
            // along the way it is going now (or the axis held)
            var d = _moveAxis is int ax ? Axes[ax].Dir * (_moveOffset.Dot(Axes[ax].Dir) < 0 ? -1 : 1) : _moveOffset.Length > 1e-12 ? _moveOffset.Normalized() : default;
            if (d.Length < 0.5) { StatusText.Text = T("✥ Beweeg eerst de muis in de richting om te verplaatsen.", "✥ First move the mouse in the direction to move."); return; }
            int? group = _moveGroup;
            EndMove();
            ApplyMove(mb, mp.Body, mp.Pick, d * Math.Abs(dist), replaceLast: false, group);
            return;
        }
        if (_dragFace == null && _lastMove is { } lm)
        {
            ApplyMove(lm.Before, lm.Body, lm.Pick, lm.Dir * (dist < 0 ? -1 : 1) * Math.Abs(dist), replaceLast: true, lm.Group);
            return;
        }
        if (_dragFace is { } f && _dragBase is { } before)
        {
            // (a pin, hole or rounding: the typed value is its new radius)
            if (_dragRadius && !RadiusDistance(before, f, mm, out dist)) return;
            else if (!_dragRadius && _dragDist < 0 && dist > 0) dist = -dist;
            EndDrag();
            Apply(before, f, dist, replaceLast: false);
        }
        else if (_last is { } last)
        {
            if (HasRadius(last.Before, last.Face)) { if (!RadiusDistance(last.Before, last.Face, mm, out dist)) return; }
            else if (last.Dist < 0 && dist > 0) dist = -dist;
            Apply(last.Before, last.Face, dist, replaceLast: true);
        }
    }

    /// <summary>The push/pull distance that gives a pin, hole or rounding a radius of <paramref name="radiusMm"/> mm; false (with the
    /// reason shown) when that cannot be.</summary>
    private bool RadiusDistance(SketchDocument doc, FaceRef f, double radiusMm, out double dist)
    {
        dist = 0;
        if (radiusMm <= 0) { StatusText.Text = T("⛔ Een straal moet groter dan 0 zijn.", "⛔ A radius must be more than 0."); MeasureBox.Text = ""; return false; }
        try { dist = PushPullMe.Core.PushPull.DistanceForRadius(doc.Bodies[f.Body], f.Face, radiusMm / Display); return true; }
        catch (PushPullException ex) { StatusText.Text = "⛔ " + ex.Message; return false; }
    }

    // ------------------------------------------------------------------ messages


    private static void Log(string what) => SessionLog.Write(what);

    // ------------------------------------------------------------------ test aid (no clicking in the user's window)

    /// <summary>
    /// PPM_OPEN=file: opens it; PPM_DRAW="rect:face:x,y,z:x,y,z;line:…" draws on faces of body 0 (corners or ends in mm);
    /// PPM_PUSH="face:mm;face:mm" pushes/pulls those faces of body 0 one after the other (face "new" = the last drawn one, face
    /// "top" = the largest flat face facing up); PPM_SAVE=file saves; PPM_SNAPSHOT=png renders the window to it and closes.
    /// </summary>
    private async Task TestAid()
    {
        if (Environment.GetEnvironmentVariable("PPM_OPEN") is not { Length: > 0 } open) return;
        string? shot = Environment.GetEnvironmentVariable("PPM_SNAPSHOT");
        var report = new System.Text.StringBuilder();
        try
        {
            if (Environment.GetEnvironmentVariable("PPM_VIA_CONVERTER") == "1")
            {
                // as the user opens a mesh: in the converter's window (its own test aid, RSUI_* with RSUI_TAKE=1, does the
                // work and hands over)
                await Open(open);
                report.AppendLine($"converter: {StatusText.Text}");
            }
            else if (open == "new") NewDocument();
            else await Load(open);
            report.AppendLine($"open: {StatusText.Text}");
            if (Environment.GetEnvironmentVariable("PPM_DRAW") is { Length: > 0 } draws && _doc != null)
                foreach (var step in draws.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    // rect|line:face:x,y,z:x,y,z (mm)
                    var parts = step.Split(':');
                    int face = parts[1] == "top" ? TopFace() : int.Parse(parts[1]);
                    Vec3 Pt(string s) { var c = s.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture) / Display).ToArray(); return new Vec3(c[0], c[1], c[2]); }
                    var f = new FaceRef(0, face);
                    var u = RectAxis(FaceGeometry.Normal(_doc!.Bodies[0].Faces[face]));
                    DrawShape(_doc, f, Pt(parts[2]), Pt(parts[3]), u, parts[0] switch { "rect" => DrawKind.Rectangle, "circle" => DrawKind.Circle, _ => DrawKind.Line });
                    report.AppendLine($"{step}: {StatusText.Text} | faces {_doc!.Bodies[0].Faces.Count}");
                }
            if (Environment.GetEnvironmentVariable("PPM_PUSH") is { Length: > 0 } pushes && _doc != null)
                foreach (var step in pushes.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = step.Split(':');
                    int face = parts[0] == "top" ? TopFace() : parts[0] == "new" && _lastDrawn is { } ld ? ld.Face : int.Parse(parts[0]);
                    double mm = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Apply(_doc!, new FaceRef(0, face), mm / Display, replaceLast: false);
                    report.AppendLine($"push {face} {mm} mm ({sw.ElapsedMilliseconds} ms): {StatusText.Text} | faces {_doc!.Bodies[0].Faces.Count}, volume {_doc.Volume() * 1e9:0.###} mm³");
                }
            if (Environment.GetEnvironmentVariable("PPM_DRAG") is { Length: > 0 } drag && _doc != null)
            {
                // face:from:to (mm): the mouse grabs the middle of the face and moves along its normal in 0.25 mm steps,
                // through the real hit tests (and the "gelijk met" snap), as dragging does; then the click to finish
                var parts = drag.Split(':');
                int face = parts[0] == "top" ? TopFace() : int.Parse(parts[0]);
                double from = double.Parse(parts[1], CultureInfo.InvariantCulture), to = double.Parse(parts[2], CultureInfo.InvariantCulture);
                SetTool(Tool.PushPull);
                await _window.Idle();
                var fr = new FaceRef(0, face);
                var tris = _doc.TrianglesOf(fr);
                var mid = tris.Select(t => (t.A + t.B + t.C) * (1.0 / 3)).Aggregate((a, b) => a + b) * (1.0 / tris.Count);
                var n = FaceGeometry.Normal(_doc.Bodies[0].Faces[face]);
                StartDrag(fr, P(mid));
                var seen = new List<string>();
                for (double mm = from; from < to ? mm <= to + 1e-9 : mm >= to - 1e-9; mm += from < to ? 0.25 : -0.25)
                {
                    var pos = Viewport3DHelper.Point3DtoPoint2D(View.Viewport, P(mid + n * (mm / Display)));
                    DragTo(pos);
                    await _window.Idle();
                    string line = $"{_dragDist * Display:0.##} mm: {StatusText.Text}";
                    if (seen.Count == 0 || seen[^1] != line) seen.Add(line);
                }
                foreach (var l in seen) report.AppendLine("drag " + l);
                Finish(_dragDist);
                report.AppendLine($"drag done: {StatusText.Text} | faces {_doc!.Bodies[0].Faces.Count}, volume {_doc.Volume() * 1e9:0.###} mm³");
            }
            if (Environment.GetEnvironmentVariable("PPM_SIM") is { Length: > 0 } sim && _doc != null)
            {
                await _window.Idle();
                await Simulate(sim, report);
                report.AppendLine($"sim done: faces {_doc!.Bodies[0].Faces.Count}, volume {_doc.Volume() * 1e9:0.###} mm³");
            }
            if (Environment.GetEnvironmentVariable("PPM_SAVE") is { Length: > 0 } save) { Save(save); report.AppendLine($"saved {save}"); }
        }
        catch (Exception ex) { report.AppendLine("error: " + ex); }
        if (shot is { Length: > 0 })
        {
            File.WriteAllText(shot + ".txt", report.ToString());
            await Snapshot(shot);
        }
    }

    private int TopFace()
    {
        var body = _doc!.Bodies[0];
        return Enumerable.Range(0, body.Faces.Count).Where(f => body.Faces[f].Surface is BrepPlane && FaceGeometry.Normal(body.Faces[f]).Z > 0.999)
            .MaxBy(f => _doc.TrianglesOf(new FaceRef(0, f)).Sum(t => (t.B - t.A).Cross(t.C - t.A).Length));
    }

    /// <summary>Renders the window's own content to a PNG and closes (no screen capture involved).</summary>
    private async Task Snapshot(string path)
    {
        await _window.SaveWindowPng(path);
        _window.CloseWindow();
    }

    /// <summary>
    /// PPM_SIM: mouse and keys played in this window, steps split by '|': "tool:R" (P, R, L, S), "move:x,y,z" and
    /// "click:x,y,z" (a world point in mm, through the real hit tests), "type:text" (characters for the box), "enter",
    /// "esc", "shot:file.png" (this window as it is now).
    /// </summary>
    private async Task Simulate(string script, System.Text.StringBuilder report)
    {
        foreach (var step in script.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = step.IndexOf(':');
            string what = colon < 0 ? step : step[..colon], arg = colon < 0 ? "" : step[(colon + 1)..];
            // (no keys held but the ones the script plays)
            _mods = ModifierKeys.None;
            Point Screen() { var c = arg.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return Viewport3DHelper.Point3DtoPoint2D(View.Viewport, new Point3D(c[0], c[1], c[2])); }
            switch (what)
            {
                case "shell": OuterShell(); break;
                case "note": TypeNote(arg); break;
                case "tool": SetTool(arg switch { "P" => Tool.PushPull, "R" => Tool.Rectangle, "L" => Tool.Line, "C" => Tool.Circle, "E" => Tool.Eraser, "M" => Tool.Move, "T" => Tool.Tape, "B" => Tool.Paint, "F" => Tool.Offset, "K" => Tool.Round, "Q" => Tool.Rotate, "S" => Tool.Scale, "LASSO" => Tool.Lasso, "G" => Tool.Polygon, "A" => Tool.Arc, "CARC" => Tool.CentreArc, "PIE" => Tool.Pie, "RRECT" => Tool.RotatedRectangle, "ARC3" => Tool.Arc3, "FOLLOW" => Tool.FollowMe, "FREEHAND" => Tool.Freehand, "ZOOM" => Tool.Zoom, "ZOOMWIN" => Tool.ZoomWindow, "CAMERA" => Tool.PositionCamera, "LOOK" => Tool.LookAround, "WALK" => Tool.Walk, "PROTRACTOR" => Tool.Protractor, "SECTION" => Tool.Section, "TAG" => Tool.Hide, "DIM" => Tool.Dimension, "TEXT" => Tool.Text, "AXES" => Tool.Axes, "TEXT3D" => Tool.Text3D, "UNION" => Tool.Union, "SUBTRACT" => Tool.Subtract, "INTERSECT" => Tool.Intersect, "TRIM" => Tool.Trim, "SPLIT" => Tool.Split, "MIRROR" => Tool.Mirror, "PLANE" => Tool.WorkPlane, _ => Tool.Select }); break;
                case "key": KeyDown(Enum.Parse<Key>(arg), Mods); break;
                case "move": MouseMoveAt(Screen()); break;
                case "mods": _simMods = arg switch { "ctrl" => ModifierKeys.Control, "shift" => ModifierKeys.Shift, "ctrl+shift" => ModifierKeys.Control | ModifierKeys.Shift, _ => null }; break;
                case "click": ClickAt(Screen()); break;
                case "dblclick": if (!DoubleClickAt(Screen())) ClickAt(Screen()); break;
                case "down": PressAt(Screen()); break;
                case "up": ReleaseAt(Screen()); break;
                case "probe":
                {
                    // where a click on that point lands: the face and the point hit (mm)
                    var sp = Screen(); var h = FaceAt(sp);
                    var back = Viewport3DHelper.GetRay(View, sp);
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  size {View.ActualWidth:0}x{View.ActualHeight:0} ray {back?.Origin} → {back?.Direction}"));
                    report.AppendLine(h is { } ph ? string.Create(CultureInfo.InvariantCulture, $"  probe {arg} → screen ({sp.X:0.##},{sp.Y:0.##}) face {ph.Face.Body}/{ph.Face.Face} at ({ph.Point.X:0.###},{ph.Point.Y:0.###},{ph.Point.Z:0.###})") : $"  probe {arg} → screen {sp}: nothing");
                    break;
                }
                case "type": foreach (char ch in arg) TypeChar(ch); break;
                case "enter": if (_typed.Length > 0) TypedDistance(); break;
                case "esc": KeyDown(Key.Escape, Mods); break;
                case "shot": await _window.SaveWindowPng(arg); break;
                case "groundbutton": GroundButton(); break;
                case "paint": SetPaint(uint.Parse(arg, NumberStyles.HexNumber)); break;
                case "camera":
                {
                    // camera:x,y,z:dx,dy,dz (mm): where it stands and where it looks
                    var parts = arg.Split(':').Select(p => p.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();
                    if (View.Camera is ProjectionCamera pc)
                    {
                        pc.Position = new Point3D(parts[0][0], parts[0][1], parts[0][2]);
                        pc.LookDirection = new Vector3D(parts[1][0], parts[1][1], parts[1][2]);
                        pc.UpDirection = new Vector3D(0, 0, 1);
                    }
                    break;
                }
                case "push":
                {
                    // push:face:mm (body 0)
                    var parts = arg.Split(':');
                    Apply(_doc!, new FaceRef(0, int.Parse(parts[0])), double.Parse(parts[1], CultureInfo.InvariantCulture) / Display, replaceLast: false);
                    break;
                }
                case "grip":
                {
                    // a click on the middle of a grip of the work plane's handle (ShiftX … TurnN, Ball)
                    var shape = GripShapes()[Enum.Parse<Grip>(arg)];
                    ClickAt(Viewport3DHelper.Point3DtoPoint2D(View, P(shape[shape.Count / 2])));
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  plane origin ({_plane.Origin.X * Display:0.##},{_plane.Origin.Y * Display:0.##},{_plane.Origin.Z * Display:0.##}) normal ({_plane.N.X:0.###},{_plane.N.Y:0.###},{_plane.N.Z:0.###})"));
                    break;
                }
                case "pickgroup": PickGroup(int.Parse(arg)); break;
                case "leveloff":
                {
                    // leveloff:i,j,… — those storeys of the list (from the bottom, as Levels gives them) switched off
                    foreach (var i in arg.Split(',')) SetLevelShown(_levels[int.Parse(i)], false);
                    report.AppendLine("  levels: " + string.Join(", ", _levels.Select((l, i) => $"{i} {l.Name} {l.Elevation:0.00}{(IsLevelShown(l) ? "" : " off")}")));
                    break;
                }
                case "spin":
                {
                    // spin:n — the camera turned n times a little round the vertical through what it looks at, as an
                    // orbit does; the time it took reported
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int k = 0; k < int.Parse(arg); k++)
                    {
                        var cam = View.Camera; var target = cam.Position + cam.LookDirection;
                        double a = Math.PI / 90, ca = Math.Cos(a), sa = Math.Sin(a);
                        var l = cam.LookDirection; var turned = new Vector3D(l.X * ca - l.Y * sa, l.X * sa + l.Y * ca, l.Z);
                        cam.Set(target - turned, turned, cam.UpDirection);
                    }
                    report.AppendLine($"  spin {arg}: {sw.ElapsedMilliseconds} ms, {View.Children.Count} things beside the model");
                    break;
                }
                case "viewfrom":
                {
                    // as a click on the view cube: viewfrom:x,y,z (a side, edge or corner)
                    var d = arg.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    ViewFrom(new Vector3D(d[0], d[1], d[2]), arg);
                    break;
                }
                case "topview":
                    // as the view cube's top: straight down, up along y
                    if (View.Camera is ProjectionCamera tc) { tc.Position = new Point3D(0, 0, 400); tc.LookDirection = new Vector3D(0, 0, -400); tc.UpDirection = new Vector3D(0, 1, 0); }
                    break;
                case "zoomall": ZoomAll(0); break;
                case "camera?":
                    if (View.Camera is ProjectionCamera qc)
                        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  camera ok {CameraOk()} (angle of view {(View.FieldOfViewAcross ? "across" : "up")}): at ({qc.Position.X:0.#},{qc.Position.Y:0.#},{qc.Position.Z:0.#}) look ({qc.LookDirection.X:0.###},{qc.LookDirection.Y:0.###},{qc.LookDirection.Z:0.###}) up ({qc.UpDirection.X:0.###},{qc.UpDirection.Y:0.###},{qc.UpDirection.Z:0.###})"));
                    break;
                case "pan":
                {
                    // dx,dy pixels, at the depth of the middle of the view
                    var d = arg.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    if (View.Camera is ProjectionCamera pcam) _panDepth = pcam.LookDirection.Length;
                    // (the point the camera looks at: at the depth panned, so it should move exactly as far as the mouse)
                    var mid = ((ProjectionCamera)View.Camera).Position + ((ProjectionCamera)View.Camera).LookDirection;
                    var before = Viewport3DHelper.Point3DtoPoint2D(View, mid);
                    Pan(d[0], d[1]);
                    var moved = Viewport3DHelper.Point3DtoPoint2D(View, mid) - before;
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  pan: the middle moved ({moved.X:0.#},{moved.Y:0.#}) px"));
                    break;
                }
                case "wheel":
                {
                    // x,y,z:delta — the wheel turned with the mouse over that point (mm)
                    var parts = arg.Split(':');
                    var c = parts[0].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    var at = Viewport3DHelper.Point3DtoPoint2D(View, new Point3D(c[0], c[1], c[2]));
                    MouseWheel(at, int.Parse(parts[1], CultureInfo.InvariantCulture));
                    var after = Viewport3DHelper.Point3DtoPoint2D(View, new Point3D(c[0], c[1], c[2]));
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  wheel: the point stayed at ({at.X:0.#},{at.Y:0.#}) → ({after.X:0.#},{after.Y:0.#}); look length {((ProjectionCamera)View.Camera).LookDirection.Length:0.#}"));
                    break;
                }
                case "orbit":
                {
                    // dx,dy pixels, in steps of 5 as a mouse would, around the middle of the view
                    var d = arg.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    if (View.Camera is ProjectionCamera oc) _orbitPivot = oc.Position + oc.LookDirection;
                    int steps = (int)Math.Ceiling(Math.Max(Math.Abs(d[0]), Math.Abs(d[1])) / 5);
                    int changes = _cameraChanges; var watch = Stopwatch.StartNew();
                    for (int i = 0; i < steps; i++) Orbit(d[0] / steps, d[1] / steps);
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  orbit: {steps} steps, {watch.Elapsed.TotalMilliseconds / Math.Max(1, steps):0.0} ms per step, {_cameraChanges - changes} camera changes"));
                    if (View.Camera is ProjectionCamera c2) report.AppendLine($"  camera look {c2.LookDirection.X:0.00},{c2.LookDirection.Y:0.00},{c2.LookDirection.Z:0.00} up {c2.UpDirection}");
                    break;
                }
            }
            await _window.Idle();
            report.AppendLine($"{step} → [{MeasureLabel.Text} {MeasureBox.Text}] {StatusText.Text}");
        }
    }
}
