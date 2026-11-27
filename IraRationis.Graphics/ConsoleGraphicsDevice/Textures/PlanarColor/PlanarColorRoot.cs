// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static byte[]? SerializePlanarColorRoot(TextWriter report)
    {
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[4];

        for (var parameterIndex = 0; parameterIndex < 3; parameterIndex++)
        {
            parameters[parameterIndex] = new D3D12_ROOT_PARAMETER1
            {
                ParameterType = (D3D12_ROOT_PARAMETER_TYPE)2,
                ShaderVisibility = (D3D12_SHADER_VISIBILITY)(parameterIndex == 2 ? 0 : 1)
            };

            parameters[parameterIndex].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1
            {
                ShaderRegister = (uint)parameterIndex
            };
        }

        var range = new D3D12_DESCRIPTOR_RANGE1
        {
            RangeType = 0,
            NumDescriptors = 4,
            BaseShaderRegister = 0,
            OffsetInDescriptorsFromTableStart = 0
        };

        parameters[3] = new D3D12_ROOT_PARAMETER1
        {
            ParameterType = 0,
            ShaderVisibility = (D3D12_SHADER_VISIBILITY)5
        };

        parameters[3].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &range
        };

        var sampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = (D3D12_FILTER)0x14,
            AddressU = (D3D12_TEXTURE_ADDRESS_MODE)3,
            AddressV = (D3D12_TEXTURE_ADDRESS_MODE)3,
            AddressW = (D3D12_TEXTURE_ADDRESS_MODE)3,
            MaxAnisotropy = 1,
            ComparisonFunc = (D3D12_COMPARISON_FUNC)1,
            MinLOD = 0,
            MaxLOD = 12,
            ShaderVisibility = (D3D12_SHADER_VISIBILITY)5
        };

        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC
        {
            Version = (D3D_ROOT_SIGNATURE_VERSION)2
        };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 4,
            pParameters = parameters,
            NumStaticSamplers = 1,
            pStaticSamplers = &sampler,
            Flags = (D3D12_ROOT_SIGNATURE_FLAGS)1
        };

        nint blob = 0;
        nint errors = 0;

        try
        {
            var resultCode = SerializeRootNative(&description, &blob, &errors);

            report.WriteLine($"[graphics] planar-color root serialization HRESULT=0x{resultCode:X8}.");

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
}
