// SPDX-License-Identifier: GPL-3.0-or-later

using TerraFX.Interop.DirectX;
using GraphicsDevice = global::IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Media;

/// <summary>
///     Thread-affine host-media device and bounded pipeline cache.
///     Each presenter owns its own references and completion fence. Dispose after all presenters.
///     A runtime-backed context retains the runtime host device but owns a separate direct queue.
///     This context does not own or publish guest resources.
/// </summary>
public sealed unsafe class MediaGpuContext : IDisposable
{
    private const int MaximumPipelines = 8;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, (nint Root, nint Pipeline)> _pipelines = new(StringComparer.Ordinal);
    private readonly TextWriter _report;
    private readonly bool _runtimeBacked;
    private int _acquisitions;
    private nint _device;
    private bool _disposed;
    private int _hits;
    private int _misses;
    private nint _queue;

    public MediaGpuContext(TextWriter report, bool useRuntimeDevice = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        _report = report;

        _runtimeBacked = useRuntimeDevice;

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("D3D12 requires Windows.");
        }

        try
        {
            if (useRuntimeDevice)
            {
                _device = AcquireRuntimeHostDevice();
            }
            else
            {
                var device = nint.Zero;
                var deviceId = GraphicsDevice.DeviceId;
                var deviceResult = GraphicsDevice.D3D12CreateDevice(0, 0xC000, &deviceId, &device);

                _device = device;

                GraphicsDevice.CheckHostResult(deviceResult, "R44 device");

                GraphicsDevice.RequireHostObject(_device, "device");
            }

            var queue = nint.Zero;
            var queueId = GraphicsDevice.QueueId;
            var description = default(D3D12_COMMAND_QUEUE_DESC);
            var create =
                (delegate* unmanaged[Stdcall]<nint, D3D12_COMMAND_QUEUE_DESC*, Guid*, nint*, int>)GraphicsDevice.Slot(_device, 8);
            var result = create(_device, &description, &queueId, &queue);

            _queue = queue;

            GraphicsDevice.CheckHostResult(result, "R44 queue");

            GraphicsDevice.RequireHostObject(_queue, "queue");
        }
        catch
        {
            GraphicsDevice.ReleaseHostObject(_queue);

            GraphicsDevice.ReleaseHostObject(_device);

            throw;
        }
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
            GraphicsDevice.ReleaseHostObject(entry.Pipeline);

            GraphicsDevice.ReleaseHostObject(entry.Root);
        }

        _pipelines.Clear();

        GraphicsDevice.ReleaseHostObject(_queue);

        GraphicsDevice.ReleaseHostObject(_device);

        _queue = 0;

        _device = 0;

        var deviceCreations = _runtimeBacked ? 0 : 1;

        _report.WriteLine(
            $"[media] IRX-R45: runtimeBacked={_runtimeBacked}; contextDeviceCreations={deviceCreations}; " +
            $"queueCreations=1; presenterAcquisitions={_acquisitions}; " +
            $"pipelineMisses={_misses}; pipelineHits={_hits}.");
    }

    // Use the existing runtime initialization path. A null output is its
    // capability-query form: Create initializes the process-owned device
    // but does not return or retain a guest interface. No guest resource is
    // materialized or made visible by acquiring this host COM reference.
    private static nint AcquireRuntimeHostDevice()
    {
        var create = (delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)GraphicsDevice.ExportAddress;
        var version = 0x00020004U;
        var id = GraphicsDevice.DeviceId;
        var result = create(0, &version, &id, null);

        GraphicsDevice.CheckHostResult(result, "R45 runtime device initialization");

        lock (GraphicsDevice.Gate)
        {
            var device = (nint)GraphicsDevice._host;

            GraphicsDevice.RequireHostObject(device, "runtime host device");

            AddReference(device);

            return device;
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

            GraphicsDevice.CreateNv12ConversionPipeline(_device, _report, out var createdRoot, out var createdPipeline, shader);

            entry = (createdRoot, createdPipeline);

            try
            {
                _pipelines.Add(shader, entry);
            }
            catch
            {
                GraphicsDevice.ReleaseHostObject(createdPipeline);

                GraphicsDevice.ReleaseHostObject(createdRoot);

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
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)GraphicsDevice.Slot(value, 1))(value);
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
}
