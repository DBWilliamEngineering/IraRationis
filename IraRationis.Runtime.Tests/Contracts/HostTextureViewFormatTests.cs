// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Runtime.Tests.Contracts;

internal static class HostTextureViewFormatTests
{
    internal static void Run()
    {
        var captured = new ResourceDescription
        {
            Dimension = 3, Width = 3840, Height = 2160, DepthOrArraySize = 1,
            MipLevels = 1, Format = 29, SampleCount = 1, Flags = 0xC00001
        };

        var linear = View(28);
        var srgb = View(29);

        Require(HostTextureViewFormats.BackingFormat(captured) == 27 && captured.Format == 29 &&
                HostTextureViewFormats.RequiresExplicitDefaultView(captured),
            "captured sRGB target uses typeless host storage");
        Require(HostTextureViewFormats.TryResolveTargetView(captured, false, linear, out var linearFormat) &&
                linearFormat == 28, "captured linear RTV is accepted");
        Require(HostTextureViewFormats.TryResolveTargetView(captured, false, srgb, out var srgbFormat) &&
                srgbFormat == 29, "explicit sRGB RTV is preserved");
        Require(HostTextureViewFormats.TryResolveTargetView(captured, false, null, out var defaultFormat) &&
                defaultFormat == 29, "default guest RTV remains sRGB");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, View(87), out _),
            "unrelated family rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, View(27), out _),
            "typeless RTV rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, View(28, 5), out _),
            "array RTV rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, View(28, 4, 1), out _),
            "nonzero mip rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, View(28, 4, 0, 1), out _),
            "nonzero plane rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, false, linear[..20], out _),
            "truncated RTV rejected");
        Require(!HostTextureViewFormats.TryResolveTargetView(captured, true, linear, out _),
            "color target is not a DSV");

        var typed = captured;
        typed.Format = 28;

        Require(HostTextureViewFormats.BackingFormat(typed) == 28 &&
                !HostTextureViewFormats.RequiresExplicitDefaultView(typed) &&
                HostTextureViewFormats.TryResolveTargetView(typed, false, linear, out _),
            "ordinary UNORM path unchanged");
        Require(!HostTextureViewFormats.TryResolveTargetView(typed, false, srgb, out _),
            "unpromoted typed cross-cast rejected");

        var typeless = captured;
        typeless.Format = 27;

        Require(HostTextureViewFormats.TryResolveTargetView(typeless, false, linear, out _) &&
                HostTextureViewFormats.TryResolveTargetView(typeless, false, srgb, out _) &&
                !HostTextureViewFormats.TryResolveTargetView(typeless, false, null, out _),
            "existing typeless path preserved");

        var sampled = captured;
        sampled.Flags = 0;

        Require(HostTextureViewFormats.BackingFormat(sampled) == 29 &&
                !HostTextureViewFormats.RequiresExplicitDefaultView(sampled) &&
                !HostTextureViewFormats.IsCompatible(sampled, 28), "ordinary sRGB sampled texture unchanged");

        var unsupported = captured;
        unsupported.MipLevels = 2;
        Require(HostTextureViewFormats.BackingFormat(unsupported) == 29 &&
                !HostTextureViewFormats.TryResolveTargetView(unsupported, false, linear, out _),
            "multi-mip cast not broadened");
        unsupported = captured;
        unsupported.SampleCount = 4;
        Require(HostTextureViewFormats.BackingFormat(unsupported) == 29, "MSAA cast not broadened");
        unsupported = captured;
        unsupported.DepthOrArraySize = 2;
        Require(HostTextureViewFormats.BackingFormat(unsupported) == 29, "array cast not broadened");
        unsupported = captured;
        unsupported.Flags |= 4;
        Require(HostTextureViewFormats.BackingFormat(unsupported) == 29 &&
                !HostTextureViewFormats.TryResolveTargetView(unsupported, false, linear, out _),
            "UAV cast not broadened");

        sampled.Format = 65;
        Require(HostTextureViewFormats.IsSampledCompatible(sampled, 65) &&
                HostTextureViewFormats.TryGetColorCopyFormat(65, out var alphaCopy) && alphaCopy == 65,
            "A8 alpha mask");
        sampled.Format = 60;
        Require(HostTextureViewFormats.IsSampledCompatible(sampled, 61) &&
                HostTextureViewFormats.TryGetColorCopyFormat(60, out var redCopy) && redCopy == 61 &&
                !HostTextureViewFormats.IsSampledCompatible(sampled, 65) &&
                !HostTextureViewFormats.IsSampledCompatible(sampled, 62),
            "typeless R8 with explicit normalized view only");
        sampled.Format = 92;
        Require(HostTextureViewFormats.IsSampledCompatible(sampled, 93) &&
                HostTextureViewFormats.TryGetColorCopyFormat(92, out var xCopy) && xCopy == 88, "BGRX storage family");
        Require(!HostTextureViewFormats.TryGetColorCopyFormat(20, out _) &&
                !HostTextureViewFormats.TryGetColorCopyFormat(42, out _) &&
                HostTextureViewFormats.IsComponentMapping(5768) &&
                !HostTextureViewFormats.IsComponentMapping(0) &&
                !HostTextureViewFormats.IsComponentMapping(0x1FFF), "reject depth, integer and malformed mappings");
        Console.WriteLine("Host RGBA8 backing and typed view contracts passed (CPU only).");
        Console.WriteLine("R77 sampled alpha and typeless color storage contracts passed (CPU only).");
    }

    private static byte[] View(uint format, uint dimension = 4, uint mip = 0, uint plane = 0)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, format);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), dimension);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), mip);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), plane);

        return bytes;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Host texture view contract failed: {description}.");
        }
    }
}
