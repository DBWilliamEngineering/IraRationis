// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Concentus.Structs;
using IraRationis.Media;

namespace IraRationis.HLE;

public static unsafe partial class XApuModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int QueueFull = unchecked((int)0x8AC81000);
    private const int PendingResults = unchecked((int)0x8AC81001);
    private const int InvalidCommand = unchecked((int)0x8AC81002);
    private const int InvalidStream = unchecked((int)0x8AC81003);
    private const int AlreadyActive = unchecked((int)0x8AC81005);
    private const int BadPacket = unchecked((int)0x8AC81008);
    private const uint DecodeOpus = 1;
    private const uint Activate = 1;
    private const uint Deactivate = 2;
    private const uint Process = 3;
    private const uint Notify = 4;
    private const uint Reset = 5;
    private const uint SignalResult = 1;
    private const uint DeinterleavedOutput = 4;
    private const uint ResultEvent = 8;
    private const int MaxFrames = 5760;
    private const uint MaxMemory = 512U * 1024 * 1024;
    private static readonly ConcurrentDictionary<nint, Connection> Connections = new();
    private static long _nextHandle;

    public static HleModule Create()
    {
        if (sizeof(ConnectInput) != 24 || sizeof(ConnectOutput) != 16 || sizeof(CommandId) != 24 ||
            sizeof(ActivateCommand) != 56 || sizeof(ProcessCommand) != 64 || sizeof(Result) != 80)
        {
            throw new InvalidOperationException("Unexpected XApu x64 ABI layout.");
        }

        return new HleModule("xapu", ["xapu.dll"],
        [
            new HleExport("XApuConnect",
                (nuint)(delegate* unmanaged[Stdcall]<ConnectInput*, ConnectOutput*, nint*, int>)&Connect),
            new HleExport("XApuDisconnect", (nuint)(delegate* unmanaged[Stdcall]<nint, int>)&Disconnect),
            new HleExport("XApuEnqueueCommand",
                (nuint)(delegate* unmanaged[Stdcall]<nint, CommandId*, uint*, int>)&Enqueue),
            new HleExport("XApuDequeueResult", (nuint)(delegate* unmanaged[Stdcall]<nint, Result*, int>)&Dequeue),
            new HleExport("XApuGetResultCount", (nuint)(delegate* unmanaged[Stdcall]<nint, uint*, uint>)&ResultCount)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Connect(ConnectInput* input, ConnectOutput* output, nint* handle)
    {
        if (output != null)
        {
            *output = default;
        }

        if (handle != null)
        {
            *handle = 0;
        }

        if (input == null || output == null || handle == null)
        {
            return InvalidArgument;
        }

        var request = *input;
        if (request.Streams is < 1 or > 500 || request.CommandsPerStream is < 1 or > 16 ||
            request.Memory is 0 or > MaxMemory)
        {
            return InvalidArgument;
        }

        if (request.Processing != DecodeOpus || request.FramesPerOutput != 0)
        {
            Console.Error.WriteLine(
                $"[audio] XApu processing={request.Processing}, framesPerOutput={request.FramesPerOutput} not implemented.");

            return NotImplemented;
        }

        nint memory = 0;
        EventWaitHandle? resultEvent = null;
        Connection? connection = null;
        try
        {
            var rounded = ((nuint)request.Memory + 15) & ~(nuint)15;
            memory = (nint)NativeMemory.AlignedAlloc(rounded, 16);
            if (memory == 0)
            {
                return OutOfMemory;
            }

            NativeMemory.Clear((void*)memory, rounded);
            if ((request.Options & ResultEvent) != 0)
            {
                resultEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            }

            var eventHandle = resultEvent?.SafeWaitHandle.DangerousGetHandle() ?? 0;
            Console.Error.WriteLine(
                $"[audio] IRX-XAPU-R8: managed Opus connection requested; streams={request.Streams}, commandsPerStream={request.CommandsPerStream}, memory={request.Memory}, options=0x{request.Options:X}.");
            connection = new Connection(request, memory, resultEvent);
            // Opaque, never-dereferenced connection ID; avoid GCHandle ABA/reuse.
            var token = (nint)Interlocked.Increment(ref _nextHandle);
            if (token <= 0 || !Connections.TryAdd(token, connection))
            {
                return OutOfMemory;
            }

            *output = new ConnectOutput { Memory = memory, Event = eventHandle };
            *handle = token;
            connection = null;
            memory = 0;
            resultEvent = null;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
        {
            return NotImplemented;
        }
        finally
        {
            if (connection != null)
            {
                connection.Dispose();
            }
            else
            {
                resultEvent?.Dispose();
                NativeMemory.AlignedFree((void*)memory);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Disconnect(nint handle)
    {
        if (!Connections.TryRemove(handle, out var connection))
        {
            return InvalidArgument;
        }

        connection.Dispose();

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Enqueue(nint handle, CommandId* command, uint* total)
    {
        if (command == null || !Connections.TryGetValue(handle, out var connection))
        {
            return InvalidArgument;
        }

        lock (connection.Gate)
        {
            if (connection.Closed)
            {
                return InvalidArgument;
            }

            var id = Unsafe.ReadUnaligned<CommandId>(command);
            if (id.Stream >= (uint)connection.Voices.Length)
            {
                return InvalidStream;
            }

            var index = (int)id.Stream;
            if (connection.Queued[index] == connection.PerStream || connection.Count == connection.Results.Length)
            {
                return QueueFull;
            }

            var result = new Result { Id = id, Pitch = 1, State = 0, StateData = 0, Microseconds = 0 };
            var voice = connection.Voices[index];
            switch (id.Kind)
            {
                case Activate:
                    var activate = Unsafe.ReadUnaligned<ActivateCommand>(command);
                    result.Error = ActivateVoice(connection, index, activate);

                    break;

                case Deactivate:
                    voice?.Decoder.Dispose();
                    connection.Voices[index] = null;

                    break;

                case Reset:
                    voice?.Decoder.ResetState();

                    break;

                case Notify:
                    break;

                case Process:
                    if (voice == null)
                    {
                        result.Error = InvalidStream;
                    }
                    else
                    {
                        Decode(connection, voice, Unsafe.ReadUnaligned<ProcessCommand>(command), ref result);
                    }

                    break;

                default:
                    return InvalidCommand;
            }

            var tail = connection.Head + connection.Count;
            if (tail >= connection.Results.Length)
            {
                tail -= connection.Results.Length;
            }

            connection.Results[tail] = result;
            connection.Count++;
            connection.Queued[index]++;
            connection.Submitted = unchecked(connection.Submitted + 1);
            if (total != null)
            {
                *total = connection.Submitted;
            }

            if ((id.Options & SignalResult) != 0 && connection.Event != null)
            {
                try
                {
                    _ = connection.Event.Set();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or ObjectDisposedException)
                {
                    // The result remains available through the polling ABI.
                }
            }

            return 0;
        }
    }

    private static int ActivateVoice(Connection connection, int index, ActivateCommand command)
    {
        if (connection.Voices[index] != null)
        {
            return AlreadyActive;
        }

        if (command.Channels is < 1 or > 2)
        {
            return InvalidArgument;
        }

        try
        {
            var channels = (int)command.Channels;
            // Deliberately bypass the factory's optional native library probing.
#pragma warning disable CS0618
            var decoder = new OpusDecoder(48_000, channels);
#pragma warning restore CS0618
            connection.Voices[index] = new Voice(decoder, channels);

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        catch (ArgumentException)
        {
            return InvalidArgument;
        }
    }

    private static void Decode(Connection connection, Voice voice, ProcessCommand command, ref Result result)
    {
        result.Input = command.Input;
        result.InputLength = command.InputLength;
        result.Output = command.Output;
        if (command.Input == 0 || command.Output == 0 || command.InputLength == 0 ||
            command.InputLength > int.MaxValue || command.OutputLength > int.MaxValue ||
            !connection.Contains(command.Input, command.InputLength)
            || !connection.Contains(command.Output, command.OutputLength))
        {
            result.Error = InvalidArgument;

            return;
        }

        // Plain decode only; do not silently claim variable-pitch processing.
        if (command.TargetPitch != 1.0f || command.PitchRamp != 0.0f)
        {
            result.Error = NotImplemented;

            return;
        }

        var channels = voice.Channels;
        var pcm = connection.Scratch.AsSpan(0, MaxFrames * channels);
        int decoded;
        try
        {
            decoded = voice.Decoder.Decode(new ReadOnlySpan<byte>((void*)command.Input, (int)command.InputLength), pcm,
                MaxFrames);
        }
        catch (OutOfMemoryException)
        {
            result.Error = OutOfMemory;

            return;
        }
        catch (Exception)
        {
            // A malformed packet must not unwind across the unmanaged ABI.
            result.Error = BadPacket;

            return;
        }

        if (decoded < 0 || decoded > MaxFrames)
        {
            result.Error = BadPacket;

            return;
        }

        var first = (int)Math.Min(command.FirstFrame, (uint)decoded);
        var frames = (int)Math.Min((uint)(decoded - first),
            Math.Min(command.FrameCount, command.OutputLength / (uint)(sizeof(float) * channels)));
        var source = pcm.Slice(first * channels, frames * channels);
        var output = new Span<float>((void*)command.Output, frames * channels);
        if ((connection.Options & DeinterleavedOutput) != 0 && channels == 2)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                output[frame] = source[frame * 2] * (1.0f / 32768.0f);
                output[frames + frame] = source[frame * 2 + 1] * (1.0f / 32768.0f);
            }
        }
        else
        {
            for (var sample = 0; sample < source.Length; sample++)
            {
                output[sample] = source[sample] * (1.0f / 32768.0f);
            }
        }

        result.Frames = (uint)frames;
        result.OutputLength = (uint)(frames * channels * sizeof(float));

        ObservePcm(connection, command.Id.Stream, source, channels);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Dequeue(nint handle, Result* output)
    {
        if (output == null || !Connections.TryGetValue(handle, out var connection))
        {
            return InvalidArgument;
        }

        lock (connection.Gate)
        {
            if (connection.Closed)
            {
                return InvalidArgument;
            }

            if (connection.Count == 0)
            {
                return PendingResults;
            }

            var result = connection.Results[connection.Head];
            connection.Head++;
            if (connection.Head == connection.Results.Length)
            {
                connection.Head = 0;
            }

            connection.Count--;
            connection.Queued[(int)result.Id.Stream]--;
            connection.Dequeued = unchecked(connection.Dequeued + 1);
            Unsafe.WriteUnaligned(output, result);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ResultCount(nint handle, uint* total)
    {
        if (!Connections.TryGetValue(handle, out var connection))
        {
            return 0;
        }

        lock (connection.Gate)
        {
            if (connection.Closed)
            {
                return 0;
            }

            if (total != null)
            {
                *total = connection.Dequeued;
            }

            return (uint)connection.Count;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectInput
    {
        public uint Processing;
        public uint Streams;
        public uint CommandsPerStream;
        public uint Memory;
        public uint FramesPerOutput;
        public uint Options;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectOutput
    {
        public nint Memory;
        public nint Event;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CommandId
    {
        public uint Kind;
        public uint Stream;
        public uint Sequence;
        public uint Options;
        public ulong Context;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ActivateCommand
    {
        public CommandId Id;
        public uint Channels;
        public uint InitLength;
        public nint Init;
        public float StartPitch;
        public uint ProcessingLength;
        public nint Processing;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ProcessCommand
    {
        public CommandId Id;
        public float PitchRamp;
        public float TargetPitch;
        public uint FirstFrame;
        public uint FrameCount;
        public uint InputLength;
        public nint Input;
        public uint OutputLength;
        public nint Output;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Result
    {
        public CommandId Id;
        public uint State;
        public ulong StateData;
        public ulong Microseconds;
        public int Error;
        public uint Frames;
        public uint OutputLength;
        public nint Output;
        public float Pitch;
        public uint InputLength;
        public nint Input;
    }

    private sealed class Voice(OpusDecoder decoder, int channels)
    {
        public readonly int Channels = channels;
        public readonly OpusDecoder Decoder = decoder;
    }

    private sealed class Connection : IDisposable
    {
        public readonly EventWaitHandle? Event;
        public readonly Lock Gate = new();
        public readonly nint Memory;
        public readonly uint MemorySize;
        public readonly uint Options;
        public readonly int PerStream;
        public readonly int[] Queued;
        public readonly Result[] Results;
        public readonly short[] Scratch = new short[MaxFrames * 2];
        public readonly Voice?[] Voices;
        public bool Closed;
        public int Count;
        public uint Dequeued;
        public int Head;
        public uint Submitted;
        public uint PcmStream;
        public IPcmPacketSink? PcmSink;

        public Connection(ConnectInput input, nint memory, EventWaitHandle? resultEvent)
        {
            Voices = new Voice?[input.Streams];
            Queued = new int[input.Streams];
            Results = new Result[checked((int)(input.Streams * input.CommandsPerStream))];
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
}
