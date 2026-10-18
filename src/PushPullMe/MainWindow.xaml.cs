using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using PushPullMe.Core;
using PushPullMe.Editor;
using PushPullMe.Editor.Session;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using DxMesh = HelixToolkit.SharpDX.MeshGeometry3D;
using Color = System.Windows.Media.Color;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using MouseButton = System.Windows.Input.MouseButton;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;
using Point3DCollection = System.Windows.Media.Media3D.Point3DCollection;
using Point = System.Windows.Point;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Rect = System.Windows.Rect;
using Tool = PushPullMe.Editor.Session.Tool;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

namespace PushPullMe;

/// <summary>
/// The WPF window (Windows): its buttons and panels, and the model drawn by DirectX (HelixToolkit). What the tools do is
/// the editor's (<see cref="EditorSession"/>, the same on every system); this window passes the mouse and the keys on,
/// shows what the editor sets (the status bar, the measurements box, the buttons) and draws what it describes (the model,
/// the lines and highlights beside it, the labels over it).
/// </summary>
public partial class MainWindow : Window, IEditorWindow
{
    private readonly EditorSession _s;

    /// <summary>What is drawn for each line set and highlight the editor shows.</summary>
    private readonly Dictionary<object, Element3D> _items = new(ReferenceEqualityComparer.Instance);
    private LinesVisual3D? _edges;

    /// <summary>The tool buttons, by tool.</summary>
    private readonly Dictionary<Tool, RadioButton> _toolButtons = new();

    /// <summary>Setting the camera from the editor (not a change of the viewer's own, the view cube's, to pass back).</summary>
    private bool _syncing;

    public MainWindow()
    {
        Lang.English = Settings.LoadEnglish();
        InitializeComponent();
        _s = new EditorSession(this);
        BuildStructure();
        XamlTexts.Apply(this);
        LanguageButton.Content = Lang.English ? "🌐  EN" : "🌐  NL";
        View.EffectsManager = new HelixToolkit.SharpDX.DefaultEffectsManager();
        Closed += (_, _) => { _s.Closed(); (View.EffectsManager as IDisposable)?.Dispose(); };
        BindEditor();
        // (started without a file: an empty work sheet, the ground ready to draw on)
        Loaded += async (_, _) =>
        {
            _s.View.ActualWidth = View.ActualWidth; _s.View.ActualHeight = View.ActualHeight;
            _s.View.FieldOfViewAcross = Viewport3DHelper.FieldOfViewAcross(View) ?? true;
            CameraToView();
            await _s.Started();
        };
        // a test window (the test aid plays mouse and keys itself): it takes no focus and no real mouse, so the user
        // working beside it neither is disturbed nor disturbs it
        if (Environment.GetEnvironmentVariable("PPM_OPEN") is { Length: > 0 } && Environment.GetEnvironmentVariable("PPM_SNAPSHOT") is { Length: > 0 })
        {
            ShowActivated = false;
            ((UIElement)Content).IsHitTestVisible = false;
        }
        // an unexpected error: written down, the action in progress stopped, the model kept
        Application.Current.DispatcherUnhandledException += (_, e) => { e.Handled = true; _s.Crashed(e.Exception); };
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
        Flag(_s.EmptyHint, on => EmptyHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed);
        Flag(_s.PaintPanel, on => { PaintPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed; if (on) BuildPalette(); });
        Flag(_s.JoinBox, on => JoinBox.IsChecked = on);
        Flag(_s.AssistantBox, on => AssistantBox.IsChecked = on);
        Flag(_s.GroundButtonShown, on => GroundButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed);

        // the tool buttons: named after their tool
        var names = new Dictionary<string, Tool>
        {
            ["Select"] = Tool.Select, ["Push"] = Tool.PushPull, ["Move"] = Tool.Move, ["Rotate"] = Tool.Rotate, ["Scale"] = Tool.Scale,
            ["Line"] = Tool.Line, ["Rect"] = Tool.Rectangle, ["Circle"] = Tool.Circle, ["RotRect"] = Tool.RotatedRectangle, ["Polygon"] = Tool.Polygon,
            ["CentreArc"] = Tool.CentreArc, ["Arc"] = Tool.Arc, ["Arc3"] = Tool.Arc3, ["Pie"] = Tool.Pie, ["Freehand"] = Tool.Freehand,
            ["WorkPlane"] = Tool.WorkPlane, ["Offset"] = Tool.Offset, ["Round"] = Tool.Round, ["Follow"] = Tool.FollowMe, ["Mirror"] = Tool.Mirror,
            ["Union"] = Tool.Union, ["Subtract"] = Tool.Subtract, ["Intersect"] = Tool.Intersect, ["Trim"] = Tool.Trim, ["Split"] = Tool.Split,
            ["Tape"] = Tool.Tape, ["Dimension"] = Tool.Dimension, ["Protractor"] = Tool.Protractor, ["Axes"] = Tool.Axes, ["Text"] = Tool.Text,
            ["Section"] = Tool.Section, ["Tag"] = Tool.Hide, ["Text3D"] = Tool.Text3D, ["Paint"] = Tool.Paint, ["Lasso"] = Tool.Lasso,
            ["Eraser"] = Tool.Eraser, ["Zoom"] = Tool.Zoom, ["ZoomWindow"] = Tool.ZoomWindow, ["PositionCamera"] = Tool.PositionCamera,
            ["LookAround"] = Tool.LookAround, ["Walk"] = Tool.Walk,
        };
        foreach (var (name, tool) in names)
            if (FindName(name + "Tool") is RadioButton rb) { _toolButtons[tool] = rb; rb.Tag = tool; }
        _s.ToolChanged += () => { if (_toolButtons.TryGetValue(_s.Tool, out var rb) && rb.IsChecked != true) rb.IsChecked = true; };

        // the model, and the lines and highlights beside it
        _s.SceneChanged += RenderModel;
        _s.View.Children.CollectionChanged += ItemsChanged;
        _s.View.Camera.Changed += CameraToView;
        _s.View.MouseCaptureChanged += on => { if (on) View.CaptureMouse(); else View.ReleaseMouseCapture(); };
        _s.PaintChanged += MarkSwatch;
        View.CameraChanged += (_, _) => CameraFromView();
        View.SizeChanged += (_, _) => { _s.View.ActualWidth = View.ActualWidth; _s.View.ActualHeight = View.ActualHeight; };
    }

