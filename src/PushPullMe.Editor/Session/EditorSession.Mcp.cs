using System.Diagnostics;
using System.IO.Pipes;
using PushPullMe.Mcp;
using System.Globalization;
using System.Text.Json.Nodes;
using PushPullMe.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;
using static PushPullMe.Editor.Units;

namespace PushPullMe.Editor.Session;


/// <summary>
/// The app's side of the MCP server (src/PushPullMe.Mcp): with "AI assistant may work along" ticked, a named pipe of this
/// Windows user takes tool calls from it (any MCP client: Claude Code, Claude Desktop, VS Code, …) and does them here, in
/// this window, as the user would: through the same functions, each change undoable, shown in the status bar and the log.
/// </summary>
public sealed partial class EditorSession
{
    /// <summary>The pipe server while it runs (null: off).</summary>
    private CancellationTokenSource? _mcpStop;

    /// <summary>The document as last opened or saved (to know whether there is unsaved work).</summary>
    private SketchDocument? _savedDoc;

    private bool Unsaved => _doc != null && !ReferenceEquals(_doc, _savedDoc) && _history.CanUndo;

    /// <summary>The choice kept from before (off when there is none); PPM_MCP=1 turns it on for a test window.</summary>
    private void LoadAssistant()
    {
        // (the tools of this build, for the MCP server while this window does not listen)
        PipeProtocol.WriteToolsFile(ToolCatalog.ToJson());
        bool on = Settings.LoadAssistant();
        AssistantBox.On = on;
        if (on) StartMcp();
        AssistantBox.Changed += AssistantSwitched;
    }

    /// <summary>The window closed: the pipe server stopped.</summary>
    public void Closed() => _mcpStop?.Cancel();

