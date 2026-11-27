// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.Loader.Images;
using IraRationis.Loader.Imports;
using IraRationis.Loader.Imports.Models;
using IraRationis.PE.Models;

namespace IraRationis.HLE.Audio.Movie;

public sealed unsafe class MovieAudioImportTrace : IImportResolver, IDisposable
{
    public const string LibraryName = "Bink2Scarlett.dll";

    private const string LibraryHash = "cb71456919aada87fddafc44a27b8733aa827de4da58516e9381323ef70da331";
    private const string RegistrationName = "BinkSetSoundSystem";
    private const string MixerSleepLibrary = "api-ms-win-core-synch-l1-2-0";
    private const string EndpointWaitLibrary = "api-ms-win-core-synch-l1-1-0";
    private const uint RegistrationRva = 0x4DC0;
    private const uint FactoryRva = 0x5F172C0;
    private const uint OpenTrackRva = 0x1A9B300;
    private const uint ReadyRva = 0x20EEB40;
    private const uint LockRva = 0x442E820;
    private const uint UnlockRva = 0xB98F70;
    private const int ReadyOffset = 0x170;
    private const int LockOffset = 0x178;
    private const int UnlockOffset = 0x180;
    private const int MaximumTracks = 32;
    private const int MaximumReports = 128;
    private static readonly long ActiveMovieWindowTicks = Stopwatch.Frequency / 2;

    private static MovieAudioImportTrace? _binding;

    private readonly IImportResolver _resolver;
    private readonly nint _registerAddress;
    private readonly nint _factoryAddress;
    private readonly nint _openTrackAddress;
    private readonly nint _readyAddress;
    private readonly nint _lockAddress;
    private readonly nint _unlockAddress;
    private readonly Lock _registrationLock = new();
    private readonly ConcurrentDictionary<nint, MovieAudioTrack> _tracks = new();
    private readonly MovieMixerWaitAudit _mixerWaitAudit = new();
    private readonly MovieAudioEndpointTrace? _endpointTrace;
    private readonly bool _mixerWaitAuditEnabled = MixerWaitAuditEnabled;
    private readonly bool _preciseMixerWaitEnabled;
    private readonly Timer _reporter;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _nextTrack;
    private int _reportedRegistration;
    private int _reporting;
    private int _reports;
    private int _disposed;
    private nint _sleepAddress;
    private nint _endpointWaitAddress;
    private long _lastAcceptedMovieBlock;

    private MovieAudioImportTrace(IImportResolver resolver, nuint title, nuint registration)
    {
        _resolver = resolver;
        _registerAddress = (nint)registration;
        _factoryAddress = (nint)(title + FactoryRva);
        _openTrackAddress = (nint)(title + OpenTrackRva);
        _readyAddress = (nint)(title + ReadyRva);
        _lockAddress = (nint)(title + LockRva);
        _unlockAddress = (nint)(title + UnlockRva);
        _preciseMixerWaitEnabled = _mixerWaitAuditEnabled && PreciseMixerWaitRequested;
        _endpointTrace = EndpointProbeRequested ? new MovieAudioEndpointTrace() : null;
        _reporter = new Timer(static state => ((MovieAudioImportTrace)state!).Report(), this,
            Timeout.Infinite, Timeout.Infinite);
    }

    public static bool Requested
    {
        get
        {
            return Environment.GetEnvironmentVariable("IRX_MOVIE_AUDIO_TRACE") == "1";
        }
    }

    internal static bool MixerWaitAuditRequested
    {
        get
        {
            return Requested && MixerWaitAuditEnabled;
        }
    }

    internal static bool EndpointProbeRequested
    {
        get
        {
            return Requested && Environment.GetEnvironmentVariable("IRX_MOVIE_ENDPOINT_TRACE") == "1";
        }
    }

    internal static bool IsEndpointProbeBound
    {
        get
        {
            return Volatile.Read(ref _binding) is { } trace && Volatile.Read(ref trace._disposed) == 0 &&
                Volatile.Read(ref trace._endpointWaitAddress) != 0;
        }
    }

