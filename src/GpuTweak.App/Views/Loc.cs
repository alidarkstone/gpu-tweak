using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using GpuTweak.Core.Localization;

namespace GpuTweak.App.Views;

/// <summary>
/// Translated text in XAML: <c>local:Loc.Text="Türkçe metin"</c> instead of <c>Text="Türkçe metin"</c>.
/// The Turkish text is the key (see <see cref="L"/>), and the element updates when the language changes.
/// </summary>
public static class Loc
{
    private static readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Property, string Key)> Targets = [];

    static Loc() => L.LanguageChanged += Refresh;

    public static readonly DependencyProperty TextProperty = Register("Text");
    public static readonly DependencyProperty ContentProperty = Register("Content");
    public static readonly DependencyProperty ToolTipProperty = Register("ToolTip");

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string GetContent(DependencyObject d) => (string)d.GetValue(ContentProperty);
    public static void SetContent(DependencyObject d, string value) => d.SetValue(ContentProperty, value);
    public static string GetToolTip(DependencyObject d) => (string)d.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject d, string value) => d.SetValue(ToolTipProperty, value);

    private static DependencyProperty Register(string name) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(Loc), new PropertyMetadata(null, (d, e) => Attach(d, name, (string)e.NewValue)));

    private static void Attach(DependencyObject d, string name, string key)
    {
        DependencyProperty? property = (name, d) switch
        {
            ("Text", TextBlock) => TextBlock.TextProperty,
            ("Text", Run) => Run.TextProperty,
            ("Content", ContentControl) => ContentControl.ContentProperty,
            ("ToolTip", FrameworkElement) => FrameworkElement.ToolTipProperty,
            _ => null,
        };
        if (property is null) return;
        d.SetValue(property, L.T(key));
        lock (Targets) Targets.Add((new WeakReference<DependencyObject>(d), property, key));
    }

    private static void Refresh()
    {
        lock (Targets)
        {
            Targets.RemoveAll(t => !t.Target.TryGetTarget(out _));
            foreach (var (target, property, key) in Targets)
                if (target.TryGetTarget(out var d)) d.SetValue(property, L.T(key));
        }
    }
}

/// <summary>
/// <c>{local:T 'Türkçe metin'}</c> for places an attached property cannot reach (style setters, string formats).
/// Evaluated when the element is created, so items in lists pick up a language change when the list is rebuilt.
/// </summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(text);
}
