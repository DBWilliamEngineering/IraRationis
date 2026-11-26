// SPDX-License-Identifier: GPL-3.0-or-later

using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumIdleUtilityContexts = 8;
    private const ulong MaximumIdleUtilityBytes = 64UL << 20;
    private static readonly bool UtilityPoolingEnabled =
        Environment.GetEnvironmentVariable("IRX_RUNTIME_UTILITY_POOL") != "0";
    private static readonly Lock UtilityPoolGate = new();
    private static readonly List<UtilityContext> IdleUtilityContexts = new();
    private static ulong _idleUtilityBytes;
    private static long _utilityCreated, _utilityReused, _utilityUploadBytes, _utilityUploadSkipped;

    // Only completed DIRECT utility work is pooled. Never use this for a guest-owned
    // allocator, a deferred command list, or a resource with unproven GPU completion.
    private static UtilityContext AcquireUtilityContext(ulong uploadBytes, uint descriptorCount,
        bool versioned = false)
    {
        if (_host == null || descriptorCount > 64 || uploadBytes > MaximumHostResourceBytes ||
            (versioned && (uploadBytes == 0 || uploadBytes > 16UL * 1024 * 1024)))
        {
            throw new ArgumentOutOfRangeException(nameof(uploadBytes), "Invalid utility context request.");
        }

        var device = (nint)_host;
        var owner = Environment.CurrentManagedThreadId;
        UtilityContext? context = null;

        lock (UtilityPoolGate)
        {
            for (var index = IdleUtilityContexts.Count - 1; index >= 0; index--)
            {
                var candidate = IdleUtilityContexts[index];

                if (candidate.Device != device || candidate.UploadBytes != uploadBytes ||
                    candidate.DescriptorCount != descriptorCount || candidate.Versioned != versioned ||
                    (versioned && candidate.Owner != owner))
                {
                    continue;
                }

                IdleUtilityContexts.RemoveAt(index);

                _idleUtilityBytes -= candidate.UploadBytes;

                context = candidate;

                break;
            }
        }

        if (context is null)
        {
            context = new UtilityContext(device, uploadBytes, descriptorCount, versioned);

            Interlocked.Increment(ref _utilityCreated);
        }
        else
        {
            try
            {
                context.Reset();
            }
            catch
            {
                context.Destroy();

                throw;
            }

            Interlocked.Increment(ref _utilityReused);
        }

        return context;
    }

    private static void ReturnUtilityContext(UtilityContext context)
    {
        lock (UtilityPoolGate)
        {
            if (UtilityPoolingEnabled && context.Device == (nint)_host &&
                IdleUtilityContexts.Count < MaximumIdleUtilityContexts &&
                context.UploadBytes <= MaximumIdleUtilityBytes - _idleUtilityBytes)
            {
                IdleUtilityContexts.Add(context);

                _idleUtilityBytes += context.UploadBytes;

                return;
            }
        }

        context.Destroy();
    }

    private static void ReportUtilityExecution()
    {
        TraceR24($"[graphics] IRX-R53-UTILITY: created={Interlocked.Read(ref _utilityCreated)}; " +
                 $"reused={Interlocked.Read(ref _utilityReused)}; " +
                 $"versionedUploadBytes={Interlocked.Read(ref _utilityUploadBytes)}; " +
                 $"unchangedUploads={Interlocked.Read(ref _utilityUploadSkipped)}.");
    }

    private sealed class UtilityContext : IDisposable
    {
        private readonly VersionedUploadBuffer? _versions;
        private VersionedUploadBuffer.Snapshot? _snapshot;
        private bool _prepared;
        private bool _submitted;
        private bool _completed;
        private bool _returned;

        internal readonly nint Device;
        internal readonly ulong UploadBytes;
        internal readonly uint DescriptorCount;
        internal readonly int Owner = Environment.CurrentManagedThreadId;
        internal readonly bool Versioned;

        internal nint Allocator;
        internal nint List;
        internal nint Upload;
        internal nint Heap;

        internal int LastUploadBytes
        {
            get;
            private set;
        }

        internal UtilityContext(nint device, ulong uploadBytes, uint descriptorCount, bool versioned)
        {
            Device = device;
            UploadBytes = uploadBytes;
            DescriptorCount = descriptorCount;
            Versioned = versioned;

            if (versioned)
            {
                _versions = new VersionedUploadBuffer(checked((int)uploadBytes));
            }

            try
            {
                CreateR16UtilityList(out Allocator, out List);

                if (uploadBytes != 0)
                {
                    var upload = nint.Zero;

                    var hr = CreateHostResource(HostBufferDescription(uploadBytes), 2, GenericRead, &upload);

                    Upload = upload;

                    CheckHostResult(hr, "R53 utility upload allocation");

                    RequireR42Object(Upload, "R53 utility upload");
                }

                if (descriptorCount != 0)
                {
                    var description = new DescriptorHeapDescription { Kind = 0, Count = descriptorCount, Flags = 1 };
                    var iid = DescriptorHeapId;
                    var heap = nint.Zero;
                    var create = (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)
                        Slot(device, 14);

                    var hr = create(device, &description, &iid, &heap);

                    Heap = heap;

                    CheckHostResult(hr, "R53 utility descriptor heap");

                    RequireR42Object(Heap, "R53 utility descriptor heap");
                }
            }
            catch
            {
                Destroy();

                throw;
            }
        }

        internal void Reset()
        {
            if (!_completed || _snapshot is not null)
            {
                throw new InvalidOperationException("R53 utility context is not retired.");
            }

            _completed = false;
            _submitted = false;

            CheckHostResult(((ID3D12CommandAllocator*)Allocator)->Reset(), "R53 utility allocator reset");

            CheckHostResult(((ID3D12GraphicsCommandList*)List)->Reset((ID3D12CommandAllocator*)Allocator, null),
                "R53 utility list reset");

            _returned = false;
            _prepared = false;
        }

        // Compare explicitly owned snapshots, not raw guest pointers. Never read mapped
        // write-combined upload memory. Split writes to keep unchanged blocks clean.
        internal void PrepareVersionedUpload(ReadOnlySpan<byte> data)
        {
            if (_versions is null || _prepared || _submitted || _returned || (ulong)data.Length != UploadBytes)
            {
                throw new InvalidOperationException("R53 invalid versioned upload preparation.");
            }

            const int blockBytes = 256;

            for (var offset = 0; offset < data.Length; offset += blockBytes)
            {
                var count = Math.Min(blockBytes, data.Length - offset);

                _versions.Write(offset, data.Slice(offset, count));
            }

            _snapshot = _versions.Capture();
            _prepared = true;
            LastUploadBytes = 0;

            if (_snapshot is not { } snapshot)
            {
                Interlocked.Increment(ref _utilityUploadSkipped);

                return;
            }

            var noRead = default(HostRange);
            var mapped = (void*)null;
            var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(Upload, 8);
            var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(Upload, 9);

            CheckHostResult(map(Upload, 0, &noRead, &mapped), "R53 versioned upload map");

            var written = default(HostRange);

            try
            {
                if (mapped == null)
                {
                    throw new InvalidOperationException("R53 versioned upload mapping is null.");
                }

                snapshot.Bytes.CopyTo(new Span<byte>((byte*)mapped + snapshot.Offset, snapshot.Bytes.Length));

                written.Begin = (nuint)snapshot.Offset;
                written.End = (nuint)(snapshot.Offset + snapshot.Bytes.Length);

                LastUploadBytes = snapshot.Bytes.Length;

                Interlocked.Add(ref _utilityUploadBytes, snapshot.Bytes.Length);
            }
            finally
            {
                unmap(Upload, 0, &written);
            }
        }

        internal void SubmitAndWait(nint queue)
        {
            if (_returned || _submitted || (Versioned && !_prepared) || queue == 0)
            {
                throw new InvalidOperationException("R53 invalid utility submission.");
            }

            using var timing = BeginR25Timing(R25Stage.UtilitySubmit);

            CheckHostResult(((ID3D12GraphicsCommandList*)List)->Close(), "R53 utility close");

            // Establish the completion object before issuing work. If completion cannot
            // be established after execution, native resources must not be reclaimed.
            using var completion = new HostCompletionFence();

            if (_snapshot is { } snapshot)
            {
                _versions!.MarkSubmitted(snapshot);
            }

            var list = List;

            _submitted = true;

            try
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &list);

                completion.Wait(queue);

                _completed = true;

                if (_snapshot is { } done)
                {
                    _versions!.CompleteAfterFenceWait(done);

                    _snapshot = null;
                }
            }
            catch (Exception exception)
            {
                if (!_completed)
                {
                    Environment.FailFast("R53 utility GPU completion is unproven.", exception);
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            if (_submitted && !_completed)
            {
                Environment.FailFast("R53 attempted to retire an in-flight utility context.");
            }

            _returned = true;

            if (_completed && _snapshot is null)
            {
                ReturnUtilityContext(this);

                return;
            }

            // Aborted recording is discarded, never pooled. Its upload state and
            // native descriptors may contain partially prepared data.
            Destroy();
        }

        internal void Destroy()
        {
            ReleaseR42Object(List);
            ReleaseR42Object(Allocator);
            ReleaseR42Object(Heap);
            ReleaseR42Object(Upload);

            List = 0;
            Allocator = 0;
            Heap = 0;
            Upload = 0;
        }
    }
}
