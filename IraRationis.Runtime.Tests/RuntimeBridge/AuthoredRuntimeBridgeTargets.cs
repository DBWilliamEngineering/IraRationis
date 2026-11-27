// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static unsafe class AuthoredRuntimeBridgeTargets
{
    internal const int IncomingSystemError = 271;
    internal const int IncomingInvocationError = 389;
    internal const int OutgoingSystemError = 457;
    internal const int OutgoingInvocationError = 613;

    private static int _result;
    private static nint _pointerResult;
    private static bool _writeOutput;

    internal static int Calls
    {
        get;
        private set;
    }

    internal static int ObservedSystemError
    {
        get;
        private set;
    }

    internal static int ObservedInvocationError
    {
        get;
        private set;
    }

    internal static ulong FirstVersion
    {
        get;
        private set;
    }

    internal static ulong SecondVersion
    {
        get;
        private set;
    }

    internal static ulong Flags
    {
        get;
        private set;
    }

    internal static nint Options
    {
        get;
        private set;
    }

    internal static nint Name
    {
        get;
        private set;
    }

    internal static nint File
    {
        get;
        private set;
    }

    internal static nint Module
    {
        get;
        private set;
    }

    internal static nint Provider
    {
        get;
        private set;
    }

    internal static nint RequestedInterface
    {
        get;
        private set;
    }

    internal static nint Output
    {
        get;
        private set;
    }

    internal static nint InitializeAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, int>)&Initialize;
        }
    }

    internal static nint InitializeExtendedAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, ulong, int>)&InitializeExtended;
        }
    }

    internal static nint InitializeWithOptionsAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, ulong, nint, int>)&InitializeWithOptions;
        }
    }

    internal static nint QueryAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Query;
        }
    }

    internal static nint LoadLibraryAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)&LoadLibrary;
        }
    }

    internal static nint AlternateLoadLibraryAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)&AlternateLoadLibrary;
        }
    }

    internal static nint LookupAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<nint, byte*, nint>)&Lookup;
        }
    }

    internal static void Configure(int result = 0, nint pointerResult = 0, bool writeOutput = false)
    {
        _result = result;
        _pointerResult = pointerResult;
        _writeOutput = writeOutput;
        Calls = 0;
    }

    internal static void SetIncomingErrors()
    {
        Marshal.SetLastPInvokeError(IncomingInvocationError);
        Marshal.SetLastSystemError(IncomingSystemError);
    }

    private static void RecordIncomingErrors()
    {
        ObservedSystemError = Marshal.GetLastSystemError();
        ObservedInvocationError = Marshal.GetLastPInvokeError();
        Calls++;
    }

    private static void SetOutgoingErrors()
    {
        Marshal.SetLastPInvokeError(OutgoingInvocationError);
        Marshal.SetLastSystemError(OutgoingSystemError);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Initialize(ulong firstVersion, ulong secondVersion)
    {
        RecordIncomingErrors();
        FirstVersion = firstVersion;
        SecondVersion = secondVersion;
        SetOutgoingErrors();

        return _result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InitializeExtended(ulong firstVersion, ulong secondVersion, ulong flags)
    {
        RecordIncomingErrors();
        FirstVersion = firstVersion;
        SecondVersion = secondVersion;
        Flags = flags;
        SetOutgoingErrors();

        return _result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InitializeWithOptions(ulong firstVersion, ulong secondVersion, ulong flags, nint options)
    {
        RecordIncomingErrors();
        FirstVersion = firstVersion;
        SecondVersion = secondVersion;
        Flags = flags;
        Options = options;
        SetOutgoingErrors();

        return _result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint provider, nint requestedInterface, nint output)
    {
        RecordIncomingErrors();
        Provider = provider;
        RequestedInterface = requestedInterface;
        Output = output;

        if (_writeOutput && output != 0)
        {
            *(nint*)output = _pointerResult;
        }

        SetOutgoingErrors();

        return _result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadLibrary(char* name, nint file, uint flags)
    {
        RecordIncomingErrors();
        Name = (nint)name;
        File = file;
        Flags = flags;
        SetOutgoingErrors();

        return _pointerResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint AlternateLoadLibrary(char* name, nint file, uint flags)
    {
        RecordIncomingErrors();
        Name = (nint)name;
        File = file;
        Flags = flags;
        SetOutgoingErrors();

        return _pointerResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Lookup(nint module, byte* name)
    {
        RecordIncomingErrors();
        Module = module;
        Name = (nint)name;
        SetOutgoingErrors();

        return _pointerResult;
    }
}
