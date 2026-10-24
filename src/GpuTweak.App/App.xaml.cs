using System.IO;
using System.Windows;
using System.Windows.Threading;
using GpuTweak.App.ViewModels;
using GpuTweak.App.Views;
using GpuTweak.Core;
using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Safety;
using GpuTweak.Core.Settings;

namespace GpuTweak.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var dataDirectory = DataPaths.Resolve();
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logos"));
        L.SetLanguage(new SettingsStore(dataDirectory).Load().Language);

        // Crash check must happen before anything touches the card.
        var crash = new CrashGuard(dataDirectory).BeginSession();

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var backend = GpuBackendFactory.Create(demo);
        if (backend is null)
        {
            var answer = MessageBox.Show(
                L.T("Desteklenen bir NVIDIA ya da AMD ekran kartı bulunamadı. Ekran kartı sürücüsünün kurulu olduğundan emin ol.\n\n") +
                L.T("Arayüzü denemek için demo modunda açılsın mı?"),
                "Tweakstone", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
            backend = new DemoBackend();
        }

        _viewModel = new MainViewModel(backend, dataDirectory, crash);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        // Started by Windows at sign-in: stay out of the way, the profile is applied in the background.
        if (e.Args.Any(a => a.Equals(Native.StartupTask.StartupArgument, StringComparison.OrdinalIgnoreCase)))
            window.StartInTray();
        else
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, L.T("Tweakstone – beklenmeyen hata"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
