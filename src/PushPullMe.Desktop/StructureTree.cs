using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PushPullMe.Core;
using PushPullMe.Editor.Session;
using Key = Avalonia.Input.Key;
using RsDocx.Core.Brep;

namespace PushPullMe.Desktop;

/// <summary>
/// The structure of the model as a tree (as DesignSpark's structure tree), as the WPF window has it: its groups with what
/// is in them, its bodies, each with its faces when unfolded (made only then: a mesh can have thousands), and the drawing
/// aids under "Schets". A box per line shows or hides it; a click on a line picks that body or face in the view; a double
/// click or F2 on a name changes it; a right click opens the group menu.
/// </summary>
internal sealed class StructureTree : UserControl
{
    private readonly TreeView _tree = new() { Background = Brushes.Transparent };
    private SketchDocument? _doc;
    private Func<int, bool> _bodyShown = _ => true;

    public event Action<int, bool>? BodyShown;
    public event Action<SketchKind, bool>? SketchShown;
    public event Action<int, int?>? Picked;
    public event Action<int, string>? Renamed;
    public event Action<int>? GroupPicked;
    public event Action<int, string>? GroupRenamed;
    public event Action<GroupAction, int?, int?>? GroupMenu;

    private readonly Dictionary<int, TreeViewItem> _bodyItems = new();

    /// <summary>Set while the tree itself changes what is selected (not the user).</summary>
    private bool _quiet;

