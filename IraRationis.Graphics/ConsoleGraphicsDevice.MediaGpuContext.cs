// SPDX-License-Identifier: GPL-3.0-or-later

using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    /// <summary>Thread-affine host-media device and bounded pipeline cache.
    /// Each presenter owns its own references and completion fence. Dispose after all presenters.
    /// This context does not own or publish guest resources.</summary>
    public sealed unsafe class MediaGpuContext : IDisposable
    {
        private const int MaximumPipelines = 8;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly TextWriter _report;
        private readonly Dictionary<string, (nint Root, nint Pipeline)> _pipelines = new(StringComparer.Ordinal);
        private nint _device;
        private nint _queue;
        private bool _disposed;
        private int _acquisitions;
        private int _hits;
        private int _misses;

        public MediaGpuContext(TextWriter report)
        {
            ArgumentNullException.ThrowIfNull(report);
            _report = report;

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("D3D12 requires Windows.");
            }

            try
            {
                var device = nint.Zero;
                var deviceId = DeviceId;
                var result = D3D12CreateDevice(0, 0xC000, &deviceId, &device);
                _device = device;
                CheckHostResult(result, "R44 device");
                RequireR42Object(_device, "device");

                var queue = nint.Zero;
                var queueId = QueueId;
                var description = default(D3D12_COMMAND_QUEUE_DESC);
                var create = (delegate* unmanaged[Stdcall]<nint, D3D12_COMMAND_QUEUE_DESC*, Guid*, nint*, int>)Slot(_device, 8);
                result = create(_device, &description, &queueId, &queue);
                _queue = queue;
                CheckHostResult(result, "R44 queue");
                RequireR42Object(_queue, "queue");
            }
            catch
            {
                ReleaseR42Object(_queue);
                ReleaseR42Object(_device);

                throw;
            }
        }

        internal void AcquireDeviceQueue(out nint device, out nint queue)
        {
            CheckUsable();
            AddReference(_device);
            AddReference(_queue);
            device = _device;
            queue = _queue;
            _acquisitions++;
        }

        internal void AcquirePipeline(string shader, out nint root, out nint pipeline)
        {
            CheckUsable();
            if (!_pipelines.TryGetValue(shader, out var entry))
            {
                if (_pipelines.Count >= MaximumPipelines)
                {
                    throw new InvalidOperationException("R44 host media pipeline cache capacity reached.");
                }

                CreateR42Pipeline(_device, _report, out var createdRoot, out var createdPipeline, shader);
                entry = (createdRoot, createdPipeline);
                try
                {
                    _pipelines.Add(shader, entry);
                }
                catch
                {
                    ReleaseR42Object(createdPipeline);
                    ReleaseR42Object(createdRoot);

                    throw;
                }

                _misses++;
            }
            else
            {
                _hits++;
            }

            AddReference(entry.Root);
            AddReference(entry.Pipeline);
            root = entry.Root;
            pipeline = entry.Pipeline;
        }

        private static void AddReference(nint value)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 1))(value);
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != _owner)
            {
                throw new InvalidOperationException("R44 media context is thread-affine.");
            }
        }

        private void CheckUsable()
        {
            CheckThread();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            CheckThread();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Presenters hold separate COM references and drain their own GPU fences.
            // Only the cache's references are released here.
            foreach (var entry in _pipelines.Values)
            {
                ReleaseR42Object(entry.Pipeline);
                ReleaseR42Object(entry.Root);
            }

            _pipelines.Clear();
            ReleaseR42Object(_queue);
            ReleaseR42Object(_device);
            _queue = 0;
            _device = 0;
            _report.WriteLine($"[media] IRX-R44: deviceCreations=1; queueCreations=1; presenterAcquisitions={_acquisitions}; " +
                              $"pipelineMisses={_misses}; pipelineHits={_hits}.");
        }
    }
}
