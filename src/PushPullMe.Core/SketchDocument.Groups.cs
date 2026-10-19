using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Core;

/// <summary>A group of bodies (as SketchUp's): <paramref name="Id"/> stays the same through every change,
/// <paramref name="Name"/> its own name (null: "Groep n"), <paramref name="Parent"/> the group it is in (null: none).
/// A component instance (SketchUp's Component) has <paramref name="Component"/>: the groups with the same one are copies of
/// each other, each where its <paramref name="Place"/> puts the component's own shape (see
/// <see cref="SketchDocument.SyncComponent"/>).</summary>
public sealed record SketchGroup(int Id, string? Name, int? Parent, int? Component = null, Placement? Place = null);

/// <summary>What a click takes: a body on its own, or a whole group.</summary>
public readonly record struct GroupUnit(int? Body, int? Group)
{
    public static GroupUnit OfBody(int b) => new(b, null);
    public static GroupUnit OfGroup(int g) => new(null, g);
}

public sealed partial class SketchDocument
{
    /// <summary>The groups (see <see cref="SketchGroup"/>); a body is in one through <see cref="BodyGroups"/>.</summary>
    public IReadOnlyList<SketchGroup> Groups { get; init; } = [];

    /// <summary>Per body the group it is in directly (null, or missing: none). Carried along by <see cref="CarryMeta"/>.</summary>
    public IReadOnlyList<int?> BodyGroups { get; init; } = [];

    /// <summary>The group body <paramref name="b"/> is in directly, or null.</summary>
    public int? GroupOf(int b) => b < BodyGroups.Count ? BodyGroups[b] : null;

    public SketchGroup? Group(int id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>A group's name: its own, or "Groep n" (n its id).</summary>
    public string GroupName(int id) => Group(id) switch
    {
        { Name: { Length: > 0 } n } => n,
        { Component: { } c } => "Component " + c,
        _ => Lang.T("Groep", "Group") + $" {id}",
    };

    /// <summary>Whether group <paramref name="inner"/> is <paramref name="outer"/> or lies (deeper) inside it; any group
    /// lies inside null (the model itself).</summary>
    public bool IsInside(int? inner, int? outer)
    {
        if (outer == null) return true;
        for (int? g = inner; g != null; g = Group(g.Value)?.Parent)
            if (g == outer) return true;
        return false;
    }

    /// <summary>The bodies in a group, also those in the groups inside it.</summary>
    public List<int> Members(int group) => Enumerable.Range(0, Bodies.Count).Where(b => GroupOf(b) is { } g && IsInside(g, group)).ToList();

    /// <summary>The groups directly in <paramref name="parent"/> (null: at the top).</summary>
    public IEnumerable<SketchGroup> GroupsIn(int? parent) => Groups.Where(g => g.Parent == parent);

    /// <summary>
    /// What a click on body <paramref name="b"/> takes while group <paramref name="open"/> is open (null: none): the body
    /// itself when it lies directly in it, the group directly in it the body lies in otherwise; null when the body lies
    /// outside the open group (it cannot be taken then).
    /// </summary>
    public GroupUnit? UnitOf(int b, int? open)
    {
        int? g = GroupOf(b);
        if (g == open) return GroupUnit.OfBody(b);
        if (!IsInside(g, open)) return null;
        while (Group(g!.Value)?.Parent != open) g = Group(g.Value)!.Parent;
        return GroupUnit.OfGroup(g.Value);
    }

    /// <summary>The bodies of what a click takes.</summary>
    public List<int> BodiesOf(GroupUnit u) => u.Group is { } g ? Members(g) : [u.Body!.Value];

    /// <summary>The document with these names and groups (the rest as it is).</summary>
    public SketchDocument WithMeta(IReadOnlyList<string?> names, IReadOnlyList<SketchGroup> groups, IReadOnlyList<int?> bodyGroups) =>
        new() { Bodies = Bodies, Curved = Curved, Colors = Colors, Lines = Lines, Names = names, Groups = Used(groups, bodyGroups), BodyGroups = bodyGroups };

    /// <summary>The bodies' groups, one per body (null: none).</summary>
    private List<int?> OwnGroups() => Enumerable.Range(0, Bodies.Count).Select(GroupOf).ToList();

    /// <summary>The groups that still hold a body (directly or in a group inside them): empty ones go.</summary>
    private static List<SketchGroup> Used(IReadOnlyList<SketchGroup> groups, IEnumerable<int?> bodyGroups)
    {
        var used = new HashSet<int>();
        var byId = groups.ToDictionary(g => g.Id);
        foreach (var bg in bodyGroups)
            for (int? g = bg; g is { } id && byId.ContainsKey(id) && used.Add(id); g = byId[id].Parent) { }
        return groups.Where(g => used.Contains(g.Id)).ToList();
    }

    /// <summary>
    /// A new group (named <paramref name="name"/>, null: "Groep n") of these bodies and groups, in the group they share
    /// (all must lie directly in the same one, see <see cref="UnitOf"/>). Returns its id.
    /// </summary>
    public (SketchDocument Doc, int Group) MakeGroup(IReadOnlyCollection<GroupUnit> units, string? name = null)
    {
        if (units.Count == 0) throw new PushPullException(Lang.T("Kies eerst wat in de groep moet.", "First pick what goes into the group."));
        var parents = units.Select(u => u.Group is { } g ? Group(g)?.Parent : GroupOf(u.Body!.Value)).Distinct().ToList();
        if (parents.Count != 1) throw new PushPullException(Lang.T("Wat in een groep gaat moet in dezelfde groep liggen.", "What goes into a group must lie in the same group."));
        int id = Groups.Count == 0 ? 1 : Groups.Max(g => g.Id) + 1;
        var groups = Groups.Select(g => units.Any(u => u.Group == g.Id) ? g with { Parent = id } : g).Append(new SketchGroup(id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), parents[0])).ToList();
        var bodyGroups = OwnGroups();
        foreach (var u in units) if (u.Body is { } b) bodyGroups[b] = id;
        return (WithMeta(OwnNames(), groups, bodyGroups), id);
    }

