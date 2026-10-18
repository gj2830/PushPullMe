using System.Text;
using PushPullMe.Mcp;

// The MCP server PushPullMe offers: an AI assistant (Claude Code, Claude Desktop, VS Code, …) starts this program and
// talks MCP with it over stdin/stdout; each tool call goes on to the running PushPullMe. The tools are the app's: asked
// of it (or, while it does not run, as it last wrote them down) and looked at again every two seconds, so a rebuilt app
// with new tools needs no new server: the client is told and fetches them. (Stdout is for the protocol only: anything
// else goes to stderr.)
Console.InputEncoding = Encoding.UTF8;
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
Console.Error.WriteLine("PushPullMe MCP server: waiting for an MCP client on stdin.");
await new McpServer((client, tool, args) => McpServer.CallApp(client, tool, args),
        async () => await PipeProtocol.ListToolsAsync() ?? PipeProtocol.ReadToolsFile())
    .RunAsync(stdin, stdout, watch: TimeSpan.FromSeconds(2));