    /// <summary>The editor's camera onto the viewer's (and the head light the way it looks).</summary>
    private void CameraToView()
    {
        if (View.Camera is not ProjectionCamera cam) return;
        var c = _s.View.Camera;
        _syncing = true;
        try
        {
            cam.Position = new Point3D(c.Position.X, c.Position.Y, c.Position.Z);
            cam.LookDirection = new Vector3D(c.LookDirection.X, c.LookDirection.Y, c.LookDirection.Z);
            cam.UpDirection = new Vector3D(c.UpDirection.X, c.UpDirection.Y, c.UpDirection.Z);
            cam.NearPlaneDistance = c.NearPlaneDistance; cam.FarPlaneDistance = c.FarPlaneDistance;
        }
        finally { _syncing = false; }
        HeadLight.Direction = cam.LookDirection;
    }

    /// <summary>The viewer turned its camera itself (the view cube): the editor follows.</summary>
    private void CameraFromView()
    {
        if (_syncing || View.Camera is not ProjectionCamera cam) return;
        var c = _s.View.Camera;
        var p = cam.Position; var l = cam.LookDirection; var u = cam.UpDirection;
        if (Math.Abs(p.X - c.Position.X) + Math.Abs(p.Y - c.Position.Y) + Math.Abs(p.Z - c.Position.Z)
            + Math.Abs(l.X - c.LookDirection.X) + Math.Abs(l.Y - c.LookDirection.Y) + Math.Abs(l.Z - c.LookDirection.Z)
            + Math.Abs(u.X - c.UpDirection.X) + Math.Abs(u.Y - c.UpDirection.Y) + Math.Abs(u.Z - c.UpDirection.Z) < 1e-9) return;
        c.Set(new(p.X, p.Y, p.Z), new(l.X, l.Y, l.Z), new(u.X, u.Y, u.Z));
    }

    // ------------------------------------------------------------------ drawing

    private static Color C(PushPullMe.Editor.Session.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    private static HelixToolkit.Maths.Color4 C4(PushPullMe.Editor.Session.Color c, double opacity = 1) => new(c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255.0 * opacity));

