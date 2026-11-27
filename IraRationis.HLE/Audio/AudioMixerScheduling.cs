// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Audio.Movie;
using IraRationis.HLE.Runtime.Diagnostics;

namespace IraRationis.HLE.Audio;

public sealed unsafe partial class AudioMixerScheduling : IDisposable
{
    internal const string ObservedBuild = "b99f876b74d4a77f03af5f878223b27bd647b5c35a79d5decd9f716504239e93";
    private const uint ThreadWrapperRva = 0x2215EA0;
    private const uint MixerThunkRva = 0x5C60800;
    private const uint MixerRva = 0x1E95040;
    private const uint MovieWorkerRva = 0x442FA80;
    private const uint EndpointWorkerThunkRva = 0x43ADC30;
    private const uint EndpointWorkerRva = 0x43ADC40;
    private static nuint _image;
    private static bool _mixerEnabled;
    private static bool _movieEnabled;
    private static bool _endpointEnabled;
    [ThreadStatic] private static int _mixerObservationDepth;
    [ThreadStatic] private static int _endpointObservationDepth;
    private nint _endpointMixerState;
    private nint _mmcss;
    private bool _observesEndpoint;
    private bool _observesMixer;
    private nuint _previousAffinity;
    private int _previousPriority = int.MaxValue;

    private AudioMixerScheduling(bool observesMixer, bool observesEndpoint)
    {
        _observesMixer = observesMixer;
        _observesEndpoint = observesEndpoint;

        if (_observesMixer)
        {
            _mixerObservationDepth++;
        }

        if (_observesEndpoint)
        {
            _endpointObservationDepth++;
        }
    }

    internal static bool IsCurrentMixerObserved
    {
        get
        {
            return _mixerObservationDepth != 0;
        }
    }

    internal static bool IsCurrentEndpointObserved
    {
        get
        {
            return _endpointObservationDepth != 0;
        }
    }

    public void Dispose()
    {
        if (_endpointMixerState != 0)
        {
            MovieAudioImportTrace.DetachEndpointMixer(_endpointMixerState);
            _endpointMixerState = 0;
        }

        if (_observesEndpoint)
        {
            _endpointObservationDepth--;
            _observesEndpoint = false;
        }

        if (_observesMixer)
        {
            _mixerObservationDepth--;
            _observesMixer = false;

            if (_mixerObservationDepth == 0)
            {
                MovieMixerPreciseWait.Release();
            }
        }

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

    public static void Configure(nuint image, string hash)
    {
        _mixerEnabled = Environment.GetEnvironmentVariable("IRX_AUDIO_MIXER_SCHEDULING") == "1";
        _movieEnabled = Environment.GetEnvironmentVariable("IRX_BINK_WORKER_SCHEDULING") == "1";
        _endpointEnabled = Environment.GetEnvironmentVariable("IRX_AUDIO_ENDPOINT_SCHEDULING") == "1";
        _image = (_mixerEnabled || _movieEnabled || _endpointEnabled || MovieAudioImportTrace.MixerWaitAuditRequested ||
                  MovieAudioImportTrace.EndpointProbeRequested) &&
                 string.Equals(hash, ObservedBuild, StringComparison.OrdinalIgnoreCase)
            ? image
            : 0;

        if (_mixerEnabled || _movieEnabled || _endpointEnabled)
        {
            Console.Error.WriteLine($"[audio-scheduling] profileMatched={_image != 0}; mixer={_mixerEnabled}; " +
                                    $"movie={_movieEnabled}; endpoint={_endpointEnabled}.");
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

    internal static bool MatchesEndpointWorker(nuint image, nuint wrapper, nuint entry)
    {
        return image != 0 && wrapper >= image && entry >= image && wrapper - image == ThreadWrapperRva &&
               (entry - image == EndpointWorkerThunkRva || entry - image == EndpointWorkerRva);
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

        var matchedMixer = Matches(image, start, entry);
        var matchedEndpoint = MatchesEndpointWorker(image, start, entry);
        var observesMixer = matchedMixer && MovieAudioImportTrace.IsMixerWaitAuditBound;
        var observesEndpoint = (matchedMixer || matchedEndpoint) &&
                               MovieAudioImportTrace.IsEndpointProbeBound;

        var mixer = _mixerEnabled && matchedMixer;
        var endpoint = _endpointEnabled && matchedEndpoint;
        var movie = _movieEnabled && MatchesMovie(image, start, entry);

        if (!mixer && !endpoint && !movie && !observesMixer && !observesEndpoint)
        {
            return null;
        }

        var scope = new AudioMixerScheduling(observesMixer, observesEndpoint);

        if (observesEndpoint && matchedMixer && parameter > 0 && parameter <= nint.MaxValue - sizeof(nint) &&
            RuntimeBridgeMemoryReader.TryReadPointer(parameter + sizeof(nint), out var mixerState) &&
            MovieAudioImportTrace.TryAttachEndpointMixer(mixerState))
        {
            scope._endpointMixerState = mixerState;
        }

        if (!mixer && !endpoint && !movie)
        {
            if (matchedEndpoint && observesEndpoint)
            {
                Console.Error.WriteLine(
                    $"[audio-scheduling] role=endpoint; thread={GetCurrentThreadId()}; observeOnly=True.");
            }

            return scope;
        }

        try
        {
            var realtimeAudio = mixer || endpoint;

            if (realtimeAudio && GetActiveProcessorGroupCount() == 1 &&
                GetProcessAffinityMask(-1, out var processMask, out _) != 0 && processMask != 0)
            {
                scope._previousAffinity = SetThreadAffinityMask(-2, processMask);
            }

            uint taskIndex = 0;
            // The observed movie wrapper starts below normal. Avoid real-time priority;
            // give its producer a normal baseline and the separate Playback MMCSS class.
            if (movie)
            {
                var previous = GetThreadPriority(-2);
                if (previous != int.MaxValue && previous < 0 && SetThreadPriority(-2, 0) != 0)
                {
                    scope._previousPriority = previous;
                }
            }

            scope._mmcss = AvSetMmThreadCharacteristics(realtimeAudio ? "Pro Audio" : "Playback", &taskIndex);

            var priority = scope._mmcss != 0 && AvSetMmThreadPriority(scope._mmcss, realtimeAudio ? 1 : 0) != 0;
            var role = mixer ? "mixer" : endpoint ? "endpoint" : "movie";

            Console.Error.WriteLine($"[audio-scheduling] role={role}; " +
                                    $"thread={GetCurrentThreadId()}; mmcss={scope._mmcss != 0}; relativePrioritySet={priority}; "
                                    +
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

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, uint* taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AvSetMmThreadPriority(nint task, int priority);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AvRevertMmThreadCharacteristics(nint task);
}
