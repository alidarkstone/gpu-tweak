using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuTweak.Core.AutoTune;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GpuTweak.App.Stress;

/// <summary>
/// Self-checking GPU workload on a DirectX 11 compute shader. Every thread runs a long chain of
/// floating-point FMA and integer hashing whose result is deterministic for the same hardware and shader.
/// The results recorded at stock are the reference; any later mismatch means the chip computed wrongly,
/// which is the earliest sign of an unstable undervolt (it shows up well before a driver crash).
/// </summary>
public sealed class D3D11StressTest : IStressTest
{
    private const uint ThreadsPerGroup = 256;
    private const uint Groups = 4096;
    private const uint ThreadCount = ThreadsPerGroup * Groups;
    private const uint Seed = 0x9E3779B9;
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(40);

    private const string Shader = """
        cbuffer Params : register(b0) { uint Iterations; uint Seed; uint Reserved0; uint Reserved1; };
        RWStructuredBuffer<uint> Output : register(u0);

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.x;
            float4 a = float4(1.0f + (i & 1023) * 0.0001f, 0.5f + (Seed & 255) * 0.001f, 0.25f, 0.75f);
            const float4 b = float4(0.99991f, 1.00007f, 0.99997f, 1.00003f);
            const float4 c = float4(0.00011f, -0.00013f, 0.00017f, -0.00019f);
            uint h = i * 2654435761u ^ Seed;
            [loop] for (uint k = 0; k < Iterations; k++)
            {
                a = frac(mad(a, b, c)) + 0.5f;
                a = frac(mad(a, b.wzyx, -c)) + 0.5f;
                h = h * 1664525u + 1013904223u;
                h ^= asuint(a.x) ^ (asuint(a.w) >> 7);
            }
            uint result = h ^ asuint(a.y) ^ asuint(a.z);
            // Accumulate across passes so an error in any pass survives to the read-back.
            Output[i] = Output[i] * 31u + result;
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _output, _staging, _constants;
    private ID3D11UnorderedAccessView? _view;
    private uint _iterations = 256;
    private uint[]? _reference;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint Iterations, Seed, Reserved0, Reserved1;
    }

    public Task<StressResult> PrepareAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            CreateDevice();
            Calibrate();
            // Two runs at stock must agree, otherwise the reference itself can't be trusted.
            var first = RunPasses(1);
            var second = RunPasses(1);
            if (!first.AsSpan().SequenceEqual(second)) return StressResult.Fail("stok ayarlarda iki ölçüm farklı çıktı");
            _reference = first;
            return new StressResult(true, 0, null);
        }
        catch (Exception e) when (e is SharpGenException or COMException)
        {
            return StressResult.Fail(Describe(e));
        }
    }, cancellationToken);

    public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (_reference is null) throw new InvalidOperationException("PrepareAsync must run first.");
        var watch = Stopwatch.StartNew();
        double work = 0;
        double busySeconds = 0;
        var segment = 0;

        try
        {
            while (watch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transient = pattern == StressPattern.Transient || (pattern == StressPattern.Mixed && segment % 4 == 3);

                if (transient)
                {
                    // Short bursts with idle gaps: the card boosts to its top clock and current swings hard.
                    for (var burst = 0; burst < 10 && watch.Elapsed < duration; burst++)
                    {
                        if (!Verify(3, out var reason)) return StressResult.Fail(reason);
                        Thread.Sleep(60 + burst * 15 % 90);
                    }
                }
                else
                {
                    const int passes = 8;
                    var start = watch.Elapsed;
                    if (!Verify(passes, out var reason)) return StressResult.Fail(reason);
                    busySeconds += (watch.Elapsed - start).TotalSeconds;
                    work += (double)passes * ThreadCount * _iterations;
                }
                segment++;
            }
        }
        catch (Exception e) when (e is SharpGenException or COMException)
        {
            var reason = Describe(e);
            RecreateAfterDeviceLoss();
            return StressResult.Fail(reason);
        }

        // Score is only taken from heavy segments; transient-only runs report 0.
        var score = busySeconds > 0 ? work / busySeconds / 1e9 : 0;
        return new StressResult(true, score, null);
    }, cancellationToken);

    private bool Verify(int passes, out string reason)
    {
        var result = RunPasses(passes);
        var expected = Expected(passes);
        var errors = 0;
        for (var i = 0; i < result.Length; i++)
            if (result[i] != expected[i]) errors++;
        reason = errors == 0 ? "" : $"{errors:N0} hesap hatası";
        return errors == 0;
    }

    private uint[]? _expectedCache;
    private int _expectedPasses;

    /// <summary>After n passes the buffer holds r*31^(n-1) + … + r, computed with the same uint wrap-around as the GPU.</summary>
    private uint[] Expected(int passes)
    {
        if (_expectedCache is not null && _expectedPasses == passes) return _expectedCache;
        var expected = new uint[_reference!.Length];
        for (var i = 0; i < expected.Length; i++)
        {
            uint value = 0;
            unchecked
            {
                for (var p = 0; p < passes; p++) value = value * 31u + _reference[i];
            }
            expected[i] = value;
        }
        _expectedCache = expected;
        _expectedPasses = passes;
        return expected;
    }

    private uint[] RunPasses(int passes)
    {
        var context = _context!;
        context.ClearUnorderedAccessView(_view!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
        context.CSSetShader(_shader);
        context.CSSetUnorderedAccessView(0, _view);
        context.CSSetConstantBuffer(0, _constants);
        for (var p = 0; p < passes; p++) context.Dispatch(Groups, 1, 1);
        context.CopyResource(_staging!, _output!);

        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new uint[ThreadCount];
            Marshal.Copy(mapped.DataPointer, (int[])(object)result, 0, result.Length);
            return result;
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    /// <summary>Picks the loop length so one dispatch takes about 40 ms: heavy enough to load the GPU, short enough to stay far from the 2 s driver timeout.</summary>
    private void Calibrate()
    {
        for (var round = 0; round < 6; round++)
        {
            WriteConstants();
            RunPasses(1); // warm-up
            var watch = Stopwatch.StartNew();
            RunPasses(2);
            var perDispatch = watch.Elapsed / 2;
            if (perDispatch.TotalMilliseconds < 1) { _iterations *= 8; continue; }
            var factor = TargetDispatch / perDispatch;
            var next = (uint)Math.Clamp(_iterations * factor, 64, 1 << 20);
            if (Math.Abs(factor - 1) < 0.15) break;
            _iterations = next;
        }
        WriteConstants();
    }

    private void WriteConstants()
    {
        var data = new Params { Iterations = _iterations, Seed = Seed };
        _context!.UpdateSubresource(in data, _constants!);
        _expectedCache = null;
    }

    private void CreateDevice()
    {
        Release();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            // Prefer the NVIDIA card (vendor 0x10DE) so an iGPU is never the one being tested.
            if (adapter.Description1.VendorId == 0x10DE && chosen is null) chosen = adapter;
            else adapter.Dispose();
        }

        FeatureLevel[] levels = [FeatureLevel.Level_11_0];
        var result = chosen is not null
            ? D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.None, levels, out _device, out _context)
            : D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.None, levels, out _device, out _context);
        chosen?.Dispose();
        result.CheckError();

        var bytecode = Compiler.Compile(Shader, "main", "stress.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _shader = _device!.CreateComputeShader(bytecode.Span);

        var size = ThreadCount * sizeof(uint);
        _output = _device.CreateBuffer(new BufferDescription(size, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, sizeof(uint)));
        _staging = _device.CreateBuffer(new BufferDescription(size, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        _constants = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _view = _device.CreateUnorderedAccessView(_output, new UnorderedAccessViewDescription(_output, Format.Unknown, 0, ThreadCount));
    }

    /// <summary>After a driver reset (TDR) the device is gone; build a fresh one so the next step can run.</summary>
    private void RecreateAfterDeviceLoss()
    {
        try
        {
            CreateDevice();
            WriteConstants();
        }
        catch (Exception)
        {
            // The next run will throw and be reported as a failure too.
        }
    }

    private string Describe(Exception e)
    {
        var removed = _device?.DeviceRemovedReason;
        return removed is { Failure: true } r
            ? $"sürücü sıfırlandı (0x{r.Code:X8})"
            : $"DirectX hatası: {e.Message}";
    }

    private void Release()
    {
        _view?.Dispose(); _view = null;
        _constants?.Dispose(); _constants = null;
        _staging?.Dispose(); _staging = null;
        _output?.Dispose(); _output = null;
        _shader?.Dispose(); _shader = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => Release();
}
