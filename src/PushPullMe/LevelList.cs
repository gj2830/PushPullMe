using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PushPullMe.Core;
using PushPullMe.Editor.Session;

namespace PushPullMe;

/// <summary>
/// The storeys of a model read from Revit, under the structure tree: per model (a heading when there are more) its
/// storeys from the top down, as a section through a building is read, each with a box (unticked: the bodies on it
/// hidden, besides what the tree hides), its name and its height. Not shown when the document has none.
/// </summary>
internal sealed class LevelList : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _head;

    /// <summary>A storey's box ticked (true) or not.</summary>
    public event Action<SketchLevel, bool>? LevelShown;

    public LevelList(IBrush muted)
    {
        _head = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = muted, Margin = new Thickness(0, 10, 0, 4) };
        var pane = new DockPanel();
        DockPanel.SetDock(_head, Dock.Top);
        pane.Children.Add(_head);
        pane.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = pane;
        IsVisible = false;
    }

    /// <summary>The list made anew: these storeys, each ticked as <paramref name="shown"/> says.</summary>
    public void Show(IReadOnlyList<SketchLevel> levels, Func<SketchLevel, bool> shown, IBrush muted)
    {
        _head.Text = Lang.T("VERDIEPINGEN", "LEVELS");
        _rows.Children.Clear();
        IsVisible = levels.Count > 0;
        var models = levels.Select(l => l.Model).Distinct().ToList();
        foreach (var model in models)
        {
            if (models.Count > 1)
                _rows.Children.Add(new TextBlock { Text = model, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = muted, Margin = new Thickness(0, 6, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis });
            foreach (var level in levels.Where(l => l.Model == model).OrderByDescending(l => l.Elevation))
                _rows.Children.Add(Row(level, shown(level), muted));
        }
    }

    private Control Row(SketchLevel level, bool shown, IBrush muted)
    {
        var box = new CheckBox { Classes = { "small" }, IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Focusable = false };
        box.Click += (_, _) => LevelShown?.Invoke(level, box.IsChecked == true);
        var height = new TextBlock
        {
            Text = (level.Elevation >= 0 ? "+" : "−") + Math.Abs(level.Elevation).ToString("0.00", Lang.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("nl-NL")) + " m",
            Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
        };
        var name = new TextBlock { Text = level.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, Lang.T($"{level.Name}: {level.Bodies.Count} lichamen", $"{level.Name}: {level.Bodies.Count} bodies"));
        // (room on the right for the scroll bar)
        var row = new DockPanel { Margin = new Thickness(2, 1, 14, 1) };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(height, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(height);
        row.Children.Add(name);
        return row;
    }
}
