// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R31InputColorObservations = 3;

    private const int R172LastColorObservation = 64;
    private static readonly Lock R16PreviewGate = new();
    private static long _r16LastPreview;
    private static long _r16Frames;
    private static int _r16PreviewWarning;
    private static readonly bool EnableR16IntermediatePreview =
        Environment.GetEnvironmentVariable("IRX_PREVIEW_COMPLETED_DRAW") == "1";
    private static readonly bool EnableR16Window = Environment.GetEnvironmentVariable("IRX_PREVIEW_WINDOW") == "1";
    private static readonly bool EnableR18StartupStatus = EnableR16Window &&
                                                          Environment.GetEnvironmentVariable("IRX_PREVIEW_STATUS")
                                                          != "0";
    private static readonly Lock R18StatusGate = new();
    private static int _r18CompileStarted;
    private static int _r18CompileActive;
    private static int _r18CompileSucceeded;
    private static int _r18CompileFailed;
    private static string _r18LastProfile = string.Empty;
    private static readonly bool EnableR171ColorFlow =
        Environment.GetEnvironmentVariable("IRX_TRACE_COLOR_FLOW") != "0";
    private static readonly bool EnableR31InputColorTrace =
        Environment.GetEnvironmentVariable("IRX_R31_INPUT_COLOR_TRACE") == "1";
    private static int _r31InputColorRequested;
    private static readonly Dictionary<string, int> R31InputColorSamples = new(StringComparer.Ordinal);
    // Protected by the existing preview gate. No additional capture files are written.
    private static readonly Dictionary<string, int> R171ColorSamples = new(StringComparer.Ordinal);

    private static void BeginR18ShaderCompilation(string profile)
    {
        if (!EnableR18StartupStatus)
        {
            return;
        }

        lock (R18StatusGate)
        {
            _r18CompileStarted++;

            _r18CompileActive++;

            _r18LastProfile = profile;
        }

        // Do not wait for the UI thread: shader compilation must not depend on a window.
        R16PreviewWindow.EnsureStarted();
    }

    private static void EndR18ShaderCompilation(bool succeeded)
    {
        if (!EnableR18StartupStatus)
        {
            return;
        }

        lock (R18StatusGate)
        {
            _r18CompileActive--;

            if (succeeded)
            {
                _r18CompileSucceeded++;
            }
            else
            {
                _r18CompileFailed++;
            }
        }
    }

    private static (int Started, int Active, int Succeeded, int Failed, string Profile) R18StatusSnapshot()
    {
        lock (R18StatusGate)
        {
            return (_r18CompileStarted, _r18CompileActive, _r18CompileSucceeded, _r18CompileFailed, _r18LastProfile);
        }
    }

    // Called from the input ABI. Do not touch GPU objects or take graphics locks here.
    public static void RequestR31InputColorTrace()
    {
        if (EnableR31InputColorTrace)
        {
            _ = Interlocked.CompareExchange(ref _r31InputColorRequested, 1, 0);
        }
    }

    private static bool ReserveR171ColorSample(string stage)
    {
        var count = R171ColorSamples.GetValueOrDefault(stage);

        if (count >= R172LastColorObservation)
        {
            return false;
        }

        count++;

        R171ColorSamples[stage] = count;

        // Sample startup and later frame boundaries without an unbounded readback cost.
        return IsR172ColorObservation(count);
    }

    private static bool IsR172ColorObservation(int count)
    {
        return count is > 0 and <= R172LastColorObservation &&
               (count <= 3 || (count & (count - 1)) == 0);
    }

    private static void TraceR171Color(nint queue, nint target, string stage, bool cpuUploadCompleted = false)
    {
        if ((!EnableR171ColorFlow && !EnableR31InputColorTrace) || queue == 0 || !IsResource(target))
        {
            return;
        }

        lock (R16PreviewGate)
        {
            if (EnableR31InputColorTrace && Volatile.Read(ref _r31InputColorRequested) != 0)
            {
                var count = R31InputColorSamples.GetValueOrDefault(stage);

                if (count >= R31InputColorObservations)
                {
                    return;
                }

                count++;

                R31InputColorSamples[stage] = count;

                stage = "R31-after-Y/" + stage;

                R171ColorSamples[stage] = count;
            }
            else if (!EnableR171ColorFlow || !ReserveR171ColorSample(stage))
            {
                return;
            }

            var writer = R16Writer(target);

            if (writer != queue && !(cpuUploadCompleted && writer == 0))
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={R171ColorSamples.GetValueOrDefault(stage)}, SKIPPED (source authority or queue mismatch).");

                return;
            }

            try
            {
                // The caller has fenced the producing segment or completed its upload.
                // The supported draw paths restore the color resource to COMMON.
                // A diagnostic readback does not create or update a window.
                _ = ReadR16PreviewFrame(queue, target, false, stage);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={R171ColorSamples.GetValueOrDefault(stage)}, readback failed: {exception.Message}");
            }
        }
    }

    private static R171ColorStats MeasureR171Color(ReadOnlySpan<byte> data, int width, int height, int pitch)
    {
        var nonBlack = 0;
        var nonZeroAlpha = 0;

        byte maxRgb = 0;

        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < height; y++)
        {
            var row = data.Slice(y * pitch, width * 4);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                var maximum = Math.Max(row[offset], Math.Max(row[offset + 1], row[offset + 2]));

                maxRgb = Math.Max(maxRgb, maximum);

                if (row[offset + 3] != 0)
                {
                    nonZeroAlpha++;
                }

                if (maximum == 0)
                {
                    continue;
                }

                nonBlack++;

                left = Math.Min(left, x);

                top = Math.Min(top, y);

                right = Math.Max(right, x);

                bottom = Math.Max(bottom, y);
            }
        }

        var center = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(height / 2 * pitch + width / 2 * 4, 4));

        return new R171ColorStats(nonBlack, nonZeroAlpha, maxRgb, left, top, right, bottom, center);
    }

    private static void WriteR171ColorStats(string stage, ResourceObject* resource, void* mapped, ulong bytes,
        uint pitch)
    {
        var desc = resource->Description;
        var width = checked((int)desc.Width);
        var height = checked((int)desc.Height);
        var data = new ReadOnlySpan<byte>(mapped, checked((int)bytes));
        var rowPitch = checked((int)pitch);
        var stats = MeasureR171Color(data, width, height, rowPitch);
        var bounds = stats.NonBlack == 0 ? "none" : $"{stats.Left},{stats.Top}..{stats.Right},{stats.Bottom}";

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={R171ColorSamples.GetValueOrDefault(stage)}, generation={resource->Generation}, guest=0x{resource->Address:X}, size={width}x{height}, format={desc.Format}, nonBlack={stats.NonBlack}/{width * height}, nonZeroAlpha={stats.NonZeroAlpha}, maxRgb={stats.MaxRgb}, bounds={bounds}, centerRaw=0x{stats.Center:X8}.");

        if (EnableR31InputColorTrace)
        {
            // Exclude row padding: compare only the actual pixel bytes.
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);

            for (var y = 0; y < height; y++)
            {
                hash.AppendData(data.Slice(y * rowPitch, width * 4));
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset());

            Console.Error.WriteLine(
                $"[graphics] IRX-R31-COLOR: stage={stage}; observation={R171ColorSamples.GetValueOrDefault(stage)}; generation={resource->Generation}; size={width}x{height}; format={desc.Format}; sha256={digest}; qpc={Stopwatch.GetTimestamp()}");
        }
    }

    private static void VerifyR171ColorStats()
    {
        // Padded rows: the nonzero padding must never count as image content.
        Span<byte> data = stackalloc byte[24];

        data.Clear();

        data[8] = 255;

        data[20] = 255;

        data[3] = 255;

        data[16] = 17;

        data[19] = 255;

        var stats = MeasureR171Color(data, 2, 2, 12);

        if (stats != new R171ColorStats(1, 2, 17, 1, 1, 1, 1, 0xFF000011))
        {
            throw new InvalidOperationException("R17.1 color statistics regression failed.");
        }

        int[] expectedObservations = [1, 2, 3, 4, 8, 16, 32, 64];

        if (!Enumerable.Range(1, 65).Where(IsR172ColorObservation).SequenceEqual(expectedObservations) ||
            IsR172ColorObservation(0) || IsR172ColorObservation(-1))
        {
            throw new InvalidOperationException("R17.2 color sampling regression failed.");
        }

        Console.Error.WriteLine(
            "[graphics-probe] R17.2 full-resolution color statistics and sampling passed (CPU only).");

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-COLOR-R17.2: enabled={EnableR171ColorFlow}; observations=1,2,3,4,8,16,32,64; set IRX_TRACE_COLOR_FLOW=0 to disable.");
    }

    private static void PreviewCompletedR16Draw(nint queue, nint target)
    {
        if (EnableR16IntermediatePreview && EnableR16Window)
        {
            _ = TryPreviewR16Resource(queue, target, false);
        }
    }

    private static bool TryPreviewR16Resource(nint queue, nint target, bool present)
    {
        if (!EnableR16Window || !IsResource(target) || queue == 0 || R16Writer(target) != queue)
        {
            return false;
        }

        lock (R16PreviewGate)
        {
            var now = Stopwatch.GetTimestamp();
            // Limit intermediate previews only. PresentX always reads its actual source.
            // This is a readback window, not a production swap chain or A/V clock.
            if (!present && _r16LastPreview != 0
                         && Stopwatch.GetElapsedTime(_r16LastPreview, now).TotalMilliseconds < 67)
            {
                return false;
            }

            try
            {
                var frame = ReadR16PreviewFrame(queue, target, present);

                if (frame == null)
                {
                    return false;
                }

                if (!R16PreviewWindow.Publish(frame))
                {
                    return false;
                }

                _r16LastPreview = now;

                if (frame.Id <= 4 || (frame.Id & (frame.Id - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PREVIEW-R16: QUEUED {frame.Width}x{frame.Height} readback #{frame.Id}; source={(present ? "PresentX" : "intermediate draw")}, sampledNonBlack={frame.NonBlack}.");
                }

                return true;
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _r16PreviewWarning, 1) == 0)
                {
                    Console.Error.WriteLine($"[graphics] R16 preview failed: {exception.Message}");
                }

                return false;
            }
        }
    }

    private static R16PreviewFrame? ReadR16PreviewFrame(nint queue, nint target, bool present,
        string? traceStage = null)
    {
        var resource = (ResourceObject*)target;
        var desc = resource->Description;

        if (resource->HostResource == 0 || desc.Dimension != 3 || desc.Width is 0 or > 8192 ||
            desc.Height is 0 or > 8192 || desc.DepthOrArraySize != 1 || desc.MipLevels != 1 || desc.SampleCount != 1 ||
            desc.Format is not (28 or 29 or 87 or 91))
        {
            if (traceStage != null)
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={traceStage}, observation={R171ColorSamples.GetValueOrDefault(traceStage)}, SKIPPED (unsupported resource shape or format {desc.Format}).");
            }

            return null;
        }

        var width = (uint)desc.Width;
        var height = desc.Height;
        var pitch = (width * 4U + 255U) & ~255U;
        var bytes = (ulong)pitch * height;

        if (bytes > R16MaximumTextureBytes)
        {
            return null;
        }

        nint readback = 0, allocator = 0, list = 0;

        try
        {
            CheckHostResult(CreateHostResource(HostBufferDescription(bytes), 3, CopyDestination, &readback),
                "R16 preview readback allocation");

            CreateR16UtilityList(out allocator, out list);

            var from = new ClearProbeCopyLocation { Resource = resource->HostResource };
            var to = new ClearProbeCopyLocation
            {
                Resource = readback,
                Kind = 1,
                Format = desc.Format,
                Width = width,
                Height = height,
                Depth = 1,
                Pitch = pitch
            };

            TransitionHostResource(list, resource->HostResource, 0, CopySource);

            ((delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                    void*, void>)Slot(list, 16))
                (list, &to, 0, 0, 0, &from, null);

            TransitionHostResource(list, resource->HostResource, CopySource, 0);

            SubmitR16UtilityList(queue, list);

            var range = new HostRange { End = (nuint)bytes };

            void* mapped = null;

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8))
                (readback, 0, &range, &mapped), "R16 map preview readback");

            if (mapped == null)
            {
                return null;
            }

            try
            {
                if (traceStage != null)
                {
                    WriteR171ColorStats(traceStage, resource, mapped, bytes, pitch);

                    return null;
                }

                if (EnableR171ColorFlow && present && ReserveR171ColorSample("PresentX"))
                {
                    WriteR171ColorStats("PresentX", resource, mapped, bytes, pitch);
                }

                var scale = Math.Min(1.0, Math.Min(1280.0 / width, 720.0 / height));
                var previewWidth = Math.Max(1, (int)(width * scale));
                var previewHeight = Math.Max(1, (int)(height * scale));
                var pixels = new byte[checked(previewWidth * previewHeight * 4)];
                var nonBlack = 0;

                for (var y = 0; y < previewHeight; y++)
                {
                    var sourceY = (uint)((ulong)y * height / (uint)previewHeight);
                    var row = (byte*)mapped + (nuint)((ulong)sourceY * pitch);

                    for (var x = 0; x < previewWidth; x++)
                    {
                        var sourceX = (uint)((ulong)x * width / (uint)previewWidth);
                        var pixel = row + sourceX * 4;
                        var index = (y * previewWidth + x) * 4;
                        var rgba = desc.Format is 28 or 29;

                        pixels[index] = pixel[rgba ? 2 : 0];

                        pixels[index + 1] = pixel[1];

                        pixels[index + 2] = pixel[rgba ? 0 : 2];

                        pixels[index + 3] = 255;

                        if ((pixels[index] | pixels[index + 1] | pixels[index + 2]) != 0)
                        {
                            nonBlack++;
                        }
                    }
                }

                return new R16PreviewFrame(Interlocked.Increment(ref _r16Frames), previewWidth, previewHeight, pixels,
                    nonBlack, present);
            }
            finally
            {
                var written = default(HostRange);

                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9))(readback, 0, &written);
            }
        }
        finally
        {
            ReleaseR16Utility(list, allocator, readback);
        }
    }

    private readonly record struct R171ColorStats(
        int NonBlack,
        int NonZeroAlpha,
        byte MaxRgb,
        int Left,
        int Top,
        int Right,
        int Bottom,
        uint Center);

    private sealed record R16PreviewFrame(long Id, int Width, int Height, byte[] Pixels, int NonBlack, bool Present);

    // Independent Win32 message pump; no WinForms, WPF, CLR reflection or extra native DLL.
    // Own the window class and WM_PAINT lifecycle; the framebuffer remains unmodified.
    private static class R16PreviewWindow
    {
        private const string PreviewClassName = "IraRationis.GuestPreview.R181";
        private const string PreviewTitle = "IraRationis | Guest GPU Preview";
        private static R16PreviewFrame? _latest;
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

        internal static bool Publish(R16PreviewFrame frame)
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
                    $"[graphics] IRX-GFX-STATUS-R18.1: owned WM_PAINT window ready; startup overlay enabled={EnableR18StartupStatus}.");

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

        private static string BuildCaption(R16PreviewFrame? frame)
        {
            if (!EnableR18StartupStatus || Volatile.Read(ref _visibleContentSeen) != 0)
            {
                return PreviewTitle;
            }

            var status = R18StatusSnapshot();
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
                    var dc = BeginPaint(window, &paint);

                    if (dc == 0)
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

                        if (Draw(window, dc, frame) && frame != null &&
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

        private static bool Draw(nint window, nint dc, R16PreviewFrame? frame)
        {
            if (GetClientRect(window, out var bounds) == 0 || bounds.Right <= bounds.Left
                                                           || bounds.Bottom <= bounds.Top)
            {
                return false;
            }

            var clientWidth = bounds.Right - bounds.Left;
            var clientHeight = bounds.Bottom - bounds.Top;

            if (PatBlt(dc, 0, 0, clientWidth, clientHeight, 0x00000042) == 0)
            {
                ReportPaintFailure("PatBlt");
            }

            var displayed = false;

            if (frame != null)
            {
                var scale = Math.Min((double)clientWidth / frame.Width, (double)clientHeight / frame.Height);
                var width = Math.Max(1, (int)(frame.Width * scale));
                var height = Math.Max(1, (int)(frame.Height * scale));
                var x = (clientWidth - width) / 2;
                var y = (clientHeight - height) / 2;
                var info = new BitmapHeader
                {
                    Size = 40,
                    Width = frame.Width,
                    Height = -frame.Height,
                    Planes = 1,
                    Bits = 32,
                    ImageBytes = (uint)frame.Pixels.Length
                };

                _ = SetStretchBltMode(dc, 3);

                fixed (byte* pixels = frame.Pixels)
                {
                    displayed = StretchDIBits(dc, x, y, width, height, 0, 0, frame.Width, frame.Height,
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
            if (EnableR18StartupStatus && Volatile.Read(ref _visibleContentSeen) == 0 &&
                (frame == null || frame.NonBlack == 0))
            {
                DrawStartupStatus(dc, clientWidth, clientHeight, frame);
            }

            return displayed;
        }

        private static void DrawStartupStatus(nint dc, int width, int height, R16PreviewFrame? frame)
        {
            var status = R18StatusSnapshot();
            var left = Math.Max(12, width / 12);
            var top = Math.Max(12, height / 3);
            var font = GetStockObject(17); // DEFAULT_GUI_FONT, owned by GDI.
            var previous = font == 0 ? 0 : SelectObject(dc, font);
            var background = SetBkMode(dc, 1); // TRANSPARENT
            var color = SetTextColor(dc, 0x00E8E8E8);

            try
            {
                var heading = status.Active > 0 ? "Compiling shaders..." : "Waiting for visible guest output...";

                WriteStatusLine(dc, left, top, "IRARATIONIS  |  FIRST LIGHT");

                WriteStatusLine(dc, left, top + 30, heading);

                WriteStatusLine(dc, left, top + 56,
                    $"Host shader compilations: {status.Succeeded} succeeded / {status.Failed} failed / {status.Active} active");

                WriteStatusLine(dc, left, top + 80,
                    $"Attempts: {status.Started}  |  Latest profile: {status.Profile}");

                WriteStatusLine(dc, left, top + 104, frame == null
                    ? "No guest frame received yet."
                    : $"Preview frame #{frame.Id}: no non-black sampled pixels.");

                WriteStatusLine(dc, left, top + 128, "Guest GPU preview  |  See console for detailed diagnostics.");
            }
            finally
            {
                if (color != uint.MaxValue)
                {
                    _ = SetTextColor(dc, color);
                }

                if (background != 0)
                {
                    _ = SetBkMode(dc, background);
                }

                if (previous != 0 && previous != -1)
                {
                    _ = SelectObject(dc, previous);
                }
            }
        }

        private static void WriteStatusLine(nint dc, int x, int y, string text)
        {
            if (TextOutW(dc, x, y, text, text.Length) == 0)
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
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int IsWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int DestroyWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int PeekMessageW(out WindowMessage message, nint window, uint min, uint max, uint remove);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int TranslateMessage(ref WindowMessage message);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint DispatchMessageW(ref WindowMessage message);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetClientRect(nint window, out WindowRect rect);

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
        private static extern nint SelectObject(nint dc, nint value);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int SetBkMode(nint dc, int mode);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern uint SetTextColor(nint dc, uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int TextOutW(nint dc, int x, int y, string text, int count);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int PatBlt(nint dc, int x, int y, int width, int height, uint operation);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int SetStretchBltMode(nint dc, int mode);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int StretchDIBits(nint dc, int x, int y, int width,
            int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, void* pixels,
            BitmapHeader* header, uint usage, uint operation);

        [StructLayout(LayoutKind.Sequential)]
        private struct PreviewWindowClass
        {
            public uint Size, Style;
            public nint Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PreviewPaintStruct
        {
            public nint Dc;
            public int Erase;
            public WindowRect Paint;
            public int Restore, IncrementalUpdate;
            public fixed byte Reserved[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowMessage
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, Bits;
            public uint Compression, ImageBytes;
            public int XPerMeter, YPerMeter;
            public uint ColorsUsed, Important;
        }
    }
}
