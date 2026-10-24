using Avalonia.Platform.Storage;
using BrowserKit;
using PushPullMe.Core;
using StlConverter;
using Lang = PushPullMe.Core.Lang;

namespace PushPullMe;

/// <summary>
/// The page around the shared view (<see cref="MainView"/>) in a browser. A browser has no file paths: files go in and
/// out through the runtime's in-memory folder (<see cref="BrowserFiles"/>). Questions are asked over the view, and a
/// mesh opens in the converter as a layer over it (there are no windows).
/// </summary>
public sealed class BrowserHost : IAppHost
{
    private MainView _view = null!;
    private BrowserFiles _files = null!;

    /// <summary>The view with this page around it, started once it is shown (with the model the page's address names,
    /// ?open=…, if any).</summary>
    public static MainView CreateView(string? pageAddress)
    {
        var host = new BrowserHost();
        var view = host._view = new MainView(host);
        host._files = new BrowserFiles(view);
        bool started = false;
        view.AttachedToVisualTree += async (_, _) =>
        {
            if (started) return;
            started = true;
            await view.Start(pageAddress != null ? await BrowserFiles.FromPageAddress(pageAddress) : null);
        };
        return view;
    }

    public string Name => "browser page";

    private static string T(string nl, string en) => Lang.T(nl, en);

    public Task<string?> AskOpenPath() => _files.Open(T("Model openen", "Open a model"), FileTypes.Open());

    public Task<string?> AskSavePath(string name, string? folder, int kind) => _files.AskSave(T("Opslaan", "Save"), name, FileTypes.Save(kind));

    public Task Saved(string path) => _files.Saved(path);

    public bool HasConverter => true;

    /// <summary>The converter over the view (shapes are best found there, with all its options); what it hands over taken over.</summary>
    public async Task<(SketchDocument Doc, string Note)?> OpenInConverter(string path)
    {
        StlConverter.Lang.English = Lang.English; // (the converter speaks as this app does)
        var layer = new ConverterLayer(_view);
        var converter = layer.View = new ConverterView(layer, embedded: true);
        _view.ShowLayer(converter);
        converter.Start(path);
        bool taken = await layer.Finished;
        _view.HideLayer(converter);
        if (!taken || converter.Result is not { } r) return null;
        var doc = await Task.Run(() => SketchDocument.FromRecognized(r.Bodies, r.Labels));
        return (doc, r.Note);
    }

    /// <summary>What the converter asks of the page while it lies over the view.</summary>
    private sealed class ConverterLayer(MainView over) : IConverterHost
    {
        private readonly BrowserFiles _files = new(over);
        private readonly TaskCompletionSource<bool> _done = new();

        public ConverterView View { get; set; } = null!;

        /// <summary>Done: with the result handed over (true) or closed (false).</summary>
        public Task<bool> Finished => _done.Task;

        public Task<string?> AskOpenPath(FilePickerFileType[] types) => _files.Open(StlConverter.Lang.T("Mesh openen", "Open a mesh"), types);
        public Task<string?> AskSavePath(string title, string name, string? folder, FilePickerFileType[] types) => _files.AskSave(title, name, types);
        public Task Saved(string path) => _files.Saved(path);
        public Task ShowMessage(string title, string text) => View.ShowMessageOver(title, text);
        public bool HasCloseButton => true;
        public void Done(bool handedOver) => _done.TrySetResult(handedOver);
    }

    public Task<bool> Ask(string question, string title) => _view.AskOver(question, title);

    public bool HasAssistant => false;

    public void Close() { }
}
