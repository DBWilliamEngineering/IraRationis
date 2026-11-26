// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.ColorLut;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int ColorLutSmallBytes = 128;
    private const int ColorLutLargeBytes = 2704;
    private const int ColorLutLargeOffset = 256;
    private const int ColorLutUploadBytes = 3072;
    private static readonly Lock ColorLutPipelineGate = new();
    private static byte[]? _colorLutBytecode;
    private static int _colorLutExecutionReported;

    private static bool RejectColorLutDispatch(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-COMPUTE-R17: REFUSED: {reason}.");

        return false;
    }

    private static bool TryGetColorLutOutput(ulong handle, out TextureSource? source)
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

        if (record.Description is { } bytes && (bytes.Length != 40 || ReadDescriptorUInt32(bytes, 0) != desc.Format ||
                                                ReadDescriptorUInt32(bytes, 4) != 8 || ReadDescriptorUInt32(bytes, 8) != 0
                                                || ReadDescriptorUInt32(bytes, 12) != 0 ||
                                                (ReadDescriptorUInt32(bytes, 16) != desc.DepthOrArraySize
                                                 && ReadDescriptorUInt32(bytes, 16) != uint.MaxValue)))
        {
            Console.Error.WriteLine("[graphics] R17 UAV view is not the full mip-zero Texture3D.");

            return false;
        }

        source = new TextureSource(handle, token.Id, token.Kind, record.Resource, resource->Generation, record.Description);

        return true;
    }

    private static bool TryGetColorLutConstants(ulong address, int minimum, out ConstantDescriptor descriptor)
    {
        return TryReadMappingRecord(address, out descriptor) && descriptor.Tag == ConstantTag &&
               descriptor.Size >= minimum && descriptor.Address != 0 &&
               descriptor.Address <= (ulong)nint.MaxValue - (uint)minimum;
    }

    private static bool IsSameColorLutConstant(ConstantDescriptor leftDescriptor, ConstantDescriptor rightDescriptor)
    {
        return leftDescriptor.Tag == rightDescriptor.Tag && leftDescriptor.Size == rightDescriptor.Size && leftDescriptor.Address == rightDescriptor.Address;
    }

    private static byte[]? SerializeColorLutRoot()
    {
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[3];
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE1[2];

        for (var index = 0; index < 2; index++)
        {
            parameters[index] = new D3D12_ROOT_PARAMETER1
            {
                ParameterType = (D3D12_ROOT_PARAMETER_TYPE)2,
                ShaderVisibility = 0
            };

            parameters[index].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1 { ShaderRegister = (uint)index };

            ranges[index] = new D3D12_DESCRIPTOR_RANGE1
            {
                RangeType = (D3D12_DESCRIPTOR_RANGE_TYPE)index,
                NumDescriptors = 1,
                BaseShaderRegister = 0,
                OffsetInDescriptorsFromTableStart = (uint)index
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
            var resultCode = SerializeRootNative(&description, &blob, &errors);

            Console.Error.WriteLine($"[graphics] R17 root serialization HRESULT=0x{resultCode:X8}.");

            if (errors != 0)
            {
                Console.Error.WriteLine(Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            return resultCode >= 0 && blob != 0 ? ReadShaderBlob(blob, 1024 * 1024) : null;
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

    private static bool TryCreateColorLutPipeline(ComputePipelineState metadata, RecordedCommandState state,
        out nint root, out nint pipeline)
    {
        root = 0;

        pipeline = 0;

        if (SharedColorLutPipelines.Acquire(metadata, state) is { } shared)
        {
            root = shared.Root;

            pipeline = shared.Pipeline;

            return true;
        }

        byte[]? code;

        lock (ColorLutPipelineGate)
        {
            if (_colorLutBytecode == null)
            {
                try
                {
                    var hlsl = Rdna2ColorLutCompiler.Translate(metadata.Shader.Bytes);

                    Console.Error.WriteLine(
                        "[graphics] IRX-GFX-SHADER-R17: recognized captured 3D color transform; compiling cs_5_1.");

                    _colorLutBytecode = CompileHostShader(hlsl, "cs_5_1", Console.Error);
                }
                catch (Exception exception)
                {
                    return RejectColorLutDispatch($"compute translation failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            code = _colorLutBytecode;
        }

        if (code == null || SerializeColorLutRoot() is not { } rootBytes)
        {
            return false;
        }

        void* nativeRoot = null, nativePipeline = null;

        try
        {
            var rootId = RootSignatureId;

            fixed (byte* bytes = rootBytes)
            {
                var resultCode = (int)_host->CreateRootSignature(0, bytes, (nuint)rootBytes.Length, &rootId, &nativeRoot);

                if (resultCode < 0 || nativeRoot == null)
                {
                    return RejectColorLutDispatch($"CreateRootSignature HRESULT=0x{resultCode:X8}");
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
                var resultCode = (int)_host->CreateComputePipelineState(&desc, &pipelineId, &nativePipeline);

                if (resultCode < 0 || nativePipeline == null)
                {
                    return RejectColorLutDispatch($"CreateComputePipelineState HRESULT=0x{resultCode:X8}");
                }
            }

            root = (nint)nativeRoot;

            pipeline = (nint)nativePipeline;

            state.HostViews.Add(root);

            nativeRoot = null;

            state.HostViews.Add(pipeline);

            nativePipeline = null;

            SharedColorLutPipelines.Publish(metadata, new ColorLutPipeline(root, pipeline));

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

    private static bool TryRecordColorLutCompute(nint self, uint coordinateX, uint coordinateY, uint coordinateZ, uint flags = 0)
    {
        using var stageTiming = BeginTiming(GraphicsStage.RecordCompute);

        var state = StateFor(self);

        ComputePipelineState? metadata;

        lock (PipelineGate)
        {
            _ = ComputePipelines.TryGetValue(state.Pipeline, out metadata);
        }

        if (Interlocked.Exchange(ref _colorLutExecutionReported, 1) == 0)
        {
            Console.Error.WriteLine(
                "[graphics] IRX-GFX-COMPUTE-R17.4: ordinary and placed SRV compute execution path active.");
        }

        if (metadata == null)
        {
            return RejectColorLutDispatch($"no compute metadata for bound pipeline 0x{state.Pipeline:X}");
        }

        if (metadata.Hash != Rdna2ColorLutCompiler.ContainerHash)
        {
            return RejectColorLutDispatch($"unsupported compute SHA256={metadata.Hash}; groups={coordinateX}x{coordinateY}x{coordinateZ}; flags=0x{flags:X}");
        }

        if (_host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 || coordinateX != 5 || coordinateY != 5 || coordinateZ != 33 || flags != 0
            ||
            state.Compute.Signature != metadata.Root || !IsRootSignature(metadata.Root)
            || RootSignatureBlob(metadata.Root).Length != 7600 ||
            state.Compute.Tables.Count != 2 || !state.Compute.Tables.ContainsKey(0) ||
            !state.Compute.Tables.TryGetValue(1, out var table) || table == 0 || table > (ulong)nint.MaxValue - 112 ||
            state.Compute.Constants.Count != 0 || state.Compute.Views.Count != 0
            || state.MemoryBoundaries.Count >= MaximumMemoryOperations ||
            state.HostViews.Count > MaximumCommandHostViews - 2)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-COMPUTE-R17.4: groups={coordinateX}x{coordinateY}x{coordinateZ}; "
                + $"flags=0x{flags:X}; listType={*(uint*)(self + 0x10)}; "
                + $"rootMatches={state.Compute.Signature == metadata.Root}; "
                + $"tables={state.Compute.Tables.Count}; "
                + $"constants={state.Compute.Constants.Count}; "
                + $"views={state.Compute.Views.Count}; "
                + $"boundaries={state.MemoryBoundaries.Count}; "
                + $"hostViews={state.HostViews.Count}.");

            return RejectColorLutDispatch("dispatch or root contract differs from the translated program");
        }

        // Use the shared resolver for both ordinary and placed SRVs. A placed
        // view is normalized to the same owned resource/view representation.
        if (!TryGetTextureSource(state, table, out var input) || input == null)
        {
            WriteTextureTableEvidence(Console.Error, table);

            return RejectColorLutDispatch("slot 1 input SRV could not be resolved (ordinary or placed)");
        }

        if (!TryGetTextureView(input, out var inputFormat))
        {
            WriteTextureTableEvidence(Console.Error, table);

            return RejectColorLutDispatch($"slot 1 input SRV kind={input.Kind} is not a supported Texture2D view");
        }

        if (!TryGetColorLutOutput(table + 32, out var output) || output == null)
        {
            return RejectColorLutDispatch("slot 1 offset 32 is not a supported full Texture3D UAV");
        }

        if (!TryGetColorLutConstants(table + 64, ColorLutSmallBytes, out _) ||
            !TryGetColorLutConstants(table + 96, ColorLutLargeBytes, out _))
        {
            return RejectColorLutDispatch("slot 1 constant-buffer tokens are missing or undersized");
        }

        TraceGraphicsSuccess(
            $"[graphics] IRX-GFX-COMPUTE-R17.4: resolved {(input.Kind == 70 ? "placed" : "ordinary")} SRV; kind={input.Kind}, descriptor={input.Token}.");

        var inputResource = (ResourceObject*)input.Resource;
        var outputResource = (ResourceObject*)output.Resource;
        var inputDesc = inputResource->Description;
        var outputDesc = outputResource->Description;

        TraceGraphicsSuccess(
            $"[graphics] R17 SRV: size={inputDesc.Width}x{inputDesc.Height}, format={inputFormat}, layout=0x{inputDesc.Layout:X}, flags=0x{inputDesc.Flags:X}.");

        TraceGraphicsSuccess(
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
            return RejectColorLutDispatch("invalid or overlapping source/output texture extent");
        }

        state.RetainObject(input.Resource);

        state.RetainObject(output.Resource);

        if (MaterializeHostResource(input.Resource, out var inputHost) < 0 || inputHost == 0 ||
            MaterializeHostResource(output.Resource, out var outputHost) < 0 || outputHost == 0
            || inputHost == outputHost ||
            !TryCreateColorLutPipeline(metadata, state, out var root, out var pipeline))
        {
            return RejectColorLutDispatch("native resources or compute pipeline unavailable");
        }

        // No guest reads occur until the earlier host segment and its memory operations complete.
        SealMemoryBoundary(self, state, null, null, compute: new ColorLutComputeBoundary(input, output, table,
            inputHost, outputHost, root, pipeline));

        TraceGraphicsSuccess("[graphics] IRX-GFX-COMPUTE-R17: RECORDED 5x5x33 color transform dispatch; awaiting submission.");

        return true;
    }

    private static void ExecuteColorLutCompute(nint queue, ColorLutComputeBoundary boundary)
    {
        using var stageTiming = BeginTiming(GraphicsStage.ExecuteCompute);

        if (!IsTextureSourceCurrent(boundary.Input) || !IsTextureSourceCurrent(boundary.Output) ||
            !TryGetColorLutConstants(boundary.Table + 64, ColorLutSmallBytes, out var small) ||
            !TryGetColorLutConstants(boundary.Table + 96, ColorLutLargeBytes, out var large))
        {
            throw new InvalidOperationException("R17 descriptors changed before submission.");
        }

        var snapshot = new byte[ColorLutUploadBytes];

        if (!TryReadGuestMemory(small.Address, snapshot.AsSpan(0, ColorLutSmallBytes)) ||
            !TryReadGuestMemory(large.Address, snapshot.AsSpan(ColorLutLargeOffset, ColorLutLargeBytes)) ||
            !TryGetColorLutConstants(boundary.Table + 64, ColorLutSmallBytes, out var smallCheck) ||
            !TryGetColorLutConstants(boundary.Table + 96, ColorLutLargeBytes, out var largeCheck) ||
            !IsSameColorLutConstant(small, smallCheck) || !IsSameColorLutConstant(large, largeCheck))
        {
            throw new InvalidOperationException("R17 constant-buffer snapshot unavailable or descriptor changed.");
        }

        // The native scalar branch only reads the texture when this word is nonzero.
        var readsTexture = ReadDescriptorUInt32(snapshot, 0) != 0;
        var writer = GetResourceWriterQueue(boundary.Input.Resource);

        if (readsTexture && ((writer == 0 && !UploadTextureTexture(queue, boundary.Input.Resource, boundary.InputHost)) ||
                             (writer != 0 && writer != queue)))
        {
            throw new InvalidOperationException("R17 input texture coherence unavailable.");
        }

        var outputWriter = GetResourceWriterQueue(boundary.Output.Resource);

        if (outputWriter != 0 && outputWriter != queue)
        {
            throw new InvalidOperationException("R17 output was GPU-written on another queue.");
        }

        using var utility = AcquireUtilityContext(ColorLutUploadBytes, 2, versioned: true);

        utility.PrepareVersionedUpload(snapshot);

        var upload = utility.Upload;
        var heap = utility.Heap;
        var list = utility.List;

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

        TransitionHostResource(list, boundary.InputHost, 0, 0x40);

        TransitionHostResource(list, boundary.OutputHost, 0, 8);

        var commands = (ID3D12GraphicsCommandList*)list;

        commands->SetDescriptorHeaps(1, &nativeHeap);
        commands->SetPipelineState((ID3D12PipelineState*)boundary.Pipeline);
        commands->SetComputeRootSignature((ID3D12RootSignature*)boundary.Root);
        commands->SetComputeRootConstantBufferView(0, cbv);
        commands->SetComputeRootConstantBufferView(1, cbv + ColorLutLargeOffset);
        commands->SetComputeRootDescriptorTable(2, gpu);

        TraceGraphicsSuccess($"[graphics] IRX-GFX-COMPUTE-R17: BOUND 2 CBVs, SRV, UAV; readsTexture={readsTexture}.");

        commands->Dispatch(5, 5, 33);

        TransitionHostResource(list, boundary.OutputHost, 8, 0);
        TransitionHostResource(list, boundary.InputHost, 0x40, 0);

        utility.SubmitAndWait(queue);

        ReportUtilityExecution();

        MarkGpuWrite(boundary.Output.Resource, queue);

        Interlocked.Increment(ref _dispatches);

        TraceGraphicsSuccess(
            "[graphics] IRX-GFX-COMPUTE-R17: GPU COMPLETED 3D color transform (UAV content not numerically verified).");
    }
}
