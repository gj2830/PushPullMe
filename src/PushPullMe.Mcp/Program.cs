using System.Text;
using PushPullMe.Mcp;

// The MCP server PushPullMe offers: an AI assistant (Claude Code, Claude Desktop, VS Code, …) starts this program and
// talks MCP with it over stdin/stdout; each tool call goes on to the running PushPullMe. (Stdout is for the protocol
// only: anything else goes to stderr.)
Console.InputEncoding = Encoding.UTF8;
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
Console.Error.WriteLine("PushPullMe MCP server: waiting for an MCP client on stdin.");
await new McpServer((client, tool, args) => McpServer.CallApp(client, tool, args)).RunAsync(stdin, stdout);
