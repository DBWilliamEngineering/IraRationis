// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R17SmallBytes = 128;
    private const int R17LargeBytes = 2704;
    private const int R17LargeOffset = 256;
    private const int R17UploadBytes = 3072;
    private static readonly Lock R17PipelineGate = new();
    private static byte[]? _r17Bytecode;
    private static int _r174ComputeReported;

    private static bool RefuseR17(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-COMPUTE-R17: REFUSED: {reason}.");

        return false;
    }

    private static bool TryR17Output(ulong handle, out R16Source? source)
    {
        source = null;

        if (!TryReadMappingRecord(handle, out DescriptorToken token) || token.Tag != DescriptorTag || token.Kind != 19)
        {
            return false;
        }

        DescriptorMetadata? record;

        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out record);
        }

        if (record is not { Kind: 19 } || record.Counter != 0 || !IsResource(record.Resource))
        {
            return false;
        }

        var resource = (ResourceObject*)record.Resource;
        var desc = resource->Description;

        if (resource->Generation != record.ResourceGeneration || desc.Dimension != 4 || desc.MipLevels != 1 ||
            desc.SampleCount != 1 || desc.Width is < 33 or > 40 || desc.Height is < 33 or > 40 ||
            desc.DepthOrArraySize != 33 || (desc.Flags & 4U) == 0 || desc.Format is not (2 or 10 or 11 or 24 or 28))
        {
            Console.Error.WriteLine(
                $"[graphics] R17 UAV: dimension={desc.Dimension}, size={desc.Width}x{desc.Height}x{desc.DepthOrArraySize}, format={desc.Format}, mips={desc.MipLevels}, flags=0x{desc.Flags:X}.");

            return false;
        }

        if (record.Description is { } bytes && (bytes.Length != 40 || R16Word(bytes, 0) != desc.Format ||
                                                R16Word(bytes, 4) != 8 || R16Word(bytes, 8) != 0
                                                || R16Word(bytes, 12) != 0 ||
                                                (R16Word(bytes, 16) != desc.DepthOrArraySize
                                                 && R16Word(bytes, 16) != uint.MaxValue)))
        {
            Console.Error.WriteLine("[graphics] R17 UAV view is not the full mip-zero Texture3D.");

            return false;
        }

        source = new R16Source(handle, token.Id, token.Kind, record.Resource, resource->Generation, record.Description);

        return true;
    }

    private static bool TryR17Constants(ulong address, int minimum, out ConstantDescriptor descriptor)
    {
        return TryReadMappingRecord(address, out descriptor) && descriptor.Tag == ConstantTag &&
               descriptor.Size >= minimum && descriptor.Address != 0 &&
               descriptor.Address <= (ulong)nint.MaxValue - (uint)minimum;
    }

    private static bool R17SameConstant(ConstantDescriptor a, ConstantDescriptor b)
    {
        return a.Tag == b.Tag && a.Size == b.Size && a.Address == b.Address;
    }

    private static byte[]? SerializeR17Root()
    {
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[3];
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE1[2];

        for (var i = 0; i < 2; i++)
        {
            parameters[i] = new D3D12_ROOT_PARAMETER1
            {
                ParameterType = (D3D12_ROOT_PARAMETER_TYPE)2,
                ShaderVisibility = 0
            };

            parameters[i].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1 { ShaderRegister = (uint)i };

            ranges[i] = new D3D12_DESCRIPTOR_RANGE1
            {
                RangeType = (D3D12_DESCRIPTOR_RANGE_TYPE)i,
                NumDescriptors = 1,
                BaseShaderRegister = 0,
                OffsetInDescriptorsFromTableStart = (uint)i
            };
        }

        parameters[2] = new D3D12_ROOT_PARAMETER1
        {
            ParameterType = 0,
            ShaderVisibility = 0
        };

        parameters[2].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 2,
            pDescriptorRanges = ranges
        };

        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 3,
            pParameters = parameters
        };

        nint blob = 0, errors = 0;

        try
        {
            var hr = SerializeR10RootNative(&description, &blob, &errors);

            Console.Error.WriteLine($"[graphics] R17 root serialization HRESULT=0x{hr:X8}.");

            if (errors != 0)
            {
                Console.Error.WriteLine(Encoding.UTF8.GetString(ReadR10Blob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            return hr >= 0 && blob != 0 ? ReadR10Blob(blob, 1024 * 1024) : null;
        }
        finally
        {
            if (errors != 0)
            {
                _ = HostRelease(errors);
            }

            if (blob != 0)
            {
                _ = HostRelease(blob);
            }
        }
    }

    private static bool TryCreateR17Pipeline(ComputePipelineState metadata, RecordedCommandState state,
        out nint root, out nint pipeline)
    {
        root = 0;

        pipeline = 0;

        if (R21ComputePipelines.Acquire(metadata, state) is { } shared)
        {
            root = shared.Root;

            pipeline = shared.Pipeline;

            return true;
        }

        byte[]? code;

        lock (R17PipelineGate)
        {
            if (_r17Bytecode == null)
            {
                try
                {
                    var hlsl = Rdna2ColorLutCompiler.Translate(metadata.Shader.Bytes);

                    Console.Error.WriteLine(
                        "[graphics] IRX-GFX-SHADER-R17: recognized captured 3D color transform; compiling cs_5_1.");

                    _r17Bytecode = CompileR10Shader(hlsl, "cs_5_1", Console.Error);
                }
                catch (Exception exception)
                {
                    return RefuseR17($"compute translation failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            code = _r17Bytecode;
        }

        if (code == null || SerializeR17Root() is not { } rootBytes)
        {
            return false;
        }

        void* nativeRoot = null, nativePipeline = null;

        try
        {
            var rootId = RootSignatureId;

            fixed (byte* bytes = rootBytes)
            {
                var hr = (int)_host->CreateRootSignature(0, bytes, (nuint)rootBytes.Length, &rootId, &nativeRoot);

                if (hr < 0 || nativeRoot == null)
                {
                    return RefuseR17($"CreateRootSignature HRESULT=0x{hr:X8}");
                }
            }

            var pipelineId = PipelineId;

            fixed (byte* bytes = code)
            {
                var desc = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)nativeRoot,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = bytes, BytecodeLength = (nuint)code.Length }
                };
                var hr = (int)_host->CreateComputePipelineState(&desc, &pipelineId, &nativePipeline);

                if (hr < 0 || nativePipeline == null)
                {
                    return RefuseR17($"CreateComputePipelineState HRESULT=0x{hr:X8}");
                }
            }

            root = (nint)nativeRoot;

            pipeline = (nint)nativePipeline;

            state.HostViews.Add(root);

            nativeRoot = null;

            state.HostViews.Add(pipeline);

            nativePipeline = null;

            R21ComputePipelines.Publish(metadata, new R21ComputePipeline(root, pipeline));

            return true;
        }
        finally
        {
            if (nativePipeline != null)
            {
                _ = HostRelease((nint)nativePipeline);
            }

            if (nativeRoot != null)
            {
                _ = HostRelease((nint)nativeRoot);
            }
        }
    }

    private static bool TryRecordR17Compute(nint self, uint x, uint y, uint z, uint flags = 0)
    {
        using var r25Timing = BeginR25Timing(R25Stage.RecordCompute);

        var state = StateFor(self);

        ComputePipelineState? metadata;

        lock (PipelineGate)
        {
            _ = ComputePipelines.TryGetValue(state.Pipeline, out metadata);
        }

        if (Interlocked.Exchange(ref _r174ComputeReported, 1) == 0)
        {
            Console.Error.WriteLine(
                "[graphics] IRX-GFX-COMPUTE-R17.4: ordinary and placed SRV compute execution path active.");
        }

        if (metadata == null)
        {
            return RefuseR17($"no compute metadata for bound pipeline 0x{state.Pipeline:X}");
        }

        if (metadata.Hash != Rdna2ColorLutCompiler.ContainerHash)
        {
            return RefuseR17($"unsupported compute SHA256={metadata.Hash}; groups={x}x{y}x{z}; flags=0x{flags:X}");
        }

        if (_host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 || x != 5 || y != 5 || z != 33 || flags != 0
            ||
            state.Compute.Signature != metadata.Root || !IsRootSignature(metadata.Root)
            || RootSignatureBlob(metadata.Root).Length != 7600 ||
            state.Compute.Tables.Count != 2 || !state.Compute.Tables.ContainsKey(0) ||
            !state.Compute.Tables.TryGetValue(1, out var table) || table == 0 || table > (ulong)nint.MaxValue - 112 ||
            state.Compute.Constants.Count != 0 || state.Compute.Views.Count != 0
            || state.MemoryBoundaries.Count >= MaximumMemoryOperations ||
            state.HostViews.Count > R13MaximumHostViews - 2)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-COMPUTE-R17.4: groups={x}x{y}x{z}; "
                + $"flags=0x{flags:X}; listType={*(uint*)(self + 0x10)}; "
                + $"rootMatches={state.Compute.Signature == metadata.Root}; "
                + $"tables={state.Compute.Tables.Count}; "
                + $"constants={state.Compute.Constants.Count}; "
                + $"views={state.Compute.Views.Count}; "
                + $"boundaries={state.MemoryBoundaries.Count}; "
                + $"hostViews={state.HostViews.Count}.");

            return RefuseR17("dispatch or root contract differs from the translated program");
        }

        // Use the shared resolver for both ordinary and placed SRVs. A placed
        // view is normalized to the same owned resource/view representation.
        if (!TryR16Source(state, table, out var input) || input == null)
        {
            WriteR16TableEvidence(Console.Error, table);

            return RefuseR17("slot 1 input SRV could not be resolved (ordinary or placed)");
        }

        if (!TryR16TextureView(input, out var inputFormat))
        {
            WriteR16TableEvidence(Console.Error, table);

            return RefuseR17($"slot 1 input SRV kind={input.Kind} is not a supported Texture2D view");
        }

        if (!TryR17Output(table + 32, out var output) || output == null)
        {
            return RefuseR17("slot 1 offset 32 is not a supported full Texture3D UAV");
        }

        if (!TryR17Constants(table + 64, R17SmallBytes, out _) ||
            !TryR17Constants(table + 96, R17LargeBytes, out _))
        {
            return RefuseR17("slot 1 constant-buffer tokens are missing or undersized");
        }

        TraceR24(
            $"[graphics] IRX-GFX-COMPUTE-R17.4: resolved {(input.Kind == 70 ? "placed" : "ordinary")} SRV; kind={input.Kind}, descriptor={input.Token}.");

        var inputResource = (ResourceObject*)input.Resource;
        var outputResource = (ResourceObject*)output.Resource;
        var inputDesc = inputResource->Description;
        var outputDesc = outputResource->Description;

        TraceR24(
            $"[graphics] R17 SRV: size={inputDesc.Width}x{inputDesc.Height}, format={inputFormat}, layout=0x{inputDesc.Layout:X}, flags=0x{inputDesc.Flags:X}.");

        TraceR24(
            $"[graphics] R17 UAV: size={outputDesc.Width}x{outputDesc.Height}x{outputDesc.DepthOrArraySize}, format={outputDesc.Format}, layout=0x{outputDesc.Layout:X}.");

        if (input.Resource == output.Resource ||
            !ResourceFootprint(inputDesc, out var inputExtent) || !ResourceFootprint(outputDesc, out var outputExtent)
            ||
            inputResource->Address == 0 || outputResource->Address == 0 ||
            inputExtent.Size > ulong.MaxValue - inputResource->Address
            || outputExtent.Size > ulong.MaxValue - outputResource->Address ||
            (inputResource->Address < outputResource->Address + outputExtent.Size &&
             outputResource->Address < inputResource->Address + inputExtent.Size))
        {
            return RefuseR17("invalid or overlapping source/output texture extent");
        }

        state.RetainObject(input.Resource);

        state.RetainObject(output.Resource);

        if (MaterializeHostResource(input.Resource, out var inputHost) < 0 || inputHost == 0 ||
            MaterializeHostResource(output.Resource, out var outputHost) < 0 || outputHost == 0
            || inputHost == outputHost ||
            !TryCreateR17Pipeline(metadata, state, out var root, out var pipeline))
        {
            return RefuseR17("native resources or compute pipeline unavailable");
        }

        // No guest reads occur until the earlier host segment and its memory operations complete.
        SealMemoryBoundary(self, state, null, null, compute: new R17ComputeBoundary(input, output, table,
            inputHost, outputHost, root, pipeline));

        TraceR24("[graphics] IRX-GFX-COMPUTE-R17: RECORDED 5x5x33 color transform dispatch; awaiting submission.");

        return true;
    }

    private static void ExecuteR17Compute(nint queue, R17ComputeBoundary boundary)
    {
        using var r25Timing = BeginR25Timing(R25Stage.ExecuteCompute);

        if (!R16SourceUnchanged(boundary.Input) || !R16SourceUnchanged(boundary.Output) ||
            !TryR17Constants(boundary.Table + 64, R17SmallBytes, out var small) ||
            !TryR17Constants(boundary.Table + 96, R17LargeBytes, out var large))
        {
            throw new InvalidOperationException("R17 descriptors changed before submission.");
        }

        var snapshot = new byte[R17UploadBytes];

        if (!TryReadR12Memory(small.Address, snapshot.AsSpan(0, R17SmallBytes)) ||
            !TryReadR12Memory(large.Address, snapshot.AsSpan(R17LargeOffset, R17LargeBytes)) ||
            !TryR17Constants(boundary.Table + 64, R17SmallBytes, out var smallCheck) ||
            !TryR17Constants(boundary.Table + 96, R17LargeBytes, out var largeCheck) ||
            !R17SameConstant(small, smallCheck) || !R17SameConstant(large, largeCheck))
        {
            throw new InvalidOperationException("R17 constant-buffer snapshot unavailable or descriptor changed.");
        }

        // The native scalar branch only reads the texture when this word is nonzero.
        var readsTexture = R16Word(snapshot, 0) != 0;
        var writer = R16Writer(boundary.Input.Resource);

        if (readsTexture && ((writer == 0 && !UploadR16Texture(queue, boundary.Input.Resource, boundary.InputHost)) ||
                             (writer != 0 && writer != queue)))
        {
            throw new InvalidOperationException("R17 input texture coherence unavailable.");
        }

        var outputWriter = R16Writer(boundary.Output.Resource);

        if (outputWriter != 0 && outputWriter != queue)
        {
            throw new InvalidOperationException("R17 output was GPU-written on another queue.");
        }

        nint upload = 0, heap = 0, allocator = 0, list = 0;

        try
        {
            CheckHostResult(CreateHostResource(HostBufferDescription(R17UploadBytes), 2, GenericRead, &upload),
                "R17 CBV upload");

            var noRead = default(HostRange);

            void* mapped = null;

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, 8))
                (upload, 0, &noRead, &mapped), "R17 CBV map");

            if (mapped == null)
            {
                throw new InvalidOperationException("R17 CBV map returned null.");
            }

            var written = new HostRange { End = R17UploadBytes };

            try
            {
                snapshot.AsSpan().CopyTo(new Span<byte>(mapped, R17UploadBytes));
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9))(upload, 0, &written);
            }

            var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 2, Flags = 1 };
            var heapId = DescriptorHeapId;
            var device = (nint)_host;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14))
                (device, &heapDescription, &heapId, &heap), "R17 shader-visible heap");

            if (heap == 0)
            {
                throw new InvalidOperationException("R17 descriptor heap returned null.");
            }

            var nativeHeap = (ID3D12DescriptorHeap*)heap;
            var cpu = nativeHeap->GetCPUDescriptorHandleForHeapStart();
            var gpu = nativeHeap->GetGPUDescriptorHandleForHeapStart();
            var increment = _host->GetDescriptorHandleIncrementSize(0);
            var cbv = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0 || cbv == 0 || (cbv & 255UL) != 0)
            {
                throw new InvalidOperationException("R17 invalid host binding address.");
            }

            fixed (byte* inputView = boundary.Input.Description)
            fixed (byte* outputView = boundary.Output.Description)
            {
                _host->CreateShaderResourceView((ID3D12Resource*)boundary.InputHost,
                    (D3D12_SHADER_RESOURCE_VIEW_DESC*)inputView, cpu);

                cpu.ptr += increment;

                _host->CreateUnorderedAccessView((ID3D12Resource*)boundary.OutputHost, null,
                    (D3D12_UNORDERED_ACCESS_VIEW_DESC*)outputView, cpu);
            }

            CreateR16UtilityList(out allocator, out list);

            TransitionHostResource(list, boundary.InputHost, 0, 0x40);

            TransitionHostResource(list, boundary.OutputHost, 0, 8);

            var commands = (ID3D12GraphicsCommandList*)list;

            commands->SetDescriptorHeaps(1, &nativeHeap);
            commands->SetPipelineState((ID3D12PipelineState*)boundary.Pipeline);
            commands->SetComputeRootSignature((ID3D12RootSignature*)boundary.Root);
            commands->SetComputeRootConstantBufferView(0, cbv);
            commands->SetComputeRootConstantBufferView(1, cbv + R17LargeOffset);
            commands->SetComputeRootDescriptorTable(2, gpu);

            TraceR24($"[graphics] IRX-GFX-COMPUTE-R17: BOUND 2 CBVs, SRV, UAV; readsTexture={readsTexture}.");

            commands->Dispatch(5, 5, 33);

            TransitionHostResource(list, boundary.OutputHost, 8, 0);
            TransitionHostResource(list, boundary.InputHost, 0x40, 0);

            SubmitR16UtilityList(queue, list);

            MarkR16GpuWrite(boundary.Output.Resource, queue);

            Interlocked.Increment(ref _r24Dispatches);

            TraceR24(
                "[graphics] IRX-GFX-COMPUTE-R17: GPU COMPLETED 3D color transform (UAV content not numerically verified).");
        }
        finally
        {
            ReleaseR16Utility(list, allocator, upload);

            if (heap != 0)
            {
                _ = HostRelease(heap);
            }
        }
    }

    private sealed record R17ComputeBoundary(
        R16Source Input,
        R16Source Output,
        ulong Table,
        nint InputHost,
        nint OutputHost,
        nint Root,
        nint Pipeline);
}
