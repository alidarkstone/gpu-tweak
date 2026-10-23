using System.Windows;
using GpuTweak.Core.Localization;
using Forms = System.Windows.Forms;

namespace GpuTweak.App.Native;

/// <summary>
/// Notification-area icon, so closing the window keeps the overlay, stability tracking and the applied
/// profile running in the background.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _hintShown;

    public TrayIcon(Action open, Action exit)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Aç"), null, (_, _) => open());
        menu.Items.Add(L.T("Çıkış"), null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Tweakstone",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void SetText(string text) => _icon.Text = text;

    /// <summary>Tells the user once per run where the app went after they pressed X.</summary>
    public void ShowHiddenHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "Tweakstone",
            L.T("Arka planda çalışmaya devam ediyor. Açmak için bu simgeye tıkla, kapatmak için sağ tıklayıp Çıkış'ı seç."),
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
