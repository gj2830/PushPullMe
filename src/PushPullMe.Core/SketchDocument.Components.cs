using RsDocx.Core;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

public sealed partial class SketchDocument
{
    /// <summary>The instances of a component (groups with it as their <see cref="SketchGroup.Component"/>).</summary>
    public List<SketchGroup> Instances(int component) => Groups.Where(g => g.Component == component).ToList();

    /// <summary>
    /// A new component (as SketchUp's Make Component) of these bodies and groups: a group as <see cref="MakeGroup"/> makes
    /// it, the first instance of a component of its own, standing where its shape stands. Returns the group.
    /// </summary>
    public (SketchDocument Doc, int Group) MakeComponent(IReadOnlyCollection<GroupUnit> units, string? name = null)
    {
        var (doc, g) = MakeGroup(units, name);
        int component = Groups.Max(x => x.Component) is { } c ? c + 1 : 1;
        return (doc.WithMeta(doc.OwnNames(), doc.Groups.Select(x => x.Id == g ? x with { Component = component, Place = Placement.Identity } : x).ToList(), doc.OwnGroups()), g);
    }

    /// <summary>An instance made unique (SketchUp's Make Unique): a component of its own from now on, its shape as it is.</summary>
    public SketchDocument MakeUnique(int group)
    {
        if (Group(group)?.Component is not { } c) throw new PushPullException(Lang.T("Dit is geen component.", "This is not a component."));
        if (Instances(c).Count < 2) throw new PushPullException(Lang.T("Dit is al het enige exemplaar van zijn component.", "This is the only instance of its component already."));
        int fresh = Groups.Max(x => x.Component)!.Value + 1;
        return WithMeta(OwnNames(), Groups.Select(x => x.Id == group ? x with { Component = fresh } : x).ToList(), OwnGroups());
    }

    /// <summary>The component instance group <paramref name="open"/> lies in (it, or the nearest group round it that is
    /// one), or null.</summary>
    public int? InstanceAround(int? open)
    {
        for (int? g = open; g is { } id; g = Group(id)?.Parent)
            if (Group(id)?.Component != null) return id;
        return null;
    }

    /// <summary>
    /// The other instances of the component that instance <paramref name="source"/> (or the instance round the group with
    /// that id; null: none) belongs to made the same as it again, each where it stands: after something changed in it.
    /// Their bodies are replaced in order (the first by a copy of the first, …), so the bodies outside keep their numbers
    /// as long as the instance keeps its number of bodies; with its colours, names and the groups inside it.
    /// </summary>
    public SketchDocument SyncComponent(int? source)
    {
        if (InstanceAround(source) is not { } s) return this;
        var from = Group(s)!;
        var others = Groups.Where(g => g.Component == from.Component && g.Id != s && !IsInside(g.Id, s) && !IsInside(s, g.Id)).ToList();
        if (others.Count == 0) return this;
        var src = Members(s);
        var srcGroups = Groups.Where(g => g.Id != s && IsInside(g.Id, s)).ToList();
        var back = (from.Place ?? Placement.Identity).Inverse();

        var bodies = Bodies.ToList();
        var curved = Curved.ToList();
        var colors = Enumerable.Range(0, Bodies.Count).Select(b => b < Colors.Count ? Colors[b] : new Dictionary<int, uint>()).ToList();
        var names = OwnNames(); var bodyGroups = OwnGroups();
        var groups = Groups.ToList();
        var gone = new HashSet<int>();
        int next = Groups.Max(g => g.Id) + 1;
        foreach (var to in others)
        {
            var move = back.Then(to.Place ?? Placement.Identity);
            // (the groups inside it made anew, as those inside the source; with their own numbers when they are built the
            // same way, so they keep them)
            var mineGroups = Groups.Where(g => g.Id != to.Id && IsInside(g.Id, to.Id)).ToList();
            var old = mineGroups.Select(g => g.Id).ToHashSet();
            groups.RemoveAll(g => old.Contains(g.Id));
            bool same = mineGroups.Count == srcGroups.Count;
            var ids = same ? srcGroups.Select((g, k) => (g.Id, k)).ToDictionary(x => x.Id, x => mineGroups[x.k].Id) : [];
            same = same && srcGroups.Select((g, k) => g.Parent == s ? mineGroups[k].Parent == to.Id : mineGroups[k].Parent == ids[g.Parent!.Value]).All(x => x);
            if (!same) ids = srcGroups.ToDictionary(g => g.Id, _ => next++);
            foreach (var g in srcGroups)
                groups.Add(g with { Id = ids[g.Id], Parent = g.Parent == s ? to.Id : ids[g.Parent!.Value], Place = g.Place is { } p ? p.Then(move) : null });
            var mine = Members(to.Id);
            for (int k = 0; k < Math.Max(src.Count, mine.Count); k++)
            {
                if (k >= src.Count) { gone.Add(mine[k]); continue; }
                int b = src[k];
                int at = k < mine.Count ? mine[k] : bodies.Count;
                if (at == bodies.Count) { bodies.Add(null!); curved.Add(null!); colors.Add(null!); names.Add(null); bodyGroups.Add(null); }
                bodies[at] = BrepTransform.Placed(Bodies[b], move);
                curved[at] = PlacedTriangles(Curved[b], move);
                colors[at] = colors[b];
                names[at] = names[b];
                bodyGroups[at] = bodyGroups[b] == s ? to.Id : ids[bodyGroups[b]!.Value];
            }
        }
        var keep = Enumerable.Range(0, bodies.Count).Where(b => !gone.Contains(b)).ToList();
        return new SketchDocument
        {
            Bodies = keep.Select(b => bodies[b]).ToList(),
            Curved = keep.Select(b => curved[b]).ToList(),
            Colors = keep.Select(b => colors[b]).ToList(),
            Lines = Lines,
            Names = keep.Select(b => names[b]).ToList(),
            Groups = Used(groups, keep.Select(b => bodyGroups[b])),
            BodyGroups = keep.Select(b => bodyGroups[b]).ToList(),
        };
    }

