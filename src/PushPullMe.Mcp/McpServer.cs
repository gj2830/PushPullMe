using System.Text.Json;
using System.Text.Json.Nodes;

namespace PushPullMe.Mcp;

/// <summary>What a tool call gave: text, and optionally a PNG; <see cref="IsError"/>: it did not work (the text says
/// why).</summary>
public sealed record ToolResult(string Text, byte[]? Png = null, bool IsError = false);

/// <summary>
/// The MCP server side of the protocol (JSON-RPC 2.0, one message per line, as the stdio transport has it): initialize,
/// ping, tools/list and tools/call; a tool call is handed to <c>call</c> (in the program: to PushPullMe over its pipe).
/// The tools are not its own: <c>fetchTools</c> gives them (in the program: the running app's, else the ones it last
/// wrote down), so a rebuilt app brings its new tools without this server changing; when they change while it runs, it
/// tells the client (notifications/tools/list_changed) and the client fetches them again.
/// </summary>
public sealed class McpServer(Func<string, string, JsonObject, Task<ToolResult>> call, Func<Task<JsonArray?>> fetchTools)
{
    /// <summary>The protocol versions understood; a client asking another one gets the newest.</summary>
    public static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>What the server says when the tools changed.</summary>
    public const string ListChanged = """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

    /// <summary>What is offered when the tools are not known at all (PushPullMe never ran here): one tool that says so;
    /// once the app runs, its tools come in its place.</summary>
    public static JsonArray Fallback() => new(new JsonObject
    {
        ["name"] = "get_model",
        ["description"] = "The model open in PushPullMe. (PushPullMe has not run on this PC yet: start it and tick 'AI assistant may work along'; its other tools then appear.)",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    });

    /// <summary>The client's name, from initialize (shown in the app with every call).</summary>
    public string Client { get; private set; } = "?";

    /// <summary>The tools as the client last got them (null: it has not asked yet).</summary>
    private JsonArray? _tools;

    /// <summary>Whether the client said it is ready (only then may the server send it notifications).</summary>
    private bool _initialized;

    /// <summary>One thing at a time: a message from the client, or a look at whether the tools changed.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Messages read from <paramref name="input"/> answered on <paramref name="output"/> until the input ends;
    /// with <paramref name="watch"/>, every so often a look at whether the tools changed (and if so, the client told).</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default, TimeSpan? watch = null)
    {
        var write = new SemaphoreSlim(1, 1);
        async Task Send(string text)
        {
            await write.WaitAsync(CancellationToken.None);
            try { await output.WriteLineAsync(text); await output.FlushAsync(CancellationToken.None); }
            finally { write.Release(); }
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = watch is { } every ? Task.Run(async () =>
        {
            while (!stop.Token.IsCancellationRequested)
            {
                await Task.Delay(every, stop.Token);
                if (await ToolsChangedAsync()) await Send(ListChanged);
            }
        }, stop.Token) : Task.CompletedTask;
        try
        {
            while (await input.ReadLineAsync(ct) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                if (await HandleAsync(line) is { } answer) await Send(answer);
            }
        }
        finally
        {
            stop.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Whether the tools now differ from what the client last got (then they are taken as the new ones); false
    /// while the client is not ready or has not asked yet, or the tools cannot be had just now.</summary>
    public async Task<bool> ToolsChangedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_initialized || _tools == null || await fetchTools() is not { } now || JsonNode.DeepEquals(now, _tools)) return false;
            _tools = now;
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The tools as they are now (the last known when they cannot be had).</summary>
    private async Task<JsonArray> ToolsAsync()
    {
        if (await fetchTools() is { } now) _tools = now;
        return _tools ??= Fallback();
    }

    /// <summary>The answer to one message (null: none, it was a notification or a response).</summary>
    public async Task<string?> HandleAsync(string line)
    {
        await _gate.WaitAsync();
        try { return await HandleOneAsync(line); }
        finally { _gate.Release(); }
    }

    private async Task<string?> HandleOneAsync(string line)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        var id = msg["id"]?.DeepClone();
        string? method = msg["method"]?.GetValue<string>();
        if (method == "notifications/initialized") _initialized = true;
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
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                    ["serverInfo"] = new JsonObject { ["name"] = "pushpullme", ["version"] = "0.5" },
                    ["instructions"] = "Works in a running PushPullMe window (a SketchUp/DesignSpark-like modeller) whose user ticked 'AI assistant may work along'. Lengths in mm, z up. Start with get_model; every change can be undone (undo, or Ctrl+Z by the user).",
                });
            }
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = (await ToolsAsync()).DeepClone() });
            case "tools/call":
            {
                string? name = p["name"]?.GetValue<string>();
                // (a tool the client got; else one the app has now, newer than the list the client has)
                bool Known(JsonArray tools) => tools.Any(t => t?["name"]?.GetValue<string>() == name);
                if (name == null || !Known(_tools ?? Fallback()) && !Known(await ToolsAsync())) return Error(id, -32602, $"Unknown tool: {name}");
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
