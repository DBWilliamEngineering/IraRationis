// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.Resources;

/// <summary>Guest view semantics and the actual backing format selected for host materialization.</summary>
internal static class HostTextureViewFormats
{
    internal const uint Rgba8Typeless = 27;
    internal const uint Rgba8Unorm = 28;
    internal const uint Rgba8Srgb = 29;

    internal static uint BackingFormat(in ResourceDescription guest)
    {
        // The captured color target is sRGB storage with a linear RTV. Use a
        // typeless host allocation only for the supported single-subresource RT
        // contract. Do not mutate the guest description or broaden UAV/DS paths.
        if (guest.Format == Rgba8Srgb && guest.Dimension == 3 && guest.DepthOrArraySize == 1 &&
            guest.MipLevels == 1 && guest.SampleCount == 1 && (guest.Flags & 7U) == 1U)
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

    internal static bool TryResolveTargetView(in ResourceDescription guest, bool depth, byte[]? descriptor,
        out uint viewFormat)
    {
        viewFormat = guest.Format;

        if (guest.Dimension != 3 || guest.DepthOrArraySize != 1 || guest.MipLevels != 1 ||
            guest.SampleCount != 1 || (guest.Flags & (depth ? 2U : 1U)) == 0)
        {
            return false;
        }

        if (descriptor != null)
        {
            var words = descriptor.AsSpan();

            if (words.Length != 24 || BinaryPrimitives.ReadUInt32LittleEndian(words[4..]) != (depth ? 3U : 4U) ||
                BinaryPrimitives.ReadUInt32LittleEndian(words[8..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(words[12..]) != 0)
            {
                return false;
            }

            viewFormat = BinaryPrimitives.ReadUInt32LittleEndian(words);
        }

        return (depth
            ? viewFormat is 40 or 45 or 55 or 20
            : viewFormat is 2 or 10 or 24 or 28 or 29 or 41 or 49 or 61 or 87 or 91) &&
            IsCompatible(guest, viewFormat);
    }
}
