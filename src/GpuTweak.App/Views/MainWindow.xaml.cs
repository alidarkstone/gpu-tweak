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
    private GlobalHotKey? _hotKey;

    // The layout is designed at this size; smaller screens get the whole UI scaled down to fit.
    private const double DesignWidth = 1120, DesignHeight = 800;
    private readonly ScaleTransform _zoom = new(1, 1);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _overlay = new OverlayWindow(viewModel.Overlay);

        FitToScreen();
        Root.LayoutTransform = _zoom;
        PreviewMouseWheel += OnPreviewMouseWheel;

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
        };
        Loaded += (_, _) => _overlay.Show();
        Closed += (_, _) =>
        {
            _hotKey?.Dispose();
            _overlay.Close();
        };
    }

    /// <summary>
    /// Window sizes are in DPI-independent units, so at 125–150% Windows scaling the design size can be
    /// bigger than the screen. Shrink the window and the content together so everything stays visible.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.9 / DesignWidth, area.Height * 0.9 / DesignHeight));
        SetZoom(scale);
        Width = DesignWidth * scale;
        Height = DesignHeight * scale;
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    private void SetZoom(double scale)
    {
        _zoom.ScaleX = _zoom.ScaleY = Math.Clamp(scale, 0.6, 1.3);
    }

    /// <summary>Ctrl + mouse wheel makes the interface larger or smaller, like in a browser.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(_zoom.ScaleX + (e.Delta > 0 ? 0.05 : -0.05));
        e.Handled = true;
    }

    private void OnCornerChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string corner }) _viewModel.Overlay.SetCorner(corner);
    }
}
