using Avalonia;

namespace Imgagent.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Imgagent.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
