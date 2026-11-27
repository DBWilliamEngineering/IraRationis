// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Drawing.Models;
using GraphicsDevice = global::IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Drawing;

// Cache references are independent of recorded command-list references.
// Eviction cannot retire an object still owned by a recording.
// Only immutable native pipeline objects are cached; bindings remain per list.
internal sealed unsafe class NativePipelineCache<TKey, TValue> where TKey : class
    where TValue : class
{
    private const int Capacity = 64;
    private readonly Dictionary<(nint Device, TKey Metadata), LinkedListNode<NativePipelineCacheEntry<TKey, TValue>>> _entries = new();
    private readonly LinkedList<NativePipelineCacheEntry<TKey, TValue>> _recency = new();
    private readonly Lock _gate = new();
    private readonly Func<TValue, (nint Root, nint Pipeline)> _handles;
    private readonly string _name;
    private long _hits, _misses, _evictions;

    public NativePipelineCache(string name, Func<TValue, (nint Root, nint Pipeline)> handles)
    {
        _name = name;

        _handles = handles;
    }

    public TValue? Acquire(TKey metadata, RecordedCommandState state)
    {
        TValue? value = null;
        long hits, misses, evictions;
        int count;

        lock (_gate)
        {
            if (_entries.TryGetValue(((nint)GraphicsDevice._host, metadata), out var entry))
            {
                // Reserve recording storage before acquiring its two references.
                _ = state.HostViews.EnsureCapacity(checked(state.HostViews.Count + 2));

                var handles = _handles(entry.Value.Value);

                GraphicsDevice.RetainHostObject(handles.Root);

                GraphicsDevice.RetainHostObject(handles.Pipeline);

                state.HostViews.Add(handles.Root);

                state.HostViews.Add(handles.Pipeline);

                Touch(entry);

                value = entry.Value.Value;

                _hits++;
            }
            else
            {
                _misses++;
            }

            hits = _hits;
            misses = _misses;
            evictions = _evictions;
            count = _entries.Count;
        }

        // Never hold the native cache lock while writing diagnostics.
        var observations = value is null ? misses : hits;

        if (GraphicsDevice.EnableVerboseGraphics || observations <= 2 || (observations & (observations - 1)) == 0)
        {
            Console.Error.WriteLine($"[graphics] IRX-R69-PSO: name={_name}; hits={hits}; misses={misses}; evictions={evictions}; entries={count}.");
        }

        return value;
    }

    public void Publish(TKey metadata, TValue value)
    {
        lock (_gate)
        {
            var key = ((nint)GraphicsDevice._host, metadata);

            if (_entries.TryGetValue(key, out var existing))
            {
                Touch(existing);

                return;
            }

            var node = new LinkedListNode<NativePipelineCacheEntry<TKey, TValue>>(new NativePipelineCacheEntry<TKey, TValue>(key, value));
            var handles = _handles(value);

            GraphicsDevice.RetainHostObject(handles.Root);

            GraphicsDevice.RetainHostObject(handles.Pipeline);

            try
            {
                _entries.Add(key, node);
            }
            catch
            {
                _ = GraphicsDevice.HostRelease(handles.Pipeline);

                _ = GraphicsDevice.HostRelease(handles.Root);

                throw;
            }

            _recency.AddFirst(node);

            if (_entries.Count <= Capacity)
            {
                return;
            }

            var victim = _recency.Last!;
            var retired = _handles(victim.Value.Value);

            _ = _entries.Remove(victim.Value.Key);

            _recency.RemoveLast();

            _evictions++;

            // Release the cache's references only. Recording ownership
            // and existing completion boundaries remain unchanged.
            _ = GraphicsDevice.HostRelease(retired.Pipeline);

            _ = GraphicsDevice.HostRelease(retired.Root);
        }
    }

    private void Touch(LinkedListNode<NativePipelineCacheEntry<TKey, TValue>> node)
    {
        if (ReferenceEquals(_recency.First, node))
        {
            return;
        }

        _recency.Remove(node);

        _recency.AddFirst(node);
    }
}
