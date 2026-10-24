# The browser app (WebAssembly)

The shared view (`src/PushPullMe.App`) in a web page: `wwwroot/index.html` + `main.js` start the .NET runtime, which
draws into `<div id="out">` (the `avalonia-splash` div is hidden by app.css once Avalonia marks it `splash-close`).
`BrowserHost` is its `IAppHost`. No AI assistant, no Revit links (one file at a time).

- Files: through the converter's `src/BrowserKit` (`BrowserFiles`): a chosen file is copied into the runtime's
  in-memory folder and opened from there; saving writes there first and then into the file the user chose (`Saved`).
  `?open=name` in the page's address opens that file from the server (a link to a model; the test aid here).
- A mesh (STL/3MF) opens in the converter's view (`tools/StlConverter.View`) as a layer over the view
  (`MainView.ShowLayer`), with a ✕ of its own; what it hands over is taken over as on the desktop.
- 3D: `ModelView` is a `GlView` (`src/GlKit`): Avalonia has no OpenGL control in a browser, so it draws in Skia's
  WebGL 2 context, on a plain control right behind it, into a framebuffer of its own, the picture to Skia as a texture.
  GL calls go to WebGL straight (`WebGl`, DllImport "*"): the runtime cannot call through Silk.NET's function pointers
  ("aot-runtime-wasm.c:188" in the console means a native call shape it does not know).
- Fonts: `BrowserKit`'s `WithBrowserFonts` (Inter, and Noto fallbacks for the symbols and emoji; ~4.6 MB).
- Build: needs the `wasm-tools` workload. The page is only complete when published:
  `dotnet publish src/PushPullMe.Browser -c Debug -o output/web`, served from `output/web/wwwroot` (any static server;
  `.claude/launch.json` "browser", and "converter-browser" for the converter's own page). Debug while the MCP server
  runs (it holds `PushPullMe.Mcp/bin/Release`).
- Single-threaded: heavy work (`Task.Run`, recognising a mesh) runs on the page's thread and the view waits for it.
- Avalonia's warnings and errors go to the browser console (Trace → Console).
