// SPDX-License-Identifier: GPL-3.0-or-later
// Console DXGI object layouts derived from Zettrox/ztrx victory-gpu/src/dxgi.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int DxgiNotFound = unchecked((int)0x887A0002);
    private const int DxgiMoreData = unchecked((int)0x887A0003);
    private static nint _dxgiDevice, _dxgiAdapter, _dxgiFactory, _dxgiOutput;
    private static nint _dxgiDeviceTable, _dxgiAdapterTable, _dxgiFactoryTable, _dxgiOutputTable;
    private static readonly Guid[] DxgiDeviceIds =
    [
        new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), new("77db970f-6276-48ba-ba28-070143b4392c"),
        new("05008617-fbfd-4051-a790-144884b4f6a9")
    ];
    private static readonly Guid[] DxgiAdapterIds =
    [
        new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), new("29038f61-3839-4626-91fd-086879011a05"),
        new("0aa1ae0a-fa0e-4b84-8644-e05ff8e5acb5")
    ];
    private static readonly Guid[] DxgiFactoryIds =
    [
        new("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), new("770aae78-f26f-4dba-a829-253c83d1b387"),
        new("50c83a1c-e072-4c48-87b0-3630fa36a6d0")
    ];
    private static readonly Guid[] DxgiOutputIds = [new("ae02eedb-c735-4690-8d52-5a8dc20213aa")];

    private static bool IsDisplayObject(nint value)
    {
        return value != 0 && (value == _dxgiDevice || value == _dxgiAdapter || value == _dxgiFactory
                              || value == _dxgiOutput);
    }

    private static bool DisplayKnows(nint value, Guid id)
    {
        if (id == UnknownId)
        {
            return true;
        }

        var ids = value == _dxgiDevice ? DxgiDeviceIds :
            value == _dxgiAdapter ? DxgiAdapterIds :
            value == _dxgiFactory ? DxgiFactoryIds : DxgiOutputIds;

        return Array.IndexOf(ids, id) >= 0;
    }

    private static int QueryCore(nint self, Guid* iid, nint* output)
    {
        if (iid != null && self == _device && Array.IndexOf(DxgiDeviceIds, *iid) >= 0)
        {
            return Answer(_dxgiDevice, iid, output);
        }

        if (iid != null && self == _dxgiDevice && (Array.IndexOf(DeviceIds, *iid) >= 0 || *iid == UnknownId))
        {
            return Answer(_device, iid, output);
        }

        return Answer(self, iid, output);
    }

    private static nint* DisplayTable(int size)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)size, (nuint)sizeof(nint));
        for (var index = 0; index < size; index++)
        {
            table[index] = MissingAddress(index);
        }

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&DisplayParent;

        return table;
    }

    private static nint NewDisplayObject(nint table)
    {
        var value = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Header));
        ((Header*)value)->Table = table;
        LiveObjects[value] = table;

        return value;
    }

    private static void InitializeDisplayObjects()
    {
        if (sizeof(AdapterDescription) != 0x130 || sizeof(DisplayMode) != 32)
        {
            Environment.FailFast("[graphics] Invalid console DXGI ABI structure size.");
        }

        var device = DisplayTable(32);
        device[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&DisplayAdapter;
        _dxgiDeviceTable = (nint)device;
        _dxgiDevice = NewDisplayObject(_dxgiDeviceTable);

        var adapter = DisplayTable(24);
        adapter[7] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&DisplayOutputs;
        adapter[8] = (nint)(delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)&DescribeAdapter;
        _dxgiAdapterTable = (nint)adapter;
        _dxgiAdapter = NewDisplayObject(_dxgiAdapterTable);

        var factory = DisplayTable(32);
        factory[7] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&DisplayAdapters;
        factory[12] = factory[7];
        _dxgiFactoryTable = (nint)factory;
        _dxgiFactory = NewDisplayObject(_dxgiFactoryTable);

        var output = DisplayTable(32);
        output[21] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, DisplayMode*, int>)&DisplayModes;
        _dxgiOutputTable = (nint)output;
        _dxgiOutput = NewDisplayObject(_dxgiOutputTable);

        // InitializeTables has already constructed both queue tables and holds Gate.
        RegisterPresentationMethods();
        ValidatePresentationRegistration();
        RegisterFenceAddressMethods();
        ValidateFenceAddressRegistration();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayParent(nint self, Guid* iid, nint* output)
    {
        var parent = self == _dxgiAdapter ? _dxgiFactory :
            self == _dxgiDevice || self == _dxgiOutput ? _dxgiAdapter : 0;
        if (parent == 0)
        {
            if (output != null)
            {
                *output = 0;
            }

            return NoInterface;
        }

        return Answer(parent, iid, output);
    }

    private static int EnumerateDisplay(nint value, uint index, nint* output)
    {
        if (output == null)
        {
            return PointerError;
        }

        *output = 0;
        if (index != 0)
        {
            return DxgiNotFound;
        }

        _ = Retain(value);
        *output = value;

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayAdapter(nint self, nint* output)
    {
        return EnumerateDisplay(_dxgiAdapter, 0, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayAdapters(nint self, uint index, nint* output)
    {
        return EnumerateDisplay(_dxgiAdapter, index, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayOutputs(nint self, uint index, nint* output)
    {
        return EnumerateDisplay(_dxgiOutput, index, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DescribeAdapter(nint self, AdapterDescription* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = default;
        var name = "Ira Rationis console GPU";
        for (var index = 0; index < name.Length; index++)
        {
            output->Name[index] = name[index];
        }

        var (gpu, standard) = ConsoleConfiguration.GetTitleMemory(ConsoleConfiguration.Current);
        output->Vendor = 0x1002;
        output->DedicatedVideoMemory = (nuint)gpu;
        output->SharedSystemMemory = (nuint)standard;
        output->LuidLow = 1;

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayModes(nint self, uint format, uint flags, uint* count, DisplayMode* output)
    {
        if (count == null)
        {
            return InvalidArgument;
        }

        if (output == null)
        {
            *count = 1;

            return 0;
        }

        if (*count < 1)
        {
            *count = 1;

            return DxgiMoreData;
        }

        *count = 1;
        // One logical 4K60 console mode, matching the upstream default.
        // This is not a host monitor enumeration or an actual swap chain.
        *output = new DisplayMode
            { Width = 3840, Height = 2160, RefreshNumerator = 60, RefreshDenominator = 1, Scanlines = 1 };

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Name[128];
        public uint Vendor;
        public uint Device;
        public uint Subsystem;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayMode
    {
        public uint Width;
        public uint Height;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint Format;
        public uint Scanlines;
        public uint Scaling;
        public uint Flags;
    }
}
