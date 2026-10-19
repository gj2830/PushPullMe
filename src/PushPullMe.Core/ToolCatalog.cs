using System.Text.Json.Nodes;

namespace PushPullMe.Core;

/// <summary>A tool as an MCP client sees it: its name, what it does (for the language model, in English) and the JSON
/// schema of its arguments.</summary>
public sealed record Tool(string Name, string Description, JsonObject InputSchema);

/// <summary>The tools PushPullMe offers (a fixed set: each goes through the app's own functions and can be undone). The
/// app hands them to the MCP server (over the pipe, and in <c>mcp-tools.json</c> for when it is not running), so a new or
/// changed tool needs only the app rebuilt: the server itself stays as it is.</summary>
public static class ToolCatalog
{
    /// <summary>The tools as MCP's tools/list gives them: [{name, description, inputSchema}, …].</summary>
    public static JsonArray ToJson() => new(Tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema.DeepClone(),
    }).ToArray());

    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
    private static JsonObject Point(string d) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3, ["description"] = d + " [x, y, z] in mm" };
    private static JsonObject Ints(string d) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = d };
    private static JsonObject Choice(string d, params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()), ["description"] = d };

    private static JsonObject Schema(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, s, _) in props) p[n] = s;
        var o = new JsonObject { ["type"] = "object", ["properties"] = p };
        var req = props.Where(x => x.Required).Select(x => (JsonNode)x.Name!).ToArray();
        if (req.Length > 0) o["required"] = new JsonArray(req);
        return o;
    }

    private const string BodyNote = " Bodies and faces are numbered from 0, as get_model and get_body give them.";

    public static readonly IReadOnlyList<Tool> Tools =
    [
        new("get_model", "The model open in PushPullMe: file, bodies (number, name, group, solid or surface, face count, bounding box in mm), groups, the group open for editing, loose lines, the work plane (origin, x, y, normal). Lengths in mm; z is up. Body and face numbers can change after a change: ask again when unsure.", Schema()),
        new("get_body", "The faces of one body: number, kind (flat, cylinder, cone, sphere, torus, free form), outward normal (flat faces), centre and area." + BodyNote,
            Schema(("body", Int("Body number"), true))),
        new("screenshot", "A PNG of PushPullMe's 3D view (only the view, never the screen), optionally from a standard direction first. With azimuth and/or elevation the camera goes round the middle of the model instead, as far back as fits from every azimuth: to show the model all round, call it with azimuth 0, 45, 90 … 315 and it stays the same size. Only the camera moves; the model is not changed. Says the camera's azimuth and elevation.",
            Schema(("view", Choice("Direction to look from; 'current' keeps the camera", "current", "iso", "top", "front", "right", "all"), false),
                ("azimuth", Num("Degrees round the model: 0 from the front (-y), 90 from the right (+x), counter-clockwise seen from above; left out: as the camera is"), false),
                ("elevation", Num("Degrees above level, from -89 to 89 (89: nearly straight down from above); left out: as the camera is"), false))),
        new("push_pull", "Push/pull a flat face along its normal (positive: outward, the body grows). A face on a round end of a revolve follows the turn. Parts touching afterwards are joined when the app's 'join' box is ticked (only within the same group)." + BodyNote,
            Schema(("body", Int("Body number"), true), ("face", Int("Face number"), true), ("distance", Num("Distance in mm"), true))),
        new("draw_rectangle", "Draw a rectangle: on a flat face of a body (both corners on that face; it splits the face), or with body/face left out on the work plane (get_model's work_plane, the ground unless the user set another; the corners are brought onto it; a new surface body, to push/pull up into a solid)." + BodyNote,
            Schema(("corner1", Point("First corner"), true), ("corner2", Point("Opposite corner"), true), ("body", Int("Body of the face to draw on"), false), ("face", Int("Face to draw on"), false))),
        new("draw_circle", "Draw a circle: on a flat face of a body (the centre on that face; it splits the face), or with body/face left out on the work plane (get_model's work_plane; the centre is brought onto it; a new surface body, to push/pull up into a cylinder)." + BodyNote,
            Schema(("centre", Point("Centre"), true), ("radius", Num("Radius in mm"), true), ("body", Int("Body of the face to draw on"), false), ("face", Int("Face to draw on"), false))),
        new("draw_line", "Draw lines from point to point: on a flat face of a body (all points on that face or its edges; from edge to edge it divides the face, back on the first point it makes a face of its own; a corner cut off this way is a profile for follow_me), or with body/face left out on the work plane (loose lines; a shape they close becomes a new surface body). Returns the new face." + BodyNote,
            Schema(("points", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 }, ["minItems"] = 2, ["description"] = "The points, each [x, y, z] in mm" }, true),
                ("body", Int("Body of the face to draw on"), false), ("face", Int("Face to draw on"), false))),
        new("offset","Offset the outline of a flat face (SketchUp's Offset): positive draws it smaller inside the face, negative larger on the face round it. Returns the new inner face, to push/pull (a rim, a recess)." + BodyNote,
            Schema(("body", Int("Body number"), true), ("face", Int("Face number"), true), ("distance", Num("Distance in mm"), true))),
        new("revolve", "Turn a flat face out of its body round an axis in the face's plane (a positive angle takes it out along its normal; 360: the whole solid of revolution, joined to the body). Returns the end face." + BodyNote,
            Schema(("body", Int("Body number"), true), ("face", Int("Face number"), true), ("axis_point", Point("A point on the axis"), true), ("axis", Point("Axis direction (any length)"), true), ("degrees", Num("Angle"), true))),
        new("follow_me", "SketchUp's Follow Me: a profile face swept along the outer edge of a path face. Where the sweep lies in the body it is cut away (a chamfer or groove all round), else added (a rim). Both faces of one body, or the profile a surface body of its own (it is used up)." + BodyNote,
            Schema(("profile_body", Int("Body of the profile"), true), ("profile_face", Int("Profile face"), true), ("path_body", Int("Body of the path"), true), ("path_face", Int("Path face (its outer edge is followed)"), true))),
        new("delete_body", "Delete a whole body (or, when it lies in a closed group, the whole group). The bodies after it get a number one lower." + BodyNote,
            Schema(("body", Int("Body number"), true))),
        new("move","Move a whole body by an offset (or, when it lies in a closed group, the whole group); copy: a moved copy instead." + BodyNote,
            Schema(("body", Int("Body number"), true), ("offset", Point("Offset"), true), ("copy", Bool("Make a copy (the answer gives copy_body, or copy_group and copy_bodies)"), false))),
        new("rotate", "Turn a whole body (or its closed group) round an axis through a point." + BodyNote,
            Schema(("body", Int("Body number"), true), ("centre", Point("A point on the axis"), true), ("axis", Point("Axis direction (any length)"), true), ("degrees", Num("Angle, counter-clockwise seen from the tip of the axis"), true), ("copy", Bool("Make a copy (the answer gives copy_body, or copy_group and copy_bodies)"), false))),
        new("scale", "Scale a whole body (or its closed group) from a fixed point." + BodyNote,
            Schema(("body", Int("Body number"), true), ("fixed_point", Point("The point that stays"), true), ("factor", Num("Factor, e.g. 2 or 0.5"), true), ("copy", Bool("Make a copy (the answer gives copy_body, or copy_group and copy_bodies)"), false))),
        new("mirror", "Mirror a whole body (or its closed group) in a plane." + BodyNote,
            Schema(("body", Int("Body number"), true), ("origin", Point("A point in the plane"), true), ("normal", Point("Plane normal"), true), ("copy", Bool("Keep the original, add the mirrored copy (the answer gives copy_body, or copy_group and copy_bodies)"), false))),
        new("paint", "Paint faces of a body a colour (or take the paint off with color null). Without faces: all faces of the body." + BodyNote,
            Schema(("body", Int("Body number"), true), ("faces", Ints("Face numbers"), false), ("color", new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "#RRGGBB, or null for no paint" }, true))),
        new("rename", "Give a body or a group a name (empty: back to the standard name).",
            Schema(("body", Int("Body number"), false), ("group", Int("Group id"), false), ("name", Str("The new name"), true))),
        new("make_group", "Make a group of bodies and/or groups that lie directly in the same group (as SketchUp's Make Group), or a component (Make Component): a group whose copies (move or rotate it with copy) change along with it; whatever changes inside one instance (open it with open_group) changes in all. Returns the new group's id.",
            Schema(("bodies", Ints("Body numbers"), false), ("groups", Ints("Group ids"), false), ("name", Str("Its name (optional)"), false), ("component", Bool("Make a component instead of a plain group"), false))),
        new("make_unique", "Make a component instance unique: a component of its own, no longer changing with the other instances.", Schema(("group", Int("Group id of the instance"), true))),
        new("explode_group", "Take a group away; what was in it goes to the group it was in.", Schema(("group", Int("Group id"), true))),
        new("open_group", "Open a group to work inside it (what lies outside is shown faded and cannot be picked), or with group left out close the open one.",
            Schema(("group", Int("Group id"), false))),
        new("boolean", "Combine two solid bodies: union, subtract (b from a) or intersect. They must lie in the same group." + BodyNote,
            Schema(("op", Choice("Operation", "union", "subtract", "intersect"), true), ("a", Int("First body"), true), ("b", Int("Second body"), true))),
        new("undo", "Undo the last change (the user can also press Ctrl+Z).", Schema()),
        new("redo", "Redo what was undone.", Schema()),
        new("open_file", "Open a file (.step/.stp, .rsdocx, .rvt (Revit 2023 and later), .stl, .3mf). The user is asked first when there is unsaved work.",
            Schema(("path", Str("Full path"), true))),
        new("save_file", "Save the model (.step is the main format; .rsdocx for DesignSpark; .stl). The user is asked first when the file exists.",
            Schema(("path", Str("Full path"), true))),
        new("simulate", "Play mouse and keys in the window, for trying the user interface: steps split by '|', e.g. 'tool:R|click:0,0,0|click:20,20,0|tool:P|click:10,10,0|type:20|enter'. Steps: tool:<P R L C E M T B F K Q S G A MIRROR UNION … or SEL>, move:x,y,z / click:x,y,z / dblclick:x,y,z (a world point in mm, through the real hit tests), mods:ctrl|shift|ctrl+shift|none, key:<WPF key name>, type:<text>, enter, esc, camera:… . Returns what the status bar said after each step.",
            Schema(("steps", Str("The steps"), true))),
    ];
}
