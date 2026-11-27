/*
* @file        PresentationProbeSession.cs
* @description Runs the standalone D3D12 synthetic presentation benchmark.
* @author      Damon William
* @date        2026-11-27
*
* See the repository license and attribution notices.
*/

using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Win32 = global::TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_FENCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_FEATURE;
using static TerraFX.Interop.DirectX.D3D12_HEAP_TYPE;
using static TerraFX.Interop.DirectX.DXGI_ADAPTER_FLAG;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.DXGI_GPU_PREFERENCE;
using static TerraFX.Interop.DirectX.DXGI_SWAP_EFFECT;

namespace IraRationis.Graphics.Presentation;

internal sealed unsafe class PresentationProbeSession(TextWriter writer, bool gpuUpload) : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int NoFramesPresented = unchecked((int)0x80004005);
    private const int GpuUploadUnsupported = unchecked((int)0x887A0004);
    private const int SwapChainOccluded = 0x087A0001;
    private const uint FenceTimeoutMilliseconds = 10_000;
    private const uint PresentSyncInterval = 1;
    private const uint WaitSignaled = 0;
    private readonly PresentationProbeFrame[] _frames = [new(), new()];
    private readonly PresentationProbeWindow _window = new();
    private ComPtr<IDXGIFactory6> _factory;
    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _queue;
    private ComPtr<IDXGISwapChain3> _swapChain;
    private ComPtr<ID3D12GraphicsCommandList> _commands;
    private ComPtr<ID3D12Fence> _fence;
    private HANDLE _completion;

    internal int Run(uint seconds, uint framesPerSecond)
    {
        var result = Initialize();

        return result < 0 ? result : PresentFrames(seconds, framesPerSecond);
    }

    public void Dispose()
    {
        if (_completion != default)
        {
            _ = Win32.CloseHandle(_completion);
            _completion = default;
        }

        _fence.Dispose();
        _commands.Dispose();

        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _swapChain.Dispose();
        _queue.Dispose();
        _window.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }

    private int Initialize()
    {
        int result;
        fixed (IDXGIFactory6** factory = _factory)
        {
            result = DirectX.CreateDXGIFactory2(0, Win32.__uuidof<IDXGIFactory6>(), (void**)factory);
        }

        if (result < 0)
        {
            return result;
        }

        result = CreateDevice();

        if (result < 0)
        {
            return result;
        }

        if (gpuUpload && !SupportsGpuUpload())
        {
            return GpuUploadUnsupported;
        }

        result = _window.Create(Width, Height);

        if (result < 0)
        {
            return result;
        }

        var queueDescription =
            new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };

        fixed (ID3D12CommandQueue** queue = _queue)
        {
            result = _device.Get()->CreateCommandQueue(&queueDescription, Win32.__uuidof<ID3D12CommandQueue>(),
                (void**)queue);
        }

        if (result < 0)
        {
            return result;
        }

        result = CreateSwapChain();

        if (result < 0)
        {
            return result;
        }

        var uploadHeap = gpuUpload ? D3D12_HEAP_TYPE_GPU_UPLOAD : D3D12_HEAP_TYPE_UPLOAD;

        for (var index = 0; index < _frames.Length; index++)
        {
            result = _frames[index].Create(_device.Get(), _swapChain.Get(), (uint)index, uploadHeap);

            if (result < 0)
            {
                return result;
            }
        }

        fixed (ID3D12GraphicsCommandList** commands = _commands)
        {
            result = _device.Get()->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, _frames[0].Allocator, null,
                Win32.__uuidof<ID3D12GraphicsCommandList>(), (void**)commands);
        }

        if (result < 0)
        {
            return result;
        }

        result = _commands.Get()->Close();

        if (result < 0)
        {
            return result;
        }

        fixed (ID3D12Fence** fence = _fence)
        {
            result = _device.Get()->CreateFence(0, D3D12_FENCE_FLAG_NONE, Win32.__uuidof<ID3D12Fence>(),
                (void**)fence);
        }

        if (result < 0)
        {
            return result;
        }

        _completion = CreateEventW(null, false, false, null);

        return _completion == default ? Marshal.GetHRForLastWin32Error() : 0;
    }

    private int CreateDevice()
    {
        for (var index = 0U;; index++)
        {
            IDXGIAdapter1* adapter = null;

            int result = _factory.Get()->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                Win32.__uuidof<IDXGIAdapter1>(), (void**)&adapter);

            if (result < 0)
            {
                return result;
            }

            try
            {
                var description = default(DXGI_ADAPTER_DESC1);

                result = adapter->GetDesc1(&description);

                if (result < 0 || (description.Flags & (uint)DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                {
                    continue;
                }

                fixed (ID3D12Device** device = _device)
                {
                    result = DirectX.D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_12_0,
                        Win32.__uuidof<ID3D12Device>(), (void**)device);
                }

                if (result >= 0)
                {
                    writer.WriteLine($"[present] Adapter: {new string(description.Description)}");

                    return result;
                }
            }
            finally
            {
                _ = adapter->Release();
            }
        }
    }

    private bool SupportsGpuUpload()
    {
        var options = default(D3D12_FEATURE_DATA_D3D12_OPTIONS16);

        int result = _device.Get()->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16, &options,
            (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS16));

        return result >= 0 && options.GPUUploadHeapSupported;
    }

    private int CreateSwapChain()
    {
        var description = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = Width,
            Height = Height,
            Format = DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = (uint)_frames.Length,
            SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD
        };

        IDXGISwapChain1* initial = null;

        int result = _factory.Get()->CreateSwapChainForHwnd((IUnknown*)_queue.Get(), _window.Handle,
            &description, null, default, &initial);

        if (result < 0)
        {
            return result;
        }

        try
        {
            fixed (IDXGISwapChain3** swapChain = _swapChain)
            {
                return initial->QueryInterface(Win32.__uuidof<IDXGISwapChain3>(), (void**)swapChain);
            }
        }
        finally
        {
            _ = initial->Release();
        }
    }

    private int PresentFrames(uint seconds, uint framesPerSecond)
    {
        var fenceValue = 0UL;
        var presented = 0U;
        var late = 0U;
        var fillTicks = 0L;
        var fenceTicks = 0L;
        var presentTicks = 0L;
        var result = 0;
        var started = Stopwatch.GetTimestamp();
        var deadline = started;
        var period = Stopwatch.Frequency / framesPerSecond;
        var duration = TimeSpan.FromSeconds(seconds);

        try
        {
            while (_window.IsOpen && Stopwatch.GetElapsedTime(started) < duration)
            {
                _window.ProcessMessages();

                if (!_window.IsOpen)
                {
                    break;
                }

                var frame = _frames[_swapChain.Get()->GetCurrentBackBufferIndex()];
                var fenceStarted = Stopwatch.GetTimestamp();

                WaitForFence(frame.FenceValue);

                fenceTicks += Stopwatch.GetTimestamp() - fenceStarted;

                var fillStarted = Stopwatch.GetTimestamp();

                frame.Fill(Width, Height, presented);

                Thread.MemoryBarrier();

                fillTicks += Stopwatch.GetTimestamp() - fillStarted;

                result = frame.RecordCopy(_commands.Get());

                if (result < 0)
                {
                    break;
                }

                var list = (ID3D12CommandList*)_commands.Get();

                _queue.Get()->ExecuteCommandLists(1, &list);

                var presentStarted = Stopwatch.GetTimestamp();

                result = _swapChain.Get()->Present(PresentSyncInterval, 0);
                presentTicks += Stopwatch.GetTimestamp() - presentStarted;

                fenceValue++;

                if (_queue.Get()->Signal(_fence.Get(), fenceValue).Value < 0)
                {
                    Environment.FailFast("[present] GPU fence signal failed.");
                }

                frame.FenceValue = fenceValue;

                if (result < 0 || result == SwapChainOccluded)
                {
                    break;
                }

                presented++;
                deadline += period;

                var now = Stopwatch.GetTimestamp();

                if (now < deadline)
                {
                    SleepUntil(deadline);
                }
                else
                {
                    late++;

                    if (now - deadline > period)
                    {
                        deadline = now;
                    }
                }
            }
        }
        finally
        {
            WaitForFence(fenceValue);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var divisor = Math.Max(presented, 1U);
        var fillMilliseconds = Stopwatch.GetElapsedTime(0, fillTicks).TotalMilliseconds / divisor;
        var fenceMilliseconds = Stopwatch.GetElapsedTime(0, fenceTicks).TotalMilliseconds / divisor;
        var presentMilliseconds = Stopwatch.GetElapsedTime(0, presentTicks).TotalMilliseconds / divisor;
        var mode = gpuUpload ? "GPU_UPLOAD" : "UPLOAD";

        writer.WriteLine(FormattableString.Invariant(
            $"[present] {mode} frames={presented} elapsed={elapsed:F3}s submit_fps={presented / elapsed:F2} late={late} fill_ms={fillMilliseconds:F3} fence_ms={fenceMilliseconds:F3} present_ms={presentMilliseconds:F3}"));
        writer.Flush();

        return result >= 0 && presented == 0 ? NoFramesPresented : result;
    }

    private static void SleepUntil(long deadline)
    {
        for (;;)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            Thread.Sleep((int)Math.Ceiling(remaining.TotalMilliseconds));
        }
    }

    private void WaitForFence(ulong value)
    {
        var fence = _fence.Get();
        var completed = fence->GetCompletedValue();

        if (completed == ulong.MaxValue)
        {
            Environment.FailFast("[present] GPU removed before the presentation fence completed.");
        }

        if (completed >= value)
        {
            return;
        }

        if (fence->SetEventOnCompletion(value, _completion).Value < 0 ||
            Win32.WaitForSingleObject(_completion, FenceTimeoutMilliseconds) != WaitSignaled)
        {
            // Releasing upload buffers before the GPU drains can invalidate in-flight commands.
            Environment.FailFast("[present] GPU fence failed or timed out.");
        }

        completed = fence->GetCompletedValue();

        if (completed < value || completed == ulong.MaxValue)
        {
            Environment.FailFast("[present] GPU fence did not complete after signaling.");
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HANDLE CreateEventW(SECURITY_ATTRIBUTES* attributes, BOOL manualReset, BOOL initialState,
        char* name);
}
