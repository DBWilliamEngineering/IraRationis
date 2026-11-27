// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int TexturedVertexOffset = 512;
    private const int TextureTransformBytes = 64;
    private const int TextureSecondTransformOffset = 0x160;
    private const int TextureMaximumDrawBytes = 4 * 1024 * 1024;
    private const int TextureMaximumTextureBytes = 128 * 1024 * 1024;

    private const int TexturePlanCacheLimit = 32;
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, RectanglePipeline>>
        TexturedDrawPipelines =
            new();
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<ulong, nint>> TexturePlacedResources =
        new();
    private static readonly Lock ResourceWriteGate = new();
    private static readonly Dictionary<long, nint> ResourceWriterQueues = new();
    private static readonly object TexturePlanGate = new();

    private static readonly Dictionary<TexturePlanKey, HostTextureCopy> TexturePlans = new();

    private static bool RejectTexturedDraw(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-DRAW-R16: REFUSED: {reason}.");

        return false;
    }

    private static nint FindOwnedResource(RecordedCommandState state, nint host)
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

    private static void MarkGpuWrite(nint resource, nint queue)
    {
        if (!IsResource(resource) || queue == 0)
        {
            return;
        }

        var generation = ((ResourceObject*)resource)->Generation;

        lock (ResourceWriteGate)
        {
            if (ResourceWriterQueues.Count < 65536 || ResourceWriterQueues.ContainsKey(generation))
            {
                ResourceWriterQueues[generation] = queue;
            }
        }
    }

    private static nint GetResourceWriterQueue(nint resource)
    {
        if (!IsResource(resource))
        {
            return 0;
        }

        lock (ResourceWriteGate)
        {
            return ResourceWriterQueues.GetValueOrDefault(((ResourceObject*)resource)->Generation);
        }
    }

    private static bool TryGetTextureSource(RecordedCommandState state, ulong handle, out TextureSource? source)
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

            var cache = TexturePlacedResources.GetOrCreateValue(state);

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
                    var properties = new GraphicsHeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };
                    var resultCode = NewResource(placed, properties, 0, address, 0, 0, &iid, &guest);

                    if (resultCode < 0 || guest == 0)
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

        source = new TextureSource(handle, token.Id, token.Kind, guest, ((ResourceObject*)guest)->Generation,
            description);

        return true;
    }

    private static void WriteTextureTableEvidence(TextWriter report, ulong table)
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
                    $"[graphics] R16 view: format={ReadDescriptorUInt32(view, 0)}, dimension={ReadDescriptorUInt32(view, 4)}, words4..8={ReadDescriptorUInt32(view, 16)},{ReadDescriptorUInt32(view, 20)},{ReadDescriptorUInt32(view, 24)},{ReadDescriptorUInt32(view, 28)},{ReadDescriptorUInt32(view, 32)}.");
            }

            if (metadata?.Extra is { Length: 56 } extra)
            {
                var desc = MemoryMarshal.Read<ResourceDescription>(extra);

                report.WriteLine(
                    $"[graphics] R16 placed: dimension={desc.Dimension}, width={desc.Width}, height={desc.Height}, format={desc.Format}, layout=0x{desc.Layout:X}, flags=0x{desc.Flags:X}.");
            }
        }
    }

    private static bool IsTextureSourceCurrent(TextureSource source)
    {
        return TryReadMappingRecord(source.Handle, out DescriptorToken token) && token.Tag == DescriptorTag &&
               token.Kind == source.Kind && token.Id == source.Token && IsResource(source.Resource) &&
               ((ResourceObject*)source.Resource)->Generation == source.Generation;
    }

    private static uint ReadDescriptorUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static bool TryGetTextureBuffer(TextureSource source, out ulong address)
    {
        address = 0;

        if (!IsResource(source.Resource))
        {
            return false;
        }

        var resource = (ResourceObject*)source.Resource;

        if (resource->Description.Dimension != 1 || resource->HostResource != 0 || resource->Address == 0 ||
            source.Description is not { Length: 40 } bytes || ReadDescriptorUInt32(bytes, 4) != 1)
        {
            return false;
        }

        var format = ReadDescriptorUInt32(bytes, 0);
        var stride = ReadDescriptorUInt32(bytes, 28);
        var flags = ReadDescriptorUInt32(bytes, 32);
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
        var count = ReadDescriptorUInt32(bytes, 24);

        if (unit == 0 || first > ulong.MaxValue / unit || (ulong)count * unit < TextureTransformBytes)
        {
            return false;
        }

        var offset = first * unit;

        if (offset > resource->Description.Width || TextureTransformBytes > resource->Description.Width - offset ||
            offset > ulong.MaxValue - resource->Address)
        {
            return false;
        }

        address = resource->Address + offset;

        return true;
    }

    private static bool TryGetTextureView(TextureSource source, out uint viewFormat)
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
            if (bytes.Length != 40 || ReadDescriptorUInt32(bytes, 4) != 4 || ReadDescriptorUInt32(bytes, 24) != 0 ||
                ReadDescriptorUInt32(bytes, 16) >= description.MipLevels || ReadDescriptorUInt32(bytes, 20) == 0 ||
                (ReadDescriptorUInt32(bytes, 20) != uint.MaxValue
                 && ReadDescriptorUInt32(bytes, 20) > description.MipLevels - ReadDescriptorUInt32(bytes, 16)))
            {
                return false;
            }

            viewFormat = ReadDescriptorUInt32(bytes, 0);

            if (!HostTextureViewFormats.IsSampledCompatible(description, viewFormat) ||
                !HostTextureViewFormats.IsComponentMapping(ReadDescriptorUInt32(bytes, 8)))
            {
                return false;
            }
        }

        // Integer, depth and planar views need distinct shader contracts.
        return HostTextureViewFormats.IsSampledColorFormat(viewFormat);
    }

    private static byte[]? SerializeTextureRoot(TextWriter report)
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
            var resultCode = SerializeRootNative(&description, &blob, &errors);

            report.WriteLine($"[graphics] R16 root serialization HRESULT=0x{resultCode:X8}.");

            if (errors != 0)
            {
                report.WriteLine(Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
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

    private static RectanglePipeline? GetTexturedPipeline(RecordedCommandState state, GraphicsPipelineMetadata metadata,
        uint stride)
    {
        if (metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root) || !metadata.CompleteStream ||
            !metadata.IsStream || metadata.Shaders.Count != 2
            || !TryNormalizeRectangleLayout(metadata.InputLayout, stride, out var elements) ||
            !elements.Any(static e => e.Name.Equals("POSITION", StringComparison.OrdinalIgnoreCase) && e.Format == 6) ||
            !elements.Any(static e => e.Name.Equals("COLOR", StringComparison.OrdinalIgnoreCase) && e.Format == 28) ||
            !elements.Any(static e => e.Name.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && e.Format == 16))
        {
            _ = RejectTexturedDraw("unsupported input-layout or pipeline shape");

            return null;
        }

        var cache = TexturedDrawPipelines.GetOrCreateValue(state);

        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            return cached;
        }

        var vertex = metadata.Shaders.SingleOrDefault(static shader => shader.Stage == 1);
        var pixel = metadata.Shaders.SingleOrDefault(static shader => shader.Stage == 2);
        var depth = metadata.State.SingleOrDefault(static record => record.Kind == 21);
        var depthFormat = metadata.State.SingleOrDefault(static record => record.Kind == 16);

        if (vertex == null || pixel == null || depth == null || depthFormat == null || depth.Bytes.Length != 64 ||
            ReadDescriptorUInt32(depth.Bytes, 4) != 0 || ReadDescriptorUInt32(depth.Bytes, 16) != 0
            || ReadDescriptorUInt32(depth.Bytes, 56) != 0 ||
            depthFormat.Bytes.Length != 8 || ReadDescriptorUInt32(depthFormat.Bytes, 4) != 0)
        {
            _ = RejectTexturedDraw("textured pipeline requires disabled depth and stencil and UNKNOWN DSV format");

            return null;
        }

        if (SharedTexturedPipelines.Acquire(metadata, state) is { } shared)
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

            var vertexShader = CompileHostShader(pair.VertexSource, "vs_5_1", report);
            var geometryShader = CompileHostShader(pair.GeometrySource, "gs_5_1", report);
            var pixelShader = CompileHostShader(pair.PixelSource, "ps_5_1", report);
            var blob = SerializeTextureRoot(report);

            if (vertexShader == null || geometryShader == null || pixelShader == null || blob == null)
            {
                return null;
            }

            var iid = RootSignatureId;

            fixed (byte* bytes = blob)
            {
                var resultCode = (int)_host->CreateRootSignature(0, bytes, (nuint)blob.Length, &iid, &root);

                if (resultCode < 0 || root == null)
                {
                    _ = RejectTexturedDraw($"host root creation HRESULT=0x{resultCode:X8}");

                    return null;
                }
            }

            var native = stackalloc NativeVertexInputElement[3];

            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* textureCoordinateSemantic = "TEXCOORD\0"u8)
            {
                for (var index = 0; index < elements.Length; index++)
                {
                    var element = elements[index];

                    native[index] = new NativeVertexInputElement
                    {
                        Name = element.Name.ToUpperInvariant() switch
                        {
                            "POSITION" => (nint)position,
                            "COLOR" => (nint)color,
                            _ => (nint)textureCoordinateSemantic
                        },
                        Index = element.Index,
                        Format = element.Format,
                        Slot = element.Slot,
                        Offset = element.Offset,
                        Class = element.Class,
                        Step = element.Step
                    };
                }

                pipeline = CreatePipelineCandidate(root, vertexShader, pixelShader, metadata, report, geometryShader,
                    native,
                    (uint)elements.Length);
            }

            if (pipeline == 0)
            {
                return null;
            }

            var result = new RectanglePipeline((nint)root, pipeline, elements);

            state.HostViews.Add((nint)root);

            root = null;

            state.HostViews.Add(pipeline);

            pipeline = 0;

            cache.Add(state.Pipeline, result);

            SharedTexturedPipelines.Publish(metadata, result);

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                                              or NotSupportedException or
                                              OverflowException or DllNotFoundException or EntryPointNotFoundException)
        {
            _ = RejectTexturedDraw(exception.Message);

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

    private static bool TryRecordTexturedGuestDraw(nint self, uint vertices, uint instances, uint first,
        uint firstInstance)
    {
        using var stageTiming = BeginTiming(GraphicsStage.RecordTexture);

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
            || state.Topology != RectangleRectangleTopology ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } || state.Targets.Length != 1 ||
            state.Depth.HasValue || state.Vertices.Count != 1 || !state.Vertices.TryGetValue(0, out var vertex) ||
            state.Graphics.Tables.Count != 1 || !state.Graphics.Tables.TryGetValue(0, out var table) ||
            state.Graphics.Views.Count != 0 || state.Graphics.Constants.Count != 0)
        {
            return RejectTexturedDraw("unsupported textured rectangle draw/binding shape");
        }

        var stride = vertex.FormatOrStride;
        var start = (ulong)first * stride;
        var bytes = (ulong)vertices * stride;

        if (stride is 0 or > 256 || vertex.Address == 0 || start > vertex.Size || bytes > vertex.Size - start ||
            start > ulong.MaxValue - vertex.Address || bytes > TextureMaximumDrawBytes || table == 0
            || table > ulong.MaxValue - 80 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2
            || state.HostViews.Count > MaximumCommandHostViews - 5 ||
            state.UploadBytes > UploadBudget - (bytes + TexturedVertexOffset))
        {
            return RejectTexturedDraw("vertex extent or command budget invalid");
        }

        var translated = GetTexturedPipeline(state, metadata, stride);

        if (translated == null)
        {
            return RejectTexturedDraw("textured host pipeline unavailable");
        }

        if (!TryGetTextureSource(state, table, out var transform) || transform == null
                                                                  || !TryGetTextureBuffer(transform, out _) ||
                                                                  !TryGetTextureSource(state, table + 32,
                                                                      out var texture) || texture == null
                                                                  || !TryGetTextureView(texture, out var format))
        {
            WriteTextureTableEvidence(Console.Error, table);

            return RejectTexturedDraw("expected live buffer SRV at table+0 and Texture2D SRV at table+32");
        }

        state.RetainObject(transform.Resource);

        state.RetainObject(texture.Resource);

        if (MaterializeHostResource(texture.Resource, out var textureHost) < 0 || textureHost == 0 ||
            !TryResolveClear(self, state.Targets[0], 20, out var color, out var rtv, out var colorFormat) ||
            colorFormat != 28 || color == textureHost)
        {
            return RejectTexturedDraw("unsupported texture/RTV or read-write alias");
        }

        var target = FindOwnedResource(state, color);

        if (target == 0)
        {
            return RejectTexturedDraw("missing retained render target");
        }

        var resource = (ResourceObject*)texture.Resource;

        TraceGraphicsSuccess($"[graphics] R16 texture: {resource->Description.Width}x{resource->Description.Height}, " +
                             $"format={format}, layout=0x{resource->Description.Layout:X}, mips={resource->Description.MipLevels}; descriptor={texture.Token}.");

        var capacity = checked((int)bytes + TexturedVertexOffset);

        nint upload = 0, heap = 0;

        try
        {
            var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 1, Flags = 1 };
            var heapId = DescriptorHeapId;
            var hostDevice = (nint)_host;
            var resultCode =
                ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(hostDevice,
                    14))
                (hostDevice, &heapDescription, &heapId, &heap);

            if (resultCode < 0 || heap == 0)
            {
                return RejectTexturedDraw($"shader-visible heap HRESULT=0x{resultCode:X8}");
            }

            var cpu = ((ID3D12DescriptorHeap*)heap)->GetCPUDescriptorHandleForHeapStart();
            var gpuDescriptor = ((ID3D12DescriptorHeap*)heap)->GetGPUDescriptorHandleForHeapStart();

            WriteHostTextureShaderView(texture, textureHost, cpu);

            resultCode = CreateHostResource(HostBufferDescription((ulong)capacity), 2, GenericRead, &upload);

            if (resultCode < 0 || upload == 0)
            {
                return RejectTexturedDraw($"vertex/transform upload HRESULT=0x{resultCode:X8}");
            }

            var gpu = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (gpu == 0 || (gpu & 255UL) != 0 || gpuDescriptor.ptr == 0)
            {
                return RejectTexturedDraw("invalid native binding addresses");
            }

            var deferred = new TexturedDrawUpload(transform, texture, table, vertex.Address + start, (int)bytes,
                capacity,
                upload,
                textureHost, target);

            state.HostViews.Add(upload);

            upload = 0;

            state.HostViews.Add(heap);

            var nativeHeap = (ID3D12DescriptorHeap*)heap;

            heap = 0;

            state.UploadBytes += (ulong)capacity;

            SealMemoryBoundary(self, state, null, null, textured: new TexturedDrawBoundary(deferred, false));

            var list = Host(self);
            var commands = (ID3D12GraphicsCommandList*)list;
            var vertexBufferView = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = gpu + TexturedVertexOffset,
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

            commands->IASetVertexBuffers(0, 1, &vertexBufferView);

            commands->OMSetRenderTargets(1, &colorView, false, null);

            TraceHostCommand(self, "R16 textured rectangle DrawInstanced");

            commands->DrawInstanced(vertices, instances, 0, firstInstance);

            commands->OMSetRenderTargets(0, null, false, null);

            TransitionHostResource(list, textureHost, 0x80, 0);

            TransitionHostResource(list, color, 4, 0);

            SealMemoryBoundary(self, state, null, null, textured: new TexturedDrawBoundary(deferred, true));

            TraceGraphicsSuccess(
                "[graphics] IRX-GFX-DRAW-R16: RECORDED textured guest rectangle; awaiting submission.");

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

    private static void ExecuteTexturedDrawBoundary(nint queue, TexturedDrawBoundary boundary,
        OrderedTextureUploads? ordered = null)
    {
        using var stageTiming = BeginTiming(GraphicsStage.ExecuteTexture);

        var upload = boundary.Upload;

        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] R16 completion without preparation.");
            }

            MarkGpuWrite(upload.Target, queue);

            Interlocked.Increment(ref _draws);

            TraceGraphicsSuccess("[graphics] IRX-GFX-DRAW-R16: GPU COMPLETED textured guest rectangle.");

            TraceColor(queue, upload.Target, "R16-output");

            PreviewCompletedTextureDraw(queue, upload.Target);

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] R16 experimental draw requires list reset before resubmission.");
        }

        ulong transform = 0;

        if (!IsTextureSourceCurrent(upload.Transform) || !IsTextureSourceCurrent(upload.Texture) ||
            !TryGetTextureBuffer(upload.Transform, out transform))
        {
            Environment.FailFast("[graphics] R16 source SRV changed or buffer unavailable at submission.");
        }

        Span<byte> token = stackalloc byte[16];
        Span<byte> recheck = stackalloc byte[16];

        if (!TryReadGuestMemory(upload.Table + 64, token) || ReadDescriptorUInt32(token, 0) != ConstantTag ||
            ReadDescriptorUInt32(token, 4) < TextureSecondTransformOffset + TextureTransformBytes)
        {
            Environment.FailFast("[graphics] R16 table+64 must contain a CBV covering bytes 0x160..0x19f.");
        }

        var constants = BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
        var rented = ArrayPool<byte>.Shared.Rent(upload.Capacity);

        try
        {
            var snapshot = rented.AsSpan(0, upload.Capacity);

            snapshot.Clear();

            if (constants == 0 || constants > ulong.MaxValue - TextureSecondTransformOffset ||
                !TryReadGuestMemory(transform, snapshot[..TextureTransformBytes]) ||
                !TryReadGuestMemory(constants + TextureSecondTransformOffset,
                    snapshot.Slice(256, TextureTransformBytes)) ||
                !TryReadGuestMemory(upload.Vertex, snapshot.Slice(TexturedVertexOffset, upload.VertexBytes)) ||
                !TryReadGuestMemory(upload.Table + 64, recheck) || !token.SequenceEqual(recheck) ||
                !IsTextureSourceCurrent(upload.Transform) || !IsTextureSourceCurrent(upload.Texture))
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

        var writer = GetResourceWriterQueue(upload.Texture.Resource);

        if (writer == 0)
        {
            if (!UploadTextureTexture(queue, upload.Texture.Resource, upload.TextureHost, ordered))
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
            TraceGraphicsSuccess(
                "[graphics] R16 sampling prior same-queue GPU-written texture (guest alias writes untracked).");
        }

        TraceColor(queue, upload.Texture.Resource, "R16-texture-input", writer == 0);

        TraceGraphicsSuccess(
            $"[graphics] IRX-GFX-DRAW-R16: UPLOADED {upload.VertexBytes} vertex bytes and two transform blocks.");
    }

    private static bool TryPlanTextureTexture(ResourceDescription description, out HostTextureCopy? plan)
    {
        var key = new TexturePlanKey(description.Dimension, description.Alignment, description.Width,
            description.Height,
            description.DepthOrArraySize, description.MipLevels, description.Format, description.SampleCount,
            description.SampleQuality, description.Layout, description.Flags);
        lock (TexturePlanGate)
        {
            if (TexturePlans.TryGetValue(key, out var existing))
            {
                plan = existing;

                return true;
            }
        }

        if (!TryCreateTexturePlan(description, out plan) || plan == null)
        {
            return false;
        }

        // Bound the retained arrays; larger and unusually deep layouts remain uncached.
        if (description.Width <= 4096 && description.Height <= 4096 && plan.Pitches.Length <= 256)
        {
            lock (TexturePlanGate)
            {
                if (TexturePlans.Count >= TexturePlanCacheLimit)
                {
                    TexturePlans.Clear();
                }

                TexturePlans[key] = plan;
            }
        }

        return true;
    }

    private static bool TryCreateTexturePlan(ResourceDescription description, out HostTextureCopy? plan)
    {
        plan = null;

        if (!TextureFootprintCalculator.TryCreate(description, out var layout) || layout.Planes.Length != 1 ||
            layout.Size == 0 || layout.Size > TextureMaximumTextureBytes)
        {
            return false;
        }

        var plane = layout.Planes[0];

        if (!plane.Addressable || plane.Thick || plane.Slices == 0 || plane.Slices > 2048
            || plane.Mips.Length != description.MipLevels ||
            plane.Slices != description.DepthOrArraySize)
        {
            return false;
        }

        var subresources = checked(plane.Mips.Length * (int)plane.Slices);
        var pitches = new uint[subresources];
        var offsets = new ulong[subresources];

        ulong bytes = 0;

        for (var index = 0; index < pitches.Length; index++)
        {
            var mip = plane.Mips[index % plane.Mips.Length];

            if (mip.Depth != 1 || mip.Width == 0 || mip.Height == 0)
            {
                return false;
            }

            var pitch = ((ulong)mip.Width * plane.Bytes + 255) & ~255UL;
            var offset = (bytes + 511) & ~511UL;

            bytes = offset + pitch * mip.Height;

            if (bytes > TextureMaximumTextureBytes)
            {
                return false;
            }

            pitches[index] = (uint)pitch;

            offsets[index] = offset;
        }

        var columns = new ulong[plane.Mips.Length][];
        var patterns = plane.Patterns();
        for (var index = 0; index < columns.Length; index++)
        {
            var mip = plane.Mips[index];
            var values = plane.Mode == 0 ? Array.Empty<ulong>() : new ulong[mip.Width];
            for (var x = 0U; x < values.Length; x++)
            {
                var tx = x + mip.TailX;
                values[x] = (tx / plane.TileWidth * plane.Alignment) | patterns[0][tx % plane.TileWidth];
            }

            columns[index] = values;
        }

        plan = new HostTextureCopy(layout, bytes, pitches, offsets, columns);

        return true;
    }

    private static bool DetileTextureTexture(HostTextureCopy plan, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var plane = plan.Layout.Planes[0];

        if ((ulong)source.Length < plan.Layout.Size || (ulong)destination.Length < plan.Bytes)
        {
            return false;
        }

        var patterns = plane.Patterns();

        for (var index = 0; index < plan.Pitches.Length; index++)
        {
            var mip = plane.Mips[index % plane.Mips.Length];
            var sliceOffset = (ulong)(index / plane.Mips.Length) * plane.SliceSize;

            var activeRowBytes = (ulong)mip.Width * plane.Bytes;
            if (plane.Mode == 0 && activeRowBytes == (ulong)mip.Pitch * plane.Bytes
                                && activeRowBytes == plan.Pitches[index])
            {
                var from = plane.Offset + sliceOffset + mip.Offset;
                var bytes = activeRowBytes * mip.Height;
                if (from > (ulong)source.Length || bytes > (ulong)source.Length - from ||
                    plan.Offsets[index] > (ulong)destination.Length
                    || bytes > (ulong)destination.Length - plan.Offsets[index])
                {
                    return false;
                }

                source.Slice((int)from, (int)bytes).CopyTo(destination.Slice((int)plan.Offsets[index], (int)bytes));

                continue;
            }

            var xOffsets = plan.Columns[index % plane.Mips.Length];

            for (var coordinateY = 0U; coordinateY < mip.Height; coordinateY++)
            {
                var row = plan.Offsets[index] + (ulong)coordinateY * plan.Pitches[index];

                if (plane.Mode == 0)
                {
                    var from = plane.Offset + sliceOffset + mip.Offset + (ulong)coordinateY * mip.Pitch * plane.Bytes;
                    var count = mip.Width * plane.Bytes;

                    if (from > (ulong)source.Length || count > (ulong)source.Length - from)
                    {
                        return false;
                    }

                    source.Slice((int)from, (int)count).CopyTo(destination.Slice((int)row, (int)count));

                    continue;
                }

                var ty = coordinateY + mip.TailY;
                var rowBase = plane.Offset + sliceOffset + mip.Offset +
                              (ulong)(ty / plane.TileHeight) * (mip.Pitch / plane.TileWidth) * plane.Alignment;
                var rowPattern = patterns[1][ty % plane.TileHeight] | patterns[2][0];

                for (var coordinateX = 0U; coordinateX < mip.Width; coordinateX++)
                {
                    var from = rowBase + (xOffsets[coordinateX] | rowPattern);

                    if (from > (ulong)source.Length || plane.Bytes > (ulong)source.Length - from)
                    {
                        return false;
                    }

                    source.Slice((int)from, (int)plane.Bytes)
                        .CopyTo(destination.Slice((int)(row + (ulong)coordinateX * plane.Bytes), (int)plane.Bytes));
                }
            }
        }

        return true;
    }

    private static bool TextureUploadFormat(uint format)
    {
        return HostTextureViewFormats.IsSampledColorFormat(format);
    }

    private static bool UploadTextureTexture(nint queue, nint guest, nint host, OrderedTextureUploads? ordered = null)
    {
        using var stageTiming = BeginTiming(GraphicsStage.UploadTexture);

        var resource = (ResourceObject*)guest;
        var description = resource->Description;
        // Do not overwrite a render target from CPU memory unless a real CPU-authoritative path is known.
        if ((description.Flags & 7U) != 0 || resource->Address == 0
                                          || !HostTextureViewFormats.TryGetColorCopyFormat(description.Format,
                                              out var copyFormat) ||
                                          !TryPlanTextureTexture(description, out var plan) || plan == null)
        {
            Console.Error.WriteLine(
                $"[graphics] R16 CPU texture refused: flags=0x{description.Flags:X}, layout=0x{description.Layout:X}, format={description.Format}, mips={description.MipLevels}.");

            return false;
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)plan.Layout.Size);

        try
        {
            var source = rented.AsSpan(0, (int)plan.Layout.Size);

            if (!TryReadGuestMemory(resource->Address, source))
            {
                Console.Error.WriteLine("[graphics] R16 texture has unreadable/sparse CPU pages; no upload performed.");

                return false;
            }

            var versioned = VersionedTextureUploadsEnabled && plan.Bytes <= 16UL * 1024 * 1024;

            var utility = AcquireUtilityContext(plan.Bytes, 0, versioned);
            var transferred = false;
            try
            {
                var staging = utility.Upload;
                var list = utility.List;

                if (!PrepareHostTextureUpload(utility, plan, source))
                {
                    return false;
                }

                TransitionHostResource(list, host, 0, CopyDestination);

                for (var index = 0; index < plan.Pitches.Length; index++)
                {
                    var mip = index % description.MipLevels;
                    var footprint = new HostTextureTransferFootprint((uint)index, copyFormat,
                        (uint)Math.Max(1UL, description.Width >> mip), Math.Max(1U, description.Height >> mip),
                        plan.Pitches[index], plan.Offsets[index]);

                    RecordHostTextureTransfer(list, host, staging, plan.Bytes, footprint, true);
                }

                TransitionHostResource(list, host, CopyDestination, 0);

                if (ordered is null)
                {
                    utility.SubmitAndWait(queue);
                }
                else
                {
                    ordered.Submit(utility);
                    transferred = true;
                }

                ReportUtilityExecution();

                TraceGraphicsSuccess(
                    $"[graphics] IRX-GFX-TEXTURE-R16: {(transferred ? "QUEUED" : "UPLOADED")} {description.Width}x{description.Height}, mips={description.MipLevels}, bytes={plan.Bytes} from console layout.");

                return true;
            }
            finally
            {
                if (!transferred)
                {
                    utility.Dispose();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static void CreateUtilityCommandList(out nint allocator, out nint list)
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

    private static void SubmitUtilityCommandList(nint queue, nint list)
    {
        using var stageTiming = BeginTiming(GraphicsStage.UtilitySubmit);

        CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9))(list), "R16 utility close");

        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &list);

        WaitForHostCompletion(queue);
    }

    internal static void ReleaseUtilityCommands(nint list, nint allocator, nint resource)
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

    private static void VerifyTextureTexturePlan()
    {
        VerifyColorStats();

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

        if (!TryPlanTextureTexture(description, out var plan) || plan == null || plan.Pitches[0] != 512 ||
            plan.Offsets[0] != 0 || plan.Bytes != 1536)
        {
            throw new InvalidOperationException("R16 linear staging footprint regression failed.");
        }

        var source = new byte[(int)plan.Layout.Size];
        var destination = new byte[(int)plan.Bytes];
        var plane = plan.Layout.Planes[0];
        var mip = plane.Mips[0];

        for (var coordinateY = 0U; coordinateY < 3; coordinateY++)
        {
            for (var coordinateX = 0U; coordinateX < 65; coordinateX++)
            {
                var offset = (int)(plane.Offset + mip.Offset
                                                + ((ulong)coordinateY * mip.Pitch + coordinateX) * plane.Bytes);

                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset),
                    coordinateX | (coordinateY << 16) | 0xFF000000U);
            }
        }

        if (!DetileTextureTexture(plan, source, destination) ||
            ReadDescriptorUInt32(destination, 2 * 512 + 64 * 4) != 0xFF020040U ||
            DetileTextureTexture(plan, source.AsSpan(0, source.Length - 1), destination))
        {
            throw new InvalidOperationException("R16 texture staging copy regression failed.");
        }

        description.DepthOrArraySize = 2;
        if (!TryPlanTextureTexture(description, out var cachedLayout) || cachedLayout == null ||
            !TryPlanTextureTexture(description, out var reusedLayout) || !ReferenceEquals(cachedLayout, reusedLayout))
        {
            throw new InvalidOperationException("R77 immutable texture plan reuse failed.");
        }

        var changedLayout = description;
        changedLayout.Width += 1;
        if (!TryPlanTextureTexture(changedLayout, out var isolatedLayout)
            || ReferenceEquals(cachedLayout, isolatedLayout))
        {
            throw new InvalidOperationException("R77 texture plan cache did not isolate differing extents.");
        }

        if (!TryPlanTextureTexture(description, out var arrayPlan) || arrayPlan == null
                                                                   || arrayPlan.Pitches.Length != 2)
        {
            throw new InvalidOperationException("Indexed UI array staging footprint failed.");
        }

        var arraySource = new byte[(int)arrayPlan.Layout.Size];
        var arrayDestination = new byte[(int)arrayPlan.Bytes];
        var arrayPlane = arrayPlan.Layout.Planes[0];
        BinaryPrimitives.WriteUInt32LittleEndian(arraySource.AsSpan((int)arrayPlane.Offset), 0x10203040U);
        BinaryPrimitives.WriteUInt32LittleEndian(arraySource.AsSpan((int)(arrayPlane.Offset + arrayPlane.SliceSize)),
            0x50607080U);
        if (!DetileTextureTexture(arrayPlan, arraySource, arrayDestination) ||
            ReadDescriptorUInt32(arrayDestination, (int)arrayPlan.Offsets[0]) != 0x10203040U ||
            ReadDescriptorUInt32(arrayDestination, (int)arrayPlan.Offsets[1]) != 0x50607080U)
        {
            throw new InvalidOperationException("Indexed UI array slice isolation failed.");
        }

        Console.WriteLine("[graphics-probe] Indexed UI texture-array slice staging passed (CPU only).");
        Console.WriteLine("[graphics-probe] R77 immutable texture plan reuse and isolation passed (CPU only).");

        description.Width = 64;
        description.DepthOrArraySize = 1;
        if (!TryPlanTextureTexture(description, out var tight) || tight == null)
        {
            throw new InvalidOperationException("R76 tight linear texture plan failed.");
        }

        var tightSource = new byte[(int)tight.Layout.Size];
        var tightTarget = new byte[(int)tight.Bytes];
        var tightPlane = tight.Layout.Planes[0];
        var tightMip = tightPlane.Mips[0];
        var tightOffset = (int)(tightPlane.Offset + tightMip.Offset);
        for (var i = 0; i < tightTarget.Length; i++)
        {
            tightSource[tightOffset + i] = (byte)(i * 17);
        }

        if (!DetileTextureTexture(tight, tightSource, tightTarget) ||
            !tightSource.AsSpan(tightOffset, tightTarget.Length).SequenceEqual(tightTarget))
        {
            throw new InvalidOperationException("R76 tight linear staging mismatch.");
        }

        description.Width = 133;
        description.Height = 79;
        description.MipLevels = 4;
        description.DepthOrArraySize = 2;
        foreach (var layout in new uint[] { 0x101, 0x105, 0x10A })
        {
            description.Layout = layout;
            if (!TryPlanTextureTexture(description, out var tiled) || tiled == null)
            {
                throw new InvalidOperationException("R76 tiled staging plan failed.");
            }

            var tiledSource = new byte[(int)tiled.Layout.Size];
            var tiledTarget = new byte[(int)tiled.Bytes];
            new Random(76).NextBytes(tiledSource);
            var tiledPlane = tiled.Layout.Planes[0];
            var patterns = tiledPlane.Patterns();
            if (!DetileTextureTexture(tiled, tiledSource, tiledTarget))
            {
                throw new InvalidOperationException("R76 tiled staging failed.");
            }

            for (var sub = 0; sub < tiled.Pitches.Length; sub++)
            {
                var m = tiledPlane.Mips[sub % tiledPlane.Mips.Length];
                var slice = (ulong)(sub / tiledPlane.Mips.Length) * tiledPlane.SliceSize;
                for (var y = 0U; y < m.Height; y++)
                {
                    for (var x = 0U; x < m.Width; x++)
                    {
                        var tx = x + m.TailX;
                        var ty = y + m.TailY;
                        var from = tiledPlane.Offset + slice + m.Offset +
                                   ((ulong)(ty / tiledPlane.TileHeight) * (m.Pitch / tiledPlane.TileWidth)
                                    + tx / tiledPlane.TileWidth) * tiledPlane.Alignment +
                                   (patterns[0][tx % tiledPlane.TileWidth] | patterns[1][ty % tiledPlane.TileHeight]
                                                                           | patterns[2][0]);
                        var to = tiled.Offsets[sub] + (ulong)y * tiled.Pitches[sub] + (ulong)x * tiledPlane.Bytes;
                        if (!tiledSource.AsSpan((int)from, (int)tiledPlane.Bytes)
                                .SequenceEqual(tiledTarget.AsSpan((int)to, (int)tiledPlane.Bytes)))
                        {
                            throw new InvalidOperationException(
                                "R76 tiled staging differs from original addressing formula.");
                        }
                    }
                }
            }
        }

        Console.WriteLine("[graphics-probe] R76 bulk linear and tiled addressing parity passed (CPU only).");

        description.DepthOrArraySize = 1;
        description.Layout = 0x11B;

        if (TryPlanTextureTexture(description, out _))
        {
            throw new InvalidOperationException("R16 unsupported XOR texture layout was accepted.");
        }

        Console.WriteLine(
            "[graphics-probe] R16 linear texture staging, bounds and unsupported-layout checks passed (CPU only).");
    }

    private readonly record struct TexturePlanKey(
        uint Dimension,
        ulong Alignment,
        ulong Width,
        uint Height,
        ushort Slices,
        ushort Mips,
        uint Format,
        uint Samples,
        uint Quality,
        uint Layout,
        uint Flags);
}
