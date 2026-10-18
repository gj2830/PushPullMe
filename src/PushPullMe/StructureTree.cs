using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PushPullMe.Core;
using RsDocx.Core.Brep;

namespace PushPullMe;

/// <summary>The kinds of drawing aids the structure tree lists under "Schets".</summary>
public enum SketchKind { Loose, Guides, Dimensions, Notes }

/// <summary>
/// The structure of the model as a tree (as DesignSpark's structure tree): its bodies ("Vaste vorm", or "Oppervlak" for
/// faces without thickness), each with its faces when unfolded (made only then: a mesh can have thousands), and the
/// drawing aids under "Schets". A box per line shows or hides it; a click on a line picks that body or face in the view.
/// The same control sits in the window or in a window of its own.
/// </summary>
internal sealed class StructureTree : UserControl
{
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private SketchDocument? _doc;
    private Func<int, bool> _bodyShown = _ => true;
    private Func<SketchKind, bool> _sketchShown = _ => true;

    /// <summary>A body's box was ticked or cleared: (body, shown).</summary>
    public event Action<int, bool>? BodyShown;
    /// <summary>A kind of drawing aid's box was ticked or cleared.</summary>
    public event Action<SketchKind, bool>? SketchShown;
    /// <summary>A line was clicked: (body, face), or a whole body (face null).</summary>
    public event Action<int, int?>? Picked;
    /// <summary>A body was given another name (double click or F2 on it, Enter): (body, the name typed).</summary>
    public event Action<int, string>? Renamed;

    /// <summary>Set while the tree itself changes what is selected (not the user).</summary>
    private bool _quiet;

    public StructureTree()
    {
        Content = _tree;
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (_quiet || e.NewValue is not TreeViewItem { Tag: Pick p }) return;
            Picked?.Invoke(p.Body, p.Face);
        };
        // F2 on a body: its name to change
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F2 && _tree.SelectedItem is TreeViewItem { Tag: Pick { Face: null } p } item) { BeginRename(item, p.Body); e.Handled = true; }
        };
    }

    /// <summary>What a line stands for.</summary>
    private sealed record Pick(int Body, int? Face);

    /// <summary>The tree for the document (null: empty), with what is shown now and how many drawing aids there are.</summary>
    public void Show(SketchDocument? doc, Func<int, bool> bodyShown, Func<SketchKind, bool> sketchShown, IReadOnlyDictionary<SketchKind, int> counts)
    {
        _doc = doc; _bodyShown = bodyShown; _sketchShown = sketchShown;
        // (what was unfolded stays unfolded)
        var open = _tree.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded).Select(i => i.Tag).OfType<Pick>().Select(p => p.Body).ToHashSet();
        bool sketchOpen = _tree.Items.OfType<TreeViewItem>().Any(i => i.Tag is "sketch" && i.IsExpanded);
        _quiet = true;
        _tree.Items.Clear();
        if (doc != null)
            for (int b = 0; b < doc.Bodies.Count; b++)
            {
                int body = b;
                var item = Line(BodyName(doc, b), bodyShown(b), shown => BodyShown?.Invoke(body, shown));
                item.Tag = new Pick(b, null);
                // (a double click on its name: the name to change)
                if (item.Header is StackPanel { Children: [_, TextBlock name] }) name.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { BeginRename(item, body); e.Handled = true; } };
                // (its faces when unfolded: a placeholder till then)
                item.Items.Add(new TreeViewItem { Header = "…" });
                item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) Faces(item, body); };
                _tree.Items.Add(item);
                if (open.Contains(b)) item.IsExpanded = true;
            }
        var sketch = new TreeViewItem { Header = Label("✎ " + Lang.T("Schets", "Sketch"), Brushes.Black), Tag = "sketch", IsExpanded = sketchOpen };
        foreach (var (kind, name) in new[] { (SketchKind.Loose, Lang.T("Losse lijnen", "Loose lines")), (SketchKind.Guides, Lang.T("Hulplijnen", "Guide lines")),
                     (SketchKind.Dimensions, Lang.T("Maatlijnen", "Dimensions")), (SketchKind.Notes, Lang.T("Teksten", "Texts")) })
        {
            var k = kind;
            sketch.Items.Add(Line($"{name} ({counts.GetValueOrDefault(kind)})", sketchShown(kind), shown => SketchShown?.Invoke(k, shown)));
        }
        _tree.Items.Add(sketch);
        _quiet = false;
    }

    /// <summary>The name of a body as DesignSpark calls it, numbered: "Vaste vorm 1", "Oppervlak 2".</summary>
    public static string BodyName(SketchDocument doc, int b) => doc.BodyName(b);

    /// <summary>A face's line: its number (as the status bar and the log give it) and what it is.</summary>
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

    /// <summary>A body's name in a box to change it: Enter (or clicking elsewhere) keeps what is typed, Esc leaves it.</summary>
    private void BeginRename(TreeViewItem item, int body)
    {
        if (item.Header is not StackPanel row || row.Children.Count < 2 || row.Children[1] is not TextBlock label) return;
        var box = new TextBox { Text = label.Text, MinWidth = 140, Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children[1] = box;
        box.Focus(); box.SelectAll();
        bool done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            row.Children[1] = label;
            if (keep && box.Text.Trim() != label.Text) Renamed?.Invoke(body, box.Text.Trim());
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
    }

    /// <summary>The line of the body or face picked in the view selected here too (null: nothing).</summary>
    public void Select(int? body, int? face)
    {
        _quiet = true;
        try
        {
            if (body is not int b || b >= _tree.Items.Count - 1 || _tree.Items[b] is not TreeViewItem item)
            {
                if (_tree.SelectedItem is TreeViewItem sel) sel.IsSelected = false;
                return;
            }
            if (face is int f)
            {
                Faces(item, b);
                item.IsExpanded = true;
                if (f < item.Items.Count && item.Items[f] is TreeViewItem fi) { fi.IsSelected = true; fi.BringIntoView(); }
            }
            else { item.IsSelected = true; item.BringIntoView(); }
        }
        finally { _quiet = false; }
    }

    private static TreeViewItem Line(string text, bool shown, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Focusable = false };
        box.Click += (_, _) => changed(box.IsChecked == true);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(Label(text, Brushes.Black));
        return new TreeViewItem { Header = row };
    }

    private static TextBlock Label(string text, Brush brush) => new() { Text = text, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
}
