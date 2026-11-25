// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.
// Ordered per-queue workers; weighted shared scheduling and BCPack are not yet ported.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IraRationis.HLE;

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
    private const ulong BcPack = 0x1E;
    private const ulong PageDestination = 1UL << 21;
    private const ulong MemorySource = 1UL << 22;
    private static readonly Guid FactoryId = new("62959d11-d45f-4884-a0a3-f7862ea243ae");
    private static readonly nint FactoryTable = CreateFactoryTable();
    private static readonly nint FileTable = CreateFileTable();
    private static readonly nint StatusTable = CreateStatusTable();
    private static readonly nint QueueTable = CreateQueueTable();
    private static readonly nint Factory = NewObject(FactoryTable, new object());
    private static long _stagingBytes = 32L << 20;
    private static readonly object StagingGate = new();
    private static long _staged;
    private static long _r32NextQueue;
    private static readonly bool R32StrictLengths =
        Environment.GetEnvironmentVariable("IRX_STORAGE_STRICT_LENGTH") != "0";
    private static readonly bool R32TraceStorage = Environment.GetEnvironmentVariable("IRX_STORAGE_TRACE") == "1";

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
        var instance = (NativeObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeObject));
        instance->Table = table;
        instance->State = GCHandle.ToIntPtr(handle);
        instance->References = 1;

        return (nint)instance;
    }

    private static T State<T>(nint self) where T : class
    {
        return (T)GCHandle.FromIntPtr(((NativeObject*)self)->State).Target!;
    }

    private static uint Retain(nint self)
    {
        return self == Factory ? 1U : (uint)Interlocked.Increment(ref ((NativeObject*)self)->References);
    }

    private static uint ReleaseCore(nint self)
    {
        if (self == Factory)
        {
            return 1;
        }

        var instance = (NativeObject*)self;
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

    private static void Drop(Command command)
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

    private static void Discard(Command command)
    {
        if (command.Status != 0)
        {
            Volatile.Write(ref State<StatusArray>(command.Status).Entries[command.Index], Cancelled);
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
            *output = NewObject(StatusTable, new StatusArray(new int[capacity]));

            return 0;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateQueue(nint self, QueueDescription* description, Guid* iid, nint* output)
    {
        if (description == null || output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        try
        {
            var queue = new StorageQueue(description->Capacity);

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
                $"[dstorage] Queue created: capacity={description->Capacity}, priority={description->Priority} (serial worker).");

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
        var entries = State<StatusArray>(self).Entries;

        return index < entries.Length ? Volatile.Read(ref entries[index]) : InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte IsComplete(nint self, uint index)
    {
        return GetStatusCore(self, index) == Pending ? (byte)0 : (byte)1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EnqueueRequest(nint self, Request* request)
    {
        if (request == null)
        {
            return;
        }

        var supportedSource = (request->Options & ~(Zlib | BcPack | PageDestination | MemorySource)) == 0;
        var file = supportedSource && (request->Options & MemorySource) == 0 ? request->Source : 0;

        if (file != 0)
        {
            Retain(file);
        }

        State<StorageQueue>(self).Enqueue(new Command(*request, file, 0, 0));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EnqueueStatus(nint self, nint status, uint index)
    {
        if (status == 0)
        {
            return;
        }

        var entries = State<StatusArray>(status).Entries;

        if (index >= entries.Length)
        {
            return;
        }

        Volatile.Write(ref entries[index], Pending);
        Retain(status);
        State<StorageQueue>(self).Enqueue(new Command(default, 0, status, index));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Submit(nint self)
    {
        State<StorageQueue>(self).Submit();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Close(nint self)
    {
        State<StorageQueue>(self).Dispose();
    }

    private static int Execute(Request request, nint file, out int bytesWritten)
    {
        bytesWritten = 0;
        try
        {
            var result = ExecuteCore(request, file, out bytesWritten);

            if (result < 0)
            {
                Console.Error.WriteLine(
                    $"[dstorage] Request failed: 0x{result:X8}, options=0x{request.Options:X}, source={request.SourceSize}, destination={request.DestinationSize}.");
            }

            return result;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static int ExecuteCore(Request request, nint file, out int bytesWritten)
    {
        bytesWritten = 0;
        if ((request.Options & ~(Zlib | BcPack | PageDestination | MemorySource)) != 0
            || (request.Options & BcPack) != 0)
        {
            return NotImplemented;
        }

        if (request.Source == 0 || request.Destination == 0 || request.SourceSize > int.MaxValue
            || request.DestinationSize > int.MaxValue)
        {
            return InvalidArgument;
        }

        var compressed = (request.Options & Zlib) != 0;
        var physical = (request.Options & PageDestination) != 0;
        var sourceSize = (int)request.SourceSize;
        var destinationSize = (int)request.DestinationSize;

        if (!compressed && !DirectStoragePayload.IsRawLengthValid(sourceSize, destinationSize, R32StrictLengths))
        {
            return InvalidArgument;
        }

        // Normal raw reads go straight to the caller's destination.
        if (!compressed && !physical)
        {
            var result = Read(request, file, new Span<byte>((void*)request.Destination, sourceSize));
            if (result >= 0)
            {
                bytesWritten = sourceSize;
            }

            return result;
        }

        var staging = file != 0 ? sourceSize : 0;
        AcquireStaging(staging);

        try
        {
            var source = new byte[sourceSize];
            var result = Read(request, file, source);

            if (result < 0)
            {
                return result;
            }

            if (!compressed)
            {
                if (!PhysicalPageModule.WritePhysical((nuint*)request.Destination, request.PageOffset & 0xFFFF,
                        source))
                {
                    return InvalidArgument;
                }

                bytesWritten = sourceSize;

                return 0;
            }

            var buffer = physical ? new byte[destinationSize] : null;
            var output = physical ? buffer.AsSpan() : new Span<byte>((void*)request.Destination, destinationSize);
            if (!DirectStoragePayload.TryInflate(source, output, R32StrictLengths, out var written))
            {
                return InvalidData;
            }

            if (physical && !PhysicalPageModule.WritePhysical((nuint*)request.Destination,
                    request.PageOffset & 0xFFFF, output[..written]))
            {
                return InvalidArgument;
            }

            bytesWritten = written;

            return 0;
        }
        finally
        {
            ReleaseStaging(staging);
        }
    }

    private static int Read(Request request, nint file, Span<byte> destination)
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
        lock (StagingGate)
        {
            while (bytes != 0 && _staged != 0 && _staged + bytes > _stagingBytes)
            {
                Monitor.Wait(StagingGate);
            }

            _staged += bytes;
        }
    }

    private static void ReleaseStaging(long bytes)
    {
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
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, QueueDescription*, Guid*, nint*, int>)&CreateQueue;

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
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, Request*, void>)&EnqueueRequest;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, void>)&EnqueueStatus;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&Submit;
        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&Close;

        return (nint)table;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObject
    {
        public nint Table;
        public nint State;
        public int References;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct QueueDescription
    {
        [FieldOffset(0)] public ulong SourceType;
        [FieldOffset(8)] public ushort Capacity;
        [FieldOffset(10)] public sbyte Priority;
        [FieldOffset(16)] public nint Name;
        [FieldOffset(24)] public ulong Unknown;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x48)]
    private struct Request
    {
        [FieldOffset(0)] public ulong Options;
        [FieldOffset(8)] public nint Destination;
        [FieldOffset(16)] public ulong PageOffset;
        [FieldOffset(24)] public uint DestinationSize;
        [FieldOffset(32)] public nint Source;
        [FieldOffset(40)] public ulong Offset;
        [FieldOffset(48)] public uint SourceSize;
        [FieldOffset(52)] public uint IntermediateSize;
        [FieldOffset(56)] public ulong Tag;
        [FieldOffset(64)] public nint Name;
    }

    private sealed record StatusArray(int[] Entries);

    private sealed record Command(Request Request, nint File, nint Status, uint Index);

    private sealed class StorageQueue : IDisposable
    {
        private readonly int _capacity;
        private readonly object _gate = new();
        private readonly List<Command> _pending = [];
        private readonly long _r32QueueId = Interlocked.Increment(ref _r32NextQueue);
        private readonly Queue<Command> _submitted = new();
        private readonly Thread _worker;
        private bool _closed;
        private int _firstFailure;
        private long _r32Completed;
        private long _r32Failed;
        private long _r32OutputBytes;
        private long _r32StatusCount;
        private bool _running;

        public StorageQueue(int capacity)
        {
            _capacity = Math.Max(2, capacity);
            _worker = new Thread(Work) { IsBackground = true, Name = "irx-storage" };
            _worker.Start();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _closed = true;

                    foreach (var command in _pending)
                    {
                        Discard(command);
                    }

                    _pending.Clear();

                    while (_submitted.TryDequeue(out var command))
                    {
                        Discard(command);
                    }

                    Monitor.PulseAll(_gate);
                }
            }

            _worker.Join();
        }

        private static bool Sample(long count)
        {
            return count <= 4 || (count & (count - 1)) == 0;
        }

        private void TraceCompletion(Request request, int result, int bytesWritten, long started)
        {
            _r32Completed++;
            if (result < 0)
            {
                _r32Failed++;
            }
            else
            {
                _r32OutputBytes += bytesWritten;
            }

            if (Sample(_r32Completed) || (result < 0 && Sample(_r32Failed)))
            {
                Console.Error.WriteLine(
                    $"[dstorage] IRX-R32-REQUEST: queue={_r32QueueId}; completed={_r32Completed}; failed={_r32Failed}; options=0x{request.Options:X}; sourceBytes={request.SourceSize}; expectedBytes={request.DestinationSize}; successfulBytes={(result >= 0 ? bytesWritten : 0)}; cumulativeOutputBytes={_r32OutputBytes}; hr=0x{result:X8}; ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}; strictLengths={R32StrictLengths}.");
            }
        }

        public void Enqueue(Command command)
        {
            lock (_gate)
            {
                while (!_closed && _pending.Count + _submitted.Count + (_running ? 1 : 0) >= _capacity - 1)
                {
                    SubmitCore();
                    Monitor.Wait(_gate);
                }

                if (_closed)
                {
                    Discard(command);

                    return;
                }

                _pending.Add(command);

                if (_pending.Count >= Math.Max(1, _capacity / 2))
                {
                    SubmitCore();
                }
            }
        }

        private void SubmitCore()
        {
            foreach (var command in _pending)
            {
                _submitted.Enqueue(command);
            }

            _pending.Clear();
            Monitor.PulseAll(_gate);
        }

        public void Submit()
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    SubmitCore();
                }
            }
        }

        private void Work()
        {
            while (true)
            {
                Command command;

                lock (_gate)
                {
                    while (!_closed && _submitted.Count == 0)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_closed)
                    {
                        return;
                    }

                    command = _submitted.Dequeue();
                    _running = true;
                }

                try
                {
                    if (command.Status != 0)
                    {
                        var array = State<StatusArray>(command.Status);
                        var status = _firstFailure;
                        Volatile.Write(ref array.Entries[command.Index], status);
                        _firstFailure = 0;
                        if (R32TraceStorage && (Sample(++_r32StatusCount) || status < 0))
                        {
                            Console.Error.WriteLine(
                                $"[dstorage] IRX-R32-STATUS: queue={_r32QueueId}; markers={_r32StatusCount}; index={command.Index}; hr=0x{status:X8}; completed={_r32Completed}; failed={_r32Failed}.");
                        }
                    }
                    else
                    {
                        var started = R32TraceStorage ? Stopwatch.GetTimestamp() : 0;
                        var result = Execute(command.Request, command.File, out var bytesWritten);

                        if (result < 0 && _firstFailure == 0)
                        {
                            _firstFailure = result;
                        }

                        if (R32TraceStorage)
                        {
                            TraceCompletion(command.Request, result, bytesWritten, started);
                        }
                    }
                }
                finally
                {
                    Drop(command);

                    lock (_gate)
                    {
                        _running = false;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }
    }
}
