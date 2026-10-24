using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GpuTweak.App.Views;

public static class Converters
{
    public static IValueConverter NotEmptyToVisibility { get; } = new Lambda(v => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible);
    public static IValueConverter EmptyToVisibility { get; } = new Lambda(v => string.IsNullOrEmpty(v as string) ? Visibility.Visible : Visibility.Collapsed);
    public static IValueConverter IsPositive { get; } = new Lambda(v => v is int i && i > 0);
    public static IValueConverter NonZeroToVisible { get; } = new Lambda(v => v is int i && i != 0 ? Visibility.Visible : Visibility.Collapsed);
    public static IValueConverter ZeroToVisible { get; } = new Lambda(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);
    public static IValueConverter FalseToVisible { get; } = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    private sealed class Lambda(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
