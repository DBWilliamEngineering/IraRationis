// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio;

public sealed unsafe partial class AudioMixerScheduling : IDisposable
{
    internal const string ObservedBuild = "b99f876b74d4a77f03af5f878223b27bd647b5c35a79d5decd9f716504239e93";
    private const uint ThreadWrapperRva = 0x2215EA0;
    private const uint MixerThunkRva = 0x5C60800;
    private const uint MixerRva = 0x1E95040;
    private const uint MovieWorkerRva = 0x442FA80;
    private static nuint _image;
    private static bool _mixerEnabled;
    private static bool _movieEnabled;
    private int _previousPriority = int.MaxValue;
    private nint _mmcss;
    private nuint _previousAffinity;

    public static void Configure(nuint image, string hash)
    {
        _mixerEnabled = Environment.GetEnvironmentVariable("IRX_AUDIO_MIXER_SCHEDULING") == "1";
        _movieEnabled = Environment.GetEnvironmentVariable("IRX_BINK_WORKER_SCHEDULING") == "1";
        _image = (_mixerEnabled || _movieEnabled) && string.Equals(hash, ObservedBuild, StringComparison.OrdinalIgnoreCase) ? image : 0;

        if (_mixerEnabled || _movieEnabled)
        {
            Console.Error.WriteLine($"[audio-scheduling] profileMatched={_image != 0}; mixer={_mixerEnabled}; movie={_movieEnabled}.");
        }
    }

    internal static bool Matches(nuint image, nuint wrapper, nuint entry)
    {
        return image != 0 && wrapper >= image && entry >= image && wrapper - image == ThreadWrapperRva &&
            (entry - image == MixerThunkRva || entry - image == MixerRva);
    }

    internal static bool MatchesMovie(nuint image, nuint wrapper, nuint entry)
    {
        return image != 0 && wrapper >= image && entry >= image && wrapper - image == ThreadWrapperRva &&
            entry - image == MovieWorkerRva;
    }

    internal static AudioMixerScheduling? TryEnter(nint routine, nint parameter)
    {
        var image = _image;
        if (image == 0)
        {
            return null;
        }
        var start = (nuint)routine;
        var entry = start;
        if (start >= image && start - image == ThreadWrapperRva && parameter != 0)
        {
            var read = (nuint)0;
            if (ReadProcessMemory(-1, (void*)parameter, &entry, (nuint)sizeof(nuint), &read) == 0 ||
                read != (nuint)sizeof(nuint))
            {
                return null;
            }
        }
        var mixer = _mixerEnabled && Matches(image, start, entry);
        var movie = _movieEnabled && MatchesMovie(image, start, entry);
        if (!mixer && !movie)
        {
            return null;
        }

        var scope = new AudioMixerScheduling();
        try
        {
            if (mixer && GetActiveProcessorGroupCount() == 1 &&
                GetProcessAffinityMask(-1, out var processMask, out _) != 0 && processMask != 0)
            {
                scope._previousAffinity = SetThreadAffinityMask(-2, processMask);
            }

            uint taskIndex = 0;
            // The observed movie wrapper starts below normal. Avoid real-time priority;
            // give its producer a normal baseline and the separate Playback MMCSS class.
            if (movie && !mixer)
            {
                var previous = GetThreadPriority(-2);
                if (previous != int.MaxValue && previous < 0 && SetThreadPriority(-2, 0) != 0)
                {
                    scope._previousPriority = previous;
                }
            }
            scope._mmcss = AvSetMmThreadCharacteristics(mixer ? "Pro Audio" : "Playback", &taskIndex);
            var priority = scope._mmcss != 0 && AvSetMmThreadPriority(scope._mmcss, mixer ? 1 : 0) != 0;
            Console.Error.WriteLine($"[audio-scheduling] role={(mixer ? "mixer" : "movie")}; " +
                $"thread={GetCurrentThreadId()}; mmcss={scope._mmcss != 0}; relativePrioritySet={priority}; " +
                $"normalPriorityRestored={scope._previousPriority != int.MaxValue}; previousMask=0x{scope._previousAffinity:X}.");

            return scope;
        }
        catch (Exception exception)
        {
            scope.Dispose();
            Console.Error.WriteLine($"[audio-scheduling] unavailable ({exception.GetType().Name}).");
            return null;
        }
    }

    public void Dispose()
    {
        if (_mmcss != 0)
        {
            _ = AvRevertMmThreadCharacteristics(_mmcss);
            _mmcss = 0;
        }
        if (_previousAffinity != 0)
        {
            _ = SetThreadAffinityMask(-2, _previousAffinity);
            _previousAffinity = 0;
        }
        if (_previousPriority != int.MaxValue)
        {
            _ = SetThreadPriority(-2, _previousPriority);
            _previousPriority = int.MaxValue;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetThreadPriority(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReadProcessMemory(nint process, void* address, void* buffer, nuint bytes, nuint* read);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, uint* taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AvSetMmThreadPriority(nint task, int priority);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AvRevertMmThreadCharacteristics(nint task);
}
