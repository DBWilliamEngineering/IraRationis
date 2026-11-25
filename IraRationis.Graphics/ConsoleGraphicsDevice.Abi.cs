// SPDX-License-Identifier: GPL-3.0-or-later
// Known slot inventory based on the upstream device, list, queue, resource and DXGI tables.
using System.Globalization;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly object EvidenceGate = new();
    private static string? _evidencePath;

    private static readonly Dictionary<int, string> DeviceMethods = new()
    {
        [7] = "GetNodeCount", [8] = "CreateCommandQueue", [9] = "CreateCommandAllocator",
        [10] = "CreateGraphicsPipelineState", [11] = "CreateComputePipelineState", [12] = "CreateCommandList",
        [13] = "CheckFeatureSupport", [14] = "CreateDescriptorHeap", [15] = "GetDescriptorHandleIncrementSize",
        [16] = "CreateRootSignature", [17] = "CreateConstantBufferView", [18] = "CreateShaderResourceView",
        [19] = "CreateUnorderedAccessView", [20] = "CreateRenderTargetView", [21] = "CreateDepthStencilView",
        [22] = "CreateSampler", [23] = "CopyDescriptors", [24] = "CopyDescriptorsSimple",
        [25] = "GetResourceAllocationInfo", [26] = "GetCustomHeapProperties", [27] = "CreateCommittedResource",
        [28] = "CreateHeap", [29] = "CreatePlacedResource", [36] = "CreateFence", [38] = "GetCopyableFootprints",
        [39] = "CreateQueryHeap", [41] = "CreateCommandSignature", [48] = "CreateCommandQueueX",
        [57] = "UnknownPipelineCreation_0x1C8", [61] = "CreateSamplerX", [62] = "CreateComputePipelineX",
        [67] = "UnknownPipelineCreation_0x218", [69] = "DebugErrorFilter", [70] = "CreatePlacedSRV",
        [71] = "CreatePlacedUAV", [73] = "CreateCommandListX", [74] = "RegisterPagePool", [75] = "UnregisterPagePool",
        [76] = "CreateCommittedResourceX", [78] = "SetFrameInterval", [79] = "ScheduleFrameEvent",
        [80] = "WaitFrameEvent", [81] = "GetFrameStatistics", [86] = "CreatePipelineStateStream",
        [108] = "GetResourceAllocationInfo2", [127] = "CreateCommittedResource3", [133] = "CreateResourceAtAddress"
    };

    private static readonly Dictionary<int, string> ListMethods = new()
    {
        [7] = "GetDevice", [8] = "GetType", [9] = "Close", [10] = "Reset", [12] = "DrawInstanced",
        [13] = "DrawIndexedInstanced", [14] = "Dispatch", [15] = "CopyBufferRegion", [16] = "CopyTextureRegion",
        [17] = "CopyResource", [19] = "ResolveSubresource", [20] = "IASetPrimitiveTopology", [21] = "RSSetViewports",
        [22] = "RSSetScissorRects", [23] = "OMSetBlendFactor", [24] = "OMSetStencilRef", [25] = "SetPipelineState",
        [26] = "ResourceBarrier", [28] = "BeginQuery", [29] = "EndQuery", [30] = "ResolveQueryData",
        [32] = "SetDescriptorHeaps", [33] = "SetComputeRootSignature", [34] = "SetGraphicsRootSignature",
        [35] = "SetComputeRootDescriptorTable", [36] = "SetGraphicsRootDescriptorTable",
        [37] = "SetComputeRoot32BitConstant", [38] = "SetGraphicsRoot32BitConstant",
        [39] = "SetComputeRoot32BitConstants", [40] = "SetGraphicsRoot32BitConstants",
        [41] = "SetComputeRootConstantBufferView", [42] = "SetGraphicsRootConstantBufferView",
        [43] = "SetComputeRootShaderResourceView", [44] = "SetGraphicsRootShaderResourceView",
        [45] = "SetComputeRootUnorderedAccessView", [46] = "SetGraphicsRootUnorderedAccessView",
        [47] = "IASetIndexBuffer", [48] = "IASetVertexBuffers", [50] = "OMSetRenderTargets", [51] = "ClearDepthStencilView",
        [52] = "ClearRenderTargetView", [59] = "ExecuteIndirect", [60] = "CopyMemory", [68] = "DepthBounds",
        [69] = "Write32", [70] = "Write64", [71] = "WaitValue32", [72] = "WaitValue64", [73] = "PixelShaderDepthOrder",
        [78] = "DispatchX", [83] = "CopyTextureRegionX", [84] = "CopyResourceX", [98] = "DispatchMeshX",
        [99] = "DriverHint", [124] = "ShadingRate", [127] = "DispatchMesh", [129] = "TopOfPipeSync",
        [130] = "BottomOfPipeSync", [137] = "ResetX"
    };

    private static readonly Dictionary<int, string> QueueMethods = new()
    {
        [7] = "GetDevice", [10] = "ExecuteCommandLists", [11] = "SetMarker", [12] = "BeginEvent", [13] = "EndEvent",
        [14] = "Signal", [15] = "Wait", [16] = "GetTimestampFrequency", [26] = "GetDesc", [29] = "CopyPageMappings",
        [30] = "PresentX", [32] = "Signal2X", [33] = "ExecuteCommandListsX"
    };

    private static readonly Dictionary<int, string> ResourceMethods = new()
    {
        [7] = "GetDevice", [8] = "Map", [9] = "Unmap", [10] = "GetDesc", [11] = "GetGPUVirtualAddress", [14] = "GetHeapProperties"
    };

    private static readonly Dictionary<int, string> DescriptorHeapMethods = new() { [7] = "GetDevice", [8] = "GetDesc", [9] = "GetCPUDescriptorHandleForHeapStart", [10] = "GetGPUDescriptorHandleForHeapStart" };
    private static readonly Dictionary<int, string> HeapMethods = new() { [7] = "GetDevice", [8] = "GetDesc" };
    private static readonly Dictionary<int, string> FenceMethods = new()
    {
        [7] = "GetDevice", [8] = "GetCompletedValue", [9] = "SetEventOnCompletion", [10] = "Signal", [11] = "GetCreationFlags", [12] = "GetGPUVirtualAddress"
    };
    private static readonly Dictionary<int, string> AllocatorMethods = new() { [7] = "GetDevice", [8] = "Reset", [9] = "AllocationCallback" };
    private static readonly Dictionary<int, string> DxgiDeviceMethods = new() { [6] = "GetParent", [7] = "GetAdapter" };
    private static readonly Dictionary<int, string> DxgiAdapterMethods = new() { [6] = "GetParent", [7] = "EnumOutputs", [8] = "GetDesc" };
    private static readonly Dictionary<int, string> DxgiFactoryMethods = new() { [7] = "EnumAdapters", [12] = "EnumAdapters1" };
    private static readonly Dictionary<int, string> DxgiOutputMethods = new() { [6] = "GetParent", [21] = "GetDisplayModeListX" };

    private static string TableKind(nint table)
    {
        if (table == 0)
        {
            return "unknown-object";
        }
        return table == _deviceTable ? "device" : table == _queueOld ? "queue-old" :
            table == _queueNew ? "queue-new" : table == _allocatorTable ? "allocator" :
            table == _fenceTable ? "fence" : table == _listOld ? "list-old" : table == _listNew ? "list-new" :
            table == _resourceTable ? "resource" : table == _heapTable ? "heap" :
            table == _descriptorHeapTable ? "descriptor-heap" :
            table == _dxgiDeviceTable ? "dxgi-device" : table == _dxgiAdapterTable ? "dxgi-adapter" :
            table == _dxgiFactoryTable ? "dxgi-factory" : table == _dxgiOutputTable ? "dxgi-output" : "unknown-object";
    }

    private static Dictionary<int, string>? Inventory(string kind)
    {
        return kind switch
        {
            "device" => DeviceMethods,
            "list-old" or "list-new" => ListMethods,
            "queue-old" or "queue-new" => QueueMethods,
            "resource" => ResourceMethods,
            "heap" => HeapMethods,
            "descriptor-heap" => DescriptorHeapMethods,
            "fence" => FenceMethods,
            "allocator" => AllocatorMethods,
            "dxgi-device" => DxgiDeviceMethods,
            "dxgi-adapter" => DxgiAdapterMethods,
            "dxgi-factory" => DxgiFactoryMethods,
            "dxgi-output" => DxgiOutputMethods,
            _ => null
        };
    }

    private static string? MethodName(string kind, int index)
    {
        if (kind == "list-new")
        {
            switch (index)
            {
                case 142: return "UnknownState_0x470";
                case 146: return "EnhancedBarrierGroups";
                case 148: return "AllocateUpload";
            }
            if (index >= 61)
            {
                index -= 2;
            }
            else if (index >= 59)
            {
                return null;
            }
        }
        if (kind == "queue-new")
        {
            if (index >= 16)
            {
                index -= 2;
            }
            else if (index >= 14)
            {
                return null;
            }
        }
        var methods = Inventory(kind);
        return methods != null && methods.TryGetValue(index, out var method) ? method : null;
    }

    private static (nint Table, int Count)[] AllTables()
    {
        return [(_deviceTable, 160), (_queueOld, 36), (_queueNew, 36), (_listOld, 160), (_listNew, 160),
            (_allocatorTable, 24), (_fenceTable, 24), (_resourceTable, 32), (_heapTable, 24), (_descriptorHeapTable, 24),
            (_dxgiDeviceTable, 32), (_dxgiAdapterTable, 24), (_dxgiFactoryTable, 32), (_dxgiOutputTable, 32)];
    }

    // Preflight constructs tables only. No graphics device, title mapping or title callbacks.
    public static void Preflight(TextWriter writer, string reportDirectory)
    {
        lock (Gate)
        {
            if (_deviceTable == 0)
            {
                InitializeTables();
            }
        }
        Directory.CreateDirectory(reportDirectory);
        _evidencePath = Path.Combine(reportDirectory, "blocking-slots.tsv");
        var reportPath = Path.Combine(reportDirectory, "graphics-abi.tsv");
        var rows = new List<string> { "interface\tslot\toffset\tmethod\tstatus" };
        foreach (var (table, count) in AllTables())
        {
            var kind = TableKind(table);
            var registered = 0;
            var known = 0;
            var knownRegistered = 0;
            for (var index = 0; index < count; index++)
            {
                var pointer = ((nint*)table)[index];
                if (pointer == 0)
                {
                    throw new InvalidOperationException($"Null ABI slot {kind} 0x{index * 8:X}.");
                }
                var bound = pointer != MissingAddress(index);
                var method = MethodName(kind, index);
                if (bound)
                {
                    registered++;
                }
                if (method != null)
                {
                    known++;
                    if (bound)
                    {
                        knownRegistered++;
                    }
                }
                var cpuOnly = kind == "descriptor-heap" || kind == "resource" || kind == "heap" ||
                    (kind == "device" && index is 14 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 27 or 28 or 29 or 61 or 70 or 71 or 76 or 127 or 133);
                var status = !bound ? "missing" : cpuOnly ? "registered-cpu-only" : "registered-unverified";
                rows.Add($"{kind}\t{index}\t0x{index * 8:X}\t{method ?? "(unnamed)"}\t{status}");
            }
            writer.WriteLine($"[abi] {kind,-14}: {registered}/{count} registered; {knownRegistered}/{known} named methods registered.");
        }
        File.WriteAllLines(reportPath, rows);
        if (File.Exists(_evidencePath))
        {
            var seen = new HashSet<(string, int)>();
            foreach (var line in File.ReadLines(_evidencePath))
            {
                var fields = line.Split('\t');
                if (fields.Length < 4 || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || !seen.Add((fields[1], slot)))
                {
                    continue;
                }
                var current = AllTables().FirstOrDefault(item => TableKind(item.Table) == fields[1]);
                var status = current.Table != 0 && slot >= 0 && slot < current.Count && ((nint*)current.Table)[slot] != MissingAddress(slot) ? "now registered (unverified)" : "still missing/unknown";
                writer.WriteLine($"[abi] Previous blocker: {fields[1]} 0x{slot * 8:X} {fields[3]}: {status}.");
            }
        }
        writer.WriteLine($"[abi] Report: {reportPath}");
        writer.WriteLine("[abi] Coverage counts registrations, not functional parity or methods the selected title necessarily calls.");
    }

    private static void Trap(int index, nint first, nint second)
    {
        // Hidden aggregate return pointers can precede this. Do not dereference unrecognized arguments.
        if (!LiveObjects.TryGetValue(first, out var table))
        {
            _ = LiveObjects.TryGetValue(second, out table);
        }
        var kind = TableKind(table);
        var method = MethodName(kind, index) ?? "unknown method";
        var path = _evidencePath;
        if (path != null)
        {
            try
            {
                lock (EvidenceGate)
                {
                    File.AppendAllText(path, $"{DateTime.UtcNow:O}\t{kind}\t{index.ToString(CultureInfo.InvariantCulture)}\t{method}\n");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[abi] Could not persist blocker: {exception.Message}");
            }
        }
        Environment.FailFast($"[graphics] Unsupported console graphics {kind} slot 0x{index * 8:X} ({method}, C#).");
    }
}