    internal static bool TryAttachEndpointMixer(nint state)
    {
        return Volatile.Read(ref _binding)?._endpointTrace?.TryAttach(state) == true;
    }

    internal static void DetachEndpointMixer(nint state)
    {
        Volatile.Read(ref _binding)?._endpointTrace?.Detach(state);
    }

    private static bool MixerWaitAuditEnabled
    {
        get
        {
            var audit = Environment.GetEnvironmentVariable("IRX_MOVIE_MIXER_WAIT_AUDIT");

            return audit == "1" || (audit != "0" && PreciseMixerWaitRequested);
        }
    }

    private static bool PreciseMixerWaitRequested
    {
        get
        {
            return Environment.GetEnvironmentVariable("IRX_PRECISE_MOVIE_MIXER_WAIT") == "1";
        }
    }

    internal static bool IsMixerWaitAuditBound
    {
        get
        {
            return Volatile.Read(ref _binding) is { } trace && Volatile.Read(ref trace._disposed) == 0 &&
                Volatile.Read(ref trace._sleepAddress) != 0;
        }
    }

    public static MovieAudioImportTrace? Begin(IImportResolver resolver, LoadedImage title, LoadedImage? library)
    {
        if (!Requested)
        {
            return null;
        }

        if (library is null || !MatchesProfile(title.OriginalHash, library.OriginalHash) ||
            title.Info.SizeOfImage <= FactoryRva || library.Info.SizeOfImage <= RegistrationRva ||
            library.Export(new ImportSymbol(RegistrationName, 0)) is not { } address ||
            address != library.BaseAddress + RegistrationRva)
        {
            Note("enabled=False; reason=unverified-title-or-library");

            return null;
        }

        var trace = new MovieAudioImportTrace(resolver, title.BaseAddress, address);

        // Bound callbacks may outlive this trace's reporting scope. Keep their forwards valid.
        if (Interlocked.CompareExchange(ref _binding, trace, null) is not null)
        {
            trace.Dispose();

            return null;
        }

        trace._reporter.Change(2_000, 2_000);
        Note($"enabled=True; scope=Bink-to-guest-voice; units=PCM-bytes; periodMs=2000; " +
            $"mixerWaitAudit={trace._mixerWaitAuditEnabled}; mixerPreciseWaitRequested={trace._preciseMixerWaitEnabled}; " +
            $"endpointProbe={trace._endpointTrace != null}");

        return trace;
    }

