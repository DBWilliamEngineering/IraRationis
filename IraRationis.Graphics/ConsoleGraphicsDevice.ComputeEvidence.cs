// SPDX-License-Identifier: GPL-3.0-or-later
// Native table fields cross-checked against VIctory victory-shader/src/native.rs.

using System.Buffers.Binary;
using System.Globalization;
using IraRationis.Shaders;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R161MaximumTableEntries = 128;
    private const int R161MaximumCodeBytes = 16384;
    private static int _r161ComputeCaptured;

    // Capture the first unsupported dispatch, before FailFast. No guest buffer
    // contents are read and no binary capture directory is created.
    private static void CaptureR161Compute(nint self, uint x, uint y, uint z, uint? flags = null)
    {
        if (Interlocked.CompareExchange(ref _r161ComputeCaptured, 1, 0) != 0)
        {
            return;
        }

        using var report = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var state = StateFor(self);
            report.WriteLine(
                $"[graphics] IRX-GFX-COMPUTE-R16.1: groups={x},{y},{z}; flags={flags?.ToString("X") ?? "none"}; listType={*(uint*)(self + 0x10)}; priorBoundaries={state.MemoryBoundaries.Count}.");
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
                $"[graphics] R16.1 compute SHA256={pipeline.Hash}; bytes={pipeline.Shader.Bytes.Length}; rootMatches={pipeline.Root == state.Compute.Signature}; tables={state.Compute.Tables.Count}; constants={state.Compute.Constants.Count}; views={state.Compute.Views.Count}; heaps={state.Heaps.Length}.");
            if (IsRootSignature(pipeline.Root))
            {
                var blob = RootSignatureBlob(pipeline.Root);
                report.WriteLine($"[graphics] R16.1 pipeline root bytes={blob.Length}.");
            }

            foreach (var (slot, address) in state.Compute.Tables.OrderBy(static entry => entry.Key)
                         .Take(R161MaximumTableEntries))
            {
                report.WriteLine($"[graphics] R16.1 table slot={slot}, address=0x{address:X}.");
            }

            foreach (var (slot, view) in state.Compute.Views.OrderBy(static entry => entry.Key)
                         .Take(R161MaximumTableEntries))
            {
                report.WriteLine(
                    $"[graphics] R16.1 root view slot={slot}, kind={view.Kind}, address=0x{view.Address:X}.");
            }

            foreach (var (slot, values) in state.Compute.Constants.OrderBy(static entry => entry.Key)
                         .Take(R161MaximumTableEntries))
            {
                report.WriteLine($"[graphics] R16.1 constants slot={slot}, dwords={values.Length}.");
            }

            var container = Shaders.ShaderContainer.Parse(pipeline.Shader.Bytes);
            var programs = NativeProgramReader.Read(container);
            report.WriteLine($"[graphics] R16.1 native programs={programs.Count}.");
            foreach (var program in programs.Where(static program => program.Stage == NativeShaderStage.Compute)
                         .Take(4))
            {
                var threads = program.Threads;
                report.WriteLine(
                    $"[graphics] R16.1 native compute: wave={(program.Wave64 ? 64 : 32)}, VGPR={program.VectorRegisters}, SGPR={program.ScalarRegisters}, local={program.LocalBytes}, threads={(threads == null ? "unknown" : string.Join(",", threads))}, codeBytes={program.Code.Length}.");
                WriteR161ResourceLayout(report, state, program);
                // The instruction dump is opt-in and bounded. The default report
                // contains only the dispatch and resource ABI metadata.
                if (Environment.GetEnvironmentVariable("IRX_COMPUTE_INSTRUCTION_TRACE") == "1")
                {
                    var length = Math.Min(program.Code.Length, R161MaximumCodeBytes);
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
        }
        catch (Exception exception)
        {
            report.WriteLine(
                $"[graphics] R16.1 compute evidence incomplete: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            report.WriteLine("[graphics] IRX-GFX-COMPUTE-R16.1: END (diagnostic only; dispatch not executed).");
            Console.Error.Write(report.ToString());
            Console.Error.Flush();
        }
    }

    private static void WriteR161ResourceLayout(TextWriter report, RecordedCommandState state, NativeProgram program)
    {
        const int entrySize = 20;
        var layout = program.RootLayout.AsSpan();
        var resources = program.UsedResources.AsSpan();
        report.WriteLine(
            $"[graphics] R16.1 root entries={layout.Length / entrySize}, used resources={resources.Length / entrySize}.");
        for (var i = 0; i < Math.Min(layout.Length / entrySize, R161MaximumTableEntries); i++)
        {
            var entry = layout.Slice(i * entrySize, entrySize);
            report.WriteLine(
                $"[graphics] R16.1 root[{i}]: kind={R161Word(entry, 0)}, tableDword={R161Word(entry, 4)}, sgprs={R161Word(entry, 8)}, register={R161Word(entry, 12)}, userData={entry[16]}, slot={entry[17]}.");
        }

        for (var i = 0; i < Math.Min(resources.Length / entrySize, R161MaximumTableEntries); i++)
        {
            var entry = resources.Slice(i * entrySize, entrySize);
            var offset = R161Word(entry, 4);
            var slot = entry[18];
            report.WriteLine(
                $"[graphics] R16.1 used[{i}]: kind={entry[0]}, rootKind={entry[1]}, tableDword={offset}, register={R161Word(entry, 8)}, stride={R161Word(entry, 12)}, descriptorDwords={entry[16]}, userData={entry[17]}, slot={slot}.");
            if (entry[1] == 3 && state.Compute.Tables.TryGetValue(slot, out var table) && table != 0
                && (ulong)offset * 4 <= ulong.MaxValue - table)
            {
                var handle = table + (ulong)offset * 4;
                if (TryReadMappingRecord(handle, out DescriptorToken token))
                {
                    if (token.Tag == DescriptorTag)
                    {
                        report.WriteLine($"[graphics] R16.1 descriptor[{i}]: token kind={token.Kind}, id={token.Id}.");
                    }
                    else if (token.Tag == ConstantTag && TryReadMappingRecord(handle, out ConstantDescriptor constant))
                    {
                        report.WriteLine(
                            $"[graphics] R16.1 descriptor[{i}]: CBV bytes={constant.Size}, address=0x{constant.Address:X}.");
                    }
                    else
                    {
                        report.WriteLine($"[graphics] R16.1 descriptor[{i}]: unrecognized token tag=0x{token.Tag:X8}.");
                    }
                }
                else
                {
                    report.WriteLine($"[graphics] R16.1 descriptor[{i}]: unreadable at recording time.");
                }
            }
        }
    }

    private static uint R161Word(ReadOnlySpan<byte> entry, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(entry[offset..]);
    }
}
