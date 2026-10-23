using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GpuTweak.App.Native;
using GpuTweak.App.ViewModels;
using GpuTweak.Core.Settings;

namespace GpuTweak.App.Views;

public partial class OverlayWindow : Window
{
    private const double EdgeMargin = 16;
    private readonly OverlayViewModel _viewModel;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        SourceInitialized += (_, _) => WindowEffects.MakeClickThrough(this);
        SizeChanged += (_, _) => Reposition();
        viewModel.PropertyChanged += OnSettingsChanged;
        _topmostTimer.Tick += (_, _) => { if (IsVisible) WindowEffects.BringToTop(this); };
        _topmostTimer.Start();
        Closed += (_, _) => _topmostTimer.Stop();
        IsVisibleChanged += (_, _) => Reposition();
    }

    public new void Show()
    {
        base.Show();
        UpdateVisibility();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(OverlayViewModel.Enabled)) UpdateVisibility();
        if (e.PropertyName is "" or nameof(OverlayViewModel.Corner)) Reposition();
    }

    private void UpdateVisibility() => Visibility = _viewModel.Enabled ? Visibility.Visible : Visibility.Hidden;

    /// <summary>Uses full screen bounds rather than the work area, because games cover the taskbar.</summary>
    private void Reposition()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var corner = _viewModel.Corner;
        Left = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? EdgeMargin : screenWidth - ActualWidth - EdgeMargin;
        Top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? EdgeMargin : screenHeight - ActualHeight - EdgeMargin;
    }
}
