// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Pipeline.Conversion;

namespace IraRationis.Runtime.Tests.Contracts;

internal sealed class FoundationTestsUpperConverter(uint version) : IConverter
{
    public int Runs
    {
        get;
        private set;
    }

    public string Id
    {
        get;
    } = "test.upper";

    public uint Version
    {
        get;
    } = version;

    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        Runs++;

        var output = input.ToArray();

        for (var index = 0; index < output.Length; index++)
        {
            if (output[index] is >= (byte)'a' and <= (byte)'z')
            {
                output[index] -= 32;
            }
        }

        return output;
    }
}
