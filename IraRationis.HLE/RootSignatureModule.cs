// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-gpu/src/root.rs serialization entry points.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.HLE;

public static unsafe partial class RootSignatureModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int NotImplemented = unchecked((int)0x80004001);

    public static HleModule Create()
    {
        return new HleModule("d3d12-root-signatures", ["d3d12_xs.dll"],
        [
            new HleExport("D3D12ConfigureVirtualMemoryX",
                (nuint)(delegate* unmanaged[Stdcall]<VirtualMemory*, int>)&ConfigureVirtualMemory),
            new HleExport("D3D12SerializeRootSignature",
                (nuint)(delegate* unmanaged[Stdcall]<Description*, uint, nint*, nint*, int>)&Serialize),
            new HleExport("D3D12SerializeVersionedRootSignature",
                (nuint)(delegate* unmanaged[Stdcall]<VersionedDescription*, nint*, nint*, int>)&SerializeVersioned),
            new HleExport("D3D12CreateRootSignatureDeserializer",
                (nuint)(delegate* unmanaged[Stdcall]<byte*, nuint, Guid*, nint*, int>)&Deserialize)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ConfigureVirtualMemory(VirtualMemory* configuration)
    {
        if (configuration == null)
        {
            return InvalidArgument;
        }

        var information = new SystemInformation();

        GetSystemInfo(&information);

        configuration->Lowest = information.MinimumApplicationAddress;

        configuration->Highest = information.MaximumApplicationAddress;

        return 0;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void GetSystemInfo(SystemInformation* information);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Serialize(Description* description, uint version, nint* blob, nint* error)
    {
        if (description == null)
        {
            return InvalidArgument;
        }

        var host = *description;

        host.Flags &= 0x00FFFFFF;

        try
        {
            return D3D12SerializeRootSignature(&host, version, blob, error);
        }
        catch (DllNotFoundException)
        {
            return NotImplemented;
        }
        catch (EntryPointNotFoundException)
        {
            return NotImplemented;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SerializeVersioned(VersionedDescription* description, nint* blob, nint* error)
    {
        if (description == null)
        {
            return InvalidArgument;
        }

        var host = *description;

        host.Description.Flags &= 0x00FFFFFF;

        try
        {
            return D3D12SerializeVersionedRootSignature(&host, blob, error);
        }
        catch (DllNotFoundException)
        {
            return NotImplemented;
        }
        catch (EntryPointNotFoundException)
        {
            return NotImplemented;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Deserialize(byte* blob, nuint size, Guid* iid, nint* output)
    {
        if (blob == null || output == null)
        {
            return InvalidArgument;
        }

        try
        {
            return D3D12CreateRootSignatureDeserializer(blob, size, iid, output);
        }
        catch (DllNotFoundException)
        {
            return NotImplemented;
        }
        catch (EntryPointNotFoundException)
        {
            return NotImplemented;
        }
    }

    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D12SerializeRootSignature(Description* description, uint version, nint* blob,
        nint* error);

    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D12SerializeVersionedRootSignature(VersionedDescription* description, nint* blob,
        nint* error);

    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D12CreateRootSignatureDeserializer(byte* blob, nuint size, Guid* iid, nint* output);

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualMemory
    {
        public uint Flags;
        public uint PageTablePages;
        public uint PreservedBytes;
        public ulong Lowest;
        public ulong Highest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInformation
    {
        public uint ProcessorArchitecture;
        public uint PageSize;
        public nuint MinimumApplicationAddress;
        public nuint MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Description
    {
        public uint ParameterCount;
        public nint Parameters;
        public uint SamplerCount;
        public nint Samplers;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VersionedDescription
    {
        public uint Version;
        public Description Description;
    }
}
