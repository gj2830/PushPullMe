using System.Globalization;

namespace PushPullMe.Editor;

/// <summary>
/// The app's own folder for what it keeps between sessions (the choices made, the logs): where the system keeps such
/// things for this user, %LOCALAPPDATA%\PushPullMe on Windows, ~/.local/share/PushPullMe on Linux,
/// ~/Library/Application Support/PushPullMe on macOS. Nothing here may stop the app: a file that cannot be read or
/// written is as if it were not there.
/// </summary>
public static class AppData
{
    public static string Folder { get; } = Path.Combine(Root(), "PushPullMe");

    private static string Root()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return dir.Length > 0 ? dir : Path.GetTempPath();
    }

    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>What the file holds, trimmed; null when it is not there or cannot be read.</summary>
    public static string? Read(string name)
    {
        try { return File.Exists(PathOf(name)) ? File.ReadAllText(PathOf(name)).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The file written (left as it was when that cannot be).</summary>
    public static void Write(string name, string text)
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(PathOf(name), text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Where unexpected errors are written down (to find them later).</summary>
    public static string ErrorLog => PathOf("fouten.log");

    public static void LogError(Exception ex)
    {
        try { Directory.CreateDirectory(Folder); File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The choices kept from one session to the next.</summary>
public static class Settings
{
    private const string LanguageFile = "taal.txt", JoinFile = "samenvoegen.txt", AssistantFile = "assistent.txt";
    private const string On = "aan", Off = "uit";

    /// <summary>English or Dutch: PPM_LANG=en or nl (the test aid, without touching the choice kept), else as chosen
    /// before, else as the system speaks (Dutch for Dutch, English otherwise).</summary>
    public static bool LoadEnglish()
    {
        if (Environment.GetEnvironmentVariable("PPM_LANG") is "en" or "nl") return Environment.GetEnvironmentVariable("PPM_LANG") == "en";
        if (AppData.Read(LanguageFile) is { } kept) return kept == "en";
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "nl";
    }

    public static void SaveEnglish(bool english) => AppData.Write(LanguageFile, english ? "en" : "nl");

    /// <summary>Whether ⇕ joins what it pulls against another part with it (on when never chosen).</summary>
    public static bool LoadJoin() => AppData.Read(JoinFile) != Off;

    public static void SaveJoin(bool on) => AppData.Write(JoinFile, on ? On : Off);

    /// <summary>Whether an AI assistant may work along (off when never chosen); PPM_MCP=1 turns it on for a test window.</summary>
    public static bool LoadAssistant() => Environment.GetEnvironmentVariable("PPM_MCP") == "1" || AppData.Read(AssistantFile) == On;

    public static void SaveAssistant(bool on) => AppData.Write(AssistantFile, on ? On : Off);
}

/// <summary>The session log: PPM_LOG, or else sessie.log in the app's folder (the last session; it starts empty).</summary>
public static class SessionLog
{
    public static string? Path { get; } = Environment.GetEnvironmentVariable("PPM_LOG") ?? Start();

    private static string? Start()
    {
        try
        {
            Directory.CreateDirectory(AppData.Folder);
            string path = AppData.PathOf("sessie.log");
            File.WriteAllText(path, $"PushPullMe, sessie van {DateTime.Now:yyyy-MM-dd HH:mm}\n");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write(string what)
    {
        if (Path == null) return;
        try { File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss}] {what}\n"); } catch (IOException) { }
    }
}
