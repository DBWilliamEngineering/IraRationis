// SPDX-License-Identifier: GPL-3.0-or-later
// ACP object ABI and message layout cross-checked against VIctory/victory-fork audio.rs.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Audio.Acp.Models;

namespace IraRationis.HLE.Audio.Acp;

public static unsafe partial class AcpHalModule
{
    private const int NotImplemented = unchecked((int)0x80004001);
    private const uint RegisterMessages = 2;
    private const uint UnregisterMessages = 3;
    internal const uint CommandCompleted = 0x10;
    private const uint Disconnected = 0x80;
    private const uint MaxMessageCapacity = 1_048_576;
    private static readonly ConcurrentDictionary<nint, AcpHalAcpConnection> Connections = new();
    private static readonly nint AcpTable = CreateAcpTable();
    private static long _reportedUnsupportedCommands;
    private static int _reportedOtherCommand;
    private static int _reportedCreate;

    private static nint CreateAcpTable()
    {
        if (sizeof(AcpHalAcpMessage) != 28)
        {
            throw new InvalidOperationException("ACP message ABI layout mismatch.");
        }

        // Process-lifetime immutable native table; objects own only one pointer.
        var table = (nint*)NativeMemory.Alloc(6, (nuint)sizeof(nint));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, int>)&ConnectAcp;

        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&DisconnectAcp;

        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, uint, void*, uint, int>)&SubmitAcp;

        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, AcpHalAcpMessage*, byte>)&PopAcpMessage;

        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&CountAcpMessages;

        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&ReleaseAcp;

        return (nint)table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateAcp(nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        var memory = (nint)0;

        try
        {
            var state = new AcpHalAcpConnection();

            memory = (nint)NativeMemory.Alloc((nuint)sizeof(nint));

            *(nint*)memory = AcpTable;

            if (!Connections.TryAdd(memory, state))
            {
                return Failure;
            }

            *output = memory;

            memory = 0;

            if (Interlocked.Exchange(ref _reportedCreate, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[audio] IRX-ACP-R7: ACP control interface created (no audio decoder or mixer).");
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        finally
        {
            NativeMemory.Free((void*)memory);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ConnectAcp(nint self, uint commands, uint messages)
    {
        if (commands == 0 || messages > MaxMessageCapacity || !Connections.TryGetValue(self, out var state))
        {
            return InvalidArgument;
        }

        lock (state.Gate)
        {
            if (state.Released || state.Connected)
            {
                return Failure;
            }

            try
            {
                state.Messages = messages == 0 ? [] : new AcpHalAcpMessage[(int)messages];
            }
            catch (OutOfMemoryException)
            {
                return OutOfMemory;
            }

            state.Mask = 0;

            state.Dropped = 0;

            state.Head = 0;

            state.Count = 0;

            state.Connected = true;
        }

        Console.Error.WriteLine(
            $"[audio] ACP connected: commands={commands}, messages={messages} (immediate control commands only).");

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisconnectAcp(nint self)
    {
        if (!Connections.TryGetValue(self, out var state))
        {
            return InvalidArgument;
        }

        lock (state.Gate)
        {
            if (state.Released || !state.Connected)
            {
                return Failure;
            }

            state.Post(Disconnected);

            state.Connected = false;

            return 0;
        }
    }

    // Caller holds HeapGate until the notification is published. This keeps
    // the entire aligned word within a live allocation and prevents free races.
    private static bool TryGetNotificationWord(uint address, out uint* word)
    {
        word = null;

        var index = FloorIndex(ByDevice, address);

        if (index < 0 || (address & 3) != 0)
        {
            return false;
        }

        var block = ByDevice.GetValueAtIndex(index);
        var offset = address - block.Device;

        if (block.Size < sizeof(uint) || offset > block.Size - sizeof(uint))
        {
            return false;
        }

        word = (uint*)(block.Host + offset);

        return true;
    }

    private static void ReportUnsupportedCommand(uint command, uint frame)
    {
        var first = command < 64
            ? (Interlocked.Or(ref _reportedUnsupportedCommands, 1L << (int)command) & (1L << (int)command)) == 0
            : Interlocked.Exchange(ref _reportedOtherCommand, 1) == 0;

        if (first)
        {
            Console.Error.WriteLine(
                $"[audio] ACP command {command} (frame={frame}) not implemented; no completion or notification published.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SubmitAcp(nint self, uint command, ulong id, uint frame, void* data, uint notification)
    {
        if (!Connections.TryGetValue(self, out var state))
        {
            return InvalidArgument;
        }

        lock (state.Gate)
        {
            if (state.Released || !state.Connected)
            {
                return Failure;
            }

            // Do not claim DSP, XMA or flowgraph execution. Only message
            // registration has real execution semantics in this revision.
            if (command is not (RegisterMessages or UnregisterMessages))
            {
                ReportUnsupportedCommand(command, frame);

                return NotImplemented;
            }

            if (data == null)
            {
                return InvalidArgument;
            }

            var mask = Unsafe.ReadUnaligned<uint>(data);

            if (notification == 0)
            {
                CompleteRegistration(state, command, id, mask);

                return 0;
            }

            lock (HeapGate)
            {
                if (!TryGetNotificationWord(notification, out var word))
                {
                    return InvalidArgument;
                }

                CompleteRegistration(state, command, id, mask);

                Volatile.Write(ref *word, 1U);

                return 0;
            }
        }
    }

    private static void CompleteRegistration(AcpHalAcpConnection state, uint command, ulong id, uint mask)
    {
        state.Mask = command == RegisterMessages ? state.Mask | mask : state.Mask & ~mask;

        if (id != 0)
        {
            state.Post(CommandCompleted, command, id);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte PopAcpMessage(nint self, AcpHalAcpMessage* output)
    {
        if (!Connections.TryGetValue(self, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            if (state.Released || state.Count == 0)
            {
                return 0;
            }

            if (output != null)
            {
                Unsafe.WriteUnaligned(output, state.Messages[state.Head]);
            }

            state.Head++;

            if (state.Head == state.Messages.Length)
            {
                state.Head = 0;
            }

            state.Count--;

            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CountAcpMessages(nint self)
    {
        if (!Connections.TryGetValue(self, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.Released ? 0 : (uint)state.Count;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ReleaseAcp(nint self)
    {
        if (!Connections.TryRemove(self, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.Released = true;

            state.Connected = false;

            state.Messages = [];

            state.Count = 0;

            NativeMemory.Free((void*)self);
        }
    }
}
