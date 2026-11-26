// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;

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
    internal static long _utilityCreated, _utilityReused, _utilityUploadBytes, _utilityUploadSkipped;

    // Only completed DIRECT utility work is pooled. Never use this for a guest-owned
    // allocator, a deferred command list, or a resource with unproven GPU completion.
    internal static UtilityContext AcquireUtilityContext(ulong uploadBytes, uint descriptorCount,
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

    internal static void ReturnUtilityContext(UtilityContext context)
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
        TraceGraphicsSuccess($"[graphics] IRX-R53-UTILITY: created={Interlocked.Read(ref _utilityCreated)}; " +
                 $"reused={Interlocked.Read(ref _utilityReused)}; " +
                 $"versionedUploadBytes={Interlocked.Read(ref _utilityUploadBytes)}; " +
                 $"unchangedUploads={Interlocked.Read(ref _utilityUploadSkipped)}.");
    }
}
