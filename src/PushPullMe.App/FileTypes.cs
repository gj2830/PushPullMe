using Avalonia.Platform.Storage;
using PushPullMe.Core;
using PushPullMe.Editor;

namespace PushPullMe;

/// <summary>The kinds of files in the open and save dialogs (the same on every system).</summary>
public static class FileTypes
{
    private static string T(string nl, string en) => Lang.T(nl, en);

    /// <summary>What can be opened: the 3D models, or anything.</summary>
    public static FilePickerFileType[] Open() =>
    [
        new FilePickerFileType(T("3D-modellen", "3D models")) { Patterns = ModelFiles.Extensions.Select(x => "*." + x).ToList() },
        new FilePickerFileType(T("Alle bestanden", "All files")) { Patterns = ["*"] },
    ];

    /// <summary>What a model is saved as, the chosen kind (1 STEP, 2 rsdocx, 3 STL) first.</summary>
    public static FilePickerFileType[] Save(int kind)
    {
        FilePickerFileType Type(string nl, string en, string ext) => new(T(nl, en)) { Patterns = ["*." + ext] };
        FilePickerFileType[] types =
        [
            Type("STEP, voor elk CAD-programma", "STEP, for any CAD program", "step"),
            Type("DesignSpark Mechanical", "DesignSpark Mechanical", "rsdocx"),
            Type("STL voor de printer", "STL for the printer", "stl"),
        ];
        var chosen = types[Math.Clamp(kind - 1, 0, 2)];
        return [chosen, .. types.Where(t => t != chosen)];
    }

    /// <summary>The extension of the first kind (without the dot).</summary>
    public static string Extension(FilePickerFileType[] types) => types[0].Patterns![0][2..];
}
