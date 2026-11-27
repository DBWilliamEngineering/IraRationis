// SPDX-License-Identifier: GPL-3.0-or-later
// Presentation follows the title-window / DXGI flip-model architecture in
// DexNom/VIctory, victory-gpu/src/host.rs. This implementation uses an
// exact-format copy on the producing queue, not a CPU readback or plane shader.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Presentation;
using IraRationis.Graphics.Presentation.Models;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int WindowClientWidth = 1920;
    private const int WindowClientHeight = 1080;
    internal const int SwapChainBufferCount = 3;
    private static readonly Lock SwapChainPresentGate = new();
    private static readonly string? GuestWindowClassName =
        Environment.GetEnvironmentVariable("IRX_PRESENT_WINDOW_CLASS");
    private static readonly bool EnableSwapChain =
        Environment.GetEnvironmentVariable("IRX_PRESENT_SWAPCHAIN") != "0";
    private static readonly bool EnableWindowed1080 =
        Environment.GetEnvironmentVariable("IRX_PRESENT_WINDOWED_1080") != "0";
    private static nint _configuredWindow;
    private static int _windowWarnings;
    private static SwapChainPresenter? _swapChainPresenter;
    private static int _presentWarning;
    private static long _presentCount;
    internal static readonly Guid FactoryId = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    internal static readonly Guid SwapChainId = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    // Configure the existing title window at its first PresentX, even if
    // no guest render target has yet been verified. Never resize GPU resources
    // or the swap-chain buffers to match the window: DXGI stretches the source.
    private static void ConfigureWindow()
    {
        using var stageTiming = BeginTiming(GraphicsStage.WindowSetup);

        if (!EnableSwapChain || !EnableWindowed1080)
        {
            return;
        }

        lock (SwapChainPresentGate)
        {
            if (_configuredWindow != 0 && IsPresentationWindow(_configuredWindow) != 0)
            {
                return;
            }

            var window = FindWindow();

            if (window == 0)
            {
                return;
            }

            try
            {
                var style = unchecked((uint)(nuint)GetWindowLongPtr(window, -16));
                var exStyle = unchecked((uint)(nuint)GetWindowLongPtr(window, -20));
                var dpi = GetDpiForWindow(window);

                if (dpi == 0)
                {
                    dpi = 96;
                }

                var menu = GetMenu(window) != 0 ? 1 : 0;
                var bounds = new PresentationRect { Right = WindowClientWidth, Bottom = WindowClientHeight };

                if (AdjustWindowRectExForDpi(ref bounds, style, menu, exStyle, dpi) == 0)
                {
                    ReportWindowFailure("could not determine window decorations");

                    return;
                }

                var extraWidth = Math.Max(0, bounds.Right - bounds.Left - WindowClientWidth);
                var extraHeight = Math.Max(0, bounds.Bottom - bounds.Top - WindowClientHeight);
                var info = new PresentationMonitorInfo { Size = (uint)sizeof(PresentationMonitorInfo) };
                var monitor = MonitorFromWindow(window, 2);
                var hasWorkArea = monitor != 0 && GetMonitorInfo(monitor, ref info) != 0;
                var clientWidth = WindowClientWidth;
                var clientHeight = WindowClientHeight;
                var workWidth = info.Work.Right - info.Work.Left;
                var workHeight = info.Work.Bottom - info.Work.Top;

                if (hasWorkArea)
                {
                    var availableWidth = workWidth - extraWidth;
                    var availableHeight = workHeight - extraHeight;

                    if (availableWidth <= 0 || availableHeight <= 0)
                    {
                        ReportWindowFailure("monitor work area is too small");

                        return;
                    }

                    var scale = Math.Min(1.0, Math.Min((double)availableWidth / WindowClientWidth,
                        (double)availableHeight / WindowClientHeight));

                    clientWidth = Math.Max(1, (int)Math.Floor(WindowClientWidth * scale));

                    clientHeight = Math.Max(1, (int)Math.Floor(WindowClientHeight * scale));

                    bounds = new PresentationRect { Right = clientWidth, Bottom = clientHeight };

                    if (AdjustWindowRectExForDpi(ref bounds, style, menu, exStyle, dpi) == 0)
                    {
                        ReportWindowFailure("could not adjust fitted client size");

                        return;
                    }
                }

                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;

                if (width <= 0 || height <= 0)
                {
                    ReportWindowFailure("invalid adjusted window size");

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
                var coordinateX = hasWorkArea ? info.Work.Left + (workWidth - width) / 2 : 0;
                var coordinateY = hasWorkArea ? info.Work.Top + (workHeight - height) / 2 : 0;

                if (!hasWorkArea)
                {
                    flags |= noMove;
                }

                if (SetWindowPos(window, 0, coordinateX, coordinateY, width, height, flags) == 0)
                {
                    ReportWindowFailure("SetWindowPos rejected the resize request");

                    return;
                }

                _configuredWindow = window;

                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-WINDOW-R19.1: REQUESTED client={clientWidth}x{clientHeight}, hwnd=0x{window:X}, dpi={dpi}; asynchronous resize, guest render extent unchanged.");
            }
            catch (Exception exception)
            {
                ReportWindowFailure(exception.Message);
            }
        }
    }

    private static void ReportWindowFailure(string reason)
    {
        if (Interlocked.Increment(ref _windowWarnings) <= 3)
        {
            Console.Error.WriteLine($"[graphics] IRX-GFX-WINDOW-R19.1: REFUSED: {reason}.");
        }
    }

    private static bool TryPresentSwapChain(nint queue, nint target)
    {
        using var stageTiming = BeginTiming(GraphicsStage.SwapChain);

        if (!EnableSwapChain || queue == 0 || !IsResource(target) || GetResourceWriterQueue(target) != queue)
        {
            return false;
        }

        lock (SwapChainPresentGate)
        {
            var resource = (ResourceObject*)target;
            var description = resource->Description;

            if (resource->HostResource == 0 || description.Dimension != 3 || description.Width is 0 or > 8192 ||
                description.Height is 0 or > 8192 || description.DepthOrArraySize != 1 || description.MipLevels != 1 ||
                description.SampleCount != 1 || description.Format is not (28 or 87))
            {
                ReportPresentFailure("unsupported single-plane color resource");

                return false;
            }

            try
            {
                var width = checked((uint)description.Width);
                var height = description.Height;
                var presenter = _swapChainPresenter;

                if (presenter != null && (presenter.Queue != queue
                                          || !presenter.Matches(width, height, description.Format)
                                          || IsPresentationWindow(presenter.Window) == 0))
                {
                    // The copy-only presenter deliberately refuses a new queue,
                    // format or extent rather than pretending to scale/convert it.
                    ReportPresentFailure("presentation queue, extent, format or window changed");

                    return false;
                }

                if (presenter == null)
                {
                    var window = FindWindow();

                    if (window == 0)
                    {
                        ReportPresentFailure(
                            "no unique visible title window; set IRX_PRESENT_WINDOW_CLASS to disambiguate");

                        return false;
                    }

                    presenter = new SwapChainPresenter(window, queue, width, height, description.Format);

                    _swapChainPresenter = presenter;

                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PRESENT-R19: SwapChain READY hwnd=0x{window:X}, {width}x{height}, format={description.Format}, buffers={SwapChainBufferCount}, flipDiscard=True.");
                }

                // Bounded color-flow checks are enabled during development.
                // Unsampled frames stay on the GPU; disable tracing for timing.
                TraceColor(queue, target, "PresentX");

                var started = Stopwatch.GetTimestamp();
                var resultCode = presenter.Present(resource->HostResource);

                RecordPresent(resultCode, Stopwatch.GetTimestamp() - started);

                if (resultCode < 0)
                {
                    ReportPresentFailure($"DXGI Present failed: 0x{resultCode:X8}");

                    return false;
                }

                var count = Interlocked.Increment(ref _presentCount);

                if (count <= 4 || (count & (count - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PRESENT-R19: ACCEPTED frame #{count}, HRESULT=0x{resultCode:X8}; GPU copy; content unverified.");

                    if (GetPresentationClientRect(presenter.Window, out var client) != 0)
                    {
                        Console.Error.WriteLine(
                            $"[graphics] IRX-GFX-WINDOW-R19.1: OBSERVED client={client.Right - client.Left}x{client.Bottom - client.Top}, source={width}x{height}.");
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                ReportPresentFailure(exception.Message);

                return false;
            }
        }
    }

    private static void ReportPresentFailure(string reason)
    {
        if (Interlocked.Increment(ref _presentWarning) <= 4)
        {
            Console.Error.WriteLine($"[graphics] IRX-GFX-PRESENT-R19: REFUSED: {reason}.");
        }
    }

    private static nint FindWindow()
    {
        var candidates = new WindowCandidates();

        _ = EnumWindows(&VisitWindow, (nint)(&candidates));

        return candidates.Count == 1 ? candidates.Window : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int VisitWindow(nint window, nint argument)
    {
        try
        {
            _ = GetWindowThreadProcessId(window, out var process);

            if (process != (uint)Environment.ProcessId || IsWindowVisible(window) == 0 ||
                GetWindow(window, 4) != 0 || (GetWindowLongPtr(window, -20) & 0x80) != 0
                || GetPresentationClientRect(window, out var rect) == 0 ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return 1;
            }

            var className = stackalloc char[512];
            var length = GetClassName(window, className, 512);

            if (length <= 0)
            {
                return 1;
            }

            var name = new ReadOnlySpan<char>(className, length);

            if (name.StartsWith("IraRationis.GuestPreview", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(GuestWindowClassName)
                 && !name.Equals(GuestWindowClassName.AsSpan(), StringComparison.Ordinal)))
            {
                return 1;
            }

            var candidates = (WindowCandidates*)argument;

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
    internal static extern int CreateDxgiFactory(uint flags, Guid* iid, nint* factory);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", ExactSpelling = true)]
    private static extern int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint argument);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", ExactSpelling = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true)]
    private static extern int IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    internal static extern int IsPresentationWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindow", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetClientRect", ExactSpelling = true)]
    private static extern int GetPresentationClientRect(nint window, out PresentationRect rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true)]
    private static extern int GetClassName(nint window, char* className, int length);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMenu", ExactSpelling = true)]
    private static extern nint GetMenu(nint window);

    [DllImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", ExactSpelling = true)]
    private static extern int AdjustWindowRectExForDpi(ref PresentationRect rect, uint style, int menu, uint exStyle,
        uint dpi);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern int GetMonitorInfo(nint monitor, ref PresentationMonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", ExactSpelling = true)]
    private static extern int SetWindowPos(nint window, nint after, int coordinateX, int coordinateY, int width,
        int height,
        uint flags);
}
