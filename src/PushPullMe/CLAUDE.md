# The desktop app (Windows, Linux, macOS)

A window (`MainWindow`, the `IAppHost` of the desktop) around the shared view in `src/PushPullMe.App` (see its
CLAUDE.md). Only what the desktop has lives here: file paths in the dialogs, `Ask` as a dialog window, the converter's
window for STL/3MF (StlConverter, embedded), the AI assistant (MCP pipe), the test window (no focus, no real mouse).
The exe keeps its name and folder (skills, workflows and the release build use `src/PushPullMe`).
