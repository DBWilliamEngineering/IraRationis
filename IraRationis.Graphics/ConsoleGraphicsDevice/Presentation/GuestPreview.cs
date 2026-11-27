// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using IraRationis.Graphics.Presentation;
using IraRationis.Graphics.Presentation.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int InputColorObservations = 3;

    private const int LastColorObservation = 64;
    private static readonly Lock GuestPreviewGate = new();
    private static long _lastPreviewTimestamp;
    private static long _previewFrameCount;
    private static int _previewWarningReported;
    private static readonly bool EnableTextureIntermediatePreview =
        Environment.GetEnvironmentVariable("IRX_PREVIEW_COMPLETED_DRAW") == "1";
    private static readonly bool EnableGuestPreviewWindow =
        Environment.GetEnvironmentVariable("IRX_PREVIEW_WINDOW") == "1";
    internal static readonly bool EnableStartupStatus = EnableGuestPreviewWindow &&
                                                        Environment.GetEnvironmentVariable("IRX_PREVIEW_STATUS")
                                                        != "0";
    private static readonly Lock StatusGate = new();
    private static int _compileStarted;
    private static int _compileActive;
    private static int _compileSucceeded;
    private static int _compileFailed;
    private static string _lastProfile = string.Empty;
    private static readonly bool EnableColorFlow =
        Environment.GetEnvironmentVariable("IRX_TRACE_COLOR_FLOW") != "0";
    private static readonly bool EnableInputColorTrace =
        Environment.GetEnvironmentVariable("IRX_R31_INPUT_COLOR_TRACE") == "1";
    private static int _inputColorRequested;
    private static readonly Dictionary<string, int> InputColorSamples = new(StringComparer.Ordinal);
    // Protected by the existing preview gate. No additional capture files are written.
    private static readonly Dictionary<string, int> ColorSamples = new(StringComparer.Ordinal);

    private static void BeginShaderCompilation(string profile)
    {
        if (!EnableStartupStatus)
        {
            return;
        }

        lock (StatusGate)
        {
            _compileStarted++;

            _compileActive++;

            _lastProfile = profile;
        }

        // Do not wait for the UI thread: shader compilation must not depend on a window.
        GuestPreviewWindow.EnsureStarted();
    }

    private static void EndShaderCompilation(bool succeeded)
    {
        if (!EnableStartupStatus)
        {
            return;
        }

        lock (StatusGate)
        {
            _compileActive--;

            if (succeeded)
            {
                _compileSucceeded++;
            }
            else
            {
                _compileFailed++;
            }
        }
    }

    internal static (int Started, int Active, int Succeeded, int Failed, string Profile) StatusSnapshot()
    {
        lock (StatusGate)
        {
            return (_compileStarted, _compileActive, _compileSucceeded, _compileFailed, _lastProfile);
        }
    }

    // Called from the input ABI. Do not touch GPU objects or take graphics locks here.
    public static void RequestInputColorTrace()
    {
        if (EnableInputColorTrace)
        {
            _ = Interlocked.CompareExchange(ref _inputColorRequested, 1, 0);
        }
    }

    private static bool ReserveColorSample(string stage)
    {
        var count = ColorSamples.GetValueOrDefault(stage);

        if (count >= LastColorObservation)
        {
            return false;
        }

        count++;

        ColorSamples[stage] = count;

        // Sample startup and later frame boundaries without an unbounded readback cost.
        return IsColorObservation(count);
    }

    private static bool IsColorObservation(int count)
    {
        return count is > 0 and <= LastColorObservation &&
               (count <= 3 || (count & (count - 1)) == 0);
    }

    private static void TraceColor(nint queue, nint target, string stage, bool cpuUploadCompleted = false)
    {
        if ((!EnableColorFlow && !EnableInputColorTrace) || queue == 0 || !IsResource(target))
        {
            return;
        }

        lock (GuestPreviewGate)
        {
            if (EnableInputColorTrace && Volatile.Read(ref _inputColorRequested) != 0)
            {
                var count = InputColorSamples.GetValueOrDefault(stage);

                if (count >= InputColorObservations)
                {
                    return;
                }

                count++;

                InputColorSamples[stage] = count;

                stage = "R31-after-Y/" + stage;

                ColorSamples[stage] = count;
            }
            else if (!EnableColorFlow || !ReserveColorSample(stage))
            {
                return;
            }

            var writer = GetResourceWriterQueue(target);

            if (writer != queue && !(cpuUploadCompleted && writer == 0))
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={ColorSamples.GetValueOrDefault(stage)}, SKIPPED (source authority or queue mismatch).");

                return;
            }

            try
            {
                // The caller has fenced the producing segment or completed its upload.
                // The supported draw paths restore the color resource to COMMON.
                // A diagnostic readback does not create or update a window.
                _ = ReadTexturePreviewFrame(queue, target, false, stage);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={ColorSamples.GetValueOrDefault(stage)}, readback failed: {exception.Message}");
            }
        }
    }

    private static ColorStats MeasureColor(ReadOnlySpan<byte> data, int width, int height, int pitch)
    {
        var nonBlack = 0;
        var nonZeroAlpha = 0;

        byte maxRgb = 0;

        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;

        for (var coordinateY = 0; coordinateY < height; coordinateY++)
        {
            var row = data.Slice(coordinateY * pitch, width * 4);

            for (var coordinateX = 0; coordinateX < width; coordinateX++)
            {
                var offset = coordinateX * 4;
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

                left = Math.Min(left, coordinateX);

                top = Math.Min(top, coordinateY);

                right = Math.Max(right, coordinateX);

                bottom = Math.Max(bottom, coordinateY);
            }
        }

        var center = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(height / 2 * pitch + width / 2 * 4, 4));

        return new ColorStats(nonBlack, nonZeroAlpha, maxRgb, left, top, right, bottom, center);
    }

    private static void WriteColorStats(string stage, ResourceObject* resource, void* mapped, ulong bytes,
        uint pitch)
    {
        var desc = resource->Description;
        var width = checked((int)desc.Width);
        var height = checked((int)desc.Height);
        var data = new ReadOnlySpan<byte>(mapped, checked((int)bytes));
        var rowPitch = checked((int)pitch);
        var stats = MeasureColor(data, width, height, rowPitch);
        var bounds = stats.NonBlack == 0 ? "none" : $"{stats.Left},{stats.Top}..{stats.Right},{stats.Bottom}";

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-COLOR-R17.2: stage={stage}, observation={ColorSamples.GetValueOrDefault(stage)}, generation={resource->Generation}, guest=0x{resource->Address:X}, size={width}x{height}, format={desc.Format}, nonBlack={stats.NonBlack}/{width * height}, nonZeroAlpha={stats.NonZeroAlpha}, maxRgb={stats.MaxRgb}, bounds={bounds}, centerRaw=0x{stats.Center:X8}.");

        if (EnableInputColorTrace)
        {
            // Exclude row padding: compare only the actual pixel bytes.
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);

            for (var coordinateY = 0; coordinateY < height; coordinateY++)
            {
                hash.AppendData(data.Slice(coordinateY * rowPitch, width * 4));
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset());

            Console.Error.WriteLine(
                $"[graphics] IRX-R31-COLOR: stage={stage}; observation={ColorSamples.GetValueOrDefault(stage)}; generation={resource->Generation}; size={width}x{height}; format={desc.Format}; sha256={digest}; qpc={Stopwatch.GetTimestamp()}");
        }
    }

    private static void VerifyColorStats()
    {
        // Padded rows: the nonzero padding must never count as image content.
        Span<byte> data = stackalloc byte[24];

        data.Clear();

        data[8] = 255;

        data[20] = 255;

        data[3] = 255;

        data[16] = 17;

        data[19] = 255;

        var stats = MeasureColor(data, 2, 2, 12);

        if (stats != new ColorStats(1, 2, 17, 1, 1, 1, 1, 0xFF000011))
        {
            throw new InvalidOperationException("R17.1 color statistics regression failed.");
        }

        int[] expectedObservations = [1, 2, 3, 4, 8, 16, 32, 64];

        if (!Enumerable.Range(1, 65).Where(IsColorObservation).SequenceEqual(expectedObservations) ||
            IsColorObservation(0) || IsColorObservation(-1))
        {
            throw new InvalidOperationException("R17.2 color sampling regression failed.");
        }

        Console.Error.WriteLine(
            "[graphics-probe] R17.2 full-resolution color statistics and sampling passed (CPU only).");

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-COLOR-R17.2: enabled={EnableColorFlow}; observations=1,2,3,4,8,16,32,64; set IRX_TRACE_COLOR_FLOW=0 to disable.");
    }

    private static void PreviewCompletedTextureDraw(nint queue, nint target)
    {
        if (EnableTextureIntermediatePreview && EnableGuestPreviewWindow)
        {
            _ = TryPreviewTextureResource(queue, target, false);
        }
    }

    private static bool TryPreviewTextureResource(nint queue, nint target, bool present)
    {
        if (!EnableGuestPreviewWindow || !IsResource(target) || queue == 0 || GetResourceWriterQueue(target) != queue)
        {
            return false;
        }

        lock (GuestPreviewGate)
        {
            var now = Stopwatch.GetTimestamp();
            // Limit intermediate previews only. PresentX always reads its actual source.
            // This is a readback window, not a production swap chain or A/V clock.
            if (!present && _lastPreviewTimestamp != 0
                         && Stopwatch.GetElapsedTime(_lastPreviewTimestamp, now).TotalMilliseconds < 67)
            {
                return false;
            }

            try
            {
                var frame = ReadTexturePreviewFrame(queue, target, present);

                if (frame == null)
                {
                    return false;
                }

                if (!GuestPreviewWindow.Publish(frame))
                {
                    return false;
                }

                _lastPreviewTimestamp = now;

                if (frame.Id <= 4 || (frame.Id & (frame.Id - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-PREVIEW-R16: QUEUED {frame.Width}x{frame.Height} readback #{frame.Id}; source={(present ? "PresentX" : "intermediate draw")}, sampledNonBlack={frame.NonBlack}.");
                }

                return true;
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _previewWarningReported, 1) == 0)
                {
                    Console.Error.WriteLine($"[graphics] R16 preview failed: {exception.Message}");
                }

                return false;
            }
        }
    }

    private static GuestPreviewFrame? ReadTexturePreviewFrame(nint queue, nint target, bool present,
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
                    $"[graphics] IRX-GFX-COLOR-R17.2: stage={traceStage}, observation={ColorSamples.GetValueOrDefault(traceStage)}, SKIPPED (unsupported resource shape or format {desc.Format}).");
            }

            return null;
        }

        var width = (uint)desc.Width;
        var height = desc.Height;
        var pitch = (width * 4U + 255U) & ~255U;
        var bytes = (ulong)pitch * height;

        if (bytes > TextureMaximumTextureBytes)
        {
            return null;
        }

        nint readback = 0, allocator = 0, list = 0;

        try
        {
            CheckHostResult(CreateHostResource(HostBufferDescription(bytes), 3, CopyDestination, &readback),
                "R16 preview readback allocation");

            CreateUtilityCommandList(out allocator, out list);

            var footprint = new HostTextureTransferFootprint(0, desc.Format, width, height, pitch, 0);

            TransitionHostResource(list, resource->HostResource, 0, CopySource);

            RecordHostTextureTransfer(list, resource->HostResource, readback, bytes, footprint, false);

            TransitionHostResource(list, resource->HostResource, CopySource, 0);

            SubmitUtilityCommandList(queue, list);

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
                    WriteColorStats(traceStage, resource, mapped, bytes, pitch);

                    return null;
                }

                if (EnableColorFlow && present && ReserveColorSample("PresentX"))
                {
                    WriteColorStats("PresentX", resource, mapped, bytes, pitch);
                }

                var scale = Math.Min(1.0, Math.Min(1280.0 / width, 720.0 / height));
                var previewWidth = Math.Max(1, (int)(width * scale));
                var previewHeight = Math.Max(1, (int)(height * scale));
                var pixels = new byte[checked(previewWidth * previewHeight * 4)];
                var nonBlack = 0;

                for (var coordinateY = 0; coordinateY < previewHeight; coordinateY++)
                {
                    var sourceY = (uint)((ulong)coordinateY * height / (uint)previewHeight);
                    var row = (byte*)mapped + (nuint)((ulong)sourceY * pitch);

                    for (var coordinateX = 0; coordinateX < previewWidth; coordinateX++)
                    {
                        var sourceX = (uint)((ulong)coordinateX * width / (uint)previewWidth);
                        var pixel = row + sourceX * 4;
                        var index = (coordinateY * previewWidth + coordinateX) * 4;
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

                return new GuestPreviewFrame(Interlocked.Increment(ref _previewFrameCount), previewWidth, previewHeight,
                    pixels,
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
            ReleaseUtilityCommands(list, allocator, readback);
        }
    }
}