    /// <summary>A face colour lit as WPF lit it: diffuse, the ambient light in the same colour, no shine.</summary>
    private static PhongMaterial Phong(PushPullMe.Editor.Session.Color c, double opacity = 1) => new()
    {
        DiffuseColor = C4(c, opacity), AmbientColor = C4(c, opacity), SpecularColor = new HelixToolkit.Maths.Color4(0, 0, 0, 1), SpecularShininess = 1,
    };

    /// <summary>The model as the editor shows it: per body and colour its triangles (the inside in the back colour, the
    /// triangles turned round), faded outside the open group; and its edges.</summary>
    private void RenderModel()
    {
        ModelHost.Children.Clear();
        if (_edges != null) { View.Items.Remove(_edges); _edges = null; }
        if (_s.Shown is not { } model) return;
        var front = Phong(EditorSession.FrontColor);
        var back = Phong(EditorSession.BackColor);
        foreach (var m in model.Meshes)
        {
            var pos = new HelixToolkit.Vector3Collection(m.Positions);
            var nor = new HelixToolkit.Vector3Collection(m.Normals);
            var mesh = new DxMesh { Positions = pos, Normals = nor, TriangleIndices = new HelixToolkit.IntCollection(Enumerable.Range(0, pos.Count)) };
            if (m.Faded)
            {
                ModelHost.Children.Add(new MeshGeometryModel3D { Geometry = mesh, Material = Phong(EditorSession.FrontColor, 0x38 / 255.0), IsTransparent = true, CullMode = SharpDX.Direct3D11.CullMode.Back, FrontCounterClockwise = true, IsHitTestVisible = false });
                continue;
            }
            ModelHost.Children.Add(new MeshGeometryModel3D { Geometry = mesh, Material = m.Color is { } c ? Phong(c) : front, CullMode = SharpDX.Direct3D11.CullMode.Back, FrontCounterClockwise = true, IsHitTestVisible = false });
            var backIdx = new HelixToolkit.IntCollection(pos.Count);
            for (int i = 0; i < pos.Count; i += 3) { backIdx.Add(i); backIdx.Add(i + 2); backIdx.Add(i + 1); }
            var backNor = new HelixToolkit.Vector3Collection(nor.Count);
            foreach (var n in nor) backNor.Add(-n);
            ModelHost.Children.Add(new MeshGeometryModel3D
            {
                Geometry = new DxMesh { Positions = pos, Normals = backNor, TriangleIndices = backIdx },
                Material = back, CullMode = SharpDX.Direct3D11.CullMode.Back, FrontCounterClockwise = true, IsHitTestVisible = false,
            });
        }
        _edges = new LinesVisual3D { Points = Points(model.Edges), Color = C(EditorSession.EdgeColor), Thickness = 1.3 };
        View.Items.Add(_edges);
    }

    private static Point3DCollection Points(IEnumerable<PushPullMe.Editor.Session.Point3D> pts) => new(pts.Select(p => new Point3D(p.X, p.Y, p.Z)));

