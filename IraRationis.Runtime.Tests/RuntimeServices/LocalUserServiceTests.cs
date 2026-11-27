// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using IraRationis.HLE.Runtime.Services;
using IraRationis.HLE.Runtime.Services.Async.Models;
using IraRationis.HLE.Runtime.Services.Users;
using IraRationis.HLE.Runtime.Services.Users.LocalUserService;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class LocalUserServiceTests
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("Local user unmanaged service contracts require Windows x64; skipped.");

            return;
        }

        AddUsesWorkAndCompletionQueues();
        CancellationCompletesWithoutAddingUser();
        BeginAndScheduleFailuresDoNotReturnUsers();
        AddOptionsFollowPublicContract();
        HandlesRemainOwnedAndIdentifiersRemainDistinct();
        UserInterfacesShareIdentity();
        NativeOutputsRespectTheirWidths();
        GamertagWritesRespectCapacity();
        UnsupportedServicesNeverReturnCredentials();
        RegistrationsHaveBoundedOwnership();

        Console.WriteLine("Authored local user async, identity, handle, buffer, and registration contracts passed.");
    }

    private static void AddUsesWorkAndCompletionQueues()
    {
        using var fixture = new LocalUserServiceFixture();
        var block = new XAsyncBlock { Callback = AuthoredLocalUserCallbacks.CompletionAddress };
        var handle = (nint)71;

        Require(FindByLocalId(fixture, LocalUserServiceFixture.Profile.LocalId, &handle) == RuntimeServiceResult.UserNotFound && handle == 0,
            "The local user must not be visible before an Add operation completes.");
        Require(fixture.Add(&block) == RuntimeServiceResult.Success && fixture.Host.QueuedWork == 1 &&
            fixture.Host.CompleteCalls == 0 && fixture.Host.QueuedCompletions == 0 && AuthoredLocalUserCallbacks.Completions == 0,
            "Starting Add must schedule work without directly completing or invoking the title callback.");
        Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Pending && handle == 0,
            "Result retrieval before work dispatch must remain pending and expose no user handle.");
        Require(fixture.Add(&block) < 0 && fixture.Host.QueuedWork == 1,
            "A second operation must not replace a block already in use.");

        Require(fixture.Host.DispatchWork() && fixture.Host.WorkCalls == 1 && fixture.Host.CompleteCalls == 1 &&
            fixture.Host.LastCompletedResult == RuntimeServiceResult.Success && fixture.Host.LastCompletedSize == (nuint)sizeof(nint),
            "The work callback must complete exactly once with a native-width user-handle result.");
        Require(fixture.Host.QueuedCompletions == 1 && AuthoredLocalUserCallbacks.Completions == 0 && fixture.Host.CleanupCalls == 0,
            "Work completion must queue the title callback and retain provider context until result retrieval.");

        handle = 71;

        Require(fixture.Async.GetResult(&block, fixture.Host.LastIdentity + 1, (nuint)sizeof(nint), (nint)(&handle), null) < 0 && handle == 71,
            "An unrelated async identity must not retrieve or consume the user result.");
        Require(fixture.Async.GetResult(&block, fixture.Host.LastIdentity, 1, (nint)(&handle), null) == RuntimeServiceResult.InsufficientBuffer && handle == 71,
            "An undersized result request must permit a later correctly sized retrieval.");
        Require(fixture.Host.DispatchCompletion() && AuthoredLocalUserCallbacks.Completions == 1 &&
            AuthoredLocalUserCallbacks.LastBlock == (nint)(&block),
            "Only completion dispatch may invoke the title callback with its original async block.");
        Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Success && handle != 0 && fixture.Host.CleanupCalls == 1,
            "Taking the result must create one owned handle and clean up provider context exactly once.");

        var firstHandle = handle;

        Require(fixture.TakeResult(&block, &handle) < 0 && handle == 0 && fixture.Host.CleanupCalls == 1,
            "A consumed result must not create another user handle or repeat cleanup.");
        Require(!fixture.Host.DispatchWork() && !fixture.Host.DispatchCompletion() && fixture.Host.BoundaryFailures == 0,
            "A completed operation must leave no unexpected work, callback, or native-boundary exception.");

        fixture.Close(firstHandle);
    }

    private static void CancellationCompletesWithoutAddingUser()
    {
        using var fixture = new LocalUserServiceFixture();
        var block = new XAsyncBlock { Callback = AuthoredLocalUserCallbacks.CompletionAddress };

        Require(fixture.Add(&block) == RuntimeServiceResult.Success, "The authored cancellation operation must start.");

        fixture.Host.Cancel(&block);
        fixture.Host.Cancel(&block);

        Require(fixture.Host.CompleteCalls == 1 && fixture.Host.LastCompletedResult == RuntimeServiceResult.Aborted &&
            fixture.Host.LastCompletedSize == 0 && fixture.Host.CleanupCalls == 1 && AuthoredLocalUserCallbacks.Completions == 0,
            "Cancellation must complete once with E_ABORT, no result payload, and one cleanup before queued completion.");
        Require(fixture.Host.DispatchWork() && fixture.Host.WorkCalls == 0,
            "Work remaining in the queue after cancellation must not enter the released provider context.");
        Require(fixture.Host.DispatchCompletion() && AuthoredLocalUserCallbacks.Completions == 1,
            "Canceled operations still deliver their queued completion callback.");

        var handle = (nint)71;

        Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Aborted && handle == 0,
            "A canceled operation must expose its terminal status without a user handle.");
        Require(FindByLocalId(fixture, LocalUserServiceFixture.Profile.LocalId, &handle) == RuntimeServiceResult.UserNotFound && handle == 0,
            "Canceling before work must leave the local user unadded.");
        Require(fixture.Host.BoundaryFailures == 0, "Cancellation must not escape through an unmanaged boundary.");
    }

    private static void BeginAndScheduleFailuresDoNotReturnUsers()
    {
        using (var fixture = new LocalUserServiceFixture())
        {
            fixture.Host.BeginResult = RuntimeServiceResult.OutOfMemory;
            var block = new XAsyncBlock();

            Require(fixture.Add(&block) == RuntimeServiceResult.OutOfMemory && fixture.Host.QueuedWork == 0 && fixture.Host.CleanupCalls == 0,
                "Rejected Begin must retain no queued work and must not depend on a host cleanup callback.");

            fixture.Host.BeginResult = RuntimeServiceResult.Success;

            Require(fixture.Add(&block) == RuntimeServiceResult.Success && fixture.Host.DispatchWork(),
                "A failed Begin must not prevent retrying an unused async block.");

            var handle = (nint)0;

            Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Success && fixture.Host.CleanupCalls == 1,
                "A retried operation must own and clean up its new provider context.");

            fixture.Close(handle);
        }

        using (var fixture = new LocalUserServiceFixture())
        {
            fixture.Host.ScheduleResult = RuntimeServiceResult.OutOfMemory;
            var block = new XAsyncBlock { Callback = AuthoredLocalUserCallbacks.CompletionAddress };

            Require(fixture.Add(&block) == RuntimeServiceResult.Success && fixture.Host.QueuedWork == 0 &&
                fixture.Host.CleanupCalls == 1 && fixture.Host.QueuedCompletions == 1,
                "Accepted Begin may report scheduling failure through completion and must allow the host's synchronous cleanup.");

            var handle = (nint)71;

            Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.OutOfMemory && handle == 0,
                "A provider Begin scheduling failure must be returned by AddResult without a fabricated handle.");
            Require(fixture.Host.DispatchCompletion() && AuthoredLocalUserCallbacks.Completions == 1 && fixture.Host.BoundaryFailures == 0,
                "The scheduling failure must still deliver exactly one completion callback safely.");
        }
    }

    private static void AddOptionsFollowPublicContract()
    {
        using var fixture = new LocalUserServiceFixture();
        var block = new XAsyncBlock();

        foreach (var options in new uint[] { 3, 5, 7, 8, uint.MaxValue })
        {
            Require(fixture.Add(&block, options) == RuntimeServiceResult.InvalidArgument,
                "Invalid option bits, silent guest selection, and conflicting default-user modes must be rejected.");
        }

        Require(fixture.Add(null) == RuntimeServiceResult.InvalidArgument && fixture.Host.BeginCalls == 0,
            "Invalid Add inputs must fail before the host async service owns any context.");

        foreach (var options in new uint[] { 0, 1, 2, 4, 6 })
        {
            Require(fixture.Add(&block, options) == RuntimeServiceResult.Success && fixture.Host.DispatchWork(),
                "Every supported public Add option combination must use the asynchronous provider.");

            var handle = (nint)0;

            Require(fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Success && handle != 0,
                "A supported Add must return an owned local-user handle.");

            fixture.Close(handle);
        }

        Require(fixture.Host.CleanupCalls == 5 && fixture.Host.BoundaryFailures == 0,
            "Repeated supported Add calls must independently complete and clean up.");
    }

    private static void HandlesRemainOwnedAndIdentifiersRemainDistinct()
    {
        using var fixture = new LocalUserServiceFixture();
        var original = AddUser(fixture);
        var duplicate = (nint)0;
        var duplicateCall = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)fixture.Method(0x18);
        var compare = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)fixture.Method(0x28);

        Require(duplicateCall(fixture.UserInterface, original, &duplicate) == RuntimeServiceResult.Success &&
            duplicate != 0 && duplicate != original && compare(fixture.UserInterface, original, duplicate) == 0,
            "Duplicate must create separate handle ownership while Compare identifies the same local user.");

        var localId = 0UL;
        var userId = 0UL;
        var readLocalId = (delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)fixture.Method(0x48);
        var readUserId = (delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)fixture.Method(0x58);

        Require(readLocalId(fixture.UserInterface, duplicate, &localId) == RuntimeServiceResult.Success &&
            readUserId(fixture.UserInterface, duplicate, &userId) == RuntimeServiceResult.Success &&
            localId == LocalUserServiceFixture.Profile.LocalId && userId == LocalUserServiceFixture.Profile.UserId && localId != userId,
            "Local ID and user ID slots must retain distinct full-width identifiers.");

        fixture.Close(original);
        fixture.Close(original);

        Require(readLocalId(fixture.UserInterface, original, &localId) == RuntimeServiceResult.InvalidHandle && localId == 0 &&
            readLocalId(fixture.UserInterface, duplicate, &localId) == RuntimeServiceResult.Success,
            "Closing one handle, even twice, must not invalidate its separately owned duplicate.");

        var foundLocal = (nint)0;
        var foundUser = (nint)0;
        var findUserId = (delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)fixture.Method(0x60);

        Require(FindByLocalId(fixture, LocalUserServiceFixture.Profile.LocalId, &foundLocal) == RuntimeServiceResult.Success &&
            findUserId(fixture.UserInterface, LocalUserServiceFixture.Profile.UserId, &foundUser) == RuntimeServiceResult.Success &&
            foundLocal != duplicate && foundUser != foundLocal && foundUser != duplicate,
            "Find operations must issue new owned handles to the same added profile.");

        var missing = (nint)71;

        Require(FindByLocalId(fixture, LocalUserServiceFixture.Profile.UserId, &missing) == RuntimeServiceResult.UserNotFound && missing == 0 &&
            duplicateCall(fixture.UserInterface, unchecked((nint)0xFEDCBA1234567890UL), &missing) == RuntimeServiceResult.InvalidHandle && missing == 0,
            "Wrong identifiers and unknown opaque handles must not resolve to the local user.");

        fixture.Close(duplicate);
        fixture.Close(foundLocal);
        fixture.Close(foundUser);

        var reopened = (nint)0;

        Require(FindByLocalId(fixture, LocalUserServiceFixture.Profile.LocalId, &reopened) == RuntimeServiceResult.Success &&
            reopened != original && reopened != duplicate && reopened != foundLocal && reopened != foundUser,
            "Later handles must not alias previously closed tokens.");

        fixture.Close(reopened);
    }

    private static void NativeOutputsRespectTheirWidths()
    {
        using var fixture = new LocalUserServiceFixture();
        var handle = AddUser(fixture);
        var bytes = stackalloc byte[12];
        new Span<byte>(bytes, 12).Fill(0xCC);
        var isGuest = (delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)fixture.Method(0x68);
        var state = (delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)fixture.Method(0x70);
        var age = (delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)fixture.Method(0x98);

        Require(isGuest(fixture.UserInterface, handle, bytes + 4) == RuntimeServiceResult.Success && bytes[4] == 0 &&
            bytes[3] == 0xCC && bytes[5] == 0xCC,
            "The C++ bool output must write exactly one byte.");

        new Span<byte>(bytes, 12).Fill(0xCC);

        Require(state(fixture.UserInterface, handle, (uint*)(bytes + 4)) == RuntimeServiceResult.Success &&
            *(uint*)(bytes + 4) == 0 && bytes[3] == 0xCC && bytes[8] == 0xCC,
            "The user state output must write one unsigned 32-bit enum without crossing its bounds.");
        Require(age(fixture.UserInterface, handle, (uint*)(bytes + 4)) == RuntimeServiceResult.Success && *(uint*)(bytes + 4) == 3,
            "The local profile age-group slot must use the documented 32-bit representation.");
        Require(isGuest(fixture.UserInterface, 0, bytes + 4) == RuntimeServiceResult.InvalidHandle &&
            state(fixture.UserInterface, handle, null) == RuntimeServiceResult.InvalidArgument,
            "Invalid handles and missing output storage must be rejected.");

        fixture.Close(handle);
    }

    private static void UserInterfacesShareIdentity()
    {
        using var fixture = new LocalUserServiceFixture();
        var queryBase = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)fixture.Method(0);
        var queryGamertag = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)fixture.GamertagInterface)[0];
        var requested = LocalUserService.GamertagInterface;
        var result = (nint)0;

        Require(queryBase(fixture.UserInterface, &requested, &result) == RuntimeServiceResult.Success && result == fixture.GamertagInterface,
            "The base user interface must expose its gamertag extension through QueryInterface.");

        ReleaseInterface(result);
        requested = LocalUserService.Provider;

        Require(queryGamertag(fixture.GamertagInterface, &requested, &result) == RuntimeServiceResult.Success && result == fixture.UserInterface,
            "The gamertag extension must resolve back to the same base user interface.");

        ReleaseInterface(result);
        requested = RuntimeServiceObject.UnknownInterface;

        Require(queryGamertag(fixture.GamertagInterface, &requested, &result) == RuntimeServiceResult.Success && result == fixture.UserInterface,
            "All user interfaces must return one stable IUnknown identity.");

        ReleaseInterface(result);
        requested = new Guid("b976fe31-7ad7-4dba-8b54-620c7fed853e");
        result = 71;

        Require(queryBase(fixture.UserInterface, &requested, &result) == RuntimeServiceResult.NoInterface && result == 0,
            "Unknown interfaces must fail without returning a provider object.");
    }

    private static void GamertagWritesRespectCapacity()
    {
        using var fixture = new LocalUserServiceFixture();
        var handle = AddUser(fixture);
        var expected = Encoding.UTF8.GetBytes(LocalUserServiceFixture.Profile.Gamertag + '\0');
        var bytes = stackalloc byte[32];

        for (var interfaceIndex = 0; interfaceIndex < 2; interfaceIndex++)
        {
            var used = (nuint)71;
            new Span<byte>(bytes, 32).Fill(0xCC);

            Require(ReadGamertag(fixture, interfaceIndex != 0, handle, 0, (nuint)expected.Length - 1, bytes + 1, &used) == RuntimeServiceResult.InsufficientBuffer &&
                used == 0 && new ReadOnlySpan<byte>(bytes, 32).IndexOfAnyExcept((byte)0xCC) < 0,
                "An undersized gamertag buffer must remain untouched and report no bytes written.");
            Require(ReadGamertag(fixture, interfaceIndex != 0, handle, 0, (nuint)expected.Length, bytes + 1, &used) == RuntimeServiceResult.Success &&
                used == (nuint)expected.Length && new ReadOnlySpan<byte>(bytes + 1, expected.Length).SequenceEqual(expected) &&
                bytes[0] == 0xCC && bytes[expected.Length + 1] == 0xCC,
                "Both gamertag interfaces must write the exact UTF-8 payload and terminator within capacity.");
        }

        var componentBytes = (nuint)0;

        Require(ReadGamertag(fixture, true, handle, 2, 1, bytes + 1, &componentBytes) == RuntimeServiceResult.Success && componentBytes == 1 && bytes[1] == 0,
            "The profile's absent modern suffix must be an empty terminated string.");
        Require(ReadGamertag(fixture, true, handle, 4, 31, bytes + 1, &componentBytes) == RuntimeServiceResult.InvalidArgument && componentBytes == 0,
            "Unknown gamertag components must be rejected.");

        fixture.Close(handle);
    }

    private static void UnsupportedServicesNeverReturnCredentials()
    {
        using var fixture = new LocalUserServiceFixture();
        var handle = AddUser(fixture);
        var beginCalls = fixture.Host.BeginCalls;
        var block = new XAsyncBlock { Callback = AuthoredLocalUserCallbacks.CompletionAddress };
        var buffer = stackalloc byte[16];

        foreach (var startOffset in new[] { 0xB8, 0xD0 })
        {
            var begin = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, nuint, nint, nuint, nint, XAsyncBlock*, int>)fixture.Method(startOffset);
            var sizeCall = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint*, int>)fixture.Method(startOffset + 8);
            var resultCall = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint, nint, nint*, nuint*, int>)fixture.Method(startOffset + 16);
            var size = (nuint)71;
            var token = (nint)71;
            var used = (nuint)71;
            new Span<byte>(buffer, 16).Fill(0xCC);

            Require(begin(fixture.UserInterface, handle, 0, 0, 0, 0, 0, 0, 0, &block) == RuntimeServiceResult.NotImplemented &&
                sizeCall(fixture.UserInterface, &block, &size) == RuntimeServiceResult.NotImplemented && size == 0,
                "Unsupported token operations must fail synchronously and report no result capacity.");
            Require(resultCall(fixture.UserInterface, &block, 16, (nint)buffer, &token, &used) == RuntimeServiceResult.NotImplemented &&
                token == 0 && used == 0 && new ReadOnlySpan<byte>(buffer, 16).IndexOfAnyExcept((byte)0xCC) < 0,
                "Token result calls must expose no token, signature, or fabricated output bytes.");
        }

        Require(fixture.Host.BeginCalls == beginCalls && AuthoredLocalUserCallbacks.Completions == 0,
            "Unsupported account services must not claim to have scheduled an asynchronous completion.");

        fixture.Close(handle);
    }

    private static void RegistrationsHaveBoundedOwnership()
    {
        using var fixture = new LocalUserServiceFixture();
        var register = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint, ulong*, int>)fixture.Method(0x108);
        var unregister = (delegate* unmanaged[Stdcall]<nint, ulong, byte, byte>)fixture.Method(0x110);
        var tokens = new ulong[256];

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = 0UL;

            Require(register(fixture.UserInterface, 0, 0, AuthoredLocalUserCallbacks.ChangeAddress, &token) == RuntimeServiceResult.Success &&
                token != 0 && (index == 0 || token > tokens[index - 1]),
                "Every accepted change registration must have distinct increasing ownership.");

            tokens[index] = token;
        }

        var overflow = 71UL;

        Require(register(fixture.UserInterface, 0, 0, AuthoredLocalUserCallbacks.ChangeAddress, &overflow) == RuntimeServiceResult.OutOfMemory && overflow == 0,
            "The registration limit must reject additional registrations without issuing a token.");
        Require(unregister(fixture.UserInterface, tokens[0], 1) == 1 && unregister(fixture.UserInterface, tokens[0], 0) == 0 &&
            unregister(fixture.UserInterface, ulong.MaxValue, 0) == 0,
            "Unregister must consume only existing registration ownership.");
        Require(register(fixture.UserInterface, 0, 0, AuthoredLocalUserCallbacks.ChangeAddress, &overflow) == RuntimeServiceResult.Success && overflow > tokens[^1],
            "Removing a registration must restore capacity without reusing its token.");

        var handle = AddUser(fixture);

        Require(AuthoredLocalUserCallbacks.Changes == 0,
            "The first successful Add must not fabricate a SignedInAgain event for an immutable profile.");

        fixture.Close(handle);

        for (var index = 1; index < tokens.Length; index++)
        {
            Require(unregister(fixture.UserInterface, tokens[index], 0) == 1, "Every remaining registration must unregister once.");
        }

        Require(unregister(fixture.UserInterface, overflow, 0) == 1,
            "The replacement registration must retain independent ownership.");
    }

    private static nint AddUser(LocalUserServiceFixture fixture)
    {
        var block = new XAsyncBlock();
        var handle = (nint)0;

        Require(fixture.Add(&block) == RuntimeServiceResult.Success && fixture.Host.DispatchWork() &&
            fixture.TakeResult(&block, &handle) == RuntimeServiceResult.Success && handle != 0,
            "The authored local-user setup must finish through host-provider work and result retrieval.");

        return handle;
    }

    private static int FindByLocalId(LocalUserServiceFixture fixture, ulong identifier, nint* handle)
    {
        return ((delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)fixture.Method(0x50))(fixture.UserInterface, identifier, handle);
    }

    private static int ReadGamertag(LocalUserServiceFixture fixture, bool modern, nint handle, uint component,
        nuint capacity, byte* buffer, nuint* used)
    {
        if (modern)
        {
            var call = (delegate* unmanaged[Stdcall]<nint, nint, uint, nuint, byte*, nuint*, int>)(*(nint**)fixture.GamertagInterface)[3];

            return call(fixture.GamertagInterface, handle, component, capacity, buffer, used);
        }

        var legacyCall = (delegate* unmanaged[Stdcall]<nint, nint, nuint, byte*, nuint*, int>)fixture.Method(0x78);

        return legacyCall(fixture.UserInterface, handle, capacity, buffer, used);
    }

    private static void ReleaseInterface(nint address)
    {
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)address)[2])(address);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
