// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/runtime.rs, use_console_timers.

using System.Runtime.InteropServices;
using IraRationis.HLE.Kernel.Models;

namespace IraRationis.HLE.Kernel;

public sealed unsafe partial class ConsoleTimers : IDisposable
{
    private const int ProcessPowerThrottling = 4;
    private const uint IgnoreTimerResolution = 0x4;
    private bool _active;

    public ConsoleTimers()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Console timers require Windows.");
        }

        var state = new ConsoleTimersPowerThrottlingState
        {
            Version = 1,
            ControlMask = IgnoreTimerResolution,
            StateMask = 0
        };

        // Older Windows releases may reject the power setting. The upstream runtime
        // continues in this case and still requests millisecond timer resolution.
        _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state,
            (uint)sizeof(ConsoleTimersPowerThrottlingState));

        _active = TimeBeginPeriod(1) == 0;
    }

    public void Dispose()
    {
        if (!_active)
        {
            return;
        }

        _ = TimeEndPeriod(1);

        _active = false;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetProcessInformation(nint process, int informationClass, void* information,
        uint informationSize);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TimeEndPeriod(uint period);
}
