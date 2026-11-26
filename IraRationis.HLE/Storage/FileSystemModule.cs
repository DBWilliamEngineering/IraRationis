// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/files.rs. Wide/narrow Win32 path subset.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Storage;

public static unsafe partial class FileSystemModule
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<char, string> Drives = [];

    public static void Mount(char letter, string root)
    {
        letter = char.ToUpperInvariant(letter);

        if (letter is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(letter));
        }

        lock (Gate)
        {
            Drives[letter] = Path.GetFullPath(root);
        }
    }

    public static string? Translate(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var verbatim = path.StartsWith(@"\\?\", StringComparison.Ordinal);
        var tail = verbatim ? path[4..] : path;

        if (tail.Length < 2 || tail[1] != ':')
        {
            return null;
        }

        var letter = char.ToUpperInvariant(tail[0]);
        var root = (string?)null;

        lock (Gate)
        {
            _ = Drives.TryGetValue(letter, out root);
        }

        if (root is null)
        {
            return null;
        }

        // Normalize relative components within the virtual root. This is not a sandbox:
        // junctions and unmounted paths may still refer to the host file system.
        var parts = new List<string>();

        foreach (var part in tail[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        var mapped = root.TrimEnd('\\', '/') + "\\" + string.Join('\\', parts);

        if ((verbatim || mapped.Length >= 248) && !mapped.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            mapped = mapped.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + mapped[2..] : @"\\?\" + mapped;
        }

        return mapped;
    }

    private static string? TranslateWide(char* path)
    {
        return path == null ? null : Translate(new string(path));
    }

    private static string? TranslateNarrow(byte* path)
    {
        return path == null ? null : Translate(Marshal.PtrToStringAnsi((nint)path));
    }

    public static HleModule Create()
    {
        return new HleModule("files",
            ["kernel32.dll", "kernelbase.dll", "api-ms-win-core-file-*", "api-ms-win-core-processenvironment-*"],
            [
                new HleExport("CreateFileW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, uint, uint, nint, uint, uint, nint, nint>)
                    &OverrideCreateFileW),
                new HleExport("CreateFile2",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, uint, uint, uint, nint, nint>)&OverrideCreateFile2),
                new HleExport("GetFileAttributesW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, uint>)&OverrideGetFileAttributesW),
                new HleExport("GetFileAttributesExW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, int, nint, int>)&OverrideGetFileAttributesExW),
                new HleExport("SetFileAttributesW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, uint, int>)&OverrideSetFileAttributesW),
                new HleExport("DeleteFileW", (nuint)(delegate* unmanaged[Stdcall]<char*, int>)&OverrideDeleteFileW),
                new HleExport("RemoveDirectoryW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, int>)&OverrideRemoveDirectoryW),
                new HleExport("CreateDirectoryW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, nint, int>)&OverrideCreateDirectoryW),
                new HleExport("GetDiskFreeSpaceExW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, ulong*, ulong*, ulong*, int>)
                    &OverrideGetDiskFreeSpaceExW),
                new HleExport("GetDiskFreeSpaceW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, uint*, uint*, uint*, uint*, int>)
                    &OverrideGetDiskFreeSpaceW),
                new HleExport("FindFirstFileW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, nint, nint>)&OverrideFindFirstFileW),
                new HleExport("FindFirstFileExW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, int, nint, int, nint, uint, nint>)
                    &OverrideFindFirstFileExW),
                new HleExport("CreateFileA",
                    (nuint)(delegate* unmanaged[Stdcall]<byte*, uint, uint, nint, uint, uint, nint, nint>)
                    &OverrideCreateFileA),
                new HleExport("GetFileAttributesA",
                    (nuint)(delegate* unmanaged[Stdcall]<byte*, uint>)&OverrideGetFileAttributesA),
                new HleExport("DeleteFileA", (nuint)(delegate* unmanaged[Stdcall]<byte*, int>)&OverrideDeleteFileA),
                new HleExport("MoveFileExW",
                    (nuint)(delegate* unmanaged[Stdcall]<char*, char*, uint, int>)&OverrideMoveFileExW)
            ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverrideCreateFileW(char* path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = CreateFileW(translated is null ? path : mapped, access, share, security, disposition,
                    flags, template);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverrideCreateFile2(char* path, uint access, uint share, uint disposition, nint parameters)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = CreateFile2(translated is null ? path : mapped, access, share, disposition, parameters);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OverrideGetFileAttributesW(char* path)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = GetFileAttributesW(translated is null ? path : mapped);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return uint.MaxValue;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideGetFileAttributesExW(char* path, int level, nint information)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = GetFileAttributesExW(translated is null ? path : mapped, level, information);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideSetFileAttributesW(char* path, uint attributes)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = SetFileAttributesW(translated is null ? path : mapped, attributes);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideDeleteFileW(char* path)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = DeleteFileW(translated is null ? path : mapped);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideRemoveDirectoryW(char* path)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = RemoveDirectoryW(translated is null ? path : mapped);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideCreateDirectoryW(char* path, nint security)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = CreateDirectoryW(translated is null ? path : mapped, security);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideGetDiskFreeSpaceExW(char* path, ulong* available, ulong* total, ulong* free)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = GetDiskFreeSpaceExW(translated is null ? path : mapped, available, total, free);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideGetDiskFreeSpaceW(char* path, uint* sectors, uint* bytes, uint* free, uint* total)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = GetDiskFreeSpaceW(translated is null ? path : mapped, sectors, bytes, free, total);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverrideFindFirstFileW(char* path, nint data)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = FindFirstFileW(translated is null ? path : mapped, data);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverrideFindFirstFileExW(char* path, int level, nint data, int search, nint filter, uint flags)
    {
        try
        {
            var translated = TranslateWide(path);

            fixed (char* mapped = translated)
            {
                var result = FindFirstFileExW(translated is null ? path : mapped, level, data, search, filter, flags);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverrideCreateFileA(byte* path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template)
    {
        try
        {
            var translated = TranslateNarrow(path);

            if (translated is null)
            {
                var result = CreateFileA(path, access, share, security, disposition, flags, template);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }

            fixed (char* mapped = translated)
            {
                var result = CreateFileW(mapped, access, share, security, disposition, flags, template);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OverrideGetFileAttributesA(byte* path)
    {
        try
        {
            var translated = TranslateNarrow(path);

            if (translated is null)
            {
                var result = GetFileAttributesA(path);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }

            fixed (char* mapped = translated)
            {
                var result = GetFileAttributesW(mapped);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return uint.MaxValue;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideDeleteFileA(byte* path)
    {
        try
        {
            var translated = TranslateNarrow(path);

            if (translated is null)
            {
                var result = DeleteFileA(path);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }

            fixed (char* mapped = translated)
            {
                var result = DeleteFileW(mapped);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OverrideMoveFileExW(char* from, char* destination, uint flags)
    {
        try
        {
            var translatedFrom = TranslateWide(from);
            var translatedTo = TranslateWide(destination);

            fixed (char* mappedFrom = translatedFrom, mappedTo = translatedTo)
            {
                var result = MoveFileExW(translatedFrom is null ? from : mappedFrom,
                    translatedTo is null ? destination : mappedTo, flags);

                SetLastError((uint)Marshal.GetLastPInvokeError());

                return result;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            SetLastError(123);

            return 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFileW(char* path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFile2(char* path, uint access, uint share, uint disposition, nint parameters);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFileAttributesW(char* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetFileAttributesExW(char* path, int level, nint information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetFileAttributesW(char* path, uint attributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DeleteFileW(char* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RemoveDirectoryW(char* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDirectoryW(char* path, nint security);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDiskFreeSpaceExW(char* path, ulong* available, ulong* total, ulong* free);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDiskFreeSpaceW(char* path, uint* sectors, uint* bytes, uint* free, uint* total);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint FindFirstFileW(char* path, nint data);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint FindFirstFileExW(char* path, int level, nint data, int search, nint filter, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFileA(byte* path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFileAttributesA(byte* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DeleteFileA(byte* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MoveFileExW(char* from, char* destination, uint flags);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SetLastError(uint error);
}
