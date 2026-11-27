// SPDX-License-Identifier: GPL-3.0-or-later
/*
 * @file        HostPolicy.cs
 * @description Resolves allowed Windows imports for the guest.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     Successful module loads are retained for the lifetime of the process.
 */

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using IraRationis.PE.Models;

namespace IraRationis.HLE.Runtime;

public sealed unsafe partial class HostPolicy
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private static readonly Lock ModuleLoadGate = new();
    private static readonly ConcurrentDictionary<string, nint> LoadedModules = new(StringComparer.OrdinalIgnoreCase);

    // Default compatibility names are retained from Zettrox/ztrx, crates/victory-hle/src/host.rs.
    private static readonly string[] DefaultModuleRules =
    [
        "api-ms-win-core-*", "api-ms-win-eventing-*", "api-ms-win-security-*", "api-ms-win-crt-*",
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "ucrtbase.dll", "vcruntime140*", "msvcp140*",
        "ws2_32.dll", "bcrypt.dll", "crypt32.dll", "ncrypt.dll", "winhttp.dll", "sspicli.dll",
        "cabinet.dll", "avrt.dll", "ext-ms-win-rtcore-ntuser-*", "api-ms-win-shcore-*", "msvcp_win.dll",
        "ole32.dll", "advapi32.dll", "iphlpapi.dll", "dbghelp.dll", "xaudio2_9.dll", "mfplat.dll", "mfreadwrite.dll"
    ];

    private readonly HashSet<string> _exactModuleNames = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly string[] _moduleFamilyPrefixes;

    public HostPolicy() : this(DefaultModuleRules)
    {
    }

    public HostPolicy(IEnumerable<string> allow)
    {
        ArgumentNullException.ThrowIfNull(allow);

        var families = new List<string>();

        foreach (var rule in allow)
        {
            if (rule.EndsWith('*'))
            {
                families.Add(rule[..^1]);
            }
            else
            {
                _exactModuleNames.Add(rule);
            }
        }

        _moduleFamilyPrefixes = [.. families];
    }

    public bool Allows(string dll)
    {
        if (_exactModuleNames.Contains(dll))
        {
            return true;
        }

        foreach (var prefix in _moduleFamilyPrefixes)
        {
            if (dll.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public nuint? Resolve(string dll, ImportSymbol symbol)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(dll) || dll.AsSpan().IndexOfAny("\0/\\:") >= 0 ||
            !Allows(dll) || symbol.Name?.Contains('\0') == true)
        {
            return null;
        }

        var module = GetOrLoadModule(dll);

        if (module == 0)
        {
            return null;
        }

        var address = symbol.Name is { } name
            ? GetProcAddressByName(module, name)
            : GetProcAddressByOrdinal(module, (byte*)symbol.HintOrOrdinal);

        return address == 0 ? null : (nuint)address;
    }

    private static nint GetOrLoadModule(string dll)
    {
        if (LoadedModules.TryGetValue(dll, out var module))
        {
            return module;
        }

        lock (ModuleLoadGate)
        {
            if (LoadedModules.TryGetValue(dll, out module))
            {
                return module;
            }

            module = LoadLibraryExW(dll, 0, LoadLibrarySearchSystem32);

            if (module != 0)
            {
                LoadedModules[dll] = module;
            }

            return module;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcAddressByOrdinal(nint module, byte* ordinal);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcAddressByName(nint module, string name);
}
