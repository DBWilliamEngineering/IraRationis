// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using IraRationis.Shaders;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R12MaximumHeaderBytes = 4096;
    private const int R12MaximumSourceBytes = 16 * 1024 * 1024;

    private static readonly bool VerifyGuestVertexSource =
        Environment.GetEnvironmentVariable("IRX_VERIFY_GUEST_SOURCE") == "1";

    // Only on the first blocked draw, with a recognized shader pair. A CPU-readable
    // virtual range is not a GPU mapping, ownership guarantee or submission-time snapshot.
    private static void InspectR12GuestSource(VertexFetchShaderPair pair, RecordedCommandState state,
        VertexFetchDrawRange draw, TextWriter report)
    {
        if (!VerifyGuestVertexSource)
        {
            return;
        }

        if (draw.Indexed || draw.Count == 0 || draw.Instances == 0 || draw.BaseVertex != 0 ||
            state.Graphics.Tables.Count != 0 || state.Graphics.Constants.Count != 0 ||
            state.Graphics.Views.Count != 1 || !state.Graphics.Views.TryGetValue(0, out var view) ||
            view.Kind != 1 || view.Address == 0 || (view.Address & 3UL) != 0)
        {
            report.WriteLine("[graphics] IRX-GFX-SOURCE-R12: refused unsupported draw or root binding.");

            return;
        }

        if (!TryR12HeaderLength(pair, out var headerLength))
        {
            report.WriteLine("[graphics] IRX-GFX-SOURCE-R12: refused unsupported header range.");

            return;
        }

        report.WriteLine($"[graphics] IRX-GFX-SOURCE-R12: address=0x{view.Address:X}, " +
                         $"first={draw.First}, count={draw.Count}, instances={draw.Instances}, " +
                         $"priorBoundaries={state.MemoryBoundaries.Count} (recording-time only).");

        Span<byte> header = stackalloc byte[headerLength];

        if (!TryReadR12Memory(view.Address, header))
        {
            report.WriteLine("[graphics] R12 source header is not fully CPU-readable.");

            return;
        }

        WriteR12Field(report, "position", pair.Position, header);

        WriteR12Field(report, "attribute", pair.Attribute, header);

        if (!Rdna2VertexFetchCompiler.TryGetReadExtent(pair, header, draw.First, draw.Count,
                R12MaximumSourceBytes, out var extent) || extent < (ulong)headerLength ||
            extent > R12MaximumSourceBytes || extent > ulong.MaxValue - view.Address)
        {
            report.WriteLine($"[graphics] R12 source extent refused (budget={R12MaximumSourceBytes} bytes).");

            return;
        }

        ReportR12RetainedBuffers(state, view.Address, extent, report);

        // No guest content is logged or written to disk. Rent only the bounded prefix
        // required by the recognized shader; a later execution path must resolve it again.
        var length = checked((int)extent);
        var rented = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            var source = rented.AsSpan(0, length);

            if (!TryReadR12Memory(view.Address, source))
            {
                report.WriteLine($"[graphics] R12 source range not fully CPU-readable: bytes={length}.");

                return;
            }

            // Detect header drift between the extent calculation and the source copy.
            // This does not detect concurrent changes to the payload or an ABA remap.
            if (!source[..headerLength].SequenceEqual(header))
            {
                report.WriteLine("[graphics] R12 source header changed while reading; snapshot refused.");

                return;
            }

            report.WriteLine($"[graphics] IRX-GFX-SOURCE-R12: PASS CPU-readable source extent={length}; " +
                             "headers matched; GPU coherence and submission-time ownership unverified.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, true);
        }
    }

    private static bool TryR12HeaderLength(VertexFetchShaderPair pair, out int length)
    {
        length = 0;

        var end = Math.Max((ulong)pair.Position.HeaderOffset, pair.Attribute.HeaderOffset) + sizeof(uint);

        if (end > R12MaximumHeaderBytes || (pair.Position.HeaderOffset & 3U) != 0 ||
            (pair.Attribute.HeaderOffset & 3U) != 0)
        {
            return false;
        }

        length = (int)end;

        return true;
    }

    private static bool TryReadR12Memory(ulong address, Span<byte> destination)
    {
        if (address == 0 || destination.IsEmpty || (ulong)destination.Length > ulong.MaxValue - address)
        {
            return false;
        }

        fixed (byte* target = destination)
        {
            nuint transferred = 0;

            var count = (nuint)destination.Length;

            return ReadProcessMemory(-1, (void*)(nuint)address, target, count, &transferred) != 0 &&
                   transferred == count;
        }
    }

    private static void WriteR12Field(TextWriter report, string name, VertexFetchField field,
        ReadOnlySpan<byte> header)
    {
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(header[(int)field.HeaderOffset..]);
        var components = packed & 15U;
        var stride = (packed >> 3) & 60U;
        var offset = (packed >> 7) & 0x01fffffcU;

        report.WriteLine($"[graphics] R12 {name}: components={components}, expected={field.Components}, " +
                         $"stride={stride}, baseOffset={offset}, active={components == field.Components}.");
    }

    private static void ReportR12RetainedBuffers(RecordedCommandState state, ulong address, ulong extent,
        TextWriter report)
    {
        var matches = 0;
        var materialized = 0;

        foreach (var value in state.Owned)
        {
            if (!IsResource(value))
            {
                continue;
            }

            // The command state owns a reference for the lifetime of this inspection.
            var resource = (ResourceObject*)value;

            if (resource->Description.Dimension != 1 || resource->Address == 0 || address < resource->Address)
            {
                continue;
            }

            var offset = address - resource->Address;

            if (offset > resource->Description.Width || extent > resource->Description.Width - offset)
            {
                continue;
            }

            matches++;

            if (resource->HostResource != 0)
            {
                materialized++;
            }
        }

        report.WriteLine($"[graphics] R12 retained covering buffers={matches}, hostMaterialized={materialized}; " +
                         "not a global allocation lookup or coherence proof.");
    }

    private static void VerifyR12SourcePlanning()
    {
        var pair = new VertexFetchShaderPair(string.Empty, string.Empty, 0, 3,
            new VertexFetchField(0, 3), new VertexFetchField(12, 4));
        var invalid = pair with
        {
            Attribute = new VertexFetchField(uint.MaxValue, 4)
        };

        if (!TryR12HeaderLength(pair, out var length) || length != 16 ||
            TryR12HeaderLength(invalid, out _) || TryReadR12Memory(0, Span<byte>.Empty))
        {
            throw new InvalidOperationException("R12 header bounds regression failed.");
        }

        Span<byte> source = stackalloc byte[128];

        source.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(source, (32U << 7) | (12U << 3) | 3U);

        BinaryPrimitives.WriteUInt32LittleEndian(source[12..], (80U << 7) | (16U << 3) | 4U);

        if (!Rdna2VertexFetchCompiler.TryGetReadExtent(pair, source[..length], 0, 3,
                R12MaximumSourceBytes, out var extent) || extent != 128)
        {
            throw new InvalidOperationException("R12 extent regression failed.");
        }

        if (OperatingSystem.IsWindows())
        {
            Span<byte> copy = stackalloc byte[128];

            fixed (byte* pointer = source)
            {
                if (!TryReadR12Memory((nuint)pointer, copy) || !copy.SequenceEqual(source))
                {
                    throw new InvalidOperationException("R12 CPU source copy regression failed.");
                }
            }
        }

        Console.WriteLine("[graphics-probe] R12 vertex source planning and bounds passed (CPU only).");
    }

    private readonly record struct VertexFetchDrawRange(
        bool Indexed,
        uint Count,
        uint Instances,
        uint First,
        int BaseVertex,
        uint FirstInstance);
}
