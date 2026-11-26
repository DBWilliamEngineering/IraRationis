// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using IraRationis.Shaders;

namespace IraRationis.Graphics;

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

            if (directory != null)
            {
                File.WriteAllBytes(Path.Combine(directory, $"pso-{record.Kind}-{hash}.bin"), record.Bytes);
            }
        }

        foreach (var shader in pipeline.Shaders)
        {
            WriteShaderEvidence(report, shader, directory);
        }

        if (pipeline.InputLayout is { } layout)
        {
            foreach (var element in layout)
            {
                report.WriteLine($"R14 input: {element.Name}{element.Index}, format={element.Format}, " +
                                 $"slot={element.Slot}, offset={element.Offset}, class={element.Class}, step={element.Step}.");
            }
        }

        if (state.Topology == R14RectangleTopology)
        {
            if (state.Graphics.Tables.TryGetValue(0, out var descriptorTable))
            {
                WriteR16TableEvidence(report, descriptorTable);
            }

            report.WriteLine("[graphics] Rectangle execution refused; inspect preceding R14/R16 reasons.");

            return;
        }

        PrepareVertexFetchTranslation(report, state, pipeline, directory, draw);
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

    private static void WriteShaderEvidence(TextWriter report, PipelineShader shader, string? directory)
    {
        var bytes = shader.Container.Bytes;

        report.WriteLine($"shader stage={shader.Stage} bytes={bytes.Length} sha256={shader.Hash}");

        if (directory != null)
        {
            File.WriteAllBytes(Path.Combine(directory, $"shader-{shader.Hash}.cdxil"), bytes);
        }

        try
        {
            var container = Shaders.ShaderContainer.Parse(bytes);

            report.WriteLine($"  parts={string.Join(",", container.Parts.Select(static part => part.FourCC))}");

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

        WriteShaderEvidence(report, new PipelineShader(1, hash, new ShaderContainer(bytes)), null);

        if (!report.ToString().Contains("native programs=0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("First-draw shader evidence regression failed.");
        }

        Console.WriteLine("[graphics-probe] R9 blocked-draw metadata and shader classification passed (CPU only).");

        VerifyR10VertexFetchExtent();

        VerifyR12SourcePlanning();

        VerifyR14InputLayout();
    }
}
