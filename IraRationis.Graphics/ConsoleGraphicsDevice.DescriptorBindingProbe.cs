// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R52UploadBytes = 560;
    private const int R52OutputBytes = 16;
    private const uint R52UnorderedAccess = 8;

    // Original authored fixture. No guest shader, descriptor or memory translation.
    private const string R52Shader = """
        cbuffer Parameters : register(b0)
        {
            uint Addend;
        };
        Buffer<uint> Input : register(t0);
        RWBuffer<uint> Output : register(u0);

        [numthreads(4, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            Output[id.x] = Input[id.x] + Addend;
        }
        """;

    internal static void VerifyR52DescriptorTables(nint guestQueue)
    {
        var queue = Host(guestQueue);

        if (_host == null || queue == 0)
        {
            throw new InvalidOperationException("R52 requires an initialized host device and direct queue.");
        }

        if (sizeof(R52BufferSrv) != sizeof(D3D12_SHADER_RESOURCE_VIEW_DESC) ||
            sizeof(R52BufferUav) != sizeof(D3D12_UNORDERED_ACCESS_VIEW_DESC))
        {
            throw new InvalidOperationException("R52 host descriptor ABI size mismatch.");
        }

        var output = nint.Zero;
        var readback = nint.Zero;
        var root = nint.Zero;
        var pipeline = nint.Zero;

        try
        {
            using var utility = AcquireUtilityContext(R52UploadBytes,
                2 * DescriptorTableCache.DescriptorsPerTable, versioned: true);

            var input = CreateR52UploadData();

            var outputDescription = HostBufferDescription(R52OutputBytes);

            outputDescription.Flags = 4;

            CheckHostResult(CreateHostResource(outputDescription, 1, 0, &output), "R52 UAV allocation");

            CheckHostResult(CreateHostResource(HostBufferDescription(R52OutputBytes), 3, CopyDestination, &readback),
                "R52 readback allocation");

            RequireR42Object(output, "R52 output");
            RequireR42Object(readback, "R52 readback");

            CreateR52Pipeline(out root, out pipeline);

            var catalog = new DescriptorBindingCatalog(2);
            var uploadIdentity = catalog.Register(1, R52UploadBytes, false);
            var outputIdentity = catalog.Register(2, R52OutputBytes, true);
            var cache = new DescriptorTableCache(catalog, 2);

            var first = new ComputeBufferTable(
                new BufferDescriptor(uploadIdentity, BufferViewKind.Constant, 0, 256),
                new BufferDescriptor(uploadIdentity, BufferViewKind.ShaderResource, 512, 16),
                new BufferDescriptor(outputIdentity, BufferViewKind.UnorderedAccess, 0, 16));

            var second = first with
            {
                Constant = first.Constant with { Offset = 256 },
                Input = first.Input with { Offset = 528 }
            };

            // Each ordinal is published only after the actual host fence wait succeeds.
            // These are private serial probe ordinals, not the host fence's native values.
            ExecuteR52Table(queue, utility, root, pipeline, output, readback, cache, first, 1, input, [9, 11, 13, 15]);

            var initialBytes = utility.LastUploadBytes;

            utility.Reset();

            MemoryMarshal.Cast<byte, uint>(input.AsSpan())[0] = 8;

            ExecuteR52Table(queue, utility, root, pipeline, output, readback, cache, first, 2, input, [10, 12, 14, 16]);

            var changedBytes = utility.LastUploadBytes;

            utility.Reset();

            MemoryMarshal.Cast<byte, uint>(input.AsSpan())[0] = 7;

            ExecuteR52Table(queue, utility, root, pipeline, output, readback, cache, second, 3, input, [29, 39, 49, 59]);

            var restoredBytes = utility.LastUploadBytes;

            utility.Reset();

            ExecuteR52Table(queue, utility, root, pipeline, output, readback, cache, first, 4, input, [9, 11, 13, 15]);

            if (initialBytes is not (0 or R52UploadBytes) || changedBytes != 256 || restoredBytes != 256 ||
                utility.LastUploadBytes != 0)
            {
                throw new InvalidOperationException("R53 versioned utility upload accounting mismatch.");
            }

            Console.WriteLine($"[graphics-probe] IRX-R53: initialBytes={initialBytes}; changedBytes={changedBytes}; " +
                              $"restoredBytes={restoredBytes}; unchangedBytes={utility.LastUploadBytes}; readback=verified.");

            cache.EnsureIdle();

            catalog.Remove(uploadIdentity);
            catalog.Remove(outputIdentity);

            if (cache.Hits != 2 || cache.Misses != 2 || cache.Materializations != 2 || cache.Backpressure != 0)
            {
                throw new InvalidOperationException("R52 descriptor cache accounting differs from the fixture.");
            }

            Console.WriteLine($"[graphics-probe] IRX-R52: dispatches=4; tableHits={cache.Hits}; " +
                              $"tableMisses={cache.Misses}; materializations={cache.Materializations}; " +
                              "nativeDescriptors=6; heapCount=1; readback=verified.");
        }
        finally
        {
            ReleaseR42Object(pipeline);
            ReleaseR42Object(root);
            ReleaseR42Object(readback);
            ReleaseR42Object(output);
        }
    }

    private static byte[] CreateR52UploadData()
    {
        var data = new byte[R52UploadBytes];
        var words = MemoryMarshal.Cast<byte, uint>(data.AsSpan());

        words[0] = 7;
        words[64] = 19;

        for (var index = 0; index < 4; index++)
        {
            words[128 + index] = (uint)(2 * (index + 1));
            words[132 + index] = (uint)(10 * (index + 1));
        }

        return data;
    }

    private static void ExecuteR52Table(nint queue, UtilityContext utility, nint root, nint pipeline,
        nint output, nint readback, DescriptorTableCache cache, ComputeBufferTable table, ulong ordinal,
        ReadOnlySpan<byte> input, ReadOnlySpan<uint> expected)
    {
        var lease = cache.TryAcquire(table) ?? throw new InvalidOperationException("R52 descriptor table exhausted.");
        var upload = utility.Upload;
        var list = utility.List;
        var submitted = false;
        var completed = false;

        try
        {
            utility.PrepareVersionedUpload(input);

            var nativeHeap = (ID3D12DescriptorHeap*)utility.Heap;
            var cpu = nativeHeap->GetCPUDescriptorHandleForHeapStart();
            var gpu = nativeHeap->GetGPUDescriptorHandleForHeapStart();
            var increment = _host->GetDescriptorHandleIncrementSize(0);

            if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0)
            {
                throw new InvalidOperationException("R52 descriptor handles are invalid.");
            }

            var offset = checked((ulong)lease.DescriptorOffset * increment);

            cpu.ptr = checked(cpu.ptr + (nuint)offset);
            gpu.ptr = checked(gpu.ptr + offset);

            if (lease.RequiresMaterialization)
            {
                MaterializeR52Table(cpu, increment, upload, output, table);

                cache.MarkMaterialized(lease);
            }

            var commands = (ID3D12GraphicsCommandList*)list;

            // Reset the output from an independent, zero-filled range before each dispatch.
            // A skipped dispatch cannot pass by reading the previous dispatch's result.
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, ulong, nint, ulong, ulong, void>)Slot(list, 15);

            TransitionHostResource(list, output, 0, CopyDestination);

            copy(list, output, 0, upload, 544, R52OutputBytes);

            // UPLOAD remains GENERIC_READ; the DEFAULT output is explicitly transitioned.
            TransitionHostResource(list, output, CopyDestination, R52UnorderedAccess);

            commands->SetDescriptorHeaps(1, &nativeHeap);

            commands->SetPipelineState((ID3D12PipelineState*)pipeline);

            commands->SetComputeRootSignature((ID3D12RootSignature*)root);

            commands->SetComputeRootDescriptorTable(0, gpu);

            commands->Dispatch(1, 1, 1);

            TransitionHostResource(list, output, R52UnorderedAccess, CopySource);

            copy(list, readback, 0, output, 0, R52OutputBytes);

            TransitionHostResource(list, output, CopySource, 0);

            cache.MarkSubmitted(lease, ordinal);

            submitted = true;

            utility.SubmitAndWait(queue);

            completed = true;

            cache.Collect(ordinal);

            VerifyR52Readback(readback, expected);
        }
        catch (Exception exception)
        {
            if (submitted && !completed)
            {
                Environment.FailFast("R52 GPU completion is unproven; refusing descriptor/resource release.", exception);
            }

            if (!submitted)
            {
                cache.Cancel(lease);
            }

            throw;
        }
    }

    private static void MaterializeR52Table(D3D12_CPU_DESCRIPTOR_HANDLE cpu, uint increment, nint upload,
        nint output, ComputeBufferTable table)
    {
        var uploadResource = (ID3D12Resource*)upload;
        var outputResource = (ID3D12Resource*)output;
        var address = uploadResource->GetGPUVirtualAddress();

        if (address == 0 || (address & 255UL) != 0)
        {
            throw new InvalidOperationException("R52 constant buffer address is invalid.");
        }

        var cbv = new D3D12_CONSTANT_BUFFER_VIEW_DESC
        {
            BufferLocation = checked(address + table.Constant.Offset),
            SizeInBytes = checked((uint)table.Constant.Length)
        };

        _host->CreateConstantBufferView(&cbv, cpu);

        cpu.ptr = checked(cpu.ptr + increment);

        // Native PC D3D12 view layout, checked against TerraFX structure sizes.
        var srv = new R52BufferSrv
        {
            Format = 42,
            Dimension = 1,
            Mapping = 5768,
            FirstElement = table.Input.Offset / 4,
            Elements = checked((uint)(table.Input.Length / 4))
        };

        _host->CreateShaderResourceView(uploadResource, (D3D12_SHADER_RESOURCE_VIEW_DESC*)&srv, cpu);

        cpu.ptr = checked(cpu.ptr + increment);

        var uav = new R52BufferUav
        {
            Format = 42,
            Dimension = 1,
            FirstElement = table.Output.Offset / 4,
            Elements = checked((uint)(table.Output.Length / 4))
        };

        _host->CreateUnorderedAccessView(outputResource, null, (D3D12_UNORDERED_ACCESS_VIEW_DESC*)&uav, cpu);
    }

    private static void VerifyR52Readback(nint readback, ReadOnlySpan<uint> expected)
    {
        var range = new HostRange { End = R52OutputBytes };
        var noWrite = default(HostRange);
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

        CheckHostResult(map(readback, 0, &range, &mapped), "R52 map readback");

        try
        {
            if (mapped == null || !new ReadOnlySpan<uint>(mapped, R52OutputBytes / sizeof(uint)).SequenceEqual(expected))
            {
                throw new InvalidOperationException("R52 CBV/SRV/UAV compute readback differs from expected values.");
            }
        }
        finally
        {
            unmap(readback, 0, &noWrite);
        }
    }

    private static void CreateR52Pipeline(out nint root, out nint pipeline, string? source = null)
    {
        root = 0;
        pipeline = 0;

        var shader = CompileR10Shader(source ?? R52Shader, "cs_5_1", Console.Error)
                     ?? throw new InvalidOperationException("R52 compute shader compilation failed.");
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE1[3];

        for (var index = 0; index < 3; index++)
        {
            ranges[index] = new D3D12_DESCRIPTOR_RANGE1
            {
                RangeType = (D3D12_DESCRIPTOR_RANGE_TYPE)((index + 2) % 3),
                NumDescriptors = 1,
                BaseShaderRegister = 0,
                OffsetInDescriptorsFromTableStart = (uint)index
            };
        }

        var parameter = new D3D12_ROOT_PARAMETER1 { ParameterType = 0 };

        parameter.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 3,
            pDescriptorRanges = ranges
        };

        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 1,
            pParameters = &parameter
        };

        var blob = nint.Zero;
        var errors = nint.Zero;
        var nativeRoot = (void*)null;
        var nativePipeline = (void*)null;

        try
        {
            var hr = SerializeR10RootNative(&description, &blob, &errors);

            if (errors != 0)
            {
                Console.Error.WriteLine(Encoding.UTF8.GetString(ReadR10Blob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            CheckHostResult(hr, "R52 root serialization");

            RequireR42Object(blob, "R52 root blob");

            var rootBytes = ReadR10Blob(blob, 1024 * 1024);
            var rootId = RootSignatureId;

            fixed (byte* bytes = rootBytes)
            {
                CheckHostResult(_host->CreateRootSignature(0, bytes, (nuint)rootBytes.Length, &rootId, &nativeRoot),
                    "R52 root signature");
            }

            RequireR42Object((nint)nativeRoot, "R52 root signature");

            var pipelineId = PipelineId;

            fixed (byte* bytes = shader)
            {
                var state = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)nativeRoot,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = bytes, BytecodeLength = (nuint)shader.Length }
                };

                CheckHostResult(_host->CreateComputePipelineState(&state, &pipelineId, &nativePipeline),
                    "R52 compute pipeline");
            }

            RequireR42Object((nint)nativePipeline, "R52 compute pipeline");

            root = (nint)nativeRoot;
            pipeline = (nint)nativePipeline;

            nativeRoot = null;
            nativePipeline = null;
        }
        finally
        {
            ReleaseR42Object((nint)nativePipeline);
            ReleaseR42Object((nint)nativeRoot);
            ReleaseR42Object(errors);
            ReleaseR42Object(blob);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct R52BufferSrv
    {
        [FieldOffset(0)]
        public uint Format;

        [FieldOffset(4)]
        public uint Dimension;

        [FieldOffset(8)]
        public uint Mapping;

        [FieldOffset(16)]
        public ulong FirstElement;

        [FieldOffset(24)]
        public uint Elements;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct R52BufferUav
    {
        [FieldOffset(0)]
        public uint Format;

        [FieldOffset(4)]
        public uint Dimension;

        [FieldOffset(8)]
        public ulong FirstElement;

        [FieldOffset(16)]
        public uint Elements;
    }
}
