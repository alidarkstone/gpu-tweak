using System.Windows;
using System.Windows.Media;
using GpuTweak.App.Native;
using GpuTweak.App.ViewModels;

namespace GpuTweak.App.Views;

public partial class TuneMonitorWindow : Window
{
    public TuneMonitorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var area = SystemParameters.WorkArea;
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.85 / Width, area.Height * 0.85 / Height));
        Width *= scale;
        Height *= scale;

        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.ApplyMica(this))
            {
                Background = Brushes.Transparent;
                Root.Background = (Brush)FindResource("WindowTintBrush");
            }
        };
    }
}
