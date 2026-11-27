// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly Timer _reporter;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _nextTrack;
    private int _reportedRegistration;
    private int _reporting;
    private int _reports;
    private int _disposed;

    private MovieAudioImportTrace(IImportResolver resolver, nuint title, nuint registration)
    {
        _resolver = resolver;
        _registerAddress = (nint)registration;
        _factoryAddress = (nint)(title + FactoryRva);
        _openTrackAddress = (nint)(title + OpenTrackRva);
        _readyAddress = (nint)(title + ReadyRva);
        _lockAddress = (nint)(title + LockRva);
        _unlockAddress = (nint)(title + UnlockRva);
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
        Note("enabled=True; scope=Bink-to-guest-voice; units=PCM-bytes; periodMs=2000");

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

        return resolution;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _reporter.Dispose();
        }
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
            track.RecordRefusalObservation(channel);
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

            foreach (var (_, track) in _tracks)
            {
                if (track.TakeReport(now) is { } report)
                {
                    Note(FormattableString.Invariant($"elapsedMs={elapsed.TotalMilliseconds:F0}; {report}"));

                    if (Interlocked.Increment(ref _reports) >= MaximumReports)
                    {
                        _reporter.Change(Timeout.Infinite, Timeout.Infinite);

                        break;
                    }
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
