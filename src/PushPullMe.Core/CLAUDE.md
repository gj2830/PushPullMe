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
  - ⇕ on a cylinder changes its radius; ⇕ on the end face of a turned piece follows the turn.
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
