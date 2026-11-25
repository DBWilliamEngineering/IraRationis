// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong MaximumDiagnosticMessages = 32;
    private const int MaximumDiagnosticMessageBytes = 65536;
    private static readonly Guid HostDebugId = new("344488b7-6846-474b-b989-f027448245e0");
    private static readonly Guid HostInfoQueueId = new("0742a90b-c387-483f-b946-30a7e4e61458");

    private static int _hostValidationStarted;
    private static int _r3BoundaryReported;

    // Only instrument opt-in validation runs; static operation names are allocation-free.
    private static readonly bool TraceHostRecording = Environment.GetEnvironmentVariable("IRX_D3D12_DEBUG") == "1";

    // The host debug layer must be enabled before the first host device is created.
    // This also supports checkouts that never installed the R2 InitializeHost hook.
    static ConsoleGraphicsDevice()
    {
        EnableHostValidation();
    }

    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D12GetDebugInterface(Guid* iid, nint* output);

    private static void ReportR3BoundaryOnce()
    {
        if (Volatile.Read(ref _r3BoundaryReported) == 0 && Interlocked.Exchange(ref _r3BoundaryReported, 1) == 0)
        {
            Console.Error.WriteLine("[graphics] IRX-GFX-CLOSE-R3 active: typed Windows clear and close dispatch.");
        }
    }

    private static void EnableHostValidation()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("IRX_D3D12_DEBUG") != "1" ||
            Interlocked.Exchange(ref _hostValidationStarted, 1) != 0)
        {
            return;
        }

        var iid = HostDebugId;
        nint debug = 0;
        var hr = D3D12GetDebugInterface(&iid, &debug);
        if (hr < 0 || debug == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] D3D12 debug layer unavailable: 0x{hr:X8}. Install Windows Graphics Tools.");

            return;
        }

        try
        {
            // Windows ID3D12Debug::EnableDebugLayer, before device creation.
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(debug, 3))(debug);
            Console.Error.WriteLine("[graphics] D3D12 debug layer enabled (CPU validation).");
        }
        finally
        {
            _ = HostRelease(debug);
        }
    }

    private static void TraceHostCommand(nint self, string operation)
    {
        if (!TraceHostRecording)
        {
            return;
        }

        var state = StateFor(self);
        state.LastHostCommand = operation;
        state.HostCommandCount++;
    }

    private static void ReportHostCloseFailure(nint self, int hr, string boundary)
    {
        var count = CommandStates.TryGetValue(self, out var state) ? state.MemoryBoundaries.Count : 0;
        Console.Error.WriteLine(
            $"[graphics] Host Close failed: HRESULT=0x{hr:X8}, guest=0x{self:X}, host=0x{Host(self):X}, kind={*(uint*)(self + 0x10)}, segment={count}, boundary={boundary}.");
        if (state != null)
        {
            Console.Error.WriteLine(
                $"[graphics] R3 host recording: last={state.LastHostCommand ?? "none"}, tracedCalls={state.HostCommandCount}, boundaryCount={state.MemoryBoundaries.Count}.");
        }

        if (_host == null)
        {
            return;
        }

        var device = (nint)_host;
        var iid = HostInfoQueueId;
        nint queue = 0;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(device, 0);
        if (query(device, &iid, &queue) < 0 || queue == 0)
        {
            Console.Error.WriteLine("[graphics] No D3D12 information queue; rerun with IRX_D3D12_DEBUG=1.");

            return;
        }

        try
        {
            // Device-wide queue: other recording threads can contribute messages.
            var available = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(queue, 9))(queue);
            var getMessage = (delegate* unmanaged[Stdcall]<nint, ulong, HostDebugMessage*, nuint*, int>)Slot(queue, 5);
            Console.Error.WriteLine(
                $"[graphics] D3D12 device-wide validation messages: {available}; showing up to {MaximumDiagnosticMessages} most recent.");
            for (var index = available > MaximumDiagnosticMessages ? available - MaximumDiagnosticMessages : 0;
                 index < available;
                 index++)
            {
                nuint bytes = 0;
                if (getMessage(queue, index, null, &bytes) < 0 || bytes < (nuint)sizeof(HostDebugMessage)
                                                               || bytes > MaximumDiagnosticMessageBytes)
                {
                    continue;
                }

                var message = (HostDebugMessage*)NativeMemory.Alloc(bytes);
                if (message == null)
                {
                    break;
                }

                try
                {
                    if (getMessage(queue, index, message, &bytes) >= 0 && message->Description != 0 &&
                        message->DescriptionBytes > 0 && message->DescriptionBytes <= MaximumDiagnosticMessageBytes)
                    {
                        var description = Marshal
                            .PtrToStringAnsi(message->Description, checked((int)message->DescriptionBytes))
                            ?.TrimEnd('\0');
                        Console.Error.WriteLine(
                            $"[graphics] D3D12 validation: id={message->Id}, severity={message->Severity}, category={message->Category}: {description}");
                    }
                }
                finally
                {
                    NativeMemory.Free(message);
                }
            }
        }
        finally
        {
            _ = HostRelease(queue);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostDebugMessage
    {
        public int Category;
        public int Severity;
        public int Id;
        public nint Description;
        public nuint DescriptionBytes;
    }
}
