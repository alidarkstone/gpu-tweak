using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GpuTweak.App.Native;
using GpuTweak.App.ViewModels;

namespace GpuTweak.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlay;
    private GlobalHotKey? _hotKey, _artifactHotKey;
    private readonly TrayIcon _tray;
    private bool _exiting;

    // Layout design size (see the Viewbox in MainWindow.xaml); used to open the window with the right proportions.
    private const double DesignWidth = 1400, DesignHeight = 1480;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _overlay = new OverlayWindow(viewModel.Overlay);
        _tray = new TrayIcon(ShowFromTray, ExitApp);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayText)) _tray.SetText(viewModel.TrayText);
        };

        FitToScreen();

        foreach (RadioButton corner in CornerPicker.Children)
            corner.IsChecked = (string)corner.Tag == viewModel.Overlay.Corner.ToString();

        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.ApplyMica(this))
            {
                Background = Brushes.Transparent;
                Root.Background = (Brush)FindResource("WindowTintBrush");
            }
            _hotKey = new GlobalHotKey(this, GlobalHotKey.ModControl | GlobalHotKey.ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.O),
                () => viewModel.Overlay.Enabled = !viewModel.Overlay.Enabled);
            // Report an artifact without leaving the game.
            _artifactHotKey = new GlobalHotKey(this, GlobalHotKey.ModControl | GlobalHotKey.ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.A),
                () => { if (viewModel.ReportArtifactCommand.CanExecute(null)) viewModel.ReportArtifactCommand.Execute(null); }, id: 0x4755);
        };
        Loaded += (_, _) => _overlay.Show();
        // X hides to the notification area; the app really exits only from the tray menu.
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            Hide();
            _tray.ShowHiddenHint();
        };
        Closed += (_, _) =>
        {
            _tray.Dispose();
            _hotKey?.Dispose();
            _artifactHotKey?.Dispose();
            _overlay.Close();
        };
    }

    /// <summary>Started at Windows sign-in: run in the notification area without showing the window.</summary>
    public void StartInTray()
    {
        // Show once so the window handle exists (hotkeys) and the overlay starts, then hide straight away.
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Show();
        Hide();
        _overlay.Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Opens the window at most 90% of the screen with the layout's proportions. After that the
    /// content scales with the window, whatever size the user drags it to.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.9 / DesignWidth, area.Height * 0.9 / DesignHeight));
        Width = DesignWidth * scale;
        Height = DesignHeight * scale;
    }

    private void OnCornerChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string corner }) _viewModel.Overlay.SetCorner(corner);
    }
}
