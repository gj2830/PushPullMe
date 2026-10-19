namespace PushPullMe.Editor.Session;

/// <summary>The kinds of drawing aids the structure tree shows (each with its box).</summary>
public enum SketchKind { Loose, Guides, Dimensions, Notes }

/// <summary>What the structure tree's menu asks for a group: open it to work in, explode it, make a group or a component
/// (of what is picked, or of the body the menu was opened on), or make a component instance unique.</summary>
public enum GroupAction { Open, Explode, Make, MakeComponent, MakeUnique }
