using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Localization;
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
/// <summary>What the live panel draws: the latest results and which threads have ever been wrong in this run.</summary>
/// <param name="ArtifactsFound">Batches with wrong results since the stress test was created (across all tuning steps).</param>
public sealed record StressSnapshot(uint[] Results, byte[] ErrorMask, long Checked, long Errors, StressPattern Pattern, int[]? Image, int ArtifactsFound, long TotalChecked);

public sealed class D3D11StressTest : IStressTest
{
    /// <summary>Results are 1024 × 1024, one pixel per GPU thread.</summary>
    public const int MapSize = 1024;
    private const uint ThreadsPerGroup = 256;
    private const uint Groups = 4096;
    private const uint ThreadCount = ThreadsPerGroup * Groups;
    private const uint Seed = 0x9E3779B9;
    // Short dispatches leave room for Windows to draw the desktop between them (long ones made the screen tear).
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(10);

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

    /// <summary>
    /// Memory test: each pass reads every value of one 256 MB buffer, checks it against what the previous pass wrote
    /// (a hash of its index and the pass number, so the expected value never needs the data itself), counts mismatches,
    /// and writes the next pass's values to the other buffer. The written value does not depend on what was read, so a
    /// single bad transfer is counted once and does not spread.
    /// </summary>
    private const string MemoryShader = """
        cbuffer MemParams : register(b0) { uint PassIndex; uint Seed; uint Count4; uint Reserved; };
        RWByteAddressBuffer Source : register(u0);
        RWByteAddressBuffer Target : register(u1);
        RWByteAddressBuffer Errors : register(u2);

        uint Value(uint j, uint passIndex)
        {
            uint h = j * 0x9E3779B1u ^ (passIndex * 0x85EBCA77u + Seed);
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return h;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            const uint threads = 16384u * 256u;
            uint bad = 0;
            [unroll] for (uint k = 0; k < 4; k++)
            {
                uint q = id.x + k * threads;   // neighbouring threads touch neighbouring 16-byte blocks
                if (q < Count4)
                {
                    uint j = q * 4;
                    uint4 got = Source.Load4(q * 16);
                    uint4 want = uint4(Value(j, PassIndex), Value(j + 1, PassIndex), Value(j + 2, PassIndex), Value(j + 3, PassIndex));
                    bad += (got.x != want.x ? 1u : 0u) + (got.y != want.y ? 1u : 0u) + (got.z != want.z ? 1u : 0u) + (got.w != want.w ? 1u : 0u);
                    Target.Store4(q * 16, uint4(Value(j, PassIndex + 1), Value(j + 1, PassIndex + 1), Value(j + 2, PassIndex + 1), Value(j + 3, PassIndex + 1)));
                }
            }
            if (bad != 0) { uint ignored; Errors.InterlockedAdd(0, bad, ignored); }
        }
        """;

    /// <summary>Same hash as the memory shader, used to write the starting buffer.</summary>
    private const string MemoryFillShader = """
        cbuffer MemParams : register(b0) { uint PassIndex; uint Seed; uint Count4; uint Reserved; };
        RWByteAddressBuffer Target : register(u1);

        uint Value(uint j, uint passIndex)
        {
            uint h = j * 0x9E3779B1u ^ (passIndex * 0x85EBCA77u + Seed);
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return h;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            const uint threads = 16384u * 256u;
            [unroll] for (uint k = 0; k < 4; k++)
            {
                uint q = id.x + k * threads;
                if (q < Count4)
                {
                    uint j = q * 4;
                    Target.Store4(q * 16, uint4(Value(j, PassIndex), Value(j + 1, PassIndex), Value(j + 2, PassIndex), Value(j + 3, PassIndex)));
                }
            }
        }
        """;

    private const uint MemoryBufferBytes = 256u << 20;
    private const uint MemoryGroups = 16384;
    private const int MemoryPassesPerBatch = 16;
    private ID3D11ComputeShader? _memoryShader, _memoryFill;
    private ID3D11Buffer? _memoryA, _memoryB, _memoryErrors, _memoryErrorsStaging, _memoryConstants;
    private ID3D11UnorderedAccessView? _memoryViewA, _memoryViewB, _memoryErrorsView;
    /// <summary>Pass number whose values the current source buffer holds.</summary>
    private uint _memoryPass;
    private bool _memorySourceIsA;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryParams
    {
        public uint Pass, Seed, Count4, Reserved;
    }

