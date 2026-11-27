// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Text;
using IraRationis.HLE.Runtime.Services.Storage.Models;

namespace IraRationis.HLE.Runtime.Services.Storage;

internal sealed unsafe partial class PersistentLocalStorageService
{
    internal static readonly Guid ProviderId = new("f4faf4d4-2d04-4fce-b3e0-474a713a3e84");
    internal static readonly Guid InterfaceId = new("41a4e10c-5a7e-41d9-8c37-37bde62a07d6");
    private readonly RuntimeServiceObject _object;
    private readonly RuntimeServiceTrace _trace;
    private readonly PersistentStorageDirectory _directory;
    private readonly byte[] _pathBytes;

    internal PersistentLocalStorageService(string directory, RuntimeServiceTrace trace, ulong capacityBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(trace);

        _trace = trace;
        _directory = new PersistentStorageDirectory(directory, capacityBytes);
        var path = Path.EndsInDirectorySeparator(_directory.Path)
            ? _directory.Path : _directory.Path + Path.DirectorySeparatorChar;
        _pathBytes = Encoding.UTF8.GetBytes(path + '\0');

        ReadOnlySpan<nint> methods =
        [
            0, 0, 0,
            (nint)(delegate* unmanaged[Stdcall]<nint, nuint*, int>)&GetPathSize,
            (nint)(delegate* unmanaged[Stdcall]<nint, nuint, byte*, nuint*, int>)&GetPath,
            (nint)(delegate* unmanaged[Stdcall]<nint, PersistentStorageSpaceInfo*, int>)&GetSpaceInfo,
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)&PromptForSpace,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&PromptForSpaceResult
        ];

        _object = new RuntimeServiceObject(this, methods, InterfaceId);
    }

    internal bool TryQuery(Guid requestedInterface, out nint address)
    {
        address = 0;

        if (requestedInterface != InterfaceId)
        {
            return false;
        }

        address = _object.Retain();

        return true;
    }

    private int ReportFailure(string operation, Exception exception)
    {
        var result = exception is Win32Exception { NativeErrorCode: > 0 } windowsError
            ? unchecked((int)(0x80070000U | ((uint)windowsError.NativeErrorCode & 0xffff)))
            : exception.HResult < 0 ? exception.HResult : RuntimeServiceResult.Failure;

        try
        {
            _trace.Write(operation, $"hresult=0x{unchecked((uint)result):X8}; error={exception.GetType().Name}");
        }
        catch
        {
        }

        return result;
    }
}
