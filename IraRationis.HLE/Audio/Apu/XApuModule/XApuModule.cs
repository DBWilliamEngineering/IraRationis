// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Concentus.Structs;
using IraRationis.HLE.Audio.Apu.Models;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Audio.Apu.XApuModule;

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
    internal const int MaxFrames = 5760;
    private const uint MaxMemory = 512U * 1024 * 1024;
    private static readonly ConcurrentDictionary<nint, XApuConnection> Connections = new();
    private static long _nextHandle;
    private static readonly bool TraceAudio = Environment.GetEnvironmentVariable("IRX_AUDIO_TRACE") == "1";
    private static long _decodeAttempts, _decodeFailures, _decodedFrames;

    public static HleModule Create()
    {
        if (sizeof(XApuConnectInput) != 24 || sizeof(XApuConnectOutput) != 16 || sizeof(XApuCommandId) != 24 ||
            sizeof(XApuActivateCommand) != 56 || sizeof(XApuProcessCommand) != 64 || sizeof(XApuResult) != 80)
        {
            throw new InvalidOperationException("Unexpected XApu x64 ABI layout.");
        }

        return new HleModule("xapu", ["xapu.dll"],
        [
            new HleExport("XApuConnect",
                (nuint)(delegate* unmanaged[Stdcall]<XApuConnectInput*, XApuConnectOutput*, nint*, int>)&Connect),
            new HleExport("XApuDisconnect", (nuint)(delegate* unmanaged[Stdcall]<nint, int>)&Disconnect),
            new HleExport("XApuEnqueueCommand",
                (nuint)(delegate* unmanaged[Stdcall]<nint, XApuCommandId*, uint*, int>)&Enqueue),
            new HleExport("XApuDequeueResult", (nuint)(delegate* unmanaged[Stdcall]<nint, XApuResult*, int>)&Dequeue),
            new HleExport("XApuGetResultCount", (nuint)(delegate* unmanaged[Stdcall]<nint, uint*, uint>)&ResultCount)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Connect(XApuConnectInput* input, XApuConnectOutput* output, nint* handle)
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

        XApuConnection? connection = null;

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

            connection = new XApuConnection(request, memory, resultEvent);
            // Opaque, never-dereferenced connection ID; avoid GCHandle ABA/reuse.
            var token = (nint)Interlocked.Increment(ref _nextHandle);

            if (token <= 0 || !Connections.TryAdd(token, connection))
            {
                return OutOfMemory;
            }

            *output = new XApuConnectOutput { Memory = memory, Event = eventHandle };

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
    private static int Enqueue(nint handle, XApuCommandId* command, uint* total)
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

            var id = Unsafe.ReadUnaligned<XApuCommandId>(command);

            if (id.Stream >= (uint)connection.Voices.Length)
            {
                return InvalidStream;
            }

            var index = (int)id.Stream;

            if (connection.Queued[index] == connection.PerStream || connection.Count == connection.Results.Length)
            {
                return QueueFull;
            }

            var result = new XApuResult { Id = id, Pitch = 1, State = 0, StateData = 0, Microseconds = 0 };
            var voice = connection.Voices[index];

            switch (id.Kind)
            {
                case Activate:
                    var activate = Unsafe.ReadUnaligned<XApuActivateCommand>(command);

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
                        Decode(connection, voice, Unsafe.ReadUnaligned<XApuProcessCommand>(command), ref result);
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

    private static int ActivateVoice(XApuConnection connection, int index, XApuActivateCommand command)
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
            connection.Voices[index] = new XApuVoice(decoder, channels);

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

    private static void Decode(XApuConnection connection, XApuVoice voice, XApuProcessCommand command, ref XApuResult result)
    {
        var observation = Interlocked.Increment(ref _decodeAttempts);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        DecodeCore(connection, voice, command, ref result);
        if (result.Error != 0)
        {
            var failures = Interlocked.Increment(ref _decodeFailures);
            if (failures <= 4 || (failures & (failures - 1)) == 0)
            {
                Console.Error.WriteLine($"[audio] XApu decode rejected: rate={command.TargetPitch}; ramp={command.PitchRamp}; error=0x{result.Error:X8}; failures={failures}.");
            }
        }
        else
        {
            _ = Interlocked.Add(ref _decodedFrames, result.Frames);
        }

        if (TraceAudio && (observation <= 4 || (observation & (observation - 1)) == 0))
        {
            Console.Error.WriteLine($"[audio] XApu decode: attempts={observation}; failures={Interlocked.Read(ref _decodeFailures)}; frames={Interlocked.Read(ref _decodedFrames)}; rate={command.TargetPitch}; elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}.");
        }
    }

    private static void DecodeCore(XApuConnection connection, XApuVoice voice, XApuProcessCommand command, ref XApuResult result)
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

        // The guest passes the source sample rate at +0x1C; 1.0 is retained for
        // legacy normalized-pitch callers. The decoder produces 48 kHz PCM.
        // Other rates and nonzero ramp fields require explicit conversion support.
        if (command.TargetPitch is not (1.0f or 48_000.0f) || command.PitchRamp != 0.0f)
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
    private static int Dequeue(nint handle, XApuResult* output)
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
}
