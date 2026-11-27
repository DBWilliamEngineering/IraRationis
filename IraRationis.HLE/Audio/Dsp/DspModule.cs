// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-hle/src/modules/dsp.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Audio.Dsp.Models;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Audio.Dsp;

public static unsafe class DspModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int PointerError = unchecked((int)0x80004003);
    private const int OutOfMemory = unchecked((int)0x8007000E);

    public static HleModule Create()
    {
        return new HleModule("dsp", ["chad.dll"],
        [
            new HleExport("XDspConnectWithMaximumStreamLimit",
                (nuint)(delegate* unmanaged[Stdcall]<void*, uint, uint, uint, uint, DspHandle**, int>)&Connect),
            new HleExport("XDspDisconnect", (nuint)(delegate* unmanaged[Stdcall]<DspHandle*, int>)&Disconnect),
            new HleExport("XDspActivate",
                (nuint)(delegate* unmanaged[Stdcall]<DspHandle*, DspStreamDescription*, DspStatus**, DspStream**, int>)&Activate),
            new HleExport("XDspDeactivate", (nuint)(delegate* unmanaged[Stdcall]<DspStream*, int>)&Deactivate),
            new HleExport("XDspSubmitCommand",
                (nuint)(delegate* unmanaged[Stdcall]<DspStream*, DspCommand*, uint*, int>)&Submit)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Connect(void* memory, uint length, uint irSeconds, uint maxStreams, uint maxChannels,
        DspHandle** output)
    {
        return ConnectCore(memory, length, irSeconds, maxStreams, maxChannels, output);
    }

    public static int ConnectCore(void* memory, uint length, uint irSeconds, uint maxStreams, uint maxChannels,
        DspHandle** output)
    {
        if (memory == null || length is < 0x10000 or > 0x1F400000 || irSeconds > 128 || maxStreams > 256
            || maxChannels > 2)
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
            var dsp = (DspHandle*)NativeMemory.AllocZeroed((nuint)sizeof(DspHandle));

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
    private static int Disconnect(DspHandle* dsp)
    {
        return DisconnectCore(dsp);
    }

    public static int DisconnectCore(DspHandle* dsp)
    {
        if (dsp == null || Volatile.Read(ref dsp->Active) != 0)
        {
            return InvalidArgument;
        }

        NativeMemory.Free(dsp);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Activate(DspHandle* dsp, DspStreamDescription* description, DspStatus** status, DspStream** stream)
    {
        return ActivateCore(dsp, description, status, stream);
    }

    public static int ActivateCore(DspHandle* dsp, DspStreamDescription* description, DspStatus** status, DspStream** stream)
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
            ? value.Channels is 1 or 2 && value.Frames is 0x200 or 0x400 && (value.Options & 4) == 0
            : value.Channels == 1 && value.Frames is 0x200 or 0x400 or 0x800;

        if (!shape || ((value.Options & 10) != 0 && value.Channels != 2))
        {
            return InvalidArgument;
        }

        for(;;)
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
            var created = (DspStream*)NativeMemory.AllocZeroed((nuint)sizeof(DspStream));

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
    private static int Deactivate(DspStream* stream)
    {
        return DeactivateCore(stream);
    }

    public static int DeactivateCore(DspStream* stream)
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
    private static int Submit(DspStream* stream, DspCommand* command, uint* ticket)
    {
        return SubmitCore(stream, command, ticket);
    }

    public static int SubmitCore(DspStream* stream, DspCommand* command, uint* ticket)
    {
        if (stream == null || command == null)
        {
            return InvalidArgument;
        }

        // Preserve upstream's limitation: convolution format is unknown, so reverb returns silence.
        if (command->Output != null)
        {
            NativeMemory.Clear(command->Output, stream->Frames * stream->Channels * sizeof(float));
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
