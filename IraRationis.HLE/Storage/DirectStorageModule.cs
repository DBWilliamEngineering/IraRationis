// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.
// Ordered per-queue workers.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Memory;
using IraRationis.HLE.Memory.Models;
using IraRationis.HLE.Runtime.Models;
using IraRationis.HLE.Storage.BcPack;
using IraRationis.HLE.Storage.Models;
using Microsoft.Win32.SafeHandles;

namespace IraRationis.HLE.Storage;

public static unsafe class DirectStorageModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int NoInterface = unchecked((int)0x80004002);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int Pending = unchecked((int)0x8000000A);
    private const int InvalidData = unchecked((int)0x8007000D);
    private const int EndOfFile = unchecked((int)0x80070026);
    private const int Cancelled = unchecked((int)0x800704C7);
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const ulong Zlib = 1;
    private const ulong BcPackModeMask = 0x1E;
    private const ulong BcPackBc1 = 2;
    private const ulong PageDestination = 1UL << 21;
    private const ulong MemorySource = 1UL << 22;
    private const ulong AutoSubmitDisabled = 1;
    private const long DefaultStagingBudgetBytes = 32L << 20;
    private const int InitialFailuresToLog = 4;
    private const string StrictLengthsVariable = "IRX_STORAGE_STRICT_LENGTH";
    private const string TraceVariable = "IRX_STORAGE_TRACE";
    private static readonly Guid FactoryId = new("62959d11-d45f-4884-a0a3-f7862ea243ae");
    private static readonly nint FactoryTable = CreateFactoryTable();
    private static readonly nint FileTable = CreateFileTable();
    private static readonly nint StatusTable = CreateStatusTable();
    private static readonly nint QueueTable = CreateQueueTable();
    private static readonly nint Factory = NewObject(FactoryTable, new object());
    private static long _stagingBytes = DefaultStagingBudgetBytes;
    private static readonly object StagingGate = new();
    private static readonly long[] RequestFailures = new long[Enum.GetValues<StorageRequestFailure>().Length];
    private static long _staged;
    internal static long _nextStorageQueueId;
    internal static readonly bool StrictStorageLengths =
        Environment.GetEnvironmentVariable(StrictLengthsVariable) != "0";
    internal static readonly bool EnableStorageTrace = Environment.GetEnvironmentVariable(TraceVariable) == "1";

    public static HleModule Create()
    {
        return new HleModule("dstorage", ["xcrdapi.dll"],
        [
            new HleExport("DStorageGetFactory", (nuint)(delegate* unmanaged[Stdcall]<Guid*, nint*, int>)&GetFactory)
        ]);
    }

    private static nint NewObject(nint table, object state)
    {
        var handle = GCHandle.Alloc(state);
        var instance = (StorageNativeObject*)NativeMemory.AllocZeroed((nuint)sizeof(StorageNativeObject));

        instance->Table = table;

        instance->State = GCHandle.ToIntPtr(handle);

        instance->References = 1;

        return (nint)instance;
    }

    internal static T State<T>(nint self) where T : class
    {
        return (T)GCHandle.FromIntPtr(((StorageNativeObject*)self)->State).Target!;
    }

    private static uint Retain(nint self)
    {
        return self == Factory ? 1U : (uint)Interlocked.Increment(ref ((StorageNativeObject*)self)->References);
    }

    private static uint ReleaseCore(nint self)
    {
        if (self == Factory)
        {
            return 1;
        }

        var instance = (StorageNativeObject*)self;
        var remaining = Interlocked.Decrement(ref instance->References);

        if (remaining == 0)
        {
            var handle = GCHandle.FromIntPtr(instance->State);

            if (handle.Target is IDisposable disposable)
            {
                disposable.Dispose();
            }

            handle.Free();

            NativeMemory.Free(instance);
        }

        return (uint)remaining;
    }

    internal static void Drop(StorageCommand command)
    {
        if (command.File != 0)
        {
            ReleaseCore(command.File);
        }

        if (command.Status != 0)
        {
            ReleaseCore(command.Status);
        }
    }

    internal static void Discard(StorageCommand command)
    {
        if (command.Status != 0)
        {
            Volatile.Write(ref State<StorageStatusArray>(command.Status).Entries[command.Index], Cancelled);
        }

        Drop(command);
    }

    private static int Failure(Exception exception)
    {
        Console.Error.WriteLine($"[dstorage] {exception.Message}");

        return exception switch
        {
            OutOfMemoryException => OutOfMemory,
            InvalidDataException => InvalidData,
            _ => exception.HResult
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFactory(Guid* iid, nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        if (iid == null || *iid != FactoryId)
        {
            return NoInterface;
        }

        Console.WriteLine("[dstorage] Factory created.");

        *output = Factory;

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self)
    {
        return Retain(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        return ReleaseCore(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint self, Guid* iid, nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Missing(nint self)
    {
        Console.Error.WriteLine("[dstorage] Unsupported interface method.");

        return NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OpenFile(nint self, char* path, Guid* iid, nint* output)
    {
        if (path == null || output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        try
        {
            var original = new string(path);
            var translated = FileSystemModule.Translate(original) ?? original;
            var file = File.OpenHandle(translated, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.RandomAccess);

            try
            {
                *output = NewObject(FileTable, file);
            }
            catch
            {
                file.Dispose();

                throw;
            }

            Console.WriteLine($"[dstorage] Opened {original}.");

            return 0;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateStatus(nint self, uint capacity, nint name, Guid* iid, nint* output)
    {
        if (output == null || capacity > int.MaxValue)
        {
            return InvalidArgument;
        }

        *output = 0;

        try
        {
            *output = NewObject(StatusTable, new StorageStatusArray(new int[capacity]));

            return 0;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateQueue(nint self, StorageQueueDescription* description, Guid* iid, nint* output)
    {
        if (description == null || output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        try
        {
            var autoSubmit = (description->Options & AutoSubmitDisabled) == 0;
            var queue = new DirectStorageQueue(description->Capacity, autoSubmit);

            try
            {
                *output = NewObject(QueueTable, queue);
            }
            catch
            {
                queue.Dispose();

                throw;
            }

            Console.WriteLine(
                $"[dstorage] Queue created: capacity={description->Capacity}, priority={description->Priority}, " +
                $"options=0x{description->Options:X}, autoSubmit={autoSubmit} (serial worker).");

            return 0;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetStaging(nint self, uint size)
    {
        lock (StagingGate)
        {
            _stagingBytes = size;

            Monitor.PulseAll(StagingGate);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetStatus(nint self, uint index)
    {
        return GetStatusCore(self, index);
    }

    private static int GetStatusCore(nint self, uint index)
    {
        var entries = State<StorageStatusArray>(self).Entries;

        return index < entries.Length ? Volatile.Read(ref entries[index]) : InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte IsComplete(nint self, uint index)
    {
        return GetStatusCore(self, index) == Pending ? (byte)0 : (byte)1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EnqueueRequest(nint self, StorageRequest* request)
    {
        if (request == null)
        {
            return;
        }

        var supportedSource = (request->Options & ~(Zlib | BcPackModeMask | PageDestination | MemorySource)) == 0;
        var file = supportedSource && (request->Options & MemorySource) == 0 ? request->Source : 0;

        if (file != 0)
        {
            Retain(file);
        }

        State<DirectStorageQueue>(self).Enqueue(new StorageCommand(*request, file, 0, 0));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EnqueueStatus(nint self, nint status, uint index)
    {
        if (status == 0)
        {
            return;
        }

        var entries = State<StorageStatusArray>(status).Entries;

        if (index >= entries.Length)
        {
            return;
        }

        Volatile.Write(ref entries[index], Pending);

        Retain(status);

        State<DirectStorageQueue>(self).Enqueue(new StorageCommand(default, 0, status, index));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Submit(nint self)
    {
        State<DirectStorageQueue>(self).Submit();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Close(nint self)
    {
        State<DirectStorageQueue>(self).Dispose();
    }

    internal static int Execute(StorageRequest request, nint file, out int bytesWritten)
    {
        bytesWritten = 0;

        try
        {
            return ExecuteCore(request, file, out bytesWritten);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static int RejectRequest(in StorageRequest request, int result, StorageRequestFailure failure,
        PhysicalPageWriteFailure pageFailure = PhysicalPageWriteFailure.None)
    {
        var count = Interlocked.Increment(ref RequestFailures[(int)failure]);

        if (EnableStorageTrace || count <= InitialFailuresToLog || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[dstorage] Request failed: 0x{result:X8}, options=0x{request.Options:X}, source={request.SourceSize}, " +
                $"destination={request.DestinationSize}, reason={failure}, pageFailure={pageFailure}, reasonCount={count}.");
        }

        return result;
    }

    private static int ExecuteCore(StorageRequest request, nint file, out int bytesWritten)
    {
        bytesWritten = 0;

        if ((request.Options & ~(Zlib | BcPackModeMask | PageDestination | MemorySource)) != 0)
        {
            return RejectRequest(request, NotImplemented, StorageRequestFailure.UnsupportedOptions);
        }

        var bcPackMode = request.Options & BcPackModeMask;

        if (bcPackMode != 0 && bcPackMode != BcPackBc1)
        {
            return RejectRequest(request, NotImplemented, StorageRequestFailure.UnsupportedBcPackMode);
        }

        if (request.Source == 0 || request.Destination == 0 || request.SourceSize > int.MaxValue
            || request.DestinationSize > int.MaxValue)
        {
            return RejectRequest(request, InvalidArgument, StorageRequestFailure.InvalidRequest);
        }

        var inflate = (request.Options & Zlib) != 0;
        var bcPack = bcPackMode == BcPackBc1;
        var compressed = inflate || bcPack;
        var physical = (request.Options & PageDestination) != 0;
        var sourceSize = (int)request.SourceSize;
        var destinationSize = (int)request.DestinationSize;

        if ((!compressed && !DirectStoragePayload.IsRawLengthValid(sourceSize, destinationSize, StrictStorageLengths)) ||
            (bcPack && (destinationSize == 0 || destinationSize % 8 != 0)) ||
            (bcPack && inflate && (request.IntermediateSize == 0 || request.IntermediateSize > request.DestinationSize)))
        {
            return RejectRequest(request, InvalidArgument, StorageRequestFailure.InvalidLength);
        }

        // Normal raw reads go straight to the caller's destination.
        if (!compressed && !physical)
        {
            var result = Read(request, file, new Span<byte>((void*)request.Destination, sourceSize));

            if (result >= 0)
            {
                bytesWritten = sourceSize;
            }

            return result >= 0 ? result : RejectRequest(request, result, StorageRequestFailure.ReadFailed);
        }

        var staging = file != 0 ? sourceSize : 0;
        byte[]? sourceBuffer = null;
        byte[]? outputBuffer = null;

        AcquireStaging(staging);

        try
        {
            sourceBuffer = ArrayPool<byte>.Shared.Rent(sourceSize);

            var source = new ArraySegment<byte>(sourceBuffer, 0, sourceSize);
            var result = Read(request, file, source.AsSpan());

            if (result < 0)
            {
                return RejectRequest(request, result, StorageRequestFailure.ReadFailed);
            }

            if (!compressed)
            {
                result = PublishPhysicalPages(request, source.AsSpan());

                if (result < 0)
                {
                    return result;
                }

                bytesWritten = sourceSize;

                return 0;
            }

            outputBuffer = physical || bcPack ? ArrayPool<byte>.Shared.Rent(destinationSize) : null;

            var output = outputBuffer is null
                ? new Span<byte>((void*)request.Destination, destinationSize)
                : outputBuffer.AsSpan(0, destinationSize);

            result = DecodePayload(request, source, output, inflate, bcPack, out var written);

            if (result < 0)
            {
                return result;
            }

            if (physical)
            {
                result = PublishPhysicalPages(request, output[..written]);

                if (result < 0)
                {
                    return result;
                }
            }
            else if (bcPack)
            {
                output.CopyTo(new Span<byte>((void*)request.Destination, destinationSize));
            }

            bytesWritten = written;

            return 0;
        }
        finally
        {
            if (outputBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(outputBuffer);
            }

            if (sourceBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(sourceBuffer);
            }

            ReleaseStaging(staging);
        }
    }

    private static int DecodePayload(in StorageRequest request, ArraySegment<byte> source, Span<byte> output, bool inflate,
        bool bcPack, out int bytesWritten)
    {
        bytesWritten = 0;

        if (!bcPack)
        {
            return DirectStoragePayload.TryInflate(source, output, StrictStorageLengths, out bytesWritten)
                ? 0
                : RejectRequest(request, InvalidData, StorageRequestFailure.InvalidZlibPayload);
        }

        if (!inflate)
        {
            return DecodeBcPack(request, source.AsSpan(), output, out bytesWritten);
        }

        var intermediateSize = (int)request.IntermediateSize;
        var intermediate = ArrayPool<byte>.Shared.Rent(intermediateSize);

        try
        {
            var payload = intermediate.AsSpan(0, intermediateSize);

            return DirectStoragePayload.TryInflate(source, payload, strict: true, out _)
                ? DecodeBcPack(request, payload, output, out bytesWritten)
                : RejectRequest(request, InvalidData, StorageRequestFailure.InvalidZlibPayload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(intermediate);
        }
    }

    private static int DecodeBcPack(in StorageRequest request, ReadOnlySpan<byte> payload, Span<byte> output,
        out int bytesWritten)
    {
        bytesWritten = 0;

        if (!BcPackBc1Decoder.TryDecode(payload, output, BcPackDictionaryFile.Dictionary))
        {
            return RejectRequest(request, InvalidData, StorageRequestFailure.BcPackDecodeRejected);
        }

        bytesWritten = output.Length;

        return 0;
    }

    private static int PublishPhysicalPages(in StorageRequest request, ReadOnlySpan<byte> data)
    {
        return PhysicalPageModule.WritePhysical((nuint*)request.Destination, request.DestinationPageOffset, data,
            out var failure)
            ? 0
            : RejectRequest(request, InvalidArgument, StorageRequestFailure.PhysicalPagePublication, failure);
    }

    private static int Read(StorageRequest request, nint file, Span<byte> destination)
    {
        if ((request.Options & MemorySource) != 0)
        {
            new ReadOnlySpan<byte>((void*)request.Source, destination.Length).CopyTo(destination);

            return 0;
        }

        if (file == 0 || request.Offset > long.MaxValue - (ulong)destination.Length)
        {
            return InvalidArgument;
        }

        var handle = State<SafeFileHandle>(file);
        var done = 0;

        while (done < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[done..], (long)request.Offset + done);

            if (read == 0)
            {
                return EndOfFile;
            }

            done += read;
        }

        return 0;
    }

    private static void AcquireStaging(long bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        lock (StagingGate)
        {
            while (_staged != 0 && _staged + bytes > _stagingBytes)
            {
                Monitor.Wait(StagingGate);
            }

            _staged += bytes;
        }
    }

    private static void ReleaseStaging(long bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        lock (StagingGate)
        {
            _staged -= bytes;

            Monitor.PulseAll(StagingGate);
        }
    }

    private static nint* NewTable(int slots)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)slots, (nuint)sizeof(nint));

        for (var index = 0; index < slots; index++)
        {
            table[index] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Missing;
        }

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;

        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;

        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;

        return table;
    }

    private static nint CreateFactoryTable()
    {
        var table = NewTable(16);

        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, Guid*, nint*, int>)&OpenFile;

        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, Guid*, nint*, int>)&CreateStatus;

        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&SetStaging;

        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, StorageQueueDescription*, Guid*, nint*, int>)&CreateQueue;

        return (nint)table;
    }

    private static nint CreateFileTable()
    {
        return (nint)NewTable(8);
    }

    private static nint CreateStatusTable()
    {
        var table = NewTable(5);

        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, byte>)&IsComplete;

        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&GetStatus;

        return (nint)table;
    }

    private static nint CreateQueueTable()
    {
        var table = NewTable(12);

        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, StorageRequest*, void>)&EnqueueRequest;

        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, void>)&EnqueueStatus;

        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&Submit;

        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&Close;

        return (nint)table;
    }
}
