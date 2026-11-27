/*
 * @file        DirectStorageBcPackTests.cs
 * @description Verifies BC1 decoding and publication with authored byte fixtures.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     The fixtures cover exact blocks, malformed inputs, dictionary lookups, and unchanged output after rejection.
 *
 * See the repository license and attribution notices.
 */

using IraRationis.HLE.Storage;
using IraRationis.HLE.Storage.BcPack;
using IraRationis.HLE.Storage.Models;

namespace IraRationis.Runtime.Tests.Contracts;

internal static class DirectStorageBcPackTests
{
    internal static void Run()
    {
        // Authored from the uniform rANS model and the eight-byte feeder. The twelve lanes encode a
        // red endpoint residual and four different uniform quadrants, with no preset dictionary references.
        var packed = Convert.FromHexString(
            "5050505050505050030303030303030380801020101020104040000401010401" +
            "0000200000000000414100050202050250505050030303038080808000004000" +
            "0406000200004100");
        Span<byte> block = stackalloc byte[8];
        ReadOnlySpan<byte> expected = [0x00, 0x08, 0x00, 0x00, 0x50, 0x50, 0xfa, 0xfa];

        Require(BcPackBc1Decoder.TryDecode(packed, block) && block.SequenceEqual(expected),
            "Authored BC1 endpoints and quadrant indices");
        Require(!BcPackBc1Decoder.TryDecode(packed.AsSpan(0, packed.Length - 1), block),
            "Truncated BC1 input is refused");
        Require(!BcPackBc1Decoder.TryDecode(packed, block[..^1]), "Partial BC1 destination block is refused");
        Require(!BcPackBc1Decoder.TryDecode([.. packed, 0xff], block), "BC1 input with trailing data is refused");

        // The second block predicts its endpoints from the first. Some lanes need a refill byte.
        var packedPair = Convert.FromHexString(
            "d0d0d0d0d0d0d0d00304030303030303040410401010401000010002000002" +
            "000400840814140814110201040202040205d0d0d0d0030303030404040400" +
            "0000000404040409090909");
        Span<byte> blockPair = stackalloc byte[16];
        ReadOnlySpan<byte> expectedPair =
        [
            0x00, 0x08, 0x00, 0x00, 0xaa, 0xaa, 0xaa, 0xaa,
            0x00, 0x08, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff
        ];

        Require(BcPackBc1Decoder.TryDecode(packedPair, blockPair) && blockPair.SequenceEqual(expectedPair),
            "BC1 refills and prior-block prediction");

        var dictionaryReference = Convert.FromHexString(
            "5050505050505050030303030303030380801020101020100040000401010401" +
            "0400200000000000004100050202050250505050030303038080808000004000" +
            "0406000200004100");

        Require(!BcPackBc1Decoder.TryDecode(dictionaryReference, block),
            "BC1 preset references require a known dictionary entry");

        var history = new uint?[255];
        var quadrants = new byte?[256];

        history[0] = 0;

        Require(BcPackBc1Dictionary.TryCreate(history, quadrants, out var dictionary) &&
                BcPackBc1Decoder.TryDecode(dictionaryReference, block, dictionary) && block.SequenceEqual(expected),
            "BC1 uses the supplied first preset entry");

        quadrants[4] = 0;

        Require(!BcPackBc1Dictionary.TryCreate(history, quadrants, out _),
            "An additional quadrant cannot duplicate the exact uniform selector");

        var entropyState = 0x0100_0001U;

        Require(BcPackEntropyModel.Decode(ref entropyState, model: 12, width: 5) == 0 &&
                entropyState == 53_290U * 256,
            "Nonuniform entropy model uses the exact integer frequency");

        VerifyStoragePublication(packed, expected);

        Console.WriteLine("[dstorage-probe] Authored BC1 block and malformed-input checks passed.");
    }

    private static unsafe void VerifyStoragePublication(byte[] packed, ReadOnlySpan<byte> expected)
    {
        Span<byte> destination = stackalloc byte[expected.Length];

        fixed (byte* source = packed)
        fixed (byte* output = destination)
        {
            var request = new StorageRequest
            {
                Options = (1UL << 22) | 2,
                Source = (nint)source,
                SourceSize = (uint)packed.Length,
                Destination = (nint)output,
                DestinationSize = (uint)destination.Length
            };

            Require(DirectStorageModule.Execute(request, 0, out var written) == 0 &&
                    written == expected.Length && destination.SequenceEqual(expected),
                "BC1 bytes reach the caller through DirectStorage");

            destination.Fill(0xA5);
            request.SourceSize--;

            Require(DirectStorageModule.Execute(request, 0, out written) < 0 &&
                    written == 0 && destination.IndexOfAnyExcept((byte)0xA5) == -1,
                "Failed BC1 request leaves the caller's destination untouched");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"DirectStorage BCPack contract failed. {message}.");
        }
    }
}
