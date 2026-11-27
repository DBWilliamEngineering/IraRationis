/*
 * @file        BcPackEntropyModel.cs
 * @description Builds integer probability models for BCPack rANS decoding.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     Integer cumulative frequencies determine symbol boundaries and must match the format exactly.
 *
 * See the repository license and attribution notices.
 */

namespace IraRationis.HLE.Storage.BcPack;

internal static class BcPackEntropyModel
{
    private const int ProbabilityBits = 16;
    private const int ProbabilityTotal = 1 << ProbabilityBits;
    private const int ModelCount = 16;
    private const int MaximumWidth = 8;

    private static readonly int[][] CumulativeFrequencies = BuildModels();

    internal static byte Decode(ref uint state, int model, int width)
    {
        var cumulative = CumulativeFrequencies[(width - 1) * ModelCount + model];
        var slot = (int)(state & (ProbabilityTotal - 1));
        var lower = 0;
        var upper = cumulative.Length - 1;

        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;

            if (cumulative[middle] <= slot)
            {
                lower = middle;
            }
            else
            {
                upper = middle;
            }
        }

        var frequency = (uint)(cumulative[lower + 1] - cumulative[lower]);

        state = frequency * (state >> ProbabilityBits) + (uint)(slot - cumulative[lower]);

        return (byte)lower;
    }

    private static int[][] BuildModels()
    {
        var models = new int[MaximumWidth * ModelCount][];
        // Parameters from the published BCPack bitstream specification, checked against Silicon-rANS.
        ReadOnlySpan<double> decay = [
            0, 0.7399434, 1.4962494, 2.30715745, 3.22101565, 4.3073742, 5.6759508, 7.50811,
            10.1124375, 14.03737, 20.35237, 31.4462, 53.67977, 109.2579, 328.076, 3000
        ];

        for (var width = 1; width <= MaximumWidth; width++)
        {
            var symbolCount = 1 << width;

            models[(width - 1) * ModelCount] = BuildUniformModel(symbolCount);

            for (var model = 1; model < ModelCount; model++)
            {
                models[(width - 1) * ModelCount + model] = BuildGeometricModel(width, symbolCount, decay[model]);
            }
        }

        return models;
    }

    private static int[] BuildUniformModel(int symbolCount)
    {
        var cumulative = new int[symbolCount + 1];

        cumulative[0] = 1;

        for (var symbol = 1; symbol < cumulative.Length; symbol++)
        {
            cumulative[symbol] = symbol * (ProbabilityTotal / symbolCount);
        }

        return cumulative;
    }

    private static int[] BuildGeometricModel(int width, int symbolCount, double decay)
    {
        Span<int> boundaries = stackalloc int[MaximumWidth + 2];

        boundaries[width + 1] = ProbabilityTotal;

        var distributionTotal = 1.0 - Math.Exp(-decay);

        for (var group = 1; group <= width; group++)
        {
            var firstSymbol = 1 << (group - 1);
            var distribution = 1.0 - Math.Exp(-decay * firstSymbol / symbolCount);

            boundaries[group] = (int)Math.Round(ProbabilityTotal * distribution / distributionTotal);
        }

        var cumulative = new int[symbolCount + 1];

        cumulative[0] = 1;
        cumulative[symbolCount] = ProbabilityTotal;

        for (var group = width; group >= 1; group--)
        {
            var groupSize = 1 << (group - 1);
            var upper = boundaries[group + 1];
            var lower = Math.Min(boundaries[group], upper - groupSize);
            var frequency = Math.Max(1, (int)Math.Round((double)(upper - lower) / groupSize));

            boundaries[group] = upper - frequency * groupSize;

            for (var index = 0; index < groupSize; index++)
            {
                cumulative[groupSize + index] = boundaries[group] + index * frequency;
            }
        }

        return cumulative;
    }
}
