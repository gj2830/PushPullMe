using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PushPullMe.Core;
using PushPullMe.Editor;
using RsDocx.Core.Brep;
using RsDocx.Core.Stl;

namespace PushPullMe.Desktop;

/// <summary>
/// The window for every system, a first try: open a model (STEP, DesignSpark, STL/3MF), look round it, point at and
/// pick faces. The tools come over from the WPF window as they move into the editor layer.
/// </summary>
public partial class MainWindow : Window
{
    private static string T(string nl, string en) => Lang.T(nl, en);

    public MainWindow() : this(null) { }

    public MainWindow(string? open)
    {
        Lang.English = Settings.LoadEnglish();
        InitializeComponent();
        OpenButton.Content = T("📂 openen", "📂 open");
        ZoomButton.Content = T("⛶ alles in beeld", "⛶ zoom to fit");
        FileText.Text = "";
        StatusText.Text = T("Open een model om te beginnen. Middelste of rechter muisknop: draaien (met Shift: schuiven) · wiel: zoomen · klik: vlak kiezen.",
            "Open a model to begin. Middle or right mouse button: orbit (with Shift: pan) · wheel: zoom · click: pick a face.");
        View.FacesChanged += (hover, picked) => StatusText.Text = Describe(hover, picked);
        // (a test window: it takes no focus, so the user working beside it is not disturbed)
        if (Environment.GetEnvironmentVariable("PPM_SNAPSHOT") is { Length: > 0 }) ShowActivated = false;
        if (open is { Length: > 0 }) Opened += async (_, _) => { await Load(open); await TestAid(); };
        SessionLog.Write($"desktop window ({Environment.OSVersion})");
    }

    private string Describe(FaceRef? hover, FaceRef? picked)
    {
        string Face(FaceRef f)
        {
            var doc = View.Document!;
            var face = doc.Bodies[f.Body].Faces[f.Face];
            string kind = face.Surface switch
            {
                BrepPlane => T("plat", "flat"), BrepCylinder => T("cilinder", "cylinder"), BrepSphere => T("bol", "sphere"),
                BrepTorus => T("torus", "torus"), BrepCone => T("kegel", "cone"), _ => T("vrije vorm", "free form"),
            };
            return $"{doc.BodyName(f.Body)} · {T("vlak", "face")} {f.Face} ({kind})";
        }
        if (picked is { } p && hover is { } h && h != p) return $"🔵 {Face(p)}   ·   👉 {Face(h)}";
        if (picked is { } p2) return $"🔵 {T("Gekozen", "Picked")}: {Face(p2)}";
        if (hover is { } h2) return $"👉 {Face(h2)}";
        return T("Klik een vlak om het te kiezen.", "Click a face to pick it.");
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
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
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await Load(path);
    }

    private void Zoom_Click(object? sender, RoutedEventArgs e) => View.ZoomAll();

    private async Task Load(string path)
    {
        Busy.IsVisible = true;
        BusyText.Text = T("Bestand lezen en vormen herkennen…", "Reading the file and recognising shapes…");
        try
        {
            var (doc, note) = await Task.Run(() => ModelFiles.Load(path));
            View.Document = doc;
            FileText.Text = Path.GetFileName(path);
            int faces = doc.Bodies.Sum(b => b.Faces.Count), planes = doc.Bodies.Sum(b => b.Faces.Count(f => f.Surface is BrepPlane));
            StatusText.Text = T($"📦 {faces:N0} vlakken, waarvan {planes:N0} plat.", $"📦 {faces:N0} faces, {planes:N0} of them flat.")
                + (note is { } n ? $"  {n.Title}: {n.Text}" : "");
            SessionLog.Write($"open {path}: {faces} faces, {planes} planar");
        }
        catch (Exception ex) when (ModelFiles.IsFileError(ex))
        {
            StatusText.Text = T("😕 Dit bestand lukt niet: ", "😕 This file does not work: ") + ex.Message;
        }
        finally { Busy.IsVisible = false; }
    }

    // ------------------------------------------------------------------ test aid (no clicking in the user's window)

    /// <summary>
    /// PPM_ORBIT=dx,dy turns the view as the mouse would (pixels); PPM_PROBE="x,y,z;x,y,z" (mm) says which face a click
    /// on each point would pick and picks the last; PPM_SNAPSHOT=png saves what OpenGL drew (and a .txt beside it) and
    /// closes. Opened with a file (an argument or PPM_OPEN).
    /// </summary>
    private async Task TestAid()
    {
        if (Environment.GetEnvironmentVariable("PPM_SNAPSHOT") is not { Length: > 0 } shot) return;
        var report = new System.Text.StringBuilder();
        report.AppendLine($"system: {Environment.OSVersion}; GL: {View.GlInfo}");
        report.AppendLine($"open: {StatusText.Text}");
        // (laid out and drawn once: the view has its size)
        await Task.Delay(300);
        if (Environment.GetEnvironmentVariable("PPM_ORBIT") is { Length: > 0 } orbit)
        {
            var d = orbit.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            View.Orbit(d[0], d[1]);
        }
        if (Environment.GetEnvironmentVariable("PPM_PROBE") is { Length: > 0 } probes)
            foreach (var probe in probes.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = probe.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var p = new Vec3(c[0], c[1], c[2]);
                if (View.ScreenOf(p) is not { } s) continue;
                var hit = View.FaceAt(s);
                string what = hit is { } h ? string.Create(CultureInfo.InvariantCulture, $"face {h.Face.Body}/{h.Face.Face} at ({h.At.X:0.###},{h.At.Y:0.###},{h.At.Z:0.###})") : "nothing";
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"probe {probe} → screen ({s.X:0.##},{s.Y:0.##}) {what}"));
                View.Pick(s);
            }
        // (what the mouse would reach in the middle of the view: the view itself, or the mouse does nothing there)
        var middle = View.TranslatePoint(new Point(View.Bounds.Width / 2, View.Bounds.Height / 2), this) ?? default;
        report.AppendLine($"mouse in the middle reaches: {this.InputHitTest(middle)?.GetType().Name ?? "nothing"}");
        report.AppendLine($"status: {StatusText.Text}");
        var c0 = View.Camera;
        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"camera at ({c0.Position.X:0.#},{c0.Position.Y:0.#},{c0.Position.Z:0.#}) look ({c0.Look.X:0.###},{c0.Look.Y:0.###},{c0.Look.Z:0.###})"));
        View.AfterFrame = (w, h, rgba) =>
        {
            // (OpenGL gives the rows bottom up)
            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var fb = bmp.Lock())
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(rgba, (h - 1 - y) * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            bmp.Save(shot, new PngBitmapEncoderOptions());
            File.WriteAllText(shot + ".txt", report.ToString());
            Dispatcher.UIThread.Post(Close);
        };
        View.RequestNextFrameRendering();
    }
}
