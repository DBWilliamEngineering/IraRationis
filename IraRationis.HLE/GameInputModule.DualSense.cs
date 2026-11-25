// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;

namespace IraRationis.HLE;

public static unsafe partial class GameInputModule
{
    private static readonly bool TraceInputR30 = Environment.GetEnvironmentVariable("IRX_INPUT_TRACE") == "1";
    private static long _r30CurrentReads;
    private static long _r30PadReads;
    private static int _r30GuestButtons;

    private static bool SampleInputR30(long value)
    {
        return value <= 8 || (value & (value - 1)) == 0;
    }

    // Standard 64-byte USB report, ID 1. The layout is corroborated by hid-playstation.
    // Reject other transports and report formats rather than interpreting unknown bytes.
    public static bool TryDecodeDualSenseUsb(ReadOnlySpan<byte> report, out PadState pad)
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
            0 => 64, 1 => 64 | 512, 2 => 512, 3 => 128 | 512,
            4 => 128, 5 => 128 | 256, 6 => 256, 7 => 64 | 256,
            8 => 0, _ => uint.MaxValue
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

        pad = new PadState(buttons, report[5] / 255F, report[6] / 255F,
            AxisR30(report[1]), -AxisR30(report[2]), AxisR30(report[3]), -AxisR30(report[4]));

