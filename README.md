# PushPullMe

[![Build and test](https://github.com/gj2830/PushPullMe/actions/workflows/build.yml/badge.svg)](https://github.com/gj2830/PushPullMe/actions/workflows/build.yml)

*[Nederlands hieronder](#nederlands)*

A playful 3D modelling app in the spirit of SketchUp, with the habits of DesignSpark Mechanical. It works with exact
geometry (planes, cylinders, cones, spheres, tori, free forms), saves as **STEP** (its main format, read by nearly every
CAD program, and opened again from them) and reads and writes DesignSpark files (`.rsdocx`) through
[StlConverter](https://github.com/gj2830/StlConverter), which comes along as a submodule. STL and 3MF can be imported
too. The interface is in English or Dutch; switch with the 🌐 button.

![A simple birdhouse, drawn from an empty sheet with push/pull, a work plane and paint, saved as .rsdocx and opened again](docs/birdhouse.png)

The birdhouse is in [examples/birdhouse.rsdocx](examples/birdhouse.rsdocx): open it in PushPullMe or in DesignSpark Mechanical.

## What it can do

- **Start drawing right away** on an empty work sheet: rectangles, circles and lines on the ground (or on any face)
  become faces; push/pull turns them into blocks and cylinders. Overlapping shapes divide each other, as in other
  modellers.
- **Work plane** (⊞): put it level with a face, or move and turn it with its handle (arrows, arcs, a ball), and draw
  beside the model on it.
- **Push/pull**, also cylinders, roundings and faces without thickness; "level with this face" while dragging.
- **Draw**: lines, rectangles (also rotated), circles, polygons, arcs (2-point, 3-point, from the centre), pies,
  freehand, 3D text, loose lines that close into faces.
- **Edit**: offset, round edges, move, rotate, scale, mirror, Follow Me (round an axis, also a whole turn, or along a
  path), union / subtract / intersect / trim / split (also with round faces), outer shell.
- **Measure and annotate**: tape measure and guides, protractor, dimensions, text labels, axes, section plane, hiding
  parts, paint and the colour picker.
- **Look around**: orbit freely all the way round, pan, zoom to the mouse, zoom window, position camera, look around,
  walk. Drawn with DirectX 11 (HelixToolkit.Wpf.SharpDX).
- Every tool works by dragging (press, drag, release) as well as by click, move, click; distances and angles can be
  typed.

![Drawing on the ground from an empty sheet: a block with a hole and a peg, beside it a loose face](docs/ground.png)

![The work plane upright against the front face, with its handle; a cylinder drawn on it beside the model](docs/workplane.png)

![A DesignSpark file with its colours](docs/colours.png)

## Download

[Releases](https://github.com/gj2830/PushPullMe/releases) has a zip for Windows (64-bit). Unpack it and start
`PushPullMe.exe`; .NET does not need to be installed separately.

## Build it yourself

You need Windows and the .NET 10 SDK.

```
git clone --recursive https://github.com/gj2830/PushPullMe.git
cd PushPullMe
dotnet build src/PushPullMe -c Release
```

The app is then in `src/PushPullMe/bin/Release/net10.0-windows/PushPullMe.exe`. Cloned without `--recursive`? Fetch
the converter with `git submodule update --init`.

Tests: `dotnet test tests/PushPullMe.Tests`.

## Working with an AI assistant (MCP)

PushPullMe can let an AI assistant work along in its window through the
[Model Context Protocol](https://modelcontextprotocol.io): any MCP client (Claude Code, Claude Desktop, VS Code, Cursor,
…) can look at the model and change it: push/pull, draw, move, group, paint, open and save, and take a picture of the
view. It is **off** until you tick **🤖 AI assistant may work along** (bottom right). You see everything it does in the
status bar, and every change can be undone (Ctrl+Z); before opening over unsaved work or overwriting a file it asks you.
To show the model all round, the assistant can turn the camera round it (screenshot with an azimuth and elevation): the
model keeps its size from every side and is not changed.
It only listens on your own PC, to your own Windows account (a named pipe; no network port).

The MCP server is `PushPullMe.Mcp.exe`, next to `PushPullMe.exe` (stdio transport). Tell your client to start it:

- **Claude Code**: in this folder `.mcp.json` already does (built Release); elsewhere
  `claude mcp add pushpullme -- "C:\path\to\PushPullMe.Mcp.exe"`.
- **Claude Desktop** (`claude_desktop_config.json`):
  `{"mcpServers": {"pushpullme": {"command": "C:\\path\\to\\PushPullMe.Mcp.exe"}}}`.
- **VS Code** (`.vscode/mcp.json`):
  `{"servers": {"pushpullme": {"type": "stdio", "command": "C:\\path\\to\\PushPullMe.Mcp.exe"}}}`.

## Nederlands

Een speels tekenprogramma voor 3D-vormen in de stijl van SketchUp, met de gewoontes van DesignSpark Mechanical. Het
werkt met exacte vormen (vlakken, cilinders, kegels, bollen, ringen) en leest en schrijft DesignSpark-bestanden
(`.rsdocx`) via [StlConverter](https://github.com/gj2830/StlConverter), dat als submodule meekomt. Het hoofdformaat is
**STEP**: dat lezen vrijwel alle CAD-programma's, en STEP uit andere programma's opent PushPullMe ook. Ook STL en 3MF
kunnen worden ingelezen. Het programma is in het Engels en het Nederlands (🌐-knop).

Wat er kan: meteen tekenen op een leeg werkblad (rechthoeken, cirkels en lijnen op de grond worden vlakken, ⇕ maakt er
blokken van; overlappende vormen delen elkaar), een werkvlak (⊞) dat je gelijk legt met een vlak of verschuift en draait
met de greep, duwen/trekken (ook cilinders, afrondingen en vlakken zonder dikte), alle tekengereedschappen van
SketchUp, offset, afronden, verplaatsen, draaien, schalen, spiegelen, Follow Me (rond een as of langs een pad),
verenigen/aftrekken/doorsnede/wegsnijden/splitsen (ook met ronde vlakken), meten, maatlijnen, tekst, doorsnedevlak,
verbergen, verven, vrij draaien, zoomen naar de muis en lopen. Alles werkt met slepen en met klik-beweeg-klik; maten en
hoeken kun je typen.

Het vogelhuisje bovenaan staat in [examples/birdhouse.rsdocx](examples/birdhouse.rsdocx); het opent in PushPullMe en in
DesignSpark Mechanical.

**Een AI-assistent laten meewerken (MCP):** vink rechtsonder **🤖 AI-assistent mag meewerken** aan, en laat je
MCP-client (Claude Code, Claude Desktop, VS Code, …) `PushPullMe.Mcp.exe` starten (naast `PushPullMe.exe`); zie
hierboven. Alles wat de assistent doet zie je in de statusregel en kun je ongedaan maken; standaard staat het uit.

**Downloaden:** onder [Releases](https://github.com/gj2830/PushPullMe/releases) staat een zip voor Windows (64-bit);
uitpakken en `PushPullMe.exe` starten. **Zelf bouwen:** Windows en de .NET 10 SDK, zie hierboven.

## License / Licentie

[PolyForm Noncommercial 1.0.0](LICENSE): free to use, change and share for any noncommercial purpose, and
contributions are welcome; using it in a commercial product is not allowed. Versions up to and including v0.4 were
released under MIT.

Nederlands: vrij te gebruiken, aan te passen en te delen voor niet-commerciële doelen, en meewerken is welkom; gebruik in
een commercieel product mag niet. Versies tot en met v0.4 zijn onder MIT uitgebracht.
