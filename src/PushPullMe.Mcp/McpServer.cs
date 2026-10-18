using System.Text.Json;
using System.Text.Json.Nodes;

namespace PushPullMe.Mcp;

/// <summary>What a tool call gave: text, and optionally a PNG; <see cref="IsError"/>: it did not work (the text says
/// why).</summary>
public sealed record ToolResult(string Text, byte[]? Png = null, bool IsError = false);

/// <summary>
/// The MCP server side of the protocol (JSON-RPC 2.0, one message per line, as the stdio transport has it): initialize,
/// ping, tools/list and tools/call; a tool call is handed to <c>call</c> (in the program: to PushPullMe over its pipe).
/// </summary>
public sealed class McpServer(Func<string, string, JsonObject, Task<ToolResult>> call)
{
    /// <summary>The protocol versions understood; a client asking another one gets the newest.</summary>
    public static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The client's name, from initialize (shown in the app with every call).</summary>
    public string Client { get; private set; } = "?";

    /// <summary>Messages read from <paramref name="input"/> answered on <paramref name="output"/> until the input ends.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim().Length == 0) continue;
            if (await HandleAsync(line) is { } answer)
            {
                await output.WriteLineAsync(answer);
                await output.FlushAsync(ct);
            }
        }
    }

    /// <summary>The answer to one message (null: none, it was a notification or a response).</summary>
    public async Task<string?> HandleAsync(string line)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        var id = msg["id"]?.DeepClone();
        string? method = msg["method"]?.GetValue<string>();
        // (a notification, or an answer to something we did not ask: nothing to say)
        if (method == null || id == null) return null;
        var p = msg["params"] as JsonObject ?? new JsonObject();
        switch (method)
        {
            case "initialize":
            {
                string asked = p["protocolVersion"]?.GetValue<string>() ?? Versions[0];
                Client = p["clientInfo"]?["name"]?.GetValue<string>() ?? "?";
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "pushpullme", ["version"] = "0.5" },
                    ["instructions"] = "Works in a running PushPullMe window (a SketchUp/DesignSpark-like modeller) whose user ticked 'AI assistant may work along'. Lengths in mm, z up. Start with get_model; every change can be undone (undo, or Ctrl+Z by the user).",
                });
            }
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(ToolCatalog.Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema.DeepClone(),
                    }).ToArray()),
                });
            case "tools/call":
            {
                string? name = p["name"]?.GetValue<string>();
                if (name == null || ToolCatalog.Tools.All(t => t.Name != name)) return Error(id, -32602, $"Unknown tool: {name}");
                var args = p["arguments"] as JsonObject ?? new JsonObject();
                ToolResult r;
                try { r = await call(Client, name, args); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                {
                    r = new ToolResult("PushPullMe did not answer: " + ex.Message, IsError: true);
                }
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text });
                if (r.Png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(r.Png), ["mimeType"] = "image/png" });
                return Result(id, new JsonObject { ["content"] = content, ["isError"] = r.IsError });
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static string Result(JsonNode id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    /// <summary>A tool call passed on to PushPullMe over its pipe.</summary>
    public static async Task<ToolResult> CallApp(string client, string tool, JsonObject args, string pipeName = PipeProtocol.PipeName)
    {
        var answer = await PipeProtocol.AskAsync(client, tool, args, pipeName);
        if (answer == null)
            return new ToolResult("PushPullMe is not reachable. Start PushPullMe and tick 'AI assistant may work along' (bottom right) / " +
                "PushPullMe is niet bereikbaar. Start PushPullMe en vink 'AI-assistent mag meewerken' aan (rechtsonder).", IsError: true);
        byte[]? png = answer["png"]?.GetValue<string>() is { Length: > 0 } b64 ? Convert.FromBase64String(b64) : null;
        return new ToolResult(answer["text"]?.GetValue<string>() ?? "", png, answer["ok"]?.GetValue<bool>() != true);
    }
}
