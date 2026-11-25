// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media;

/// <summary>
/// Bounded in-process Windows decoder for independently supplied local MP4 media.
/// Returns the existing playback contract without launching a decoder process.
/// </summary>
public static unsafe partial class MediaFoundationDecoder
{
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint VideoStream = 0xFFFFFFFC;
    private const uint AudioStream = 0xFFFFFFFD;
    private const uint EndOfStream = 0x2;
    private const uint UnsupportedFlags = 0x1 | 0x4 | 0x10 | 0x20 | 0x100;
    private const int MaxVideoBytes = 128 * 1024 * 1024;
    private const int MaxAudioBytes = 48000 * 2 * 2 * 5;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
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
                var duration = audio.Samples.Length / 96000.0;

                if (Math.Abs((double)video.Start - audio.Start) > 333334 ||
                    Math.Abs(duration - video.Frames.Length / (double)video.FrameBytes / 30) > 1.0 / 30.0)
                {
                    throw new InvalidDataException("The bounded player requires aligned audio and video tracks.");
                }

                return new DecodedClip(video.Width, video.Height, video.Frames, audio.Samples, 48000, 2);
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

    private static VideoPayload ReadVideo(string path)
    {
        var reader = Open(path, VideoStream, VideoType, Nv12);

        try
        {
            var type = CurrentType(reader, VideoStream);
            var width = 0;
            var height = 0;

            try
            {
                RequireGuid(type, MajorType, VideoType);
                RequireGuid(type, Subtype, Nv12);

                var size = GetUInt64(type, FrameSize);
                width = checked((int)(size >> 32));
                height = checked((int)(size & uint.MaxValue));
                var rate = GetUInt64(type, FrameRate);
                var denominator = (uint)rate;

                if (width is < 2 or > 1920 || height is < 2 or > 1080 ||
                    (width & 1) != 0 || (height & 1) != 0 || denominator == 0 ||
                    (rate >> 32) != 30UL * denominator)
                {
                    throw new InvalidDataException("Only even-sized constant 30 FPS video up to 1080p is supported.");
                }
            }
            finally
            {
                Release(type);
            }

            var frameBytes = checked(width * height * 3 / 2);
            var frame = new byte[frameBytes];
            using var output = new MemoryStream();
            var count = 0;
            var start = 0L;

            while (true)
            {
                var sample = ReadSample(reader, VideoStream, out var timestamp, out var end);

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
                    if (count == 0)
                    {
                        start = timestamp;
                    }

                    var expected = (double)start + count * 10000000L / 30;

                    if (count >= 150 || output.Length + frameBytes > MaxVideoBytes ||
                        Math.Abs((double)timestamp - expected) > 166667)
                    {
                        throw new InvalidDataException("Video exceeds the bounded constant-rate playback contract.");
                    }

                    CopyFrame(sample, frame);
                    output.Write(frame);
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

            return new VideoPayload(width, height, frameBytes, start, output.ToArray());
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

            using var output = new MemoryStream();
            var start = 0L;
            var count = 0;

            while (true)
            {
                var sample = ReadSample(reader, AudioStream, out var timestamp, out var end);

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
                    if (count == 0)
                    {
                        start = timestamp;
                    }

                    var expected = (double)start + output.Length / 4 * 10000000L / 48000;

                    if (Math.Abs((double)timestamp - expected) > 333334 || count >= 10000)
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

    private static nint ReadSample(nint reader, uint stream, out long timestamp, out bool end)
    {
        var sample = nint.Zero;
        var flags = 0U;
        var actual = 0U;
        var time = 0L;
        var read = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9);
        var hr = read(reader, stream, 0, &actual, &flags, &time, &sample);

        if (hr < 0 || (flags & UnsupportedFlags) != 0)
        {
            Release(sample);
            Check(hr, "ReadSample");

            throw new InvalidDataException($"Unsupported source-reader flags: 0x{flags:X}.");
        }

        timestamp = time;
        end = (flags & EndOfStream) != 0;

        return sample;
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
                    throw new InvalidDataException("Unexpected contiguous NV12 extent.");
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
                        throw new InvalidDataException("Unexpected linear NV12 extent.");
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

    private sealed record VideoPayload(int Width, int Height, int FrameBytes, long Start, byte[] Frames);

    private sealed record AudioPayload(long Start, short[] Samples);
}
