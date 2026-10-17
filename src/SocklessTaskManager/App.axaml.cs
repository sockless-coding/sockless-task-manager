using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Features.Memory;
using Sockless.TaskManager.Features.Performance;
using Sockless.TaskManager.Features.Processes;
using Sockless.TaskManager.Shell;

namespace Sockless.TaskManager;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clock = new SampleClock();

            // Composition root. Each top-level feature is a slice under Features/; add new ones here.
            // Sidebar order follows this list.
            Feature[] features =
            [
                new ProcessesFeature(clock),
                PerformanceModule.Create(clock),
                new MemoryFeature(clock),
            ];

            var shell = new ShellViewModel(clock, features);
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.Exit += (_, _) => shell.Dispose();

            clock.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
