// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int DualMaskConstantsOffset = 64;
    private const int DualMaskConstantBytes = 64;

    private static bool TryBindDualMaskTextures(RecordedCommandState state, ulong table, nint target,
        out DualMaskRectangleBindings? bindings)
    {
        bindings = null;

        if (table > (ulong)nint.MaxValue - DualMaskConstantsOffset - (ulong)sizeof(ConstantDescriptor) ||
            !TryReadConstantDescriptor(table + DualMaskConstantsOffset, DualMaskConstantBytes, out _) ||
            !TryGetTextureSource(state, table, out var first) || first == null || !IsDualMaskTexture(first) ||
            !TryGetTextureSource(state, table + 32, out var second) || second == null || !IsDualMaskTexture(second))
        {
            return RejectRectangleDraw("dual-mask rectangle requires two BC3 Texture2D views and a pixel CBV");
        }

        state.RetainObject(first.Resource);
        state.RetainObject(second.Resource);

        if (MaterializeHostResource(first.Resource, out var firstHost) < 0 || firstHost == 0 ||
            MaterializeHostResource(second.Resource, out var secondHost) < 0 || secondHost == 0 ||
            firstHost == target || secondHost == target)
        {
            return RejectRectangleDraw("dual-mask rectangle cannot materialize non-aliasing sample sources");
        }

        var description = new DescriptorHeapDescription { Kind = 0, Count = 2, Flags = 1 };
        var id = DescriptorHeapId;
        nint heap = 0;

        try
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)
                Slot((nint)_host, 14))((nint)_host, &description, &id, &heap);

            if (result < 0 || heap == 0)
            {
                return RejectRectangleDraw($"dual-mask descriptor heap HRESULT=0x{result:X8}");
            }

            var cpu = ((ID3D12DescriptorHeap*)heap)->GetCPUDescriptorHandleForHeapStart();
            var gpu = ((ID3D12DescriptorHeap*)heap)->GetGPUDescriptorHandleForHeapStart();
            var increment = _host->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

            if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0)
            {
                return RejectRectangleDraw("dual-mask descriptor heap addresses unavailable");
            }

            WriteHostTextureShaderView(first, firstHost, cpu);
            cpu.ptr += increment;
            WriteHostTextureShaderView(second, secondHost, cpu);

            bindings = new DualMaskRectangleBindings(first, firstHost, second, secondHost, heap, gpu.ptr);
            state.HostViews.Add(heap);
            heap = 0;

            return true;
        }
        finally
        {
            if (heap != 0)
            {
                _ = HostRelease(heap);
            }
        }
    }

    private static bool IsDualMaskTexture(TextureSource source)
    {
        if (!TryGetTextureView(source, out var format) ||
            (DXGI_FORMAT)format is not (DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB) ||
            !SupportsHostTextureSampling(format))
        {
            return false;
        }

        // The guest sampler's degamma flag is immaterial for alpha or constant channels.
        return source.Description is not { } descriptor || ((ReadDescriptorUInt32(descriptor, 8) >> 9) & 7U) >= 3;
    }

    private static void TransitionDualMaskTextures(nint list, DualMaskRectangleBindings bindings, uint before, uint after)
    {
        TransitionHostResource(list, bindings.FirstHost, before, after);

        if (bindings.SecondHost != bindings.FirstHost)
        {
            TransitionHostResource(list, bindings.SecondHost, before, after);
        }
    }

    private static void PrepareDualMaskTextures(nint queue, DualMaskRectangleBindings bindings, OrderedTextureUploads? ordered)
    {
        if (!IsDualMaskSourceCurrent(bindings.First, bindings.FirstHost) ||
            !IsDualMaskSourceCurrent(bindings.Second, bindings.SecondHost))
        {
            Environment.FailFast("[graphics] Dual-mask texture view changed before submission.");
        }

        var firstWriter = GetResourceWriterQueue(bindings.First.Resource);
        var secondWriter = GetResourceWriterQueue(bindings.Second.Resource);

        if ((firstWriter != 0 && firstWriter != queue) || (secondWriter != 0 && secondWriter != queue))
        {
            Environment.FailFast("[graphics] Dual-mask textures have an unverified cross-queue writer.");
        }

        if ((firstWriter == 0 && !UploadTextureTexture(queue, bindings.First.Resource, bindings.FirstHost, ordered)) ||
            (bindings.SecondHost != bindings.FirstHost && secondWriter == 0 &&
             !UploadTextureTexture(queue, bindings.Second.Resource, bindings.SecondHost, ordered)))
        {
            Environment.FailFast("[graphics] Dual-mask texture upload refused; inspect layout and source diagnostics.");
        }

        if (!IsDualMaskSourceCurrent(bindings.First, bindings.FirstHost) ||
            !IsDualMaskSourceCurrent(bindings.Second, bindings.SecondHost))
        {
            Environment.FailFast("[graphics] Dual-mask texture view changed during preparation.");
        }
    }

    private static bool IsDualMaskSourceCurrent(TextureSource source, nint host)
    {
        return IsTextureSourceCurrent(source) && ((ResourceObject*)source.Resource)->HostResource == host;
    }
}
