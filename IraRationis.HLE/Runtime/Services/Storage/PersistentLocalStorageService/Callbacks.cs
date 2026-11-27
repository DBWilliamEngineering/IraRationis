// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Services.Storage.Models;

namespace IraRationis.HLE.Runtime.Services.Storage.PersistentLocalStorageService;

internal sealed unsafe partial class PersistentLocalStorageService
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPathSize(nint self, nuint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;
        var errors = NativeErrorState.Capture();

        try
        {
            var service = RuntimeServiceObject.Owner<PersistentLocalStorageService>(self);
            *output = (nuint)service._pathBytes.Length;
            service._trace.Write("PersistentStorage.GetPathSize", $"bytes={*output}; includesTerminator=True");

            return RuntimeServiceResult.Success;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
        finally
        {
            errors.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPath(nint self, nuint capacity, byte* output, nuint* used)
    {
        if (used != null)
        {
            *used = 0;
        }

        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        var errors = NativeErrorState.Capture();

        try
        {
            var service = RuntimeServiceObject.Owner<PersistentLocalStorageService>(self);
            var required = service._pathBytes.Length;

            if (capacity < (nuint)required)
            {
                service._trace.Write("PersistentStorage.GetPath", $"capacity={capacity}; required={required}; hresult=0x80004005");

                return RuntimeServiceResult.Failure;
            }

            try
            {
                service._directory.EnsureExists();
                service._pathBytes.AsSpan().CopyTo(new Span<byte>(output, required));

                if (used != null)
                {
                    *used = (nuint)required;
                }

                service._trace.Write("PersistentStorage.GetPath", $"bytes={required}; backedByHostDirectory=True; hresult=0x00000000");

                return RuntimeServiceResult.Success;
            }
            catch (Exception exception)
            {
                return service.ReportFailure("PersistentStorage.GetPath", exception);
            }
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
        finally
        {
            errors.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetSpaceInfo(nint self, PersistentStorageSpaceInfo* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = default;
        var errors = NativeErrorState.Capture();

        try
        {
            var service = RuntimeServiceObject.Owner<PersistentLocalStorageService>(self);

            try
            {
                *output = service._directory.ReadSpaceInfo();
                service._trace.Write("PersistentStorage.GetSpaceInfo",
                    $"availableBytes={output->AvailableFreeBytes}; usedBytes={output->UsedBytes}; "
                    + $"capacityBytes={output->TotalBytes}; capacityEnforced=False; hresult=0x00000000");

                return RuntimeServiceResult.Success;
            }
            catch (Exception exception)
            {
                return service.ReportFailure("PersistentStorage.GetSpaceInfo", exception);
            }
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
        finally
        {
            errors.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PromptForSpace(nint self, ulong requestedBytes, nint asyncBlock)
    {
        return RejectSpacePrompt(self, asyncBlock, "PersistentStorage.PromptForSpace", requestedBytes);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PromptForSpaceResult(nint self, nint asyncBlock)
    {
        return RejectSpacePrompt(self, asyncBlock, "PersistentStorage.PromptForSpaceResult", 0);
    }

    private static int RejectSpacePrompt(nint self, nint asyncBlock, string operation, ulong requestedBytes)
    {
        if (asyncBlock == 0)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        var errors = NativeErrorState.Capture();

        try
        {
            RuntimeServiceObject.Owner<PersistentLocalStorageService>(self)._trace.Write(operation,
                $"requestedBytes={requestedBytes}; hresult=0x80004001; asyncStarted=False");
        }
        catch
        {
        }
        finally
        {
            errors.Restore();
        }

        return RuntimeServiceResult.NotImplemented;
    }
}
