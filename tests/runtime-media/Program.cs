// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.HLE.Audio.Apu;
using IraRationis.HLE.Runtime;
using IraRationis.Media.Audio;
using IraRationis.Media.Video;

namespace IraRationis.RuntimeMedia.Tests;

internal static unsafe class Program
{
    private const float Pcm16Scale = 1.0f / 32768.0f;
    private const float Tolerance = Pcm16Scale;

    private static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--play"))
        {
            Console.Error.WriteLine("Usage: RuntimeMedia [--play]");

            return 2;
        }

        TestVideo();

        TestAudio(args.Length == 1);

        Console.WriteLine("IRX-R40 runtime media contracts passed.");

        return 0;
    }

    private static void TestVideo()
    {
        var lumaPlane = new byte[] { 16, 16, 16, 16, 99, 99, 235, 235, 235, 235, 99, 99 };
        var chromaPlane = new byte[] { 128, 128, 128, 128, 99, 99 };
        var frame = Nv12UploadFrame.Capture(lumaPlane, 6, chromaPlane, 6, 4, 2, 123);

        Array.Clear(lumaPlane);

        Array.Clear(chromaPlane);

        var upload = new byte[768];

        upload.AsSpan().Fill(0xFF);

        frame.CopyToUpload(upload.AsSpan(0, 512), 256, upload.AsSpan(512), 256);

        Require(upload.AsSpan(0, 4).SequenceEqual(new byte[] { 16, 16, 16, 16 }), "Luma row zero");
        Require(upload.AsSpan(256, 4).SequenceEqual(new byte[] { 235, 235, 235, 235 }), "Luma row one");
        Require(upload.AsSpan(512, 4).SequenceEqual(new byte[] { 128, 128, 128, 128 }), "Chroma row");
        Require(upload.AsSpan(4, 252).IndexOfAnyExcept((byte)0) < 0, "Initialized padding");
        Require(frame.Timestamp == 123, "Timestamp ownership");

        var bgra = new byte[32];

        frame.ConvertToBgra(bgra, 16);

        Require(bgra.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Black conversion");

        Require(bgra.AsSpan(16, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }), "White conversion");

        Console.WriteLine("[media] IRX-R40: NV12 snapshot ownership, pitched copy, padding and conversion passed.");
    }

    private static void TestAudio(bool play)
    {
        var registry = new HleRegistry([XApuModule.Create()]);

        var connect = (delegate* unmanaged[Stdcall]<uint*, nint*, nint*, int>)Export("XApuConnect");
        var disconnect = (delegate* unmanaged[Stdcall]<nint, int>)Export("XApuDisconnect");
        var enqueue = (delegate* unmanaged[Stdcall]<nint, byte*, uint*, int>)Export("XApuEnqueueCommand");
        var dequeue = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Export("XApuDequeueResult");
        var input = stackalloc uint[6] { 1, 2, 2, 65536, 0, 0 };
        var output = stackalloc nint[2];
        var handle = (nint)0;

        Require(connect(input, output, &handle) == 0 && handle != 0, "XApu connection");

        using var monitor = play ? new WindowsPcmMonitor() : null;

        var sink = new ProgramProbeSink(monitor);

        try
        {
            Require(!XApuModule.AttachPcmSink(handle, 2, sink), "Invalid voice rejected");

            Require(XApuModule.AttachPcmSink(handle, 1, sink), "Sink attached");

            // Original synthetic 440 Hz Opus test packet from the repository's InteropTests.
            var packet = Convert.FromBase64String(
                "/LQGWqe14f0LvgB/+QdyhNj0C69bh6dmtpD7sail85yTi1ueGeRgTYRPqYtjhKqkjPtSY0+EOenwjqA8KAaZE8Hu/n/KnGus1EAeaHscF95RABf3LfAPgm/XglZ+p4zh+MLd5oSTnZDV8VmfNi3Y9o7JGpQxKW243UkC7OBBZ1C2zFaXpTO5pTQPCUw0kKlTr418VyfjNJP5pPBhmd7PeddQ5YuJhGRyz4OIgzM7S6hSVdEQ59J64P0Q59IjgxQ5t1eqRLbfL4r+WGH12MBXJflBkV3msKUTYhWG9DaLDBcKUF8sg6v87g4oduJtPaBqdLgT/d2QqeVb03I2Hq5m0z/iTKHpy+AuYKPZ7G2T/Tz4i7haUHF7D5DEiWzewH8hNtL9Uq2k/tVt");
            var memory = (byte*)output[0];

            packet.AsSpan().CopyTo(new Span<byte>(memory, packet.Length));

            var command = stackalloc byte[64];
            var result = stackalloc byte[80];
            var commandBytes = new Span<byte>(command, 64);
            var resultBytes = new ReadOnlySpan<byte>(result, 80);

            commandBytes.Clear();

            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[4..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[24..], 2);
            BinaryPrimitives.WriteSingleLittleEndian(commandBytes[40..], 1);

            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0, "Voice activation");

            commandBytes[24..].Clear();

            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 3);
            BinaryPrimitives.WriteSingleLittleEndian(commandBytes[28..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[36..], 960);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[40..], (uint)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[44..], (nuint)memory);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[52..], 7680);
            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[56..], (nuint)(memory + 4096));

            var packets = play ? 50 : 1;

            for (var index = 0; index < packets; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[8..], (uint)(index + 1));

                Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                        BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(resultBytes[48..]) == 960, "PCM decode");

                if (play)
                {
                    Thread.Sleep(20);
                }
            }

            Require(sink.Packets == packets && sink.Frames == packets * 960 && sink.Peak > 0,
                "Non-silent PCM reached attached sink");

            var guest = new ReadOnlySpan<float>(memory + 4096, 1920);

            Require(sink.LastPacket.Length == guest.Length, "Guest and observer lengths agree");

            for (var index = 0; index < guest.Length; index++)
            {
                Require(Math.Abs(guest[index] - sink.LastPacket[index] * (1.0f / 32768.0f)) < Tolerance, "Guest output unchanged");
            }

            Require(XApuModule.AttachPcmSink(handle, 1, null), "Sink detached");

            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0 && sink.Packets == packets,
                "Detached sink not called");

            Console.WriteLine($"[audio] IRX-R40: packets={sink.Packets}; frames={sink.Frames}; peak={sink.Peak}.");
        }
        finally
        {
            Require(disconnect(handle) == 0, "XApu disconnect");

            if (monitor is not null)
            {
                monitor.Dispose();

                Require(monitor.WaitForCompletion(TimeSpan.FromSeconds(8)) && !monitor.Faulted &&
                        monitor.CompletedPackets > 0, "Windows PCM output completion");
            }
        }

        return;

        nuint Export(string name)
        {
            return registry.Lookup("xapu.dll", name) ?? throw new InvalidOperationException(name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
