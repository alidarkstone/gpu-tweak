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

    // Layout design size (see the Viewbox in MainWindow.xaml); used to open the window with the right proportions.
    private const double DesignWidth = 1400, DesignHeight = 960;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _overlay = new OverlayWindow(viewModel.Overlay);

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
        };
        Loaded += (_, _) => _overlay.Show();
        Closed += (_, _) =>
        {
            _hotKey?.Dispose();
            _overlay.Close();
        };
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