    /// <summary>A line set or highlight the editor shows (or takes away).</summary>
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var el in _items.Values) View.Items.Remove(el);
            _items.Clear();
            return;
        }
        foreach (var old in e.OldItems?.Cast<object>() ?? [])
            if (_items.Remove(old, out var el)) View.Items.Remove(el);
        foreach (var item in e.NewItems?.Cast<object>() ?? [])
        {
            Element3D el;
            switch (item)
            {
                case LineSet ls:
                {
                    var lines = new LinesVisual3D { Points = Points(ls.Points), Color = C(ls.Color), Thickness = ls.Thickness };
                    if (ls.Behind) lines.DepthBias = 200;
                    el = ls.OnTop ? new TopMostGroup3D { EnableTopMost = true, Children = { lines } } : lines;
                    break;
                }
                case FaceSet fs:
                {
                    var pos = new HelixToolkit.Vector3Collection(fs.Triangles.Select(p => new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z)));
                    el = new MeshGeometryModel3D
                    {
                        Geometry = new DxMesh { Positions = pos, TriangleIndices = new HelixToolkit.IntCollection(Enumerable.Range(0, pos.Count)) },
                        Material = new DiffuseMaterial { DiffuseColor = C4(fs.Color, fs.Opacity), EnableUnLit = true },
                        IsTransparent = true, CullMode = SharpDX.Direct3D11.CullMode.None, IsHitTestVisible = false,
                    };
                    break;
                }
                default: continue;
            }
            _items[item] = el;
            View.Items.Add(el);
        }
    }

    // ------------------------------------------------------------------ labels over the view

    private readonly List<UIElement> _labelViews = new();
    private TextBox? _noteBox;

    private Point OnTags(PushPullMe.Editor.Session.Point p) => View.TranslatePoint(new Point(p.X, p.Y), TagLayer);

    public void LabelsChanged()
    {
        foreach (var v in _labelViews) TagLayer.Children.Remove(v);
        _labelViews.Clear();
        Border Label(string text, bool dim) => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), BorderBrush = dim ? new SolidColorBrush(C(EditorSession.EdgeColor)) : (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = dim ? new SolidColorBrush(C(EditorSession.EdgeColor)) : (Brush)FindResource("Accent") },
        };
        foreach (var l in _s.Labels)
        {
            var at = OnTags(l.At);
            var label = Label(l.Text, l.Kind == LabelKind.Dimension);
            if (l.Kind == LabelKind.Note)
            {
                var leader = new System.Windows.Shapes.Line { X1 = at.X, Y1 = at.Y, X2 = at.X + 28, Y2 = at.Y - 28, Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.2 };
                Canvas.SetLeft(label, at.X + 28); Canvas.SetTop(label, at.Y - 40);
                TagLayer.Children.Add(leader); _labelViews.Add(leader);
            }
            else
            {
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, at.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, at.Y - label.DesiredSize.Height / 2);
                label.Opacity = l.Opacity;
            }
            TagLayer.Children.Add(label); _labelViews.Add(label);
        }
        // the tag next to the mouse
        if (_s.Tag is { } tag)
        {
            RadiusTagText.Text = tag.Text;
            var at = OnTags(tag.At);
            Canvas.SetLeft(RadiusTag, at.X); Canvas.SetTop(RadiusTag, at.Y);
            RadiusTag.Visibility = Visibility.Visible;
        }
        else RadiusTag.Visibility = Visibility.Collapsed;
        // the lasso or the zoom frame
        LassoLine.Points = new PointCollection(_s.LassoLine.Select(OnTags));
        LassoLine.Visibility = _s.LassoLine.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
                Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
            }
            var at = OnTags(nb);
            Canvas.SetLeft(_noteBox, at.X); Canvas.SetTop(_noteBox, at.Y);
        }
        else if (_noteBox != null)
        {
            TagLayer.Children.Remove(_noteBox);
            _noteBox = null;
            Keyboard.Focus(this);
        }
    }

    // ------------------------------------------------------------------ buttons

    private async void Open_Click(object sender, RoutedEventArgs e) => await _s.OpenDialog();
    private async void Save_Click(object sender, RoutedEventArgs e) => await _s.SaveDialog();
    private void Undo_Click(object sender, RoutedEventArgs e) => _s.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _s.Redo();
    private void OuterShell_Click(object sender, RoutedEventArgs e) => _s.OuterShell();
    private void SampleButton_Click(object sender, RoutedEventArgs e) => _s.Sample();
    private void ZoomExtentsButton_Click(object sender, RoutedEventArgs e) => _s.ZoomExtents();
    private void GroundButton_Click(object sender, RoutedEventArgs e) => _s.GroundButton();
    private void JoinBox_Click(object sender, RoutedEventArgs e) => _s.JoinBox.On = JoinBox.IsChecked == true;
    private void AssistantBox_Click(object sender, RoutedEventArgs e) => _s.AssistantBox.On = AssistantBox.IsChecked == true;

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: Tool tool }) _s.SetTool(tool);
    }

    /// <summary>🌐: the other language, at once, and kept.</summary>
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        _s.SwitchLanguage();
        XamlTexts.Apply(this);
        LanguageButton.Content = Lang.English ? "🌐  EN" : "🌐  NL";
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await _s.Open(files[0]);
    }

    // ------------------------------------------------------------------ the palette

    private void BuildPalette()
    {
        if (Swatches.Children.Count > 0) return;
        foreach (var (name, color) in EditorSession.Palette)
        {
            var swatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(3), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2), ToolTip = name, Tag = color,
                Background = new SolidColorBrush(C(color is { } c ? EditorSession.Argb(c) : EditorSession.FrontColor)),
            };
            if (color == null) swatch.Child = new TextBlock { Text = "∅", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
            swatch.MouseLeftButtonDown += (_, e) => { _s.SetPaint(color); e.Handled = true; };
            Swatches.Children.Add(swatch);
        }
        MarkSwatch();
    }

    private void MarkSwatch()
    {
        foreach (var sw in Swatches.Children.OfType<Border>())
            sw.BorderBrush = Equals(sw.Tag, _s.Paint) ? new SolidColorBrush(C(EditorSession.SelectColor)) : new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    }

    // ------------------------------------------------------------------ the mouse and the keys, on to the editor

    private static PushPullMe.Editor.Session.Point P(Point p) => new(p.X, p.Y);

    private static PushPullMe.Editor.Session.ModifierKeys Mods()
    {
        var m = Keyboard.Modifiers; var r = PushPullMe.Editor.Session.ModifierKeys.None;
        if ((m & ModifierKeys.Shift) != 0) r |= PushPullMe.Editor.Session.ModifierKeys.Shift;
        if ((m & ModifierKeys.Control) != 0) r |= PushPullMe.Editor.Session.ModifierKeys.Control;
        if ((m & ModifierKeys.Alt) != 0) r |= PushPullMe.Editor.Session.ModifierKeys.Alt;
        return r;
    }

    private static PushPullMe.Editor.Session.MouseButton? Button(MouseButton b) => b switch
    {
        MouseButton.Left => PushPullMe.Editor.Session.MouseButton.Left,
        MouseButton.Middle => PushPullMe.Editor.Session.MouseButton.Middle,
        MouseButton.Right => PushPullMe.Editor.Session.MouseButton.Right,
        _ => null,
    };

    /// <summary>The middle button (without Shift: that pans) starts turning the view. (The right button is kept free
    /// for a menu.)</summary>
    private void View_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _s.MouseDown(PushPullMe.Editor.Session.MouseButton.Middle, P(e.GetPosition(View)), Mods())) e.Handled = true;
    }

    private void View_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_s.MouseDown(PushPullMe.Editor.Session.MouseButton.Left, P(e.GetPosition(View)), Mods(), e.ClickCount)) e.Handled = true;
    }

    private void View_PreviewMouseMove(object sender, MouseEventArgs e) => _s.MouseMove(P(e.GetPosition(View)), Mods());

    private void View_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Button(e.ChangedButton) is { } b && _s.MouseUp(b, P(e.GetPosition(View)), Mods())) e.Handled = true;
    }

    private void View_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _s.MouseWheel(P(e.GetPosition(View)), e.Delta);
    }

    private void View_MouseLeave(object sender, MouseEventArgs e) => _s.MouseLeave();

    private void Window_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (_s.TextInput(e.Text)) e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Enum.TryParse<PushPullMe.Editor.Session.Key>(key.ToString(), out var k)) return;
        if (_s.KeyDown(k, Mods(), Keyboard.FocusedElement is TextBox)) e.Handled = true;
    }

    // ------------------------------------------------------------------ what the editor asks of the window

    public Task<string?> AskOpenPath()
    {
        var dlg = new OpenFileDialog { Filter = T("3D-modellen (*.step;*.stp;*.rsdocx;*.stl;*.3mf)|*.step;*.stp;*.rsdocx;*.stl;*.3mf|STEP (*.step;*.stp)|*.step;*.stp|DesignSpark (*.rsdocx)|*.rsdocx|Alle bestanden|*.*", "3D models (*.step;*.stp;*.rsdocx;*.stl;*.3mf)|*.step;*.stp;*.rsdocx;*.stl;*.3mf|STEP (*.step;*.stp)|*.step;*.stp|DesignSpark (*.rsdocx)|*.rsdocx|All files|*.*") };
        return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileName : null);
    }

    public Task<string?> AskSavePath(string name, string? folder, int kind)
    {
        var dlg = new SaveFileDialog
        {
            Filter = T("STEP, voor elk CAD-programma (*.step)|*.step|DesignSpark Mechanical (*.rsdocx)|*.rsdocx|STL voor de printer (*.stl)|*.stl", "STEP, for any CAD program (*.step)|*.step|DesignSpark Mechanical (*.rsdocx)|*.rsdocx|STL for the printer (*.stl)|*.stl"),
            FilterIndex = kind, FileName = name, InitialDirectory = folder, OverwritePrompt = true,
        };
        return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileName : null);
    }

    public bool HasConverter => true;

    public async Task<(SketchDocument Doc, string Note)?> OpenInConverter(string path)
    {
        StlConverter.MainWindow converter;
        StlConverter.Lang.English = Lang.English; // (the converter speaks as this app does)
        try { converter = new StlConverter.MainWindow(path) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }; }
        catch (System.Windows.Markup.XamlParseException ex) { throw new InvalidOperationException(ex.Message, ex); }
        if (converter.ShowDialog() != true || converter.Result is not { } r) return null;
        var doc = await Task.Run(() => SketchDocument.FromRecognized(r.Bodies, r.Labels));
        return (doc, r.Note);
    }

    public Task<bool> Ask(string question) =>
        Task.FromResult(MessageBox.Show(this, question, "PushPullMe – " + T("AI-assistent", "AI assistant"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    private DispatcherTimer? _toastTimer;

    public void ShowToast(string title, string text)
    {
        ToastTitle.Text = title; ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    public void Busy(string? text)
    {
        BusyCover.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        if (text != null) BusyText.Text = text;
    }

    public void Post(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    public Task<T> OnUiThread<T>(Func<Task<T>> action) => Dispatcher.InvokeAsync(action).Task.Unwrap();

    public async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    public Task<byte[]?> Snapshot()
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(View.RenderBitmap()));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Task.FromResult<byte[]?>(ms.ToArray());
    }

    public async Task SaveWindowPng(string path)
    {
        await Task.Delay(400);
        await Idle();
        var root = (FrameworkElement)Content;
        double w = root.ActualWidth + root.Margin.Left + root.Margin.Right, h = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        bmp.Render(bg);
        bmp.Render(root);
        // (the 3D view is drawn by DirectX, which a WPF render does not see: rendered by it into a bitmap of its own, never
        // copied from the screen, where another window may lie over this one)
        var shot = View.RenderBitmap();
        var at = View.TranslatePoint(new Point(0, 0), root);
        var over = new DrawingVisual();
        using (var dc = over.RenderOpen()) dc.DrawImage(shot, new Rect(at.X + root.Margin.Left, at.Y + root.Margin.Top, View.ActualWidth, View.ActualHeight));
        bmp.Render(over);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var f = File.Create(path)) enc.Save(f);
    }

    public void CloseWindow() => Close();

    public List<List<(double X, double Y)>>? TextOutlines(string text)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 100, Brushes.Black, 1.0);
        var geo = ft.BuildGeometry(new Point(0, 0)).GetFlattenedPathGeometry(0.15, ToleranceType.Absolute);
        // (a capital 1 high, y up from the baseline)
        double caps = typeface.TryGetGlyphTypeface(out var gt) ? gt.CapsHeight : 0.7;
        double baseline = ft.Baseline, k = 1 / (caps * 100);
        var loops = new List<List<(double X, double Y)>>();
        foreach (var fig in geo.Figures)
        {
            var l = new List<(double X, double Y)> { (fig.StartPoint.X * k, (baseline - fig.StartPoint.Y) * k) };
            foreach (var seg in fig.Segments)
                if (seg is PolyLineSegment pl) foreach (var q in pl.Points) l.Add((q.X * k, (baseline - q.Y) * k));
                else if (seg is LineSegment ls) l.Add((ls.Point.X * k, (baseline - ls.Point.Y) * k));
            loops.Add(l);
        }
        return loops;
    }

    private static string T(string nl, string en) => Lang.T(nl, en);

    // ------------------------------------------------------------------ structure tree

    /// <summary>Where the structure tree is: under the tools, right of the view, or in a window of its own.</summary>
    private enum StructurePlace { Left, Right, Loose }

    private StructureTree? _structure;
    private DockPanel? _structurePane;
    private StructurePlace _structurePlace = StructurePlace.Right, _structureDocked = StructurePlace.Right;
    private Window? _structureWindow;
    private bool _structureFolded;

    private const string StructureFile = "structuur.txt";

    /// <summary>The tree with its header (its name, and buttons: left, right, loose, fold), put where it was last.</summary>
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
        Button Small(string text, string tip) => new()
        {
            Content = text, ToolTip = tip, Width = 24, Height = 22, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(0),
            FontSize = 12, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
        };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var left = Small("◧", T("Links, onder de gereedschappen", "Left, under the tools"));
        left.Click += (_, _) => PlaceStructure(StructurePlace.Left);
        var right = Small("◨", T("Rechts, naast het beeld", "Right, beside the view"));
        right.Click += (_, _) => PlaceStructure(StructurePlace.Right);
        var loose = Small("⧉", T("Los venster (overal neer te zetten; dicht: terug)", "Window of its own (put it anywhere; close: back)"));
        loose.Click += (_, _) => PlaceStructure(StructurePlace.Loose);
        var fold = Small("▾", T("In- of uitklappen", "Fold or unfold"));
        fold.Click += (_, _) => { _structureFolded = !_structureFolded; fold.Content = _structureFolded ? "▸" : "▾"; ApplyFold(); SaveStructurePlace(); };
        foreach (var b in new[] { left, right, loose, fold }) buttons.Children.Add(b);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new TextBlock { Text = T("STRUCTUUR", "STRUCTURE"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(head, Dock.Top);
        _structurePane = new DockPanel();
        _structurePane.Children.Add(head);
        _structurePane.Children.Add(_structure);
        var (place, folded) = LoadStructurePlace();
        _structureFolded = folded; fold.Content = folded ? "▸" : "▾";
        // (a window of its own last time: on the right, till it is set loose again)
        PlaceStructure(place == StructurePlace.Loose ? StructurePlace.Right : place, keep: false);
        StructureChanged();
    }

    /// <summary>The tree moved to the left, the right or a window of its own (closing that window puts it back).</summary>
    private void PlaceStructure(StructurePlace place, bool keep = true)
    {
        if (_structurePane == null) return;
        // (out of where it is)
        StructureLeft.Child = null; StructureRight.Child = null;
        if (_structureWindow != null)
        {
            var old = _structureWindow; _structureWindow = null;
            ((Border)old.Content).Child = null;
            old.Close();
        }
        _structurePlace = place;
        if (place != StructurePlace.Loose) _structureDocked = place;
        StructureLeft.Visibility = place == StructurePlace.Left ? Visibility.Visible : Visibility.Collapsed;
        StructureRight.Visibility = place == StructurePlace.Right ? Visibility.Visible : Visibility.Collapsed;
        switch (place)
        {
            case StructurePlace.Left: StructureLeft.Child = _structurePane; break;
            case StructurePlace.Right: StructureRight.Child = _structurePane; break;
            default:
            {
                var w = new Window
                {
                    Title = T("Structuur", "Structure"), Owner = this, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow,
                    Width = 280, Height = 520, Left = Left + Width - 320, Top = Top + 120, Background = (Brush)FindResource("Page"),
                    Content = new Border { Padding = new Thickness(10, 8, 6, 8), Child = _structurePane },
                };
                w.Closed += (_, _) =>
                {
                    if (!ReferenceEquals(_structureWindow, w)) return;
                    _structureWindow = null;
                    ((Border)w.Content).Child = null;
                    PlaceStructure(_structureDocked);
                };
                _structureWindow = w;
                w.Show();
                break;
            }
        }
        ApplyFold();
        if (keep) SaveStructurePlace();
    }

    /// <summary>Folded: only the header (the card no longer takes the height).</summary>
    private void ApplyFold()
    {
        if (_structure == null) return;
        _structure.Visibility = _structureFolded ? Visibility.Collapsed : Visibility.Visible;
        StructureLeft.VerticalAlignment = _structureFolded ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        StructureRight.VerticalAlignment = _structureFolded ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        // (on the left and open: the tools three fifths of the height, scrolling when they do not fit, the tree the rest)
        bool share = _structurePlace == StructurePlace.Left && !_structureFolded;
        LeftColumn.RowDefinitions[0].Height = share ? new GridLength(3, GridUnitType.Star) : GridLength.Auto;
        LeftColumn.RowDefinitions[1].Height = share ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
    }

    private static (StructurePlace, bool) LoadStructurePlace()
    {
        if (AppData.Read(StructureFile)?.Split(' ') is [var a, var b])
            return (Enum.TryParse<StructurePlace>(a, out var p) ? p : StructurePlace.Left, b == "dicht");
        return (StructurePlace.Right, false);
    }

    private void SaveStructurePlace() => AppData.Write(StructureFile, $"{_structurePlace} {(_structureFolded ? "dicht" : "open")}");

    public void StructureChanged() => _structure?.Show(_s.Document, _s.IsBodyShown, _s.SketchVisible, _s.SketchCounts);
    public void StructurePick(int? body, int? face) => _structure?.Select(body, face);
    public void StructurePickGroup(int group) => _structure?.SelectGroup(group);
}
