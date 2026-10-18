using Pipe = System.IO.Pipelines.Pipe;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using PushPullMe.Core;
using PushPullMe.Mcp;

namespace PushPullMe.Tests;

/// <summary>The MCP server (src/PushPullMe.Mcp): the protocol as a client speaks it, the tools, and the pipe to the app.</summary>
public class McpTests
{
    /// <summary>A pipe of its own for one test. Short: on Linux and macOS a pipe is a socket in the temp folder, and macOS
    /// allows a socket path of at most 104 characters (its temp folder alone is about 50).</summary>
    private static string TestPipe() => "ppm" + Guid.NewGuid().ToString("N")[..8];

    private static JsonObject Parse(string? s) => (JsonObject)JsonNode.Parse(s!)!;

    /// <summary>The app's tools, as a running PushPullMe gives them.</summary>
    private static Task<JsonArray?> AppTools() => Task.FromResult<JsonArray?>(ToolCatalog.ToJson());

    private static string Request(int id, string method, JsonObject? p = null) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();

    [Fact]
    public async Task Initialize_says_the_version_and_that_there_are_tools_and_keeps_the_client_name()
    {
        var server = new McpServer((_, _, _) => Task.FromResult(new ToolResult("")), AppTools);
        var r = Parse(await server.HandleAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "1" },
        })));
        Assert.Equal(1, r["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Equal("pushpullme", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("claude-code", server.Client);
        // a version it does not know: the newest
        var r2 = Parse(await server.HandleAsync(Request(2, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" })));
        Assert.Equal(McpServer.Versions[0], r2["result"]!["protocolVersion"]!.GetValue<string>());
        // a notification: no answer
        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal("{}", Parse(await server.HandleAsync(Request(3, "ping")))["result"]!.ToJsonString());
    }

    [Fact]
    public async Task Tools_are_listed_each_with_an_object_schema_and_unique_names()
    {
        var server = new McpServer((_, _, _) => Task.FromResult(new ToolResult("")), AppTools);
        var tools = Parse(await server.HandleAsync(Request(1, "tools/list")))["result"]!["tools"]!.AsArray();
        Assert.Equal(ToolCatalog.Tools.Count, tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t!["name"]!.GetValue<string>()).Distinct().Count());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>()));
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
            // (what is required is a property)
            if (t["inputSchema"]!["required"] is JsonArray req)
                foreach (var n in req) Assert.NotNull(t["inputSchema"]!["properties"]![n!.GetValue<string>()]);
        }
        foreach (var name in new[] { "get_model", "push_pull", "screenshot", "make_group", "undo", "simulate" })
            Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task A_tool_call_goes_to_the_app_and_comes_back_as_text_and_image()
    {
        (string Client, string Tool, JsonObject Args)? seen = null;
        var server = new McpServer((c, t, a) => { seen = (c, t, a); return Task.FromResult(new ToolResult("done", [1, 2, 3])); }, AppTools);
        await server.HandleAsync(Request(1, "initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "test" } }));
        var r = Parse(await server.HandleAsync(Request(2, "tools/call", new JsonObject { ["name"] = "push_pull", ["arguments"] = new JsonObject { ["body"] = 0, ["face"] = 1, ["distance"] = 5 } })));
        Assert.Equal(("test", "push_pull"), (seen!.Value.Client, seen.Value.Tool));
        Assert.Equal(5, seen.Value.Args["distance"]!.GetValue<int>());
        var content = r["result"]!["content"]!.AsArray();
        Assert.Equal("done", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[1]!["data"]!.GetValue<string>());
        Assert.False(r["result"]!["isError"]!.GetValue<bool>());
        // an unknown tool, an unknown method, nonsense
        Assert.Equal(-32602, Parse(await server.HandleAsync(Request(3, "tools/call", new JsonObject { ["name"] = "format_disk" })))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, Parse(await server.HandleAsync(Request(4, "resources/list")))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, Parse(await server.HandleAsync("{not json"))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void A_refusal_from_the_core_is_also_there_in_English_for_the_assistant()
    {
        // (whichever language the app speaks: the English along)
        var ex = new PushPullException(Lang.T("Dat kan niet.", "That is not possible."));
        Assert.Equal(Lang.English ? "That is not possible." : "Dat kan niet.", ex.Message);
        Assert.Equal("That is not possible.", ex.English);
        // a message not just made with Lang.T: as it is
        Assert.Equal("plain", new PushPullException("plain").English);
        Lang.T("Iets anders.", "Something else.");
        Assert.Equal("Dat kan niet.", new PushPullException("Dat kan niet.").English);
    }

    [Fact]
    public async Task Without_PushPullMe_listening_a_tool_call_says_how_to_turn_it_on()
    {
        var r = await McpServer.CallApp("test", "get_model", new JsonObject(), pipeName: TestPipe());
        Assert.True(r.IsError);
        Assert.Contains("AI assistant may work along", r.Text);
    }

    [Fact]
    public async Task A_call_goes_over_the_pipe_and_its_answer_comes_back()
    {
        string name = TestPipe();
        // the app's side, played here
        var app = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            var req = (JsonObject)(await PipeProtocol.ReadAsync(pipe))!;
            // (a large answer: a picture of a few MB goes in one line)
            var png = new byte[3_000_000]; png[0] = 0x89; png[^1] = 7;
            await PipeProtocol.WriteAsync(pipe, new JsonObject { ["ok"] = true, ["text"] = $"{req["client"]}:{req["tool"]}:{req["args"]!["body"]}", ["png"] = Convert.ToBase64String(png) });
        });
        var r = await McpServer.CallApp("claude-code", "get_body", new JsonObject { ["body"] = 2 }, pipeName: name);
        await app;
        Assert.False(r.IsError);
        Assert.Equal("claude-code:get_body:2", r.Text);
        Assert.Equal(3_000_000, r.Png!.Length);
        Assert.Equal(7, r.Png[^1]);
    }

    [Fact]
    public async Task The_server_answers_line_by_line_over_stdio()
    {
        var server = new McpServer((_, t, _) => Task.FromResult(new ToolResult("ok " + t)), AppTools);
        var input = new StringReader(string.Join("\n",
            Request(1, "initialize"), """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Request(2, "tools/call", new JsonObject { ["name"] = "undo" })) + "\n");
        var output = new StringWriter();
        await server.RunAsync(input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("ok undo", Parse(lines[1])["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tools_are_the_apps_and_a_change_is_told_to_the_client()
    {
        JsonArray? app = null;
        var server = new McpServer((_, t, _) => Task.FromResult(new ToolResult("ok " + t)), () => Task.FromResult(app?.DeepClone() as JsonArray));
        Assert.True(Parse(await server.HandleAsync(Request(1, "initialize")))["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        // never ran here: the one tool that says so
        var first = Parse(await server.HandleAsync(Request(2, "tools/list")))["result"]!["tools"]!.AsArray();
        Assert.Equal("get_model", Assert.Single(first)!["name"]!.GetValue<string>());
        // not ready yet: no notification
        app = ToolCatalog.ToJson();
        Assert.False(await server.ToolsChangedAsync());
        await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.True(await server.ToolsChangedAsync());
        Assert.False(await server.ToolsChangedAsync());
        // the app gone: the tools as they were, and no change
        var had = app; app = null;
        Assert.False(await server.ToolsChangedAsync());
        Assert.Equal(ToolCatalog.Tools.Count, Parse(await server.HandleAsync(Request(3, "tools/list")))["result"]!["tools"]!.AsArray().Count);
        // a rebuilt app with a new tool: told, and the tool can be called even before the client listed again
        app = had!.DeepClone().AsArray();
        app.Add(new JsonObject { ["name"] = "brand_new", ["description"] = "new", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        Assert.True(await server.ToolsChangedAsync());
        var r = Parse(await server.HandleAsync(Request(4, "tools/call", new JsonObject { ["name"] = "brand_new" })));
        Assert.Equal("ok brand_new", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_server_writes_the_notification_between_its_answers()
    {
        // (the input stays open until the watcher has seen the change)
        var app = new JsonArray(ToolCatalog.ToJson()[0]!.DeepClone());
        var server = new McpServer((_, _, _) => Task.FromResult(new ToolResult("")), () => Task.FromResult<JsonArray?>(app.DeepClone().AsArray()));
        var pipe = new Pipe();
        var output = new StringWriter();
        var run = server.RunAsync(new StreamReader(pipe.Reader.AsStream()), TextWriter.Synchronized(output), watch: TimeSpan.FromMilliseconds(20));
        async Task Say(string line) { await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n")); }
        await Say(Request(1, "initialize"));
        await Say("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await Say(Request(2, "tools/list"));
        for (int i = 0; i < 100 && !output.ToString().Contains("\"id\":2"); i++) await Task.Delay(10);
        app.Add(ToolCatalog.ToJson()[1]!.DeepClone());
        for (int i = 0; i < 200 && !output.ToString().Contains("list_changed"); i++) await Task.Delay(10);
        await pipe.Writer.CompleteAsync();
        await run;
        Assert.Contains(McpServer.ListChanged, output.ToString().Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public async Task The_app_answers_which_tools_it_has_over_the_pipe_and_writes_them_down()
    {
        string name = TestPipe();
        var app = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            var req = (JsonObject)(await PipeProtocol.ReadAsync(pipe))!;
            Assert.True(req["list"]!.GetValue<bool>());
            await PipeProtocol.WriteAsync(pipe, new JsonObject { ["ok"] = true, ["tools"] = ToolCatalog.ToJson() });
        });
        var tools = await PipeProtocol.ListToolsAsync(name, connectMs: 2000);
        await app;
        Assert.Equal(ToolCatalog.Tools.Count, tools!.Count);
        // nobody listening: none
        Assert.Null(await PipeProtocol.ListToolsAsync(name + ".none", connectMs: 50));
        // written down and read back; nothing there: none
        string file = Path.Combine(Path.GetTempPath(), "ppm-tools-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(PipeProtocol.ReadToolsFile(file));
            PipeProtocol.WriteToolsFile(tools, file);
            Assert.True(JsonNode.DeepEquals(tools, PipeProtocol.ReadToolsFile(file)));
        }
        finally { File.Delete(file); }
    }
}
