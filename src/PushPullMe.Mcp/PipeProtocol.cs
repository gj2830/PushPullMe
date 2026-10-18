using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace PushPullMe.Mcp;

/// <summary>
/// How the MCP server and PushPullMe talk: over a named pipe of this Windows user only, one request and one answer per
/// connection, each a line of JSON. A request: {"client": name, "tool": name, "args": {…}}; an answer: {"ok": bool,
/// "text": …, "png": base64 (optional)}.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "PushPullMe.mcp";

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
