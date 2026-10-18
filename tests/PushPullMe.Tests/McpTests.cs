using System.IO.Pipes;
using System.Text.Json.Nodes;
using PushPullMe.Mcp;

namespace PushPullMe.Tests;

/// <summary>The MCP server (src/PushPullMe.Mcp): the protocol as a client speaks it, the tools, and the pipe to the app.</summary>
public class McpTests
{
    private static JsonObject Parse(string? s) => (JsonObject)JsonNode.Parse(s!)!;

    private static string Request(int id, string method, JsonObject? p = null) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();

    [Fact]
    public async Task Initialize_says_the_version_and_that_there_are_tools_and_keeps_the_client_name()
    {
        var server = new McpServer((_, _, _) => Task.FromResult(new ToolResult("")));
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
        var server = new McpServer((_, _, _) => Task.FromResult(new ToolResult("")));
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
        var server = new McpServer((c, t, a) => { seen = (c, t, a); return Task.FromResult(new ToolResult("done", [1, 2, 3])); });
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
    public async Task Without_PushPullMe_listening_a_tool_call_says_how_to_turn_it_on()
    {
        var r = await McpServer.CallApp("test", "get_model", new JsonObject(), pipeName: "PushPullMe.mcp.test." + Guid.NewGuid().ToString("N"));
        Assert.True(r.IsError);
        Assert.Contains("AI assistant may work along", r.Text);
    }

    [Fact]
    public async Task A_call_goes_over_the_pipe_and_its_answer_comes_back()
    {
        string name = "PushPullMe.mcp.test." + Guid.NewGuid().ToString("N");
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
        var server = new McpServer((_, t, _) => Task.FromResult(new ToolResult("ok " + t)));
        var input = new StringReader(string.Join("\n",
            Request(1, "initialize"), """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Request(2, "tools/call", new JsonObject { ["name"] = "undo" })) + "\n");
        var output = new StringWriter();
        await server.RunAsync(input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("ok undo", Parse(lines[1])["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
