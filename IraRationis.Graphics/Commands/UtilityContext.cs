// SPDX-License-Identifier: GPL-3.0-or-later

using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Synchronization;
using GraphicsDevice = global::IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Commands;

internal sealed unsafe class UtilityContext : IDisposable
{
    private readonly VersionedUploadBuffer? _versions;
    private VersionedUploadBufferSnapshot? _snapshot;
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
            GraphicsDevice.CreateUtilityCommandList(out Allocator, out List);

            if (uploadBytes != 0)
            {
                var upload = nint.Zero;

                var resultCode = GraphicsDevice.CreateHostResource(GraphicsDevice.HostBufferDescription(uploadBytes), 2, GraphicsDevice.GenericRead, &upload);

                Upload = upload;

                GraphicsDevice.CheckHostResult(resultCode, "R53 utility upload allocation");

                GraphicsDevice.RequireHostObject(Upload, "R53 utility upload");
            }

            if (descriptorCount != 0)
            {
                var description = new DescriptorHeapDescription { Kind = 0, Count = descriptorCount, Flags = 1 };
                var iid = GraphicsDevice.DescriptorHeapId;
                var heap = nint.Zero;
                var create = (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)
                    GraphicsDevice.Slot(device, 14);

                var resultCode = create(device, &description, &iid, &heap);

                Heap = heap;

                GraphicsDevice.CheckHostResult(resultCode, "R53 utility descriptor heap");

                GraphicsDevice.RequireHostObject(Heap, "R53 utility descriptor heap");
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

        GraphicsDevice.CheckHostResult(((ID3D12CommandAllocator*)Allocator)->Reset(), "R53 utility allocator reset");

        GraphicsDevice.CheckHostResult(((ID3D12GraphicsCommandList*)List)->Reset((ID3D12CommandAllocator*)Allocator, null),
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
            Interlocked.Increment(ref GraphicsDevice._utilityUploadSkipped);

            return;
        }

        var noRead = default(HostRange);
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)GraphicsDevice.Slot(Upload, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)GraphicsDevice.Slot(Upload, 9);

        GraphicsDevice.CheckHostResult(map(Upload, 0, &noRead, &mapped), "R53 versioned upload map");

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

            Interlocked.Add(ref GraphicsDevice._utilityUploadBytes, snapshot.Bytes.Length);
        }
        finally
        {
            unmap(Upload, 0, &written);
        }
    }

    internal void SubmitAndWait(nint queue)
    {
        using var timing = GraphicsDevice.BeginTiming(GraphicsStage.UtilitySubmit);
        using var completion = new HostCompletionFence();

        try
        {
            SubmitOrdered(queue);
            completion.Wait(queue);
            CompleteOrdered();
        }
        catch (Exception exception)
        {
            if (_submitted && !_completed)
            {
                Environment.FailFast("R53 utility GPU completion is unproven.", exception);
            }
            throw;
        }
    }

    // Caller retains this context until a later fence on the same queue completes.
    internal void SubmitOrdered(nint queue)
    {
        if (_returned || _submitted || (Versioned && !_prepared) || queue == 0)
        {
            throw new InvalidOperationException("R53 invalid utility submission.");
        }

        GraphicsDevice.CheckHostResult(((ID3D12GraphicsCommandList*)List)->Close(), "R53 utility close");
        if (_snapshot is { } snapshot)
        {
            _versions!.MarkSubmitted(snapshot);
        }
        var list = List;
        _submitted = true;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)GraphicsDevice.Slot(queue, 10))(queue, 1, &list);
    }

    internal void CompleteOrdered()
    {
        if (!_submitted || _completed)
        {
            throw new InvalidOperationException("Utility completion without outstanding submission.");
        }
        _completed = true;
        if (_snapshot is { } done)
        {
            _versions!.CompleteAfterFenceWait(done);
            _snapshot = null;
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
            GraphicsDevice.ReturnUtilityContext(this);

            return;
        }

        // Aborted recording is discarded, never pooled. Its upload state and
        // native descriptors may contain partially prepared data.
        Destroy();
    }

    internal void Destroy()
    {
        GraphicsDevice.ReleaseHostObject(List);
        GraphicsDevice.ReleaseHostObject(Allocator);
        GraphicsDevice.ReleaseHostObject(Heap);
        GraphicsDevice.ReleaseHostObject(Upload);

        List = 0;
        Allocator = 0;
        Heap = 0;
        Upload = 0;
    }
}
