// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Compute;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    // Authored fixture: a cubic x=t^3 segment and a quadratic x=1+3t^2
    // segment. Expected values are independent closed-form evaluations.
    // No guest executable, descriptors, or captured coefficients are required.
    private static void VerifyCurveLutGpu(nint queue)
    {
        VerifyCurveLutGpu(queue, 2);
        VerifyCurveLutGpu(queue, 3);
    }

    private static void VerifyCurveLutGpu(nint queue, int segments)
    {
        var width = segments == 2 ? 17U : 29U;
        const uint bytes = 256;
        const double maximumAllowedTexelAbsoluteError = 0.0002;
        nint root = 0, pipeline = 0, texture = 0, readback = 0;

        try
        {
            var code = CompileHostShader(CurveLutCompiler.CreateHostSource(41, segments), "cs_5_1", Console.Out)
                       ?? throw new InvalidOperationException("Curve LUT probe shader failed.");
            var rootBytes = SerializeColorLutRoot()
                            ?? throw new InvalidOperationException("Curve LUT probe root failed.");
            var rootId = RootSignatureId;
            var pipelineId = PipelineId;
            void* native = null;

            fixed (byte* rootData = rootBytes)
            {
                var result = (int)_host->CreateRootSignature(0, rootData, (nuint)rootBytes.Length, &rootId, &native);
                root = (nint)native;
                CheckHostResult(result, "Curve LUT probe root creation");
                RequireHostObject(root, "Curve LUT probe root");
            }

            native = null;

            fixed (byte* shader = code)
            {
                var description = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)root,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = shader, BytecodeLength = (nuint)code.Length }
                };
                var result = (int)_host->CreateComputePipelineState(&description, &pipelineId, &native);
                pipeline = (nint)native;
                CheckHostResult(result, "Curve LUT probe pipeline creation");
                RequireHostObject(pipeline, "Curve LUT probe pipeline");
            }

            var output = new ResourceDescription
            {
                Dimension = 2, Width = width, Height = 1, DepthOrArraySize = 1,
                MipLevels = 1, Format = 41, SampleCount = 1, Flags = 4
            };
            CheckHostResult(CreateHostResource(output, 1, 0, &texture), "Curve LUT probe Texture1D");
            CheckHostResult(CreateHostResource(HostBufferDescription(bytes), 3, CopyDestination, &readback),
                "Curve LUT probe readback");
            RequireHostObject(texture, "Curve LUT probe Texture1D");
            RequireHostObject(readback, "Curve LUT probe readback");

            using var utility = AcquireUtilityContext(CurveUploadBytes, 2, true);
            var data = new byte[CurveUploadBytes];
            var parameters = MemoryMarshal.Cast<byte, uint>(data.AsSpan(0, 16));
            parameters[0] = BitConverter.SingleToUInt32Bits(0.25f);
            parameters[1] = width;
            parameters[2] = BitConverter.SingleToUInt32Bits(1.0f);
            ReadOnlySpan<float> curves = segments == 2
                ? [0, 0, 0, 1, 0, 1, 2, 3, 1, 1, 2, 4, 2, 3, 4, 5]
                : [0, 0, 0, 1, 0, 1, 2, 3, 1, 1, 2, 4, 2, 3, 4, 5, 4, 5, 6, 7, 5, 6, 7, 8];
            MemoryMarshal.AsBytes(curves).CopyTo(data.AsSpan(CurveDataOffset));
            utility.PrepareVersionedUpload(data);
            RecordCurveLutDispatch(utility, texture, root, pipeline, 41, 1);
            TransitionHostResource(utility.List, texture, 0, CopySource);
            RecordHostTextureTransfer(utility.List, texture, readback, bytes,
                new HostTextureTransferFootprint(0, 41, width, 1, bytes, 0), false);
            TransitionHostResource(utility.List, texture, CopySource, 0);
            utility.SubmitAndWait(queue);

            var read = new HostRange { End = bytes };
            var noWrite = default(HostRange);
            var mapped = (void*)null;
            var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
            var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);
            CheckHostResult(map(readback, 0, &read, &mapped), "Curve LUT probe map");

            var maximumObservedTexelAbsoluteError = 0.0;

            try
            {
                if (mapped == null)
                {
                    throw new InvalidOperationException("Curve LUT probe readback is null.");
                }

                var result = new ReadOnlySpan<float>(mapped, (int)width);

                for (var index = 0; index < result.Length; index++)
                {
                    var x = index * 0.25;
                    var expected = x < 1.0 ? 3.0 * Math.Cbrt(x) :
                        segments == 3 && x >= 4.0 ? x + 1.0 : 2.0 + 3.0 * Math.Sqrt((x - 1.0) / 3.0);

                    var actual = result[index];
                    var absoluteError = Math.Abs(actual - expected);

                    if (!float.IsFinite(actual) || absoluteError > maximumAllowedTexelAbsoluteError)
                    {
                        throw new InvalidOperationException(
                            $"Curve LUT probe ({segments} segments) texel {index} expected {expected:R} and read {actual:R}. " +
                            $"Absolute error {absoluteError:R} has a limit of {maximumAllowedTexelAbsoluteError:R}.");
                    }

                    maximumObservedTexelAbsoluteError = Math.Max(maximumObservedTexelAbsoluteError, absoluteError);
                }
            }
            finally
            {
                unmap(readback, 0, &noWrite);
            }

            Console.WriteLine(
                $"[graphics-probe] Curve LUT GPU readback passed: {segments} segments, {width} texels. " +
                $"Maximum texel absolute error {maximumObservedTexelAbsoluteError:R} with limit {maximumAllowedTexelAbsoluteError:R}.");
        }
        finally
        {
            foreach (var value in new[] { readback, texture, pipeline, root })
            {
                if (value != 0)
                {
                    _ = HostRelease(value);
                }
            }
        }
    }
}
