// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.ConsoleGraphicsDevice;
using IraRationis.HLE.Input.Diagnostics.Models;
using IraRationis.HLE.Input.Models;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Input.GameInputModule;

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
    internal const uint KeyboardKind = 0x10;
    internal const uint GamepadKind = 0x40000;
    internal const uint Connected = 3;
    internal const int Fallback = 4;
    internal const int Keyboard = 5;
    internal const int DeviceCount = 6;
    internal const int HistoryLimit = 32;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lazy<GameInputHub> Shared = new(() => new GameInputHub());
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

    internal static uint Kind(int index)
    {
        return index == Keyboard ? KeyboardKind : GamepadKind;
    }

    internal static ulong Now()
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

    private static int Hand(nint* output, GameInputSnapshot? snapshot)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        if (snapshot is null)
        {
            return Fail(output, ReadingNotFound);
        }

        var reading = (GameInputReading*)NativeMemory.AllocZeroed((nuint)sizeof(GameInputReading));

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

    private static GameInputSnapshot State(nint reading)
    {
        return (GameInputSnapshot)GCHandle.FromIntPtr(((GameInputReading*)reading)->State).Target!;
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
                GameInputSnapshot? newest = null;

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

                if (TraceInput)
                {
                    InputDiagnostics.Write(InputDiagnosticCategory.CurrentReading,
                        (Kind: kind, Device: device, Requested: requested, Selected: newest?.Index ?? -1),
                        static reading => $"kind=0x{reading.Kind:X}; device=0x{reading.Device:X}; "
                                          + $"requestedSlot={reading.Requested}; selectedSlot={reading.Selected}");
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

    //TODO: Damon William William -> Correlate reading registrations with the account-picker stall; successful registration currently delivers no readings.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterReading(nint self, nint device, uint kind, float threshold, nint context, nint callback,
        nint output)
    {
        var token = NextToken(output);

        if (TraceInput)
        {
            InputDiagnostics.Write(InputDiagnosticCategory.ReadingRegistration,
                (Token: token, Device: device, Kind: kind, Callback: callback, Context: context),
                static registration => $"token={registration.Token}; device=0x{registration.Device:X}; "
                                       + $"kind=0x{registration.Kind:X}; callback=0x{registration.Callback:X}; "
                                       + $"context=0x{registration.Context:X}; "
                                       + "result=0x00000000; delivered=false; reason=reading-callback-not-implemented");
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterDevice(nint self, nint device, uint kind, uint status, uint enumeration, nint context,
        nint callback, nint output)
    {
        if (callback == 0)
        {
            if (TraceInput)
            {
                InputDiagnostics.Write(InputDiagnosticCategory.DeviceRegistrationFailure,
                    (Device: device, Kind: kind),
                    static registration => $"device=0x{registration.Device:X}; kind=0x{registration.Kind:X}; "
                                           + "result=0x80070057; reason=null-callback");
            }

            return InvalidArgument;
        }

        try
        {
            var hub = Shared.Value;
            var registration = new GameInputRegistration(NextToken(output), device, kind, status, context, callback);
            var events = new List<GameInputNotification>();
            var enumerationCount = 0;

            lock (hub.Gate)
            {
                hub.Registrations.Add(registration);

                if (enumeration != 0 && (status & Connected) != 0)
                {
                    for (var index = 0; index < DeviceCount; index++)
                    {
                        if (hub.Slots[index].Connected && hub.Matches(registration, index))
                        {
                            events.Add(new GameInputNotification(registration, index, Now(), Connected, 0));
                        }
                    }
                }

                if (enumeration == 1)
                {
                    enumerationCount = events.Count;
                    hub.Pending.AddRange(events);

                    events.Clear();
                }

                if (TraceInput)
                {
                    InputDiagnostics.Write(InputDiagnosticCategory.DeviceRegistration,
                        (Registration: registration, Enumeration: enumeration, Immediate: events.Count,
                            Queued: enumerationCount),
                        static request =>
                            $"token={request.Registration.Token}; device=0x{request.Registration.Device:X}; "
                            + $"kind=0x{request.Registration.Kind:X}; status=0x{request.Registration.Status:X}; "
                            + $"callback=0x{request.Registration.Callback:X}; context=0x{request.Registration.Context:X}; "
                            + $"enumeration={request.Enumeration}; immediate={request.Immediate}; "
                            + $"queued={request.Queued}; phase=registered");
                }
            }

            hub.Deliver(events);

            return 0;
        }
        catch
        {
            if (TraceInput)
            {
                InputDiagnostics.Write(InputDiagnosticCategory.DeviceRegistrationFailure,
                    (Device: device, Kind: kind),
                    static registration => $"device=0x{registration.Device:X}; kind=0x{registration.Kind:X}; "
                                           + "result=0x80004005; reason=registration-or-delivery-exception");
            }

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
            if (TraceInput)
            {
                InputDiagnostics.Write(InputDiagnosticCategory.FindDeviceFailure,
                    (Id: (nuint)id, Output: (nuint)output),
                    static request => $"idAddress=0x{request.Id:X}; outputAddress=0x{request.Output:X}; "
                                      + "result=0x80070057; reason=null-argument");
            }

            return Fail(output, InvalidArgument);
        }

        foreach (var pointer in Shared.Value.Devices)
        {
            var device = (GameInputDevice*)pointer;

            if (new ReadOnlySpan<byte>(device->Info.Id, 32).SequenceEqual(new ReadOnlySpan<byte>(id, 32)))
            {
                *output = pointer;

                if (TraceInput)
                {
                    InputDiagnostics.Write(InputDiagnosticCategory.FindDeviceSuccess,
                        (Id: (nuint)id, Device: pointer, Slot: device->Index),
                        static match => $"idAddress=0x{match.Id:X}; device=0x{match.Device:X}; "
                                        + $"slot={match.Slot}; result=0x00000000");
                }

                return 0;
            }
        }

        if (TraceInput)
        {
            InputDiagnostics.Write(InputDiagnosticCategory.FindDeviceFailure, (nuint)id,
                static address => $"idAddress=0x{address:X}; result=0x838A0002; reason=id-not-found");
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
    private static GameInputDeviceInfo* Info(nint self)
    {
        return &((GameInputDevice*)self)->Info;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Status(nint self)
    {
        var hub = Shared.Value;

        lock (hub.Gate)
        {
            return hub.Slots[((GameInputDevice*)self)->Index].Connected ? Connected : 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Rumble(nint self, float* motors)
    {
        var index = ((GameInputDevice*)self)->Index;

        if (index >= 4)
        {
            return;
        }

        var vibration = new GameInputVibration
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
        return (uint)Interlocked.Increment(ref ((GameInputReading*)self)->References);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseReading(nint self)
    {
        var reading = (GameInputReading*)self;
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
    private static uint GetKeys(nint self, uint count, GameInputKeyState* output)
    {
        if (output == null)
        {
            return 0;
        }

        var keys = State(self).Keys;
        var length = (int)Math.Min((uint)keys.Length, count);

        keys.AsSpan(0, length).CopyTo(new Span<GameInputKeyState>(output, length));

        return (uint)length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte GetPad(nint self, GameInputPadState* output)
    {
        if (output == null)
        {
            return 0;
        }

        var snapshot = State(self);

        if (snapshot.Pad is not { } pad)
        {
            return 0;
        }

        *output = pad;

        if ((pad.Buttons & 32U) != 0)
        {
            ConsoleGraphicsDevice.RequestInputColorTrace();
        }

        if (TraceInput)
        {
            var observation = Interlocked.Increment(ref _padReads);
            var previous = Interlocked.Exchange(ref GuestButtons[snapshot.Index], unchecked((int)pad.Buttons));
            var category = GuestReadingCategory(unchecked((uint)previous), pad.Buttons);

            InputDiagnostics.Write(category,
                (Observation: observation, Snapshot: snapshot, pad.Buttons),
                static reading => $"reads={reading.Observation}; slot={reading.Snapshot.Index}; "
                                  + $"sequence={reading.Snapshot.Sequence}; inputTimestamp={reading.Snapshot.Timestamp}; "
                                  + $"buttons=0x{reading.Buttons:X8}; Y={(reading.Buttons & 32) != 0}");
        }

        return 1;
    }

    private static InputDiagnosticCategory GuestReadingCategory(uint previousButtons, uint currentButtons)
    {
        if (previousButtons == currentButtons)
        {
            return InputDiagnosticCategory.GuestReading;
        }

        if (((previousButtons ^ currentButtons) & 32) == 0)
        {
            return InputDiagnosticCategory.GuestButtonsChanged;
        }

        return (currentButtons & 32) != 0
            ? InputDiagnosticCategory.GuestYPressed
            : InputDiagnosticCategory.GuestYReleased;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nuint Nothing(nint self)
    {
        return 0;
    }

    internal static nint CreateInputTable()
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

    internal static nint CreateDeviceTable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(21 * sizeof(nint)));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&Query;

        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;

        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&PermanentReference;

        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, GameInputDeviceInfo*>)&Info;

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

    internal static nint CreateReadingTable()
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

        table[15] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, GameInputKeyState*, uint>)&GetKeys;

        table[16] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[17] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[19] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[20] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[21] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[22] = (nint)(delegate* unmanaged[Stdcall]<nint, GameInputPadState*, byte>)&GetPad;

        table[23] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        table[24] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint>)&Nothing;

        return (nint)table;
    }

    public static GameInputPadState ConvertPad(ushort buttons, byte leftTrigger, byte rightTrigger, short leftX,
        short leftY,
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

        return new GameInputPadState(guest, leftTrigger / 255F, rightTrigger / 255F, Normalize(leftX), Normalize(leftY),
            Normalize(rightX), Normalize(rightY));
    }

    private static float Normalize(short value)
    {
        return Math.Max(-1F, value / 32767F);
    }

    internal static GameInputPadState FallbackPad(GameInputKeyState[] keys)
    {
        var buttons = 0U;

        foreach (var pair in FallbackButtons)
        {
            if (Pressed(keys, pair.Key) != 0)
            {
                buttons |= pair.Guest;
            }
        }

        return new GameInputPadState(buttons, Pressed(keys, (byte)'Z'), Pressed(keys, (byte)'C'),
            Pressed(keys, (byte)'D') - Pressed(keys, (byte)'A'), Pressed(keys, (byte)'W') - Pressed(keys, (byte)'S'),
            Pressed(keys, (byte)'L') - Pressed(keys, (byte)'J'), Pressed(keys, (byte)'I') - Pressed(keys, (byte)'K'));
    }

    private static float Pressed(GameInputKeyState[] keys, byte key)
    {
        return keys.Any(value => value.VirtualKey == key) ? 1F : 0F;
    }

    internal static GameInputKeyState[] ReadKeys()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var process);

        if (process != (uint)Environment.ProcessId)
        {
            return [];
        }

        var keys = new List<GameInputKeyState>();

        for (var key = 8; key <= 0xFE; key++)
        {
            if (key is >= 0x10 and <= 0x12 || GetAsyncKeyState(key) >= 0)
            {
                continue;
            }

            keys.Add(new GameInputKeyState(MapVirtualKeyW((uint)key, 4), 0, (byte)key, 0));
        }

        return keys.ToArray();
    }

    [LibraryImport("xinput1_4.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint XInputGetState(uint index, out GameInputXInputState state);

    [LibraryImport("xinput1_4.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint XInputSetState(uint index, GameInputVibration* state);

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
        if (sizeof(GameInputPadState) != 28 || sizeof(GameInputKeyState) != 12 || sizeof(GameInputDeviceInfo) != 320
            || sizeof(GameInputXInputState) != 16)
        {
            throw new InvalidOperationException("GameInput ABI layout mismatch.");
        }

        VerifyDualSense();
    }
}