    public StructureTree()
    {
        Content = _tree;
        _tree.SelectionChanged += (_, _) =>
        {
            if (_quiet) return;
            if (_tree.SelectedItem is TreeViewItem { Tag: Pick p }) Picked?.Invoke(p.Body, p.Face);
            else if (_tree.SelectedItem is TreeViewItem { Tag: GroupLine gl }) GroupPicked?.Invoke(gl.Group);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key != Key.F2) return;
            if (_tree.SelectedItem is TreeViewItem { Tag: Pick { Face: null } p } item) { BeginRename(item, name => Renamed?.Invoke(p.Body, name)); e.Handled = true; }
            else if (_tree.SelectedItem is TreeViewItem { Tag: GroupLine g } gi) { BeginRename(gi, name => GroupRenamed?.Invoke(g.Group, name)); e.Handled = true; }
        };
    }

    private sealed record Pick(int Body, int? Face);
    private sealed record GroupLine(int Group);
    private const string SketchTag = "sketch";

    public void Show(SketchDocument? doc, Func<int, bool> bodyShown, Func<SketchKind, bool> sketchShown, IReadOnlyDictionary<SketchKind, int> counts)
    {
        _doc = doc; _bodyShown = bodyShown;
        // (what was unfolded stays unfolded)
        var open = All(_tree.Items).Where(i => i.IsExpanded).Select(i => i.Tag).Where(t => t is Pick { Face: null } or GroupLine or SketchTag).ToHashSet();
        _quiet = true;
        _tree.Items.Clear(); _bodyItems.Clear();
        if (doc != null) Fill(_tree.Items, doc, null, open);
        var sketch = new TreeViewItem { Header = Label("✎ " + Lang.T("Schets", "Sketch"), Brushes.Black), Tag = SketchTag, IsExpanded = open.Contains(SketchTag) };
        foreach (var (kind, name) in new[] { (SketchKind.Loose, Lang.T("Losse lijnen", "Loose lines")), (SketchKind.Guides, Lang.T("Hulplijnen", "Guide lines")),
                     (SketchKind.Dimensions, Lang.T("Maatlijnen", "Dimensions")), (SketchKind.Notes, Lang.T("Teksten", "Texts")) })
        {
            var k = kind;
            sketch.Items.Add(Line($"{name} ({counts.GetValueOrDefault(kind)})", sketchShown(kind), shown => SketchShown?.Invoke(k, shown)));
        }
        _tree.Items.Add(sketch);
        _quiet = false;
    }

    private void Fill(ItemCollection items, SketchDocument doc, int? parent, HashSet<object?> open)
    {
        foreach (var g in doc.GroupsIn(parent))
        {
            int group = g.Id;
            var members = doc.Members(group);
            var item = Line("📦 " + doc.GroupName(group), members.All(_bodyShown), shown => { foreach (int m in members) BodyShown?.Invoke(m, shown); });
            item.Tag = new GroupLine(group);
            if (item.Header is StackPanel { Children: [_, TextBlock name] })
                name.DoubleTapped += (_, e) => { BeginRename(item, n => GroupRenamed?.Invoke(group, n.StartsWith("📦", StringComparison.Ordinal) ? n[2..].Trim() : n)); e.Handled = true; };
            item.ContextMenu = Menu((Lang.T("Openen om erin te werken", "Open to work in it"), GroupAction.Open, group, null),
                (Lang.T("Groep opheffen", "Explode group"), GroupAction.Explode, group, null),
                (Lang.T("Groep maken van wat gekozen is", "Make a group of what is picked"), GroupAction.Make, null, null));
            Fill(item.Items, doc, group, open);
            items.Add(item);
            if (open.Contains(item.Tag)) item.IsExpanded = true;
        }
        for (int b = 0; b < doc.Bodies.Count; b++)
        {
            if (doc.GroupOf(b) != parent) continue;
            int body = b;
            var item = Line(doc.BodyName(b), _bodyShown(b), shown => BodyShown?.Invoke(body, shown));
            item.Tag = new Pick(b, null);
            if (item.Header is StackPanel { Children: [_, TextBlock name] }) name.DoubleTapped += (_, e) => { BeginRename(item, n => Renamed?.Invoke(body, n)); e.Handled = true; };
            item.ContextMenu = Menu((Lang.T("Groep maken (Ctrl+G)", "Make group (Ctrl+G)"), GroupAction.Make, null, body));
            // (its faces when unfolded: a placeholder till then)
            item.Items.Add(new TreeViewItem { Header = "…" });
            item.PropertyChanged += (_, e) => { if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded) Faces(item, body); };
            items.Add(item);
            _bodyItems[b] = item;
            if (open.Contains(item.Tag)) item.IsExpanded = true;
        }
    }

    private ContextMenu Menu(params (string Text, GroupAction What, int? Group, int? Body)[] entries)
    {
        var menu = new ContextMenu();
        foreach (var (text, what, group, body) in entries)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => GroupMenu?.Invoke(what, group, body);
            menu.Items.Add(mi);
        }
        return menu;
    }

    private static IEnumerable<TreeViewItem> All(ItemCollection items)
    {
        foreach (var i in items.OfType<TreeViewItem>())
        {
            yield return i;
            foreach (var j in All(i.Items)) yield return j;
        }
    }

    private static string FaceName(BrepFace f, int i) => Lang.T("Vlak", "Face") + $" {i} · " + f.Surface switch
    {
        BrepPlane => Lang.T("plat", "flat"),
        BrepCylinder => Lang.T("cilinder", "cylinder"),
        BrepCone => Lang.T("kegel", "cone"),
        BrepSphere => Lang.T("bol", "sphere"),
        BrepTorus => Lang.T("torus", "torus"),
        BrepNurbsSurface => Lang.T("vrije vorm", "free form"),
        _ => "?",
    };

    private void Faces(TreeViewItem item, int body)
    {
        if (_doc == null || body >= _doc.Bodies.Count || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
        item.Items.Clear();
        var faces = _doc.Bodies[body].Faces;
        for (int f = 0; f < faces.Count; f++)
            item.Items.Add(new TreeViewItem { Header = Label(FaceName(faces[f], f), Brushes.DimGray), Tag = new Pick(body, f) });
    }

    private static void BeginRename(TreeViewItem item, Action<string> renamed)
    {
        if (item.Header is not StackPanel row || row.Children.Count < 2 || row.Children[1] is not TextBlock label) return;
        string old = label.Text is { } t && t.StartsWith("📦", StringComparison.Ordinal) ? t[2..].Trim() : label.Text ?? "";
        var box = new TextBox { Text = old, MinWidth = 140, Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children[1] = box;
        box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
        bool done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            row.Children[1] = label;
            if (keep && box.Text?.Trim() is { } text && text != old) renamed(text);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Finish(true);
    }

    public void Select(int? body, int? face)
    {
        _quiet = true;
        try
        {
            if (body is not int b || !_bodyItems.TryGetValue(b, out var item)) { _tree.SelectedItem = null; return; }
            for (var up = item.Parent as TreeViewItem; up != null; up = up.Parent as TreeViewItem) up.IsExpanded = true;
            if (face is int f)
            {
                Faces(item, b);
                item.IsExpanded = true;
                if (f < item.Items.Count && item.Items[f] is TreeViewItem fi) { _tree.SelectedItem = fi; fi.BringIntoView(); }
            }
            else { _tree.SelectedItem = item; item.BringIntoView(); }
        }
        finally { _quiet = false; }
    }

    public void SelectGroup(int? group)
    {
        _quiet = true;
        try
        {
            var item = group is { } g ? All(_tree.Items).FirstOrDefault(i => i.Tag is GroupLine gl && gl.Group == g) : null;
            if (item == null) { _tree.SelectedItem = null; return; }
            for (var up = item.Parent as TreeViewItem; up != null; up = up.Parent as TreeViewItem) up.IsExpanded = true;
            _tree.SelectedItem = item; item.BringIntoView();
        }
        finally { _quiet = false; }
    }

    private static TreeViewItem Line(string text, bool shown, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Focusable = false, MinWidth = 0 };
        box.Click += (_, _) => changed(box.IsChecked == true);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(Label(text, Brushes.Black));
        return new TreeViewItem { Header = row };
    }

    private static TextBlock Label(string text, IBrush brush) => new() { Text = text, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
}
