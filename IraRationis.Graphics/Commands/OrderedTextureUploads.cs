// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Synchronization;

namespace IraRationis.Graphics.Commands;

/// <summary>Owns submitted utility lists until a subsequent same-queue segment fence.</summary>
internal sealed class OrderedTextureUploads(nint queue) : IDisposable
{
    private const int MaximumPending = 8;
    private readonly List<UtilityContext> _pending = [];

    public void Dispose()
    {
        Flush();
    }

    internal void Submit(UtilityContext context)
    {
        if (_pending.Count >= MaximumPending)
        {
            Flush();
        }

        // Reserve the list entry before issuing GPU work so allocation cannot lose its lifetime.
        _pending.Add(context);
        try
        {
            context.SubmitOrdered(queue);
        }
        catch
        {
            _pending.RemoveAt(_pending.Count - 1);

            throw;
        }
    }

    internal void CompleteAfterFence()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var count = _pending.Count;
        foreach (var context in _pending)
        {
            context.CompleteOrdered();
            context.Dispose();
        }

        _pending.Clear();
        ConsoleGraphicsDevice.ReportOrderedTextureUploads(count);
    }

    private void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        using var completion = new HostCompletionFence();
        completion.Wait(queue);
        CompleteAfterFence();
    }
}