    /// <summary>
    /// The instances whose shape changed since <paramref name="before"/> (their bodies, colours or names; not where they
    /// stand, nor a new instance) made in the others of their component too (see <see cref="SyncComponent"/>), the outer
    /// ones first (an instance inside another one is then made in all the others' copies of it too). Whatever changed
    /// it: a tool in the open instance, paint on a closed one, the AI assistant.
    /// </summary>
    public SketchDocument SyncChanged(SketchDocument before)
    {
        int Depth(int id) { int n = 0; for (int? g = Group(id)?.Parent; g is { } p; g = Group(p)?.Parent) n++; return n; }
        var changed = Groups.Where(g => g.Component != null && before.Group(g.Id) is { } was && was.Component == g.Component && Equals(was.Place, g.Place) && !SameShape(before, g.Id))
            .Select(g => g.Id).OrderBy(Depth).ToList();
        var doc = this;
        var done = new HashSet<int>();
        foreach (int id in changed)
            if (doc.Group(id) is { Component: { } c } && done.Add(c)) doc = doc.SyncComponent(id);
        return doc;
    }

    /// <summary>Whether group <paramref name="g"/> holds the same bodies here as in <paramref name="before"/>, in the same
    /// order, with the same colours and names.</summary>
    private bool SameShape(SketchDocument before, int g)
    {
        var now = Members(g); var was = before.Members(g);
        if (now.Count != was.Count) return false;
        for (int k = 0; k < now.Count; k++)
        {
            int a = now[k], b = was[k];
            if (!ReferenceEquals(Bodies[a], before.Bodies[b]) || (a < Names.Count ? Names[a] : null) != (b < before.Names.Count ? before.Names[b] : null)) return false;
            var ca = a < Colors.Count ? Colors[a] : null; var cb = b < before.Colors.Count ? before.Colors[b] : null;
            if ((ca?.Count ?? 0) != (cb?.Count ?? 0) || ca != null && ca.Any(kv => cb == null || !cb.TryGetValue(kv.Key, out var v) || v != kv.Value)) return false;
        }
        return true;
    }

    /// <summary>
    /// The places of the instances of each component relative to one of them (<see cref="Placement"/>s), for the
    /// components a file can keep as one part placed more than once: more than one instance, all differing only by a turn
    /// and a move (a file cannot place a part mirrored or larger). By group id.
    /// </summary>
    internal Dictionary<int, Placement> SharedPlaces()
    {
        var places = new Dictionary<int, Placement>();
        foreach (var c in Groups.Where(g => g.Component != null).GroupBy(g => g.Component!.Value))
        {
            var back = (c.First().Place ?? Placement.Identity).Inverse();
            var rel = c.Select(g => (g.Id, P: back.Then(g.Place ?? Placement.Identity))).ToList();
            if (rel.Count > 1 && rel.All(x => x.P.Rigid))
                foreach (var (id, p) in rel) places[id] = p;
        }
        return places;
    }

