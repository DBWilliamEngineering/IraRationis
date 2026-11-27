// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int PlanarColorVertexOffset = 768;
    private const int PlanarColorSharedConstantBytes = 132;
    private const int PlanarColorLookupEnableOffset = 0x70;

    private static bool RejectPlanarColorDraw(string reason)
    {
        Console.Error.WriteLine($"[graphics] planar-color draw REFUSED: {reason}.");

        return false;
    }

    private static bool TryGetPlanarColorSources(RecordedCommandState state, ulong table,
        bool srgbTexture, out TextureSource? transform, out PlanarColorTextureBinding[] textures)
    {
        textures = [];

        if (!TryGetTextureSource(state, table, out transform) || transform == null ||
            !TryGetTextureBuffer(transform, out _))
        {
            return RejectPlanarColorDraw("table+0 requires a live transform buffer SRV");
        }

        state.RetainObject(transform.Resource);

        if (srgbTexture)
        {
            if (!TryGetTextureSource(state, table + 0x20, out var texture) || texture == null ||
                !TryGetTextureView(texture, out _) ||
                !TryGetColorLutConstants(table + 0x40, TextureSecondTransformOffset + TextureTransformBytes, out _))
            {
                return RejectPlanarColorDraw(
                    "sRGB texture-strip requires Texture2D at table+0x20 and transform CBV at table+0x40");
            }

            state.RetainObject(texture.Resource);

            if (MaterializeHostResource(texture.Resource, out var host) < 0 || host == 0)
            {
                return RejectPlanarColorDraw("cannot materialize sRGB texture-strip Texture2D");
            }

            textures = [new PlanarColorTextureBinding(texture, host)];

            return true;
        }

        var bindings = new List<PlanarColorTextureBinding>(4);

        for (var planeIndex = 0; planeIndex < 3; planeIndex++)
        {
            var handle = table + (uint)(planeIndex + 1) * 32;

            if (!TryGetTextureSource(state, handle, out var source) || source == null ||
                !TryGetTextureView(source, out _))
            {
                return RejectPlanarColorDraw($"table+0x{(planeIndex + 1) * 32:X} requires a supported Texture2D plane");
            }

            state.RetainObject(source.Resource);

            if (MaterializeHostResource(source.Resource, out var host) < 0 || host == 0)
            {
                return RejectPlanarColorDraw($"cannot materialize Texture2D plane {planeIndex}");
            }

            bindings.Add(new PlanarColorTextureBinding(source, host));
        }

        if (TryGetTextureSource(state, table + 0x80, out var lookup) && lookup != null &&
            IsPlanarColorLookupView(lookup))
        {
            state.RetainObject(lookup.Resource);

            if (MaterializeHostResource(lookup.Resource, out var host) < 0 || host == 0)
            {
                return RejectPlanarColorDraw("cannot materialize the optional Texture3D color lookup");
            }

            bindings.Add(new PlanarColorTextureBinding(lookup, host));
        }

        textures = bindings.ToArray();

        return true;
    }

    private static bool IsPlanarColorLookupView(TextureSource source)
    {
        var description = ((ResourceObject*)source.Resource)->Description;

        if (description.Dimension != 4 || description.MipLevels != 1 || description.SampleCount != 1 ||
            description.Width is < 33 or > 40 || description.Height is < 33 or > 40 ||
            description.DepthOrArraySize != 33 || (description.Flags & 8U) != 0 ||
            description.Format is not (2 or 10 or 11 or 24 or 28))
        {
            return false;
        }

        if (source.Description is not { } bytes)
        {
            return true;
        }

        return bytes.Length == 40 && ReadDescriptorUInt32(bytes, 0) == description.Format &&
               ReadDescriptorUInt32(bytes, 4) == 8 && ReadDescriptorUInt32(bytes, 16) == 0 &&
               ReadDescriptorUInt32(bytes, 20) is 1 or uint.MaxValue &&
               ReadDescriptorUInt32(bytes, 24) == 0;
    }

    private static void WritePlanarColorViews(nint heap, PlanarColorTextureBinding[] textures, bool srgbTexture)
    {
        var handle = ((ID3D12DescriptorHeap*)heap)->GetCPUDescriptorHandleForHeapStart();
        var increment = _host->GetDescriptorHandleIncrementSize(0);

        for (var textureIndex = 0; textureIndex < 4; textureIndex++)
        {
            if (textureIndex < textures.Length)
            {
                var binding = textures[textureIndex];

                WriteHostTextureShaderView(binding.Source, binding.Host, handle);
            }
            else
            {
                var emptyLookup = new D3D12_SHADER_RESOURCE_VIEW_DESC
                {
                    Format = (DXGI_FORMAT)28,
                    ViewDimension = (D3D12_SRV_DIMENSION)(srgbTexture ? 4 : 8),
                    Shader4ComponentMapping = 5768
                };

                if (srgbTexture)
                {
                    emptyLookup.Anonymous.Texture2D.MipLevels = 1;
                }
                else
                {
                    emptyLookup.Anonymous.Texture3D.MipLevels = 1;
                }

                _host->CreateShaderResourceView(null, &emptyLookup, handle);
            }

            handle.ptr += increment;
        }
    }

    private static void TransitionPlanarColorTextures(nint list, PlanarColorTextureBinding[] textures,
        uint before, uint after)
    {
        for (var textureIndex = 0; textureIndex < textures.Length; textureIndex++)
        {
            if (!HasEarlierPlanarColorHost(textures, textureIndex))
            {
                TransitionHostResource(list, textures[textureIndex].Host, before, after);
            }
        }
    }

    private static bool HasEarlierPlanarColorHost(PlanarColorTextureBinding[] textures, int textureIndex)
    {
        // O(1): the verified root contract contains at most four texture bindings.
        for (var previousIndex = 0; previousIndex < textureIndex; previousIndex++)
        {
            if (textures[previousIndex].Host == textures[textureIndex].Host)
            {
                return true;
            }
        }

        return false;
    }
}
