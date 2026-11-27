// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal static unsafe partial class RuntimeBridgeMemoryReader
{
    private const int MaximumStringCharacters = 512;
    private const int MaximumSnapshotBytes = 256;
    private const uint CommittedMemory = 0x1000;
    private const uint GuardProtection = 0x100;
    private const uint ReadableProtectionMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    internal static bool TryReadUtf16(nint address, int maxChars, out string value)
    {
        return TryReadString(address, maxChars, sizeof(char), out value);
    }

    internal static bool TryReadAnsi(nint address, int maxChars, out string value)
    {
        return TryReadString(address, maxChars, sizeof(byte), out value);
    }

    internal static bool TryReadGuid(nint address, out Guid value)
    {
        var errorState = NativeErrorState.Capture();
        value = default;

        try
        {
            Span<byte> bytes = stackalloc byte[16];

            if (!TryReadBytes(address, bytes))
            {
                return false;
            }

            value = new Guid(bytes);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal static bool TryReadPointer(nint address, out nint value)
    {
        var errorState = NativeErrorState.Capture();
        value = 0;

        try
        {
            Span<byte> bytes = stackalloc byte[sizeof(nint)];

            if (!TryReadBytes(address, bytes))
            {
                return false;
            }

            value = MemoryMarshal.Read<nint>(bytes);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal static bool TryReadSnapshot(nint address, Span<byte> destination)
    {
        if (destination.Length > MaximumSnapshotBytes)
        {
            return false;
        }

        var errorState = NativeErrorState.Capture();

        try
        {
            return TryReadBytes(address, destination);
        }
        catch
        {
            return false;
        }
        finally
        {
            errorState.Restore();
        }
    }

    private static bool TryReadString(nint address, int maxChars, int characterSize, out string value)
    {
        var errorState = NativeErrorState.Capture();
        value = string.Empty;

        try
        {
            var characterLimit = Math.Clamp(maxChars, 0, MaximumStringCharacters);
            var byteLimit = characterLimit * characterSize;

            if (!OperatingSystem.IsWindows() || !IsValidRange(address, byteLimit))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[MaximumStringCharacters * sizeof(char)];
            var bytesRead = 0;

            // O(maxChars), capped at 512 characters; each native read remains inside one page.
            while (bytesRead < byteLimit)
            {
                var currentAddress = (nuint)address + (nuint)bytesRead;
                var chunkLength = GetPageChunkLength(currentAddress, byteLimit - bytesRead);

                if (!TryReadPage(currentAddress, bytes.Slice(bytesRead, chunkLength)))
                {
                    return false;
                }

                var firstCharacter = bytesRead / characterSize;

                bytesRead += chunkLength;

                for (var characterIndex = firstCharacter; characterIndex < bytesRead / characterSize; characterIndex++)
                {
                    var byteIndex = characterIndex * characterSize;
                    var character = characterSize == sizeof(char)
                        ? bytes[byteIndex] | bytes[byteIndex + 1] << 8
                        : bytes[byteIndex];

                    if (character == 0)
                    {
                        value = characterSize == sizeof(char)
                            ? new string(MemoryMarshal.Cast<byte, char>(bytes[..byteIndex]))
                            : Encoding.ASCII.GetString(bytes[..byteIndex]);

                        return true;
                    }

                    if (characterSize == sizeof(byte) && character > 0x7f)
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        catch
        {
            value = string.Empty;

            return false;
        }
        finally
        {
            errorState.Restore();
        }
    }

    private static bool TryReadBytes(nint address, Span<byte> bytes)
    {
        if (!OperatingSystem.IsWindows() || !IsValidRange(address, bytes.Length)
            || !IsReadableRange((nuint)address, bytes.Length))
        {
            return false;
        }

        var bytesRead = 0;

        while (bytesRead < bytes.Length)
        {
            var currentAddress = (nuint)address + (nuint)bytesRead;
            var chunkLength = GetPageChunkLength(currentAddress, bytes.Length - bytesRead);

            if (!TryReadPage(currentAddress, bytes.Slice(bytesRead, chunkLength)))
            {
                return false;
            }

            bytesRead += chunkLength;
        }

        return true;
    }

    private static bool IsValidRange(nint address, int byteCount)
    {
        return byteCount > 0 && (nuint)address > ushort.MaxValue
            && (nuint)address <= nuint.MaxValue - (nuint)(byteCount - 1);
    }

    private static int GetPageChunkLength(nuint address, int remainingBytes)
    {
        var pageSize = (nuint)Environment.SystemPageSize;

        return (int)nuint.Min((nuint)remainingBytes, pageSize - address % pageSize);
    }

    private static bool TryReadPage(nuint address, Span<byte> destination)
    {
        if (!IsReadablePage(address, destination.Length))
        {
            return false;
        }

        // Protection can change after VirtualQuery; ReadProcessMemory still performs the copy safely.
        // This preflight avoids intentionally reading a known guard page, but is not an atomic permission lease.
        fixed (byte* buffer = destination)
        {
            nuint bytesRead;

            return ReadProcessMemory(-1, (void*)address, buffer, (nuint)destination.Length, &bytesRead) != 0
                && bytesRead == (nuint)destination.Length;
        }
    }

    private static bool IsReadableRange(nuint address, int length)
    {
        var verifiedBytes = 0;

        while (verifiedBytes < length)
        {
            var currentAddress = address + (nuint)verifiedBytes;
            var chunkLength = GetPageChunkLength(currentAddress, length - verifiedBytes);

            if (!IsReadablePage(currentAddress, chunkLength))
            {
                return false;
            }

            verifiedBytes += chunkLength;
        }

        return true;
    }

    private static bool IsReadablePage(nuint address, int length)
    {
        RuntimeBridgeMemoryInformation information;

        if (VirtualQuery((void*)address, &information, (nuint)sizeof(RuntimeBridgeMemoryInformation))
            != (nuint)sizeof(RuntimeBridgeMemoryInformation))
        {
            return false;
        }

        return information.State == CommittedMemory && (information.Protection & GuardProtection) == 0
            && (information.Protection & ReadableProtectionMask) != 0
            && address >= information.BaseAddress && address - information.BaseAddress < information.RegionSize
            && (nuint)length <= information.RegionSize - (address - information.BaseAddress);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* address, RuntimeBridgeMemoryInformation* information,
        nuint informationSize);

    [LibraryImport("kernel32.dll")]
    private static partial int ReadProcessMemory(nint process, void* address, void* buffer, nuint size,
        nuint* bytesRead);
}
