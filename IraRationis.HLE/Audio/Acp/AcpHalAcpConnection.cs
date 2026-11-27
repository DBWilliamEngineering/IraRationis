// SPDX-License-Identifier: GPL-3.0-or-later
// ACP object ABI and message layout cross-checked against VIctory/victory-fork audio.rs.

using System.Diagnostics;
using IraRationis.HLE.Audio.Acp.Models;

namespace IraRationis.HLE.Audio.Acp;

internal sealed class AcpHalAcpConnection
{
    public readonly Lock Gate = new();
    public readonly long Started = Stopwatch.GetTimestamp();
    public bool Connected;
    public int Count;
    public uint Dropped;
    public int Head;
    public uint Mask;
    public AcpHalAcpMessage[] Messages = [];
    public bool Released;

    public void Post(uint kind, uint command = 0, ulong id = 0)
    {
        if ((Mask & kind) == 0)
        {
            return;
        }

        if (Count == Messages.Length)
        {
            if (Dropped != uint.MaxValue)
            {
                Dropped++;
            }

            return;
        }

        var usec = (ulong)(Stopwatch.GetElapsedTime(Started).Ticks / 10);
        var tail = Head + Count;

        if (tail >= Messages.Length)
        {
            tail -= Messages.Length;
        }

        Messages[tail] = new AcpHalAcpMessage
        {
            Kind = kind,
            Dropped = Dropped,
            Microseconds = unchecked((uint)usec),
            Command = command,
            Id = id,
            Frame = kind == AcpHalModule.CommandCompleted ? unchecked((uint)(usec * 375 / 1_000_000)) : 0
        };

        Dropped = 0;

        Count++;
    }
}
