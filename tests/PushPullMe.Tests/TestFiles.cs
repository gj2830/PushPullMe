namespace PushPullMe.Tests;

/// <summary>Where the test files are.</summary>
internal static class TestFiles
{
    /// <summary>A file of the converter's testdata: next to this repository (../RsCOnverter or ../StlConverter, as it is
    /// developed) or in its submodule (StlConverter/).</summary>
    public static string Converter(string name)
    {
        // (from bin/Debug/net10.0 of the test project up to the repository)
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        foreach (var dir in new[] { "../RsCOnverter", "../StlConverter", "StlConverter" })
        {
            string path = Path.GetFullPath(Path.Combine(repo, dir, "testdata", name));
            if (File.Exists(path)) return path;
        }
        return Path.GetFullPath(Path.Combine(repo, "StlConverter", "testdata", name));
    }
}
