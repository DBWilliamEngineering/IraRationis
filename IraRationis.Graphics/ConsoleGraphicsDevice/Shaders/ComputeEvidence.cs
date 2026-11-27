// SPDX-License-Identifier: GPL-3.0-or-later
// Native table fields cross-checked against VIctory victory-shader/src/native.rs.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumComputeEvidenceTableEntries = 128;
    private const int MaximumComputeEvidenceCodeBytes = 16384;
    private const int MaximumComputeCaptureBytes = 8 * 1024 * 1024;
    private const int MaximumComputeDescriptorEvidenceBytes = 128;
    private static int _computeEvidenceCaptured;

    // Capture the first unsupported dispatch, before FailFast. No guest buffer
    // contents are read. Optional captures contain retained shader/root blobs
    // and descriptor metadata, never the buffers or textures they reference.
    private static void CaptureCompute(nint self, uint coordinateX, uint coordinateY, uint coordinateZ,
        uint? flags = null)
    {
        if (Interlocked.CompareExchange(ref _computeEvidenceCaptured, 1, 0) != 0)
        {
            return;
        }

        using var report = new StringWriter(CultureInfo.InvariantCulture);
        string? captureDirectory = null;

        try
        {
            var state = StateFor(self);

            report.WriteLine(
                $"[graphics] IRX-GFX-COMPUTE-R16.1: "
                + $"groups={coordinateX},{coordinateY},{coordinateZ}; flags={flags?.ToString("X") ?? "none"}; "
                + $"listType={*(uint*)(self + 0x10)}; priorBoundaries={state.MemoryBoundaries.Count}.");

            ComputePipelineState? pipeline;

            lock (PipelineGate)
            {
                _ = ComputePipelines.TryGetValue(state.Pipeline, out pipeline);
            }

            if (pipeline == null)
            {
                report.WriteLine("[graphics] R16.1: no bound retained compute pipeline.");

                return;
            }

            report.WriteLine(
                $"[graphics] R16.1 compute SHA256={pipeline.Hash}; "
                + $"bytes={pipeline.Shader.Bytes.Length}; "
                + $"rootMatches={pipeline.Root == state.Compute.Signature}; "
                + $"tables={state.Compute.Tables.Count}; "
                + $"constants={state.Compute.Constants.Count}; "
                + $"views={state.Compute.Views.Count}; heaps={state.Heaps.Length}.");

            var capturePath = Environment.GetEnvironmentVariable("IRX_COMPUTE_CAPTURE_DIR");
            var rootBlob = IsRootSignature(pipeline.Root) ? RootSignatureBlob(pipeline.Root) : ReadOnlySpan<byte>.Empty;

            if (!rootBlob.IsEmpty)
            {
                report.WriteLine(
                    $"[graphics] R16.1 pipeline root bytes={rootBlob.Length}; sha256={Convert.ToHexString(SHA256.HashData(rootBlob))}.");
            }

            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                try
                {
                    captureDirectory = Path.Combine(Path.GetFullPath(capturePath), $"compute-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(captureDirectory);
                    WriteBoundedComputeBlob(report, captureDirectory, "shader", ".cdxil", pipeline.Shader.Bytes);
                    WriteBoundedComputeBlob(report, captureDirectory, "root", ".bin", rootBlob);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                      ArgumentException or NotSupportedException)
                {
                    report.WriteLine($"[graphics] compute capture unavailable: {exception.Message}");
                }
            }

            foreach (var (slot, address) in state.Compute.Tables.OrderBy(static entry => entry.Key)
                         .Take(MaximumComputeEvidenceTableEntries))
            {
                report.WriteLine($"[graphics] R16.1 table slot={slot}, address=0x{address:X}.");
            }

            foreach (var (slot, view) in state.Compute.Views.OrderBy(static entry => entry.Key)
                         .Take(MaximumComputeEvidenceTableEntries))
            {
                report.WriteLine(
                    $"[graphics] R16.1 root view slot={slot}, kind={view.Kind}, address=0x{view.Address:X}.");
            }

            foreach (var (slot, values) in state.Compute.Constants.OrderBy(static entry => entry.Key)
                         .Take(MaximumComputeEvidenceTableEntries))
            {
                report.WriteLine($"[graphics] R16.1 constants slot={slot}, dwords={values.Length}.");
            }

            var container = ShaderContainer.Parse(pipeline.Shader.Bytes);
            var programs = NativeProgramReader.Read(container);

            report.WriteLine($"[graphics] R16.1 native programs={programs.Count}.");

            foreach (var program in programs.Where(static program => program.Stage == NativeShaderStage.Compute)
                         .Take(4))
            {
                var threads = program.Threads;

                report.WriteLine(
                    $"[graphics] R16.1 native compute: "
                    + $"wave={(program.Wave64 ? 64 : 32)}, "
                    + $"VGPR={program.VectorRegisters}, "
                    + $"SGPR={program.ScalarRegisters}, "
                    + $"local={program.LocalBytes}, "
                    + $"threads={(threads == null ? "unknown" : string.Join(",", threads))}, codeBytes={program.Code.Length}.");

                WriteResourceLayout(report, state.Compute, program, captureDirectory != null);
                // The instruction dump is opt-in and bounded. The default report
                // contains only the dispatch and resource ABI metadata.
                if (Environment.GetEnvironmentVariable("IRX_COMPUTE_INSTRUCTION_TRACE") != "1")
                {
                    continue;
                }

                var length = Math.Min(program.Code.Length, MaximumComputeEvidenceCodeBytes);

                for (var offset = 0; offset < length; offset += 32)
                {
                    var count = Math.Min(32, length - offset);

                    report.WriteLine(
                        $"[graphics] R16.1 native code {offset:X4}: {Convert.ToHexString(program.Code.AsSpan(offset, count))}");
                }

                if (length != program.Code.Length)
                {
                    report.WriteLine($"[graphics] R16.1 native instruction trace truncated at {length} bytes.");
                }
            }
        }
        catch (Exception exception)
        {
            report.WriteLine(
                $"[graphics] R16.1 compute evidence incomplete: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            report.WriteLine("[graphics] IRX-GFX-COMPUTE-R16.1: END (diagnostic only; dispatch not executed).");

            var evidence = report.ToString();
            Console.Error.Write(evidence);
            Console.Error.Flush();

            if (captureDirectory != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(captureDirectory, "compute.txt"), evidence);
                    Console.Error.WriteLine($"[graphics] compute local capture: {captureDirectory}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                      ArgumentException or NotSupportedException)
                {
                    Console.Error.WriteLine($"[graphics] compute report unavailable: {exception.Message}");
                }
            }
        }
    }

    private static void WriteBoundedComputeBlob(TextWriter report, string directory, string label, string extension,
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumComputeCaptureBytes)
        {
            report.WriteLine(
                $"[graphics] compute capture omitted {label}: bytes={bytes.Length}; limit={MaximumComputeCaptureBytes}.");

            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(directory, $"{label}-{hash}{extension}"), bytes.ToArray());
        report.WriteLine($"[graphics] compute capture {label}: bytes={bytes.Length}; sha256={hash}.");
    }

    private static void WriteResourceLayout(TextWriter report, RecordedRootState root, NativeProgram program,
        bool includeDescriptorBytes = false)
    {
        const int entrySize = 20;

        var layout = program.RootLayout.AsSpan();
        var resources = program.UsedResources.AsSpan();

        report.WriteLine(
            $"[graphics] R16.1 root entries={layout.Length / entrySize}, used resources={resources.Length / entrySize}.");

        for (var index = 0; index < Math.Min(layout.Length / entrySize, MaximumComputeEvidenceTableEntries); index++)
        {
            var entry = layout.Slice(index * entrySize, entrySize);

            report.WriteLine(
                $"[graphics] R16.1 root[{index}]: "
                + $"kind={ReadComputeEvidenceUInt32(entry, 0)}, "
                + $"tableDword={ReadComputeEvidenceUInt32(entry, 4)}, "
                + $"sgprs={ReadComputeEvidenceUInt32(entry, 8)}, "
                + $"register={ReadComputeEvidenceUInt32(entry, 12)}, "
                + $"userData={entry[16]}, slot={entry[17]}.");
        }

        for (var index = 0; index < Math.Min(resources.Length / entrySize, MaximumComputeEvidenceTableEntries); index++)
        {
            var entry = resources.Slice(index * entrySize, entrySize);
            var offset = ReadComputeEvidenceUInt32(entry, 4);
            var slot = entry[18];

            report.WriteLine(
                $"[graphics] R16.1 used[{index}]: "
                + $"kind={entry[0]}, "
                + $"rootKind={entry[1]}, "
                + $"tableDword={offset}, "
                + $"register={ReadComputeEvidenceUInt32(entry, 8)}, "
                + $"stride={ReadComputeEvidenceUInt32(entry, 12)}, "
                + $"descriptorDwords={entry[16]}, "
                + $"userData={entry[17]}, slot={slot}.");

            if (entry[1] != 3 || !root.Tables.TryGetValue(slot, out var table) || table == 0
                || (ulong)offset * 4 > ulong.MaxValue - table)
            {
                continue;
            }

            var handle = table + (ulong)offset * 4;

            if (TryReadMappingRecord(handle, out DescriptorToken token))
            {
                switch (token.Tag)
                {
                    case DescriptorTag:
                        report.WriteLine(
                            $"[graphics] R16.1 descriptor[{index}]: token kind={token.Kind}, id={token.Id}.");

                        if (includeDescriptorBytes)
                        {
                            WriteComputeDescriptorEvidence(report, index, token);
                        }

                        break;

                    case ConstantTag when TryReadMappingRecord(handle, out ConstantDescriptor constant):
                        report.WriteLine(
                            $"[graphics] R16.1 descriptor[{index}]: CBV bytes={constant.Size}, address=0x{constant.Address:X}.");

                        break;

                    default:
                        report.WriteLine(
                            $"[graphics] R16.1 descriptor[{index}]: unrecognized token tag=0x{token.Tag:X8}.");

                        break;
                }
            }
            else
            {
                report.WriteLine($"[graphics] R16.1 descriptor[{index}]: unreadable at recording time.");
            }
        }
    }

    private static void WriteComputeDescriptorEvidence(TextWriter report, int index, DescriptorToken token)
    {
        DescriptorMetadata? metadata;

        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out metadata);
        }

        if (metadata == null || metadata.Kind != token.Kind)
        {
            report.WriteLine($"[graphics] R16.1 descriptor[{index}]: retained metadata unavailable.");

            return;
        }

        report.WriteLine(
            $"[graphics] R16.1 descriptor[{index}]: resource=0x{(nuint)metadata.Resource:X}; generation={metadata.ResourceGeneration}; counter=0x{(nuint)metadata.Counter:X}.");
        WriteComputeDescriptorBytes(report, index, "view", metadata.Description);
        WriteComputeDescriptorBytes(report, index, "extra", metadata.Extra);
    }

    private static void WriteComputeDescriptorBytes(TextWriter report, int index, string label, byte[]? bytes)
    {
        if (bytes == null)
        {
            report.WriteLine($"[graphics] R16.1 descriptor[{index}] {label}: default.");

            return;
        }

        var count = Math.Min(bytes.Length, MaximumComputeDescriptorEvidenceBytes);
        report.WriteLine(
            $"[graphics] R16.1 descriptor[{index}] {label}: bytes={bytes.Length}; hex={Convert.ToHexString(bytes.AsSpan(0, count))}; truncated={count != bytes.Length}.");
    }

    private static uint ReadComputeEvidenceUInt32(ReadOnlySpan<byte> entry, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(entry[offset..]);
    }
}
