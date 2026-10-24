using PushPullMe.Core;

namespace PushPullMe;

/// <summary>
/// What differs per system around the shared view (<see cref="MainView"/>): the desktop has file paths, dialog windows
/// and the converter's window; the browser has none of those (files come and go through the page, questions are asked
/// over the view). The view asks this for them; everything else it does itself.
/// </summary>
public interface IAppHost
{
    /// <summary>What the session log calls this system ("desktop window", "browser page").</summary>
    string Name { get; }

    /// <summary>A model to open, chosen by the user, as a path the editor can read (null: none).</summary>
    Task<string?> AskOpenPath();

    /// <summary>Where to save, chosen by the user, as a path the editor writes to (null: nowhere);
    /// <paramref name="kind"/>: 1 STEP, 2 rsdocx, 3 STL.</summary>
    Task<string?> AskSavePath(string name, string? folder, int kind);

    /// <summary>The editor wrote <paramref name="path"/> (the one <see cref="AskSavePath"/> gave): for a system where
    /// that is not yet where the user chose (the browser hands it to the page).</summary>
    Task Saved(string path);

    /// <summary>Whether a mesh opens in the converter's window (else it is recognised in the editor).</summary>
    bool HasConverter { get; }

    /// <summary>See <see cref="Editor.Session.IEditorWindow.OpenInConverter"/>.</summary>
    Task<(SketchDocument Doc, string Note)?> OpenInConverter(string path);

    /// <summary>A yes/no question, titled <paramref name="title"/>.</summary>
    Task<bool> Ask(string question, string title);

    /// <summary>Whether the AI assistant can work along here (it talks through a pipe only the desktop has).</summary>
    bool HasAssistant { get; }

    /// <summary>The window closed (the test aid, done); nothing where there is no window.</summary>
    void Close();
}
