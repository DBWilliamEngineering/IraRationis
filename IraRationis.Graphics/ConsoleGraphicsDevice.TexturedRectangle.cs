// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Graphics.Textures;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R16VertexOffset = 512;
    private const int R16TransformBytes = 64;
    private const int R16SecondTransformOffset = 0x160;
    private const int R16MaximumDrawBytes = 4 * 1024 * 1024;
    private const int R16MaximumTextureBytes = 128 * 1024 * 1024;
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, R14Pipeline>> R16Pipelines =
        new();
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<ulong, nint>> R16PlacedResources =
        new();
    private static readonly Lock R16ContentGate = new();
    private static readonly Dictionary<long, nint> R16WrittenResources = new();

    private static bool RefuseR16(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-DRAW-R16: REFUSED: {reason}.");

        return false;
    }

    private static nint R16FindOwnedResource(RecordedCommandState state, nint host)
    {
        foreach (var value in state.Owned)
        {
            if (IsResource(value) && ((ResourceObject*)value)->HostResource == host)
            {
                return value;
            }
        }

        return 0;
    }

    private static void MarkR16GpuWrite(nint resource, nint queue)
    {
        if (!IsResource(resource) || queue == 0)
        {
            return;
        }

        var generation = ((ResourceObject*)resource)->Generation;

        lock (R16ContentGate)
        {
            if (R16WrittenResources.Count < 65536 || R16WrittenResources.ContainsKey(generation))
            {
                R16WrittenResources[generation] = queue;
            }
        }
    }

    private static nint R16Writer(nint resource)
    {
        if (!IsResource(resource))
        {
            return 0;
        }

        lock (R16ContentGate)
        {
            return R16WrittenResources.GetValueOrDefault(((ResourceObject*)resource)->Generation);
        }
    }

    private static bool TryR16Source(RecordedCommandState state, ulong handle, out R16Source? source)
    {
        source = null;

        if (!TryReadMappingRecord(handle, out DescriptorToken token) || token.Tag != DescriptorTag
                                                                     || token.Kind is not (18 or 70))
        {
            return false;
        }

        DescriptorMetadata? record;

        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out record);
        }

        if (record == null || record.Kind != token.Kind)
        {
            return false;
        }

        var guest = record.Resource;
        var description = record.Description;

        if (record.Kind == 70)
        {
            // Placed SRVs describe memory directly: standard 56-byte resource desc,
            // ten view words followed by a 64-bit console address and extension words.
            if (record.Extra is not { Length: 56 } resourceBytes || description is not { Length: 64 } viewBytes ||
                sizeof(ResourceDescription) != 56)
            {
                return false;
            }

            var placed = MemoryMarshal.Read<ResourceDescription>(resourceBytes);
            var address = BinaryPrimitives.ReadUInt64LittleEndian(viewBytes.AsSpan(40));

            if (address == 0 || placed.Dimension is not (1 or 3) || !ResourceFootprint(placed, out var allocation) ||
                address > (ulong)nint.MaxValue || allocation.Size > (ulong)nint.MaxValue - address)
            {
                return false;
            }

            var cache = R16PlacedResources.GetOrCreateValue(state);

            if (!cache.TryGetValue(token.Id, out guest))
            {
                foreach (var candidate in state.Owned)
                {
                    if (IsResource(candidate))
                    {
                        var existing = (ResourceObject*)candidate;

                        if (existing->Address == address && existing->Description.Dimension == placed.Dimension &&
                            existing->Description.Width == placed.Width && existing->Description.Height == placed.Height
                            &&
                            existing->Description.Format == placed.Format
                            && existing->Description.Layout == placed.Layout &&
                            existing->Description.MipLevels == placed.MipLevels
                            && existing->Description.Flags == placed.Flags &&
                            existing->Description.DepthOrArraySize == placed.DepthOrArraySize &&
                            existing->Description.SampleCount == placed.SampleCount)
                        {
                            guest = candidate;

                            break;
                        }
                    }
                }

                if (guest == 0)
                {
                    var iid = ResourceId;
                    var properties = new HeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };
                    var hr = NewResource(placed, properties, 0, address, 0, 0, &iid, &guest);

                    if (hr < 0 || guest == 0)
                    {
                        return false;
                    }

                    state.RetainObject(guest);

                    _ = Drop(guest);
                }

                cache.Add(token.Id, guest);
            }

            description = viewBytes.AsSpan(0, 40).ToArray();
        }

        if (!IsResource(guest)
            || (record.Kind == 18 && ((ResourceObject*)guest)->Generation != record.ResourceGeneration))
        {
            return false;
        }

        source = new R16Source(handle, token.Id, token.Kind, guest, ((ResourceObject*)guest)->Generation, description);

        return true;
    }

    private static void WriteR16TableEvidence(TextWriter report, ulong table)
    {
        if (table == 0 || table > ulong.MaxValue - 80)
        {
            return;
        }

        for (var offset = 0; offset <= 64; offset += 32)
        {
            if (!TryReadMappingRecord(table + (ulong)offset, out DescriptorToken token))
            {
                report.WriteLine($"[graphics] R16 table+{offset}: unreadable.");

                continue;
            }

            if (token.Tag == ConstantTag)
            {
                report.WriteLine($"[graphics] R16 table+{offset}: CBV size={token.Kind}.");

                continue;
            }

            DescriptorMetadata? metadata = null;

            if (token.Tag == DescriptorTag)
            {
                lock (DescriptorGate)
                {
                    _ = DescriptorRecords.TryGetValue(token.Id, out metadata);
                }
            }

            report.WriteLine(
                $"[graphics] R16 table+{offset}: tag=0x{token.Tag:X}, kind={token.Kind}, descriptor={token.Id}, metadata={metadata != null}.");

            if (metadata?.Description is { Length: >= 40 } view)
            {
                report.WriteLine(
                    $"[graphics] R16 view: format={R16Word(view, 0)}, dimension={R16Word(view, 4)}, words4..8={R16Word(view, 16)},{R16Word(view, 20)},{R16Word(view, 24)},{R16Word(view, 28)},{R16Word(view, 32)}.");
            }

            if (metadata?.Extra is { Length: 56 } extra)
            {
                var desc = MemoryMarshal.Read<ResourceDescription>(extra);

                report.WriteLine(
                    $"[graphics] R16 placed: dimension={desc.Dimension}, width={desc.Width}, height={desc.Height}, format={desc.Format}, layout=0x{desc.Layout:X}, flags=0x{desc.Flags:X}.");
            }
        }
    }

    private static bool R16SourceUnchanged(R16Source source)
    {
        return TryReadMappingRecord(source.Handle, out DescriptorToken token) && token.Tag == DescriptorTag &&
               token.Kind == source.Kind && token.Id == source.Token && IsResource(source.Resource) &&
               ((ResourceObject*)source.Resource)->Generation == source.Generation;
    }

    private static uint R16Word(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static bool TryR16Buffer(R16Source source, out ulong address)
    {
        address = 0;

        if (!IsResource(source.Resource))
        {
            return false;
        }

        var resource = (ResourceObject*)source.Resource;

        if (resource->Description.Dimension != 1 || resource->HostResource != 0 || resource->Address == 0 ||
            source.Description is not { Length: 40 } bytes || R16Word(bytes, 4) != 1)
        {
            return false;
        }

        var format = R16Word(bytes, 0);
        var stride = R16Word(bytes, 28);
        var flags = R16Word(bytes, 32);
        var unit = format switch
        {
            0 when flags == 0 && stride >= 4 => stride,
            39 when flags == 1 && stride == 0 => 4U,
            2 when flags == 0 && stride == 0 => 16U,
            6 when flags == 0 && stride == 0 => 12U,
            16 when flags == 0 && stride == 0 => 8U,
            41 or 42 when flags == 0 && stride == 0 => 4U,
            _ => 0U
        };
        var first = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
        var count = R16Word(bytes, 24);

        if (unit == 0 || first > ulong.MaxValue / unit || (ulong)count * unit < R16TransformBytes)
        {
            return false;
        }

        var offset = first * unit;

        if (offset > resource->Description.Width || R16TransformBytes > resource->Description.Width - offset ||
            offset > ulong.MaxValue - resource->Address)
        {
            return false;
        }

        address = resource->Address + offset;

        return true;
    }

    private static bool TryR16TextureView(R16Source source, out uint viewFormat)
    {
        viewFormat = 0;

        var resource = (ResourceObject*)source.Resource;
        var description = resource->Description;

        if (description.Dimension != 3 || description.DepthOrArraySize != 1 || description.SampleCount != 1 ||
            description.MipLevels is 0 or > 16 || description.Width is 0 or > 16384
            || description.Height is 0 or > 16384 ||
            (description.Flags & 8U) != 0)
        {
            return false;
        }

        viewFormat = description.Format;

        if (source.Description is { } bytes)
        {
            if (bytes.Length != 40 || R16Word(bytes, 4) != 4 || R16Word(bytes, 24) != 0 ||
                R16Word(bytes, 16) >= description.MipLevels || R16Word(bytes, 20) == 0 ||
                (R16Word(bytes, 20) != uint.MaxValue
                 && R16Word(bytes, 20) > description.MipLevels - R16Word(bytes, 16)))
            {
                return false;
            }

            viewFormat = R16Word(bytes, 0);

            if (viewFormat != description.Format && !CompatibleClearFormat(description.Format, viewFormat))
            {
                return false;
            }
        }

        // Float/normalized color formats only; integer, depth and planar views require different shaders.
        return viewFormat is 2 or 10 or 11 or 16 or 24 or 28 or 29 or 34 or 35 or 41 or 49 or 54 or 56 or 61 or
            71 or 72 or 74 or 75 or 77 or 78 or 80 or 81 or 83 or 84 or 87 or 91 or 95 or 96 or 98 or 99;
    }

    private static byte[]? SerializeR16Root(TextWriter report)
    {
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[3];

        for (var index = 0; index < 2; index++)
        {
            parameters[index] = new D3D12_ROOT_PARAMETER1
            {
                ParameterType = (D3D12_ROOT_PARAMETER_TYPE)2,
                ShaderVisibility = (D3D12_SHADER_VISIBILITY)1
            };

            parameters[index].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1 { ShaderRegister = (uint)index };
        }

        var range = new D3D12_DESCRIPTOR_RANGE1
        {
            RangeType = 0,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            OffsetInDescriptorsFromTableStart = 0
        };

        parameters[2] = new D3D12_ROOT_PARAMETER1
        {
            ParameterType = 0,
            ShaderVisibility = (D3D12_SHADER_VISIBILITY)5
        };

        parameters[2].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &range
        };
        // Native immediate sampler words: 00000400 00c00000 18f00000 00000000.
        // Wrap, anisotropic filtering (4x), mip linear, LOD 0..12, zero bias.
        var sampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = (D3D12_FILTER)0x55,
            AddressU = (D3D12_TEXTURE_ADDRESS_MODE)1,
            AddressV = (D3D12_TEXTURE_ADDRESS_MODE)1,
            AddressW = (D3D12_TEXTURE_ADDRESS_MODE)1,
            MaxAnisotropy = 4,
            ComparisonFunc = (D3D12_COMPARISON_FUNC)1,
            MinLOD = 0,
            MaxLOD = 12,
            ShaderVisibility = (D3D12_SHADER_VISIBILITY)5
        };
        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 3,
            pParameters = parameters,
            NumStaticSamplers = 1,
            pStaticSamplers = &sampler,
            Flags = (D3D12_ROOT_SIGNATURE_FLAGS)1
        };

        nint blob = 0, errors = 0;

        try
        {
            var hr = SerializeR10RootNative(&description, &blob, &errors);

            report.WriteLine($"[graphics] R16 root serialization HRESULT=0x{hr:X8}.");

            if (errors != 0)
            {
                report.WriteLine(Encoding.UTF8.GetString(ReadR10Blob(errors, 1024 * 1024)).TrimEnd('\0'));
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

    private static R14Pipeline? GetR16Pipeline(RecordedCommandState state, GraphicsPipelineMetadata metadata,
        uint stride)
    {
        if (metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root) || !metadata.CompleteStream ||
            !metadata.IsStream || metadata.Shaders.Count != 2
            || !TryNormalizeR14Layout(metadata.InputLayout, stride, out var elements) ||
            !elements.Any(static e => e.Name.Equals("POSITION", StringComparison.OrdinalIgnoreCase) && e.Format == 6) ||
            !elements.Any(static e => e.Name.Equals("COLOR", StringComparison.OrdinalIgnoreCase) && e.Format == 28) ||
            !elements.Any(static e => e.Name.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && e.Format == 16))
        {
            _ = RefuseR16("unsupported input-layout or pipeline shape");

            return null;
        }

        var cache = R16Pipelines.GetOrCreateValue(state);

        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            return cached;
        }

        var vertex = metadata.Shaders.SingleOrDefault(static shader => shader.Stage == 1);
        var pixel = metadata.Shaders.SingleOrDefault(static shader => shader.Stage == 2);
        var depth = metadata.State.SingleOrDefault(static record => record.Kind == 21);
        var depthFormat = metadata.State.SingleOrDefault(static record => record.Kind == 16);

        if (vertex == null || pixel == null || depth == null || depthFormat == null || depth.Bytes.Length != 64 ||
            R16Word(depth.Bytes, 4) != 0 || R16Word(depth.Bytes, 16) != 0 || R16Word(depth.Bytes, 56) != 0 ||
            depthFormat.Bytes.Length != 8 || R16Word(depthFormat.Bytes, 4) != 0)
        {
            _ = RefuseR16("textured pipeline requires disabled depth and stencil and UNKNOWN DSV format");

            return null;
        }

        if (R21TexturedPipelines.Acquire(metadata, state) is { } shared)
        {
            cache.Add(state.Pipeline, shared);

            return shared;
        }

        void* root = null;

        nint pipeline = 0;

        try
        {
            var pair = Rdna2RectangleCompiler.TranslateTextured(vertex.Container.Bytes, pixel.Container.Bytes,
                RootSignatureBlob(metadata.Root).ToArray());
            var report = Console.Error;

            report.WriteLine(
                "[graphics] IRX-GFX-SHADER-R16: recognized two-transform IA rectangle and sampled-color export.");

            var vs = CompileR10Shader(pair.VertexSource, "vs_5_1", report);
            var gs = CompileR10Shader(pair.GeometrySource, "gs_5_1", report);
            var ps = CompileR10Shader(pair.PixelSource, "ps_5_1", report);
            var blob = SerializeR16Root(report);

            if (vs == null || gs == null || ps == null || blob == null)
            {
                return null;
            }

            var iid = RootSignatureId;

            fixed (byte* bytes = blob)
            {
                var hr = (int)_host->CreateRootSignature(0, bytes, (nuint)blob.Length, &iid, &root);

                if (hr < 0 || root == null)
                {
                    _ = RefuseR16($"host root creation HRESULT=0x{hr:X8}");

                    return null;
                }
            }

            var native = stackalloc R14NativeInput[3];

            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* uv = "TEXCOORD\0"u8)
            {
                for (var index = 0; index < elements.Length; index++)
                {
                    var element = elements[index];

                    native[index] = new R14NativeInput
                    {
                        Name = element.Name.ToUpperInvariant() switch
                        {
                            "POSITION" => (nint)position,
                            "COLOR" => (nint)color,
                            _ => (nint)uv
                        },
                        Index = element.Index,
                        Format = element.Format,
                        Slot = element.Slot,
                        Offset = element.Offset,
                        Class = element.Class,
                        Step = element.Step
                    };
                }

                pipeline = CreateR10PipelineCandidate(root, vs, ps, metadata, report, gs, native,
                    (uint)elements.Length);
            }

            if (pipeline == 0)
            {
                return null;
            }

            var result = new R14Pipeline((nint)root, pipeline, elements);

            state.HostViews.Add((nint)root);

            root = null;

            state.HostViews.Add(pipeline);

            pipeline = 0;

            cache.Add(state.Pipeline, result);

            R21TexturedPipelines.Publish(metadata, result);

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                                              or NotSupportedException or
                                              OverflowException or DllNotFoundException or EntryPointNotFoundException)
        {
            _ = RefuseR16(exception.Message);

            return null;
        }
        finally
        {
            if (pipeline != 0)
            {
                _ = HostRelease(pipeline);
            }

            if (root != null)
            {
                _ = HostRelease((nint)root);
            }
        }
    }

    private static bool TryRecordR16GuestDraw(nint self, uint vertices, uint instances, uint first, uint firstInstance)
    {
        using var r25Timing = BeginR25Timing(R25Stage.RecordTexture);

        var state = StateFor(self);

        GraphicsPipelineMetadata? metadata;

        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out metadata);
        }

        // Select by native stage contract, not by topology alone. Existing R14 remains available.
        if (metadata == null || !metadata.Shaders.Any(static shader => shader.Stage == 1 &&
                                                                       shader.Hash
                                                                       == "C65EB8C9AF8FCF7389A20872BA42EB1357CCC92A5AE5BB71842CED435741FDB4")
                             ||
                             !metadata.Shaders.Any(static shader => shader.Stage == 2 &&
                                                                    shader.Hash
                                                                    == "5CD73CC975D0976DA64F3B1A1E882E49BC99C064DB540FABB4EB1BB8614CFE9F"))
        {
            return false;
        }

        if (!ExecuteGuestVertexFetch || _host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 ||
            vertices == 0 || vertices % 3 != 0 || instances != 1 || firstInstance != 0
            || state.Topology != R14RectangleTopology ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } || state.Targets.Length != 1 ||
            state.Depth.HasValue || state.Vertices.Count != 1 || !state.Vertices.TryGetValue(0, out var vertex) ||
            state.Graphics.Tables.Count != 1 || !state.Graphics.Tables.TryGetValue(0, out var table) ||
            state.Graphics.Views.Count != 0 || state.Graphics.Constants.Count != 0)
        {
            return RefuseR16("unsupported textured rectangle draw/binding shape");
        }

        var stride = vertex.FormatOrStride;
        var start = (ulong)first * stride;
        var bytes = (ulong)vertices * stride;

        if (stride is 0 or > 256 || vertex.Address == 0 || start > vertex.Size || bytes > vertex.Size - start ||
            start > ulong.MaxValue - vertex.Address || bytes > R16MaximumDrawBytes || table == 0
            || table > ulong.MaxValue - 80 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2
            || state.HostViews.Count > R13MaximumHostViews - 5 ||
            state.UploadBytes > UploadBudget - (bytes + R16VertexOffset))
        {
            return RefuseR16("vertex extent or command budget invalid");
        }

        var translated = GetR16Pipeline(state, metadata, stride);

        if (translated == null)
        {
            return RefuseR16("textured host pipeline unavailable");
        }

        if (!TryR16Source(state, table, out var transform) || transform == null || !TryR16Buffer(transform, out _) ||
            !TryR16Source(state, table + 32, out var texture) || texture == null
            || !TryR16TextureView(texture, out var format))
        {
            WriteR16TableEvidence(Console.Error, table);

            return RefuseR16("expected live buffer SRV at table+0 and Texture2D SRV at table+32");
        }

        state.RetainObject(transform.Resource);

        state.RetainObject(texture.Resource);

        if (MaterializeHostResource(texture.Resource, out var textureHost) < 0 || textureHost == 0 ||
            !TryResolveClear(self, state.Targets[0], 20, out var color, out var rtv, out var colorFormat) ||
            colorFormat != 28 || color == textureHost)
        {
            return RefuseR16("unsupported texture/RTV or read-write alias");
        }

        var target = R16FindOwnedResource(state, color);

        if (target == 0)
        {
            return RefuseR16("missing retained render target");
        }

        var resource = (ResourceObject*)texture.Resource;

        TraceR24($"[graphics] R16 texture: {resource->Description.Width}x{resource->Description.Height}, " +
                 $"format={format}, layout=0x{resource->Description.Layout:X}, mips={resource->Description.MipLevels}; descriptor={texture.Token}.");

        var capacity = checked((int)bytes + R16VertexOffset);

        nint upload = 0, heap = 0;

        try
        {
            var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 1, Flags = 1 };
            var heapId = DescriptorHeapId;
            var hostDevice = (nint)_host;
            var hr =
                ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(hostDevice,
                    14))
                (hostDevice, &heapDescription, &heapId, &heap);

            if (hr < 0 || heap == 0)
            {
                return RefuseR16($"shader-visible heap HRESULT=0x{hr:X8}");
            }

            var cpu = ((ID3D12DescriptorHeap*)heap)->GetCPUDescriptorHandleForHeapStart();
            var gpuDescriptor = ((ID3D12DescriptorHeap*)heap)->GetGPUDescriptorHandleForHeapStart();

            fixed (byte* description = texture.Description)
            {
                _host->CreateShaderResourceView((ID3D12Resource*)textureHost,
                    (D3D12_SHADER_RESOURCE_VIEW_DESC*)description, cpu);
            }

            hr = CreateHostResource(HostBufferDescription((ulong)capacity), 2, GenericRead, &upload);

            if (hr < 0 || upload == 0)
            {
                return RefuseR16($"vertex/transform upload HRESULT=0x{hr:X8}");
            }

            var gpu = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (gpu == 0 || (gpu & 255UL) != 0 || gpuDescriptor.ptr == 0)
            {
                return RefuseR16("invalid native binding addresses");
            }

            var deferred = new R16Upload(transform, texture, table, vertex.Address + start, (int)bytes, capacity,
                upload,
                textureHost, target);

            state.HostViews.Add(upload);

            upload = 0;

            state.HostViews.Add(heap);

            var nativeHeap = (ID3D12DescriptorHeap*)heap;

            heap = 0;

            state.UploadBytes += (ulong)capacity;

            SealMemoryBoundary(self, state, null, null, textured: new R16DrawBoundary(deferred, false));

            var list = Host(self);
            var commands = (ID3D12GraphicsCommandList*)list;
            var vb = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = gpu + R16VertexOffset,
                SizeInBytes = (uint)bytes,
                StrideInBytes = stride
            };
            var colorView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = rtv };

            TransitionHostResource(list, color, 0, 4);

            TransitionHostResource(list, textureHost, 0, 0x80);

            commands->SetDescriptorHeaps(1, &nativeHeap);

            commands->SetPipelineState((ID3D12PipelineState*)translated.Pipeline);

            commands->SetGraphicsRootSignature((ID3D12RootSignature*)translated.Root);

            commands->SetGraphicsRootConstantBufferView(0, gpu);

            commands->SetGraphicsRootConstantBufferView(1, gpu + 256);

            commands->SetGraphicsRootDescriptorTable(2, gpuDescriptor);

            commands->IASetVertexBuffers(0, 1, &vb);

            commands->OMSetRenderTargets(1, &colorView, false, null);

            TraceHostCommand(self, "R16 textured rectangle DrawInstanced");

            commands->DrawInstanced(vertices, instances, 0, firstInstance);

            commands->OMSetRenderTargets(0, null, false, null);

            TransitionHostResource(list, textureHost, 0x80, 0);

            TransitionHostResource(list, color, 4, 0);

            SealMemoryBoundary(self, state, null, null, textured: new R16DrawBoundary(deferred, true));

            TraceR24("[graphics] IRX-GFX-DRAW-R16: RECORDED textured guest rectangle; awaiting submission.");

            return true;
        }
        finally
        {
            if (upload != 0)
            {
                _ = HostRelease(upload);
            }

            if (heap != 0)
            {
                _ = HostRelease(heap);
            }
        }
    }

    private static void ExecuteR16DrawBoundary(nint queue, R16DrawBoundary boundary)
    {
        using var r25Timing = BeginR25Timing(R25Stage.ExecuteTexture);

        var upload = boundary.Upload;

        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] R16 completion without preparation.");
            }

            MarkR16GpuWrite(upload.Target, queue);

            Interlocked.Increment(ref _r24Draws);

            TraceR24("[graphics] IRX-GFX-DRAW-R16: GPU COMPLETED textured guest rectangle.");

            TraceR171Color(queue, upload.Target, "R16-output");

            PreviewCompletedR16Draw(queue, upload.Target);

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] R16 experimental draw requires list reset before resubmission.");
        }

        ulong transform = 0;

        if (!R16SourceUnchanged(upload.Transform) || !R16SourceUnchanged(upload.Texture) ||
            !TryR16Buffer(upload.Transform, out transform))
        {
            Environment.FailFast("[graphics] R16 source SRV changed or buffer unavailable at submission.");
        }

        Span<byte> token = stackalloc byte[16];
        Span<byte> recheck = stackalloc byte[16];

        if (!TryReadR12Memory(upload.Table + 64, token) || R16Word(token, 0) != ConstantTag ||
            R16Word(token, 4) < R16SecondTransformOffset + R16TransformBytes)
        {
            Environment.FailFast("[graphics] R16 table+64 must contain a CBV covering bytes 0x160..0x19f.");
        }

        var constants = BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
        var rented = ArrayPool<byte>.Shared.Rent(upload.Capacity);

        try
        {
            var snapshot = rented.AsSpan(0, upload.Capacity);

            snapshot.Clear();

            if (constants == 0 || constants > ulong.MaxValue - R16SecondTransformOffset ||
                !TryReadR12Memory(transform, snapshot[..R16TransformBytes]) ||
                !TryReadR12Memory(constants + R16SecondTransformOffset, snapshot.Slice(256, R16TransformBytes)) ||
                !TryReadR12Memory(upload.Vertex, snapshot.Slice(R16VertexOffset, upload.VertexBytes)) ||
                !TryReadR12Memory(upload.Table + 64, recheck) || !token.SequenceEqual(recheck) ||
                !R16SourceUnchanged(upload.Transform) || !R16SourceUnchanged(upload.Texture))
            {
                Environment.FailFast("[graphics] R16 unreadable or changed vertex/transform data at submission.");
            }

            var noRead = default(HostRange);

            void* mapped = null;

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload.Host, 8))
                (upload.Host, 0, &noRead, &mapped), "R16 map draw upload");

            if (mapped == null)
            {
                Environment.FailFast("[graphics] R16 draw upload mapping returned null.");
            }

            var written = new HostRange { End = (nuint)upload.Capacity };

            try
            {
                snapshot.CopyTo(new Span<byte>(mapped, upload.Capacity));
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload.Host, 9))(upload.Host, 0,
                    &written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        var writer = R16Writer(upload.Texture.Resource);

        if (writer == 0)
        {
            if (!UploadR16Texture(queue, upload.Texture.Resource, upload.TextureHost))
            {
                Environment.FailFast("[graphics] R16 texture upload refused; inspect layout/source diagnostic.");
            }
        }
        else if (writer != queue)
        {
            Environment.FailFast("[graphics] R16 texture was GPU-written on another queue; coherence not established.");
        }
        else
        {
            TraceR24("[graphics] R16 sampling prior same-queue GPU-written texture (guest alias writes untracked).");
        }

        TraceR171Color(queue, upload.Texture.Resource, "R16-texture-input", writer == 0);

        TraceR24($"[graphics] IRX-GFX-DRAW-R16: UPLOADED {upload.VertexBytes} vertex bytes and two transform blocks.");
    }

    private static bool TryPlanR16Texture(ResourceDescription description, out R16TextureCopy? plan)
    {
        plan = null;

        if (!TextureFootprintCalculator.TryCreate(description, out var layout) || layout.Planes.Length != 1 ||
            layout.Size == 0 || layout.Size > R16MaximumTextureBytes)
        {
            return false;
        }

        var plane = layout.Planes[0];

        if (!plane.Addressable || plane.Thick || plane.Slices != 1 || plane.Mips.Length != description.MipLevels)
        {
            return false;
        }

        var pitches = new uint[plane.Mips.Length];
        var offsets = new ulong[plane.Mips.Length];

        ulong bytes = 0;

        for (var index = 0; index < plane.Mips.Length; index++)
        {
            var mip = plane.Mips[index];

            if (mip.Depth != 1 || mip.Width == 0 || mip.Height == 0)
            {
                return false;
            }

            var pitch = ((ulong)mip.Width * plane.Bytes + 255) & ~255UL;
            var offset = (bytes + 511) & ~511UL;

            bytes = offset + pitch * mip.Height;

            if (bytes > R16MaximumTextureBytes)
            {
                return false;
            }

            pitches[index] = (uint)pitch;

            offsets[index] = offset;
        }

        plan = new R16TextureCopy(layout, bytes, pitches, offsets);

        return true;
    }

    private static bool DetileR16Texture(R16TextureCopy plan, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var plane = plan.Layout.Planes[0];

        if ((ulong)source.Length < plan.Layout.Size || (ulong)destination.Length < plan.Bytes)
        {
            return false;
        }

        var patterns = plane.Patterns();

        for (var index = 0; index < plane.Mips.Length; index++)
        {
            var mip = plane.Mips[index];

            for (var y = 0U; y < mip.Height; y++)
            {
                var row = plan.Offsets[index] + (ulong)y * plan.Pitches[index];

                if (plane.Mode == 0)
                {
                    var from = plane.Offset + mip.Offset + (ulong)y * mip.Pitch * plane.Bytes;
                    var count = mip.Width * plane.Bytes;

                    if (from > (ulong)source.Length || count > (ulong)source.Length - from)
                    {
                        return false;
                    }

                    source.Slice((int)from, (int)count).CopyTo(destination.Slice((int)row, (int)count));

                    continue;
                }

                for (var x = 0U; x < mip.Width; x++)
                {
                    var tx = x + mip.TailX;
                    var ty = y + mip.TailY;
                    var from = plane.Offset + mip.Offset +
                               ((ulong)(ty / plane.TileHeight) * (mip.Pitch / plane.TileWidth) + tx / plane.TileWidth)
                               * plane.Alignment +
                               (patterns[0][tx % plane.TileWidth] | patterns[1][ty % plane.TileHeight]
                                                                  | patterns[2][0]);

                    if (from > (ulong)source.Length || plane.Bytes > (ulong)source.Length - from)
                    {
                        return false;
                    }

                    source.Slice((int)from, (int)plane.Bytes)
                        .CopyTo(destination.Slice((int)(row + (ulong)x * plane.Bytes), (int)plane.Bytes));
                }
            }
        }

        return true;
    }

    private static bool R16UploadFormat(uint format)
    {
        return format is 2 or 10 or 11 or 16 or 24 or 28 or 29 or 34 or 35 or 41 or 49 or 54 or 56 or 61 or
            71 or 72 or 74 or 75 or 77 or 78 or 80 or 81 or 83 or 84 or 87 or 91 or 95 or 96 or 98 or 99;
    }

    private static bool UploadR16Texture(nint queue, nint guest, nint host)
    {
        using var r25Timing = BeginR25Timing(R25Stage.UploadTexture);

        var resource = (ResourceObject*)guest;
        var description = resource->Description;
        // Do not overwrite a render target from CPU memory unless a real CPU-authoritative path is known.
        if ((description.Flags & 7U) != 0 || resource->Address == 0 || !R16UploadFormat(description.Format) ||
            !TryPlanR16Texture(description, out var plan) || plan == null)
        {
            Console.Error.WriteLine(
                $"[graphics] R16 CPU texture refused: flags=0x{description.Flags:X}, layout=0x{description.Layout:X}, format={description.Format}, mips={description.MipLevels}.");

            return false;
        }

        nint staging = 0, allocator = 0, list = 0;

        var rented = ArrayPool<byte>.Shared.Rent((int)plan.Layout.Size);

        try
        {
            var source = rented.AsSpan(0, (int)plan.Layout.Size);

            if (!TryReadR12Memory(resource->Address, source))
            {
                Console.Error.WriteLine("[graphics] R16 texture has unreadable/sparse CPU pages; no upload performed.");

                return false;
            }

            CheckHostResult(CreateHostResource(HostBufferDescription(plan.Bytes), 2, GenericRead, &staging),
                "R16 texture staging");

            var noRead = default(HostRange);

            void* mapped = null;

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(staging, 8))
                (staging, 0, &noRead, &mapped), "R16 map texture staging");

            if (mapped == null)
            {
                return false;
            }

            var written = default(HostRange);

            try
            {
                var target = new Span<byte>(mapped, (int)plan.Bytes);

                target.Clear();

                if (!DetileR16Texture(plan, source, target))
                {
                    return false;
                }

                written.End = (nuint)plan.Bytes;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(staging, 9))(staging, 0, &written);
            }

            CreateR16UtilityList(out allocator, out list);

            TransitionHostResource(list, host, 0, CopyDestination);

            var copy =
                (delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                    void*, void>)Slot(list, 16);

            for (var index = 0; index < plan.Pitches.Length; index++)
            {
                var to = new ClearProbeCopyLocation { Resource = host, Offset = (ulong)index };
                var from = new ClearProbeCopyLocation
                {
                    Resource = staging,
                    Kind = 1,
                    Offset = plan.Offsets[index],
                    Format = description.Format,
                    Width = (uint)Math.Max(1UL, description.Width >> index),
                    Height = Math.Max(1U, description.Height >> index),
                    Depth = 1,
                    Pitch = plan.Pitches[index]
                };

                copy(list, &to, 0, 0, 0, &from, null);
            }

            TransitionHostResource(list, host, CopyDestination, 0);

            SubmitR16UtilityList(queue, list);

            TraceR24(
                $"[graphics] IRX-GFX-TEXTURE-R16: UPLOADED {description.Width}x{description.Height}, mips={description.MipLevels}, bytes={plan.Bytes} from console layout.");

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);

            ReleaseR16Utility(list, allocator, staging);
        }
    }

    private static void CreateR16UtilityList(out nint allocator, out nint list)
    {
        allocator = 0;

        list = 0;

        nint created = 0;

        var device = (nint)_host;
        var iid = AllocatorId;

        CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(device, 9))
            (device, 0, &iid, &created), "R16 utility allocator");

        allocator = created;

        if (allocator == 0)
        {
            throw new InvalidOperationException("R16 utility allocator returned null.");
        }

        var listId = HostListId;

        created = 0;

        CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12))
            (device, 0, 0, allocator, 0, &listId, &created), "R16 utility list");

        list = created;

        if (list == 0)
        {
            throw new InvalidOperationException("R16 utility list returned null.");
        }
    }

    private static void SubmitR16UtilityList(nint queue, nint list)
    {
        using var r25Timing = BeginR25Timing(R25Stage.UtilitySubmit);

        CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9))(list), "R16 utility close");

        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &list);

        WaitForHostCompletion(queue);
    }

    private static void ReleaseR16Utility(nint list, nint allocator, nint resource)
    {
        if (list != 0)
        {
            _ = HostRelease(list);
        }

        if (allocator != 0)
        {
            _ = HostRelease(allocator);
        }

        if (resource != 0)
        {
            _ = HostRelease(resource);
        }
    }

    private static void VerifyR16TexturePlan()
    {
        VerifyR171ColorStats();

        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = 65,
            Height = 3,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleCount = 1,
            Format = 28,
            Layout = 1
        };

        if (!TryPlanR16Texture(description, out var plan) || plan == null || plan.Pitches[0] != 512 ||
            plan.Offsets[0] != 0 || plan.Bytes != 1536)
        {
            throw new InvalidOperationException("R16 linear staging footprint regression failed.");
        }

        var source = new byte[(int)plan.Layout.Size];
        var destination = new byte[(int)plan.Bytes];
        var plane = plan.Layout.Planes[0];
        var mip = plane.Mips[0];

        for (var y = 0U; y < 3; y++)
        {
            for (var x = 0U; x < 65; x++)
            {
                var offset = (int)(plane.Offset + mip.Offset + ((ulong)y * mip.Pitch + x) * plane.Bytes);

                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset), x | (y << 16) | 0xFF000000U);
            }
        }

        if (!DetileR16Texture(plan, source, destination) ||
            R16Word(destination, 2 * 512 + 64 * 4) != 0xFF020040U ||
            DetileR16Texture(plan, source.AsSpan(0, source.Length - 1), destination))
        {
            throw new InvalidOperationException("R16 texture staging copy regression failed.");
        }

        description.Layout = 0x11B;

        if (TryPlanR16Texture(description, out _))
        {
            throw new InvalidOperationException("R16 unsupported XOR texture layout was accepted.");
        }

        Console.WriteLine(
            "[graphics-probe] R16 linear texture staging, bounds and unsupported-layout checks passed (CPU only).");
    }

    private sealed record R16Source(
        ulong Handle,
        ulong Token,
        uint Kind,
        nint Resource,
        long Generation,
        byte[]? Description);

    private sealed record R16TextureCopy(TextureLayout Layout, ulong Bytes, uint[] Pitches, ulong[] Offsets);

    private sealed class R16Upload(
        R16Source transform,
        R16Source texture,
        ulong table,
        ulong vertex,
        int vertexBytes,
        int capacity,
        nint host,
        nint textureHost,
        nint target)
    {
        public readonly int Capacity = capacity;
        public readonly nint Host = host;
        public readonly ulong Table = table;
        public readonly nint Target = target;
        public readonly R16Source Texture = texture;
        public readonly nint TextureHost = textureHost;
        public readonly R16Source Transform = transform;
        public readonly ulong Vertex = vertex;
        public readonly int VertexBytes = vertexBytes;
        public int Phase;
    }

    private readonly record struct R16DrawBoundary(R16Upload Upload, bool Complete);
}
