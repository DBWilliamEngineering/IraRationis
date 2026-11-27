// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Resources.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Resources;

/// <summary>Guest view semantics and the actual backing format selected for host materialization.</summary>
internal static class HostTextureViewFormats
{
    internal const uint Rgba8Typeless = 27;
    internal const uint Rgba8Unorm = 28;
    internal const uint Rgba8Srgb = 29;
    internal const uint A8Unorm = 65;
    internal const uint DepthStencilTypeless = (uint)DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS;
    internal const uint DepthPlaneViewFormat = (uint)DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    internal const uint DepthPlaneSubresource = 0;

    internal static bool TryGetDepthPlaneShaderView(in ResourceDescription storage, byte[]? descriptor,
        out uint viewFormat)
    {
        const int shaderViewBytes = 40;
        const int formatOffset = 0;
        const int viewDimensionOffset = 4;
        const int componentMappingOffset = 8;
        const int mostDetailedMipOffset = 16;
        const int mipLevelsOffset = 20;
        const int planeSliceOffset = 24;
        const int minimumLodClampOffset = 28;
        const int maximumTextureExtent = 16_384;
        const uint allowDepthStencil = (uint)D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
        const uint denyShaderResource = (uint)D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE;

        viewFormat = 0;

        if (storage.Format != DepthStencilTypeless ||
            storage.Dimension != (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
            storage.DepthOrArraySize != 1 || storage.MipLevels != 1 || storage.SampleCount != 1 ||
            storage.Width is 0 or > maximumTextureExtent || storage.Height is 0 or > maximumTextureExtent ||
            storage.Layout != (uint)D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN ||
            (storage.Flags & (allowDepthStencil | denyShaderResource)) != allowDepthStencil ||
            descriptor is not { Length: shaderViewBytes })
        {
            return false;
        }

        var bytes = descriptor.AsSpan();
        viewFormat = BinaryPrimitives.ReadUInt32LittleEndian(bytes[formatOffset..]);

        return viewFormat == DepthPlaneViewFormat &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[viewDimensionOffset..]) ==
               (uint)D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D &&
               IsComponentMapping(BinaryPrimitives.ReadUInt32LittleEndian(bytes[componentMappingOffset..])) &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[mostDetailedMipOffset..]) == 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[mipLevelsOffset..]) is 1 or uint.MaxValue &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[planeSliceOffset..]) == DepthPlaneSubresource &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[minimumLodClampOffset..]) == 0;
    }

    internal static uint BackingFormat(in ResourceDescription guest)
    {
        // The captured color target is sRGB storage with a linear RTV. Use a
        // typeless host allocation only for the supported single-subresource RT
        // contract. Do not mutate the guest description or broaden UAV/DS paths.
        if (guest is
                { Format: Rgba8Srgb, Dimension: 3, DepthOrArraySize: 1, MipLevels: 1, SampleCount: 1 }
            && (guest.Flags & 7U) == 1U)
        {
            return Rgba8Typeless;
        }

        return guest.Format;
    }

    internal static bool RequiresExplicitDefaultView(in ResourceDescription guest)
    {
        return BackingFormat(guest) != guest.Format;
    }

    internal static bool IsCompatible(in ResourceDescription guest, uint viewFormat)
    {
        var backing = BackingFormat(guest);

        return backing == viewFormat || backing switch
        {
            1 => viewFormat == 2,
            9 => viewFormat == 10,
            19 => viewFormat == 20,
            23 => viewFormat == 24,
            Rgba8Typeless => viewFormat is Rgba8Unorm or Rgba8Srgb,
            39 => viewFormat is 40 or 41,
            44 => viewFormat == 45,
            48 => viewFormat == 49,
            53 => viewFormat == 55,
            60 => viewFormat == 61,
            90 => viewFormat is 87 or 91,
            _ => false
        };
    }

    internal static bool IsSampledColorFormat(uint format)
    {
        return format is 2 or 10 or 11 or 16 or 24 or 28 or 29 or 31 or 34 or 35 or 37 or 41 or 49 or 51 or
            54 or 56 or 58 or 61 or 63 or 65 or 71 or 72 or 74 or 75 or 77 or 78 or 80 or 81 or 83 or 84 or
            85 or 86 or 87 or 88 or 91 or 93 or 95 or 96 or 98 or 99 or 115;
    }

    // A copy footprint needs a typed representative of the same storage family.
    // This does not change the resource's guest format or its shader view.
    internal static bool TryGetColorCopyFormat(uint storage, out uint format)
    {
        format = storage switch
        {
            1 => 2, 9 => 10, 15 => 16, 23 => 24, 27 => 28, 33 => 34, 39 => 41,
            48 => 49, 53 => 56, 60 => 61, 70 => 71, 73 => 74, 76 => 77, 79 => 80,
            82 => 83, 90 => 87, 92 => 88, 94 => 95, 97 => 98, _ => storage
        };

        return IsSampledColorFormat(format);
    }

    internal static bool IsSampledCompatible(in ResourceDescription guest, uint format)
    {
        if (!IsSampledColorFormat(format))
        {
            return false;
        }

        var storage = BackingFormat(guest);

        return storage == format || storage switch
        {
            1 => format == 2, 9 => format is 10 or 11, 15 => format == 16, 23 => format == 24,
            27 => format is 28 or 29 or 31, 33 => format is 34 or 35 or 37, 39 => format == 41,
            48 => format is 49 or 51, 53 => format is 54 or 56 or 58, 60 => format is 61 or 63,
            70 => format is 71 or 72, 73 => format is 74 or 75, 76 => format is 77 or 78,
            79 => format is 80 or 81, 82 => format is 83 or 84, 90 => format is 87 or 91,
            92 => format is 88 or 93, 94 => format is 95 or 96, 97 => format is 98 or 99, _ => false
        };
    }

    internal static bool IsComponentMapping(uint mapping)
    {
        return (mapping & ~0x1FFFU) == 0 && (mapping & 0x1000U) != 0 &&
               (mapping & 7U) <= 5 && ((mapping >> 3) & 7U) <= 5 &&
               ((mapping >> 6) & 7U) <= 5 && ((mapping >> 9) & 7U) <= 5;
    }

    internal static bool TryResolveTargetView(in ResourceDescription guest, bool depth, byte[]? descriptor,
        out uint viewFormat)
    {
        viewFormat = guest.Format;

        if (guest.Dimension != 3 || guest.DepthOrArraySize != 1 || guest.MipLevels != 1 ||
            guest.SampleCount != 1 || (guest.Flags & (depth ? 2U : 1U)) == 0)
        {
            return false;
        }

        if (descriptor == null)
        {
            return (depth
                       ? viewFormat is 40 or 45 or 55 or 20
                       : viewFormat is 2 or 10 or 24 or 28 or 29 or 41 or 49 or 61 or A8Unorm or 87 or 91) &&
                   IsCompatible(guest, viewFormat);
        }

        var words = descriptor.AsSpan();

        if (words.Length != 24 || BinaryPrimitives.ReadUInt32LittleEndian(words[4..]) != (depth ? 3U : 4U) ||
            BinaryPrimitives.ReadUInt32LittleEndian(words[8..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(words[12..]) != 0)
        {
            return false;
        }

        viewFormat = BinaryPrimitives.ReadUInt32LittleEndian(words);

        return (depth
                   ? viewFormat is 40 or 45 or 55 or 20
                   : viewFormat is 2 or 10 or 24 or 28 or 29 or 41 or 49 or 61 or A8Unorm or 87 or 91) &&
               IsCompatible(guest, viewFormat);
    }
}
