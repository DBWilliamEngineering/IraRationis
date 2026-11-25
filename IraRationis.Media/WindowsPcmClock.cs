// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media;

/// <summary>Owns a single bounded PCM16 submission and exposes its device position.</summary>
internal sealed unsafe partial class WindowsPcmClock : IDisposable
{
    private readonly int _channels;
    private readonly int _frames;
    private readonly int _rate;
    private nint _device;
    private WaveHeader* _header;
    private bool _prepared;
    private short* _samples;

    internal WindowsPcmClock(DecodedClip clip) : this(clip.Pcm, clip.SampleRate, clip.Channels)
    {
    }

    internal WindowsPcmClock(short[] pcm, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRate != 48000 || channels != 2 || pcm.Length == 0 ||
            pcm.Length % channels != 0 || pcm.Length > sampleRate * channels * 30)
        {
            throw new ArgumentException("Invalid bounded PCM16 input.", nameof(pcm));
        }

        _rate = sampleRate;
        _channels = channels;
        _frames = pcm.Length / _channels;
        var format = new WaveFormat
        {
            Tag = 1, Channels = (ushort)_channels, SamplesPerSecond = (uint)_rate,
            BlockAlign = (ushort)(_channels * 2), BitsPerSample = 16,
            AverageBytesPerSecond = (uint)(_rate * _channels * 2)
        };
        try
        {
            var result = WaveOutOpen(out _device, uint.MaxValue, &format, 0, 0, 0);
            Check(result, "waveOutOpen");
            _samples = (short*)NativeMemory.Alloc((nuint)pcm.Length, 2);
            _header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader));
            pcm.AsSpan().CopyTo(new Span<short>(_samples, pcm.Length));
            _header->Data = (nint)_samples;
            _header->Length = (uint)(pcm.Length * 2);
            Check(WaveOutPrepareHeader(_device, _header, (uint)sizeof(WaveHeader)), "waveOutPrepareHeader");
            _prepared = true;
        }
        catch
        {
            Dispose();

            throw;
        }
    }

    internal bool Done
    {
        get
        {
            return (Volatile.Read(ref _header->Flags) & 1U) != 0;
        }
    }

    internal double Seconds
    {
        get
        {
            var time = new MultimediaTime { Type = 2 }; // TIME_SAMPLES
            Check(WaveOutGetPosition(_device, &time, (uint)sizeof(MultimediaTime)), "waveOutGetPosition");
            var seconds = time.Type switch
            {
                1 => time.Value / 1000.0, // TIME_MS
                2 => time.Value / (double)_rate,
                4 => time.Value / (double)(_rate * _channels * 2), // TIME_BYTES
                _ => throw new InvalidOperationException($"Unsupported audio clock format {time.Type}.")
            };

            return Math.Clamp(seconds, 0, _frames / (double)_rate);
        }
    }

    public void Dispose()
    {
        if (_device != 0)
        {
            if (_prepared)
            {
                Check(WaveOutReset(_device), "waveOutReset");
                // Never release driver-owned memory if unprepare fails.
                Check(WaveOutUnprepareHeader(_device, _header, (uint)sizeof(WaveHeader)), "waveOutUnprepareHeader");
                _prepared = false;
            }

            Check(WaveOutClose(_device), "waveOutClose");
            _device = 0;
        }

        NativeMemory.Free(_header);
        NativeMemory.Free(_samples);
        _header = null;
        _samples = null;
    }

    internal void Start()
    {
        Check(WaveOutWrite(_device, _header, (uint)sizeof(WaveHeader)), "waveOutWrite");
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} failed (MMRESULT {result}).");
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutOpen(out nint device, uint id, WaveFormat* format, nuint callback,
        nuint instance, uint flags);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutPrepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutWrite(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutGetPosition")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutGetPosition(nint device, MultimediaTime* time, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutReset(nint device);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutUnprepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutClose(nint device);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Length, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }
}
