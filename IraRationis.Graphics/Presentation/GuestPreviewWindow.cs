// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Presentation.Models;

namespace IraRationis.Graphics.Presentation;

// Independent Win32 message pump; no WinForms, WPF, CLR reflection or extra native DLL.
// Own the window class and WM_PAINT lifecycle; the framebuffer remains unmodified.
internal static unsafe class GuestPreviewWindow
{
    private const string PreviewClassName = "IraRationis.GuestPreview.R181";
    private const string PreviewTitle = "IraRationis | Guest GPU Preview";
    private static GuestPreviewFrame? _latest;
    private static int _started;
    private static int _closed;
    private static long _displayed;
    private static int _visibleContentSeen;
    private static int _paintReported;
    private static int _textReported;
    private static int _paintFailureReported;
    private static readonly ManualResetEventSlim Ready = new(false);

    internal static void EnsureStarted()
    {
        if (Volatile.Read(ref _closed) != 0 || Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "IraRationis GPU preview" };

            thread.Start();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _closed, 1);

            Ready.Set();

            Console.Error.WriteLine($"[graphics] R18 preview startup failed: {exception.Message}");
        }
    }

    internal static bool Publish(GuestPreviewFrame frame)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        Volatile.Write(ref _latest, frame);

        EnsureStarted();

        return Ready.Wait(TimeSpan.FromSeconds(2)) && Volatile.Read(ref _closed) == 0;
    }

    private static void Run()
    {
        nint window = 0;

        try
        {
            if (sizeof(PreviewWindowClass) != 80 || sizeof(PreviewPaintStruct) != 72)
            {
                throw new InvalidOperationException("R18.1 Win32 x64 window ABI size mismatch.");
            }

            var instance = GetModuleHandleW(0);

            fixed (char* className = PreviewClassName)
            {
                var windowClass = new PreviewWindowClass
                {
                    Size = (uint)sizeof(PreviewWindowClass),
                    Style = 3, // CS_HREDRAW | CS_VREDRAW
                    Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                    Instance = instance,
                    Cursor = LoadCursorW(0, 32512), // IDC_ARROW
                    ClassName = (nint)className
                };

                if (RegisterClassExW(&windowClass) == 0)
                {
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-STATUS-R18.1: RegisterClassExW failed: Win32 {Marshal.GetLastPInvokeError()}.");

                    return;
                }
            }

            window = CreateWindowExW(0, PreviewClassName, PreviewTitle, 0x10CF0000,
                int.MinValue, int.MinValue, 1280, 760, 0, 0, instance, 0);

            if (window == 0)
            {
                Console.Error.WriteLine(
                    $"[graphics] R16 preview window creation failed: Win32 {Marshal.GetLastPInvokeError()}.");

                return;
            }

            Ready.Set();

            Console.Error.WriteLine(
                "[graphics] IRX-GFX-PREVIEW-R16: WINDOW READY (readback preview, not a swap chain).");

            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-STATUS-R18.1: owned WM_PAINT window ready; startup overlay enabled={ConsoleGraphicsDevice.EnableStartupStatus}.");

            string? previousCaption = null;

            long previousFrame = -1;
            long lastRefresh = 0;

            while (IsWindow(window) != 0)
            {
                while (PeekMessageW(out var message, 0, 0, 0, 1) != 0)
                {
                    if (message.Message == 0x12 || (message.Window == window && message.Message == 0x10))
                    {
                        return;
                    }

                    _ = TranslateMessage(ref message);

                    _ = DispatchMessageW(ref message);
                }

                var frame = Volatile.Read(ref _latest);
                var caption = BuildCaption(frame);
                var captionChanged = !string.Equals(previousCaption, caption, StringComparison.Ordinal);

                if (captionChanged)
                {
                    if (SetWindowTextW(window, caption) == 0)
                    {
                        ReportPaintFailure("SetWindowTextW");
                    }

                    previousCaption = caption;
                }

                var frameId = frame?.Id ?? 0;
                var now = Stopwatch.GetTimestamp();
                // The window procedure also handles OS-initiated repaints and resizing.
                // Refresh status even if no GPU frame has ever arrived.
                if (captionChanged || frameId != previousFrame || lastRefresh == 0 ||
                    Stopwatch.GetElapsedTime(lastRefresh, now).TotalMilliseconds >= 250)
                {
                    if (InvalidateRect(window, 0, 0) == 0)
                    {
                        ReportPaintFailure("InvalidateRect");
                    }

                    _ = UpdateWindow(window);

                    previousFrame = frameId;

                    lastRefresh = now;
                }

                Thread.Sleep(16);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[graphics] R16 preview thread failed: {exception.Message}");
        }
        finally
        {
            if (window != 0 && IsWindow(window) != 0)
            {
                _ = DestroyWindow(window);
            }

            Volatile.Write(ref _closed, 1);

            Volatile.Write(ref _latest, null);

            Ready.Set();
        }
    }

    private static string BuildCaption(GuestPreviewFrame? frame)
    {
        if (!ConsoleGraphicsDevice.EnableStartupStatus || Volatile.Read(ref _visibleContentSeen) != 0)
        {
            return PreviewTitle;
        }

        var status = ConsoleGraphicsDevice.StatusSnapshot();
        var phase = status.Active > 0 ? "Compiling shaders" : "Waiting for visible guest output";

        return
            $"{PreviewTitle} | {phase} | {status.Succeeded} OK / {status.Failed} failed / {status.Active} active | Frame {frame?.Id ?? 0}";
    }

    private static void ReportPaintFailure(string operation)
    {
        if (Interlocked.Exchange(ref _paintFailureReported, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-STATUS-R18.1: {operation} failed (Win32/GDI paint operation).");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == 0x14) // WM_ERASEBKGND: our paint handler owns the background.
            {
                return 1;
            }

            if (message == 0x0F) // WM_PAINT
            {
                var paint = default(PreviewPaintStruct);
                var deviceContext = BeginPaint(window, &paint);

                if (deviceContext == 0)
                {
                    ReportPaintFailure("BeginPaint");

                    return 0;
                }

                try
                {
                    if (Interlocked.Exchange(ref _paintReported, 1) == 0)
                    {
                        Console.Error.WriteLine("[graphics] IRX-GFX-STATUS-R18.1: first WM_PAINT received.");
                    }

                    var frame = Volatile.Read(ref _latest);

                    if (Draw(window, deviceContext, frame) && frame != null &&
                        Interlocked.Exchange(ref _displayed, frame.Id) != frame.Id &&
                        (frame.Id <= 4 || (frame.Id & (frame.Id - 1)) == 0))
                    {
                        Console.Error.WriteLine(
                            $"[graphics] IRX-GFX-PREVIEW-R16: DISPLAYED frame #{frame.Id}, sampledNonBlack={frame.NonBlack}, source={(frame.Present ? "PresentX" : "intermediate draw")}.");
                    }
                }
                finally
                {
                    _ = EndPaint(window, &paint);
                }

                return 0;
            }
        }
        catch (Exception exception)
        {
            // Do not let managed exceptions cross the unmanaged WNDPROC boundary.
            if (Interlocked.Exchange(ref _paintFailureReported, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-STATUS-R18.1: paint handler failed: {exception.Message}");
            }

            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static bool Draw(nint window, nint deviceContext, GuestPreviewFrame? frame)
    {
        if (GetClientRect(window, out var bounds) == 0 || bounds.Right <= bounds.Left
                                                       || bounds.Bottom <= bounds.Top)
        {
            return false;
        }

        var clientWidth = bounds.Right - bounds.Left;
        var clientHeight = bounds.Bottom - bounds.Top;

        if (PatBlt(deviceContext, 0, 0, clientWidth, clientHeight, 0x00000042) == 0)
        {
            ReportPaintFailure("PatBlt");
        }

        var displayed = false;

        if (frame != null)
        {
            var scale = Math.Min((double)clientWidth / frame.Width, (double)clientHeight / frame.Height);
            var width = Math.Max(1, (int)(frame.Width * scale));
            var height = Math.Max(1, (int)(frame.Height * scale));
            var coordinateX = (clientWidth - width) / 2;
            var coordinateY = (clientHeight - height) / 2;
            var info = new PreviewBitmapHeader
            {
                Size = 40,
                Width = frame.Width,
                Height = -frame.Height,
                Planes = 1,
                Bits = 32,
                ImageBytes = (uint)frame.Pixels.Length
            };

            _ = SetStretchBltMode(deviceContext, 3);

            fixed (byte* pixels = frame.Pixels)
            {
                displayed = StretchDIBits(deviceContext, coordinateX, coordinateY, width, height, 0, 0, frame.Width, frame.Height,
                    pixels, &info, 0, 0x00CC0020) > 0;
            }

            if (displayed && frame.NonBlack > 0 && Interlocked.Exchange(ref _visibleContentSeen, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[graphics] IRX-GFX-STATUS-R18: non-black preview observed; startup overlay retired.");
            }
        }

        // Host-only decoration. Never mutate the readback or its statistics.
        // Retire permanently after visible content, including later intentional black frames.
        if (ConsoleGraphicsDevice.EnableStartupStatus && Volatile.Read(ref _visibleContentSeen) == 0 &&
            (frame == null || frame.NonBlack == 0))
        {
            DrawStartupStatus(deviceContext, clientWidth, clientHeight, frame);
        }

        return displayed;
    }

    private static void DrawStartupStatus(nint deviceContext, int width, int height, GuestPreviewFrame? frame)
    {
        var status = ConsoleGraphicsDevice.StatusSnapshot();
        var left = Math.Max(12, width / 12);
        var top = Math.Max(12, height / 3);
        var font = GetStockObject(17); // DEFAULT_GUI_FONT, owned by GDI.
        var previous = font == 0 ? 0 : SelectObject(deviceContext, font);
        var background = SetBkMode(deviceContext, 1); // TRANSPARENT
        var color = SetTextColor(deviceContext, 0x00E8E8E8);

        try
        {
            var heading = status.Active > 0 ? "Compiling shaders..." : "Waiting for visible guest output...";

            WriteStatusLine(deviceContext, left, top, "IRARATIONIS  |  FIRST LIGHT");

            WriteStatusLine(deviceContext, left, top + 30, heading);

            WriteStatusLine(deviceContext, left, top + 56,
                $"Host shader compilations: {status.Succeeded} succeeded / {status.Failed} failed / {status.Active} active");

            WriteStatusLine(deviceContext, left, top + 80,
                $"Attempts: {status.Started}  |  Latest profile: {status.Profile}");

            WriteStatusLine(deviceContext, left, top + 104, frame == null
                ? "No guest frame received yet."
                : $"Preview frame #{frame.Id}: no non-black sampled pixels.");

            WriteStatusLine(deviceContext, left, top + 128, "Guest GPU preview  |  See console for detailed diagnostics.");
        }
        finally
        {
            if (color != uint.MaxValue)
            {
                _ = SetTextColor(deviceContext, color);
            }

            if (background != 0)
            {
                _ = SetBkMode(deviceContext, background);
            }

            if (previous != 0 && previous != -1)
            {
                _ = SelectObject(deviceContext, previous);
            }
        }
    }

    private static void WriteStatusLine(nint deviceContext, int coordinateX, int coordinateY, string text)
    {
        if (TextOutW(deviceContext, coordinateX, coordinateY, text, text.Length) == 0)
        {
            ReportPaintFailure("TextOutW");
        }
        else if (Interlocked.Exchange(ref _textReported, 1) == 0)
        {
            Console.Error.WriteLine("[graphics] IRX-GFX-STATUS-R18.1: first status text accepted by GDI.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int coordinateX, int coordinateY, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int IsWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int PeekMessageW(out PreviewWindowMessage message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int TranslateMessage(ref PreviewWindowMessage message);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(ref PreviewWindowMessage message);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetClientRect(nint window, out PreviewWindowRect rect);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetModuleHandleW(nint name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadCursorW(nint instance, nint name);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(PreviewWindowClass* windowClass);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint BeginPaint(nint window, PreviewPaintStruct* paint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int EndPaint(nint window, PreviewPaintStruct* paint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int InvalidateRect(nint window, nint rect, int erase);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int UpdateWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTextW(nint window, string title);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint GetStockObject(int index);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint deviceContext, nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int SetBkMode(nint deviceContext, int mode);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint SetTextColor(nint deviceContext, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int TextOutW(nint deviceContext, int coordinateX, int coordinateY, string text, int count);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int PatBlt(nint deviceContext, int coordinateX, int coordinateY, int width, int height, uint operation);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int SetStretchBltMode(nint deviceContext, int mode);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int StretchDIBits(nint deviceContext, int coordinateX, int coordinateY, int width,
        int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, void* pixels,
        PreviewBitmapHeader* header, uint usage, uint operation);
}
