// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int RectangleUploadAlignment = 256;
    private const int RectangleUploadPageBytes = 64 * 1024;
    private const int MaximumSharedRectangleUpload = RectangleUploadPageBytes / 4;
    private const int LargeRectangleUploadPageBytes = 4 * 1024 * 1024;
    private const int MinimumLargeRectangleUpload = 256 * 1024;
    private const int MaximumSharedLargeRectangleUpload = LargeRectangleUploadPageBytes / 2;
    private const int RecycledUploadAlignment = 64 * 1024;
    private static readonly bool UseRectangleUploadPages =
        Environment.GetEnvironmentVariable("IRX_RECTANGLE_UPLOAD_PAGES") != "0";
    private static readonly bool UseLargeRectangleUploadPages = UseRectangleUploadPages &&
        Environment.GetEnvironmentVariable("IRX_RECTANGLE_UPLOAD_LARGE_PAGES") == "1";
    private static readonly RectangleUploadBufferPool? RectangleUploadPool =
        Environment.GetEnvironmentVariable("IRX_RECTANGLE_UPLOAD_RECYCLE") == "1" ? new() : null;
    private static long _rectangleUploadSlices;
    private static long _rectangleUploadAllocations;
    private static long _rectangleUploadAllocatedBytes;
    private static long _largeRectangleUploadPages;
    private static long _largeRectangleUploadReuses;
    private static long _largeRectangleUploadFallbacks;
    private static long _recycledRectangleUploadBuffers;

    private static bool TryReserveRectangleUpload(RecordedCommandState state, int bytes, out RecordedUploadSlice slice)
    {
        slice = default;

        var pageBytes = GetRectangleUploadPageSize(bytes, UseRectangleUploadPages, UseLargeRectangleUploadPages);
        var largePage = pageBytes == LargeRectangleUploadPageBytes;
        var currentPage = largePage ? state.LargeRectangleUploadPage : state.RectangleUploadPage;

        if (pageBytes != 0 && currentPage is { } page &&
            TryPlaceRectangleUpload(pageBytes, page.UsedBytes, bytes, out var offset))
        {
            slice = new RecordedUploadSlice(page.Host, page.GpuAddress + (uint)offset, offset);
            SetRectangleUploadPage(state, largePage, page with { UsedBytes = offset + bytes });
            state.RectangleUploadSliceCount++;
            Interlocked.Increment(ref _rectangleUploadSlices);

            if (largePage)
            {
                Interlocked.Increment(ref _largeRectangleUploadReuses);
            }

            return true;
        }

        if (bytes <= 0 || state.UploadBytes > UploadBudget || (ulong)bytes > UploadBudget - state.UploadBytes ||
            state.HostViews.Count >= MaximumCommandHostViews)
        {
            return false;
        }

        var remainingBudget = UploadBudget - state.UploadBytes;
        if ((ulong)pageBytes > remainingBudget)
        {
            pageBytes = 0;
        }

        var allocationBytes = pageBytes == 0 ? bytes : pageBytes;
        _ = state.HostViews.EnsureCapacity(state.HostViews.Count + 1);

        var buffer = AcquireRectangleUploadBuffer(state, allocationBytes, remainingBudget, out var error);

        if (largePage && (pageBytes == 0 || buffer.Host == 0))
        {
            Interlocked.Increment(ref _largeRectangleUploadFallbacks);

            if (pageBytes != 0)
            {
                pageBytes = 0;
                buffer = AcquireRectangleUploadBuffer(state, bytes, remainingBudget, out error);
            }
        }

        if (buffer.Host == 0)
        {
            return RejectRectangleDraw(error ?? "host upload allocation failed");
        }

        slice = new RecordedUploadSlice(buffer.Host, buffer.GpuAddress, 0);
        // One native reference per resource, retired with the command recording.
        state.HostViews.Add(buffer.Host);

        if (pageBytes != 0)
        {
            SetRectangleUploadPage(state, largePage, new RecordedUploadPage(buffer.Host, buffer.GpuAddress, bytes));

            if (largePage)
            {
                Interlocked.Increment(ref _largeRectangleUploadPages);
            }
        }

        state.UploadBytes += (ulong)buffer.Bytes;
        state.RectangleUploadSliceCount++;
        Interlocked.Increment(ref _rectangleUploadSlices);

        return true;
    }

    private static RectangleUploadBuffer AcquireRectangleUploadBuffer(RecordedCommandState state, int requestedBytes,
        ulong remainingBudget, out string? error)
    {
        error = null;
        var device = (nint)_host;
        var bytes = requestedBytes;
        Dictionary<nint, RectangleUploadBuffer>? recycledBuffers = null;
        RectangleUploadBuffer buffer = default;

        if (RectangleUploadPool is { } pool && TryGetRecycledUploadSize(requestedBytes, remainingBudget, out var roundedBytes))
        {
            bytes = roundedBytes;
            recycledBuffers = state.RectangleUploadBuffers ??= new();
            _ = recycledBuffers.EnsureCapacity(recycledBuffers.Count + 1);

            if (pool.TryRent(device, bytes, out buffer))
            {
                Interlocked.Increment(ref _recycledRectangleUploadBuffers);
            }
        }

        if (buffer.Host == 0)
        {
            var host = CreateRectangleUploadResource(bytes, out var gpuAddress, out error);

            if (host == 0 && bytes != requestedBytes)
            {
                bytes = requestedBytes;
                recycledBuffers = null;
                host = CreateRectangleUploadResource(bytes, out gpuAddress, out error);
            }

            if (host == 0)
            {
                return default;
            }

            buffer = new RectangleUploadBuffer(device, host, gpuAddress, bytes);
            Interlocked.Increment(ref _rectangleUploadAllocations);
            Interlocked.Add(ref _rectangleUploadAllocatedBytes, bytes);
        }

        recycledBuffers?.Add(buffer.Host, buffer);

        return buffer;
    }

    private static bool TryGetRecycledUploadSize(int bytes, ulong remainingBudget, out int roundedBytes)
    {
        roundedBytes = 0;

        if (bytes is < MinimumLargeRectangleUpload or > LargeRectangleUploadPageBytes)
        {
            return false;
        }

        roundedBytes = (bytes + RecycledUploadAlignment - 1) & ~(RecycledUploadAlignment - 1);

        return (ulong)roundedBytes <= remainingBudget;
    }

    private static bool CanRecycleRectangleUploads(RecordedCommandState state, bool reset, int completedSlices)
    {
        // Completion runs after the rectangle segment fence. The following segment has new bindings.
        return reset && state.RectangleUploadBuffers is { Count: > 0 } &&
            completedSlices == state.RectangleUploadSliceCount;
    }

    private static bool WasCompletedRectangleSlice(RectangleDrawBoundary rectangle)
    {
        return rectangle.Complete && Volatile.Read(ref rectangle.Upload.Phase) == 2;
    }

    private static nint CreateRectangleUploadResource(int bytes, out ulong gpuAddress, out string? error)
    {
        using var stageTiming = BeginTiming(GraphicsStage.AllocateRectangleUpload);
        gpuAddress = 0;
        error = null;
        nint native = 0;

        try
        {
            var resultCode = CreateHostResource(HostBufferDescription((ulong)bytes), 2, GenericRead, &native);

            if (resultCode < 0 || native == 0)
            {
                error = $"host upload allocation HRESULT=0x{resultCode:X8}";

                return 0;
            }

            gpuAddress = ((ID3D12Resource*)native)->GetGPUVirtualAddress();

            if (gpuAddress == 0 || (gpuAddress & (RectangleUploadAlignment - 1)) != 0 ||
                (ulong)bytes > ulong.MaxValue - gpuAddress)
            {
                error = "host upload GPU address is invalid";

                return 0;
            }

            var resource = native;
            native = 0;

            return resource;
        }
        finally
        {
            if (native != 0)
            {
                _ = HostRelease(native);
            }
        }
    }

    private static int GetRectangleUploadPageSize(int bytes, bool smallPages, bool largePages)
    {
        if (smallPages && bytes is > 0 and <= MaximumSharedRectangleUpload)
        {
            return RectangleUploadPageBytes;
        }

        return largePages && bytes is >= MinimumLargeRectangleUpload and <= MaximumSharedLargeRectangleUpload
            ? LargeRectangleUploadPageBytes : 0;
    }

    private static void SetRectangleUploadPage(RecordedCommandState state, bool largePage, RecordedUploadPage page)
    {
        if (largePage)
        {
            state.LargeRectangleUploadPage = page;
        }
        else
        {
            state.RectangleUploadPage = page;
        }
    }

    private static bool TryPlaceRectangleUpload(int pageBytes, int usedBytes, int bytes, out int start)
    {
        start = 0;

        if (usedBytes < 0 || usedBytes > pageBytes || bytes <= 0)
        {
            return false;
        }

        start = (usedBytes + RectangleUploadAlignment - 1) & ~(RectangleUploadAlignment - 1);

        return bytes <= pageBytes - start;
    }

    private static void VerifyRectangleUploadPlacement()
    {
        if (!TryPlaceRectangleUpload(RectangleUploadPageBytes, 0, 263, out var first) || first != 0 ||
            !TryPlaceRectangleUpload(RectangleUploadPageBytes, first + 263, 327, out var next) || next != 512 ||
            !TryPlaceRectangleUpload(RectangleUploadPageBytes, RectangleUploadPageBytes - 256, 256, out var last) ||
            last != RectangleUploadPageBytes - 256 ||
            TryPlaceRectangleUpload(RectangleUploadPageBytes, RectangleUploadPageBytes - 255, 1, out _) ||
            TryPlaceRectangleUpload(RectangleUploadPageBytes, int.MaxValue, 1, out _))
        {
            throw new InvalidOperationException("Rectangle upload page alignment, overlap, or capacity check failed.");
        }

        Console.WriteLine("[graphics-probe] Rectangle upload page alignment and capacity passed (CPU only).");

        const int representativeUploadBytes = 852_992;
        const int largeUploadEnd = representativeUploadBytes * 4;

        if (GetRectangleUploadPageSize(representativeUploadBytes, true, false) != 0 ||
            GetRectangleUploadPageSize(representativeUploadBytes, true, true) != LargeRectangleUploadPageBytes ||
            GetRectangleUploadPageSize(MinimumLargeRectangleUpload - 1, true, true) != 0 ||
            GetRectangleUploadPageSize(MaximumSharedLargeRectangleUpload + 1, true, true) != 0 ||
            !TryPlaceRectangleUpload(LargeRectangleUploadPageBytes, representativeUploadBytes,
                representativeUploadBytes, out var largeOffset) || largeOffset != representativeUploadBytes ||
            !TryPlaceRectangleUpload(LargeRectangleUploadPageBytes, largeUploadEnd - representativeUploadBytes,
                representativeUploadBytes, out var fourthOffset) || fourthOffset + representativeUploadBytes != largeUploadEnd ||
            TryPlaceRectangleUpload(LargeRectangleUploadPageBytes, largeUploadEnd, representativeUploadBytes, out _))
        {
            throw new InvalidOperationException("Large rectangle upload page classification or capacity check failed.");
        }

        Console.WriteLine("[graphics-probe] Large rectangle upload page classification and capacity passed (CPU only).");

        VerifyRectangleUploadRecycling();
    }

    private static void VerifyRectangleUploadRecycling()
    {
        const int requestedBytes = 852_993;
        const int allocationBytes = 917_504;

        if (!TryGetRecycledUploadSize(requestedBytes, allocationBytes, out var roundedBytes) ||
            roundedBytes != allocationBytes || TryGetRecycledUploadSize(requestedBytes, requestedBytes, out _) ||
            TryGetRecycledUploadSize(RectangleUploadPageBytes, UploadBudget, out _))
        {
            throw new InvalidOperationException("Rectangle upload recycling size class or budget check failed.");
        }

        var buffer = new RectangleUploadBuffer(1, 2, 0x10000, allocationBytes);
        var pool = new RectangleUploadBufferPool();
        if (pool.TryRent(buffer.Device, allocationBytes, out _) || !pool.TryReturn(buffer) ||
            pool.TryRent(buffer.Device, allocationBytes - RecycledUploadAlignment, out _) ||
            !pool.TryRent(buffer.Device, allocationBytes, out var rented) || rented != buffer ||
            pool.TryRent(buffer.Device, allocationBytes, out _))
        {
            throw new InvalidOperationException("Rectangle upload pool did not return an exclusive size-matched buffer.");
        }

        var state = new RecordedCommandState
        {
            RectangleUploadBuffers = new() { [buffer.Host] = buffer },
            RectangleUploadSliceCount = 1
        };
        var upload = new RectangleUpload(0, 0, 0, default);
        var boundary = new RectangleDrawBoundary(upload, true);
        if (WasCompletedRectangleSlice(boundary))
        {
            throw new InvalidOperationException("Unsubmitted rectangle allowed upload recycling.");
        }

        upload.Phase = 1;
        if (WasCompletedRectangleSlice(boundary) || CanRecycleRectangleUploads(state, true, 0))
        {
            throw new InvalidOperationException("In-flight rectangle allowed upload recycling.");
        }

        upload.Phase = 2;
        if (!WasCompletedRectangleSlice(boundary) || WasCompletedRectangleSlice(boundary with { Complete = false }) ||
            CanRecycleRectangleUploads(state, false, 1) || !CanRecycleRectangleUploads(state, true, 1))
        {
            throw new InvalidOperationException("Rectangle upload recycling retirement check failed.");
        }

        Console.WriteLine("[graphics-probe] Rectangle upload recycling size, budget, and retirement checks passed (CPU only).");
    }
}
