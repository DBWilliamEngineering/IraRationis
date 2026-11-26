// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    internal static void VerifyComputePipelineAbi(nint device)
    {
        var createRoot = (delegate* unmanaged[Stdcall]<nint, uint, byte*, nuint, Guid*, nint*, int>)Slot(device, 16);
        var create = (delegate* unmanaged[Stdcall]<nint, ComputeDescription*, Guid*, nint*, int>)Slot(device, 11);
        var createX =
            (delegate* unmanaged[Stdcall]<nint, ComputeDescription*, uint, void*, Guid*, nint*, int>)Slot(device, 62);
        var rootId = RootSignatureId;
        var pipelineId = PipelineId;
        var invalidId = Guid.Empty;
        var bytes = stackalloc byte[4] { 1, 2, 3, 4 };
        var root = (nint)0;
        var first = (nint)0;
        var second = (nint)0;
        var queried = (nint)0;

        try
        {
            if (createRoot(device, 0, bytes, 4, &rootId, &root) != 0 || root == 0)
            {
                throw new InvalidOperationException("Compute probe root creation failed.");
            }

            var description = new ComputeDescription { Root = root, Code = bytes, Size = 4 };

            if (create(device, &description, &pipelineId, &first) != 0 || first == 0 ||
                createX(device, &description, 0, null, &pipelineId, &second) != 0 || second == 0 || first == second)
            {
                throw new InvalidOperationException("Compute creation ABI failed.");
            }

            bytes[0] = 99;

            lock (PipelineGate)
            {
                var a = ComputePipelines[first];
                var b = ComputePipelines[second];

                if (!ReferenceEquals(a.Shader, b.Shader) || a.Shader.Bytes[0] != 1 || a.Shader.References != 2)
                {
                    throw new InvalidOperationException("Compute shader ownership or deduplication failed.");
                }
            }

            for (var index = 0; index < 8; index++)
            {
                if (((uint*)(first + 0x10))[index] != 0)
                {
                    throw new InvalidOperationException("Console pipeline packet must remain zero until implemented.");
                }
            }

            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(first, 0);

            if (query(first, &pipelineId, &queried) != 0 || queried != first || Drop(queried) != 1)
            {
                throw new InvalidOperationException("Pipeline QueryInterface failed.");
            }

            queried = 0;

            var rejected = (nint)123;

            if (query(first, &invalidId, &rejected) != NoInterface || rejected != 0 ||
                createX(device, &description, 1, null, &pipelineId, &rejected) != InvalidArgument || rejected != 0)
            {
                throw new InvalidOperationException("Pipeline invalid argument handling failed.");
            }

            _ = Drop(root);

            root = 0;

            if (!IsRootSignature(description.Root))
            {
                throw new InvalidOperationException("Pipeline did not retain its root signature.");
            }

            _ = Drop(first);

            first = 0;

            _ = Drop(second);

            second = 0;

            if (IsRootSignature(description.Root))
            {
                throw new InvalidOperationException("Pipeline did not release its root signature.");
            }
        }
        finally
        {
            if (queried != 0)
            {
                _ = Drop(queried);
            }

            if (first != 0)
            {
                _ = Drop(first);
            }

            if (second != 0)
            {
                _ = Drop(second);
            }

            if (root != 0)
            {
                _ = Drop(root);
            }
        }
    }
}
