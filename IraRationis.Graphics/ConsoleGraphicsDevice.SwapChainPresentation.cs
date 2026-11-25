// SPDX-License-Identifier: GPL-3.0-or-later
// Presentation follows the title-window / DXGI flip-model architecture in
// DexNom/VIctory, victory-gpu/src/host.rs. This implementation uses an
// exact-format copy on the producing queue, not a CPU readback or plane shader.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R191ClientWidth = 1920;
    private const int R191ClientHeight = 1080;
    private const int R19BufferCount = 3;
    private static readonly Lock R19PresentGate = new();
    private static readonly string? R19WindowClass = Environment.GetEnvironmentVariable("IRX_PRESENT_WINDOW_CLASS");
    private static readonly bool EnableR19SwapChain =
        Environment.GetEnvironmentVariable("IRX_PRESENT_SWAPCHAIN") != "0";
    private static readonly bool EnableR191Windowed1080 =
        Environment.GetEnvironmentVariable("IRX_PRESENT_WINDOWED_1080") != "0";
    private static nint _r191ConfiguredWindow;
    private static int _r191WindowWarnings;
    private static R19Presenter? _r19Presenter;
    private static int _r19PresentWarning;
    private static long _r19PresentCount;
    private static readonly Guid R19FactoryId = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid R19SwapChainId = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    // Configure the existing title window at its first PresentX, even if
    // no guest render target has yet been verified. Never resize GPU resources
    // or the swap-chain buffers to match the window: DXGI stretches the source.
    private static void ConfigureR191Window()
    {
        using var r25Timing = BeginR25Timing(R25Stage.WindowSetup);
        if (!EnableR19SwapChain || !EnableR191Windowed1080)
        {
            return;
        }

        lock (R19PresentGate)
        {
            if (_r191ConfiguredWindow != 0 && R19IsWindow(_r191ConfiguredWindow) != 0)
            {
                return;
            }

            var window = FindR19Window();
            if (window == 0)
            {
                return;
            }

            try
            {
                var style = unchecked((uint)(nuint)R19GetWindowLongPtr(window, -16));
                var exStyle = unchecked((uint)(nuint)R19GetWindowLongPtr(window, -20));
                var dpi = R191GetDpiForWindow(window);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                var menu = R191GetMenu(window) != 0 ? 1 : 0;
                var bounds = new R19Rect { Right = R191ClientWidth, Bottom = R191ClientHeight };
                if (R191AdjustWindowRectExForDpi(ref bounds, style, menu, exStyle, dpi) == 0)
                {
                    ReportR191WindowFailure("could not determine window decorations");

                    return;
                }

                var extraWidth = Math.Max(0, bounds.Right - bounds.Left - R191ClientWidth);
                var extraHeight = Math.Max(0, bounds.Bottom - bounds.Top - R191ClientHeight);
                var info = new R191MonitorInfo { Size = (uint)sizeof(R191MonitorInfo) };
                var monitor = R191MonitorFromWindow(window, 2);
                var hasWorkArea = monitor != 0 && R191GetMonitorInfo(monitor, ref info) != 0;
                var clientWidth = R191ClientWidth;
                var clientHeight = R191ClientHeight;
                var workWidth = info.Work.Right - info.Work.Left;
                var workHeight = info.Work.Bottom - info.Work.Top;
                if (hasWorkArea)
                {
                    var availableWidth = workWidth - extraWidth;
                    var availableHeight = workHeight - extraHeight;
                    if (availableWidth <= 0 || availableHeight <= 0)
                    {
                        ReportR191WindowFailure("monitor work area is too small");

                        return;
                    }

                    var scale = Math.Min(1.0, Math.Min((double)availableWidth / R191ClientWidth,
                        (double)availableHeight / R191ClientHeight));
                    clientWidth = Math.Max(1, (int)Math.Floor(R191ClientWidth * scale));
                    clientHeight = Math.Max(1, (int)Math.Floor(R191ClientHeight * scale));
                    bounds = new R19Rect { Right = clientWidth, Bottom = clientHeight };
                    if (R191AdjustWindowRectExForDpi(ref bounds, style, menu, exStyle, dpi) == 0)
                    {
                        ReportR191WindowFailure("could not adjust fitted client size");

                        return;
                    }
                }

                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                if (width <= 0 || height <= 0)
                {
                    ReportR191WindowFailure("invalid adjusted window size");

                    return;
                }

                // Do not wait for the guest's message thread or change its style,
                // focus, Z order, fullscreen state, or ownership.
                const uint noZOrder = 0x0004;
                const uint noActivate = 0x0010;
                const uint noOwnerZOrder = 0x0200;
                const uint asyncWindowPos = 0x4000;
                const uint noMove = 0x0002;
                var flags = noZOrder | noActivate | noOwnerZOrder | asyncWindowPos;
                var x = hasWorkArea ? info.Work.Left + (workWidth - width) / 2 : 0;
                var y = hasWorkArea ? info.Work.Top + (workHeight - height) / 2 : 0;
                if (!hasWorkArea)
                {
                    flags |= noMove;
                }

                if (R191SetWindowPos(window, 0, x, y, width, height, flags) == 0)
                {
                    ReportR191WindowFailure("SetWindowPos rejected the resize request");

                    return;
                }

                _r191ConfiguredWindow = window;
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-WINDOW-R19.1: REQUESTED client={clientWidth}x{clientHeight}, hwnd=0x{window:X}, dpi={dpi}; asynchronous resize, guest render extent unchanged.");
            }
            catch (Exception exception)
            {
                ReportR191WindowFailure(exception.Message);
            }
        }
    }

    private static void ReportR191WindowFailure(string reason)
    {
        if (Interlocked.Increment(ref _r191WindowWarnings) <= 3)
        {
            Console.Error.WriteLine($"[graphics] IRX-GFX-WINDOW-R19.1: REFUSED: {reason}.");
        }
    }

    private static bool TryPresentR19(nint queue, nint target)
    {
        using var r25Timing = BeginR25Timing(R25Stage.SwapChain);
        if (!EnableR19SwapChain || queue == 0 || !IsResource(target) || R16Writer(target) != queue)
        {
            return false;
        }

        lock (R19PresentGate)
        {
            var resource = (ResourceObject*)target;
            var description = resource->Description;
            if (resource->HostResource == 0 || description.Dimension != 3 || description.Width is 0 or > 8192 ||
                description.Height is 0 or > 8192 || description.DepthOrArraySize != 1 || description.MipLevels != 1 ||
                description.SampleCount != 1 || description.Format is not (28 or 87))
            {
                ReportR19PresentFailure("unsupported single-plane color resource");

                return false;
            }

            try
            {
                var width = checked((uint)description.Width);
                var height = description.Height;
                var presenter = _r19Presenter;
                if (presenter != null && (presenter.Queue != queue
                                          || !presenter.Matches(width, height, description.Format)
                                          || R19IsWindow(presenter.Window) == 0))
                {
                    // The copy-only presenter deliberately refuses a new queue,
                    // format or extent rather than pretending to scale/convert it.
                    ReportR19PresentFailure("presentation queue, extent, format or window changed");

                    return false;
                }

                if (presenter == null)
                {
                    var window = FindR19Window();
                    if (window == 0)
                    {
                        ReportR19PresentFailure(
                            "no unique visible title window; set IRX_PRESENT_WINDOW_CLASS to disambiguate");

                        return false;
                    }

                    presenter = new R19Presenter(window, queue, width, height, description.Format);
                    _r19Presenter = presenter;
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PRESENT-R19: SWAPCHAIN READY hwnd=0x{window:X}, {width}x{height}, format={description.Format}, buffers={R19BufferCount}, flipDiscard=True.");
                }

                // Bounded color-flow checks are enabled during development.
                // Unsampled frames stay on the GPU; disable tracing for timing.
                TraceR171Color(queue, target, "PresentX");
                var started = Stopwatch.GetTimestamp();
                var hr = presenter.Present(resource->HostResource);
                RecordR24Present(hr, Stopwatch.GetTimestamp() - started);
                if (hr < 0)
                {
                    ReportR19PresentFailure($"DXGI Present failed: 0x{hr:X8}");

                    return false;
                }

                var count = Interlocked.Increment(ref _r19PresentCount);
                if (count <= 4 || (count & (count - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PRESENT-R19: ACCEPTED frame #{count}, HRESULT=0x{hr:X8}; GPU copy; content unverified.");
                    if (R19GetClientRect(presenter.Window, out var client) != 0)
                    {
                        Console.Error.WriteLine(
                            $"[graphics] IRX-GFX-WINDOW-R19.1: OBSERVED client={client.Right - client.Left}x{client.Bottom - client.Top}, source={width}x{height}.");
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                ReportR19PresentFailure(exception.Message);

                return false;
            }
        }
    }

    private static void ReportR19PresentFailure(string reason)
    {
        if (Interlocked.Increment(ref _r19PresentWarning) <= 4)
        {
            Console.Error.WriteLine($"[graphics] IRX-GFX-PRESENT-R19: REFUSED: {reason}.");
        }
    }

    private static nint FindR19Window()
    {
        var candidates = new R19WindowCandidates();
        _ = R19EnumWindows(&VisitR19Window, (nint)(&candidates));

        return candidates.Count == 1 ? candidates.Window : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int VisitR19Window(nint window, nint argument)
    {
        try
        {
            _ = R19GetWindowThreadProcessId(window, out var process);
            if (process != (uint)Environment.ProcessId || R19IsWindowVisible(window) == 0 ||
                R19GetWindow(window, 4) != 0 || (R19GetWindowLongPtr(window, -20) & 0x80) != 0
                || R19GetClientRect(window, out var rect) == 0 ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return 1;
            }

            var className = stackalloc char[512];
            var length = R19GetClassName(window, className, 512);
            if (length <= 0)
            {
                return 1;
            }

            var name = new ReadOnlySpan<char>(className, length);
            if (name.StartsWith("IraRationis.GuestPreview", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(R19WindowClass)
                 && !name.Equals(R19WindowClass.AsSpan(), StringComparison.Ordinal)))
            {
                return 1;
            }

            var candidates = (R19WindowCandidates*)argument;
            candidates->Count++;
            candidates->Window = window;

            return 1;
        }
        catch
        {
            return 1;
        }
    }

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2", ExactSpelling = true)]
    private static extern int R19CreateDxgiFactory(uint flags, Guid* iid, nint* factory);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", ExactSpelling = true)]
    private static extern int R19EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint argument);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", ExactSpelling = true)]
    private static extern uint R19GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true)]
    private static extern int R19IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    private static extern int R19IsWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindow", ExactSpelling = true)]
    private static extern nint R19GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern nint R19GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetClientRect", ExactSpelling = true)]
    private static extern int R19GetClientRect(nint window, out R19Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true)]
    private static extern int R19GetClassName(nint window, char* className, int length);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", ExactSpelling = true)]
    private static extern uint R191GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMenu", ExactSpelling = true)]
    private static extern nint R191GetMenu(nint window);

    [DllImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", ExactSpelling = true)]
    private static extern int R191AdjustWindowRectExForDpi(ref R19Rect rect, uint style, int menu, uint exStyle,
        uint dpi);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow", ExactSpelling = true)]
    private static extern nint R191MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern int R191GetMonitorInfo(nint monitor, ref R191MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", ExactSpelling = true)]
    private static extern int R191SetWindowPos(nint window, nint after, int x, int y, int width, int height,
        uint flags);

    private struct R19WindowCandidates
    {
        public nint Window;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct R19SwapDescription
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    private sealed class R19FrameSlot
    {
        public nint Buffer, Allocator, List, Source;
        public ulong FenceValue;
    }

    private sealed class R19Presenter
    {
        private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
        private readonly R19FrameSlot[] _slots = [new(), new(), new()];
        private readonly uint _width, _height, _format;
        private readonly nint _chain;
        private readonly nint _fence;
        private ulong _nextValue;
        private bool _poisoned;

        public R19Presenter(nint window, nint queue, uint width, uint height, uint format)
        {
            Window = window;
            Queue = queue;
            _width = width;
            _height = height;
            _format = format;
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(queue, 1))(queue);
            nint factory = 0, baseChain = 0, chain = 0, fence = 0;
            try
            {
                var factoryId = R19FactoryId;
                CheckHostResult(R19CreateDxgiFactory(0, &factoryId, &factory), "R19 CreateDXGIFactory2");
                var desc = new R19SwapDescription
                {
                    Width = width, Height = height, Format = format, SampleCount = 1,
                    BufferUsage = 0x20, BufferCount = R19BufferCount,
                    Scaling = 0, SwapEffect = 4, AlphaMode = 3
                };
                CheckHostResult(
                    ((delegate* unmanaged[Stdcall]<nint, nint, nint, R19SwapDescription*, void*, nint, nint*, int>)Slot(
                        factory, 15))
                    (factory, queue, window, &desc, null, 0, &baseChain), "R19 CreateSwapChainForHwnd");
                var chainId = R19SwapChainId;
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(baseChain, 0))
                    (baseChain, &chainId, &chain), "R19 IDXGISwapChain3");
                _chain = chain;
                var fenceId = FenceId;
                var device = (nint)_host;
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36))
                    (device, 0, 0, &fenceId, &fence), "R19 frame fence");
                _fence = fence;
                for (var i = 0; i < _slots.Length; i++)
                {
                    var slot = _slots[i];
                    var resourceId = ResourceId;
                    nint buffer = 0;
                    CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(_chain, 9))
                        (_chain, (uint)i, &resourceId, &buffer), "R19 GetBuffer");
                    slot.Buffer = buffer;
                    CreateR16UtilityList(out slot.Allocator, out slot.List);
                    CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot.List, 9))(slot.List),
                        "R19 initial close");
                }
            }
            catch
            {
                // No command list has been submitted during construction.
                foreach (var slot in _slots)
                {
                    ReleaseR16Utility(slot.List, slot.Allocator, slot.Buffer);
                }

                if (_fence != 0)
                {
                    _ = HostRelease(_fence);
                }

                if (_chain != 0)
                {
                    _ = HostRelease(_chain);
                }

                _ = HostRelease(queue);
                _event.Dispose();

                throw;
            }
            finally
            {
                if (baseChain != 0)
                {
                    _ = HostRelease(baseChain);
                }

                if (factory != 0)
                {
                    _ = HostRelease(factory);
                }
            }
        }

        public nint Window
        {
            get;
        }

        public nint Queue
        {
            get;
        }

        public bool Matches(uint width, uint height, uint format)
        {
            return width == _width && height == _height && format == _format;
        }

        private void WaitForSlot(R19FrameSlot slot)
        {
            if (slot.FenceValue == 0)
            {
                return;
            }

            var completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(_fence, 8))(_fence);
            if (completed == ulong.MaxValue)
            {
                throw new InvalidOperationException("R19 GPU removed");
            }

            if (completed < slot.FenceValue)
            {
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(_fence, 9))
                    (_fence, slot.FenceValue, _event.SafeWaitHandle.DangerousGetHandle()), "R19 frame fence event");
                if (!_event.WaitOne(MemoryWaitTimeoutMilliseconds))
                {
                    throw new InvalidOperationException("R19 frame fence timed out");
                }

                completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(_fence, 8))(_fence);
                if (completed == ulong.MaxValue || completed < slot.FenceValue)
                {
                    throw new InvalidOperationException("R19 frame fence completion invalid");
                }
            }

            if (slot.Source != 0)
            {
                _ = HostRelease(slot.Source);
                slot.Source = 0;
            }

            slot.FenceValue = 0;
        }

        public int Present(nint source)
        {
            if (_poisoned)
            {
                return NotImplemented;
            }

            try
            {
                var index = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(_chain, 36))(_chain);
                if (index >= _slots.Length)
                {
                    throw new InvalidOperationException("R19 invalid swap-chain buffer index");
                }

                var slot = _slots[index];
                WaitForSlot(slot);
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot.Allocator, 8))(slot.Allocator),
                    "R19 allocator reset");
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(slot.List, 10))
                    (slot.List, slot.Allocator, 0), "R19 list reset");
                // Guest draw paths currently restore COMMON. PRESENT is also 0.
                TransitionHostResource(slot.List, source, 0, CopySource);
                TransitionHostResource(slot.List, slot.Buffer, 0, CopyDestination);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(slot.List, 17))(slot.List, slot.Buffer,
                    source);
                TransitionHostResource(slot.List, slot.Buffer, CopyDestination, 0);
                TransitionHostResource(slot.List, source, CopySource, 0);
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot.List, 9))(slot.List),
                    "R19 list close");
                // Keep the source alive until the copy completes, even if the
                // guest releases its resource immediately after PresentX.
                _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(source, 1))(source);
                slot.Source = source;
                var list = slot.List;
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(Queue, 10))(Queue, 1, &list);
                var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(_chain, 8))(_chain, 1, 0);
                var value = ++_nextValue;
                CheckHostResult(((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(Queue, 14))
                    (Queue, _fence, value), "R19 frame fence signal");
                slot.FenceValue = value;

                return hr;
            }
            catch
            {
                // A submission may be in flight. Do not reset/release unknown
                // resources or reuse this presenter after a synchronization error.
                _poisoned = true;

                throw;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct R191MonitorInfo
    {
        public uint Size;
        public R19Rect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct R19Rect
    {
        public int Left, Top, Right, Bottom;
    }
}
