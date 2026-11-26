// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace IraRationis.Media;

/// <summary>
///     Bounded in-process Windows decoder for independently supplied local MP4 media.
///     Returns the existing playback contract without launching a decoder process.
/// </summary>
public static unsafe partial class MediaFoundationDecoder
{
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint VideoStream = 0xFFFFFFFC;
    private const uint AudioStream = 0xFFFFFFFD;
    private const uint EndOfStream = 0x2;
    private const uint NativeMediaTypeChanged = 0x10;
    private const uint CurrentMediaTypeChanged = 0x20;
    private const uint FormatChangeFlags = NativeMediaTypeChanged | CurrentMediaTypeChanged;
    private const uint SupportedFlags = EndOfStream | FormatChangeFlags;
    private const int MaxConsecutiveFormatEvents = 32;
    private const uint FramesPerSecond = 30;
    private const long TicksPerSecond = 10_000_000;
    private const int MaxVideoBytes = 128 * 1024 * 1024;
    private const int MaxAudioBytes = 48000 * 2 * 2 * 5;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateAttribute = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid Buffer2D = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    public static DecodedClip Decode(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Media Foundation requires Windows.");
        }

        var path = Path.GetFullPath(inputPath);
        var root = Path.GetPathRoot(path);

        if (root is null || root.Length != 3 || root[1] != ':' ||
            !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) ||
            !File.Exists(path))
        {
            throw new ArgumentException("Select a local MP4 file on a local drive.", nameof(inputPath));
        }

        Check(CoInitializeEx(0, 0), "CoInitializeEx");

        try
        {
            Check(MFStartup(0x00020070, 0), "MFStartup");

            try
            {
                var video = ReadVideo(path);
                var audio = ReadAudio(path);
                var pcm = AlignAudio(video, audio);

                Console.Error.WriteLine(
                    $"[media] IRX-R39.1: Media Foundation decoded {video.Frames.Length / video.FrameBytes} frames; " +
                    $"size={video.Width}x{video.Height}; PCM samples={pcm.Length}.");

                return new DecodedClip(video.Width, video.Height, video.Frames, pcm, 48000, 2);
            }
            finally
            {
                Check(MFShutdown(), "MFShutdown");
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    // Keep the video timeline authoritative for the existing constant-rate player.
    // Only small edge corrections are supported; never conceal gaps inside a track.
    private static short[] AlignAudio(VideoPayload video, AudioPayload audio)
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const int maxCorrectionFrames = sampleRate / 10;

        var videoFrames = video.Frames.Length / video.FrameBytes;
        var targetFrames = checked(videoFrames * (sampleRate / (int)FramesPerSecond));
        var audioFrames = audio.Samples.Length / channels;
        var startDeltaTicks = (decimal)audio.Start - video.Start;
        var startDeltaFrames = decimal.Round(startDeltaTicks * sampleRate / TicksPerSecond,
            0, MidpointRounding.AwayFromZero);
        var endDeltaFrames = startDeltaFrames + audioFrames - targetFrames;

        Console.Error.WriteLine(
            $"[media] IRX-R39.1: timeline videoStart={video.Start}; audioStart={audio.Start}; " +
            $"videoFrames={videoFrames}; audioFrames={audioFrames}; targetAudioFrames={targetFrames}; " +
            $"startDeltaFrames={startDeltaFrames}; endDeltaFrames={endDeltaFrames} (48 kHz frames).");

        if (targetFrames <= 0 || audioFrames <= 0 || audio.Samples.Length % channels != 0 ||
            Math.Abs(startDeltaFrames) > maxCorrectionFrames || Math.Abs(endDeltaFrames) > maxCorrectionFrames)
        {
            throw new InvalidDataException(
                "Audio/video alignment exceeds the bounded 100 ms edge-correction contract.");
        }

        var offset = (int)startDeltaFrames;
        var sourceStart = Math.Max(0, -offset);
        var destinationStart = Math.Max(0, offset);
        var copyFrames = Math.Min(audioFrames - sourceStart, targetFrames - destinationStart);

        if (copyFrames <= 0)
        {
            throw new InvalidDataException("Audio and video timelines do not overlap.");
        }

        if (offset == 0 && audioFrames == targetFrames)
        {
            return audio.Samples;
        }

        var aligned = new short[checked(targetFrames * channels)];

        audio.Samples.AsSpan(sourceStart * channels, copyFrames * channels)
            .CopyTo(aligned.AsSpan(destinationStart * channels));

        var tailTrim = audioFrames - sourceStart - copyFrames;
        var tailPad = targetFrames - destinationStart - copyFrames;

        Console.Error.WriteLine(
            $"[media] IRX-R39.1: alignment headTrim={sourceStart}; headPad={destinationStart}; " +
            $"tailTrim={tailTrim}; tailPad={tailPad}; copied={copyFrames} (48 kHz frames).");

        return aligned;
    }

    private static VideoPayload ReadVideo(string path)
    {
        var reader = Open(path, VideoStream, VideoType, Nv12);

        try
        {
            // The output type is provisional until the first decoded sample.
            var provisionalType = CurrentType(reader, VideoStream);

            try
            {
                var provisionalSize = GetUInt64(provisionalType, FrameSize);

                Console.Error.WriteLine(
                    $"[media] IRX-R39: provisional video {provisionalSize >> 32}x{(uint)provisionalSize}.");
            }
            finally
            {
                Release(provisionalType);
            }

            var layout = default(VideoLayout);
            var frame = Array.Empty<byte>();
            var cropped = Array.Empty<byte>();

            using var output = new MemoryStream();

            var count = 0;
            var start = 0L;

            while (true)
            {
                var sample = ReadSample(reader, VideoStream, out var timestamp, out var end, out var formatFlags);

                if (sample == 0)
                {
                    if (end)
                    {
                        break;
                    }

                    throw new InvalidDataException("Video sample is missing.");
                }

                try
                {
                    if (count == 0 || formatFlags != 0)
                    {
                        var current = ValidateVideoType(reader);

                        if (count == 0)
                        {
                            layout = current;

                            frame = new byte[layout.CodedBytes];

                            cropped = layout.IsFullFrame ? Array.Empty<byte>() : new byte[layout.VisibleBytes];

                            start = timestamp;

                            Console.Error.WriteLine(
                                $"[media] IRX-R39: first sample negotiated {layout.CodedWidth}x{layout.CodedHeight}; " +
                                $"display={layout.Width}x{layout.Height}; origin={layout.X},{layout.Y}; flags=0x{formatFlags:X}.");
                        }
                        else if (current != layout)
                        {
                            throw new InvalidDataException(
                                $"Video layout changed after {count} frames: {layout} -> {current}.");
                        }
                        else
                        {
                            Console.Error.WriteLine(
                                $"[media] IRX-R39: compatible video format notification 0x{formatFlags:X}.");
                        }
                    }

                    var expected = (double)start + count * TicksPerSecond / FramesPerSecond;

                    if (count >= 150 || output.Length + layout.VisibleBytes > MaxVideoBytes ||
                        Math.Abs(timestamp - expected) > 166667)
                    {
                        throw new InvalidDataException("Video exceeds the bounded constant-rate playback contract.");
                    }

                    CopyFrame(sample, frame);

                    if (layout.IsFullFrame)
                    {
                        output.Write(frame);
                    }
                    else
                    {
                        CopyVisibleFrame(frame, layout, cropped);

                        output.Write(cropped);
                    }

                    count++;
                }
                finally
                {
                    Release(sample);
                }

                if (end)
                {
                    break;
                }
            }

            if (count == 0)
            {
                throw new InvalidDataException("No video frames decoded.");
            }

            return new VideoPayload(layout.Width, layout.Height, layout.VisibleBytes, start, output.ToArray());
        }
        finally
        {
            Release(reader);
        }
    }

    private static AudioPayload ReadAudio(string path)
    {
        var reader = Open(path, AudioStream, AudioType, Pcm);

        try
        {
            using var output = new MemoryStream();

            var start = 0L;
            var count = 0;

            while (true)
            {
                var sample = ReadSample(reader, AudioStream, out var timestamp, out var end, out var formatFlags);

                if (sample == 0)
                {
                    if (end)
                    {
                        break;
                    }

                    throw new InvalidDataException("Audio sample is missing.");
                }

                try
                {
                    if (count == 0 || formatFlags != 0)
                    {
                        ValidateAudioType(reader);

                        if (count == 0)
                        {
                            start = timestamp;
                        }

                        if (formatFlags != 0)
                        {
                            Console.Error.WriteLine(
                                $"[media] IRX-R39: compatible audio format notification 0x{formatFlags:X}.");
                        }
                    }

                    var expected = (double)start + output.Length / 4 * TicksPerSecond / 48000;

                    if (Math.Abs(timestamp - expected) > 333334 || count >= 10000)
                    {
                        throw new InvalidDataException("Audio timeline is discontinuous.");
                    }

                    CopyAudio(sample, output);

                    count++;
                }
                finally
                {
                    Release(sample);
                }

                if (end)
                {
                    break;
                }
            }

            if (output.Length == 0)
            {
                throw new InvalidDataException("No PCM audio decoded.");
            }

            var bytes = output.ToArray();
            var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan()).ToArray();

            return new AudioPayload(start, pcm);
        }
        finally
        {
            Release(reader);
        }
    }

    private static nint Open(string path, uint stream, Guid major, Guid subtype)
    {
        var byteStream = nint.Zero;
        var reader = nint.Zero;
        var type = nint.Zero;

        try
        {
            Check(MFCreateFile(1, 0, 0, path, out byteStream), "MFCreateFile");

            Check(MFCreateSourceReaderFromByteStream(byteStream, 0, out reader), "MFCreateSourceReaderFromByteStream");

            var select = (delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4);

            Check(select(reader, AllStreams, 0), "Disable streams");

            Check(select(reader, stream, 1), "Select stream");

            Check(MFCreateMediaType(out type), "MFCreateMediaType");

            SetGuid(type, MajorType, major);

            SetGuid(type, Subtype, subtype);

            if (stream == AudioStream)
            {
                SetUInt32(type, Channels, 2);

                SetUInt32(type, SampleRate, 48000);

                SetUInt32(type, BitsPerSample, 16);

                SetUInt32(type, BlockAlignment, 4);

                SetUInt32(type, AverageBytesPerSecond, 192000);
            }

            var set = (delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(reader, 7);

            Check(set(reader, stream, null, type), "SetCurrentMediaType");

            var result = reader;

            reader = 0;

            return result;
        }
        finally
        {
            Release(type);

            Release(reader);

            Release(byteStream);
        }
    }

    private static nint CurrentType(nint reader, uint stream)
    {
        var type = nint.Zero;
        var get = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(reader, 6);

        Check(get(reader, stream, &type), "GetCurrentMediaType");

        return type;
    }

    private static VideoLayout ValidateVideoType(nint reader)
    {
        var type = CurrentType(reader, VideoStream);

        try
        {
            RequireGuid(type, MajorType, VideoType);

            RequireGuid(type, Subtype, Nv12);

            var size = GetUInt64(type, FrameSize);
            var width = checked((int)(size >> 32));
            var height = checked((int)(size & uint.MaxValue));
            var frameRateRatio = GetUInt64(type, FrameRateAttribute);

            var numerator = (uint)(frameRateRatio >> 32);
            var denominator = (uint)(frameRateRatio & uint.MaxValue);

            if (width is < 2 or > 1920 || height is < 2 or > 1088 ||
                (width & 1) != 0 || (height & 1) != 0 || denominator == 0 ||
                numerator != (ulong)FramesPerSecond * denominator)
            {
                throw new InvalidDataException(
                    "Unsupported coded dimensions or frame rate; expected even NV12 up to 1920x1088 at 30 FPS.");
            }

            // Prefer the declared valid image area. Never infer a crop from padding alone.
            if (!TryGetAperture(type, MinimumDisplayAperture, out var area) &&
                !TryGetAperture(type, GeometricAperture, out area))
            {
                area = (0, 0, width, height);
            }

            var layout = new VideoLayout(width, height, area.X, area.Y, area.Width, area.Height);

            if (layout.X < 0 || layout.Y < 0 || layout.Width is < 2 or > 1920 ||
                layout.Height is < 2 or > 1080 || (layout.X & 1) != 0 || (layout.Y & 1) != 0 ||
                (layout.Width & 1) != 0 || (layout.Height & 1) != 0 ||
                (long)layout.X + layout.Width > width || (long)layout.Y + layout.Height > height)
            {
                throw new InvalidDataException($"Unsupported NV12 display aperture: {layout}.");
            }

            return layout;
        }
        finally
        {
            Release(type);
        }
    }

    private static void ValidateAudioType(nint reader)
    {
        var type = CurrentType(reader, AudioStream);

        try
        {
            RequireGuid(type, MajorType, AudioType);

            RequireGuid(type, Subtype, Pcm);

            if (GetUInt32(type, Channels) != 2 || GetUInt32(type, SampleRate) != 48000 ||
                GetUInt32(type, BitsPerSample) != 16 || GetUInt32(type, BlockAlignment) != 4)
            {
                throw new InvalidDataException("The player requires stereo 48 kHz PCM16 audio.");
            }
        }
        finally
        {
            Release(type);
        }
    }

    private static nint ReadSample(nint reader, uint stream, out long timestamp, out bool end,
        out uint formatFlags)
    {
        var read = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9);

        formatFlags = 0;

        for (var attempt = 0; attempt < MaxConsecutiveFormatEvents; attempt++)
        {
            var sample = nint.Zero;
            var flags = 0U;
            var actual = 0U;
            var time = 0L;

            try
            {
                var hr = read(reader, stream, 0, &actual, &flags, &time, &sample);

                Check(hr, "ReadSample");

                if ((flags & ~SupportedFlags) != 0)
                {
                    throw new InvalidDataException($"Unsupported source-reader flags: 0x{flags:X}.");
                }

                // Retain notifications across sample-free reads. The caller inspects
                // the current type when a sample actually arrives.
                formatFlags |= flags & FormatChangeFlags;

                timestamp = time;

                end = (flags & EndOfStream) != 0;

                if (sample != 0 || end)
                {
                    var result = sample;

                    sample = 0;

                    return result;
                }

                if ((flags & FormatChangeFlags) == 0)
                {
                    throw new InvalidDataException("Source reader returned no sample or supported event.");
                }
            }
            finally
            {
                Release(sample);
            }
        }

        throw new InvalidDataException("Too many consecutive source-reader format notifications without a sample.");
    }

    private static bool TryGetAperture(nint type, Guid key, out (int X, int Y, int Width, int Height) area)
    {
        // MFVideoArea: two MFOffset pairs (WORD fraction, SHORT value), then SIZE.
        var data = stackalloc byte[16];
        var length = 0U;
        var get = (delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, uint*, int>)Slot(type, 15);
        var hr = get(type, &key, data, 16, &length);

        if (hr == unchecked((int)0xC00D36E6))
        {
            area = default;

            return false;
        }

        Check(hr, "GetBlob(display aperture)");

        if (length != 16)
        {
            throw new InvalidDataException("Unexpected MFVideoArea size.");
        }

        var bytes = new ReadOnlySpan<byte>(data, 16);

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 0)
        {
            throw new InvalidDataException("Fractional NV12 display apertures are not supported.");
        }

        area = (BinaryPrimitives.ReadInt16LittleEndian(bytes[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]));

        return true;
    }

    private static void CopyVisibleFrame(ReadOnlySpan<byte> source, VideoLayout layout, Span<byte> destination)
    {
        // The validated layout is even in both axes, preserving NV12 chroma pairs.
        var sourceChroma = checked(layout.CodedWidth * layout.CodedHeight);
        var destinationChroma = checked(layout.Width * layout.Height);

        for (var row = 0; row < layout.Height; row++)
        {
            var offset = checked((layout.Y + row) * layout.CodedWidth + layout.X);

            source.Slice(offset, layout.Width).CopyTo(destination.Slice(row * layout.Width, layout.Width));
        }

        for (var row = 0; row < layout.Height / 2; row++)
        {
            var offset = checked(sourceChroma + (layout.Y / 2 + row) * layout.CodedWidth + layout.X);

            source.Slice(offset, layout.Width).CopyTo(
                destination.Slice(destinationChroma + row * layout.Width, layout.Width));
        }
    }

    private static nint ContiguousBuffer(nint sample)
    {
        var buffer = nint.Zero;
        var convert = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41);

        Check(convert(sample, &buffer), "ConvertToContiguousBuffer");

        return buffer;
    }

    private static void CopyFrame(nint sample, byte[] destination)
    {
        var buffer = ContiguousBuffer(sample);
        var plane = nint.Zero;

        try
        {
            var iid = Buffer2D;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(buffer, 0);
            var hr = query(buffer, &iid, &plane);

            if (hr >= 0)
            {
                var length = 0U;
                var get = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(plane, 7);

                Check(get(plane, &length), "GetContiguousLength");

                if (length != destination.Length)
                {
                    throw new InvalidDataException(
                        $"Unexpected contiguous NV12 extent: expected {destination.Length}, received {length}.");
                }

                var copy = (delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)Slot(plane, 8);

                fixed (byte* target = destination)
                {
                    Check(copy(plane, target, length), "ContiguousCopyTo");
                }
            }
            else
            {
                if (hr != unchecked((int)0x80004002))
                {
                    Check(hr, "QueryInterface(IMF2DBuffer)");
                }

                var data = Lock(buffer, out var length);

                try
                {
                    if (length != destination.Length)
                    {
                        throw new InvalidDataException(
                            $"Unexpected linear NV12 extent: expected {destination.Length}, received {length}.");
                    }

                    new ReadOnlySpan<byte>(data, length).CopyTo(destination);
                }
                finally
                {
                    Unlock(buffer);
                }
            }
        }
        finally
        {
            Release(plane);

            Release(buffer);
        }
    }

    private static void CopyAudio(nint sample, MemoryStream output)
    {
        var buffer = ContiguousBuffer(sample);

        try
        {
            var data = Lock(buffer, out var length);

            try
            {
                if (length == 0 || length % 4 != 0 || output.Length + length > MaxAudioBytes)
                {
                    throw new InvalidDataException("PCM payload exceeds its bounds or is misaligned.");
                }

                output.Write(new ReadOnlySpan<byte>(data, length));
            }
            finally
            {
                Unlock(buffer);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    private static byte* Lock(nint buffer, out int length)
    {
        var data = (byte*)null;
        var maximum = 0U;
        var current = 0U;
        var call = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3);

        Check(call(buffer, &data, &maximum, &current), "IMFMediaBuffer.Lock");

        if (data == null || current > maximum || current > int.MaxValue)
        {
            Unlock(buffer);

            throw new InvalidDataException("Invalid native media buffer extent.");
        }

        length = (int)current;

        return data;
    }

    private static void Unlock(nint buffer)
    {
        var call = (delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4);

        Check(call(buffer), "IMFMediaBuffer.Unlock");
    }

    private static uint GetUInt32(nint type, Guid key)
    {
        var value = 0U;
        var get = (delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(type, 7);

        Check(get(type, &key, &value), "GetUINT32");

        return value;
    }

    private static ulong GetUInt64(nint type, Guid key)
    {
        var value = 0UL;
        var get = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(type, 8);

        Check(get(type, &key, &value), "GetUINT64");

        return value;
    }

    private static void RequireGuid(nint type, Guid key, Guid expected)
    {
        var value = Guid.Empty;
        var get = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 10);

        Check(get(type, &key, &value), "GetGUID");

        if (value != expected)
        {
            throw new InvalidDataException("Unexpected negotiated media format.");
        }
    }

    private static void SetGuid(nint type, Guid key, Guid value)
    {
        var set = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 24);

        Check(set(type, &key, &value), "SetGUID");
    }

    private static void SetUInt32(nint type, Guid key, uint value)
    {
        var set = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(type, 21);

        Check(set(type, &key, value), "SetUINT32");
    }

    private static nint Slot(nint instance, int index)
    {
        if (instance == 0)
        {
            throw new InvalidOperationException("Media Foundation returned a null interface.");
        }

        return (*(nint**)instance)[index];
    }

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2);

            _ = release(instance);
        }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{operation} failed: 0x{hr:X8}.");
        }
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoInitializeEx(nint reserved, uint mode);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CoUninitialize();

    [LibraryImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MFCreateFile(uint access, uint openMode, uint flags, string path, out nint stream);

    [LibraryImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfreadwrite.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MFCreateSourceReaderFromByteStream(nint stream, nint attributes, out nint reader);

    private readonly record struct VideoLayout(int CodedWidth, int CodedHeight, int X, int Y, int Width, int Height)
    {
        public int CodedBytes
        {
            get
            {
                return checked(CodedWidth * CodedHeight * 3 / 2);
            }
        }

        public int VisibleBytes
        {
            get
            {
                return checked(Width * Height * 3 / 2);
            }
        }

        public bool IsFullFrame
        {
            get
            {
                return X == 0 && Y == 0 && Width == CodedWidth && Height == CodedHeight;
            }
        }
    }

    private sealed record VideoPayload(int Width, int Height, int FrameBytes, long Start, byte[] Frames);

    private sealed record AudioPayload(long Start, short[] Samples);
}
