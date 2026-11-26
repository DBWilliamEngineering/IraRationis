// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;
using IraRationis.HLE.Input.Models;

namespace IraRationis.HLE.Input;

internal sealed unsafe class DualSenseProvider
{
    private DualSenseReadingState? _latest;
    private int _reports;

    public DualSenseProvider()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "irx DualSense HID" };

        thread.Start();
    }

    public GameInputPadState? Current
    {
        get
        {
            return Volatile.Read(ref _latest)?.Pad;
        }
    }

    private void Run()
    {
        for(;;)
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
            var handle = GameInputModule.HidOpen(name, 0x80000000, 3, 0, 3, 0, 0);

            if (handle == -1)
            {
                if (GameInputModule.TraceInput && GameInputModule.SampleInput(Interlocked.Increment(ref _reports)))
                {
                    Console.Error.WriteLine($"[input] IRX-R30-HID: open failed ({Marshal.GetLastPInvokeError()})");
                }

                return;
            }

            try
            {
                if (!IsUsbReport(handle))
                {
                    if (GameInputModule.TraceInput && GameInputModule.SampleInput(Interlocked.Increment(ref _reports)))
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
                    while (GameInputModule.HidRead(handle, data, 64, out var bytes, 0) != 0)
                    {
                        if (bytes != 64 || !GameInputModule.TryDecodeDualSenseUsb(report, out var pad))
                        {
                            Console.Error.WriteLine(
                                $"[input] IRX-R30-HID: unsupported report bytes={bytes}; id=0x{report[0]:X2}");

                            return;
                        }

                        var latest = Volatile.Read(ref _latest);

                        if (latest is null || latest.Pad != pad)
                        {
                            Volatile.Write(ref _latest, new DualSenseReadingState(pad));
                        }

                        if (GameInputModule.TraceInput && previous != pad.Buttons)
                        {
                            Console.WriteLine(
                                $"[input] IRX-R30-HID: buttons=0x{pad.Buttons:X8}; Y={(pad.Buttons & 32) != 0}");
                        }

                        previous = pad.Buttons;
                    }
                }

                if (GameInputModule.TraceInput)
                {
                    Console.WriteLine($"[input] IRX-R30-HID: read ended ({Marshal.GetLastPInvokeError()})");
                }
            }
            finally
            {
                _ = GameInputModule.HidClose(handle);
            }
        }
    }

    private static string? FindDualSensePath()
    {
        uint count = 0;

        var entrySize = (uint)sizeof(GameInputRawDevice);

        if (GameInputModule.RawList(null, ref count, entrySize) == uint.MaxValue || count is 0 or > 4096)
        {
            return null;
        }

        var devices = new GameInputRawDevice[count];

        fixed (GameInputRawDevice* entries = devices)
        {
            var found = GameInputModule.RawList(entries, ref count, entrySize);

            if (found == uint.MaxValue || found > devices.Length)
            {
                return null;
            }

            for (var index = 0; index < found; index++)
            {
                if (entries[index].Type != 2)
                {
                    continue;
                }

                var info = new GameInputRawInfo { Size = (uint)sizeof(GameInputRawInfo) };
                var bytes = info.Size;

                if (GameInputModule.RawInfoQuery(entries[index].Handle, 0x2000000B, &info, ref bytes) == uint.MaxValue ||
                    info.Type != 2 || info.Vendor != 0x054C || info.Product != 0x0CE6 ||
                    info.UsagePage != 1 || info.Usage is not (4 or 5))
                {
                    continue;
                }

                uint chars = 0;

                if (GameInputModule.RawInfoQuery(entries[index].Handle, 0x20000007, null, ref chars) == uint.MaxValue ||
                    chars is 0 or > 4096)
                {
                    continue;
                }

                var name = new char[chars + 1];

                fixed (char* buffer = name)
                {
                    if (GameInputModule.RawInfoQuery(entries[index].Handle, 0x20000007, buffer, ref chars) == uint.MaxValue)
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

    private static bool IsUsbReport(nint handle)
    {
        if (GameInputModule.HidPreparsed(handle, out var data) == 0 || data == 0)
        {
            return false;
        }

        try
        {
            // HIDP_CAPS is 64 bytes; its first four USHORT fields are usage, usage page, input and output sizes.
            GameInputHidCaps caps = default;

            return GameInputModule.HidGetCaps(data, &caps) == 0x00110000 && caps.InputReportBytes == 64 &&
                   caps.UsagePage == 1 && caps.Usage is 4 or 5;
        }
        finally
        {
            _ = GameInputModule.HidFreePreparsed(data);
        }
    }
}
