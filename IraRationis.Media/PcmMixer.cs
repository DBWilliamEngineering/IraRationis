using System.Buffers.Binary;

namespace IraRationis.Media;

/// <summary>Mixes equal-length interleaved float PCM streams with explicit gain and saturation.</summary>
public static class PcmMixer
{
    public static void Mix(ReadOnlySpan<float> first, ReadOnlySpan<float> second,
        float firstGain, float secondGain, Span<float> output)
    {
        if (first.Length != second.Length || output.Length != first.Length ||
            !float.IsFinite(firstGain) || !float.IsFinite(secondGain))
        {
            throw new ArgumentException("Incompatible streams or gains.");
        }

        if ((first.Overlaps(output, out var firstOffset) && firstOffset != 0) ||
            (second.Overlaps(output, out var secondOffset) && secondOffset != 0))
        {
            throw new ArgumentException("Partial overlap is unsupported.");
        }

        for (var i = 0; i < output.Length; i++)
        {
            var a = float.IsFinite(first[i]) ? first[i] : 0;
            var b = float.IsFinite(second[i]) ? second[i] : 0;
            var mixed = (double)a * firstGain + (double)b * secondGain;

            output[i] = (float)Math.Clamp(mixed, -1, 1);
        }
    }

    public static byte[] ToWave(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2 || samples.IsEmpty ||
            samples.Length % channels != 0)
        {
            throw new ArgumentException("Invalid PCM format or sample count.");
        }

        var dataBytes = checked(samples.Length * 2);
        var wave = new byte[checked(44 + dataBytes)];

        "RIFF"u8.CopyTo(wave);

        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);

        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));

        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);

        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);

        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), (ushort)channels);

        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), sampleRate);

        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), sampleRate * channels * 2);

        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), (ushort)(channels * 2));

        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);

        "data"u8.CopyTo(wave.AsSpan(36));

        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), samples[i]);
        }

        return wave;
    }
}
