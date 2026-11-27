// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void ValidateDeviceBatchRegistration(nint* device)
    {
        if (device[10]
            != (nint)(delegate* unmanaged[Stdcall]<nint, GraphicsDescription*, Guid*, nint*, int>)
            &CreateGraphicsPipeline ||
            device[38]
            != (nint)(delegate* unmanaged[Stdcall]<nint, ResourceDescription*, uint, uint, ulong, CopyFootprint*, uint*,
                ulong*, ulong*, void>)&GetCopyableFootprints ||
            device[41]
            != (nint)(delegate* unmanaged[Stdcall]<nint, CommandSignatureDescription*, nint, Guid*, nint*, int>)
            &CreateCommandSignature ||
            device[86]
            != (nint)(delegate* unmanaged[Stdcall]<nint, PipelineStreamDescription*, Guid*, nint*, int>)
            &CreatePipelineStateStream ||
            device[108]
            != (nint)(delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription1*,
                AllocationInfo1*, AllocationInfo*>)&GetResourceAllocationInfo2)
        {
            throw new InvalidOperationException("Graphics device batch ABI registration is incomplete.");
        }
    }

    internal static void VerifyDeviceBatchAbi(nint device)
    {
        VerifyCommandSignatureAbi(device);

        VerifyGraphicsPipelineAbi(device);

        VerifyCopyFootprintsAbi(device);

        Console.WriteLine(
            "[graphics-probe] Command signature, graphics pipeline stream and resource footprint ABI tests passed.");
    }

    private static void VerifyCommandSignatureAbi(nint device)
    {
        var create =
            (delegate* unmanaged[Stdcall]<nint, CommandSignatureDescription*, nint, Guid*, nint*, int>)Slot(device, 41);
        var arguments = stackalloc uint[4] { 2, 3, 4, 5 };
        var description = new CommandSignatureDescription { Stride = 16, Count = 1, Arguments = arguments };
        var iid = CommandSignatureId;
        var signature = (nint)0;

        try
        {
            if (create(device, &description, 0, &iid, &signature) != 0 || signature == 0)
            {
                throw new InvalidOperationException("Command signature creation failed.");
            }

            arguments[0] = 99;

            var stored = (CommandSignatureObject*)signature;

            if (stored->Stride != 16 || stored->Count != 1 || ((uint*)(stored + 1))[0] != 2)
            {
                throw new InvalidOperationException("Command signature argument ownership failed.");
            }

            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(signature, 0);
            var result = (nint)0;

            if (query(signature, &iid, &result) != 0 || result != signature || Drop(result) != 1)
            {
                throw new InvalidOperationException("Command signature QueryInterface failed.");
            }

            description.Arguments = null;

            if (create(device, &description, 0, &iid, &result) != InvalidArgument || result != 0)
            {
                throw new InvalidOperationException("Command signature invalid arguments accepted.");
            }
        }
        finally
        {
            if (signature != 0)
            {
                _ = Drop(signature);
            }
        }
    }

    private static void VerifyGraphicsPipelineAbi(nint device)
    {
        var create = (delegate* unmanaged[Stdcall]<nint, GraphicsDescription*, Guid*, nint*, int>)Slot(device, 10);
        var createStream =
            (delegate* unmanaged[Stdcall]<nint, PipelineStreamDescription*, Guid*, nint*, int>)Slot(device, 86);
        var code = stackalloc byte[4] { 9, 8, 7, 6 };
        var description = new GraphicsDescription { Vertex = new ShaderBytecode { Code = code, Size = 4 } };
        var stream = stackalloc ulong[4] { 1, (ulong)code, 4, 14UL | (3UL << 32) };
        var streamDescription = new PipelineStreamDescription { Size = 32, Stream = (byte*)stream };
        var iid = PipelineId;
        var first = (nint)0;
        var second = (nint)0;

        try
        {
            if (create(device, &description, &iid, &first) != 0 || first == 0 ||
                createStream(device, &streamDescription, &iid, &second) != 0 || second == 0)
            {
                throw new InvalidOperationException("Graphics pipeline creation failed.");
            }

            code[0] = 99;

            lock (PipelineGate)
            {
                var plain = GraphicsPipelines[first];
                var streamed = GraphicsPipelines[second];

                if (plain.Shaders.Count != 1 || streamed.Shaders.Count != 1
                                             || !ReferenceEquals(plain.Shaders[0].Container,
                                                 streamed.Shaders[0].Container) ||
                                             plain.Shaders[0].Container.Bytes[0] != 9 || !streamed.CompleteStream
                                             || streamed.State.Count != 1 || streamed.State[0].Kind != 14)
                {
                    throw new InvalidOperationException("Graphics pipeline shader ownership or stream parsing failed.");
                }
            }

            streamDescription.Size = 16;

            var rejected = (nint)123;

            if (createStream(device, &streamDescription, &iid, &rejected) != InvalidArgument || rejected != 0)
            {
                throw new InvalidOperationException("Truncated pipeline stream accepted.");
            }
        }
        finally
        {
            if (first != 0)
            {
                _ = Drop(first);
            }

            if (second != 0)
            {
                _ = Drop(second);
            }
        }
    }

    private static void VerifyCopyFootprintsAbi(nint device)
    {
        var copy =
            (delegate* unmanaged[Stdcall]<nint, ResourceDescription*, uint, uint, ulong, CopyFootprint*, uint*, ulong*,
                ulong*, void>)Slot(device, 38);
        var allocation =
            (delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription1*, AllocationInfo1*,
                AllocationInfo*>)Slot(device, 108);
        var texture = new ResourceDescription
        {
            Dimension = 3,
            Width = 65,
            Height = 3,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 28,
            SampleCount = 1
        };

        CopyFootprint footprint = default;

        uint rows = 0;

        ulong rowSize = 0;
        ulong total = 0;

        copy(device, &texture, 0, 1, 1, &footprint, &rows, &rowSize, &total);

        if (footprint.Offset != 512 || footprint.RowPitch != 512 || rows != 3 || rowSize != 260 || total != 1795)
        {
            throw new InvalidOperationException("Copy footprint alignment failed.");
        }

        texture.Width = 5;

        texture.Height = 5;

        texture.Format = 71;

        copy(device, &texture, 0, 1, 0, &footprint, &rows, &rowSize, &total);

        if (footprint.Width != 8 || footprint.Height != 8 || rows != 2 || rowSize != 16 || total != 272)
        {
            throw new InvalidOperationException("Block-compressed footprint failed.");
        }

        copy(device, &texture, 1, 1, 0, &footprint, &rows, &rowSize, &total);

        if (total != ulong.MaxValue || footprint.Offset != ulong.MaxValue)
        {
            throw new InvalidOperationException("Invalid subresource accepted.");
        }

        var descriptions = stackalloc ResourceDescription1[2];

        descriptions[0] = new ResourceDescription1
        {
            Description = new ResourceDescription { Dimension = 1, Width = 1, SampleCount = 1 }
        };

        descriptions[1] = new ResourceDescription1
        {
            Description = new ResourceDescription { Dimension = 1, Width = 1, Alignment = 131072, SampleCount = 1 }
        };

        var each = stackalloc AllocationInfo1[2];

        AllocationInfo result = default;

        if (allocation(&result, device, 0, 2, descriptions, each) != &result || result.Size != 262144
                                                                             || result.Alignment != 131072
                                                                             || each[0].Offset != 0
                                                                             || each[1].Offset != 131072
                                                                             || each[1].Size != 131072)
        {
            throw new InvalidOperationException("Extended resource allocation placement failed.");
        }
    }
}
