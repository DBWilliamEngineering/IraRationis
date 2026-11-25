// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-hle/src/modules/dsp.rs.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.HLE;

public static unsafe class DspModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int PointerError = unchecked((int)0x80004003);
    private const int OutOfMemory = unchecked((int)0x8007000E);

    public static HleModule Create()
    {
        return new HleModule("dsp", ["chad.dll"],
        [
            new HleExport("XDspConnectWithMaximumStreamLimit", (nuint)(delegate* unmanaged[Stdcall]<void*, uint, uint, uint, uint, Dsp**, int>)&Connect),
            new HleExport("XDspDisconnect", (nuint)(delegate* unmanaged[Stdcall]<Dsp*, int>)&Disconnect),
            new HleExport("XDspActivate", (nuint)(delegate* unmanaged[Stdcall]<Dsp*, StreamDescription*, Status**, Stream**, int>)&Activate),
            new HleExport("XDspDeactivate", (nuint)(delegate* unmanaged[Stdcall]<Stream*, int>)&Deactivate),
            new HleExport("XDspSubmitCommand", (nuint)(delegate* unmanaged[Stdcall]<Stream*, Command*, uint*, int>)&Submit)
        ]);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dsp
    {
        public uint MaxStreams;
        public int Active;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StreamDescription
    {
        public uint Kind;
        public uint Options;
        public uint Frames;
        public uint Channels;
        public uint ImpulseResponseFloats;
        public float* ImpulseResponse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Submitted;
        public uint Completed;
        public uint Reserved;
        public int Result;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stream
    {
        public Dsp* Owner;
        public uint Frames;
        public uint Channels;
        public Status Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Command
    {
        public float GainLeft;
        public float GainRight;
        public float* Input;
        public float* Output;
        public ulong Reserved;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Connect(void* memory, uint length, uint irSeconds, uint maxStreams, uint maxChannels, Dsp** output)
    {
        return ConnectCore(memory, length, irSeconds, maxStreams, maxChannels, output);
    }

    public static int ConnectCore(void* memory, uint length, uint irSeconds, uint maxStreams, uint maxChannels, Dsp** output)
    {
        if (memory == null || length is < 0x10000 or > 0x1F400000 || irSeconds > 128 || maxStreams > 256 || maxChannels > 2)
        {
            return InvalidArgument;
        }

        if (output == null)
        {
            return PointerError;
        }

        *output = null;

        try
        {
            var dsp = (Dsp*)NativeMemory.AllocZeroed((nuint)sizeof(Dsp));
            dsp->MaxStreams = maxStreams;
            *output = dsp;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Disconnect(Dsp* dsp)
    {
        return DisconnectCore(dsp);
    }

    public static int DisconnectCore(Dsp* dsp)
    {
        if (dsp == null || Volatile.Read(ref dsp->Active) != 0)
        {
            return InvalidArgument;
        }

        NativeMemory.Free(dsp);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Activate(Dsp* dsp, StreamDescription* description, Status** status, Stream** stream)
    {
        return ActivateCore(dsp, description, status, stream);
    }

    public static int ActivateCore(Dsp* dsp, StreamDescription* description, Status** status, Stream** stream)
    {
        if (status == null || stream == null)
        {
            return PointerError;
        }

        *status = null;
        *stream = null;

        if (dsp == null || description == null)
        {
            return InvalidArgument;
        }

        var value = *description;
        var shape = value.Kind == 1
            ? (value.Channels is 1 or 2) && (value.Frames is 0x200 or 0x400) && (value.Options & 4) == 0
            : value.Channels == 1 && (value.Frames is 0x200 or 0x400 or 0x800);

        if (!shape || ((value.Options & 10) != 0 && value.Channels != 2))
        {
            return InvalidArgument;
        }

        while (true)
        {
            var active = Volatile.Read(ref dsp->Active);

            if (active >= dsp->MaxStreams)
            {
                return OutOfMemory;
            }

            if (Interlocked.CompareExchange(ref dsp->Active, active + 1, active) == active)
            {
                break;
            }
        }

        try
        {
            var created = (Stream*)NativeMemory.AllocZeroed((nuint)sizeof(Stream));
            created->Owner = dsp;
            created->Frames = value.Frames;
            created->Channels = value.Channels;
            *status = &created->Status;
            *stream = created;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            _ = Interlocked.Decrement(ref dsp->Active);

            return OutOfMemory;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Deactivate(Stream* stream)
    {
        return DeactivateCore(stream);
    }

    public static int DeactivateCore(Stream* stream)
    {
        if (stream == null)
        {
            return InvalidArgument;
        }

        _ = Interlocked.Decrement(ref stream->Owner->Active);
        NativeMemory.Free(stream);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Submit(Stream* stream, Command* command, uint* ticket)
    {
        return SubmitCore(stream, command, ticket);
    }

    public static int SubmitCore(Stream* stream, Command* command, uint* ticket)
    {
        if (stream == null || command == null)
        {
            return InvalidArgument;
        }

        // Preserve upstream's limitation: convolution format is unknown, so reverb returns silence.
        if (command->Output != null)
        {
            NativeMemory.Clear(command->Output, (nuint)(stream->Frames * stream->Channels * sizeof(float)));
        }

        var next = unchecked(stream->Status.Submitted + 1);
        Volatile.Write(ref stream->Status.Submitted, next);
        Volatile.Write(ref stream->Status.Result, 0);
        Volatile.Write(ref stream->Status.Completed, next);

        if (ticket != null)
        {
            *ticket = next;
        }

        return 0;
    }
}