    /// <summary>Side of the animated preview image (pixels). One preview frame is drawn after every verified batch.</summary>
    public const int ImageSize = 384;

    /// <summary>
    /// Animated Julia fractal shown in the live panel, so the user can see the card working.
    /// It is not part of the verification; correctness is checked by the main shader.
    /// </summary>
    private const string VisualShader = """
        cbuffer Visual : register(b1) { float Time; float3 Pad; };
        RWStructuredBuffer<uint> Image : register(u1);

        float3 Palette(float t)
        {
            // Smooth blue → violet → orange gradient.
            return 0.5f + 0.5f * cos(6.28318f * (float3(0.62f, 0.55f, 0.50f) * t + float3(0.55f, 0.65f, 0.80f)));
        }

        [numthreads(16, 16, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            const uint size = 384;
            if (id.x >= size || id.y >= size) return;
            float2 z = (float2(id.xy) / size - 0.5f) * float2(3.0f, 3.0f);
            float angle = Time * 0.35f;
            float2 c = 0.7885f * float2(cos(angle), sin(angle));
            uint i = 0;
            const uint maxIter = 160;
            [loop] for (; i < maxIter && dot(z, z) < 16.0f; i++)
                z = float2(z.x * z.x - z.y * z.y, 2.0f * z.x * z.y) + c;
            float smoothIter = i < maxIter ? i + 1 - log2(max(1e-6f, log2(dot(z, z)) * 0.5f)) : 0;
            float3 color = i < maxIter ? Palette(smoothIter * 0.035f + Time * 0.05f) * saturate(smoothIter / 12.0f) : float3(0.03f, 0.03f, 0.05f);
            uint3 rgb = uint3(saturate(color) * 255.0f);
            Image[id.y * size + id.x] = (rgb.r << 16) | (rgb.g << 8) | rgb.b;
        }
        """;

    private ID3D11ComputeShader? _visual;
    private ID3D11Buffer? _image, _imageStaging, _visualConstants;
    private ID3D11UnorderedAccessView? _imageView;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [StructLayout(LayoutKind.Sequential)]
    private struct VisualParams
    {
        public float Time, Pad0, Pad1, Pad2;
    }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _output, _staging, _constants;
    private ID3D11UnorderedAccessView? _view;
    private uint _iterations = 256;
    private uint[]? _reference;
    private byte[] _errorMask = new byte[ThreadCount];
    private long _checked, _errors;
    private int _artifactsFound;
    private long _totalChecked;
    private StressPattern _pattern;

    /// <summary>Latest state for the live panel. Arrays are replaced, never mutated after publishing, so reading from another thread is safe.</summary>
    public StressSnapshot? Snapshot { get; private set; }

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
            if (!first.AsSpan().SequenceEqual(second)) return StressResult.Fail(L.T("stok ayarlarda iki ölçüm farklı çıktı"));
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
        _errorMask = new byte[ThreadCount];
        _checked = _errors = 0;
        _pattern = pattern;
        var watch = Stopwatch.StartNew();
        double work = 0;
        double busySeconds = 0;
        var segment = 0;

