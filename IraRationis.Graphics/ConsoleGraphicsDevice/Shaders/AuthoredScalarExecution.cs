// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Descriptors;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Media;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Shaders.Compute;
using IraRationis.Shaders.Scalar;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int AuthoredScalarBytes = ScalarReferenceExecutor.StateWords * sizeof(uint);
    private const int AuthoredScalarInputA = 256;
    private const int AuthoredScalarInputB = AuthoredScalarInputA + AuthoredScalarBytes;
    private const int AuthoredScalarZero = AuthoredScalarInputB + AuthoredScalarBytes;
    private const int AuthoredScalarUploadBytes = AuthoredScalarZero + AuthoredScalarBytes;
    private const int AuthoredScalarDispatches = 3;

    /// <summary>
    ///     Original scalar fixture executed through the runtime-owned D3D12 device and
    ///     media queue. No guest descriptors, memory addresses or title assets are accepted.
    /// </summary>
    public static void VerifyAuthoredScalarBatch(MediaGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ScalarValueGraphProbe.Verify();

        var started = Stopwatch.GetTimestamp();
        // Original fixture: dynamic add/subtract and bitwise operations, repeated
        // expressions, overwritten values, then the existing known constant results.
        uint[] words =
        [
            0x80058104, 0x80068104, 0x80878104, 0x87080405, 0x88090405, 0x890a0405,
            0xbe8b0385, 0xbe8b0386, 0xbe800385, 0x80018100, 0xb002fffe,
            0x8003ff01, 0x12345678, 0x800c810c, 0xbf810000
        ];
        var code = new byte[words.Length * sizeof(uint)];

        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(index * sizeof(uint)), words[index]);
        }

        var inputA = new uint[ScalarReferenceExecutor.StateWords];
        var inputB = new uint[ScalarReferenceExecutor.StateWords];

        inputA[4] = 0x11223344;
        inputB[4] = 0x55667788;
        inputA[ScalarReferenceExecutor.RegisterCount] = 1;
        inputB[12] = uint.MaxValue;

        var expectedA = ScalarReferenceExecutor.Execute(code, inputA);
        var expectedB = ScalarReferenceExecutor.Execute(code, inputB);

        if (expectedA[0] != 5 || expectedA[1] != 6 || expectedA[2] != 0xfffffffe ||
            expectedA[3] != 0x1234567e || expectedA[4] != inputA[4] || expectedB[4] != inputB[4] ||
            expectedA[12] != 1 || expectedB[12] != 0 ||
            expectedA[ScalarReferenceExecutor.RegisterCount] != 0 ||
            expectedB[ScalarReferenceExecutor.RegisterCount] != 1)
        {
            throw new InvalidOperationException("Authored scalar reference result differs from known constants.");
        }

        var graph = ScalarValueGraph.Build(code);

        if (!graph.Evaluate(inputA).AsSpan().SequenceEqual(expectedA) ||
            !graph.Evaluate(inputB).AsSpan().SequenceEqual(expectedB))
        {
            throw new InvalidOperationException("R65 optimized scalar graph differs from CPU reference.");
        }

        var source = graph.EmitTypedCompute();

        Console.WriteLine($"[runtime] IRX-R65: values={graph.ValueCount}; live={graph.LiveValueCount}; eliminated={graph.ValueCount - graph.LiveValueCount}; sourceChars={source.Length}; backend=typed-hlsl.");

        var lowered = Stopwatch.GetTimestamp();

        context.AcquireDeviceQueue(out var device, out var queue);

        try
        {
            if (device != (nint)_host || queue == 0)
            {
                throw new InvalidOperationException("Authored scalar execution requires the runtime host device.");
            }

            ExecuteAuthoredScalarBatch(queue, source, inputA, inputB, expectedA, expectedB, started, lowered);

            if (Environment.GetEnvironmentVariable("IRX_SAMPLE_TYPED_COMPUTE") == "1")
            {
                VerifyAuthoredTypedCompute(queue);
            }
        }
        finally
        {
            ReleaseHostObject(queue);
            ReleaseHostObject(device);
        }
    }

    private static void VerifyAuthoredTypedCompute(nint queue)
    {
        var started = Stopwatch.GetTimestamp();
        var program = TypedComputeProbe.CreateProgram();

        TypedComputeProbe.Verify(program);

        var inputA = TypedComputeProbe.CreateInput(3);
        var inputB = TypedComputeProbe.CreateInput(100);
        var expectedA = TypedComputeProbe.Expected(3);
        var expectedB = TypedComputeProbe.Expected(100);
        var source = program.EmitHlsl();
        var lowered = Stopwatch.GetTimestamp();

        ExecuteAuthoredScalarBatch(queue, source, inputA, inputB, expectedA, expectedB, started, lowered,
            "IRX-R66-GPU");

        Console.WriteLine("[runtime] IRX-R66: returned=2; exhausted=1; outputWords=309; partialOutput=none; readback=verified.");
    }

    private static void ExecuteAuthoredScalarBatch(nint queue, string source, ReadOnlySpan<uint> inputA,
        ReadOnlySpan<uint> inputB, ReadOnlySpan<uint> expectedA, ReadOnlySpan<uint> expectedB,
        long started, long lowered, string reportMarker = "IRX-R58-62")
    {
        if (sizeof(BufferShaderResourceView) != sizeof(D3D12_SHADER_RESOURCE_VIEW_DESC) ||
            sizeof(BufferUnorderedAccessView) != sizeof(D3D12_UNORDERED_ACCESS_VIEW_DESC))
        {
            throw new InvalidOperationException("Authored scalar host descriptor ABI mismatch.");
        }

        nint root = 0, pipeline = 0, output = 0, readback = 0;

        try
        {
            CreateDescriptorComputePipeline(out root, out pipeline, source);

            var compiled = Stopwatch.GetTimestamp();
            var description = HostBufferDescription(AuthoredScalarBytes);

            description.Flags = 4;

            CheckHostResult(CreateHostResource(description, 1, 0, &output), "Authored scalar UAV");
            CheckHostResult(CreateHostResource(HostBufferDescription(AuthoredScalarDispatches * AuthoredScalarBytes),
                3, CopyDestination, &readback), "Authored scalar readback");

            RequireHostObject(output, "Authored scalar UAV");
            RequireHostObject(readback, "Authored scalar readback");

            using var utility = AcquireUtilityContext(AuthoredScalarUploadBytes,
                2 * DescriptorTableCache.DescriptorsPerTable, versioned: true);

            var data = new byte[AuthoredScalarUploadBytes];

            MemoryMarshal.AsBytes(inputA).CopyTo(data.AsSpan(AuthoredScalarInputA, AuthoredScalarBytes));
            MemoryMarshal.AsBytes(inputB).CopyTo(data.AsSpan(AuthoredScalarInputB, AuthoredScalarBytes));

            utility.PrepareVersionedUpload(data);

            var catalog = new DescriptorBindingCatalog(2);
            var uploadIdentity = catalog.Register(1, AuthoredScalarUploadBytes, false);
            var outputIdentity = catalog.Register(2, AuthoredScalarBytes, true);
            var cache = new DescriptorTableCache(catalog, 2);
            var tableA = new ComputeBufferTable(
                new BufferDescriptor(uploadIdentity, BufferViewKind.Constant, 0, 256),
                new BufferDescriptor(uploadIdentity, BufferViewKind.ShaderResource, AuthoredScalarInputA, AuthoredScalarBytes),
                new BufferDescriptor(outputIdentity, BufferViewKind.UnorderedAccess, 0, AuthoredScalarBytes));
            var tableB = tableA with { Input = tableA.Input with { Offset = AuthoredScalarInputB } };
            var leaseA = cache.TryAcquire(tableA) ?? throw new InvalidOperationException("Scalar table A unavailable.");
            var leaseB = cache.TryAcquire(tableB) ?? throw new InvalidOperationException("Scalar table B unavailable.");

            var heap = (ID3D12DescriptorHeap*)utility.Heap;
            var cpu = heap->GetCPUDescriptorHandleForHeapStart();
            var gpu = heap->GetGPUDescriptorHandleForHeapStart();
            var increment = _host->GetDescriptorHandleIncrementSize(0);

            if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0)
            {
                throw new InvalidOperationException("Invalid authored scalar descriptor handles.");
            }

            var gpuA = BindAuthoredScalarTable(cache, leaseA, cpu, gpu, increment, utility.Upload, output);
            var gpuB = BindAuthoredScalarTable(cache, leaseB, cpu, gpu, increment, utility.Upload, output);
            var list = (ID3D12GraphicsCommandList*)utility.List;

            list->SetDescriptorHeaps(1, &heap);
            list->SetPipelineState((ID3D12PipelineState*)pipeline);
            list->SetComputeRootSignature((ID3D12RootSignature*)root);

            for (var index = 0; index < AuthoredScalarDispatches; index++)
            {
                // Clear output before each dispatch; read back every result separately.
                // Explicit transitions order UAV writes against the copy operations.
                TransitionHostResource(utility.List, output, 0, CopyDestination);
                list->CopyBufferRegion((ID3D12Resource*)output, 0, (ID3D12Resource*)utility.Upload,
                    AuthoredScalarZero, AuthoredScalarBytes);
                TransitionHostResource(utility.List, output, CopyDestination, 8);
                list->SetComputeRootDescriptorTable(0, index == 1 ? gpuB : gpuA);
                list->Dispatch(1, 1, 1);
                TransitionHostResource(utility.List, output, 8, CopySource);
                list->CopyBufferRegion((ID3D12Resource*)readback, (ulong)(index * AuthoredScalarBytes),
                    (ID3D12Resource*)output, 0, AuthoredScalarBytes);
                TransitionHostResource(utility.List, output, CopySource, 0);
            }

            // Private logical table ordinals, both covered by ONE real completion.
            // Nothing can reclaim or overwrite a table before the completion wait.
            cache.MarkSubmitted(leaseA, 1);
            cache.MarkSubmitted(leaseB, 2);

            var recorded = Stopwatch.GetTimestamp();

            utility.SubmitAndWait(queue);

            var completed = Stopwatch.GetTimestamp();

            cache.Collect(2);
            cache.EnsureIdle();
            catalog.Remove(uploadIdentity);
            catalog.Remove(outputIdentity);

            VerifyAuthoredScalarReadback(readback, expectedA, expectedB);

            if (cache.Materializations != 2 || cache.Misses != 2 || cache.Backpressure != 0)
            {
                throw new InvalidOperationException("Authored scalar descriptor accounting mismatch.");
            }

            Console.WriteLine(FormattableString.Invariant(
                $"[runtime] {reportMarker}: scalarDispatches={AuthoredScalarDispatches}; tableMaterializations={cache.Materializations}; submissions=1; completionWaits=1; uploadBytes={utility.LastUploadBytes}; loweringMs={Stopwatch.GetElapsedTime(started, lowered).TotalMilliseconds:F3}; compileAndAcquireMs={Stopwatch.GetElapsedTime(lowered, compiled).TotalMilliseconds:F3}; recordMs={Stopwatch.GetElapsedTime(compiled, recorded).TotalMilliseconds:F3}; completionMs={Stopwatch.GetElapsedTime(recorded, completed).TotalMilliseconds:F3}; readback=verified."));
        }
        finally
        {
            ReleaseHostObject(pipeline);
            ReleaseHostObject(root);
            ReleaseHostObject(readback);
            ReleaseHostObject(output);
        }
    }

    private static D3D12_GPU_DESCRIPTOR_HANDLE BindAuthoredScalarTable(DescriptorTableCache cache,
        DescriptorTableCacheLease lease, D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE gpu,
        uint increment, nint upload, nint output)
    {
        var offset = checked((ulong)lease.DescriptorOffset * increment);

        cpu.ptr = checked(cpu.ptr + (nuint)offset);
        gpu.ptr = checked(gpu.ptr + offset);

        if (lease.RequiresMaterialization)
        {
            MaterializeTable(cpu, increment, upload, output, lease.Table);
            cache.MarkMaterialized(lease);
        }

        return gpu;
    }

    private static void VerifyAuthoredScalarReadback(nint readback, ReadOnlySpan<uint> expectedA,
        ReadOnlySpan<uint> expectedB)
    {
        var range = new HostRange { End = AuthoredScalarDispatches * AuthoredScalarBytes };
        var noWrite = default(HostRange);
        void* mapped = null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

        CheckHostResult(map(readback, 0, &range, &mapped), "Authored scalar readback map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("Authored scalar readback is null.");
            }

            var results = new ReadOnlySpan<uint>(mapped, AuthoredScalarDispatches * ScalarReferenceExecutor.StateWords);

            for (var index = 0; index < AuthoredScalarDispatches; index++)
            {
                var expected = index == 1 ? expectedB : expectedA;
                var actual = results.Slice(index * ScalarReferenceExecutor.StateWords, ScalarReferenceExecutor.StateWords);

                if (!actual.SequenceEqual(expected))
                {
                    throw new InvalidOperationException($"Authored scalar dispatch {index} differs from CPU reference.");
                }
            }
        }
        finally
        {
            unmap(readback, 0, &noWrite);
        }
    }
}