    internal static bool MatchesProfile(string titleHash, string libraryHash)
    {
        return string.Equals(titleHash, AudioMixerScheduling.ObservedBuild, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(libraryHash, LibraryHash, StringComparison.OrdinalIgnoreCase);
    }

    public ImportResolution? Resolve(string dll, ImportSymbol symbol)
    {
        var resolution = _resolver.Resolve(dll, symbol);

        if (resolution is { Provenance: ImportProvenance.Title } binding &&
            binding.Address == (nuint)_registerAddress &&
            string.Equals(dll, LibraryName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(symbol.Name, RegistrationName, StringComparison.Ordinal))
        {
            return binding with { Address = (nuint)(delegate* unmanaged<nint, nuint, int>)&RegisterSoundSystem };
        }

        if (_mixerWaitAuditEnabled && resolution is { Provenance: ImportProvenance.Host, Address: not 0 } host &&
            string.Equals(symbol.Name, "Sleep", StringComparison.Ordinal) &&
            (string.Equals(dll, MixerSleepLibrary, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dll, MixerSleepLibrary + ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var address = (nint)host.Address;
            var previous = Interlocked.CompareExchange(ref _sleepAddress, address, 0);

            if (previous == 0 || previous == address)
            {
                if (previous == 0)
                {
                    Note("mixerSleepAuditBound=True");
                }

                return host with { Address = (nuint)(delegate* unmanaged[Stdcall]<uint, void>)&Sleep };
            }
        }

        if (_endpointTrace != null && resolution is { Provenance: ImportProvenance.Host, Address: not 0 } endpoint &&
            string.Equals(symbol.Name, "WaitForSingleObject", StringComparison.Ordinal) &&
            (string.Equals(dll, EndpointWaitLibrary, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dll, EndpointWaitLibrary + ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var address = (nint)endpoint.Address;
            var previous = Interlocked.CompareExchange(ref _endpointWaitAddress, address, 0);

            if (previous == 0 || previous == address)
            {
                if (previous == 0)
                {
                    Note("endpointWaitAuditBound=True");
                }

                return endpoint with { Address = (nuint)(delegate* unmanaged[Stdcall]<nint, uint, uint>)&WaitForEndpointEvent };
            }
        }

        return resolution;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _reporter.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Sleep(uint milliseconds)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged[Stdcall]<uint, void>)Volatile.Read(ref trace._sleepAddress);

        if (!AudioMixerScheduling.IsCurrentMixerObserved || Volatile.Read(ref trace._disposed) != 0 ||
            Volatile.Read(ref trace._reports) >= MaximumReports || milliseconds > MovieMixerWaitAudit.MaximumTimedWaitMilliseconds)
        {
            original(milliseconds);
            return;
        }

        if (milliseconds == 0)
        {
            trace._mixerWaitAudit.RecordYield();
            original(milliseconds);
            return;
        }

        var incomingError = NativeErrorState.Capture();
        var started = Stopwatch.GetTimestamp();
        var tryPrecise = trace.ShouldTryPreciseWait(milliseconds, started);
        var usedPrecise = tryPrecise && MovieMixerPreciseWait.TryWait(milliseconds);
        var resultingError = incomingError;

        if (!usedPrecise)
        {
            incomingError.Restore();
            original(milliseconds);
            resultingError = NativeErrorState.Capture();
        }

        try
        {
            trace._mixerWaitAudit.RecordWait(milliseconds, Stopwatch.GetTimestamp() - started, tryPrecise, usedPrecise);
        }
        finally
        {
            resultingError.Restore();
        }
    }

    private bool ShouldTryPreciseWait(uint milliseconds, long now)
    {
        if (!_preciseMixerWaitEnabled || milliseconds > MovieMixerPreciseWait.MaximumMilliseconds)
        {
            return false;
        }

        var lastBlock = Volatile.Read(ref _lastAcceptedMovieBlock);
        var age = now - lastBlock;

        return lastBlock != 0 && age >= 0 && age <= ActiveMovieWindowTicks;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint WaitForEndpointEvent(nint handle, uint milliseconds)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged[Stdcall]<nint, uint, uint>)Volatile.Read(ref trace._endpointWaitAddress);
        var endpoint = trace._endpointTrace!;

        if (!AudioMixerScheduling.IsCurrentEndpointObserved || Volatile.Read(ref trace._disposed) != 0 ||
            Volatile.Read(ref trace._reports) >= MaximumReports || !endpoint.Observes(handle))
        {
            return original(handle, milliseconds);
        }

        var result = original(handle, milliseconds);
        var error = NativeErrorState.Capture();

        try
        {
            endpoint.RecordWait(result);
        }
        finally
        {
            error.Restore();
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int RegisterSoundSystem(nint factory, nuint parameter)
    {
        var trace = Volatile.Read(ref _binding)!;
        var wrap = factory == trace._factoryAddress && parameter == 0;
        var original = (delegate* unmanaged<nint, nuint, int>)trace._registerAddress;
        var result = original(wrap ? (nint)(delegate* unmanaged<nuint, nint>)&OpenSoundSystem : factory, parameter);

        if (wrap && Interlocked.Exchange(ref trace._reportedRegistration, 1) == 0)
        {
            var error = NativeErrorState.Capture();

            try
            {
                Note($"registrationResult={result}");
            }
            finally
            {
                error.Restore();
            }
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static nint OpenSoundSystem(nuint parameter)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged<nuint, nint>)trace._factoryAddress;
        var result = original(parameter);

        return result == trace._openTrackAddress && Volatile.Read(ref trace._disposed) == 0
            ? (nint)(delegate* unmanaged<byte*, uint, uint, uint, nint, int>)&OpenTrack
            : result;
    }

    [UnmanagedCallersOnly]
    private static int OpenTrack(byte* channel, uint sampleRate, uint channels, uint flags, nint movie)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged<byte*, uint, uint, uint, nint, int>)trace._openTrackAddress;
        var result = original(channel, sampleRate, channels, flags, movie);

        if (result != 0 && channel != null && Volatile.Read(ref trace._disposed) == 0)
        {
            var error = NativeErrorState.Capture();

            try
            {
                trace.AttachTrack(channel, sampleRate, channels);
            }
            catch (Exception)
            {
            }
            finally
            {
                error.Restore();
            }
        }

        return result;
    }

    private void AttachTrack(byte* channel, uint sampleRate, uint channels)
    {
        var ready = (nint*)(channel + ReadyOffset);
        var unlock = (nint*)(channel + UnlockOffset);

        if (*ready != _readyAddress || *(nint*)(channel + LockOffset) != _lockAddress || *unlock != _unlockAddress)
        {
            return;
        }

        lock (_registrationLock)
        {
            if (_nextTrack >= MaximumTracks)
            {
                return;
            }

            var track = new MovieAudioTrack(++_nextTrack, sampleRate, channels,
                *(uint*)(channel + 0x98), *(uint*)(channel + 0x90));

            _tracks[(nint)channel] = track;
            *ready = (nint)(delegate* unmanaged<nint, int>)&TrackReady;
            *unlock = (nint)(delegate* unmanaged<nint, uint, int>)&TrackUnlock;
        }
    }

    [UnmanagedCallersOnly]
    private static int TrackReady(nint channel)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged<nint, int>)trace._readyAddress;
        var result = original(channel);

        if (Volatile.Read(ref trace._disposed) == 0 && trace._tracks.TryGetValue(channel, out var track) &&
            track.RecordReady(result != 0))
        {
            track.RecordReadyObservation(channel, result != 0);
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int TrackUnlock(nint channel, uint bytes)
    {
        var trace = Volatile.Read(ref _binding)!;
        var original = (delegate* unmanaged<nint, uint, int>)trace._unlockAddress;
        var result = original(channel, bytes);

        if (Volatile.Read(ref trace._disposed) == 0 && trace._tracks.TryGetValue(channel, out var track))
        {
            track.RecordUnlock(result != 0, bytes);

            if (trace._preciseMixerWaitEnabled && result != 0 && bytes != 0)
            {
                Volatile.Write(ref trace._lastAcceptedMovieBlock, Stopwatch.GetTimestamp());
            }
        }

        return result;
    }

    private void Report()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _reports) >= MaximumReports ||
            Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_started, now);

            if (_mixerWaitAudit.TakeReport() is { } waitReport && !ReportSample(elapsed, waitReport))
            {
                return;
            }

            if (_endpointTrace?.TakeReport(now) is { } endpointReport && !ReportSample(elapsed, endpointReport))
            {
                return;
            }

            foreach (var (_, track) in _tracks)
            {
                if (track.TakeReport(now) is { } report && !ReportSample(elapsed, report))
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    private bool ReportSample(TimeSpan elapsed, string report)
    {
        Note(FormattableString.Invariant($"elapsedMs={elapsed.TotalMilliseconds:F0}; {report}"));

        if (Interlocked.Increment(ref _reports) < MaximumReports)
        {
            return true;
        }

        _reporter.Change(Timeout.Infinite, Timeout.Infinite);

        return false;
    }

    private static void Note(string message)
    {
        try
        {
            Console.Error.WriteLine($"[movie-audio] {message}.");
        }
        catch (Exception)
        {
        }
    }
}
