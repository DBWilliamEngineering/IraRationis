// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.
// Ordered per-queue workers; weighted shared scheduling and BCPack are not yet ported.

using System.Diagnostics;
using IraRationis.HLE.Storage.Models;

namespace IraRationis.HLE.Storage;

internal sealed class DirectStorageQueue : IDisposable
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<StorageCommand> _pending = [];
    private readonly long _storageQueueId = Interlocked.Increment(ref DirectStorageModule._nextStorageQueueId);
    private readonly Queue<StorageCommand> _submitted = new();
    private readonly Thread _worker;
    private bool _closed;
    private long _completedStorageRequests;
    private long _failedStorageRequests;
    private int _firstFailure;
    private bool _running;
    private long _storageOutputBytes;
    private long _storageStatusCount;

    public DirectStorageQueue(int capacity)
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
                    DirectStorageModule.Discard(command);
                }

                _pending.Clear();

                while (_submitted.TryDequeue(out var command))
                {
                    DirectStorageModule.Discard(command);
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

    private void TraceCompletion(StorageRequest request, int result, int bytesWritten, long started)
    {
        _completedStorageRequests++;

        if (result < 0)
        {
            _failedStorageRequests++;
        }
        else
        {
            _storageOutputBytes += bytesWritten;
        }

        if (Sample(_completedStorageRequests) || (result < 0 && Sample(_failedStorageRequests)))
        {
            Console.Error.WriteLine(
                $"[dstorage] IRX-R32-REQUEST: queue={_storageQueueId}; completed={_completedStorageRequests}; failed={_failedStorageRequests}; options=0x{request.Options:X}; sourceBytes={request.SourceSize}; expectedBytes={request.DestinationSize}; successfulBytes={(result >= 0 ? bytesWritten : 0)}; cumulativeOutputBytes={_storageOutputBytes}; hr=0x{result:X8}; ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}; strictLengths={DirectStorageModule.StrictStorageLengths}.");
        }
    }

    public void Enqueue(StorageCommand command)
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
                DirectStorageModule.Discard(command);

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
        for (;;)
        {
            StorageCommand command;

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
                    var array = DirectStorageModule.State<StorageStatusArray>(command.Status);
                    var status = _firstFailure;

                    Volatile.Write(ref array.Entries[command.Index], status);

                    _firstFailure = 0;

                    if (DirectStorageModule.EnableStorageTrace && (Sample(++_storageStatusCount) || status < 0))
                    {
                        Console.Error.WriteLine(
                            $"[dstorage] IRX-R32-STATUS: queue={_storageQueueId}; markers={_storageStatusCount}; index={command.Index}; hr=0x{status:X8}; completed={_completedStorageRequests}; failed={_failedStorageRequests}.");
                    }
                }
                else
                {
                    var started = DirectStorageModule.EnableStorageTrace ? Stopwatch.GetTimestamp() : 0;
                    var result = DirectStorageModule.Execute(command.Request, command.File, out var bytesWritten);

                    if (result < 0 && _firstFailure == 0)
                    {
                        _firstFailure = result;
                    }

                    if (DirectStorageModule.EnableStorageTrace)
                    {
                        TraceCompletion(command.Request, result, bytesWritten, started);
                    }
                }
            }
            finally
            {
                DirectStorageModule.Drop(command);

                lock (_gate)
                {
                    _running = false;

                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
