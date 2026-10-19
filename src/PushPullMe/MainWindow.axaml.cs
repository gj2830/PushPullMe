using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PushPullMe.Core;
using PushPullMe.Editor;
using PushPullMe.Editor.Session;
using SkiaSharp;
using Color = Avalonia.Media.Color;
using EditorKey = PushPullMe.Editor.Session.Key;
using EditorMods = PushPullMe.Editor.Session.ModifierKeys;
using Key = Avalonia.Input.Key;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Vector = Avalonia.Vector;
using Tool = PushPullMe.Editor.Session.Tool;

namespace PushPullMe;

/// <summary>
/// The window for every system (Avalonia): its buttons and panels, and the model drawn through
/// OpenGL (<see cref="ModelView"/>). What the tools do is the editor's (<see cref="EditorSession"/>, the same on every
/// system); this window passes the mouse and the keys on, shows what the editor sets and draws what it describes.
/// </summary>
public partial class MainWindow : Window, IEditorWindow
{
    private readonly EditorSession _s;
    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();

    public MainWindow() : this(null) { }

    /// <summary>The window, with a file to open (from the command line) or none.</summary>
    public MainWindow(string? open)
    {
        Lang.English = Settings.LoadEnglish();
        InitializeComponent();
        _s = new EditorSession(this);
        View.Session = _s;
        Cube.Session = _s;
        BuildTools();
        BuildStructure();
        ApplyTexts();
        BindEditor();
        // the keys (before a control takes them) and the text typed, on to the editor
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, Window_TextInput, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        Opened += async (_, _) =>
        {
            await _s.Started();
            if (open is { Length: > 0 } && Environment.GetEnvironmentVariable("PPM_OPEN") is not { Length: > 0 }) await _s.Open(open);
        };
        Closed += (_, _) => _s.Closed();
        // (the test aid: the view as large as asked, PPM_VIEWSIZE=759x679, so runs on different screens compare pixel for pixel)
        if (Environment.GetEnvironmentVariable("PPM_VIEWSIZE")?.Split('x') is [var vw, var vh] && double.TryParse(vw, out double w) && double.TryParse(vh, out double h))
        {
            View.Width = w; View.Height = h;
            View.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; View.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        // a test window (the test aid plays mouse and keys itself): it takes no focus and no real mouse, so the user
        // working beside it neither is disturbed nor disturbs it
        if (Environment.GetEnvironmentVariable("PPM_OPEN") is { Length: > 0 } && Environment.GetEnvironmentVariable("PPM_SNAPSHOT") is { Length: > 0 })
        {
            ShowActivated = false;
            ((Control)Content!).IsHitTestVisible = false;
        }
        // an unexpected error: written down, the action in progress stopped, the model kept
        Dispatcher.UIThread.UnhandledException += (_, e) => { e.Handled = true; _s.Crashed(e.Exception); };
        SessionLog.Write($"desktop window ({Environment.OSVersion})");
    }

    // ------------------------------------------------------------------ what the editor sets, shown here

    private void BindEditor()
    {
        void Text(TextSlot slot, Action<string> show) { show(slot.Text); slot.Changed += () => show(slot.Text); }
        void Flag(FlagSlot slot, Action<bool> show) { show(slot.On); slot.Changed += () => show(slot.On); }
        Text(_s.StatusText, t => StatusText.Text = t);
        Text(_s.MeasureBox, t => MeasureBox.Text = t);
        Text(_s.MeasureLabel, t => MeasureLabel.Text = t);
        Text(_s.MeasureUnit, t => MeasureUnit.Text = t);
        Text(_s.FileText, t => FileText.Text = t);
        Text(_s.PaintName, t => PaintName.Text = t);
        Flag(_s.UndoButton, on => UndoButton.IsEnabled = on);
        Flag(_s.RedoButton, on => RedoButton.IsEnabled = on);
        Flag(_s.SaveButton, on => SaveButton.IsEnabled = on);
        Flag(_s.EmptyHint, on => EmptyHint.IsVisible = on);
        Flag(_s.PaintPanel, on => { PaintPanel.IsVisible = on; if (on) BuildPalette(); });
        Flag(_s.JoinBox, on => JoinBox.IsChecked = on);
        Flag(_s.AssistantBox, on => AssistantBox.IsChecked = on);
        Flag(_s.GroundButtonShown, on => GroundButton.IsVisible = on);
        _s.ToolChanged += MarkTool;
        _s.PaintChanged += MarkSwatch;
        MarkTool();

        LanguageButton.Click += (_, _) => { _s.SwitchLanguage(); ApplyTexts(); };
        UndoButton.Click += (_, _) => _s.Undo();
        RedoButton.Click += (_, _) => _s.Redo();
        OpenButton.Click += async (_, _) => await _s.OpenDialog();
        SaveButton.Click += async (_, _) => await _s.SaveDialog();
        GroundButton.Click += (_, _) => _s.GroundButton();
        JoinBox.Click += (_, _) => _s.JoinBox.On = JoinBox.IsChecked == true;
        AssistantBox.Click += (_, _) => _s.AssistantBox.On = AssistantBox.IsChecked == true;
    }

    /// <summary>The tool bar, from the editor's table.</summary>
    private void BuildTools()
    {
        foreach (var item in ToolBar.Items)
        {
            switch (item.Kind)
            {
                case ToolBarItemKind.Group:
                    Tools.Children.Add(new TextBlock { Classes = { "group" }, Text = item.Icon });
                    break;
                case ToolBarItemKind.Tool:
                {
                    var tool = item.Tool!.Value;
                    var b = new ToggleButton { Classes = { "tool" }, Content = item.Icon };
                    ToolTip.SetTip(b, item.Tip);
                    b.Click += (_, _) => { _s.SetTool(tool); MarkTool(); };
                    _toolButtons[tool] = b;
                    Tools.Children.Add(b);
                    break;
                }
                default:
                {
                    var action = item.Action!.Value;
                    var b = new Button { Classes = { "tool" }, Content = item.Icon };
                    ToolTip.SetTip(b, item.Tip);
                    b.Click += (_, _) => _s.Do(action);
                    Tools.Children.Add(b);
                    break;
                }
            }
        }
    }

    private void MarkTool()
    {
        foreach (var (tool, b) in _toolButtons) b.IsChecked = tool == _s.Tool;
    }

    /// <summary>The window's texts in the language chosen (tooltips, labels, button texts; from either language).</summary>
    private void ApplyTexts()
    {
        foreach (var node in this.GetLogicalDescendants().Prepend(this))
        {
            if (node is Control c && ToolTip.GetTip(c) is string tip) ToolTip.SetTip(c, UiTexts.Tr(tip));
            switch (node)
            {
                case TextBlock tb when tb.Text is { Length: > 0 } t: tb.Text = UiTexts.Tr(t); break;
                case ContentControl cc when cc.Content is string text: cc.Content = UiTexts.Tr(text); break;
            }
        }
        LanguageButton.Content = Lang.English ? "🌐  EN" : "🌐  NL";
        LevelsChanged();
        StructureChanged();
    }

    // ------------------------------------------------------------------ the palette

    private void BuildPalette()
    {
        if (Swatches.Children.Count > 0) return;
        foreach (var (name, color) in EditorSession.Palette)
        {
            var c = color is { } argb ? EditorSession.Argb(argb) : EditorSession.FrontColor;
            var swatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(3), Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(2), Tag = color, Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
            };
            ToolTip.SetTip(swatch, name);
            // (a see-through colour, glass: shining from white at the top left into its colour, fully there)
            if (c.A < 255)
                swatch.Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.15, 0.1, RelativeUnit.Relative), EndPoint = new RelativePoint(0.85, 0.9, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(c.R, c.G, c.B), 0.55), new GradientStop(Color.FromArgb(0x90, c.R, c.G, c.B), 1) },
                };
            if (color == null) swatch.Child = new TextBlock { Text = "∅", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
            swatch.PointerPressed += (_, e) => { _s.SetPaint(color); e.Handled = true; };
            Swatches.Children.Add(swatch);
        }
        MarkSwatch();
    }

    private void MarkSwatch()
    {
        var s = EditorSession.SelectColor;
        foreach (var sw in Swatches.Children.OfType<Border>())
            sw.BorderBrush = new SolidColorBrush(Equals(sw.Tag, _s.Paint) ? Color.FromRgb(s.R, s.G, s.B) : Color.FromRgb(0xE2, 0xE8, 0xF0));
    }

    // ------------------------------------------------------------------ labels over the view

    private readonly List<Control> _labelViews = new();
    private TextBox? _noteBox;

    public void LabelsChanged()
    {
        foreach (var v in _labelViews) TagLayer.Children.Remove(v);
        _labelViews.Clear();
        var accent = (IBrush)this.FindResource("Accent")!;
        var edge = new SolidColorBrush(Color.FromRgb(EditorSession.EdgeColor.R, EditorSession.EdgeColor.G, EditorSession.EdgeColor.B));
        foreach (var l in _s.Labels)
        {
            bool dim = l.Kind == LabelKind.Dimension;
            var label = new Border
            {
                Classes = { "label" }, BorderBrush = dim ? edge : accent, Opacity = l.Opacity,
                Child = new TextBlock { Text = l.Text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = dim ? edge : accent },
            };
            if (!dim)
            {
                var leader = new Line { StartPoint = new Point(l.At.X, l.At.Y), EndPoint = new Point(l.At.X + 28, l.At.Y - 28), Stroke = accent, StrokeThickness = 1.2, IsHitTestVisible = false };
                Canvas.SetLeft(label, l.At.X + 28); Canvas.SetTop(label, l.At.Y - 40);
                TagLayer.Children.Add(leader); _labelViews.Add(leader);
            }
            TagLayer.Children.Add(label); _labelViews.Add(label);
            if (dim)
            {
                // (centred on its point: measured once in the window, where its style applies)
                label.Measure(Size.Infinity);
                Canvas.SetLeft(label, l.At.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, l.At.Y - label.DesiredSize.Height / 2);
            }
        }
        // the tag next to the mouse
        if (_s.Tag is { } tag)
        {
            RadiusTagText.Text = tag.Text;
            Canvas.SetLeft(RadiusTag, tag.At.X); Canvas.SetTop(RadiusTag, tag.At.Y);
            RadiusTag.IsVisible = true;
        }
        else RadiusTag.IsVisible = false;
        // the lasso or the zoom frame
        LassoLine.Points = _s.LassoLine.Select(p => new Point(p.X, p.Y)).ToList();
        LassoLine.IsVisible = _s.LassoLine.Count > 0;
        // the box a label is typed in
        if (_s.NoteBox is { } nb)
        {
            if (_noteBox == null)
            {
                var box = _noteBox = new TextBox { MinWidth = 150, FontSize = 12.5, Padding = new Thickness(4, 2, 4, 2) };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { e.Handled = true; _s.EndNoteBox(box.Text); }
                    else if (e.Key == Key.Escape) { e.Handled = true; _s.EndNoteBox(null); }
                };
                TagLayer.Children.Add(box);
                Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
            }
            Canvas.SetLeft(_noteBox, nb.X); Canvas.SetTop(_noteBox, nb.Y);
        }
        else if (_noteBox != null)
        {
            TagLayer.Children.Remove(_noteBox);
            _noteBox = null;
            View.Focus();
        }
    }

    // ------------------------------------------------------------------ the keys, on to the editor

    private static EditorMods Mods(KeyModifiers m) =>
        ((m & KeyModifiers.Shift) != 0 ? EditorMods.Shift : 0) | ((m & KeyModifiers.Control) != 0 ? EditorMods.Control : 0)
        | ((m & KeyModifiers.Alt) != 0 ? EditorMods.Alt : 0) | ((m & KeyModifiers.Meta) != 0 ? EditorMods.Control : 0);

    private static EditorKey? Map(Key k) => k switch
    {
        Key.Enter => EditorKey.Enter,
        Key.PageUp => EditorKey.PageUp,
        Key.PageDown => EditorKey.PageDown,
        _ => Enum.TryParse<EditorKey>(k.ToString(), out var e) ? e : null,
    };

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Map(e.Key) is not { } k) return;
        bool inTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        if (_s.KeyDown(k, Mods(e.KeyModifiers), inTextBox)) e.Handled = true;
    }

    private void Window_TextInput(object? sender, TextInputEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox || e.Text is not { } text) return;
        if (_s.TextInput(text)) e.Handled = true;
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files && files[0].TryGetLocalPath() is { } path) await _s.Open(path);
    }

    // ------------------------------------------------------------------ what the editor asks of the window

    private static string T(string nl, string en) => Lang.T(nl, en);

    public async Task<string?> AskOpenPath()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Model openen", "Open a model"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("3D-modellen", "3D models")) { Patterns = ModelFiles.Extensions.Select(x => "*." + x).ToList() },
                new FilePickerFileType(T("Alle bestanden", "All files")) { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskSavePath(string name, string? folder, int kind)
    {
        FilePickerFileType Type(string nl, string en, string ext) => new(T(nl, en)) { Patterns = ["*." + ext] };
        var types = new[]
        {
            Type("STEP, voor elk CAD-programma", "STEP, for any CAD program", "step"),
            Type("DesignSpark Mechanical", "DesignSpark Mechanical", "rsdocx"),
            Type("STL voor de printer", "STL for the printer", "stl"),
        };
        var chosen = types[Math.Clamp(kind - 1, 0, 2)];
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Opslaan", "Save"), SuggestedFileName = name, DefaultExtension = chosen.Patterns![0][2..],
            FileTypeChoices = [chosen, .. types.Where(t => t != chosen)], ShowOverwritePrompt = true,
            SuggestedStartLocation = folder != null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        return file?.TryGetLocalPath();
    }

    public bool HasConverter => true;

    /// <summary>The converter's window (shapes are best found there, with all its options); what it hands over taken over.</summary>
    public async Task<(SketchDocument Doc, string Note)?> OpenInConverter(string path)
    {
        StlConverter.Lang.English = Lang.English; // (the converter speaks as this app does)
        var converter = new StlConverter.ConverterWindow(path) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (!await converter.ShowDialog<bool>(this) || converter.Result is not { } r) return null;
        var doc = await Task.Run(() => SketchDocument.FromRecognized(r.Bodies, r.Labels));
        return (doc, r.Note);
    }

    public async Task<bool> Ask(string question, string? title = null)
    {
        // (test aid: the answer given beforehand, for the window's own tests)
        if (Environment.GetEnvironmentVariable("PPM_ANSWER") is { Length: > 0 } answer) return answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        bool yes = false;
        var dlg = new Window
        {
            Title = "PushPullMe – " + (title ?? T("AI-assistent", "AI assistant")), Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = (IBrush)this.FindResource("Page")!,
        };
        var ok = new Button { Classes = { "pill", "primary" }, Content = T("Ja", "Yes") };
        var no = new Button { Classes = { "pill" }, Content = T("Nee", "No") };
        ok.Click += (_, _) => { yes = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, ok } },
            },
        };
        await dlg.ShowDialog(this);
        return yes;
    }

    private DispatcherTimer? _toastTimer;

    public void ShowToast(string title, string text)
    {
        ToastTitle.Text = title; ToastText.Text = text;
        Toast.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    public void Busy(string? text)
    {
        BusyCover.IsVisible = text != null;
        if (text != null) BusyText.Text = text;
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    public Task<T> OnUiThread<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);

    public async Task Idle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>The 3D view as a PNG, rendered by OpenGL itself (never copied from the screen).</summary>
    public async Task<byte[]?> Snapshot()
    {
        var (w, h, rgba) = await View.CaptureAsync();
        using var ms = new MemoryStream();
        Bitmap(w, h, rgba).Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }

    private static WriteableBitmap Bitmap(int w, int h, byte[] rgba)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(rgba, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        return bmp;
    }

    /// <summary>The whole window as a PNG: rendered by Avalonia, with the 3D view (which OpenGL draws, not Avalonia)
    /// laid in where it is.</summary>
    public async Task SaveWindowPng(string path)
    {
        await Task.Delay(400);
        await Idle();
        // (in device-independent pixels, as the WPF window's snapshot: the 3D view, drawn in screen pixels, scaled to fit)
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var window = new RenderTargetBitmap(size, new Vector(96, 96));
        window.Render(this);
        var (w, h, rgba) = await View.CaptureAsync();
        var at = View.TranslatePoint(new Point(0, 0), this) ?? default;
        using var whole = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = whole.CreateDrawingContext())
        {
            dc.DrawRectangle((IBrush)this.FindResource("Page")!, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
            dc.DrawImage(window, new Rect(0, 0, Bounds.Width, Bounds.Height));
            dc.DrawImage(Bitmap(w, h, rgba), new Rect(at.X, at.Y, View.Bounds.Width, View.Bounds.Height));
            // (what lies over the view: the labels, the palette, the toast, … drawn again on top of it)
            foreach (var over in new Control[] { GroundButton, EmptyHint, TagLayer, Cube, PaintPanel, Toast, BusyCover })
            {
                if (!over.IsVisible || over.Bounds.Width < 1 || over.TranslatePoint(new Point(0, 0), this) is not { } o) continue;
                using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(over.Bounds.Width), (int)Math.Ceiling(over.Bounds.Height)), new Vector(96, 96));
                bmp.Render(over);
                dc.DrawImage(bmp, new Rect(o.X, o.Y, bmp.PixelSize.Width, bmp.PixelSize.Height));
            }
        }
        whole.Save(path, new PngBitmapEncoderOptions());
    }

    public void CloseWindow() => Close();

    /// <summary>The letters' outlines from the system's bold sans-serif font (through Skia, which draws Avalonia):
    /// flattened, a capital 1 high, y up from the baseline.</summary>
    public List<List<(double X, double Y)>>? TextOutlines(string text)
    {
        using var typeface = SKTypeface.FromFamilyName(OperatingSystem.IsWindows() ? "Segoe UI" : null, SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 100);
        using var path = font.GetTextPath(text, new SKPoint(0, 0));
        double caps = font.Metrics.CapHeight is > 0 and var ch ? ch : 70;
        double k = 1 / caps;
        var loops = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? loop = null;
        (double X, double Y) Q(SKPoint p) => (p.X * k, -p.Y * k);
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
            switch (verb)
            {
                case SKPathVerb.Move: loop = [Q(pts[0])]; loops.Add(loop); break;
                case SKPathVerb.Line: loop?.Add(Q(pts[1])); break;
                case SKPathVerb.Quad:
                    for (int i = 1; i <= 8; i++) { float t = i / 8f, u = 1 - t; loop?.Add(Q(new SKPoint(u * u * pts[0].X + 2 * u * t * pts[1].X + t * t * pts[2].X, u * u * pts[0].Y + 2 * u * t * pts[1].Y + t * t * pts[2].Y))); }
                    break;
                case SKPathVerb.Conic:
                {
                    float wgt = it.ConicWeight();
                    for (int i = 1; i <= 8; i++)
                    {
                        float t = i / 8f, u = 1 - t, d = u * u + 2 * wgt * u * t + t * t;
                        loop?.Add(Q(new SKPoint((u * u * pts[0].X + 2 * wgt * u * t * pts[1].X + t * t * pts[2].X) / d, (u * u * pts[0].Y + 2 * wgt * u * t * pts[1].Y + t * t * pts[2].Y) / d)));
                    }
                    break;
                }
                case SKPathVerb.Cubic:
                    for (int i = 1; i <= 10; i++)
                    {
                        float t = i / 10f, u = 1 - t;
                        loop?.Add(Q(new SKPoint(u * u * u * pts[0].X + 3 * u * u * t * pts[1].X + 3 * u * t * t * pts[2].X + t * t * t * pts[3].X,
                            u * u * u * pts[0].Y + 3 * u * u * t * pts[1].Y + 3 * u * t * t * pts[2].Y + t * t * t * pts[3].Y)));
                    }
                    break;
            }
        return loops;
    }

    // ------------------------------------------------------------------ structure tree

    private StructureTree? _structure;

    private void BuildStructure()
    {
        _structure = new StructureTree();
        _structure.BodyShown += _s.SetBodyShown;
        _structure.Renamed += _s.RenameBody;
        _structure.GroupPicked += _s.PickGroup;
        _structure.GroupRenamed += _s.RenameGroup;
        _structure.GroupMenu += _s.GroupMenu;
        _structure.SketchShown += _s.SetSketchShown;
        _structure.Picked += _s.PickInTree;
        var muted = (IBrush)this.FindResource("Muted")!;
        _levels = new LevelList(muted);
        _levels.StoreyShown += _s.SetStoreyShown;
        var pane = new DockPanel();
        var head = new TextBlock { Text = T("STRUCTUUR", "STRUCTURE"), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = muted, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(head, Dock.Top);
        pane.Children.Add(head);
        // (the storeys under the tree)
        DockPanel.SetDock(_levels, Dock.Bottom);
        pane.Children.Add(_levels);
        pane.Children.Add(_structure);
        StructureRight.Child = pane;
        StructureChanged();
        LevelsChanged();
    }

    private LevelList? _levels;

    public void LevelsChanged() => _levels?.Show(_s.Storeys, _s.IsStoreyShown);

    public void StructureChanged() => _structure?.Show(_s.Document, _s.IsBodyShown, _s.SketchVisible, _s.SketchCounts);
    public void StructurePick(int? body, int? face) => _structure?.Select(body, face);
    public void StructurePickGroup(int group) => _structure?.SelectGroup(group);
}
