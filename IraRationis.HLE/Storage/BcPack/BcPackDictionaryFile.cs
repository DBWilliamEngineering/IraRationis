/*
 * @file        BcPackDictionaryFile.cs
 * @description Loads optional local BC1 dictionary data for the storage pipeline.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     The first BC1 request loads and validates the data once for all storage workers.
 *
 * See the repository license and attribution notices.
 */

using System.Text.Json;

namespace IraRationis.HLE.Storage.BcPack;

internal static unsafe class BcPackDictionaryFile
{
    private const int MaximumFileBytes = 32 * 1024;

    private static readonly Lazy<BcPackBc1Dictionary> LocalDictionary = new(Load);

    internal static BcPackBc1Dictionary Dictionary
    {
        get
        {
            return LocalDictionary.Value;
        }
    }

    private static BcPackBc1Dictionary Load()
    {
        var path = Environment.GetEnvironmentVariable("IRX_BCPACK_BC1_DICTIONARY");

        if (string.IsNullOrWhiteSpace(path))
        {
            return BcPackBc1Dictionary.Empty;
        }

        try
        {
            using var input = File.OpenRead(path);

            if (input.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Dictionary file exceeds 32 KiB.");
            }

            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var history = ReadTable(root.GetProperty("endpointHistory"), 255, uint.MaxValue);
            var selectors = ReadTable(root.GetProperty("quadrantCodes"), 256, byte.MaxValue);
            var quadrantCodes = new byte?[selectors.Length];

            for (var index = 0; index < selectors.Length; index++)
            {
                quadrantCodes[index] = (byte?)selectors[index];
            }

            if (!BcPackBc1Dictionary.TryCreate(history, quadrantCodes, out var dictionary) || dictionary is null)
            {
                throw new InvalidDataException("Dictionary contains an inconsistent BC1 selector table.");
            }

            Console.WriteLine("[dstorage] Local BC1 dictionary loaded.");

            return dictionary;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                         or JsonException or InvalidOperationException or KeyNotFoundException
                                         or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[dstorage] Local BC1 dictionary unavailable. {exception.Message}");

            return BcPackBc1Dictionary.Empty;
        }
    }

    private static uint?[] ReadTable(JsonElement table, int expectedEntries, uint maximum)
    {
        if (table.GetArrayLength() != expectedEntries)
        {
            throw new InvalidDataException($"Expected {expectedEntries} dictionary entries.");
        }

        var entries = new uint?[expectedEntries];
        var index = 0;

        foreach (var entry in table.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Null)
            {
                index++;

                continue;
            }

            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetUInt32(out var value) || value > maximum)
            {
                throw new InvalidDataException($"Invalid dictionary entry at index {index}.");
            }

            entries[index++] = value;
        }

        return entries;
    }
}
