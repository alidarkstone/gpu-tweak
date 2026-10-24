using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GpuTweak.App.ViewModels;

namespace GpuTweak.App.Views;

/// <summary>
/// Converters for the number next to a slider, which doubles as a text box for values the slider is too coarse to
/// hit (e.g. +405). The value is taken on Enter (a plain XAML KeyBinding to <see cref="Commit"/>) or when the box
/// loses focus. No key event handlers on purpose: v0.12.1 had key and mouse hooks here and was flagged by
/// Defender's ML heuristic (likely, not confirmed, the cause).
/// </summary>
public static class ValueBox
{
    /// <summary>Takes the typed value of the focused box.</summary>
    public static ICommand Commit { get; } = new RelayCommand(_ =>
    {
        if (Application.Current?.MainWindow is not { } window || FocusManager.GetFocusedElement(window) is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
    });

    /// <summary>Shows "+405" and reads back "405", "+405", "405 MHz" or "-30".</summary>
    public static IValueConverter Signed { get; } = new NumberConverter(signed: true);
    public static IValueConverter Plain { get; } = new NumberConverter(signed: false);

    private sealed class NumberConverter(bool signed) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is int i ? signed ? i.ToString("+0;-0;0", CultureInfo.InvariantCulture) : i.ToString(CultureInfo.InvariantCulture) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = (value as string ?? "").Trim();
            var negative = text.StartsWith('-') || text.StartsWith('−');
            var digits = new string(text.TakeWhile(c => !char.IsLetter(c)).Where(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return Binding.DoNothing;
            return negative ? -number : number;
        }
    }
}
