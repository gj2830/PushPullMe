using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace PushPullMe.Mcp;

/// <summary>
/// How the MCP server and PushPullMe talk: over a named pipe of this Windows user only, one request and one answer per
/// connection, each a line of JSON. A request: {"client": name, "tool": name, "args": {…}}; an answer: {"ok": bool,
/// "text": …, "png": base64 (optional)}. Or {"list": true}, answered with {"ok": true, "tools": [the app's tools]}.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "PushPullMe.mcp";

    /// <summary>The tools as the app last wrote them (it does on every start): what the server offers while no app
    /// listens.</summary>
    public static string ToolsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PushPullMe", "mcp-tools.json");

    /// <summary>The tools in <paramref name="file"/> (null: none there, or unreadable).</summary>
    public static JsonArray? ReadToolsFile(string? file = null)
    {
        try { return JsonNode.Parse(File.ReadAllText(file ?? ToolsFile)) as JsonArray; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The tools written to <paramref name="file"/>, unless it already says the same.</summary>
    public static void WriteToolsFile(JsonArray tools, string? file = null)
    {
        file ??= ToolsFile;
        string text = tools.ToJsonString();
        try
        {
            if (File.Exists(file) && File.ReadAllText(file) == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The tools of the app listening on the pipe (null: none listens, or it did not answer in time).</summary>
    public static async Task<JsonArray?> ListToolsAsync(string pipeName = PipeName, int connectMs = 300, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectMs, ct);
            await WriteAsync(pipe, new JsonObject { ["list"] = true }, ct);
            return (await ReadAsync(pipe, ct))?["tools"]?.DeepClone() as JsonArray;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.Text.Json.JsonException) { return null; }
    }

    public static async Task WriteAsync(Stream s, JsonNode message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>One line of JSON (null: the other side closed first). (One message per direction per connection: the
    /// reader may read ahead, there is nothing after it.)</summary>
    public static async Task<JsonNode?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        using var reader = new StreamReader(s, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        string? line = await reader.ReadLineAsync(ct);
        return line == null ? null : JsonNode.Parse(line);
    }

    /// <summary>A request to the app and its answer; null when no PushPullMe listens (not running, or not allowed).</summary>
    public static async Task<JsonNode?> AskAsync(string client, string tool, JsonObject args, string pipeName = PipeName, int connectMs = 2000, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectMs, ct); }
        catch (TimeoutException) { return null; }
        await WriteAsync(pipe, new JsonObject { ["client"] = client, ["tool"] = tool, ["args"] = args.DeepClone() }, ct);
        return await ReadAsync(pipe, ct);
    }
}
