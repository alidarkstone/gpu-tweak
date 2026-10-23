using GpuTweak.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GpuTweak.App.Native;

/// <summary>
/// Counts frames the way PresentMon does: by listening to the Present events Windows itself logs (ETW),
/// so nothing is injected into the game and anti-cheat is not involved. Covers DirectX 9–12 games, and
/// Vulkan/OpenGL games when the NVIDIA driver presents them through DXGI (its default).
/// </summary>
internal sealed class PresentEventSource : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    private readonly TraceEventSession? _session;

    public PresentEventSource(FrameRateTracker tracker)
    {
        try
        {
            // A session left over from a crash has the same name and is replaced.
            _session = new TraceEventSession("GpuTweak-FPS") { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            var source = _session.Source;
            source.AllEvents += e =>
            {
                var id = (int)e.ID;
                if ((e.ProviderGuid == Dxgi && id is DxgiPresentStart or DxgiPresentMpoStart) || (e.ProviderGuid == D3D9 && id == D3D9PresentStart))
                    tracker.OnPresent(e.ProcessID, Seconds(e.TimeStamp));
            };
            new Thread(() =>
            {
                try { source.Process(); } catch (Exception) { /* session stopped */ }
            }) { IsBackground = true, Name = "FPS events" }.Start();
            IsRunning = true;
        }
        catch (Exception)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>False when the event session could not be started (FPS is then simply not shown).</summary>
    public bool IsRunning { get; }

    public static double Seconds(DateTime time) => time.ToUniversalTime().Ticks / (double)TimeSpan.TicksPerSecond;

    public void Dispose() => _session?.Dispose();
}
