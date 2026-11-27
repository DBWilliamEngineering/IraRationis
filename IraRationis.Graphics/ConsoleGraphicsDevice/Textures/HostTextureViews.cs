// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void WriteHostTextureShaderView(TextureSource source, nint host, D3D12_CPU_DESCRIPTOR_HANDLE handle)
    {
        var guest = ((ResourceObject*)source.Resource)->Description;

        if (source.Description == null && HostTextureViewFormats.RequiresExplicitDefaultView(guest))
        {
            // A null native SRV cannot inherit a typeless format. Preserve the
            // original guest's typed default and its full single-subresource extent.
            var view = new D3D12_SHADER_RESOURCE_VIEW_DESC
            {
                Format = (DXGI_FORMAT)guest.Format,
                ViewDimension = (D3D12_SRV_DIMENSION)4,
                Shader4ComponentMapping = 5768
            };

            view.Anonymous.Texture2D.MipLevels = guest.MipLevels;

            _host->CreateShaderResourceView((ID3D12Resource*)host, &view, handle);

            return;
        }

        fixed (byte* description = source.Description)
        {
            _host->CreateShaderResourceView((ID3D12Resource*)host,
                (D3D12_SHADER_RESOURCE_VIEW_DESC*)description, handle);
        }
    }
}
