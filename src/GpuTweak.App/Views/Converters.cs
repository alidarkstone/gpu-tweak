using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GpuTweak.App.Views;

public static class Converters
{
    public static IValueConverter NotEmptyToVisibility { get; } = new Lambda(v => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible);
    public static IValueConverter FalseToVisible { get; } = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    private sealed class Lambda(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
