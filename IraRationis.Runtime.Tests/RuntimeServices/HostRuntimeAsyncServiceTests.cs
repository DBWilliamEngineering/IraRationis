// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Services.Async;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class HostRuntimeAsyncServiceTests
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("Host runtime async ABI contracts require Windows x64; skipped.");

            return;
        }

        var originalErrors = NativeErrorState.Capture();

        try
        {
            StructuresMatchPublicLayouts();
            ConstructionRequiresCompleteInterface();
            CallsPreserveArgumentsResultsAndErrorState();
            DisposalDuringCallRetainsTheInterface();
            DisposalRejectsNewCalls();
        }
        finally
        {
            originalErrors.Restore();
        }

        Console.WriteLine("Authored host async ABI, forwarding, and reference lifetime contracts passed.");
    }

    private static void StructuresMatchPublicLayouts()
    {
        Require(sizeof(XAsyncBlock) == 56 && Marshal.OffsetOf<XAsyncBlock>(nameof(XAsyncBlock.Queue)) == 0 &&
            Marshal.OffsetOf<XAsyncBlock>(nameof(XAsyncBlock.Context)) == 8 &&
            Marshal.OffsetOf<XAsyncBlock>(nameof(XAsyncBlock.Callback)) == 16,
            "XAsyncBlock must retain its three pointer fields and 32 opaque reserved bytes.");

        Require(sizeof(XAsyncProviderData) == 32 && Marshal.OffsetOf<XAsyncProviderData>(nameof(XAsyncProviderData.Async)) == 0 &&
            Marshal.OffsetOf<XAsyncProviderData>(nameof(XAsyncProviderData.BufferSize)) == 8 &&
            Marshal.OffsetOf<XAsyncProviderData>(nameof(XAsyncProviderData.Buffer)) == 16 &&
            Marshal.OffsetOf<XAsyncProviderData>(nameof(XAsyncProviderData.Context)) == 24,
            "XAsyncProviderData must match the Windows x64 provider callback layout.");

        Require((uint)XAsyncOperation.Begin == 0 && (uint)XAsyncOperation.DoWork == 1 &&
            (uint)XAsyncOperation.GetResult == 2 && (uint)XAsyncOperation.Cancel == 3 &&
            (uint)XAsyncOperation.Cleanup == 4,
            "Async provider operation values must match the public contract.");
    }

    private static void ConstructionRequiresCompleteInterface()
    {
        var rejected = false;

        try
        {
            using var unexpected = new HostRuntimeAsyncService(0);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Require(rejected, "A null runtime interface must be rejected.");

        foreach (var slot in new[] { 1, 2, 7, 9, 10, 11, 18 })
        {
            using var authored = new AuthoredHostRuntimeAsyncInterface();
            authored.RemoveMethod(slot);
            rejected = false;

            try
            {
                using var unexpected = new HostRuntimeAsyncService(authored.Address);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Require(rejected && AuthoredHostRuntimeAsyncTargets.ReferenceCount == 1,
                "A failed construction must leave the supplied reference with the caller.");
        }
    }

    private static void CallsPreserveArgumentsResultsAndErrorState()
    {
        using var authored = new AuthoredHostRuntimeAsyncInterface();
        using var service = new HostRuntimeAsyncService(authored.Address);

        var asyncBlock = new XAsyncBlock { Queue = 17, Context = 29, Callback = 43 };
        var originalBytes = new ReadOnlySpan<byte>(&asyncBlock, sizeof(XAsyncBlock)).ToArray();
        var context = unchecked((nint)0xFEDCBA9876543210UL);
        var identity = unchecked((nint)0x923456781234ABCDUL);
        var provider = unchecked((nint)0xABCDEF0198765432UL);
        var buffer = unchecked((nint)0xE1234567ABCD9012UL);
        var bufferSize = unchecked((nuint)0xFEDCBA9876543210UL);

        var identityName = stackalloc byte[] { (byte)'A', (byte)'d', (byte)'d', 0 };

        foreach (var expectedResult in new[] { 0, AuthoredHostRuntimeAsyncTargets.FailureResult })
        {
            AuthoredHostRuntimeAsyncTargets.Result = expectedResult;
            AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
            var result = service.Begin(&asyncBlock, context, identity, identityName, provider);
            CheckErrors();

            Require(result == expectedResult && AuthoredHostRuntimeAsyncTargets.Instance == authored.Address &&
                AuthoredHostRuntimeAsyncTargets.AsyncBlock == (nint)(&asyncBlock) &&
                AuthoredHostRuntimeAsyncTargets.Context == context && AuthoredHostRuntimeAsyncTargets.Identity == identity &&
                AuthoredHostRuntimeAsyncTargets.IdentityName == (nint)identityName && AuthoredHostRuntimeAsyncTargets.ProviderCallback == provider,
                "Begin must forward the full pointer widths, self pointer, callback, and HRESULT unchanged.");

            AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
            result = service.Schedule(&asyncBlock, uint.MaxValue);
            CheckErrors();

            Require(result == expectedResult && AuthoredHostRuntimeAsyncTargets.DelayMilliseconds == uint.MaxValue,
                "Schedule must preserve the unsigned 32-bit delay and HRESULT.");

            AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
            service.Complete(&asyncBlock, expectedResult, bufferSize);
            CheckErrors();

            Require(AuthoredHostRuntimeAsyncTargets.CompletionResult == expectedResult && AuthoredHostRuntimeAsyncTargets.BufferSize == bufferSize,
                "Complete must preserve the signed HRESULT and native-width result size.");

            var used = (nuint)197;
            AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
            result = service.GetResult(&asyncBlock, identity, bufferSize, buffer, &used);
            CheckErrors();

            Require(result == expectedResult && AuthoredHostRuntimeAsyncTargets.Identity == identity &&
                AuthoredHostRuntimeAsyncTargets.BufferSize == bufferSize && AuthoredHostRuntimeAsyncTargets.Buffer == buffer &&
                AuthoredHostRuntimeAsyncTargets.BufferUsed == (nint)(&used) && used == (expectedResult >= 0 ? bufferSize : 197),
                "GetResult must preserve pointers, size, result, and output behavior on success and failure.");

            AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
            result = service.SubmitCallback(buffer, uint.MaxValue, context, provider);
            CheckErrors();

            Require(result == expectedResult && AuthoredHostRuntimeAsyncTargets.Queue == buffer &&
                AuthoredHostRuntimeAsyncTargets.Port == uint.MaxValue && AuthoredHostRuntimeAsyncTargets.Context == context &&
                AuthoredHostRuntimeAsyncTargets.QueueCallback == provider,
                "SubmitCallback must preserve the queue, unsigned port, context, callback, and HRESULT.");
        }

        Require(new ReadOnlySpan<byte>(&asyncBlock, sizeof(XAsyncBlock)).SequenceEqual(originalBytes),
            "The adapter must never interpret or modify XAsyncBlock state.");
        Require(AuthoredHostRuntimeAsyncTargets.TargetCalls == 10 && AuthoredHostRuntimeAsyncTargets.AddReferenceCalls == 10 &&
            AuthoredHostRuntimeAsyncTargets.ReleaseReferenceCalls == 10 && AuthoredHostRuntimeAsyncTargets.ReferenceCount == 1,
            "Every native call must own and release one temporary interface reference.");
    }

    private static void DisposalDuringCallRetainsTheInterface()
    {
        using var authored = new AuthoredHostRuntimeAsyncInterface();
        using var service = new HostRuntimeAsyncService(authored.Address);
        var asyncBlock = new XAsyncBlock();
        AuthoredHostRuntimeAsyncTargets.DuringBegin = service.Dispose;

        var result = service.Begin(&asyncBlock, 0, 0, null, 0);

        Require(result == 0 && !AuthoredHostRuntimeAsyncTargets.CallbackFailed &&
            AuthoredHostRuntimeAsyncTargets.ReferencesAfterCallback == 1 &&
            AuthoredHostRuntimeAsyncTargets.ReferenceCount == 0 && AuthoredHostRuntimeAsyncTargets.ReleaseReferenceCalls == 2,
            "Disposal during a native call must release the session reference while retaining the in-flight call reference.");
        AuthoredHostRuntimeAsyncTargets.DuringBegin = null;
    }

    private static void DisposalRejectsNewCalls()
    {
        using var authored = new AuthoredHostRuntimeAsyncInterface();
        using var service = new HostRuntimeAsyncService(authored.Address);
        AuthoredHostRuntimeAsyncTargets.SetIncomingErrors();
        service.Dispose();

        Require(Marshal.GetLastSystemError() == AuthoredHostRuntimeAsyncTargets.IncomingSystemError &&
            Marshal.GetLastPInvokeError() == AuthoredHostRuntimeAsyncTargets.IncomingPInvokeError,
            "Releasing the owned interface must preserve the caller's native error state.");

        service.Dispose();
        var rejected = false;

        try
        {
            service.Schedule(null, 0);
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        Require(rejected && AuthoredHostRuntimeAsyncTargets.ReferenceCount == 0 &&
            AuthoredHostRuntimeAsyncTargets.ReleaseReferenceCalls == 1 && AuthoredHostRuntimeAsyncTargets.TargetCalls == 0,
            "Disposal must release ownership once and prevent subsequent native calls.");
    }

    private static void CheckErrors()
    {
        var systemError = Marshal.GetLastSystemError();
        var pinvokeError = Marshal.GetLastPInvokeError();

        Require(AuthoredHostRuntimeAsyncTargets.ObservedSystemError == AuthoredHostRuntimeAsyncTargets.IncomingSystemError &&
            AuthoredHostRuntimeAsyncTargets.ObservedPInvokeError == AuthoredHostRuntimeAsyncTargets.IncomingPInvokeError &&
            systemError == AuthoredHostRuntimeAsyncTargets.OutgoingSystemError &&
            pinvokeError == AuthoredHostRuntimeAsyncTargets.OutgoingPInvokeError,
            "Reference management must preserve both native error channels before and after the target call.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