        try
        {
            if (pattern == StressPattern.Memory) return RunMemory(duration, cancellationToken);
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
                    // Four small batches instead of one big one, so the preview updates about 10 times a second.
                    const int passes = 8;
                    for (var batch = 0; batch < 4; batch++)
                    {
                        var start = watch.Elapsed;
                        if (!Verify(passes, out var reason)) return StressResult.Fail(reason);
                        busySeconds += (watch.Elapsed - start).TotalSeconds;
                        work += (double)passes * ThreadCount * _iterations;
                    }
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

    /// <summary>Bandwidth run; Score is GB/s actually moved (one read and one write of the buffer per pass).</summary>
    private StressResult RunMemory(TimeSpan duration, CancellationToken cancellationToken)
    {
        EnsureMemoryResources();
        var context = _context!;
        var watch = Stopwatch.StartNew();
        double bytes = 0, busySeconds = 0;
        while (watch.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = watch.Elapsed;
            context.ClearUnorderedAccessView(_memoryErrorsView!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
            context.CSSetShader(_memoryShader);
            context.CSSetUnorderedAccessView(2, _memoryErrorsView);
            for (var p = 0; p < MemoryPassesPerBatch; p++)
            {
                var data = new MemoryParams { Pass = _memoryPass, Seed = Seed, Count4 = MemoryBufferBytes / 16 };
                context.UpdateSubresource(in data, _memoryConstants!);
                context.CSSetConstantBuffer(0, _memoryConstants);
                context.CSSetUnorderedAccessView(0, _memorySourceIsA ? _memoryViewA : _memoryViewB);
                context.CSSetUnorderedAccessView(1, _memorySourceIsA ? _memoryViewB : _memoryViewA);
                context.Dispatch(MemoryGroups, 1, 1);
                // Unbind before the buffers swap roles, so the next pass sees this one's writes.
                context.CSSetUnorderedAccessView(0, null);
                context.CSSetUnorderedAccessView(1, null);
                _memoryPass++;
                _memorySourceIsA = !_memorySourceIsA;
            }
            context.CopyResource(_memoryErrorsStaging!, _memoryErrors!);
            var mapped = context.Map(_memoryErrorsStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            uint errors;
            try { errors = (uint)Marshal.ReadInt32(mapped.DataPointer); }
            finally { context.Unmap(_memoryErrorsStaging!, 0); }
            busySeconds += (watch.Elapsed - start).TotalSeconds;
            bytes += 2.0 * MemoryBufferBytes * MemoryPassesPerBatch;

            var values = (long)(MemoryBufferBytes / 4) * MemoryPassesPerBatch;
            _checked += values;
            _totalChecked += values;
            _errors += errors;
            if (errors > 0) _artifactsFound++;
            Snapshot = new StressSnapshot(Snapshot?.Results ?? new uint[ThreadCount], _errorMask, _checked, _errors, StressPattern.Memory,
                DrawPreview(), _artifactsFound, _totalChecked);
            if (errors > 0) return StressResult.Fail(L.F("{0:N0} bellek hatası", errors));
        }
        return new StressResult(true, busySeconds > 0 ? bytes / busySeconds / 1e9 : 0, null);
    }

    private void EnsureMemoryResources()
    {
        if (_memoryShader is not null) return;
        var device = _device!;
        _memoryShader = device.CreateComputeShader(Compiler.Compile(MemoryShader, "main", "memory.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        _memoryFill = device.CreateComputeShader(Compiler.Compile(MemoryFillShader, "main", "memoryfill.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        (_memoryA, _memoryViewA) = RawBuffer(MemoryBufferBytes);
        (_memoryB, _memoryViewB) = RawBuffer(MemoryBufferBytes);
        (_memoryErrors, _memoryErrorsView) = RawBuffer(16);
        _memoryErrorsStaging = device.CreateBuffer(new BufferDescription(16, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        _memoryConstants = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<MemoryParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

        // Fill buffer A with pass 0 values.
        var context = _context!;
        _memoryPass = 0;
        _memorySourceIsA = true;
        var data = new MemoryParams { Pass = 0, Seed = Seed, Count4 = MemoryBufferBytes / 16 };
        context.UpdateSubresource(in data, _memoryConstants);
        context.CSSetShader(_memoryFill);
        context.CSSetConstantBuffer(0, _memoryConstants);
        context.CSSetUnorderedAccessView(1, _memoryViewA);
        context.Dispatch(MemoryGroups, 1, 1);
        context.CSSetUnorderedAccessView(1, null);

        (ID3D11Buffer, ID3D11UnorderedAccessView) RawBuffer(uint size)
        {
            var buffer = device.CreateBuffer(new BufferDescription(size, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0));
            var view = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
            {
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Format = Format.R32_Typeless,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = size / 4, Flags = BufferUnorderedAccessViewFlags.Raw },
            });
            return (buffer, view);
        }
    }

    private bool Verify(int passes, out string reason)
    {
        var result = RunPasses(passes);
        var expected = Expected(passes);
        var errors = 0;
        byte[]? mask = null;
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == expected[i]) continue;
            errors++;
            mask ??= (byte[])_errorMask.Clone();
            mask[i] = 1;
        }
        if (mask is not null)
        {
            _errorMask = mask;
            _artifactsFound++;
        }
        _checked += result.Length;
        _totalChecked += result.Length;
        _errors += errors;
        Snapshot = new StressSnapshot(result, _errorMask, _checked, _errors, _pattern, DrawPreview(), _artifactsFound, _totalChecked);
        reason = errors == 0 ? "" : L.F("{0:N0} hesap hatası", errors);
        return errors == 0;
    }

    private int[]? DrawPreview()
    {
        if (_visual is null) return null;
        try
        {
            var context = _context!;
            var visual = new VisualParams { Time = (float)_clock.Elapsed.TotalSeconds };
            context.UpdateSubresource(in visual, _visualConstants!);
            context.CSSetShader(_visual);
            context.CSSetUnorderedAccessView(1, _imageView);
            context.CSSetConstantBuffer(1, _visualConstants);
            context.Dispatch((ImageSize + 15) / 16, (ImageSize + 15) / 16, 1);
            context.CopyResource(_imageStaging!, _image!);
            var mapped = context.Map(_imageStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var image = new int[ImageSize * ImageSize];
                Marshal.Copy(mapped.DataPointer, image, 0, image.Length);
                return image;
            }
            finally
            {
                context.Unmap(_imageStaging!, 0);
            }
        }
        catch (Exception e) when (e is SharpGenException or COMException)
        {
            return null; // the preview is cosmetic; the main check reports real failures
        }
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

    /// <summary>Picks the loop length so one dispatch takes about 10 ms: heavy enough to load the GPU, short enough to stay far from the 2 s driver timeout and let the desktop draw.</summary>
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

        CreatePreview();
    }

    /// <summary>The preview is cosmetic: if it cannot be created, the test still runs without it.</summary>
    private void CreatePreview()
    {
        try
        {
            CreatePreviewResources();
        }
        catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException)
        {
            _visual?.Dispose();
            _visual = null;
        }
    }

    private void CreatePreviewResources()
    {
        var device = _device!;
        var visualBytecode = Compiler.Compile(VisualShader, "main", "preview.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _visual = device.CreateComputeShader(visualBytecode.Span);
        const uint pixels = ImageSize * ImageSize;
        _image = device.CreateBuffer(new BufferDescription(pixels * sizeof(uint), BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, sizeof(uint)));
        _imageStaging = device.CreateBuffer(new BufferDescription(pixels * sizeof(uint), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        _visualConstants = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<VisualParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _imageView = device.CreateUnorderedAccessView(_image, new UnorderedAccessViewDescription(_image, Format.Unknown, 0, pixels));
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
            ? L.F("sürücü sıfırlandı (0x{0:X8})", r.Code)
            : L.F("DirectX hatası: {0}", e.Message);
    }

    private void Release()
    {
        _memoryViewA?.Dispose(); _memoryViewA = null;
        _memoryViewB?.Dispose(); _memoryViewB = null;
        _memoryErrorsView?.Dispose(); _memoryErrorsView = null;
        _memoryA?.Dispose(); _memoryA = null;
        _memoryB?.Dispose(); _memoryB = null;
        _memoryErrors?.Dispose(); _memoryErrors = null;
        _memoryErrorsStaging?.Dispose(); _memoryErrorsStaging = null;
        _memoryConstants?.Dispose(); _memoryConstants = null;
        _memoryShader?.Dispose(); _memoryShader = null;
        _memoryFill?.Dispose(); _memoryFill = null;
        _imageView?.Dispose(); _imageView = null;
        _visualConstants?.Dispose(); _visualConstants = null;
        _imageStaging?.Dispose(); _imageStaging = null;
        _image?.Dispose(); _image = null;
        _visual?.Dispose(); _visual = null;
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
