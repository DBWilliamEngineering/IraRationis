// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Runtime.InteropServices;
using IraRationis.HLE.Input.Diagnostics.Models;
using IraRationis.HLE.Input.Models;

namespace IraRationis.HLE.Input;

internal sealed unsafe class GameInputHub
{
    public readonly nint[] Devices = new nint[GameInputModule.DeviceCount];
    public readonly object Gate = new();
    public readonly nint Object;
    public readonly List<GameInputNotification> Pending = [];
    public readonly nint ReadingTable;
    public readonly List<GameInputRegistration> Registrations = [];
    public readonly List<(ulong Token, int Thread)> Running = [];
    public readonly GameInputSlot[] Slots = Enumerable.Range(0, GameInputModule.DeviceCount)
        .Select(_ => new GameInputSlot()).ToArray();
    private readonly DualSenseProvider? _dualSense;
    private readonly long[] _retryAt = new long[4];
    private bool _usingDualSense;


    public GameInputHub()
    {
        var inputTable = GameInputModule.CreateInputTable();
        var deviceTable = GameInputModule.CreateDeviceTable();

        ReadingTable = GameInputModule.CreateReadingTable();

        var input = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));

        *input = inputTable;

        Object = (nint)input;

        for (var index = 0; index < GameInputModule.DeviceCount; index++)
        {
            var device = (GameInputDevice*)NativeMemory.AllocZeroed((nuint)sizeof(GameInputDevice));

            device->Table = deviceTable;

            device->Index = index;

            device->Info.Size = (uint)sizeof(GameInputDeviceInfo);

            device->Info.SupportedInput = GameInputModule.Kind(index);

            device->Info.RumbleMotors = index < 4 ? 3U : 0U;

            "victory"u8.CopyTo(new Span<byte>(device->Info.Id, 32));

            device->Info.Id[31] = (byte)(index + 1);

            new ReadOnlySpan<byte>(device->Info.Id, 32).CopyTo(new Span<byte>(device->Info.RootId, 32));

            Devices[index] = (nint)device;
        }

        if (Environment.GetEnvironmentVariable("IRX_INPUT_DUALSENSE") != "0")
        {
            _dualSense = new DualSenseProvider();
        }

        Poll();

        var thread = new Thread(PollLoop) { IsBackground = true, Name = "irx input poll" };

        thread.Start();
    }

    private void PollLoop()
    {
        for (;;)
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
        var pads = new GameInputPadState?[4];
        var now = Environment.TickCount64;

        for (var index = 0; index < pads.Length; index++)
        {
            if (now < _retryAt[index])
            {
                continue;
            }

            if (GameInputModule.XInputGetState((uint)index, out var state) == 0)
            {
                pads[index] = GameInputModule.ConvertPad(state.Buttons, state.LeftTrigger, state.RightTrigger,
                    state.LeftX,
                    state.LeftY, state.RightX, state.RightY);
            }
            else
            {
                _retryAt[index] = now + 1000;
            }
        }

        var keys = GameInputModule.ReadKeys();
        // Prefer existing XInput devices to avoid duplicate events from a virtual controller.
        var dualSense = pads.Any(pad => pad.HasValue) ? null : _dualSense?.Current;

        List<GameInputNotification> events;

        lock (Gate)
        {
            events = new List<GameInputNotification>(Pending);

            Pending.Clear();

            var timestamp = GameInputModule.Now();

            for (var index = 0; index < pads.Length; index++)
            {
                Update(index, pads[index].HasValue, pads[index], [], timestamp, events);
            }

            // Keep the same virtual controller identity when moving from keyboard to native HID.
            var useFallback = !pads.Any(pad => pad.HasValue);

            if (_usingDualSense != dualSense.HasValue)
            {
                _usingDualSense = dualSense.HasValue;

                Console.WriteLine(
                    $"[input] IRX-R30-ROUTE: slot={GameInputModule.Fallback}; source={(_usingDualSense ? "DualSense USB" : "keyboard/XInput")}");
            }

            Update(GameInputModule.Fallback, useFallback,
                useFallback ? dualSense ?? GameInputModule.FallbackPad(keys) : null, [], timestamp,
                events);

            Update(GameInputModule.Keyboard, true, null, keys, timestamp, events);
        }

        Deliver(events);
    }

    private void Update(int index, bool connected, GameInputPadState? pad, GameInputKeyState[] keys, ulong timestamp,
        List<GameInputNotification> events)
    {
        var slot = Slots[index];
        var previous = slot.Connected;

        slot.Connected = connected;

        var last = slot.History.Count == 0 ? null : slot.History[^1];

        if (connected && (!previous || last is null || last.Pad != pad || !last.Keys.AsSpan().SequenceEqual(keys)))
        {
            slot.History.Add(new GameInputSnapshot(index, ++slot.Sequence, timestamp, pad, keys));

            if (slot.History.Count > GameInputModule.HistoryLimit)
            {
                slot.History.RemoveAt(0);
            }
        }

        if (previous == connected)
        {
            return;
        }

        if (GameInputModule.TraceInput)
        {
            GameInputModule.InputDiagnostics.Write(InputDiagnosticCategory.DeviceConnection,
                (Slot: index, Connected: connected, Timestamp: timestamp),
                static connection => $"slot={connection.Slot}; connected={connection.Connected}; "
                                     + $"inputTimestamp={connection.Timestamp}");
        }

        foreach (var registration in Registrations)
        {
            if (Matches(registration, index) && (registration.Status & GameInputModule.Connected) != 0)
            {
                events.Add(new GameInputNotification(registration, index, timestamp,
                    connected ? GameInputModule.Connected : 0,
                    previous ? GameInputModule.Connected : 0));
            }
        }
    }

    public bool Matches(GameInputRegistration registration, int index)
    {
        return (registration.Kind & GameInputModule.Kind(index)) != 0
               && (registration.Device == 0 || registration.Device == Devices[index]);
    }

    public void Deliver(IEnumerable<GameInputNotification> events)
    {
        var thread = Environment.CurrentManagedThreadId;

        foreach (var notification in events)
        {
            var registration = notification.Registration;

            lock (Gate)
            {
                if (!Registrations.Any(value => value.Token == registration.Token))
                {
                    ReportCallback(InputDiagnosticCategory.DeviceCallbackSkipped, notification);

                    continue;
                }

                Running.Add((registration.Token, thread));
            }

            try
            {
                var callback =
                    (delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void>)registration.Callback;

                ReportCallback(InputDiagnosticCategory.DeviceCallbackEnter, notification);

                callback(registration.Token, registration.Context, Devices[notification.Index],
                    notification.Timestamp, notification.Current, notification.Previous);

                ReportCallback(InputDiagnosticCategory.DeviceCallbackReturn, notification);
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

    private void ReportCallback(InputDiagnosticCategory category, GameInputNotification notification)
    {
        if (GameInputModule.TraceInput)
        {
            GameInputModule.InputDiagnostics.Write(category,
                (Notification: notification, Device: Devices[notification.Index]),
                static delivery => $"token={delivery.Notification.Registration.Token}; "
                                   + $"callback=0x{delivery.Notification.Registration.Callback:X}; "
                                   + $"context=0x{delivery.Notification.Registration.Context:X}; "
                                   + $"slot={delivery.Notification.Index}; device=0x{delivery.Device:X}; "
                                   + $"inputTimestamp={delivery.Notification.Timestamp}; "
                                   + $"status=0x{delivery.Notification.Current:X}; previousStatus=0x{delivery.Notification.Previous:X}");
        }
    }

    public void Forget(ulong token)
    {
        Registrations.RemoveAll(value => value.Token == token);

        Pending.RemoveAll(value => value.Registration.Token == token);
    }
}
