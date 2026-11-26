// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using IraRationis.Cli.Diagnostics.Network.Models;

namespace IraRationis.Cli.Diagnostics.Network;

/// <summary>
/// Owns one bounded ETW session. Scope filters enable user-mode providers only
/// in the requested process; event filters select network-operation metadata.
/// </summary>
internal sealed unsafe partial class NetworkTraceSession : IDisposable
{
    private const uint FilterProcessId = 0x80000004;
    private const uint FilterEventId = 0x80000200;
    private const uint ControlStop = 1;
    private NetworkTraceProperties* _properties;
    private ulong _handle;
    private readonly string _name;

    internal string Name
    {
        get
        {
            return _name;
        }
    }

    internal NetworkTraceSession(string path)
    {
        if (!OperatingSystem.IsWindows() || sizeof(NetworkTraceProperties) != 120 || sizeof(NetworkTraceEnableParameters) != 48)
        {
            throw new PlatformNotSupportedException("Network tracing requires Windows x64.");
        }

        _name = $"IraRationis.Network.{Environment.ProcessId}.{Guid.NewGuid():N}";

        var nameBytes = checked((Name.Length + 1) * sizeof(char));
        var pathBytes = checked((path.Length + 1) * sizeof(char));
        var bufferBytes = checked(sizeof(NetworkTraceProperties) + nameBytes + pathBytes);

        _properties = (NetworkTraceProperties*)NativeMemory.AllocZeroed((nuint)bufferBytes);

        if (_properties == null)
        {
            throw new OutOfMemoryException();
        }

        _properties->Header.BufferSize = (uint)bufferBytes;
        _properties->Header.Guid = Guid.NewGuid();
        _properties->Header.ClientContext = 1; // QueryPerformanceCounter timestamps.
        _properties->Header.Flags = 0x00020000; // WNODE_FLAG_TRACED_GUID.
        _properties->BufferSize = 64;
        _properties->MinimumBuffers = 32;
        _properties->MaximumBuffers = 64;
        _properties->MaximumFileSize = 64;
        _properties->LogFileMode = 2; // EVENT_TRACE_FILE_MODE_CIRCULAR, in MiB.
        _properties->FlushTimer = 1;
        _properties->LoggerNameOffset = (uint)sizeof(NetworkTraceProperties);
        _properties->LogFileNameOffset = (uint)(sizeof(NetworkTraceProperties) + nameBytes);

        Name.AsSpan().CopyTo(new Span<char>((byte*)_properties + _properties->LoggerNameOffset, Name.Length));
        path.AsSpan().CopyTo(new Span<char>((byte*)_properties + _properties->LogFileNameOffset, path.Length));

        var result = StartTrace(out var handle, Name, _properties);

        if (result != 0)
        {
            NativeMemory.Free(_properties);
            _properties = null;

            throw new Win32Exception((int)result, $"Cannot start network trace ({result}). Run this diagnostic from an elevated terminal.");
        }

        _handle = handle;
    }

    internal void EnableNetworkEvents(uint processId)
    {
        // PID scope filters exclude kernel-mode AFD, so use user-mode Winsock.
        // No packet provider, HTTP headers, request bodies or TLS secrets enabled.
        Enable(new Guid("1c95126e-7eea-49a9-a3fe-a378b03ddb4d"), processId, [3006, 3008]);
        Enable(new Guid("55404e71-4db9-4deb-a5f5-8f86e46dde56"), processId,
            [1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014]);
        Enable(new Guid("bde46aea-2357-51fe-7367-d5296f530bd1"), processId,
            [1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 15, 16]);
    }

    private void Enable(Guid provider, uint processId, ReadOnlySpan<ushort> eventIds)
    {
        ObjectDisposedException.ThrowIf(_properties == null || _handle == 0, this);

        var eventFilterBytes = checked(4 + eventIds.Length * sizeof(ushort));
        var eventFilter = stackalloc byte[eventFilterBytes];

        eventFilter[0] = 1; // EVENT_FILTER_EVENT_ID.FilterIn.
        eventFilter[1] = 0;
        *(ushort*)(eventFilter + 2) = checked((ushort)eventIds.Length);
        eventIds.CopyTo(new Span<ushort>(eventFilter + 4, eventIds.Length));

        var filters = stackalloc NetworkTraceFilterDescriptor[2];

        filters[0] = new NetworkTraceFilterDescriptor
        {
            Pointer = (ulong)&processId,
            Size = sizeof(uint),
            Type = FilterProcessId
        };
        filters[1] = new NetworkTraceFilterDescriptor
        {
            Pointer = (ulong)eventFilter,
            Size = (uint)eventFilterBytes,
            Type = FilterEventId
        };

        var parameters = new NetworkTraceEnableParameters
        {
            Version = 2,
            Filters = filters,
            FilterCount = 2
        };
        var result = EnableTrace(_handle, &provider, 1, 5, ulong.MaxValue, 0, 5000, &parameters);

        if (result != 0)
        {
            throw new Win32Exception((int)result,
                $"Cannot enable filtered network provider {provider} ({result}). No unfiltered fallback is used.");
        }
    }

    internal NetworkTraceStatistics Stop()
    {
        ObjectDisposedException.ThrowIf(_properties == null, this);

        if (_handle != 0)
        {
            var result = ControlTrace(_handle, null, _properties, ControlStop);

            if (result != 0)
            {
                throw new Win32Exception((int)result,
                    $"Cannot stop network trace {Name} ({result}). Stop only this session with: logman stop {Name} -ets");
            }

            _handle = 0;
        }

        return new NetworkTraceStatistics(_properties->EventsLost, _properties->LogBuffersLost,
            _properties->BuffersWritten);
    }

    public void Dispose()
    {
        if (_properties == null)
        {
            return;
        }

        try
        {
            _ = Stop();
        }
        finally
        {
            NativeMemory.Free(_properties);
            _properties = null;
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint StartTrace(out ulong handle, string name, NetworkTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ControlTrace(ulong handle, string? name, NetworkTraceProperties* properties, uint control);

    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint EnableTrace(ulong handle, Guid* provider, uint control, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, NetworkTraceEnableParameters* parameters);
}
