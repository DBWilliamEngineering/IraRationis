// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;

namespace IraRationis.HLE.Audio.Movie;

internal static partial class MovieMixerPreciseWait
{
    internal const uint MaximumMilliseconds = 2;

    private const uint HighResolutionTimer = 0x00000002;
    private const uint TimerModifyState = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitSignaled = 0;
    private const uint WaitGraceMilliseconds = 20;

    [ThreadStatic]
    private static nint _timer;

    [ThreadStatic]
    private static bool _unavailable;

    internal static bool TryWait(uint milliseconds)
    {
        if (_unavailable || milliseconds is 0 or > MaximumMilliseconds)
        {
            return false;
        }

        try
        {
            if (_timer == 0)
            {
                _timer = CreateWaitableTimerEx(0, 0, HighResolutionTimer, Synchronize | TimerModifyState);
            }

            if (_timer != 0)
            {
                var dueTime = -(long)milliseconds * TimeSpan.TicksPerMillisecond;
                var start = Stopwatch.GetTimestamp();
                var minimumTicks = (milliseconds * Stopwatch.Frequency + 999) / 1_000;

                if (SetWaitableTimer(_timer, in dueTime, 0, 0, 0, 0) != 0 &&
                    WaitForSingleObject(_timer, milliseconds + WaitGraceMilliseconds) == WaitSignaled &&
                    Stopwatch.GetTimestamp() - start >= minimumTicks)
                {
                    return true;
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        Release();
        _unavailable = true;

        return false;
    }

    internal static void Release()
    {
        _unavailable = false;

        if (_timer == 0)
        {
            return;
        }

        var error = NativeErrorState.Capture();

        try
        {
            _ = CloseHandle(_timer);
        }
        finally
        {
            _timer = 0;
            error.Restore();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWaitableTimerEx(nint security, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetWaitableTimer(nint timer, in long dueTime, int period, nint completionRoutine,
        nint completionArgument, int resume);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CloseHandle(nint handle);
}
