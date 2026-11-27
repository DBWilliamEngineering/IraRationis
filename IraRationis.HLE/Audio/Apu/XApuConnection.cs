// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;
using IraRationis.HLE.Audio.Apu.Models;
using IraRationis.Media.Audio;

namespace IraRationis.HLE.Audio.Apu;

internal sealed class XApuConnection : IDisposable
{
    public readonly EventWaitHandle? Event;
    public readonly Lock Gate = new();
    public readonly nint Memory;
    public readonly uint MemorySize;
    public readonly uint Options;
    public readonly int PerStream;
    public readonly int[] Queued;
    public readonly XApuResult[] Results;
    public readonly short[] Scratch = new short[XApuModule.MaxFrames * 2];
    public readonly XApuVoice?[] Voices;
    public bool Closed;
    public int Count;
    public uint Dequeued;
    public int Head;
    public IPcmPacketSink? PcmSink;
    public uint PcmStream;
    public uint Submitted;

    public XApuConnection(XApuConnectInput input, nint memory, EventWaitHandle? resultEvent)
    {
        Voices = new XApuVoice?[input.Streams];

        Queued = new int[input.Streams];

        Results = new XApuResult[checked((int)(input.Streams * input.CommandsPerStream))];

        PerStream = (int)input.CommandsPerStream;

        Options = input.Options;

        MemorySize = input.Memory;

        Memory = memory;

        Event = resultEvent;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (Closed)
            {
                return;
            }

            Closed = true;

            PcmSink = null;

            foreach (var voice in Voices)
            {
                voice?.Decoder.Dispose();
            }

            Event?.Dispose();

            NativeMemory.AlignedFree((void*)Memory);
        }
    }

    public bool Contains(nint address, uint length)
    {
        var start = (nuint)Memory;
        var value = (nuint)address;

        return value >= start && value - start <= MemorySize && length <= MemorySize - (value - start);
    }
}
