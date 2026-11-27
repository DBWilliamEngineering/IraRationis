// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Graphics.Synchronization;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Commands;

/// <summary>Owns submitted utility lists until a subsequent same-queue segment fence.</summary>
internal sealed class OrderedTextureUploads(nint queue, bool batchIndependentUploads = false) : IDisposable
{
    internal const int MaximumPending = 8;
    private readonly List<UtilityContext> _pending = [];
    private readonly List<nint>? _queuedDestinations =
        batchIndependentUploads ? [with(MaximumPending)] : null;
    private int _submittedCount;

    internal bool HasPendingUploads
    {
        get
        {
            return _pending.Count != 0;
        }
    }

    public void Dispose()
    {
        Flush();
    }

    internal void Submit(UtilityContext context, nint destination = 0)
    {
        if (_pending.Count >= MaximumPending)
        {
            Flush();
        }

        if (_queuedDestinations is { } destinations && destination != 0)
        {
            // Separate submissions maintain ordering if a destination repeats in one batch.
            if (destinations.Contains(destination))
            {
                SubmitBeforeConsumer();
            }

            _pending.Add(context);

            destinations.Add(destination);

            return;
        }

        SubmitBeforeConsumer();
        _pending.Add(context);
        try
        {
            context.SubmitOrdered(queue);
            _submittedCount++;
        }
        catch
        {
            _pending.RemoveAt(_pending.Count - 1);

            throw;
        }
    }

    internal void SubmitBeforeConsumer()
    {
        var queued = _pending.Count - _submittedCount;

        if (queued == 0)
        {
            return;
        }

        try
        {
            UtilityContext.SubmitIndependentUploads(queue, CollectionsMarshal.AsSpan(_pending)[_submittedCount..]);
        }
        catch
        {
            // Submission preparation failed. Discard these lists without pooling them.
            for (var index = _submittedCount; index < _pending.Count; index++)
            {
                _pending[index].Dispose();
            }

            _pending.RemoveRange(_submittedCount, queued);
            _queuedDestinations!.Clear();

            throw;
        }

        _submittedCount = _pending.Count;
        _queuedDestinations!.Clear();

        GraphicsDevice.RecordTextureUploadSubmitsSaved(queued - 1);
    }

    internal void CompleteAfterFence()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var count = _pending.Count;

        if (_submittedCount != count)
        {
            throw new InvalidOperationException("Texture upload completion is missing a submission.");
        }

        foreach (var context in _pending)
        {
            context.CompleteOrdered();
            context.Dispose();
        }

        _pending.Clear();
        _submittedCount = 0;

        GraphicsDevice.ReportOrderedTextureUploads(count);
    }

    private void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        SubmitBeforeConsumer();

        using var completion = new HostCompletionFence();

        completion.Wait(queue);

        CompleteAfterFence();
    }
}