        return true;
    }

    private static void VerifyDualSenseR30()
    {
        if (sizeof(RawDeviceR30) != 16 || sizeof(RawInfoR30) != 32 || sizeof(HidCapsR30) != 64)
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

    private static float AxisR30(byte value)
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
    private static partial uint RawListR30(RawDeviceR30* list, ref uint count, uint entrySize);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint RawInfoQueryR30(nint device, uint command, void* data, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint HidOpenR30(char* path, uint access, uint share, nint security, uint creation,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int HidReadR30(nint handle, byte* data, uint bytes, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int HidCloseR30(nint handle);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetPreparsedData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial byte HidPreparsedR30(nint handle, out nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial byte HidFreePreparsedR30(nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int HidGetCapsR30(nint preparsed, HidCapsR30* caps);

    private sealed class DualSenseProvider
    {
        private ReadingState? _latest;
        private int _reports;

        public DualSenseProvider()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "irx DualSense HID" };
            thread.Start();
        }

        public PadState? Current
        {
            get
            {
                return Volatile.Read(ref _latest)?.Pad;
            }
        }

        private void Run()
        {
            while (true)
            {
                try
                {
                    ReadDevice();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[input] IRX-R30-HID: reader error: {exception.Message}");
                }
                finally
                {
                    if (Interlocked.Exchange(ref _latest, null) is not null)
                    {
                        Console.WriteLine("[input] IRX-R30-HID: disconnected");
                    }
                }

                Thread.Sleep(1000);
            }
        }

        private void ReadDevice()
        {
            var path = FindDualSensePath();
            if (path is null)
            {
                return;
            }

            // A blocking read is isolated on this background thread. No guest thread or hub lock waits on HID.
            fixed (char* name = path)
            {
                var handle = HidOpenR30(name, 0x80000000, 3, 0, 3, 0, 0);
                if (handle == -1)
                {
                    if (TraceInputR30 && SampleInputR30(Interlocked.Increment(ref _reports)))
                    {
                        Console.Error.WriteLine($"[input] IRX-R30-HID: open failed ({Marshal.GetLastPInvokeError()})");
                    }

                    return;
                }

                try
                {
                    if (!IsUsbReportR30(handle))
                    {
                        if (TraceInputR30 && SampleInputR30(Interlocked.Increment(ref _reports)))
                        {
                            Console.Error.WriteLine("[input] IRX-R30-HID: unsupported report length; wired USB only");
                        }

                        return;
                    }

                    Console.WriteLine("[input] IRX-R30-HID: Sony DualSense USB opened (054C:0CE6)");
                    Span<byte> report = stackalloc byte[64];
                    var previous = uint.MaxValue;
                    fixed (byte* data = report)
                    {
                        while (HidReadR30(handle, data, 64, out var bytes, 0) != 0)
                        {
                            if (bytes != 64 || !TryDecodeDualSenseUsb(report, out var pad))
                            {
                                Console.Error.WriteLine(
                                    $"[input] IRX-R30-HID: unsupported report bytes={bytes}; id=0x{report[0]:X2}");

                                return;
                            }

                            var latest = Volatile.Read(ref _latest);
                            if (latest is null || latest.Pad != pad)
                            {
                                Volatile.Write(ref _latest, new ReadingState(pad));
                            }

                            if (TraceInputR30 && previous != pad.Buttons)
                            {
                                Console.WriteLine(
                                    $"[input] IRX-R30-HID: buttons=0x{pad.Buttons:X8}; Y={(pad.Buttons & 32) != 0}");
                            }

                            previous = pad.Buttons;
                        }
                    }

                    if (TraceInputR30)
                    {
                        Console.WriteLine($"[input] IRX-R30-HID: read ended ({Marshal.GetLastPInvokeError()})");
                    }
                }
                finally
                {
                    _ = HidCloseR30(handle);
                }
            }
        }

        private static string? FindDualSensePath()
        {
            uint count = 0;
            var entrySize = (uint)sizeof(RawDeviceR30);
            if (RawListR30(null, ref count, entrySize) == uint.MaxValue || count is 0 or > 4096)
            {
                return null;
            }

            var devices = new RawDeviceR30[count];
            fixed (RawDeviceR30* entries = devices)
            {
                var found = RawListR30(entries, ref count, entrySize);
                if (found == uint.MaxValue || found > devices.Length)
                {
                    return null;
                }

                for (var i = 0; i < found; i++)
                {
                    if (entries[i].Type != 2)
                    {
                        continue;
                    }

                    var info = new RawInfoR30 { Size = (uint)sizeof(RawInfoR30) };
                    var bytes = info.Size;
                    if (RawInfoQueryR30(entries[i].Handle, 0x2000000B, &info, ref bytes) == uint.MaxValue ||
                        info.Type != 2 || info.Vendor != 0x054C || info.Product != 0x0CE6 ||
                        info.UsagePage != 1 || info.Usage is not (4 or 5))
                    {
                        continue;
                    }

                    uint chars = 0;
                    if (RawInfoQueryR30(entries[i].Handle, 0x20000007, null, ref chars) == uint.MaxValue ||
                        chars is 0 or > 4096)
                    {
                        continue;
                    }

                    var name = new char[chars + 1];
                    fixed (char* buffer = name)
                    {
                        if (RawInfoQueryR30(entries[i].Handle, 0x20000007, buffer, ref chars) == uint.MaxValue)
                        {
                            continue;
                        }

                        var end = Array.IndexOf(name, '\0');
                        if (end > 0)
                        {
                            return new string(name, 0, end);
                        }
                    }
                }
            }

            return null;
        }

        private static bool IsUsbReportR30(nint handle)
        {
            if (HidPreparsedR30(handle, out var data) == 0 || data == 0)
            {
                return false;
            }

            try
            {
                // HIDP_CAPS is 64 bytes; its first four USHORT fields are usage, usage page, input and output sizes.
                HidCapsR30 caps = default;

                return HidGetCapsR30(data, &caps) == 0x00110000 && caps.InputReportBytes == 64 &&
                       caps.UsagePage == 1 && caps.Usage is 4 or 5;
            }
            finally
            {
                _ = HidFreePreparsedR30(data);
            }
        }

        private sealed record ReadingState(PadState Pad);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDeviceR30
    {
        public nint Handle;
        public uint Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct RawInfoR30
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint Vendor;
        [FieldOffset(12)] public uint Product;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCapsR30
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportBytes;
    }
}
