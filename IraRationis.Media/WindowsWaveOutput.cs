using System.Runtime.InteropServices;

namespace IraRationis.Media;

/// <summary>Blocking native playback for short PCM samples. Not a streaming mixer or WASAPI engine.</summary>
public static unsafe partial class WindowsWaveOutput
{
    public static void Play(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var wave = PcmMixer.ToWave(samples, sampleRate, channels);

        fixed (byte* pointer = wave)
        {
            // SND_MEMORY | SND_NODEFAULT; synchronous by default, so memory stays pinned.
            if (PlaySound(pointer, 0, 0x0004 | 0x0002) == 0)
            {
                throw new InvalidOperationException("Windows could not play the PCM buffer.");
            }
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int PlaySound(byte* sound, nint module, uint flags);
}
