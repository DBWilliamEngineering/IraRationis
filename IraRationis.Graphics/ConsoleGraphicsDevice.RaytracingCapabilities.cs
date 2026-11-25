// SPDX-License-Identifier: GPL-3.0-or-later

using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // This is a host capability, never a claim that guest ray tracing is implemented.
    private static uint _hostRaytracingTier;
    private static uint _hostRaytracingShaderModel;

    public static bool HostInlineRaytracingAvailable
    {
        get
        {
            return Volatile.Read(ref _hostRaytracingTier) >= 11 &&
                   Volatile.Read(ref _hostRaytracingShaderModel) >= 0x65;
        }
    }

    private static void DetectHostRaytracing()
    {
        var options = default(D3D12_FEATURE_DATA_D3D12_OPTIONS5);
        var check = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint, int>)Slot((nint)_host, 13);
        var result = check((nint)_host, (uint)D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS5,
            &options, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS5));
        var tier = result >= 0 ? (uint)options.RaytracingTier : 0U;
        Volatile.Write(ref _hostRaytracingTier, tier);

        var shaderModel = default(D3D12_FEATURE_DATA_SHADER_MODEL);
        shaderModel.HighestShaderModel = D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_5;
        var shaderResult = check((nint)_host, (uint)D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL,
            &shaderModel, (uint)sizeof(D3D12_FEATURE_DATA_SHADER_MODEL));
        var model = shaderResult >= 0 ? (uint)shaderModel.HighestShaderModel : 0U;
        Volatile.Write(ref _hostRaytracingShaderModel, model);

        Console.Error.WriteLine(
            $"[graphics] Host DXR tier={tier}; SM 6.5 query=0x{model:X}; inline ray tracing={HostInlineRaytracingAvailable}; feature HRESULT=0x{unchecked((uint)result):X8}; shader HRESULT=0x{unchecked((uint)shaderResult):X8}. Guest RT translation not implemented.");
    }
}
