// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumIndexedUiSourceReports = 24;
    private static readonly bool IndexedUiSourceTraceEnabled =
        Environment.GetEnvironmentVariable("IRX_INDEXED_UI_SOURCE_TRACE") == "1";
    private static readonly Lock IndexedUiSourceTraceGate = new();
    private static readonly Dictionary<(IndexedUiVariant Variant, uint TargetFormat, int Slot),
        HashSet<(ulong Token, long ResourceIdentity, nint Writer)>> ReportedIndexedUiSources = new();

    private static void TraceIndexedUiSources(nint queue, IndexedUiUpload upload)
    {
        if (!IndexedUiSourceTraceEnabled)
        {
            return;
        }

        for (var slot = 0; slot < upload.Textures.Length; slot++)
        {
            var source = upload.Textures[slot].Source;
            var resource = (ResourceObject*)source.Resource;
            var writer = GetResourceWriterQueue(source.Resource);
            var group = (upload.Variant, upload.ColorFormat, slot);
            var identity = source.Kind == 70 ? (long)resource->Address : source.Generation;

            lock (IndexedUiSourceTraceGate)
            {
                if (!ReportedIndexedUiSources.TryGetValue(group, out var reported))
                {
                    reported = new HashSet<(ulong Token, long ResourceIdentity, IntPtr Writer)>();
                    ReportedIndexedUiSources.Add(group, reported);
                }

                if (reported.Count >= MaximumIndexedUiSourceReports || !reported.Add((source.Token, identity, writer)))
                {
                    continue;
                }
            }

            var description = resource->Description;
            var view = source.Description;
            var viewFormat = view is null ? description.Format : ReadDescriptorUInt32(view, 0);
            var viewDimension = view is null ? 4U : ReadDescriptorUInt32(view, 4);
            var mapping = view is null ? 5768U : ReadDescriptorUInt32(view, 8);
            var firstMip = view is null ? 0U : ReadDescriptorUInt32(view, 16);
            var mips = view is null ? description.MipLevels : ReadDescriptorUInt32(view, 20);
            var firstSlice = viewDimension == 5 && view is not null ? ReadDescriptorUInt32(view, 24) : 0U;
            var slices = viewDimension == 5 && view is not null ? ReadDescriptorUInt32(view, 28) : 1U;
            var authority = writer == 0 ? "guest-cpu" : writer == queue ? "same-queue-gpu" : "other-queue-gpu";

            Console.Error.WriteLine(
                $"[graphics] Indexed UI source: presents={Interlocked.Read(ref _presentSuccesses)}; variant={upload.Variant}; "
                +
                $"targetFormat={upload.ColorFormat}; slot={slot}; descriptor={source.Token}; descriptorKind={source.Kind}; "
                +
                $"address=0x{resource->Address:X}; extent={description.Width}x{description.Height}x{description.DepthOrArraySize}; "
                +
                $"storageFormat={description.Format}; viewFormat={viewFormat}; viewDimension={viewDimension}; mapping=0x{mapping:X}; "
                +
                $"layout=0x{description.Layout:X}; firstMip={firstMip}; viewMips={mips}; firstSlice={firstSlice}; viewSlices={slices}; "
                +
                $"authority={authority}.");
        }
    }
}
