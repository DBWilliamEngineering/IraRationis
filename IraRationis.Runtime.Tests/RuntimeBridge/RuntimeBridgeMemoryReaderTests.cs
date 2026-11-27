// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static unsafe class RuntimeBridgeMemoryReaderTests
{
    private const uint ReadWrite = 0x04;
    private const uint NoAccess = 0x01;
    private const uint Guard = 0x100;

    internal static void Run()
    {
        Require(Marshal.SizeOf<RuntimeBridgeMemoryInformation>() == 48
                && Marshal.OffsetOf<RuntimeBridgeMemoryInformation>(nameof(RuntimeBridgeMemoryInformation.State)) == 32
                && Marshal.OffsetOf<RuntimeBridgeMemoryInformation>(nameof(RuntimeBridgeMemoryInformation.Protection))
                == 36,
            "The diagnostic memory query must match the Windows x64 MEMORY_BASIC_INFORMATION layout.");

        var pageSize = Environment.SystemPageSize;
        var allocation = RuntimeBridgeMemoryNativeMethods.VirtualAlloc(0, (nuint)(pageSize * 2), 0x3000, ReadWrite);

        Require(allocation != 0, "The authored reader fixture must allocate its own pages.");

        try
        {
            KnownGuardPagesRemainArmed(allocation, pageSize);
            TerminatedStringsStopBeforeInaccessiblePages(allocation, pageSize);
            ExactValuesRequireEveryCoveredPage(allocation, pageSize);
            ReservedPagesAreRejected(allocation, pageSize);
        }
        finally
        {
            Require(RuntimeBridgeMemoryNativeMethods.VirtualFree(allocation, 0, 0x8000) != 0,
                "The authored reader fixture must release its owned reservation.");
        }

        Console.WriteLine("Owned-memory runtime bridge reader protection and boundary contracts passed.");
    }

    private static void KnownGuardPagesRemainArmed(nint allocation, int pageSize)
    {
        *(char*)allocation = '\0';
        Protect(allocation, pageSize, ReadWrite | Guard);

        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        var success = RuntimeBridgeMemoryReader.TryReadUtf16(allocation, 8, out _);

        RequireUnchangedErrors();
        Require(!success && Protection(allocation) == (ReadWrite | Guard),
            "Inspecting a UTF-16 string must not consume a known guard page.");

        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        success = RuntimeBridgeMemoryReader.TryReadAnsi(allocation, 8, out _);

        RequireUnchangedErrors();
        Require(!success && Protection(allocation) == (ReadWrite | Guard),
            "Inspecting an ANSI string must not consume a known guard page.");

        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        success = RuntimeBridgeMemoryReader.TryReadGuid(allocation, out _);

        RequireUnchangedErrors();
        Require(!success && Protection(allocation) == (ReadWrite | Guard),
            "Inspecting a GUID must not consume a known guard page.");

        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        success = RuntimeBridgeMemoryReader.TryReadPointer(allocation, out _);

        RequireUnchangedErrors();
        Require(!success && Protection(allocation) == (ReadWrite | Guard),
            "Inspecting a pointer must not consume a known guard page.");

        Protect(allocation, pageSize, ReadWrite);
    }

    private static void TerminatedStringsStopBeforeInaccessiblePages(nint allocation, int pageSize)
    {
        var secondPage = allocation + pageSize;
        var finalCharacters = allocation + pageSize - 2 * sizeof(char);
        *(char*)finalCharacters = 'Q';
        *(char*)(finalCharacters + sizeof(char)) = '\0';
        Protect(secondPage, pageSize, NoAccess);

        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        var success = RuntimeBridgeMemoryReader.TryReadUtf16(finalCharacters, 32, out var text);

        RequireUnchangedErrors();
        Require(success && text == "Q" && Protection(secondPage) == NoAccess,
            "A UTF-16 terminator in the page's last two bytes must stop inspection before the inaccessible next page.");
        Require(!RuntimeBridgeMemoryReader.TryReadUtf16(finalCharacters, 1, out _),
            "A character limit that excludes the terminator must not produce a successful string.");

        *(char*)(finalCharacters + sizeof(char)) = 'R';

        Require(!RuntimeBridgeMemoryReader.TryReadUtf16(finalCharacters, 32, out _)
                && Protection(secondPage) == NoAccess,
            "An unterminated UTF-16 string must stop with failure at the inaccessible page.");

        var finalBytes = allocation + pageSize - 2;
        *(byte*)finalBytes = (byte)'S';
        *(byte*)(finalBytes + 1) = 0;

        Require(RuntimeBridgeMemoryReader.TryReadAnsi(finalBytes, 32, out text) && text == "S",
            "An ANSI terminator at the page edge must stop inspection before the inaccessible next page.");

        *(byte*)finalBytes = 0xFF;

        Require(!RuntimeBridgeMemoryReader.TryReadAnsi(finalBytes, 32, out _),
            "Non-ASCII export bytes must remain unclassified instead of being decoded ambiguously.");

        foreach (var limit in new[] { 0, -1 })
        {
            Require(!RuntimeBridgeMemoryReader.TryReadUtf16(allocation, limit, out _)
                    && !RuntimeBridgeMemoryReader.TryReadAnsi(allocation, limit, out _),
                "An empty or negative string inspection limit must not read memory.");
        }

        Protect(secondPage, pageSize, ReadWrite);
    }

    private static void ExactValuesRequireEveryCoveredPage(nint allocation, int pageSize)
    {
        var secondPage = allocation + pageSize;
        var identifierAddress = secondPage - 8;
        var identifier = new Guid("442cdf37-5e23-44c2-bd4a-6f4e213b6b2d");

        Require(identifier.TryWriteBytes(new Span<byte>((void*)identifierAddress, 16)),
            "The authored GUID must occupy the two owned pages.");
        Require(RuntimeBridgeMemoryReader.TryReadGuid(identifierAddress, out var observed) && observed == identifier,
            "A GUID spanning two readable pages must be read exactly.");

        Protect(secondPage, pageSize, NoAccess);

        Require(!RuntimeBridgeMemoryReader.TryReadGuid(identifierAddress, out _),
            "An exact GUID read must reject an inaccessible second page.");

        Protect(secondPage, pageSize, ReadWrite | Guard);

        Require(
            !RuntimeBridgeMemoryReader.TryReadGuid(identifierAddress, out _)
            && Protection(secondPage) == (ReadWrite | Guard),
            "A GUID's second-page preflight must reject the guard without consuming it.");

        Protect(secondPage, pageSize, ReadWrite);

        Require(RuntimeBridgeMemoryReader.TryReadGuid(identifierAddress, out observed) && observed == identifier,
            "Reader permission checks must observe a subsequent protection change without caching the earlier rejection.");

        var pointerAddress = secondPage - sizeof(nint) / 2;
        var expectedPointer = unchecked((nint)0x123456789ABC);
        MemoryMarshal.Write(new Span<byte>((void*)pointerAddress, sizeof(nint)), in expectedPointer);

        Require(
            RuntimeBridgeMemoryReader.TryReadPointer(pointerAddress, out var observedPointer)
            && observedPointer == expectedPointer,
            "An unaligned pointer spanning readable pages must be read exactly.");

        Protect(secondPage, pageSize, ReadWrite | Guard);

        Require(
            !RuntimeBridgeMemoryReader.TryReadPointer(pointerAddress, out _)
            && Protection(secondPage) == (ReadWrite | Guard),
            "A pointer's second-page preflight must reject the guard without consuming it.");

        Protect(secondPage, pageSize, ReadWrite);
    }

    private static void ReservedPagesAreRejected(nint allocation, int pageSize)
    {
        var secondPage = allocation + pageSize;

        Require(RuntimeBridgeMemoryNativeMethods.VirtualFree(secondPage, (nuint)pageSize, 0x4000) != 0,
            "The fixture must decommit its second page while retaining reservation ownership.");
        Require(!RuntimeBridgeMemoryReader.TryReadGuid(secondPage, out _)
                && !RuntimeBridgeMemoryReader.TryReadPointer(secondPage, out _)
                && !RuntimeBridgeMemoryReader.TryReadUtf16(secondPage, 8, out _)
                && !RuntimeBridgeMemoryReader.TryReadAnsi(secondPage, 8, out _),
            "Reserved, uncommitted memory must not be inspected.");

        RuntimeBridgeMemoryInformation information;

        Require(RuntimeBridgeMemoryNativeMethods.VirtualQuery(secondPage, &information,
                (nuint)sizeof(RuntimeBridgeMemoryInformation))
            == (nuint)sizeof(RuntimeBridgeMemoryInformation) && information.State == 0x2000,
            "The rejected address must still belong to the fixture's reserved page.");
    }

    private static void Protect(nint address, int bytes, uint protection)
    {
        Require(RuntimeBridgeMemoryNativeMethods.VirtualProtect(address, (nuint)bytes, protection, out _) != 0,
            "The authored fixture must set its requested page protection.");
    }

    private static uint Protection(nint address)
    {
        RuntimeBridgeMemoryInformation information;

        Require(RuntimeBridgeMemoryNativeMethods.VirtualQuery(address, &information,
                    (nuint)sizeof(RuntimeBridgeMemoryInformation))
                == (nuint)sizeof(RuntimeBridgeMemoryInformation),
            "The authored fixture must query its owned page protection.");

        return information.Protection;
    }

    private static void RequireUnchangedErrors()
    {
        var systemError = Marshal.GetLastSystemError();
        var invocationError = Marshal.GetLastPInvokeError();

        Require(systemError == AuthoredRuntimeBridgeTargets.IncomingSystemError
                && invocationError == AuthoredRuntimeBridgeTargets.IncomingInvocationError,
            "Diagnostic pointer inspection must preserve both incoming error states.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
