// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/host.rs.

using System.Runtime.InteropServices;
using System.Text;
using IraRationis.PE;

namespace IraRationis.HLE;

public sealed unsafe partial class HostPolicy
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private static readonly Lock ModuleLock = new();
    private static readonly Dictionary<string, nint> Modules = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DefaultAllow =
    [
        "api-ms-win-core-*", "api-ms-win-eventing-*", "api-ms-win-security-*", "api-ms-win-crt-*",
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "ucrtbase.dll", "vcruntime140*", "msvcp140*",
        "ws2_32.dll", "bcrypt.dll", "crypt32.dll", "ncrypt.dll", "winhttp.dll", "sspicli.dll",
        "cabinet.dll", "avrt.dll", "ext-ms-win-rtcore-ntuser-*", "api-ms-win-shcore-*", "msvcp_win.dll",
        "ole32.dll", "advapi32.dll", "iphlpapi.dll", "dbghelp.dll", "xaudio2_9.dll", "mfplat.dll", "mfreadwrite.dll"
    ];

    private readonly string[] _allow;

    public HostPolicy() : this(DefaultAllow)
    {
    }

    public HostPolicy(IEnumerable<string> allow)
    {
        _allow = allow.ToArray();
    }

    public bool Allows(string dll)
    {
        foreach (var pattern in _allow)
        {
            if (pattern.EndsWith('*'))
            {
                if (dll.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, dll, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public nuint? Resolve(string dll, ImportSymbol symbol)
    {
        if (!OperatingSystem.IsWindows() || !Allows(dll))
        {
            return null;
        }

        return ResolveSystemFunction(dll, symbol);
    }

    private static nuint? ResolveSystemFunction(string dll, ImportSymbol symbol)
    {
        if (dll.Length == 0 || dll.Contains('\0') || dll.Contains('/') || dll.Contains('\\') || dll.Contains(':'))
        {
            return null;
        }

        var handle = GetModule(dll);

        if (handle == 0)
        {
            return null;
        }

        var address = (nint)0;

        if (symbol.Name is { } name)
        {
            if (name.Contains('\0'))
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(name + '\0');

            fixed (byte* pointer = bytes)
            {
                address = GetProcAddress(handle, pointer);
            }
        }
        else
        {
            address = GetProcAddress(handle, (byte*)symbol.HintOrOrdinal);
        }

        return address == 0 ? null : (nuint)address;
    }

    private static nint GetModule(string dll)
    {
        lock (ModuleLock)
        {
            if (Modules.TryGetValue(dll, out var existing))
            {
                return existing;
            }

            var handle = LoadLibraryExW(dll, 0, LoadLibrarySearchSystem32);

            if (handle != 0)
            {
                Modules.Add(dll, handle);
            }

            return handle;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint GetProcAddress(nint module, byte* name);
}
