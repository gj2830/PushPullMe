# PushPullMe.Core: the model and its operations

- `SketchDocument` is immutable: every operation returns a new document (undo = keep the old one). Partials for
  groups, components and Revit. Geometry is `BrepBody` from the converter's RsDocx.Core (exact surfaces, metres).
- Refuse rather than repair: an operation that would give a self-intersection, a negative volume or a topology error
  (`XtBrepBuilder.TopologyError`) throws a `PushPullException` with a bilingual message (`Lang.T`) and leaves the
  model as it was.
- Push/Pull (`PushPull*.cs`): copy the face's loops along its normal, a side face per edge, merge side faces that lie
  in a neighbour's plane; a rounding next to the face moves along (cut or extended); a circle edge gets a cylinder
  that merges with the existing one. Pulling back to exactly the same place gives the old model back.
- Behaviour taken from DesignSpark (decided, keep):
  - two pieces that touch only along an edge stay separate bodies;
  - a pulled piece that touches another with a face joins it (`JoinTouching`), only when pulling (distance > 0), only
    when the operation is set (not while dragging), only within the box round the new faces, only within one group;
    the "join" box (default on) turns it off;
  - ⇕ on a cylinder changes its radius; ⇕ on the end face of a turned piece follows the turn; with Ctrl at the click
    it goes straight along the normal as a prism (`straight`, `Apply(prism:)`: a side face at every edge, no round
    neighbour extended), as DesignSpark extends it with a copy of the face;
  - ⇕ on the end face of a thread follows the helix (`ScrewEnd`): side edges and helical faces made anew from the end
    that stays (`ThreadShapes.Helix`/`Ruled`), turns from the angle round the axis (DesignSpark's helices climb within
    0.01 mm only); crossing tested against other parts only (its own flanks against each other take a minute).
- Follow Me along a path (`Sweep.cs`): the path is pieces (line or arc) from a face's outer edge or a run of loose
  lines (`OfLines`: on while exactly one line goes on; in one plane). Straight profile and path: mitred prisms (polyhedra)
  united; with an arc anywhere: one body made on piece by piece (prism push/pull of the end face, `Revolve` round an
  arc) because booleans between pieces meeting with a round cap hang; that needs smooth corners and an open path (a
  whole circle is one whole turn). The profile touches the path at a corner or an end, or lies round an end (a pipe).
- Names: `Names` per body (null = none); "Vaste vorm", "Oppervlak", "Solid", "Surface", "Body" (with or without a
  number) are standard names, not own names. A body that merges into another loses its name.
- Groups (`SketchGroup(Id, Name, Parent)`, `BodyGroups`) and components: an instance is a group with `Component` and
  `Place` (`Placement`); bodies stay in world coordinates, so every tool works unchanged; a change in one instance is
  copied to the others in their own place. Scaling along one axis with an instance in it is refused.
- Files: STEP (main format), .rsdocx (DesignSpark; groups as PartDefs/components, colours via `RsDocxColors`),
  STL (loses groups), STL/3MF in via the converter; Revit via `SketchDocument.Revit.cs`.
- Revit (`SketchDocument.Revit.cs`): points merged on a 2 nm grid; flat faces as rings, curved ones exact with drawing
  triangles; `EdgeCurves` (line, circle or ellipse that lies on all faces, else a chord); what fits nothing becomes
  triangles only when saving (`Saveable`). Material colour → face colour, alpha = 255·(1 − 0.75·t), never below a
  quarter. Storeys: `SketchStoreys.Of` — levels of the main model grouped by height (≤ `Span` 2 m), storey 0 = the one
  height 0 is on; other models' levels on the storey at their height (`Margin` 0.3 m); a storey cut runs from
  0.5 m under its level to 0.5 m under the next.
