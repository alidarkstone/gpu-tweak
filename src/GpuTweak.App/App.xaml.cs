using System.IO;
using System.Windows;
using System.Windows.Threading;
using GpuTweak.App.ViewModels;
using GpuTweak.App.Views;
using GpuTweak.Core;
using GpuTweak.Core.Backends;
using GpuTweak.Core.Safety;

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

        // Crash check must happen before anything touches the card.
        var crash = new CrashGuard(dataDirectory).BeginSession();

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var backend = GpuBackendFactory.Create(demo);
        if (backend is null)
        {
            var answer = MessageBox.Show(
                "Desteklenen bir NVIDIA ekran kartı bulunamadı (NVML yüklenemedi).\n\n" +
                "AMD desteği bir sonraki sürümde geliyor. Arayüzü denemek için demo modunda açılsın mı?",
                "GPU Tweak", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
            backend = new DemoBackend();
        }

        _viewModel = new MainViewModel(backend, dataDirectory, crash);
        MainWindow = new MainWindow(_viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "GPU Tweak – beklenmeyen hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
