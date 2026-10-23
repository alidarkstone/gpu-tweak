using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GpuTweak.App.Native;
using GpuTweak.App.ViewModels;

namespace GpuTweak.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlay;
    private GlobalHotKey? _hotKey;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _overlay = new OverlayWindow(viewModel.Overlay);

        foreach (RadioButton corner in CornerPicker.Children)
            corner.IsChecked = (string)corner.Tag == viewModel.Overlay.Corner.ToString();

        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.ApplyMica(this))
            {
                Background = System.Windows.Media.Brushes.Transparent;
                Root.Background = (System.Windows.Media.Brush)FindResource("WindowTintBrush");
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

    private void OnCornerChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string corner }) _viewModel.Overlay.SetCorner(corner);
    }
}
