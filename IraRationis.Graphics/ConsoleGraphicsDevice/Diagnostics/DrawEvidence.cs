// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Scalar;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static int _firstBlockedDrawCaptured;

    // This is a failure-path diagnostic, not a shader replacement or a host draw.
    // Command lists are externally synchronized; the recorded state retains its
    // pipeline, root signatures and materialized target resources during capture.
    private static void CaptureBlockedDraw(nint self, bool indexed, uint count, uint instances, uint first,
        int baseVertex, uint firstInstance)
    {
        if (Interlocked.CompareExchange(ref _firstBlockedDrawCaptured, 1, 0) != 0)
        {
            return;
        }

        try
        {
            using var report = new StringWriter(CultureInfo.InvariantCulture);

            var state = StateFor(self);

            report.WriteLine("[graphics] IRX-GFX-DRAW-R9: first blocked draw (not executed).");

            report.WriteLine($"draw indexed={indexed} count={count} instances={instances} first={first} " +
                             $"baseVertex={baseVertex} firstInstance={firstInstance}");

            report.WriteLine($"list=0x{(nuint)self:X} type={*(uint*)(self + 0x10)} hostAllocated={Host(self) != 0}");

            string? directory = null;

            var requestedDirectory = Environment.GetEnvironmentVariable("IRX_DRAW_CAPTURE_DIR");

            if (!string.IsNullOrWhiteSpace(requestedDirectory))
            {
                try
                {
                    directory = Path.Combine(Path.GetFullPath(requestedDirectory), $"draw-{Guid.NewGuid():N}");

                    Directory.CreateDirectory(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                      ArgumentException or NotSupportedException)
                {
                    directory = null;

                    report.WriteLine($"capture directory unavailable: {exception.Message}");
                }
            }

            WriteDrawEvidence(report, state, directory, new VertexFetchDrawRange(indexed, count, instances, first,
                baseVertex, firstInstance));

            var evidence = report.ToString();

            Console.Error.Write(evidence);

            Console.Error.Flush();

            if (directory == null)
            {
                return;
            }

            File.WriteAllText(Path.Combine(directory, "draw.txt"), evidence);

            Console.Error.WriteLine($"[graphics] R9 local capture: {directory}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                              UnauthorizedAccessException or ArgumentException or
                                              NotSupportedException or OutOfMemoryException or OverflowException)
        {
            Console.Error.WriteLine(
                $"[graphics] R9 draw capture incomplete: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void WriteDrawEvidence(TextWriter report, RecordedCommandState state, string? directory,
        VertexFetchDrawRange? draw = null)
    {
        report.WriteLine($"pipeline=0x{(nuint)state.Pipeline:X} topology={state.Topology} " +
                         $"viewports={state.Viewports?.Length ?? 0} scissors={state.Scissors?.Length ?? 0} " +
                         $"targets={state.Targets.Length} depth={state.Depth.HasValue}");

        report.WriteLine($"root=0x{(nuint)state.Graphics.Signature:X} tables={state.Graphics.Tables.Count} " +
                         $"constants={state.Graphics.Constants.Count} views={state.Graphics.Views.Count} " +
                         $"heaps={state.Heaps.Length} vertexViews={state.Vertices.Count} index={state.Index.HasValue}");

        // Only read binding metadata here. The optional R12 source probe is
        // separate, bounded and restricted to a recognized shader contract.
        foreach (var (slot, handle) in state.Graphics.Tables.OrderBy(static item => item.Key))
        {
            report.WriteLine($"root table slot={slot} handle=0x{handle:X}");
        }

        foreach (var (slot, view) in state.Graphics.Views.OrderBy(static item => item.Key))
        {
            report.WriteLine($"root view slot={slot} kind={view.Kind} address=0x{view.Address:X}");
        }

        foreach (var (slot, view) in state.Vertices.OrderBy(static item => item.Key))
        {
            report.WriteLine(
                $"vertex slot={slot} address=0x{view.Address:X} bytes={view.Size} stride={view.FormatOrStride}");
        }

        if (state.Index is { } index)
        {
            report.WriteLine($"index address=0x{index.Address:X} bytes={index.Size} format={index.FormatOrStride}");
        }

        for (var slot = 0; slot < state.Targets.Length; slot++)
        {
            WriteTargetEvidence(report, state, state.Targets[slot], 20, $"RTV[{slot}]");
        }

        if (state.Depth is { } depth)
        {
            WriteTargetEvidence(report, state, depth, 21, "DSV");
        }

        GraphicsPipelineMetadata? pipeline;

        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out pipeline);
        }

        // The state owns a reference to the bound pipeline. Its immutable
        // metadata and the root it retains remain alive after this lock ends.
        if (pipeline == null)
        {
            report.WriteLine("graphics PSO unavailable: no retained graphics pipeline at draw.");

            return;
        }

        report.WriteLine($"PSO stream={pipeline.IsStream} complete={pipeline.CompleteStream} " +
                         $"stages={pipeline.Shaders.Count} stateRecords={pipeline.State.Count} " +
                         $"rootMatchesBound={pipeline.Root == state.Graphics.Signature}");

        if (!pipeline.CompleteStream)
        {
            report.WriteLine(
                $"PSO incomplete reason: {pipeline.IncompleteReason ?? "legacy description or unknown state"}.");
        }

        WriteRootEvidence(report, pipeline.Root, "pipeline", directory);

        if (pipeline.Root != state.Graphics.Signature)
        {
            WriteRootEvidence(report, state.Graphics.Signature, "bound", directory);
        }

        foreach (var record in pipeline.State)
        {
            var hash = Convert.ToHexString(SHA256.HashData(record.Bytes));

            report.WriteLine($"PSO record kind={record.Kind} bytes={record.Bytes.Length} sha256={hash}");

            WriteFixedStateEvidence(report, record);

            if (directory != null)
            {
                File.WriteAllBytes(Path.Combine(directory, $"pso-{record.Kind}-{hash}.bin"), record.Bytes);
            }
        }

        foreach (var shader in pipeline.Shaders)
        {
            WriteShaderEvidence(report, shader, directory, state.Graphics);
        }

        if (pipeline.InputLayout is { } layout)
        {
            foreach (var element in layout)
            {
                report.WriteLine($"R14 input: {element.Name}{element.Index}, format={element.Format}, " +
                                 $"slot={element.Slot}, offset={element.Offset}, class={element.Class}, step={element.Step}.");
            }
        }

        switch (state.Topology)
        {
            case RectangleRectangleTopology:
            {
                if (state.Graphics.Tables.TryGetValue(0, out var descriptorTable))
                {
                    WriteTextureTableEvidence(report, descriptorTable);
                }

                report.WriteLine("[graphics] Rectangle execution refused; inspect preceding R14/R16 reasons.");

                return;
            }
            case TriangleStripTopology:
            {
                report.WriteLine("[graphics] Triangle-strip shader contract unsupported; R10 vertex-fetch probe is not applicable.");

                if (directory == null)
                {
                    report.WriteLine("[graphics] Set IRX_DRAW_CAPTURE_DIR to retain the blocked shaders, root and PSO records.");
                }

                return;
            }
            default:
                PrepareVertexFetchTranslation(report, state, pipeline, directory, draw);

                break;
        }
    }

    private static void WriteTargetEvidence(TextWriter report, RecordedCommandState state, ulong handle,
        uint kind, string label)
    {
        var token = default(DescriptorToken);

        nuint transferred = 0;

        if (handle == 0 || ReadProcessMemory(-1, (void*)(nuint)handle, &token,
                (nuint)sizeof(DescriptorToken), &transferred) == 0 || transferred != (nuint)sizeof(DescriptorToken) ||
            token.Tag != DescriptorTag || token.Kind != kind)
        {
            report.WriteLine($"{label}: handle=0x{handle:X} unresolved token");

            return;
        }

        DescriptorMetadata? metadata;

        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out metadata);
        }

        if (metadata == null || !state.Owned.Contains(metadata.Resource) || !IsResource(metadata.Resource))
        {
            report.WriteLine($"{label}: handle=0x{handle:X} descriptor={token.Id} no retained resource");

            return;
        }

        var resource = (ResourceObject*)metadata.Resource;

        if (resource->Generation != metadata.ResourceGeneration)
        {
            report.WriteLine($"{label}: handle=0x{handle:X} stale resource generation");

            return;
        }

        var desc = resource->Description;
        var viewFormat = metadata.Description is { Length: >= 4 } bytes
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : desc.Format;

        report.WriteLine($"{label}: handle=0x{handle:X} descriptor={token.Id} resource=0x{(nuint)metadata.Resource:X} "
                         +
                         $"dimension={desc.Dimension} size={desc.Width}x{desc.Height}x{desc.DepthOrArraySize} " +
                         $"mips={desc.MipLevels} samples={desc.SampleCount} format={desc.Format} viewFormat={viewFormat} "
                         +
                         $"layout={desc.Layout} flags=0x{desc.Flags:X} hostAllocated={resource->HostResource != 0}");
    }

    private static void WriteRootEvidence(TextWriter report, nint root, string label, string? directory)
    {
        if (!IsRootSignature(root))
        {
            report.WriteLine($"{label} root: absent or invalid");

            return;
        }

        var blob = RootSignatureBlob(root);
        var hash = Convert.ToHexString(SHA256.HashData(blob));

        report.WriteLine($"{label} root: bytes={blob.Length} sha256={hash} DXBC={blob.StartsWith("DXBC"u8)}");

        if (directory != null)
        {
            File.WriteAllBytes(Path.Combine(directory, $"root-{hash}.bin"), blob.ToArray());
        }
    }

    private static void WriteShaderEvidence(TextWriter report, PipelineShader shader, string? directory,
        RecordedRootState? root = null)
    {
        var bytes = shader.Container.Bytes;

        report.WriteLine($"shader stage={shader.Stage} bytes={bytes.Length} sha256={shader.Hash}");

        if (directory != null)
        {
            File.WriteAllBytes(Path.Combine(directory, $"shader-{shader.Hash}.cdxil"), bytes);
        }

        try
        {
            var container = ShaderContainer.Parse(bytes);

            report.WriteLine($"  parts={string.Join(",", container.Parts.Select(static part => part.FourCC))}");

            foreach (var part in container.Parts.Where(static part => part.FourCC is "ISG1" or "OSG1" or "PSG1"))
            {
                WriteExtendedSignatureEvidence(report, part);
            }

            var dxil = container.Part("DXIL");

            if (dxil != null)
            {
                try
                {
                    var program = DxilProgram.Parse(dxil.Data);

                    report.WriteLine($"  DXIL kind={program.Kind} model={program.Major}.{program.Minor} " +
                                     $"bitcode={program.Bitcode.Length} standardLayout={program.IsStandardLayout} " +
                                     $"XDAT={program.HasXdat} XBOP={program.HasXbop} (not host-validated)");
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                                                      or OverflowException)
                {
                    report.WriteLine($"  DXIL bitcode parse refused: {exception.Message}");
                }
            }

            // Decode only the bound shaders, outside PipelineGate. Native program
            // extraction is budgeted by NativeProgramReader; no GPU calls occur.
            var programs = NativeProgramReader.Read(container);

            report.WriteLine($"  native programs={programs.Count}");

            foreach (var program in programs)
            {
                report.WriteLine($"  native stage={program.Stage} wave={(program.Wave64 ? 64 : 32)} " +
                                 $"VGPR={program.VectorRegisters} SGPR={program.ScalarRegisters} " +
                                 $"local={program.LocalBytes} rootLayout={program.RootLayout.Length} " +
                                 $"usedResources={program.UsedResources.Length} code={program.Code.Length}");

                if (root != null)
                {
                    WriteResourceLayout(report, root, program);
                }

                if (directory != null && program.StageMetadata.Length <= 16384)
                {
                    File.WriteAllBytes(Path.Combine(directory, $"stage-{shader.Hash}-{program.Stage}.bin"),
                        program.StageMetadata);
                }

                try
                {
                    var instructions = Rdna2ScalarCompiler.Decode(program.Code);

                    report.WriteLine(
                        $"    scalar diagnostic subset accepted {instructions.Count} instructions; not a host shader");
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    report.WriteLine($"    scalar diagnostic boundary: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or
                                              NotSupportedException or OverflowException)
        {
            report.WriteLine($"  shader inspection refused: {exception.Message}");
        }
    }

    // Standard extended DXBC signatures. This is diagnostic parsing, not a
    // declaration that an arbitrary signature can be translated or bound.
    private static void WriteExtendedSignatureEvidence(TextWriter report, ShaderPart part)
    {
        const int recordSize = 32;
        const int maximumElements = 128;
        var data = part.Data.Span;

        if (data.Length < 8)
        {
            report.WriteLine($"  {part.FourCC}: truncated signature header.");

            return;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

        if (count > maximumElements || start != 8 || 8L + count * (long)recordSize > data.Length)
        {
            report.WriteLine($"  {part.FourCC}: unsupported signature table (count={count}, start={start}).");

            return;
        }

        var tableEnd = 8 + (int)count * recordSize;

        report.WriteLine($"  {part.FourCC}: elements={count}.");

        for (var index = 0; index < count; index++)
        {
            var entry = data.Slice(8 + index * recordSize, recordSize);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);

            if (nameOffset < tableEnd || nameOffset >= data.Length)
            {
                report.WriteLine($"    element[{index}]: invalid semantic offset.");

                continue;
            }

            var nameBytes = data[(int)nameOffset..];
            var end = nameBytes.IndexOf((byte)0);

            if (end is < 1 or > 128)
            {
                report.WriteLine($"    element[{index}]: invalid semantic name extent.");

                continue;
            }

            var name = Encoding.ASCII.GetString(nameBytes[..end]);

            if (name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                report.WriteLine($"    element[{index}]: invalid semantic name characters.");

                continue;
            }

            report.WriteLine($"    element[{index}]: {name}{BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])}; " +
                $"stream={BinaryPrimitives.ReadUInt32LittleEndian(entry)}; system={BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])}; " +
                $"component={BinaryPrimitives.ReadUInt32LittleEndian(entry[16..])}; register={BinaryPrimitives.ReadUInt32LittleEndian(entry[20..])}; " +
                $"mask=0x{entry[24]:X2}; used=0x{entry[25]:X2}; precision={BinaryPrimitives.ReadUInt32LittleEndian(entry[28..])}.");
        }
    }

    private static void WriteFixedStateEvidence(TextWriter report, PipelineStateRecord record)
    {
        var bytes = record.Bytes.AsSpan();

        if (record.Kind == 16 && bytes.Length == 8)
        {
            report.WriteLine($"  DSV pipeline format={BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])}.");
        }
        else if (record.Kind == 21 && bytes.Length == 64)
        {
            report.WriteLine($"  depthEnable={BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])}; " +
                $"depthWriteMask={BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..])}; " +
                $"depthFunc={BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..])}; " +
                $"stencilEnable={BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..])}; " +
                $"depthBoundsEnable={BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..])}.");
        }
    }

    private static void VerifyDrawEvidence()
    {
        using var report = new StringWriter(CultureInfo.InvariantCulture);

        var state = new RecordedCommandState { Topology = 4 };

        WriteDrawEvidence(report, state, null);

        var output = report.ToString();

        if (!output.Contains("topology=4", StringComparison.Ordinal) ||
            !output.Contains("no retained graphics pipeline", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("First-draw evidence regression failed.");
        }

        report.GetStringBuilder().Clear();
        // Minimal valid, empty DXBC container. It must not be treated as executable.
        var bytes = new byte[32];

        "DXBC"u8.CopyTo(bytes);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 32);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        WriteShaderEvidence(report, new PipelineShader(1, hash, new ComputeShaderContainer(bytes)), null);

        if (!report.ToString().Contains("native programs=0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("First-draw shader evidence regression failed.");
        }

        report.GetStringBuilder().Clear();

        var signature = new byte[49];

        BinaryPrimitives.WriteUInt32LittleEndian(signature, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(signature.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(signature.AsSpan(12), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(signature.AsSpan(24), 3);
        signature[32] = 7;
        signature[33] = 7;
        "POSITION"u8.CopyTo(signature.AsSpan(40));

        WriteExtendedSignatureEvidence(report, new ShaderPart("ISG1", 0, signature));

        if (!report.ToString().Contains("POSITION0;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Extended shader signature evidence regression failed.");
        }

        report.GetStringBuilder().Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(signature.AsSpan(12), uint.MaxValue);
        WriteExtendedSignatureEvidence(report, new ShaderPart("ISG1", 0, signature));

        if (!report.ToString().Contains("invalid semantic offset", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Extended shader signature bounds regression failed.");
        }

        Console.WriteLine("[graphics-probe] R9 extended signature evidence and bounds passed (CPU only).");
        Console.WriteLine("[graphics-probe] R9 blocked-draw metadata and shader classification passed (CPU only).");

        VerifyVertexFetchExtent();

        VerifySourcePlanning();

        VerifyVertexInputLayout();
    }
}