    /// <summary>
    /// The model as DesignSpark gets it (see <see cref="RsDocxComposer.Compose(IReadOnlyList{BrepBody}, RsDocx.Core.Package.RsDocxPackage?, IReadOnlyList{string?}?, IReadOnlyList{RsDocxPart}?)"/>):
    /// every group a part (a DesignSpark component) used once, where it is; a component (see <see cref="SharedPlaces"/>)
    /// one part with the bodies of the instance met first (its own coordinates the world's there), used at every instance;
    /// the bodies of the other instances not written. The bodies with their names and colours, in the parts' order; no
    /// parts without groups.
    /// </summary>
    internal (List<BrepBody> Bodies, List<string?> Names, List<IReadOnlyDictionary<int, uint>> Colors, List<RsDocxPart>? Parts) RsDocxParts()
    {
        var ownNames = OwnNames();
        IReadOnlyDictionary<int, uint> ColorOfBody(int b) => b < Colors.Count ? Colors[b] : new Dictionary<int, uint>();
        if (Groups.Count == 0) return (Bodies.ToList(), ownNames, Enumerable.Range(0, Bodies.Count).Select(ColorOfBody).ToList(), null);
        var shared = SharedPlaces();
        var bodies = new List<BrepBody>(); var names = new List<string?>(); var colors = new List<IReadOnlyDictionary<int, uint>>();
        var parts = new List<(string? Name, List<int> Bodies, List<RsDocxUse> Uses)> { (null, [], []) };
        var partOf = new Dictionary<int, (int Part, Placement At)>();
        int NewPart(string name) { parts.Add((name, [], [])); return parts.Count - 1; }
        void Fill(int part, int? group)
        {
            for (int b = 0; b < Bodies.Count; b++)
                if (GroupOf(b) == group)
                {
                    parts[part].Bodies.Add(bodies.Count);
                    bodies.Add(Bodies[b]); names.Add(ownNames[b]); colors.Add(ColorOfBody(b));
                }
            foreach (var g in GroupsIn(group))
                if (g.Component is { } c && shared.TryGetValue(g.Id, out var at))
                {
                    if (!partOf.TryGetValue(c, out var made))
                    {
                        partOf[c] = made = (NewPart(GroupName(g.Id)), at);
                        Fill(made.Part, g.Id);
                    }
                    // (the part's coordinates are the world's at the instance met first)
                    parts[part].Uses.Add(new RsDocxUse(made.Part, Matrix(made.At.Inverse().Then(at))));
                }
                else
                {
                    int p = NewPart(GroupName(g.Id));
                    Fill(p, g.Id);
                    parts[part].Uses.Add(new RsDocxUse(p));
                }
        }
        Fill(0, null);
        return (bodies, names, colors, parts.Select(p => new RsDocxPart(p.Name, p.Bodies, p.Uses)).ToList());
    }

    /// <summary>A placement as a 4×4 matrix by rows (the move in the last column), as DesignSpark keeps one.</summary>
    private static double[] Matrix(Placement p) =>
        // (rounded to a nanometre: a quarter turn gives 6e-17 for 0)
        new double[] { p.X.X, p.Y.X, p.Z.X, p.O.X, p.X.Y, p.Y.Y, p.Z.Y, p.O.Y, p.X.Z, p.Y.Z, p.Z.Z, p.O.Z, 0, 0, 0, 1 }.Select(x => Math.Round(x, 12) + 0.0).ToArray();

    /// <summary>The triangles of curved faces put where <paramref name="m"/> says (turned round after a mirror, so they
    /// still face outward).</summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>> PlacedTriangles(IReadOnlyDictionary<int, IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>> t, Placement m) =>
        t.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(Vec3, Vec3, Vec3)>)kv.Value
            .Select(x => m.Mirrors ? (m.Point(x.A), m.Point(x.C), m.Point(x.B)) : (m.Point(x.A), m.Point(x.B), m.Point(x.C))).ToList());
}
