using System.Diagnostics;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;
using BrowserKit;

namespace PushPullMe;

internal static class Program
{
    private static Task Main(string[] args)
    {
        // (Avalonia's warnings and errors, a GL program that does not compile for instance, in the browser's console)
        Trace.Listeners.Add(new ConsoleTraceListener());
        App.Begin = lifetime =>
        {
            if (lifetime is ISingleViewApplicationLifetime page) page.MainView = BrowserHost.CreateView(args.FirstOrDefault());
        };
        return AppBuilder.Configure<App>()
            .WithBrowserFonts()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
            .StartBrowserAppAsync("out");
    }
}
