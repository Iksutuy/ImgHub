using Avalonia;

namespace ImgHub.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ImgHub.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
