using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Vector3D = System.Windows.Media.Media3D.Vector3D;
using System.Windows.Media.Imaging;
using HelixToolkit.Wpf.SharpDX;
using PushPullMe.Core;
using PushPullMe.Mcp;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe;

/// <summary>
/// The app's side of the MCP server (src/PushPullMe.Mcp): with "AI assistant may work along" ticked, a named pipe of this
/// Windows user takes tool calls from it (any MCP client: Claude Code, Claude Desktop, VS Code, …) and does them here, in
/// this window, as the user would: through the same functions, each change undoable, shown in the status bar and the log.
/// </summary>
public partial class MainWindow
{
    private static string AssistantFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PushPullMe", "assistent.txt");

    /// <summary>The pipe server while it runs (null: off).</summary>
    private CancellationTokenSource? _mcpStop;

    /// <summary>The document as last opened or saved (to know whether there is unsaved work).</summary>
    private SketchDocument? _savedDoc;

    private bool Unsaved => _doc != null && !ReferenceEquals(_doc, _savedDoc) && _undo.Count > 0;

    /// <summary>The choice kept from before (off when there is none); PPM_MCP=1 turns it on for a test window.</summary>
    private void LoadAssistant()
    {
        // (the tools of this build, for the MCP server while this window does not listen)
        PipeProtocol.WriteToolsFile(ToolCatalog.ToJson());
        bool on = Environment.GetEnvironmentVariable("PPM_MCP") == "1";
        try { if (!on && File.Exists(AssistantFile)) on = File.ReadAllText(AssistantFile).Trim() == "aan"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        AssistantBox.IsChecked = on;
        if (on) StartMcp();
        Closed += (_, _) => _mcpStop?.Cancel();
    }

    private void AssistantBox_Click(object sender, RoutedEventArgs e)
    {
        bool on = AssistantBox.IsChecked == true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssistantFile)!);
            File.WriteAllText(AssistantFile, on ? "aan" : "uit");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (on) StartMcp(); else { _mcpStop?.Cancel(); _mcpStop = null; }
        StatusText.Text = on ? T("🤖 Een AI-assistent (via MCP) mag nu in dit venster meewerken. Alles wat hij doet zie je hier en kun je ongedaan maken.", "🤖 An AI assistant (through MCP) may now work along in this window. You see everything it does here and can undo it.")
            : T("🤖 De AI-assistent mag niet meer meewerken.", "🤖 The AI assistant may no longer work along.");
        Log($"assistant {(on ? "on" : "off")}");
    }

    private void StartMcp()
    {
        if (_mcpStop != null) return;
        var stop = _mcpStop = new CancellationTokenSource();
        _ = Task.Run(() => ServeMcp(stop.Token));
    }

    /// <summary>One connection at a time: a request read, done on the UI thread, the answer written.</summary>
    private async Task ServeMcp(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = new NamedPipeServerStream(PipeProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); }
            catch (IOException)
            {
                // (another PushPullMe window has it)
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = T("🤖 Een ander PushPullMe-venster werkt al met de AI-assistent; dit venster niet.", "🤖 Another PushPullMe window already works with the AI assistant; this one does not.");
                    AssistantBox.IsChecked = false; _mcpStop = null;
                });
                return;
            }
            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    var req = await PipeProtocol.ReadAsync(pipe, ct) as JsonObject;
                    if (req == null) continue;
                    // (the MCP server asking which tools there are: no call, nothing shown)
                    if (req["list"]?.GetValue<bool>() == true)
                    {
                        await PipeProtocol.WriteAsync(pipe, new JsonObject { ["ok"] = true, ["tools"] = ToolCatalog.ToJson() }, ct);
                        continue;
                    }
                    var answer = await await Dispatcher.InvokeAsync(() => McpCall(req["client"]?.GetValue<string>() ?? "?", req["tool"]?.GetValue<string>() ?? "", req["args"] as JsonObject ?? new JsonObject()));
                    await PipeProtocol.WriteAsync(pipe, answer, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { }
            }
        }
    }

    private static JsonObject Answer(bool ok, string text, byte[]? png = null)
    {
        var a = new JsonObject { ["ok"] = ok, ["text"] = text };
        if (png != null) a["png"] = Convert.ToBase64String(png);
        return a;
    }

    /// <summary>A refusal the assistant can act on.</summary>
    private sealed class McpRefusal(string message) : Exception(message);

    /// <summary>One tool call, on the UI thread.</summary>
    private async Task<JsonObject> McpCall(string client, string tool, JsonObject args)
    {
        Log($"mcp {client} {tool} {args.ToJsonString()}");
        try
        {
            if (AssistantBox.IsChecked != true) throw new McpRefusal("The user turned 'AI assistant may work along' off.");
            bool reads = tool is "get_model" or "get_body" or "screenshot";
            if (!reads && ActionBusy) throw new McpRefusal(T("De gebruiker is midden in een bewerking; probeer het zo opnieuw.", "The user is in the middle of an action; try again in a moment."));
            if (!reads && tool is not ("open_file" or "undo" or "redo") && _doc == null) throw new McpRefusal(T("Er is geen model open.", "No model is open."));
            var r = await McpDo(client, tool, args);
            return r;
        }
        catch (McpRefusal ex) { return Answer(false, ex.Message); }
        catch (PushPullException ex) { Render(); return Answer(false, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IOException or KeyNotFoundException)
        {
            Render();
            return Answer(false, ex.Message);
        }
    }

    private async Task<JsonObject> McpDo(string client, string tool, JsonObject a)
    {
        int Int(string n) => a[n] is { } v ? v.GetValue<int>() : throw new McpRefusal($"'{n}' is missing.");
        // (a number, or one written as text: a client that does not know the field yet sends it so)
        double Num(string n) => a[n] is not { } v ? throw new McpRefusal($"'{n}' is missing.")
            : v is JsonValue s && s.TryGetValue(out string? text) ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : throw new McpRefusal($"'{n}' must be a number.")
            : v.GetValue<double>();
        bool Flag(string n) => a[n]?.GetValue<bool>() == true;
        Vec3 Pt(string n) => a[n] is JsonArray { Count: 3 } p ? new Vec3(p[0]!.GetValue<double>() / Display, p[1]!.GetValue<double>() / Display, p[2]!.GetValue<double>() / Display) : throw new McpRefusal($"'{n}' must be [x, y, z] in mm.");
        Vec3 Dir(string n) { var v = Pt(n) * Display; return v.Length < 1e-12 ? throw new McpRefusal($"'{n}' has no length.") : v.Normalized(); }
        int Body(string n = "body")
        {
            int b = Int(n);
            if (_doc == null || b < 0 || b >= _doc.Bodies.Count) throw new McpRefusal($"There is no body {b} (there are {_doc?.Bodies.Count ?? 0}).");
            if (Outside(b)) throw new McpRefusal($"Body {b} lies outside the open group {_doc.GroupName(_openGroup!.Value)}; close it first (open_group without group).");
            return b;
        }
        FaceRef Face(int b)
        {
            int f = Int("face");
            if (f < 0 || f >= _doc!.Bodies[b].Faces.Count) throw new McpRefusal($"Body {b} has no face {f} (it has {_doc.Bodies[b].Faces.Count}).");
            return new FaceRef(b, f);
        }
        // a change: taken (undoable), shown, said
        JsonObject Changed(SketchDocument before, SketchDocument after, string nl, string en, JsonObject? extra = null)
        {
            Commit(before, after);
            _selected = null; _multi = null; _pickedUnits.Clear(); _last = null; _lastMove = null;
            Render();
            StatusText.Text = $"🤖 {client}: " + T(nl, en);
            var text = new JsonObject
            {
                ["done"] = en,
                ["bodies"] = _doc!.Bodies.Count,
                ["volume_mm3"] = Math.Round(_doc.Volume() * 1e9, 3),
            };
            if (extra != null) foreach (var (k, v) in extra) text[k] = v?.DeepClone();
            return Answer(true, text.ToJsonString());
        }
        var before = _doc!;
        switch (tool)
        {
            case "get_model": return Answer(true, ModelJson().ToJsonString());
            case "get_body": return Answer(true, BodyJson(Body()).ToJsonString());
            case "screenshot":
            {
                if (a["view"]?.GetValue<string>() is { } v && v != "current") LookFrom(v);
                if (a["azimuth"] != null || a["elevation"] != null)
                {
                    var (az, el) = View.Camera is ProjectionCamera pc && CameraOk() ? ViewAngles.Of(Raw(pc.LookDirection)) : (325.0, 30.0);
                    LookRound(a["azimuth"] != null ? Num("azimuth") : az, a["elevation"] != null ? Num("elevation") : el);
                }
                StatusText.Text = $"🤖 {client}: " + T("bekijkt het model.", "looks at the model.");
                var png = await ViewPng();
                var (caz, cel) = View.Camera is ProjectionCamera c && CameraOk() ? ViewAngles.Of(Raw(c.LookDirection)) : (0.0, 0.0);
                return Answer(true, new JsonObject { ["view"] = "PNG of the 3D view.", ["azimuth"] = Math.Round(caz, 1), ["elevation"] = Math.Round(cel, 1) }.ToJsonString(), png);
            }
            case "push_pull":
            {
                int b = Body(); var f = Face(b); double d = Num("distance");
                var (doc, r) = before.PushPull(f, d / Display, join: Join);
                return Changed(before, doc, $"trekt vlak {f.Face} van {before.BodyName(b)} {Mm(d / Display)} mm.", $"pulled face {f.Face} of body {b} by {Mm(d / Display)} mm.");
            }
            case "draw_rectangle":
            {
                var p = Pt("corner1"); var q = Pt("corner2");
                if (a["body"] != null)
                {
                    int b = Body(); var f = Face(b);
                    var face = before.Bodies[b].Faces[f.Face];
                    if (face.Surface is not BrepPlane) throw new McpRefusal("Rectangles go on flat faces only.");
                    var n = FaceGeometry.Normal(face);
                    // (its first side along the world axis that lies most in the face)
                    var u = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) }.Select(x => x - n * x.Dot(n)).MaxBy(x => x.Length).Normalized();
                    var (doc, nf) = before.DrawRectangle(f, p, q, u);
                    return Changed(before, doc, $"tekent een rechthoek op vlak {f.Face} van {before.BodyName(b)}.", $"drew a rectangle on face {f.Face} of body {b}.", new JsonObject { ["new_face"] = nf.Face, ["body"] = nf.Body });
                }
                // on the work plane (the ground unless one is set): the corners brought onto it
                var o = _plane.Origin; Vec3 X = _plane.X, Y = _plane.Y;
                double u1 = (p - o).Dot(X), v1 = (p - o).Dot(Y), u2 = (q - o).Dot(X), v2 = (q - o).Dot(Y);
                if (Math.Abs(u2 - u1) < 1e-6 || Math.Abs(v2 - v1) < 1e-6) throw new McpRefusal("The rectangle has no width or depth on the work plane.");
                var c0 = o + X * u1 + Y * v1;
                var (made, faces) = before.AddShape([c0, o + X * u2 + Y * v1, o + X * u2 + Y * v2, o + X * u1 + Y * v2, c0], [new BrepLine(), new BrepLine(), new BrepLine(), new BrepLine()], GroundUp, o);
                var nfr = faces.Count > 0 ? faces[^1] : new FaceRef(made.Bodies.Count - 1, 0);
                return Changed(before, made, "tekent een rechthoek op het werkvlak.", "drew a rectangle on the work plane (a surface body: push_pull its face to make a block).", new JsonObject { ["body"] = nfr.Body, ["face"] = nfr.Face });
            }
            case "move":
            {
                int b = Body(); bool copy = Flag("copy");
                var doc = MoveDoc(before, b, copy ? MoveSelection.Copy() : MoveSelection.WholeBody(), Pt("offset"), GroupTaken(b), checkCrossing: true);
                return Changed(before, doc, copy ? $"kopieert {Thing(b)}." : $"verplaatst {Thing(b)}.", copy ? $"copied {ThingEn(b)}." : $"moved {ThingEn(b)}.");
            }
            case "rotate":
            {
                int b = Body(); var c = Pt("centre"); var ax = Dir("axis"); double rad = Num("degrees") * Math.PI / 180;
                var doc = Whole(before, b, Flag("copy"), (x, bb, cp) => x.RotateBody(bb, c, ax, rad, cp));
                return Changed(before, doc, $"draait {Thing(b)}.", $"turned {ThingEn(b)}.");
            }
            case "scale":
            {
                int b = Body(); var fp = Pt("fixed_point"); double k = Num("factor");
                if (k <= 0) throw new McpRefusal("The factor must be above 0.");
                var doc = Whole(before, b, Flag("copy"), (x, bb, cp) => x.ScaleBody(bb, fp, k, null, cp));
                return Changed(before, doc, $"schaalt {Thing(b)}.", $"scaled {ThingEn(b)}.");
            }
            case "mirror":
            {
                int b = Body(); var o = Pt("origin"); var n = Dir("normal");
                var doc = Whole(before, b, Flag("copy"), (x, bb, cp) => x.MirrorBody(bb, o, n, cp));
                return Changed(before, doc, $"spiegelt {Thing(b)}.", $"mirrored {ThingEn(b)}.");
            }
            case "paint":
            {
                int b = Body();
                uint? colour = a["color"]?.GetValue<string>() is { } hex
                    ? uint.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb) && hex.TrimStart('#').Length == 6 ? 0xFF000000 | rgb : throw new McpRefusal("color must be #RRGGBB or null.")
                    : null;
                var faces = a["faces"] is JsonArray fs ? fs.Select(x => x!.GetValue<int>()).ToList() : Enumerable.Range(0, before.Bodies[b].Faces.Count).ToList();
                if (faces.Any(f => f < 0 || f >= before.Bodies[b].Faces.Count)) throw new McpRefusal($"Body {b} has faces 0 to {before.Bodies[b].Faces.Count - 1}.");
                return Changed(before, before.Paint(faces.Select(f => new FaceRef(b, f)), colour), $"verft {faces.Count} vlak(ken) van {before.BodyName(b)}.", $"painted {faces.Count} face(s) of body {b}.");
            }
            case "rename":
            {
                string name = a["name"]?.GetValue<string>() ?? "";
                if (a["group"] != null)
                {
                    int g = Int("group");
                    if (before.Group(g) == null) throw new McpRefusal($"There is no group {g}.");
                    return Changed(before, before.RenameGroup(g, name), $"noemt groep {g} {name}.", $"renamed group {g}.");
                }
                int b = Body();
                return Changed(before, before.Rename(b, name), $"noemt {before.BodyName(b)} nu {name}.", $"renamed body {b}.");
            }
            case "make_group":
            {
                var units = new List<GroupUnit>();
                if (a["bodies"] is JsonArray bs) foreach (var x in bs) { int b = x!.GetValue<int>(); if (b < 0 || b >= before.Bodies.Count) throw new McpRefusal($"There is no body {b}."); units.Add(GroupUnit.OfBody(b)); }
                if (a["groups"] is JsonArray gs) foreach (var x in gs) { int g = x!.GetValue<int>(); if (before.Group(g) == null) throw new McpRefusal($"There is no group {g}."); units.Add(GroupUnit.OfGroup(g)); }
                var (doc, id) = before.MakeGroup(units, a["name"]?.GetValue<string>());
                return Changed(before, doc, $"maakt {doc.GroupName(id)}.", $"made group {id}.", new JsonObject { ["group"] = id });
            }
            case "explode_group":
            {
                int g = Int("group");
                if (before.Group(g) == null) throw new McpRefusal($"There is no group {g}.");
                if (_openGroup == g) _openGroup = before.Group(g)!.Parent;
                return Changed(before, before.Ungroup(g), $"heft {before.GroupName(g)} op.", $"exploded group {g}.");
            }
            case "open_group":
            {
                if (a["group"] is { } gn && gn.GetValue<int>() is int g)
                {
                    if (before.Group(g) == null) throw new McpRefusal($"There is no group {g}.");
                    OpenGroup(g);
                }
                else { _openGroup = null; _pickedUnits.Clear(); Render(); }
                StatusText.Text = $"🤖 {client}: " + (_openGroup is { } og ? T($"werkt in {before.GroupName(og)}.", $"works in {before.GroupName(og)}.") : T("heeft de groep gesloten.", "closed the group."));
                return Answer(true, _openGroup is { } g2 ? $"Group {g2} is open." : "No group is open.");
            }
            case "boolean":
            {
                int x = Body("a"), y = Body("b");
                var op = a["op"]?.GetValue<string>() switch { "union" => Booleans.Op.Union, "subtract" => Booleans.Op.Subtract, "intersect" => Booleans.Op.Intersect, _ => throw new McpRefusal("op must be union, subtract or intersect.") };
                var (doc, made) = before.Combine(x, y, op);
                return Changed(before, doc, $"combineert {before.BodyName(x)} en {before.BodyName(y)}.", $"combined body {x} and {y}.", new JsonObject { ["bodies_made"] = new JsonArray(made.Select(m => (JsonNode)m).ToArray()) });
            }
            case "undo":
                if (_undo.Count == 0) throw new McpRefusal("Nothing to undo.");
                Undo(); StatusText.Text = $"🤖 {client}: " + T("maakt ongedaan.", "undid the last change.");
                return Answer(true, "Undone.");
            case "redo":
                if (_redo.Count == 0) throw new McpRefusal("Nothing to redo.");
                Redo(); StatusText.Text = $"🤖 {client}: " + T("doet opnieuw.", "redid it.");
                return Answer(true, "Redone.");
            case "open_file":
            {
                string path = a["path"]?.GetValue<string>() ?? throw new McpRefusal("'path' is missing.");
                if (!File.Exists(path)) throw new McpRefusal($"There is no file {path}.");
                if (Unsaved && !Ask(T($"{client} wil {Path.GetFileName(path)} openen. Wat je nu hebt is niet opgeslagen en gaat dan weg (ongedaan maken kan niet meer). Doorgaan?", $"{client} wants to open {Path.GetFileName(path)}. What you have now is not saved and will be gone (no undo). Go ahead?")))
                    throw new McpRefusal("The user did not want that: there is unsaved work.");
                await Load(path);
                if (!ReferenceEquals(_doc, _savedDoc) || _path != path) throw new McpRefusal("The file could not be opened (the app shows why).");
                StatusText.Text = $"🤖 {client}: " + T($"opent {Path.GetFileName(path)}.", $"opened {Path.GetFileName(path)}.");
                return Answer(true, ModelJson().ToJsonString());
            }
            case "save_file":
            {
                string path = a["path"]?.GetValue<string>() ?? throw new McpRefusal("'path' is missing.");
                if (File.Exists(path) && !Ask(T($"{client} wil {Path.GetFileName(path)} overschrijven. Doorgaan?", $"{client} wants to overwrite {Path.GetFileName(path)}. Go ahead?")))
                    throw new McpRefusal("The user did not want the file overwritten.");
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                Save(path);
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) == stamp) throw new McpRefusal("Saving did not work (the app shows why).");
                StatusText.Text = $"🤖 {client}: " + T($"slaat op als {Path.GetFileName(path)}.", $"saved as {Path.GetFileName(path)}.");
                return Answer(true, $"Saved {path}.");
            }
            case "simulate":
            {
                var report = new System.Text.StringBuilder();
                await Simulate(a["steps"]?.GetValue<string>() ?? "", report);
                return Answer(true, report.ToString());
            }
            default: throw new McpRefusal($"Unknown tool {tool}.");
        }

        // (what a whole-body change takes: the body, or its closed group; in Dutch for the status bar, in English for the
        // assistant)
        string Thing(int b) => GroupTaken(b) is { } g ? $"groep {before.GroupName(g)}" : before.BodyName(b);
        string ThingEn(int b) => GroupTaken(b) is { } g ? $"group {g} ({before.GroupName(g)})" : $"body {b}";
    }

    /// <summary>A yes/no question to the user (the assistant waits for it).</summary>
    private bool Ask(string question) =>
        MessageBox.Show(this, question, "PushPullMe – " + T("AI-assistent", "AI assistant"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static JsonArray Mm3(Vec3 v) => new(Math.Round(v.X * Display, 3), Math.Round(v.Y * Display, 3), Math.Round(v.Z * Display, 3));

    private JsonObject ModelJson()
    {
        var m = new JsonObject { ["file"] = _path, ["unsaved"] = Unsaved };
        if (_doc is not { } d) { m["bodies"] = new JsonArray(); return m; }
        var bodies = new JsonArray();
        for (int b = 0; b < d.Bodies.Count; b++)
        {
            var pts = d.AllTriangles().Where(x => x.Face.Body == b).SelectMany(x => x.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            var o = new JsonObject
            {
                ["body"] = b, ["name"] = d.BodyName(b), ["group"] = d.GroupOf(b), ["kind"] = d.Bodies[b].Sheet ? "surface" : "solid",
                ["faces"] = d.Bodies[b].Faces.Count, ["hidden"] = _hidden.Contains(d.Bodies[b]),
            };
            if (pts.Count > 0)
                o["box_mm"] = new JsonObject
                {
                    ["min"] = Mm3(new Vec3(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z))),
                    ["max"] = Mm3(new Vec3(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z))),
                };
            bodies.Add(o);
        }
        m["bodies"] = bodies;
        m["groups"] = new JsonArray(d.Groups.Select(g => (JsonNode)new JsonObject { ["group"] = g.Id, ["name"] = d.GroupName(g.Id), ["parent"] = g.Parent }).ToArray());
        m["open_group"] = _openGroup;
        m["loose_lines"] = d.Lines.Count;
        m["volume_mm3"] = Math.Round(d.Volume() * 1e9, 3);
        m["join_after_pull"] = Join;
        return m;
    }

    private JsonObject BodyJson(int b)
    {
        var d = _doc!; var body = d.Bodies[b];
        var faces = new JsonArray();
        for (int f = 0; f < body.Faces.Count; f++)
        {
            var face = body.Faces[f];
            var tris = d.TrianglesOf(new FaceRef(b, f));
            double area = 0; var c = new Vec3(0, 0, 0);
            foreach (var (p, q, r) in tris) { double ar = (q - p).Cross(r - p).Length / 2; area += ar; c += (p + q + r) * (ar / 3); }
            var o = new JsonObject
            {
                ["face"] = f,
                ["kind"] = face.Surface switch { BrepPlane => "flat", BrepCylinder => "cylinder", BrepCone => "cone", BrepSphere => "sphere", BrepTorus => "torus", BrepNurbsSurface => "free form", _ => "other" },
                ["area_mm2"] = Math.Round(area * 1e6, 3),
            };
            if (area > 0) o["centre_mm"] = Mm3(c * (1 / area));
            if (face.Surface is BrepPlane) { var n = FaceGeometry.Normal(face); o["normal"] = new JsonArray(Math.Round(n.X, 6), Math.Round(n.Y, 6), Math.Round(n.Z, 6)); }
            if (d.ColorOf(new FaceRef(b, f)) is { } col) o["color"] = $"#{col & 0xFFFFFF:X6}";
            faces.Add(o);
        }
        return new JsonObject { ["body"] = b, ["name"] = d.BodyName(b), ["kind"] = body.Sheet ? "surface" : "solid", ["faces"] = faces };
    }

    /// <summary>The camera set to look from a standard direction, everything in view.</summary>
    private void LookFrom(string view)
    {
        if (View.Camera is not ProjectionCamera cam) return;
        (Vector3D look, Vector3D up) = view switch
        {
            "top" => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),
            "front" => (new Vector3D(0, 1, 0), new Vector3D(0, 0, 1)),
            "right" => (new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1)),
            "all" => (cam.LookDirection, cam.UpDirection),
            _ => (new Vector3D(1, 1.4, -0.9), new Vector3D(0, 0, 1)),
        };
        cam.LookDirection = look; cam.UpDirection = up;
        ZoomAll(0);
    }

    /// <summary>The camera put round the middle of the model at that azimuth and elevation (degrees, as ViewAngles),
    /// as far back as the model needs from every azimuth: a turn round it in steps keeps it the same size and in view.</summary>
    private void LookRound(double azimuth, double elevation)
    {
        var pts = _meshes.SelectMany(m => m.Mesh.Positions).ToList();
        if (pts.Count == 0 || View.Camera is not PerspectiveCamera cam) return;
        var lo = new Point3D(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
        var hi = new Point3D(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
        var centre = lo + (hi - lo) * 0.5;
        var (tx, ty) = ViewReach(cam);
        double d = ViewAngles.FitDistanceAllRound(BoxCorners(lo, hi), Raw(centre), elevation, tx, ty);
        var f = ViewAngles.Look(azimuth, elevation);
        var look = new Vector3D(f.X, f.Y, f.Z) * d;
        SetCamera(centre - look, look, new Vector3D(0, 0, 1));
    }

    /// <summary>The 3D view as a PNG, rendered by DirectX itself (never copied from the screen).</summary>
    private async Task<byte[]> ViewPng()
    {
        await Task.Delay(300);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(View.RenderBitmap()));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
