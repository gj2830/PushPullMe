using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace PushPullMe;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Begin = lifetime =>
        {
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow(desktop.Args is { Length: > 0 } a ? a[0] : null);
        };
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // (macOS: OpenGL, which the 3D view draws with, before Metal)
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
