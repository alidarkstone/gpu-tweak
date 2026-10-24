using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace GpuTweak.App.Views;

/// <summary>
/// The number next to a slider doubles as a text box, for values the slider is too coarse to hit (e.g. +405).
/// Enter or leaving the box takes the value, Escape puts the old one back.
/// </summary>
public static class ValueBox
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ValueBox), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    /// <summary>Shows "+405" and reads back "405", "+405", "405 MHz" or "-30".</summary>
    public static IValueConverter Signed { get; } = new NumberConverter(signed: true);
    public static IValueConverter Plain { get; } = new NumberConverter(signed: false);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                box.SelectAll();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                Keyboard.ClearFocus();
                args.Handled = true;
            }
        };
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        // The first click focuses and selects everything instead of placing the caret.
        box.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            box.Focus();
            args.Handled = true;
        };
    }

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