    private void AssistantSwitched()
    {
        bool on = AssistantBox.On;
        Settings.SaveAssistant(on);
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
                await _window.OnUiThread(() =>
                {
                    _mcpStop = null;
                    AssistantBox.Changed -= AssistantSwitched;
                    AssistantBox.On = false;
                    AssistantBox.Changed += AssistantSwitched;
                    StatusText.Text = T("🤖 Een ander PushPullMe-venster werkt al met de AI-assistent; dit venster niet.", "🤖 Another PushPullMe window already works with the AI assistant; this one does not.");
                    return Task.FromResult(true);
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
                    var answer = await _window.OnUiThread(() => McpCall(req["client"]?.GetValue<string>() ?? "?", req["tool"]?.GetValue<string>() ?? "", req["args"] as JsonObject ?? new JsonObject()));
                    await PipeProtocol.WriteAsync(pipe, answer, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { }
                // (whatever else went wrong with this one call: said to the client if it still listens, and the next one
                // is taken; the loop never stops on it, or the assistant would be gone without a word)
                catch (Exception ex)
                {
                    try { await PipeProtocol.WriteAsync(pipe, Answer(false, $"PushPullMe could not do that ({ex.GetType().Name}): {ex.Message}"), ct); }
                    catch (Exception) { }
                }
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
            if (!AssistantBox.On) throw new McpRefusal("The user turned 'AI assistant may work along' off.");
            bool reads = tool is "get_model" or "get_body" or "screenshot";
            if (!reads && ActionBusy) throw new McpRefusal("The user is in the middle of an action; try again in a moment.");
            if (!reads && tool is not ("open_file" or "undo" or "redo") && _doc == null) throw new McpRefusal("No model is open.");
            var r = await McpDo(client, tool, args);
            return r;
        }
        catch (McpRefusal ex) { return Answer(false, ex.Message); }
        // (in English, as the tools speak it, also when the app speaks Dutch)
        catch (PushPullException ex) { Render(); return Answer(false, ex.English); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IOException or KeyNotFoundException)
        {
            Render();
            return Answer(false, ex.Message);
        }
        // (anything else, e.g. a null in an argument array or a fault deep in the geometry: said, never the end of the
        // pipe server)
        catch (Exception ex)
        {
            Log($"mcp {tool} failed: {ex}");
            Render();
            return Answer(false, $"{tool} failed ({ex.GetType().Name}): {ex.Message}");
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
        Vec3 Pt(string n) => a[n] is JsonArray { Count: 3 } p && p.All(x => x is JsonValue) ? new Vec3(p[0]!.GetValue<double>() / Display, p[1]!.GetValue<double>() / Display, p[2]!.GetValue<double>() / Display) : throw new McpRefusal($"'{n}' must be [x, y, z] in mm.");
        Vec3 Dir(string n) { var v = Pt(n) * Display; return v.Length < 1e-12 ? throw new McpRefusal($"'{n}' has no length.") : v.Normalized(); }
        int Body(string n = "body")
        {
            int b = Int(n);
            if (_doc == null || b < 0 || b >= _doc.Bodies.Count) throw new McpRefusal($"There is no body {b} (there are {_doc?.Bodies.Count ?? 0}).");
            if (Outside(b)) throw new McpRefusal($"Body {b} lies outside the open group {_doc.GroupName(_openGroup!.Value)}; close it first (open_group without group).");
            return b;
        }
        FaceRef Face(int b, string n = "face")
        {
            int f = Int(n);
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
            case "draw_circle":
            {
                var c = Pt("centre"); double r = Num("radius") / Display;
                if (r <= 0) throw new McpRefusal("The radius must be above 0.");
                if (a["body"] != null)
                {
                    int b = Body(); var f = Face(b);
                    if (before.Bodies[b].Faces[f.Face].Surface is not BrepPlane) throw new McpRefusal("Circles go on flat faces only.");
                    var (doc, nf) = before.DrawCircle(f, c, r);
                    return Changed(before, doc, $"tekent een cirkel op vlak {f.Face} van {before.BodyName(b)}.", $"drew a circle on face {f.Face} of body {b}.", new JsonObject { ["new_face"] = nf.Face, ["body"] = nf.Body });
                }
                // on the work plane, as draw_rectangle
                var o = _plane.Origin;
                var on = _plane.At((c - o).Dot(_plane.X), (c - o).Dot(_plane.Y));
                var (made, faces) = before.AddShape([on + _plane.X * r, on + _plane.X * r], [new BrepCircle(on, GroundUp, _plane.X, r)], GroundUp, o);
                var nfr = faces.Count > 0 ? faces[^1] : new FaceRef(made.Bodies.Count - 1, 0);
                return Changed(before, made, "tekent een cirkel op het werkvlak.", "drew a circle on the work plane (a surface body: push_pull its face to make a cylinder).", new JsonObject { ["body"] = nfr.Body, ["face"] = nfr.Face });
            }
            case "draw_line":
            {
                var pts = a["points"] is JsonArray ps && ps.Count >= 2 && ps.All(p => p is JsonArray { Count: 3 } c && c.All(x => x is JsonValue))
                    ? ps.Select(p => new Vec3(p![0]!.GetValue<double>() / Display, p[1]!.GetValue<double>() / Display, p[2]!.GetValue<double>() / Display)).ToList()
                    : throw new McpRefusal("'points' must be two or more [x, y, z] in mm.");
                if (a["body"] != null)
                {
                    int b = Body(); var f = Face(b);
                    if (before.Bodies[b].Faces[f.Face].Surface is not BrepPlane) throw new McpRefusal("Lines go on flat faces only.");
                    var (doc, nf) = before.DrawPath(f, pts);
                    return Changed(before, doc, $"tekent lijnen op vlak {f.Face} van {before.BodyName(b)}.", $"drew lines on face {f.Face} of body {b}.", new JsonObject { ["new_face"] = nf.Face, ["body"] = nf.Body, ["faces_now"] = doc.Bodies[b].Faces.Count });
                }
                // on the work plane (the points brought onto it): loose lines, closing what they can, as the line tool there
                var o = _plane.Origin;
                var on = pts.Select(p => _plane.At((p - o).Dot(_plane.X), (p - o).Dot(_plane.Y))).ToList();
                var (made, faces) = before.AddLines(on.Zip(on.Skip(1), (p, q) => new SketchLine(p, q))).CloseLooseLinesOnPlane(GroundUp, o);
                return Changed(before, made, faces.Count > 0 ? "tekent lijnen op het werkvlak: ze sluiten een vlak." : "tekent losse lijnen op het werkvlak.",
                    faces.Count > 0 ? "drew lines on the work plane; they close a shape (a surface body: push_pull its face)." : "drew loose lines on the work plane (no closed shape yet).",
                    new JsonObject { ["faces_made"] = new JsonArray(faces.Select(x => (JsonNode)new JsonObject { ["body"] = x.Body, ["face"] = x.Face }).ToArray()), ["loose_lines"] = made.Lines.Count });
            }
            case "offset":
            {
                int b = Body(); var f = Face(b); double d = Num("distance");
                if (before.Bodies[b].Faces[f.Face].Surface is not BrepPlane) throw new McpRefusal("Offset works on flat faces only.");
                var (doc, inner) = before.Offset(f, d / Display);
                return Changed(before, doc, $"verschuift de rand van vlak {f.Face} van {before.BodyName(b)} {Mm(d / Display)} mm.", $"offset face {f.Face} of body {b} by {Mm(d / Display)} mm.", new JsonObject { ["new_face"] = inner.Face, ["body"] = inner.Body });
            }
            case "revolve":
            {
                int b = Body(); var f = Face(b); var p = Pt("axis_point"); var ax = Dir("axis"); double deg = Num("degrees");
                if (before.Bodies[b].Faces[f.Face].Surface is not BrepPlane) throw new McpRefusal("Only a flat face can be turned out.");
                var (doc, cap) = before.Revolve(f, p, ax, deg * Math.PI / 180);
                return Changed(before, doc, $"draait vlak {f.Face} van {before.BodyName(b)} {deg.ToString("0.#", CultureInfo.CurrentCulture)}° rond.", $"turned face {f.Face} of body {b} round by {deg.ToString("0.#", CultureInfo.InvariantCulture)}°.", new JsonObject { ["end_face"] = cap.Face, ["body"] = cap.Body });
            }
            case "follow_me":
            {
                int pb = Body("profile_body"); var pf = Face(pb, "profile_face");
                int qb = Body("path_body"); var qf = Face(qb, "path_face");
                var (doc, made) = before.FollowPath(pf, qf);
                bool cut = doc.Volume() < before.Volume();
                return Changed(before, doc, cut ? "volgt de rand en snijdt weg." : "volgt de rand (een lijst rondom).", cut ? "followed the edge and cut away (a chamfer or groove)." : "followed the edge (a rim added).",
                    new JsonObject { ["bodies_made"] = new JsonArray(made.Select(m => (JsonNode)m).ToArray()) });
            }
            case "delete_body":
            {
                int b = Body();
                var gone = GroupTaken(b) is { } g ? before.Members(g).ToList() : [b];
                return Changed(before, before.RemoveBodies(gone), $"verwijdert {Thing(b)}.", $"deleted {ThingEn(b)}.", new JsonObject { ["deleted_bodies"] = new JsonArray(gone.Select(x => (JsonNode)x).ToArray()) });
            }
            case "move":
            {
                int b = Body(); bool copy = Flag("copy"); var off = Pt("offset");
                var (doc, made) = WholeOf(before, b, copy, (x, bb, cp) => x.MoveBody(bb, off, cp));
                return Changed(before, doc, copy ? $"kopieert {Thing(b)}." : $"verplaatst {Thing(b)}.", copy ? $"copied {ThingEn(b)}." : $"moved {ThingEn(b)}.", Copied(doc, made, copy));
            }
            case "rotate":
            {
                int b = Body(); bool copy = Flag("copy"); var c = Pt("centre"); var ax = Dir("axis"); double rad = Num("degrees") * Math.PI / 180;
                var (doc, made) = WholeOf(before, b, copy, (x, bb, cp) => x.RotateBody(bb, c, ax, rad, cp));
                return Changed(before, doc, $"draait {Thing(b)}.", $"turned {ThingEn(b)}.", Copied(doc, made, copy));
            }
            case "scale":
            {
                int b = Body(); bool copy = Flag("copy"); var fp = Pt("fixed_point"); double k = Num("factor");
                if (k <= 0) throw new McpRefusal("The factor must be above 0.");
                var (doc, made) = WholeOf(before, b, copy, (x, bb, cp) => x.ScaleBody(bb, fp, k, null, cp));
                return Changed(before, doc, $"schaalt {Thing(b)}.", $"scaled {ThingEn(b)}.", Copied(doc, made, copy));
            }
            case "mirror":
            {
                int b = Body(); bool copy = Flag("copy"); var o = Pt("origin"); var n = Dir("normal");
                var (doc, made) = WholeOf(before, b, copy, (x, bb, cp) => x.MirrorBody(bb, o, n, cp));
                return Changed(before, doc, $"spiegelt {Thing(b)}.", $"mirrored {ThingEn(b)}.", Copied(doc, made, copy));
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
                if (!_history.CanUndo) throw new McpRefusal("Nothing to undo.");
                Undo(); StatusText.Text = $"🤖 {client}: " + T("maakt ongedaan.", "undid the last change.");
                return Answer(true, "Undone.");
            case "redo":
                if (!_history.CanRedo) throw new McpRefusal("Nothing to redo.");
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

        // (a copy made: its body number, or its group and the bodies in it, for the assistant's next call)
        static JsonObject? Copied(SketchDocument doc, GroupUnit made, bool copy) =>
            !copy ? null
            : made.Group is { } g ? new JsonObject { ["copy_group"] = g, ["copy_bodies"] = new JsonArray(doc.Members(g).Select(m => (JsonNode)m).ToArray()) }
            : new JsonObject { ["copy_body"] = made.Body };
    }

    /// <summary>A yes/no question to the user (the assistant waits for it).</summary>
    private bool Ask(string question) => _window.Ask(question);

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
        // (where draw_rectangle without a face draws: the ground unless the user set another plane)
        static JsonArray Dir3(Vec3 v) => new(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));
        m["work_plane"] = new JsonObject
        {
            ["ground"] = _plane == WorkPlane.Ground,
            ["origin_mm"] = Mm3(_plane.Origin), ["x"] = Dir3(_plane.X), ["y"] = Dir3(_plane.Y), ["normal"] = Dir3(_plane.N),
        };
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
        if (Picking.Bounds(_meshes) is not { } box || View.Camera is not PerspectiveCamera cam) return;
        var lo = new Point3D(box.Min.X, box.Min.Y, box.Min.Z);
        var hi = new Point3D(box.Max.X, box.Max.Y, box.Max.Z);
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
        await _window.Idle();
        return _window.Snapshot() ?? throw new InvalidOperationException("The view could not be rendered.");
    }
}
