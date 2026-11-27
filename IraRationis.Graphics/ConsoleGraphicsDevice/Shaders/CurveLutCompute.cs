// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Compute;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int CurveUploadBytes = 512;
    private const int CurveDataOffset = 256;

    private static bool RejectCurveLut(string reason)
    {
        Console.Error.WriteLine($"[graphics] curve-lut REFUSED: {reason}.");

        return false;
    }

    private static bool TryGetCurveOutput(ulong handle, out TextureSource? source, out uint format)
    {
        source = null;
        format = 0;

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

        format = desc.Format;

        if (resource->Generation != record.ResourceGeneration || desc.Dimension != 2 || desc.Height != 1 ||
            desc.DepthOrArraySize != 1 || desc.MipLevels != 1 || desc.SampleCount != 1 || desc.Width is 0 or > 16384 ||
            (desc.Flags & 4U) == 0 || !CurveLutCompiler.SupportsFormat(format) ||
            (record.Description is { } bytes && (bytes.Length != 40 || ReadDescriptorUInt32(bytes, 0) != format ||
                                                 ReadDescriptorUInt32(bytes, 4) != 2
                                                 || ReadDescriptorUInt32(bytes, 8) != 0)))
        {
            Console.Error.WriteLine(
                $"[graphics] curve-lut UAV: dimension={desc.Dimension}; width={desc.Width}; format={format}; flags=0x{desc.Flags:X}.");

            return false;
        }

        var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT { Format = (DXGI_FORMAT)format };

        var result = (int)_host->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT, &support,
            (uint)sizeof(D3D12_FEATURE_DATA_FORMAT_SUPPORT));

        if (result < 0 || (support.Support2 & D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE) == 0)
        {
            return false;
        }

        source = new TextureSource(handle, token.Id, token.Kind, record.Resource, resource->Generation,
            record.Description);

        return true;
    }

    private static bool TryGetCurveBuffer(TextureSource source, int curveBytes, out ulong address)
    {
        address = 0;

        if (curveBytes is not (CurveLutCompiler.CurveBytes or CurveLutCompiler.MaximumCurveBytes) ||
            source.Description is not { Length: 40 } bytes || ReadDescriptorUInt32(bytes, 0) != 0 ||
            ReadDescriptorUInt32(bytes, 28) != CurveLutCompiler.SegmentBytes || ReadDescriptorUInt32(bytes, 32) != 0 ||
            (ulong)ReadDescriptorUInt32(bytes, 24) * CurveLutCompiler.SegmentBytes < (ulong)curveBytes ||
            !TryGetTextureBuffer(source, out address))
        {
            return false;
        }

        var resource = (ResourceObject*)source.Resource;
        var offset = address - resource->Address;

        return offset <= resource->Description.Width && (ulong)curveBytes <= resource->Description.Width - offset &&
               (ulong)curveBytes <= ulong.MaxValue - address;
    }

    private static bool TryRecordCurveLutCompute(nint self, RecordedCommandState state, ComputePipelineState metadata,
        uint x, uint y, uint z, uint flags)
    {
        var segments = CurveLutCompiler.SegmentCount(metadata.Hash);
        var curveBytes = segments * CurveLutCompiler.SegmentBytes;

        if (segments == 0 || _host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 || x is 0 or > 128 || y != 1
            || z != 1 || flags != 0 ||
            state.Compute.Signature != metadata.Root || !IsRootSignature(metadata.Root) ||
            RootSignatureBlob(metadata.Root).Length != 7600 || state.Compute.Tables.Count != 2 ||
            !state.Compute.Tables.ContainsKey(0) || !state.Compute.Tables.TryGetValue(1, out var table) ||
            table == 0 || table > (ulong)nint.MaxValue - 80 || state.Compute.Constants.Count != 0
            || state.Compute.Views.Count != 0 ||
            state.MemoryBoundaries.Count >= MaximumMemoryOperations
            || state.HostViews.Count > MaximumCommandHostViews - 2)
        {
            return RejectCurveLut("unsupported dispatch or root binding contract");
        }

        if (!TryGetTextureSource(state, table, out var input) || input == null
                                                              || !TryGetCurveBuffer(input, curveBytes, out var address)
                                                              ||
                                                              !TryGetCurveOutput(table + 32, out var output,
                                                                  out var format) || output == null ||
                                                              !TryReadConstantDescriptor(table + 64,
                                                                  CurveLutCompiler.ParameterBytes, out _))
        {
            WriteTextureTableEvidence(Console.Error, table);

            return RejectCurveLut("requires 32-byte structured SRV, typed Texture1D UAV and 16-byte CBV");
        }

        var resource = (ResourceObject*)output.Resource;

        if (!ResourceFootprint(resource->Description, out var extent) || resource->Address == 0 ||
            extent.Size > ulong.MaxValue - resource->Address || address > ulong.MaxValue - (ulong)curveBytes ||
            (address < resource->Address + extent.Size && resource->Address < address + (ulong)curveBytes))
        {
            return RejectCurveLut("invalid or overlapping source/output extent");
        }

        state.RetainObject(input.Resource);
        state.RetainObject(output.Resource);

        if (MaterializeHostResource(output.Resource, out var outputHost) < 0 || outputHost == 0 ||
            !TryCreateColorLutPipeline(metadata, state, out var root, out var pipeline, format))
        {
            return RejectCurveLut("native UAV or compute PSO unavailable");
        }

        SealMemoryBoundary(self, state, null, null, compute: new ColorLutComputeBoundary(input, output, table,
            0, outputHost, root, pipeline, format, x, curveBytes));

        TraceGraphicsSuccess(
            $"[graphics] curve-lut RECORDED: segments={segments}; groups={x}x1x1; width={resource->Description.Width}; format={format}.");

        return true;
    }

    private static void ExecuteCurveLutCompute(nint queue, ColorLutComputeBoundary boundary)
    {
        if (!IsTextureSourceCurrent(boundary.Input) || !IsTextureSourceCurrent(boundary.Output) ||
            !TryGetCurveBuffer(boundary.Input, boundary.CurveBytes, out var address)
            || GetResourceWriterQueue(boundary.Input.Resource) != 0 ||
            !TryReadConstantDescriptor(boundary.Table + 64, CurveLutCompiler.ParameterBytes, out var constants))
        {
            throw new InvalidOperationException("Curve LUT source or descriptor changed before submission.");
        }

        var data = new byte[CurveUploadBytes];

        if (!TryReadGuestMemory(constants.Address, data.AsSpan(0, CurveLutCompiler.ParameterBytes)) ||
            !TryReadGuestMemory(address, data.AsSpan(CurveDataOffset, boundary.CurveBytes)) ||
            !IsTextureSourceCurrent(boundary.Input) || !IsTextureSourceCurrent(boundary.Output) ||
            !TryReadConstantDescriptor(boundary.Table + 64, CurveLutCompiler.ParameterBytes, out var check) ||
            !MatchesConstantDescriptor(constants, check))
        {
            throw new InvalidOperationException("Curve LUT source snapshot unavailable or descriptor changed.");
        }

        var count = ReadDescriptorUInt32(data, 4);
        var output = (ResourceObject*)boundary.Output.Resource;
        var writer = GetResourceWriterQueue(boundary.Output.Resource);

        if ((writer != 0 && writer != queue) || count != output->Description.Width
                                             || count > boundary.GroupsX * CurveLutCompiler.ThreadsPerGroup)
        {
            throw new InvalidOperationException(
                $"Curve LUT output coverage/coherence unsupported: count={count}; width={output->Description.Width}; groups={boundary.GroupsX}.");
        }

        if (!float.IsFinite(BitConverter.UInt32BitsToSingle(ReadDescriptorUInt32(data, 0))) ||
            !float.IsFinite(BitConverter.UInt32BitsToSingle(ReadDescriptorUInt32(data, 8))) ||
            ReadDescriptorUInt32(data, 8) is 0 or 0x80000000 ||
            !float.IsFinite(BitConverter.UInt32BitsToSingle(ReadDescriptorUInt32(data, 12))))
        {
            throw new InvalidOperationException("Curve LUT parameters are non-finite or divisor is zero.");
        }

        for (var offset = CurveDataOffset; offset < CurveDataOffset + boundary.CurveBytes; offset += 4)
        {
            if (!float.IsFinite(BitConverter.UInt32BitsToSingle(ReadDescriptorUInt32(data, offset))))
            {
                throw new InvalidOperationException("Curve LUT coefficient is non-finite.");
            }
        }

        using var utility = AcquireUtilityContext(CurveUploadBytes, 2, true);
        utility.PrepareVersionedUpload(data);
        RecordCurveLutDispatch(utility, boundary.OutputHost, boundary.Root, boundary.Pipeline, boundary.CurveFormat,
            boundary.GroupsX);
        utility.SubmitAndWait(queue);
        ReportUtilityExecution();
        MarkGpuWrite(boundary.Output.Resource, queue);
        Interlocked.Increment(ref _dispatches);

        TraceGraphicsSuccess(
            $"[graphics] curve-lut GPU COMPLETED: segments={boundary.CurveBytes / CurveLutCompiler.SegmentBytes}; texels={count}; format={boundary.CurveFormat}; numerical equivalence unverified.");
    }

    private static void RecordCurveLutDispatch(UtilityContext utility, nint outputHost, nint root, nint pipeline,
        uint format, uint groupsX)
    {
        var heap = (ID3D12DescriptorHeap*)utility.Heap;
        var cpu = heap->GetCPUDescriptorHandleForHeapStart();
        var gpu = heap->GetGPUDescriptorHandleForHeapStart();
        var increment = _host->GetDescriptorHandleIncrementSize(0);
        var cbv = ((ID3D12Resource*)utility.Upload)->GetGPUVirtualAddress();

        if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0 || cbv == 0 || (cbv & 255UL) != 0)
        {
            throw new InvalidOperationException("Curve LUT invalid native binding address.");
        }

        // Shared root contains an unused t0. Initialize its slot rather than retaining stale descriptors.
        var unused = new D3D12_SHADER_RESOURCE_VIEW_DESC
        {
            Format = (DXGI_FORMAT)28,
            ViewDimension = (D3D12_SRV_DIMENSION)4,
            Shader4ComponentMapping = 5768
        };
        unused.Anonymous.Texture2D.MipLevels = 1;

        _host->CreateShaderResourceView(null, &unused, cpu);

        cpu.ptr += increment;

        var view = new D3D12_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = (DXGI_FORMAT)format,
            ViewDimension = (D3D12_UAV_DIMENSION)2
        };

        _host->CreateUnorderedAccessView((ID3D12Resource*)outputHost, null, &view, cpu);

        var commands = (ID3D12GraphicsCommandList*)utility.List;

        TransitionHostResource(utility.List, outputHost, 0, 8);

        commands->SetDescriptorHeaps(1, &heap);
        commands->SetPipelineState((ID3D12PipelineState*)pipeline);
        commands->SetComputeRootSignature((ID3D12RootSignature*)root);
        commands->SetComputeRootConstantBufferView(0, cbv);
        commands->SetComputeRootConstantBufferView(1, cbv + CurveDataOffset);
        commands->SetComputeRootDescriptorTable(2, gpu);
        commands->Dispatch(groupsX, 1, 1);

        TransitionHostResource(utility.List, outputHost, 8, 0);
    }
}
