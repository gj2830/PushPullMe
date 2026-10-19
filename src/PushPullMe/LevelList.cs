using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PushPullMe.Core;
using PushPullMe.Editor.Session;

namespace PushPullMe;

/// <summary>
/// The storeys of a project read from Revit, under the structure tree, from the top down as a section through a building
/// is read: each a box (unticked: all its bodies hidden, of every model, besides what the tree hides; half: some of its
/// models' parts hidden), its number, the main model's levels in it and its height; unfolded (▸) a box per model for
/// that model's part of it. Not shown when the document has none.
/// </summary>
internal sealed class LevelList : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _head;
    private readonly IBrush _muted;

    /// <summary>The storeys unfolded (by number: they stay so when the list is made anew).</summary>
    private readonly HashSet<int> _open = new();

    /// <summary>A storey's box ticked (true) or not; the model null for the whole storey, else that model's part.</summary>
    public event Action<SketchStorey, string?, bool>? StoreyShown;

    public LevelList(IBrush muted)
    {
        _muted = muted;
        _head = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = muted, Margin = new Thickness(0, 10, 0, 4) };
        var pane = new DockPanel();
        DockPanel.SetDock(_head, Dock.Top);
        pane.Children.Add(_head);
        pane.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 280, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = pane;
        IsVisible = false;
    }

    private Func<SketchStorey, string?, bool?> _shown = (_, _) => true;
    private IReadOnlyList<SketchStorey> _storeys = [];

    /// <summary>The list made anew: these storeys, each ticked as <paramref name="shown"/> says.</summary>
    public void Show(IReadOnlyList<SketchStorey> storeys, Func<SketchStorey, string?, bool?> shown)
    {
        _storeys = storeys; _shown = shown;
        _head.Text = Lang.T("VERDIEPINGEN", "LEVELS");
        _rows.Children.Clear();
        IsVisible = storeys.Count > 0;
        foreach (var storey in storeys.OrderByDescending(s => s.Number))
        {
            _rows.Children.Add(StoreyRow(storey));
            if (_open.Contains(storey.Number))
                foreach (var (model, bodies) in storey.Parts)
                    _rows.Children.Add(PartRow(storey, model, bodies.Count));
        }
    }

    private Control StoreyRow(SketchStorey storey)
    {
        bool open = _open.Contains(storey.Number);
        // (unfolding, only with more models in it)
        var fold = new TextBlock
        {
            Text = storey.Parts.Count > 1 ? (open ? "▾" : "▸") : "", Width = 14, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        fold.PointerPressed += (_, e) =>
        {
            if (storey.Parts.Count < 2) return;
            if (!_open.Add(storey.Number)) _open.Remove(storey.Number);
            Show(_storeys, _shown);
            e.Handled = true;
        };
        var was = _shown(storey, null);
        var box = Box(was);
        // (a click: all of it on, unless it was all on: then all of it off)
        box.Click += (_, _) => StoreyShown?.Invoke(storey, null, was != true);
        var name = new TextBlock
        {
            Text = Lang.T($"Verdieping {storey.Number}", $"Level {storey.Number}"), FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
        var levels = new TextBlock { Text = string.Join(", ", storey.Levels), Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(levels, string.Join(", ", storey.Levels));
        return Line([fold, box, name], Height(storey.Elevation), levels, 0);
    }

    private Control PartRow(SketchStorey storey, string model, int bodies)
    {
        var box = Box(_shown(storey, model));
        box.Click += (_, _) => StoreyShown?.Invoke(storey, model, box.IsChecked == true);
        var name = new TextBlock { Text = model, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, Lang.T($"{model}: {bodies} lichamen", $"{model}: {bodies} bodies"));
        var count = new TextBlock { Text = bodies.ToString("N0", Lang.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("nl-NL")), Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        return Line([box], count, name, 28);
    }

    private static CheckBox Box(bool? shown) =>
        new() { Classes = { "small" }, IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Focusable = false };

    private TextBlock Height(double elevation) => new()
    {
        Text = (elevation >= 0 ? "+" : "−") + Math.Abs(elevation).ToString("0.00", Lang.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("nl-NL")) + " m",
        Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
    };

    /// <summary>A row: on the left these, on the right that, the rest of the width the middle (room on the right for the
    /// scroll bar).</summary>
    private static Control Line(Control[] left, Control right, Control middle, double indent)
    {
        var row = new DockPanel { Margin = new Thickness(2 + indent, 1, 14, 1) };
        foreach (var c in left) { DockPanel.SetDock(c, Dock.Left); row.Children.Add(c); }
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(middle);
        return row;
    }
}