    /// <summary>A group taken away (SketchUp's Explode): what was in it goes to the group it was in.</summary>
    public SketchDocument Ungroup(int group)
    {
        int? parent = Group(group)?.Parent;
        var groups = Groups.Where(g => g.Id != group).Select(g => g.Parent == group ? g with { Parent = parent } : g).ToList();
        var bodyGroups = OwnGroups().Select(g => g == group ? parent : g).ToList();
        return WithMeta(OwnNames(), groups, bodyGroups);
    }

    /// <summary>Whether a group name is only the standard one ("Groep 3", "Group"): no name of its own.</summary>
    public static bool IsStandardGroupName(string? name) => string.IsNullOrWhiteSpace(name)
        || System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(groep|group|component)( \d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A group given another name (empty, or the standard "Groep n": none of its own any more); a component
    /// instance with all the others of its component.</summary>
    public SketchDocument RenameGroup(int group, string? name)
    {
        string? own = IsStandardGroupName(name) ? null : name!.Trim();
        int? component = Group(group)?.Component;
        return WithMeta(OwnNames(), Groups.Select(g => g.Id == group || component != null && g.Component == component ? g with { Name = own } : g).ToList(), OwnGroups());
    }

    /// <summary>
    /// Every body of a group changed by <paramref name="op"/> (one of the whole-body changes: <see cref="RotateBody"/>,
    /// <see cref="ScaleBody"/>, <see cref="MirrorBody"/>, <see cref="MoveBody"/>; given the document, the body and whether
    /// to copy), or (<paramref name="copy"/>) a changed copy of the group added: a new group, with the groups inside it
    /// copied too and the copies of the bodies in them. Returns the group changed (the copy). <paramref name="motion"/>:
    /// the same change as a placement, for the component instances in the group (null: it is none, such as scaling along
    /// one axis; refused when the group holds an instance). A copied instance is another instance of its component.
    /// </summary>
    public (SketchDocument Doc, int Group) TransformGroup(int group, Func<SketchDocument, int, bool, (SketchDocument Doc, int Body)> op, bool copy, Placement? motion = null)
    {
        var members = Members(group);
        var names = OwnNames(); var bodyGroups = OwnGroups();
        if (motion == null && Groups.Any(g => g.Component != null && IsInside(g.Id, group)))
            throw new PushPullException(Lang.T("Een component kan zo niet veranderen (alle exemplaren hebben dezelfde vorm); schaal hem in alle richtingen, of maak hem eerst uniek.",
                "A component cannot change like this (all its instances have the same shape); scale it in all directions, or make it unique first."));
        // (the instances in it placed anew)
        SketchGroup Moved(SketchGroup g) => g.Component != null ? g with { Place = (g.Place ?? Placement.Identity).Then(motion!.Value) } : g;
        var groups = Groups.Select(g => !copy && IsInside(g.Id, group) ? Moved(g) : g).ToList();
        var doc = this;
        if (!copy)
        {
            foreach (int b in members) doc = op(doc, b, false).Doc;
            return (doc.WithMeta(names, groups, bodyGroups), group);
        }
        // the group and the groups inside it copied, with new ids
        int next = Groups.Max(g => g.Id) + 1;
        var ids = new Dictionary<int, int>();
        foreach (var g in Groups.Where(g => IsInside(g.Id, group))) ids[g.Id] = next++;
        foreach (var g in Groups.Where(g => ids.ContainsKey(g.Id)))
            groups.Add(Moved(g) with { Id = ids[g.Id], Parent = g.Id == group ? g.Parent : ids[g.Parent!.Value] });
        foreach (int b in members)
        {
            (doc, int nb) = op(doc, b, true);
            while (names.Count <= nb) { names.Add(null); bodyGroups.Add(null); }
            names[nb] = names[b];
            bodyGroups[nb] = ids[bodyGroups[b]!.Value];
        }
        return (doc.WithMeta(names, groups, bodyGroups), ids[group]);
    }

    /// <summary>A whole body moved by <paramref name="offset"/>, or a moved copy added (last); as <see cref="RotateBody"/>.</summary>
    public (SketchDocument Doc, int Body) MoveBody(int body, Vec3 offset, bool copy) =>
        (MoveWholeBody(body, copy, offset).Doc, copy ? Bodies.Count : body);

    /// <summary>
    /// <paramref name="after"/> with the names and groups of <paramref name="before"/> carried over (each unless
    /// <paramref name="after"/> has its own already): a body that did not change (the same object) keeps them; one that
    /// changed takes those of the body that was in its place, unless they went to another already (a body joined into
    /// another one loses its name, the one it joined keeps its own); a new body goes into group <paramref name="newInto"/>
    /// (the one open while it was made). Groups left empty go.
    /// </summary>
    public static SketchDocument CarryMeta(SketchDocument before, SketchDocument after, int? newInto = null)
    {
        // (nothing to carry)
        if (before.Names.All(n => n == null) && before.Groups.Count == 0) return after;
        var from = new int?[after.Bodies.Count];
        var used = new HashSet<int>();
        for (int a = 0; a < after.Bodies.Count; a++)
            for (int b = 0; b < before.Bodies.Count; b++)
                if (ReferenceEquals(after.Bodies[a], before.Bodies[b])) { from[a] = b; used.Add(b); break; }
        for (int a = 0; a < after.Bodies.Count; a++)
            if (from[a] == null && a < before.Bodies.Count && !used.Contains(a) && !after.Bodies.Any(x => ReferenceEquals(x, before.Bodies[a])))
            {
                from[a] = a;
                used.Add(a);
            }
        var names = after.Names.Count > 0 ? after.Names
            : from.Select(b => b is { } i && i < before.Names.Count ? before.Names[i] : null).ToList();
        IReadOnlyList<SketchGroup> groups; IReadOnlyList<int?> bodyGroups;
        if (after.BodyGroups.Count > 0) { groups = after.Groups; bodyGroups = after.BodyGroups; }
        else
        {
            groups = before.Groups;
            bodyGroups = from.Select(b => b is { } i ? before.GroupOf(i) : newInto is { } g && before.Group(g) != null ? g : null).ToList();
        }
        return new SketchDocument { Bodies = after.Bodies, Curved = after.Curved, Colors = after.Colors, Lines = after.Lines, Names = names, Groups = Used(groups, bodyGroups), BodyGroups = bodyGroups };
    }
}
