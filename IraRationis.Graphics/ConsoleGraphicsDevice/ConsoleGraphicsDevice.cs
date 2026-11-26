// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI derived from Zettrox/ztrx victory-gpu: device, object, queue, fence, list and resource.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int NoInterface = unchecked((int)0x80004002);
    internal const int NotImplemented = unchecked((int)0x80004001);
    private const int PointerError = unchecked((int)0x80004003);

    // Host pointers are deliberately outside all console-visible fields.
    private const int QueueHost = 0x28;
    private const int AllocatorHost = 0x18;
    private const int FenceHost = 0x20;
    private const int ListHost = 0x960;
    private const int ListAllocator = 0x968;
    private const int ListClosed = 0x970;
    private const int ListEnhancedHost = 0x978;
    internal static readonly Lock Gate = new();
    private static nint _device;
    internal static ID3D12Device* _host;
    private static volatile bool _newer;
    private static readonly ConcurrentDictionary<nint, nint> LiveObjects = new();
    private static bool _tableInitializationStarted;
    private static nint _deviceTable, _queueOld, _queueNew, _allocatorTable, _fenceTable, _listOld, _listNew;
    private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid ObjectId = new("c4fec28f-7966-4e95-9f94-f431cb56c3b8");
    private static readonly Guid ChildId = new("905db94b-a00c-4140-9df5-2b64ca9ea357");
    private static readonly Guid PageableId = new("63ee58fb-1268-4835-86da-f008ce62f0d6");
    internal static readonly Guid DeviceId = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private static readonly Guid Device11Id = new("5405c344-d457-444e-b4dd-2366e45aee39");
    internal static readonly Guid QueueId = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
    internal static readonly Guid AllocatorId = new("6102dee4-af59-4b09-b999-b44d73f09b24");
    internal static readonly Guid FenceId = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    internal static readonly Guid HostListId = new("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
    private static readonly Guid[] DeviceIds =
    [
        DeviceId, Device11Id,
        new("77acce80-638e-4e65-8895-c1f23386863e"), new("30baa41e-b15b-475c-a0bb-1af5b6b64328"),
        new("81dadc15-2bad-4392-93c5-101345c4aa98"),
        new("e865df17-a9ee-46f9-a463-3098315aa2e5"), new("8b4f173b-2fea-4b80-8f58-4307191ab95d"),
        new("c70b221b-40e4-4a17-89af-025a0727a6dc"), new("5c014b53-68a1-4b9b-8bd1-dd6046b9358b"),
        new("728f1fa2-ea11-4a9f-9b52-9b42a4d5c9d5")
    ];
    private static readonly Guid[] ListIds =
    [
        HostListId,
        new("7116d91c-e7e4-47ce-b8c6-ec8168f437e5"), new("553103fb-1fe7-4557-bb38-946d7d0e7ca7"),
        new("38c3e585-ff17-412c-9150-4fc6f9d72a28"), new("6fda83a7-b84c-4e38-9ac8-c7bd22016b3d"),
        new("8754318e-d3a9-4541-98cf-645b50dc4874"), new("55050859-4024-474c-98cf-645b51dc4874"),
        new("c3827890-4024-474c-98cf-645b51dc4874")
    ];

    public static nint ExportAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)&Create;
        }
    }

    internal static nint Slot(nint value, int index)
    {
        return (*(nint**)value)[index];
    }

    private static nint Table(nint value)
    {
        return ((GraphicsObjectHeader*)value)->Table;
    }

    private static bool IsQueue(nint value)
    {
        return value != 0 && (Table(value) == _queueOld || Table(value) == _queueNew);
    }

    private static bool IsList(nint value)
    {
        return value != 0 && (Table(value) == _listOld || Table(value) == _listNew);
    }

    private static bool IsAllocator(nint value)
    {
        return value != 0 && Table(value) == _allocatorTable;
    }

    private static bool IsFence(nint value)
    {
        return value != 0 && Table(value) == _fenceTable;
    }

    private static nint Host(nint value)
    {
        if (value == _device)
        {
            return (nint)_host;
        }

        var offset = IsQueue(value) ? QueueHost : IsList(value) ? ListHost : IsFence(value) ? FenceHost : AllocatorHost;

        return *(nint*)(value + offset);
    }

    internal static uint HostRelease(nint value)
    {
        return ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value);
    }

    internal static uint Retain(nint value)
    {
        return (uint)Interlocked.Increment(ref ((GraphicsObjectHeader*)value)->References);
    }

    private static uint Drop(nint value)
    {
        ref var references = ref ((GraphicsObjectHeader*)value)->References;

        int observed;

        do
        {
            observed = Volatile.Read(ref references);

            if (observed <= 0)
            {
                return 0;
            }
        } while (Interlocked.CompareExchange(ref references, observed - 1, observed) != observed);

        switch (observed)
        {
            case 1 when IsCommandSignature(value):
                ReleaseCommandSignature(value);

                return 0;
            case 1 when IsQueryHeap(value):
                ReleaseQueryHeap(value);

                return 0;
            case 1 when IsPipeline(value):
                ReleasePipeline(value);

                return 0;
            case 1 when IsRootSignature(value):
                ReleaseRootSignature(value);

                return 0;
            case 1 when IsDescriptorHeap(value):
                ReleaseDescriptorHeap(value);

                return 0;
            case 1 when (IsResource(value) || IsHeap(value)):
                ReleaseResourceStorage(value);

                return 0;
            case 1 when value != _device && !IsDisplayObject(value):
            {
                var allocator = IsList(value) ? *(nint*)(value + ListAllocator) : 0;

                ReleaseCommandState(value);

                _ = LiveObjects.TryRemove(value, out _);

                if (IsList(value))
                {
                    var enhanced = *(nint*)(value + ListEnhancedHost);

                    if (enhanced != 0)
                    {
                        _ = HostRelease(enhanced);
                    }
                }

                _ = HostRelease(Host(value));

                NativeMemory.Free((void*)value);

                if (allocator != 0)
                {
                    _ = Drop(allocator);
                }

                break;
            }
        }

        return (uint)(observed - 1);
    }

    private static bool Knows(nint value, Guid id)
    {
        if (IsDisplayObject(value))
        {
            return DisplayKnows(value, id);
        }

        if (id == UnknownId || id == ObjectId)
        {
            return true;
        }

        if (value == _device)
        {
            return Array.IndexOf(DeviceIds, id) >= 0;
        }

        if (id == ChildId)
        {
            return true;
        }

        if (IsList(value))
        {
            return Array.IndexOf(ListIds, id) >= 0;
        }

        if (IsCommandSignature(value))
        {
            return id == CommandSignatureId || id == PageableId;
        }

        if (IsQueryHeap(value))
        {
            return id == QueryHeapId || id == PageableId;
        }

        if (IsPipeline(value))
        {
            return id == PipelineId || id == PageableId;
        }

        if (IsRootSignature(value))
        {
            return id == RootSignatureId;
        }

        if (IsDescriptorHeap(value))
        {
            return id == PageableId || id == DescriptorHeapId;
        }

        if (IsResource(value) || IsHeap(value))
        {
            return id == PageableId || (IsResource(value) && id == ResourceId) || (IsHeap(value) && id == HeapId);
        }

        return id == PageableId || (IsQueue(value) && id == QueueId) || (IsAllocator(value) && id == AllocatorId)
               || (IsFence(value) && id == FenceId);
    }

    private static int Answer(nint value, Guid* iid, nint* output)
    {
        if (output == null)
        {
            return PointerError;
        }

        *output = 0;

        if (iid == null || !Knows(value, *iid))
        {
            return NoInterface;
        }

        _ = Retain(value);

        *output = value;

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint self, Guid* iid, nint* output)
    {
        return QueryCore(self, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self)
    {
        return Retain(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        return Drop(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Parent(nint self, Guid* iid, nint* output)
    {
        return Answer(_device, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Name(nint self, char* name)
    {
        if (IsResource(self) || IsHeap(self) || IsDescriptorHeap(self) || IsRootSignature(self) || IsPipeline(self)
            || IsQueryHeap(self) || IsCommandSignature(self))
        {
            // Names are diagnostic metadata, not a GPU operation.
            return 0;
        }

        var host = Host(self);

        return ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(host, 6))(host, name);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPrivate(nint self, Guid* id, uint* size, void* data)
    {
        if (IsResource(self) || IsHeap(self) || IsDescriptorHeap(self) || IsRootSignature(self) || IsPipeline(self)
            || IsQueryHeap(self) || IsCommandSignature(self))
        {
            return GetResourcePrivate(self, id, size, data);
        }

        var host = Host(self);

        return id == null
            ? InvalidArgument
            : ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, void*, int>)Slot(host, 3))(host, id, size, data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetPrivate(nint self, Guid* id, uint size, void* data)
    {
        if (IsResource(self) || IsHeap(self) || IsDescriptorHeap(self) || IsRootSignature(self) || IsPipeline(self)
            || IsQueryHeap(self) || IsCommandSignature(self))
        {
            return SetResourcePrivate(self, id, size, data);
        }

        var host = Host(self);

        return id == null
            ? InvalidArgument
            : ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, void*, int>)Slot(host, 4))(host, id, size, data);
    }

    private static nint Allocate(nint table, int size, int hostOffset, nint host)
    {
        var child = (nint)NativeMemory.AllocZeroed((nuint)size);

        ((GraphicsObjectHeader*)child)->Table = table;

        ((GraphicsObjectHeader*)child)->Bits = 0;

        ((GraphicsObjectHeader*)child)->References = 1;

        *(nint*)(child + hostOffset) = host;

        LiveObjects[child] = table;

        return child;
    }

    private static int FinishChild(nint child, Guid* iid, nint* output)
    {
        if (iid == null || !Knows(child, *iid))
        {
            _ = Drop(child);

            return NoInterface;
        }

        if (output == null)
        {
            _ = Drop(child);

            return 1;
        }

        *output = child;

        return 0;
    }

    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDXGIFactory2(uint flags, Guid* iid, nint* output);

    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int D3D12CreateDevice(nint adapter, uint level, Guid* iid, nint* output);

    private static int InitializeHost()
    {
        var factoryId = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        var adapterId = new Guid("29038f61-3839-4626-91fd-086879011a05");
        var factory = (nint)0;
        var resultCode = CreateDXGIFactory2(0, &factoryId, &factory);

        if (resultCode < 0)
        {
            return resultCode;
        }

        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, nint*, int>)Slot(factory, 29);

            for (var index = 0U; ; index++)
            {
                var adapter = (nint)0;

                resultCode = enumerate(factory, index, 2, &adapterId, &adapter);

                if (resultCode < 0)
                {
                    return resultCode;
                }

                try
                {
                    var description = default(DXGI_ADAPTER_DESC1);

                    resultCode = ((delegate* unmanaged[Stdcall]<nint, DXGI_ADAPTER_DESC1*, int>)Slot(adapter, 10))(adapter,
                        &description);

                    if (resultCode < 0 || (description.Flags & 2) != 0)
                    {
                        continue;
                    }

                    var id = DeviceId;
                    var device = (nint)0;

                    resultCode = D3D12CreateDevice(adapter, 0xc000, &id, &device);

                    if (resultCode < 0)
                    {
                        continue;
                    }

                    _host = (ID3D12Device*)device;

                    var name = new string((char*)&description, 0, 128).TrimEnd('\0');

                    Console.Error.WriteLine($"[graphics] Host device created: {name} (C#).");

                    DetectHostRaytracing();

                    var graphicsCapabilities = IraRationis.Graphics.Device.Hardware.HostGraphicsCapabilities.Capture(
                        adapter, name, in description, _hostRaytracingTier, _hostRaytracingShaderModel);

                    IraRationis.Graphics.Device.Hardware.HostGraphicsCapabilityReport.WriteTo(Console.Error,
                        graphicsCapabilities);

                    return 0;
                }
                finally
                {
                    _ = HostRelease(adapter);
                }
            }
        }
        finally
        {
            _ = HostRelease(factory);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Create(nint adapter, uint* parameters, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (parameters == null || iid == null)
        {
            return InvalidArgument;
        }

        if (*parameters != 0x00020004 || (Array.IndexOf(DeviceIds, *iid) < 0 && *iid != UnknownId))
        {
            return NoInterface;
        }

        lock (Gate)
        {
            if (_device == 0)
            {
                var resultCode = InitializeHost();

                if (resultCode < 0)
                {
                    return resultCode;
                }

                if (_deviceTable == 0)
                {
                    InitializeTables();
                }

                _device = (nint)NativeMemory.AllocZeroed(0x58);

                ((GraphicsObjectHeader*)_device)->Table = _deviceTable;

                *(uint*)(_device + 0x18) = 1;

                LiveObjects[_device] = _deviceTable;
            }

            if (*iid == Device11Id)
            {
                _newer = true;
            }

            return output == null ? 1 : Answer(_device, iid, output);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint NodeCount(nint self)
    {
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Increment(nint self, uint type)
    {
        return DescriptorStride(type);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Feature(nint self, uint feature, void* data, uint size)
    {
        if (data == null)
        {
            return InvalidArgument;
        }

        var check = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint, int>)Slot((nint)_host, 13);

        if (feature == 0 && size == 64)
        {
            var values = stackalloc uint[15];
            var resultCode = check((nint)_host, feature, values, 60);

            if (resultCode < 0)
            {
                return resultCode;
            }

            for (var index = 0; index < 16; index++)
            {
                ((uint*)data)[index] = index == 11 ? 0U : values[index < 11 ? index : index - 1];
            }

            return 0;
        }

        if (feature != 1 || size != 16)
        {
            return (feature == 2 && size == 24) || (feature == 3 && size == 12) || (feature == 4 && size == 16)
                ? check((nint)_host, feature, data, size)
                : NotImplemented;
        }

        var fields = (uint*)data;

        if (fields[0] != 0)
        {
            return InvalidArgument;
        }

        fields[1] = 0;

        fields[2] = 1;

        fields[3] = 1;

        return 0;

    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static GraphicsHeapProperties* CustomHeap(GraphicsHeapProperties* output, nint self, uint nodes, uint kind)
    {
        if (output == null)
        {
            return output;
        }

        var pages = kind == 1 ? 1U : kind is 2 or 3 ? 3U : 0U;

        *output = new GraphicsHeapProperties
        {
            Kind = 4,
            CpuPages = pages,
            Pool = pages == 0 ? 0U : 1U,
            CreationNodes = Math.Max(1U, nodes),
            VisibleNodes = Math.Max(1U, nodes)
        };

        return output;
    }

    private static int NewQueue(GraphicsQueueDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null)
        {
            return InvalidArgument;
        }

        var id = QueueId;
        var host = (nint)0;
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, GraphicsQueueDescription*, Guid*, nint*, int>)Slot((nint)_host, 8))(
            (nint)_host, description, &id, &host);

        if (resultCode < 0)
        {
            return resultCode;
        }

        var child = Allocate(_newer ? _queueNew : _queueOld, 0x30, QueueHost, host);

        *(uint*)(child + 0x10) = description->Kind;

        *(GraphicsQueueDescription*)(child + 0x14) = *description;

        Console.Error.WriteLine($"[graphics] Command queue type {description->Kind} created.");

        return FinishChild(child, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateQueue(nint self, GraphicsQueueDescription* description, Guid* iid, nint* output)
    {
        return NewQueue(description, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateQueueX(nint self, uint* description, Guid* iid, nint* output)
    {
        if (description == null)
        {
            if (output != null)
            {
                *output = 0;
            }

            return InvalidArgument;
        }

        var translated = new GraphicsQueueDescription { Kind = description[0], Flags = description[1], Nodes = 1 };

        return NewQueue(&translated, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DescribeQueue(nint self, GraphicsQueueDescription* output)
    {
        if (output != null)
        {
            *output = *(GraphicsQueueDescription*)(self + 0x14);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateAllocator(nint self, uint kind, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        var id = AllocatorId;
        var host = (nint)0;
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot((nint)_host, 9))((nint)_host, kind,
            &id, &host);

        if (resultCode < 0)
        {
            return resultCode;
        }

        var child = Allocate(_allocatorTable, 0x20, AllocatorHost, host);

        *(uint*)(child + 0x10) = kind;

        return FinishChild(child, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AllocatorReset(nint self)
    {
        var host = Host(self);

        return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(host, 8))(host);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateFence(nint self, ulong initial, uint flags, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        var id = FenceId;
        var host = (nint)0;
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot((nint)_host, 36))(
            (nint)_host, initial, flags, &id, &host);

        if (resultCode < 0)
        {
            return resultCode;
        }

        var child = Allocate(_fenceTable, 0x28, FenceHost, host);

        *(ulong*)(child + 0x10) = initial;

        *(uint*)(child + 0x18) = flags;

        return FinishChild(child, iid, output);
    }

    private static ulong ReadFence(nint self)
    {
        var host = Host(self);
        var value = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(host, 8))(host);

        Volatile.Write(ref *(ulong*)(self + 0x10), value);

        return value;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong Completed(nint self)
    {
        return ReadFence(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FenceEvent(nint self, ulong value, nint handle)
    {
        var host = Host(self);

        return ((delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(host, 9))(host, value, handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FenceSignal(nint self, ulong value)
    {
        var host = Host(self);
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(host, 10))(host, value);

        if (resultCode >= 0)
        {
            _ = ReadFence(self);
        }

        return resultCode;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint FenceFlags(nint self)
    {
        return *(uint*)(self + 0x18);
    }

    private static int QueueFence(nint self, nint fence, ulong value, int slot)
    {
        if (!IsFence(fence))
        {
            return InvalidArgument;
        }

        var host = Host(self);

        return ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(host, slot))(host, Host(fence), value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Signal(nint self, nint fence, ulong value)
    {
        return QueueFence(self, fence, value, 14);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Wait(nint self, nint fence, ulong value)
    {
        return QueueFence(self, fence, value, 15);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Frequency(nint self, ulong* output)
    {
        var host = Host(self);

        return output == null
            ? InvalidArgument
            : ((delegate* unmanaged[Stdcall]<nint, ulong*, int>)Slot(host, 16))(host, output);
    }

    private static int NewList(uint nodes, uint kind, nint allocator, nint pipeline, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (!IsAllocator(allocator)
            || *(uint*)(allocator + 0x10) != kind
            || (pipeline != 0 && !IsPipeline(pipeline)))
        {
            return InvalidArgument;
        }

        var id = HostListId;
        var host = (nint)0;
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot((nint)_host, 12))(
            (nint)_host, nodes, kind, Host(allocator), 0, &id, &host);

        if (resultCode < 0)
        {
            return resultCode;
        }

        var child = Allocate(_newer ? _listNew : _listOld, 0x980, ListHost, host);

        *(uint*)(child + 0x10) = kind;

        *(nint*)(child + ListAllocator) = allocator;

        _ = Retain(allocator);

        if (pipeline != 0)
        {
            BindPipelineCore(child, pipeline);
        }

        TraceListCreation(kind);

        return FinishChild(child, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateList(nint self, uint nodes, uint kind, nint allocator, nint pipeline, Guid* iid,
        nint* output)
    {
        return NewList(nodes, kind, allocator, pipeline, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateListX(nint self, GraphicsListDescription* description, nint allocator, nint pipeline, Guid* iid,
        nint* output)
    {
        if (description != null)
        {
            return NewList(0, description->Kind, allocator, pipeline, iid, output);
        }

        if (output != null)
        {
            *output = 0;
        }

        return InvalidArgument;

        // The console node selection and barrier batch tune its own command memory.
        // A single-node host uses node mask 0.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ListType(nint self)
    {
        return *(uint*)(self + 0x10);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CloseList(nint self)
    {
        var host = Host(self);
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(host, 9))(host);

        if (resultCode >= 0)
        {
            *(int*)(self + ListClosed) = 1;
        }

        return resultCode;
    }

    private static int ResetListCore(nint self, nint allocator, nint pipeline)
    {
        if (!IsAllocator(allocator)
            || *(uint*)(allocator + 0x10) != *(uint*)(self + 0x10)
            || (pipeline != 0 && !IsPipeline(pipeline)))
        {
            return InvalidArgument;
        }

        var host = Host(self);
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(host, 10))(host, Host(allocator), 0);

        if (resultCode < 0)
        {
            return resultCode;
        }

        _ = Retain(allocator);

        var previous = *(nint*)(self + ListAllocator);

        *(nint*)(self + ListAllocator) = allocator;

        _ = Drop(previous);

        *(int*)(self + ListClosed) = 0;

        ReleaseCommandState(self);

        if (pipeline != 0)
        {
            BindPipelineCore(self, pipeline);
        }

        return resultCode;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResetList(nint self, nint allocator, nint pipeline)
    {
        return ResetListCore(self, allocator, pipeline);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResetListX(nint self, nint allocator, nint pipeline, uint flags)
    {
        return ResetListCore(self, allocator, pipeline);
    }

    private static void Submit(nint self, uint count, nint* lists)
    {
        if (count == 0)
        {
            return;
        }

        if (lists == null || count > 65536)
        {
            Environment.FailFast("[graphics] Invalid command list submission.");
        }

        var length = checked((int)count);
        var hosts = length <= 128 ? stackalloc nint[length] : new nint[length];

        for (var index = 0; index < length; index++)
        {
            var list = lists[index];

            if (!IsList(list) || *(int*)(list + ListClosed) == 0 || *(uint*)(list + 0x10) != *(uint*)(self + 0x10))
            {
                Environment.FailFast("[graphics] Invalid or open command list submitted.");
            }

            hosts[index] = Host(list);
        }

        var host = Host(self);
        var hasMemory = false;

        for (var index = 0; index < length; index++)
        {
            hasMemory |= HasCommandMemory(lists[index]);
        }

        if (hasMemory)
        {
            for (var index = 0; index < length; index++)
            {
                SubmitWithCommandMemory(host, lists[index], hosts[index]);
            }

            return;
        }

        fixed (nint* values = hosts)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(host, 10))(host, count, values);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Execute(nint self, uint count, nint* lists)
    {
        Submit(self, count, lists);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ExecuteX(nint self, uint count, nint* lists, ulong token, uint flags)
    {
        Submit(self, count, lists);
    }

    private static nint* NewTable(int size)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)size, (nuint)sizeof(nint));

        for (var index = 0; index < size; index++)
        {
            table[index] = MissingAddress(index);
        }

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, uint*, void*, int>)&GetPrivate;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, uint, void*, int>)&SetPrivate;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, int>)&Name;

        return table;
    }

    private static nint* ChildTable(int size)
    {
        var table = NewTable(size);

        table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Parent;

        return table;
    }

    private static void InitializeTables()
    {
        // Both callers hold Gate. Never reuse or rebuild partially initialized tables.
        if (_tableInitializationStarted)
        {
            throw new InvalidOperationException(
                "Graphics ABI initialization previously started but did not complete. Restart the process.");
        }

        _tableInitializationStarted = true;

        if (sizeof(GraphicsObjectHeader) != 16 || sizeof(GraphicsQueueDescription) != 16 || sizeof(GraphicsListDescription) != 16
            || sizeof(GraphicsHeapProperties) != 20)
        {
            Environment.FailFast("Unexpected console graphics ABI structure size.");
        }

        var device = NewTable(160);

        device[7] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&NodeCount;
        device[8] = (nint)(delegate* unmanaged[Stdcall]<nint, GraphicsQueueDescription*, Guid*, nint*, int>)&CreateQueue;
        device[9] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)&CreateAllocator;
        device[12] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)&CreateList;
        device[13] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void*, uint, int>)&Feature;
        device[15] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint>)&Increment;
        device[26] =
            (nint)(delegate* unmanaged[Stdcall]<GraphicsHeapProperties*, nint, uint, uint, GraphicsHeapProperties*>)&CustomHeap;

        device[36] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)&CreateFence;
        device[48] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)&CreateQueueX;

        device[73] =
            (nint)(delegate* unmanaged[Stdcall]<nint, GraphicsListDescription*, nint, nint, Guid*, nint*, int>)&CreateListX;

        RegisterFrameMethods(device);
        RegisterResourceMethods(device);
        RegisterDescriptorMethods(device);
        RegisterRootSignatureMethods(device);
        RegisterComputePipelineMethods(device);
        RegisterQueryHeapMethods(device);
        RegisterCommandSignatureMethods(device);
        RegisterFootprintMethods(device);
        RegisterGraphicsPipelineMethods(device);
        RegisterOptionalDeviceMethods(device);

        _queueOld = (nint)BuildQueue(false);

        _queueNew = (nint)BuildQueue(true);

        var allocator = ChildTable(24);

        allocator[8] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&AllocatorReset;
        allocator[9] = AllocationCallbackAddress;

        _allocatorTable = (nint)allocator;

        var fence = ChildTable(24);

        fence[8] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&Completed;
        fence[9] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)&FenceEvent;
        fence[10] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&FenceSignal;
        fence[11] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&FenceFlags;

        _fenceTable = (nint)fence;

        _listOld = (nint)BuildList(false);

        _listNew = (nint)BuildList(true);

        InitializeDisplayObjects();

        ValidateTables(AllTables((nint)device));
        ValidateRootSignatureRegistration(device);
        ValidateComputePipelineRegistration(device);
        ValidateQueryHeapRegistration(device);
        ValidateDeviceBatchRegistration(device);
        ValidateOptionalDeviceRegistration(device);
        ValidateCommandStateRegistration();
        ValidateCommandMemoryRegistration();
        ValidateBarrierRegistration();
        ValidateKnownListRegistration();

        _deviceTable = (nint)device;
    }

    private static nint* BuildQueue(bool newer)
    {
        var table = ChildTable(36);
        var shift = newer ? 2 : 0;

        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)&Execute;

        table[14 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)&Signal;

        table[15 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)&Wait;

        table[16 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong*, int>)&Frequency;

        table[26 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, GraphicsQueueDescription*, void>)&DescribeQueue;

        table[33 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, ulong, uint, void>)&ExecuteX;

        return table;
    }

    private static nint* BuildList(bool newer)
    {
        var table = ChildTable(160);

        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListType;
        table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&CloseList;
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&ResetList;

        RegisterRasterMethods(table);
        RegisterCommandStateMethods(table);
        RegisterCommandMemoryMethods(table, newer);
        RegisterBarrierMethods(table, newer);
        RegisterCommandOperations(table, newer);

        table[newer ? 131 : 129] = PipelineSyncAddress;

        table[newer ? 132 : 130] = PipelineSyncAddress;

        table[newer ? 139 : 137] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, uint, int>)&ResetListX;

        return table;
    }
}
