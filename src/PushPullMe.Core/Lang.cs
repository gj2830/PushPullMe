namespace PushPullMe.Core;

/// <summary>
/// The language of what the app says: Dutch or English. Every text is written in both, side by side
/// (<see cref="T"/>); kinds of things are enums (<see cref="Snap"/>, <see cref="Drawing.Shape"/>) with their names per
/// language, never strings.
/// </summary>
public static class Lang
{
    /// <summary>English (otherwise Dutch).</summary>
    public static bool English { get; set; }

    /// <summary>The text in the language chosen.</summary>
    public static string T(string nl, string en)
    {
        string said = English ? en : nl;
        _last = (said, en);
        return said;
    }

    /// <summary>The last text <see cref="T"/> gave on this thread, with its English.</summary>
    [ThreadStatic] private static (string Said, string En)? _last;

    /// <summary>The English of <paramref name="text"/> when it is what <see cref="T"/> gave last on this thread (as in
    /// <c>throw new PushPullException(Lang.T(…, …))</c>), else null.</summary>
    public static string? EnglishOf(string text) => _last is { } l && l.Said == text ? l.En : null;
}
