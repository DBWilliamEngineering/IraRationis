// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Scalar;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static partial class ConsoleGraphicsDevice
{
    private static readonly bool InspectNativeShaders =
        Environment.GetEnvironmentVariable("IRX_TRACE_SHADER_DECODE") == "1";

    private static void InspectBoundPipeline(nint pipeline)
    {
        if (!InspectNativeShaders || pipeline == 0)
        {
            return;
        }

        // Snapshot shared containers under the registry lock, then decompress outside it.
        List<(string Hash, ComputeShaderContainer Container)> shaders = new();

        lock (PipelineGate)
        {
            if (ComputePipelines.TryGetValue(pipeline, out var compute))
            {
                shaders.Add((compute.Hash, compute.Shader));
            }

            if (GraphicsPipelines.TryGetValue(pipeline, out var graphics))
            {
                foreach (var shader in graphics.Shaders)
                {
                    shaders.Add((shader.Hash, shader.Container));
                }
            }
        }

        foreach (var (hash, container) in shaders)
        {
            if (Interlocked.Exchange(ref container.Inspected, 1) != 0)
            {
                continue;
            }

            try
            {
                var native = NativeProgramReader.Read(ShaderContainer.Parse(container.Bytes));

                Console.Error.WriteLine(
                    $"[shader] {hash[..16]} native programs={native.Count} (diagnostic, not executable).");

                foreach (var program in native)
                {
                    try
                    {
                        var ir = Rdna2ScalarCompiler.Decode(program.Code);

                        Console.Error.WriteLine(
                            $"[shader] {program.Stage} wave{(program.Wave64 ? 64 : 32)} scalar IR instructions={ir.Count}.");
                    }
                    catch (NotSupportedException ex)
                    {
                        Console.Error.WriteLine($"[shader] {program.Stage}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException
                                           or NotSupportedException)
            {
                Console.Error.WriteLine($"[shader] {hash[..16]} native inspection refused: {ex.Message}");
            }
        }
    }
}
