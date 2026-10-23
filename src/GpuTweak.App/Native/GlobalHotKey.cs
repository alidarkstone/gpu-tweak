using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GpuTweak.App.Native;

/// <summary>System-wide hotkey (works while a game has focus).</summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    public const uint ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;
    private readonly Action _callback;

    public GlobalHotKey(Window owner, uint modifiers, uint virtualKey, Action callback, int id = 0x4754)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle)!;
        _id = id;
        _callback = callback;
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(_source.Handle, _id, modifiers | ModNoRepeat, virtualKey);
    }

    public bool IsRegistered { get; }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _id)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, _id);
        _source.RemoveHook(Hook);
    }
}
