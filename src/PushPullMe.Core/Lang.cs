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
    public static string T(string nl, string en) => English ? en : nl;
}
