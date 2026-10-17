using Avalonia;

namespace Sockless.TaskManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Software rendering: the UI redraws a few small graphs once a second, so the GPU path
            // buys nothing, while loading ANGLE and the graphics driver costs ~170 MB private memory.
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .LogToTrace();
}
