# The window (Avalonia 12 + OpenGL via Silk.NET) — Windows, Linux, macOS

The window only draws what `EditorSession` describes and passes mouse and keys on; logic belongs in
`PushPullMe.Editor` (see its CLAUDE.md), not here. WPF and HelixToolkit are gone since v0.5.

- `MainWindow`: tool bar from `ToolBar`, texts via `UiTexts`, dialogs, toasts, the converter window for STL/3MF
  (StlConverter, embedded), structure tree (`StructureTree`), storey list (`LevelList`), view cube (`ViewCube`).
- `ModelView : OpenGlControlBase`: GLSL `#version 300 es` under ANGLE (Windows), else `330 core`; macOS asks OpenGL
  before Metal (deprecated there, still works).
  - Faces: light from the eye (0.55 + 0.45·cos), back faces in the back colour via `gl_FrontFacing`;
    PolygonOffset(1,1) so edges lie in front. Faint/see-through meshes (outside an open group, glass) in a second,
    blended pass that keeps the picture itself opaque.
  - Lines of any width as quads in the vertex shader (thick GL lines do not exist everywhere); edges as instances.
  - Vertex layout: `FaceStride = 11`, `EdgeStride = 7` floats, with an `aModel` attribute: the storey cut
    (`CutAway(z, model)` → discard) is done in the shaders, per model (bit mask, ≤ 31 models). Highlights (FaceSet:
    hover, selection) ignore the cut on purpose (model -1): they show the whole part, also where it is cut away
    (decided, keep; issue #8).
  - The clip matrix comes straight from `ViewFrame`, so drawing and picking agree exactly. The view angle is
    VERTICAL (as Helix had it): everything that snaps in pixels depends on it.
- Highlights: one FaceSet per colour, not rebuilt on camera moves; a picked group of more than 20 000 faces gets a
  box instead of a tint (speed).
- `ViewCube`: own control over the GL view, no text, 78 px, x red / y green / z blue, yellow under the mouse;
  a click → `EditorSession.ViewFrom(direction)`.
- Test aids: PPM_SNAPSHOT (glReadPixels after a frame, window without focus), PPM_VIEWSIZE, PPM_ORBIT, PPM_PROBE;
  `SaveWindowPng` draws the layers (labels, view cube) over the GL picture again.
- Styles in App.axaml (card, pill, tool, `CheckBox.small` for the bottom bar). FontSize 12.
