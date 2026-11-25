// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.HLE;

public static unsafe partial class GameInputModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int NoInterface = unchecked((int)0x80004002);
    private const int Failure = unchecked((int)0x80004005);
    private const int Disconnected = unchecked((int)0x838A0001);
    private const int DeviceNotFound = unchecked((int)0x838A0002);
    private const int ReadingNotFound = unchecked((int)0x838A0003);
    private const int ReferenceTooOld = unchecked((int)0x838A0004);
    private const uint KeyboardKind = 0x10;
    private const uint GamepadKind = 0x40000;
    private const uint Connected = 3;
    private const int Fallback = 4;
    private const int Keyboard = 5;
    private const int DeviceCount = 6;
    private const int HistoryLimit = 32;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lazy<Hub> Shared = new(() => new Hub());
    private static long _token;

    private static readonly (ushort Host, uint Guest)[] PadButtons =
    [
        (0x0010, 1), (0x0020, 2), (0x1000, 4), (0x2000, 8), (0x4000, 16), (0x8000, 32),
        (0x0001, 64), (0x0002, 128), (0x0004, 256), (0x0008, 512), (0x0100, 1024),
        (0x0200, 2048), (0x0040, 4096), (0x0080, 8192)
    ];

    private static readonly (byte Key, uint Guest)[] FallbackButtons =
    [
        (0x1B, 1), (0x09, 2), (0x0D, 4), (0x08, 8), ((byte)'X', 16), ((byte)'Y', 32),
        (0x26, 64), (0x28, 128), (0x25, 256), (0x27, 512), ((byte)'Q', 1024),
        ((byte)'E', 2048), ((byte)'F', 4096), ((byte)'H', 8192)
    ];

    public static HleModule Create()
    {
        var address = (nuint)(delegate* unmanaged[Stdcall]<nint*, int>)&CreateInput;

        return new HleModule("gameinput", ["gameinput.dll"],
        [
            new HleExport("#1", address),
            new HleExport("GameInputCreate", address)
        ]);
    }

    private static uint Kind(int index)
    {
        return index == Keyboard ? KeyboardKind : GamepadKind;
    }

    private static ulong Now()
    {
        return (ulong)(Clock.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency));
    }

    private static int Fail(nint* output, int error)
    {
        if (output != null)
        {
            *output = 0;
        }

        return error;
    }

    private static int Hand(nint* output, Snapshot? snapshot)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        if (snapshot is null)
        {
            return Fail(output, ReadingNotFound);
        }

        var reading = (Reading*)NativeMemory.AllocZeroed((nuint)sizeof(Reading));

        try
        {
            reading->Table = Shared.Value.ReadingTable;
            reading->References = 1;
            reading->State = GCHandle.ToIntPtr(GCHandle.Alloc(snapshot));
            *output = (nint)reading;

            return 0;
        }
        catch
        {
            NativeMemory.Free(reading);

            return Fail(output, Failure);
        }
    }

    private static Snapshot State(nint reading)
    {
        return (Snapshot)GCHandle.FromIntPtr(((Reading*)reading)->State).Target!;
    }

    private static int Relative(nint reference, uint kind, nint device, nint* output, bool forward)
    {
        if (reference == 0)
        {
            return Fail(output, InvalidArgument);
        }

        var hub = Shared.Value;
        var state = State(reference);

        if (device != 0 && device != hub.Devices[state.Index])
        {
            return Fail(output, InvalidArgument);
        }

        lock (hub.Gate)
        {
            var history = hub.Slots[state.Index].History;

            if (history.Count == 0 || state.Sequence < history[0].Sequence)
            {
                return Fail(output, ReferenceTooOld);
            }

            var found = forward
                ? history.FirstOrDefault(value => value.Sequence > state.Sequence && (value.Kind & kind) != 0)
                : history.LastOrDefault(value => value.Sequence < state.Sequence && (value.Kind & kind) != 0);

            return Hand(output, found);
        }
    }

    private static ulong NextToken(nint output)
    {
        var token = (ulong)Interlocked.Increment(ref _token);

        if (output != 0)
        {
            *(ulong*)output = token;
        }

        return token;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateInput(nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        try
        {
            *output = Shared.Value.Object;
            Console.WriteLine("[input] GameInput initialized.");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[input] Initialization failed: {exception.Message}");

            return Fail(output, Failure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint self, nint iid, nint* output)
    {
        return Fail(output, NoInterface);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint PermanentReference(nint self)
    {
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong Timestamp(nint self)
    {
        return Now();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Current(nint self, uint kind, nint device, nint* output)
    {
        try
        {
            var hub = Shared.Value;

            lock (hub.Gate)
            {
                Snapshot? newest = null;
                var requested = device == 0 ? -1 : Array.IndexOf(hub.Devices, device);

                if (device != 0 && requested < 0)
                {
                    return Fail(output, InvalidArgument);
                }

                if (requested >= 0 && !hub.Slots[requested].Connected)
                {
                    return Fail(output, Disconnected);
                }

                for (var index = 0; index < DeviceCount; index++)
                {
                    var slot = hub.Slots[index];

                    if ((requested >= 0 && requested != index) || !slot.Connected || slot.History.Count == 0
                        || (Kind(index) & kind) == 0)
                    {
                        continue;
                    }

                    var candidate = slot.History[^1];

                    if (newest is null || candidate.Timestamp >= newest.Timestamp)
                    {
                        newest = candidate;
                    }
                }

                return Hand(output, newest);
            }
        }
        catch
        {
            return Fail(output, Failure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Next(nint self, nint reference, uint kind, nint device, nint* output)
    {
        try
        {
            return Relative(reference, kind, device, output, true);
        }
        catch
        {
            return Fail(output, Failure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Previous(nint self, nint reference, uint kind, nint device, nint* output)
    {
        try
        {
            return Relative(reference, kind, device, output, false);
        }
        catch
        {
            return Fail(output, Failure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Temporal(nint self, ulong time, nint device, nint* output)
    {
        try
        {
            var hub = Shared.Value;
            var index = Array.IndexOf(hub.Devices, device);

            if (index < 0)
            {
                return Fail(output, InvalidArgument);
            }

            lock (hub.Gate)
            {
                return Hand(output, hub.Slots[index].History.LastOrDefault(value => value.Timestamp <= time));
            }
        }
        catch
        {
            return Fail(output, Failure);
        }
    }

    // Upstream does not deliver reading, guide-button or keyboard-layout callbacks.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterReading(nint self, nint device, uint kind, float threshold, nint context, nint callback,
        nint output)
    {
        _ = NextToken(output);
        Console.Error.WriteLine("[input] Reading callbacks are not delivered (upstream limitation).");

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterDevice(nint self, nint device, uint kind, uint status, uint enumeration, nint context,
        nint callback, nint output)
    {
        if (callback == 0)
        {
            return InvalidArgument;
        }

        try
        {
            var hub = Shared.Value;
            var registration = new Registration(NextToken(output), device, kind, status, context, callback);
            var events = new List<Notification>();

            lock (hub.Gate)
            {
                hub.Registrations.Add(registration);

                if (enumeration != 0 && (status & Connected) != 0)
                {
                    for (var index = 0; index < DeviceCount; index++)
                    {
                        if (hub.Slots[index].Connected && hub.Matches(registration, index))
                        {
                            events.Add(new Notification(registration, index, Now(), Connected, 0));
                        }
                    }
                }

                if (enumeration == 1)
                {
                    hub.Pending.AddRange(events);
                    events.Clear();
                }
            }

            hub.Deliver(events);

            return 0;
        }
        catch
        {
            return Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterOther(nint self, nint device, nint context, nint callback, nint output)
    {
        _ = NextToken(output);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Stop(nint self, ulong token)
    {
        var hub = Shared.Value;

        lock (hub.Gate)
        {
            hub.Forget(token);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte Unregister(nint self, ulong token, ulong timeout)
    {
        var hub = Shared.Value;
        var start = Now();
        var thread = Environment.CurrentManagedThreadId;

        lock (hub.Gate)
        {
            hub.Forget(token);

            while (hub.Running.Any(value => value.Token == token))
            {
                if (hub.Running.Contains((token, thread)))
                {
                    return 0;
                }

                var elapsed = Now() - start;

                if (elapsed >= timeout)
                {
                    return 0;
                }

                var remaining = timeout - elapsed;
                var milliseconds = (int)Math.Clamp(remaining / 1000, 1UL, int.MaxValue);
                Monitor.Wait(hub.Gate, milliseconds);
            }

            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Dispatcher(nint self, nint* output)
    {
        return Fail(output, NotImplemented);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Aggregate(nint self, uint kind, nint* output)
    {
        return Fail(output, NotImplemented);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindId(nint self, byte* id, nint* output)
    {
        if (id == null || output == null)
        {
            return Fail(output, InvalidArgument);
        }

        foreach (var pointer in Shared.Value.Devices)
        {
            var device = (Device*)pointer;

            if (new ReadOnlySpan<byte>(device->Info.Id, 32).SequenceEqual(new ReadOnlySpan<byte>(id, 32)))
            {
                *output = pointer;

                return 0;
            }
        }

        return Fail(output, DeviceNotFound);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindOther(nint self, nint value, nint* output)
    {
        return Fail(output, DeviceNotFound);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnableOem(nint self, ushort vendor, ushort product, byte iface, byte collection)
    {
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Focus(nint self, uint policy)
    {
        // Upstream uses foreground-window focus for keyboard input.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static DeviceInfo* Info(nint self)
    {
        return &((Device*)self)->Info;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Status(nint self)
    {
        var hub = Shared.Value;

        lock (hub.Gate)
        {
            return hub.Slots[((Device*)self)->Index].Connected ? Connected : 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Rumble(nint self, float* motors)
    {
        var index = ((Device*)self)->Index;

        if (index >= 4)
        {
            return;
        }

        var vibration = new Vibration
        {
            Left = motors == null ? (ushort)0 : (ushort)(Math.Clamp(motors[0], 0, 1) * ushort.MaxValue),
            Right = motors == null ? (ushort)0 : (ushort)(Math.Clamp(motors[1], 0, 1) * ushort.MaxValue)
        };
        _ = XInputSetState((uint)index, &vibration);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unsupported(nint self)
    {
        return NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddReading(nint self)
    {
        return (uint)Interlocked.Increment(ref ((Reading*)self)->References);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseReading(nint self)
    {
        var reading = (Reading*)self;
        var remaining = Interlocked.Decrement(ref reading->References);

        if (remaining == 0)
        {
            GCHandle.FromIntPtr(reading->State).Free();
            NativeMemory.Free(reading);
        }

        return (uint)remaining;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReadingKind(nint self)
    {
        return State(self).Kind;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong Sequence(nint self, uint kind)
    {
        var snapshot = State(self);

        return (snapshot.Kind & kind) != 0 ? snapshot.Sequence : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong ReadingTimestamp(nint self)
    {
        return State(self).Timestamp;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ReadingDevice(nint self, nint* output)
    {
        if (output != null)
        {
            *output = Shared.Value.Devices[State(self).Index];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint KeyCount(nint self)
    {
        return (uint)State(self).Keys.Length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetKeys(nint self, uint count, KeyState* output)
    {
        if (output == null)
        {
            return 0;
        }

        var keys = State(self).Keys;
        var length = (int)Math.Min((uint)keys.Length, count);
        keys.AsSpan(0, length).CopyTo(new Span<KeyState>(output, length));

        return (uint)length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte GetPad(nint self, PadState* output)
    {
        if (output == null || State(self).Pad is not { } pad)
        {
            return 0;
        }

        *output = pad;

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nuint Nothing(nint self)
    {
        return 0;
    }

    private static nint CreateInputTable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(22 * sizeof(nint)));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&Timestamp;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)&Current;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint*, int>)&Next;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint*, int>)&Previous;
        table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, nint, nint*, int>)&Temporal;
        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, float, nint, nint, nint, int>)&RegisterReading;
        table[9] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, nint, nint, int>)&RegisterDevice;
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int>)&RegisterOther;
        table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int>)&RegisterOther;
        table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, void>)&Stop;
        table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, byte>)&Unregister;
        table[14] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Dispatcher;
        table[15] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&Aggregate;
        table[16] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, nint*, int>)&FindId;
        table[17] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&FindOther;
        table[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&FindOther;
        table[19] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&FindOther;
        table[20] = (nint)(delegate* unmanaged[Stdcall]<nint, ushort, ushort, byte, byte, int>)&EnableOem;
        table[21] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void>)&Focus;

        return (nint)table;
    }

    private static nint CreateDeviceTable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(21 * sizeof(nint)));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, DeviceInfo*>)&Info;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Status;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, float*, void>)&Rumble;
        table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[14] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[15] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[16] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[17] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[18] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[19] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;
        table[20] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unsupported;

        return (nint)table;
    }

    private static nint CreateReadingTable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(25 * sizeof(nint)));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddReading;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseReading;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReadingKind;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong>)&Sequence;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&ReadingTimestamp;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&ReadingDevice;
        table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[14] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&KeyCount;
        table[15] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, KeyState*, uint>)&GetKeys;
        table[16] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[17] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[19] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[20] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[21] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[22] = (nint)(delegate* unmanaged[Stdcall]<nint, PadState*, byte>)&GetPad;
        table[23] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;
        table[24] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        return (nint)table;
    }

    public static PadState ConvertPad(ushort buttons, byte leftTrigger, byte rightTrigger, short leftX, short leftY,
        short rightX, short rightY)
    {
        var guest = 0U;

        foreach (var pair in PadButtons)
        {
            if ((buttons & pair.Host) != 0)
            {
                guest |= pair.Guest;
            }
        }

        return new PadState(guest, leftTrigger / 255F, rightTrigger / 255F, Normalize(leftX), Normalize(leftY),
            Normalize(rightX), Normalize(rightY));
    }

    private static float Normalize(short value)
    {
        return Math.Max(-1F, value / 32767F);
    }

    private static PadState FallbackPad(KeyState[] keys)
    {
        var buttons = 0U;

        foreach (var pair in FallbackButtons)
        {
            if (Pressed(keys, pair.Key) != 0)
            {
                buttons |= pair.Guest;
            }
        }

        return new PadState(buttons, Pressed(keys, (byte)'Z'), Pressed(keys, (byte)'C'),
            Pressed(keys, (byte)'D') - Pressed(keys, (byte)'A'), Pressed(keys, (byte)'W') - Pressed(keys, (byte)'S'),
            Pressed(keys, (byte)'L') - Pressed(keys, (byte)'J'), Pressed(keys, (byte)'I') - Pressed(keys, (byte)'K'));
    }

    private static float Pressed(KeyState[] keys, byte key)
    {
        return keys.Any(value => value.VirtualKey == key) ? 1F : 0F;
    }

    private static KeyState[] ReadKeys()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var process);

        if (process != (uint)Environment.ProcessId)
        {
            return [];
        }

        var keys = new List<KeyState>();

        for (var key = 8; key <= 0xFE; key++)
        {
            if (key is >= 0x10 and <= 0x12 || GetAsyncKeyState(key) >= 0)
            {
                continue;
            }

            keys.Add(new KeyState(MapVirtualKeyW((uint)key, 4), 0, (byte)key, 0));
        }

        return keys.ToArray();
    }

    [LibraryImport("xinput1_4.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint XInputGetState(uint index, out XInputState state);

    [LibraryImport("xinput1_4.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint XInputSetState(uint index, Vibration* state);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint MapVirtualKeyW(uint key, uint mode);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    public static void VerifyLayouts()
    {
        if (sizeof(PadState) != 28 || sizeof(KeyState) != 12 || sizeof(DeviceInfo) != 320 || sizeof(XInputState) != 16)
        {
            throw new InvalidOperationException("GameInput ABI layout mismatch.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public record struct PadState(
        uint Buttons,
        float LeftTrigger,
        float RightTrigger,
        float LeftX,
        float LeftY,
        float RightX,
        float RightY);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct KeyState(uint ScanCode, uint CodePoint, byte VirtualKey, byte DeadKey);

    [StructLayout(LayoutKind.Explicit, Size = 320)]
    private struct DeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(32)] public fixed byte Id[32];
        [FieldOffset(64)] public fixed byte RootId[32];
        [FieldOffset(104)] public uint SupportedInput;
        [FieldOffset(108)] public uint RumbleMotors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Device
    {
        public nint Table;
        public int Index;
        public DeviceInfo Info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Reading
    {
        public nint Table;
        public int References;
        public nint State;
    }

    private sealed record Snapshot(int Index, ulong Sequence, ulong Timestamp, PadState? Pad, KeyState[] Keys)
    {
        public uint Kind
        {
            get
            {
                return Index == Keyboard ? KeyboardKind : GamepadKind;
            }
        }
    }

    private sealed class Slot
    {
        public bool Connected;
        public ulong Sequence;

        public List<Snapshot> History
        {
            get;
        } = [];
    }

    private sealed record Registration(ulong Token, nint Device, uint Kind, uint Status, nint Context, nint Callback);

    private sealed record Notification(
        Registration Registration,
        int Index,
        ulong Timestamp,
        uint Current,
        uint Previous);

    private sealed class Hub
    {
        public readonly nint[] Devices = new nint[DeviceCount];
        public readonly object Gate = new();
        public readonly nint Object;
        public readonly List<Notification> Pending = [];
        public readonly nint ReadingTable;
        public readonly List<Registration> Registrations = [];
        public readonly List<(ulong Token, int Thread)> Running = [];
        public readonly Slot[] Slots = Enumerable.Range(0, DeviceCount).Select(_ => new Slot()).ToArray();
        private readonly long[] _retryAt = new long[4];

        public Hub()
        {
            var inputTable = CreateInputTable();
            var deviceTable = CreateDeviceTable();
            ReadingTable = CreateReadingTable();
            var input = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
            *input = inputTable;
            Object = (nint)input;

            for (var index = 0; index < DeviceCount; index++)
            {
                var device = (Device*)NativeMemory.AllocZeroed((nuint)sizeof(Device));
                device->Table = deviceTable;
                device->Index = index;
                device->Info.Size = (uint)sizeof(DeviceInfo);
                device->Info.SupportedInput = Kind(index);
                device->Info.RumbleMotors = index < 4 ? 3U : 0U;
                "victory"u8.CopyTo(new Span<byte>(device->Info.Id, 32));
                device->Info.Id[31] = (byte)(index + 1);
                new ReadOnlySpan<byte>(device->Info.Id, 32).CopyTo(new Span<byte>(device->Info.RootId, 32));
                Devices[index] = (nint)device;
            }

            Poll();
            var thread = new Thread(PollLoop) { IsBackground = true, Name = "irx input poll" };
            thread.Start();
        }

        private void PollLoop()
        {
            while (true)
            {
                Thread.Sleep(8);

                try
                {
                    Poll();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[input] Poll failed: {exception.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        private void Poll()
        {
            var pads = new PadState?[4];
            var now = Environment.TickCount64;

            for (var index = 0; index < pads.Length; index++)
            {
                if (now < _retryAt[index])
                {
                    continue;
                }

                if (XInputGetState((uint)index, out var state) == 0)
                {
                    pads[index] = ConvertPad(state.Buttons, state.LeftTrigger, state.RightTrigger, state.LeftX,
                        state.LeftY, state.RightX, state.RightY);
                }
                else
                {
                    _retryAt[index] = now + 1000;
                }
            }

            var keys = ReadKeys();
            List<Notification> events;

            lock (Gate)
            {
                events = new List<Notification>(Pending);
                Pending.Clear();
                var timestamp = Now();

                for (var index = 0; index < pads.Length; index++)
                {
                    Update(index, pads[index].HasValue, pads[index], [], timestamp, events);
                }

                var useFallback = !pads.Any(pad => pad.HasValue);
                Update(Fallback, useFallback, useFallback ? FallbackPad(keys) : null, [], timestamp, events);
                Update(Keyboard, true, null, keys, timestamp, events);
            }

            Deliver(events);
        }

        private void Update(int index, bool connected, PadState? pad, KeyState[] keys, ulong timestamp,
            List<Notification> events)
        {
            var slot = Slots[index];
            var previous = slot.Connected;
            slot.Connected = connected;
            var last = slot.History.Count == 0 ? null : slot.History[^1];

            if (connected && (!previous || last is null || last.Pad != pad || !last.Keys.AsSpan().SequenceEqual(keys)))
            {
                slot.History.Add(new Snapshot(index, ++slot.Sequence, timestamp, pad, keys));

                if (slot.History.Count > HistoryLimit)
                {
                    slot.History.RemoveAt(0);
                }
            }

            if (previous == connected)
            {
                return;
            }

            foreach (var registration in Registrations)
            {
                if (Matches(registration, index) && (registration.Status & Connected) != 0)
                {
                    events.Add(new Notification(registration, index, timestamp, connected ? Connected : 0,
                        previous ? Connected : 0));
                }
            }
        }

        public bool Matches(Registration registration, int index)
        {
            return (registration.Kind & Kind(index)) != 0
                   && (registration.Device == 0 || registration.Device == Devices[index]);
        }

        public void Deliver(IEnumerable<Notification> events)
        {
            var thread = Environment.CurrentManagedThreadId;

            foreach (var notification in events)
            {
                var registration = notification.Registration;

                lock (Gate)
                {
                    if (!Registrations.Any(value => value.Token == registration.Token))
                    {
                        continue;
                    }

                    Running.Add((registration.Token, thread));
                }

                try
                {
                    var callback =
                        (delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void>)registration.Callback;
                    callback(registration.Token, registration.Context, Devices[notification.Index],
                        notification.Timestamp, notification.Current, notification.Previous);
                }
                finally
                {
                    lock (Gate)
                    {
                        Running.Remove((registration.Token, thread));
                        Monitor.PulseAll(Gate);
                    }
                }
            }
        }

        public void Forget(ulong token)
        {
            Registrations.RemoveAll(value => value.Token == token);
            Pending.RemoveAll(value => value.Registration.Token == token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration
    {
        public ushort Left;
        public ushort Right;
    }
}
