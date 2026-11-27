// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;
using IraRationis.HLE.Input.Diagnostics;
using IraRationis.HLE.Input.Models;

namespace IraRationis.HLE.Input.GameInputModule;

public static unsafe partial class GameInputModule
{
    internal static readonly bool TraceInput = Environment.GetEnvironmentVariable("IRX_INPUT_TRACE") == "1";
    internal static readonly InputDiagnosticReport InputDiagnostics = new(TraceInput);
    private static long _padReads;
    private static readonly int[] GuestButtons = new int[DeviceCount];

    internal static bool SampleInput(long value)
    {
        return value <= 8 || (value & (value - 1)) == 0;
    }

    // Standard 64-byte USB report, ID 1. The layout is corroborated by hid-playstation.
    // Reject other transports and report formats rather than interpreting unknown bytes.
    public static bool TryDecodeDualSenseUsb(ReadOnlySpan<byte> report, out GameInputPadState pad)
    {
        pad = default;

        if (report.Length != 64 || report[0] != 1)
        {
            return false;
        }

        var face = report[8];
        var other = report[9];
        var hat = face & 0x0f;

        uint buttons = hat switch
        {
            0 => 64,
            1 => 64 | 512,
            2 => 512,
            3 => 128 | 512,
            4 => 128,
            5 => 128 | 256,
            6 => 256,
            7 => 64 | 256,
            8 => 0,
            _ => uint.MaxValue
        };

        if (buttons == uint.MaxValue)
        {
            return false;
        }

        if ((face & 0x10) != 0) // Square -> X
        {
            buttons |= 16;
        }

        if ((face & 0x20) != 0) // Cross -> A
        {
            buttons |= 4;
        }

        if ((face & 0x40) != 0) // Circle -> B
        {
            buttons |= 8;
        }

        if ((face & 0x80) != 0) // Triangle -> Y
        {
            buttons |= 32;
        }

        if ((other & 0x01) != 0)
        {
            buttons |= 1024;
        }

        if ((other & 0x02) != 0)
        {
            buttons |= 2048;
        }

        if ((other & 0x10) != 0) // Create -> View
        {
            buttons |= 2;
        }

        if ((other & 0x20) != 0) // Options -> Menu
        {
            buttons |= 1;
        }

        if ((other & 0x40) != 0)
        {
            buttons |= 4096;
        }

        if ((other & 0x80) != 0)
        {
            buttons |= 8192;
        }

        pad = new GameInputPadState(buttons, report[5] / 255F, report[6] / 255F,
            Axis(report[1]), -Axis(report[2]), Axis(report[3]), -Axis(report[4]));

        return true;
    }

    private static void VerifyDualSense()
    {
        if (sizeof(GameInputRawDevice) != 16 || sizeof(GameInputRawInfo) != 32 || sizeof(GameInputHidCaps) != 64)
        {
            throw new InvalidOperationException("DualSense HID ABI layout mismatch.");
        }

        Span<byte> report = stackalloc byte[64];

        report.Clear();

        report[0] = 1;

        report.Slice(1, 4).Fill(128);

        report[8] = 8;

        if (!TryDecodeDualSenseUsb(report, out var neutral) || neutral != default)
        {
            throw new InvalidOperationException("DualSense neutral report mismatch.");
        }

        report[8] = 0x88;

        if (!TryDecodeDualSenseUsb(report, out var triangle) || triangle.Buttons != 32)
        {
            throw new InvalidOperationException("DualSense Triangle mapping mismatch.");
        }

        report[8] = 1;

        report[9] = 0xf3;

        report[1] = 0;

        report[2] = 0;

        report[3] = 255;

        report[4] = 255;

        report[5] = 255;

        if (!TryDecodeDualSenseUsb(report, out var all)
            || all.Buttons != (64U | 512U | 1024U | 2048U | 1U | 2U | 4096U | 8192U) ||
            all.LeftX != -1 || all.LeftY != 1 || all.RightX != 1 || all.RightY != -1 || all.LeftTrigger != 1)
        {
            throw new InvalidOperationException("DualSense axis/button report mismatch.");
        }

        report[0] = 0x31;

        if (TryDecodeDualSenseUsb(report, out _) || TryDecodeDualSenseUsb(report[..10], out _))
        {
            throw new InvalidOperationException("DualSense unsupported report accepted.");
        }
    }

    private static float Axis(byte value)
    {
        var offset = value - 128;
        // Small fixed dead zone suppresses rest-position jitter; remap remaining range.
        const int deadZone = 8;

        if (Math.Abs(offset) <= deadZone)
        {
            return 0;
        }

        return offset < 0 ? (offset + deadZone) / (128F - deadZone) : (offset - deadZone) / (127F - deadZone);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceList", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint RawList(GameInputRawDevice* list, ref uint count, uint entrySize);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint RawInfoQuery(nint device, uint command, void* data, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint HidOpen(char* path, uint access, uint share, nint security, uint creation,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int HidRead(nint handle, byte* data, uint bytes, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int HidClose(nint handle);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetPreparsedData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial byte HidPreparsed(nint handle, out nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial byte HidFreePreparsed(nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int HidGetCaps(nint preparsed, GameInputHidCaps* caps);
}
