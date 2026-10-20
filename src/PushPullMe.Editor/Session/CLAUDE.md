# EditorSession: all of the app's logic, without a window

- `EditorSession` (partials: main file, Gizmo, Ground, Infer, Mcp, Host, Overlay) is the former WPF MainWindow code;
  the Avalonia window (`src/PushPullMe`) is a shell. New logic goes here or lower, never into the window.
- `Compat.cs`: WPF-like helper types in millimetres (Point, Vector, Point3D, Vector3D, Color, Key, ModifierKeys, Rect,
  LineSet, FaceSet). What is shown: `Shown` (ShownModel: meshes per body/colour, edges) + `SceneChanged`,
  `View.Children`, `Labels`; status, size box and buttons through TextSlot/FlagSlot.
- Input: `MouseDown/Move/Up/Wheel/Leave`, `KeyDown(key, mods, inTextBox)`, `TextInput`, `SetTool(Tool)`. Keys typed
  in a text box never choose a tool.
- `IEditorWindow`: what the session asks of a window (dialogs, Ask, converter, toast, busy, snapshot, structure tree,
  letter outlines for 3D text).
- A new tool: its code here, a line in `ToolBar` (icon, Dutch tooltip, group) and its English text in `UiTexts`.
- Texts: every user-facing text in Dutch and English, `T("nl", "en")` here, `Lang.T` in the core, `UiTexts` for tool
  bar/AXAML texts. Kinds and states are enums, turned into text only where shown (never compare texts).
- Every change goes through `Commit` (`History`: undo/redo; `CarryMeta` carries names, groups and components to the
  new document; `SyncChanged` copies an edited component instance to the others).
- Snapping (`EditorSession.Infer.cs`): axes, parallel and square to edges and to the edge the mouse last rested on;
  in a face within 3°, in space within 15 px; lengths on 0.1 mm.
- Settings (`Settings`, folder `AppData` per system): language, join toggle (default on), assistant box (default off).
- MCP (`EditorSession.Mcp.cs`): only while the user ticked "AI assistant may work along"; calls run on the UI thread,
  through `Commit` (undoable), refused while the user is mid-operation; asks before opening over unsaved work and
  before overwriting. Client-neutral wording ("AI assistant"), the client's name from initialize in the status line.
- Test aids (env vars): PPM_OPEN, PPM_SIM (a small script language: clicks, keys, "viewfrom:", "pickgroup:",
  "leveloff:", "spin:" …; documented above its parser in EditorSession.cs), PPM_SNAPSHOT (+ .txt report), PPM_ANSWER=yes|no, PPM_LANG, PPM_MCP=1, PPM_LOG.
  They never touch the user's own settings files.
