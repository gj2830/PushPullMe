using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PushPullMe.Core;
using PushPullMe.Editor;

namespace PushPullMe;

/// <summary>
/// The desktop window (Windows, Linux, macOS) around the shared view (<see cref="MainView"/>): what only the desktop
/// has is done here, with file paths, dialog windows and the converter's window.
/// </summary>
public partial class MainWindow : Window, IAppHost
{
    private readonly MainView _view;

    public MainWindow() : this(null) { }

    /// <summary>The window, with a file to open (from the command line) or none.</summary>
    public MainWindow(string? open)
    {
        InitializeComponent();
        Content = _view = new MainView(this);
        Opened += async (_, _) => await _view.Start(open);
        Closed += (_, _) => _view.Stop();
        // a test window (the test aid plays mouse and keys itself): it takes no focus and no real mouse, so the user
        // working beside it neither is disturbed nor disturbs it
        if (MainView.IsTestWindow)
        {
            ShowActivated = false;
            _view.IsHitTestVisible = false;
        }
    }

    public string Name => "desktop window";

    private static string T(string nl, string en) => Lang.T(nl, en);

    public async Task<string?> AskOpenPath()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Model openen", "Open a model"), AllowMultiple = false, FileTypeFilter = FileTypes.Open(),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskSavePath(string name, string? folder, int kind)
    {
        var types = FileTypes.Save(kind);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Opslaan", "Save"), SuggestedFileName = name, DefaultExtension = FileTypes.Extension(types),
            FileTypeChoices = types, ShowOverwritePrompt = true,
            SuggestedStartLocation = folder != null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        return file?.TryGetLocalPath();
    }

    public Task Saved(string path) => Task.CompletedTask;

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

    public async Task<bool> Ask(string question, string title)
    {
        bool yes = false;
        var dlg = new Window
        {
            Title = "PushPullMe – " + title, Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = MainView.Brush("Page"),
        };
        dlg.Content = MainView.Question(question, answer => { yes = answer; dlg.Close(); });
        await dlg.ShowDialog(this);
        return yes;
    }

    public bool HasAssistant => true;

    void IAppHost.Close() => Close();
}
