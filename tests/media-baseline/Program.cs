using System.Buffers.Binary;
using IraRationis.Media.Audio;
using IraRationis.Media.Decoding;
using IraRationis.Media.Video;

VerifyPrimitives();

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--play"))
{
    Console.WriteLine("Usage: MediaBaseline <ffmpeg.exe> <local clip.mp4> [--play]");

    return 2;
}

var clip = await LocalMediaDecoder.DecodeAsync(args[0], args[1]);
var directory = Path.Combine("artifacts", "media-baseline", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
var report = MediaClipInspector.Inspect(clip, directory);

Console.WriteLine(
    $"[media] frames={report.Frames}; unique={report.DistinctFrames}; audioSamples={report.AudioSamples}; peak={report.Peak}");

Console.WriteLine($"[media] Output: {report.OutputDirectory}");

if (report.DistinctFrames < 2 || report.Peak == 0)
{
    throw new InvalidDataException("Expected moving video and non-silent audio in the synthetic fixture.");
}

if (args.Length == 3)
{
    WindowsWaveOutput.Play(clip.Pcm, clip.SampleRate, clip.Channels);
}

return 0;

static void VerifyPrimitives()
{
    var output = new byte[16];

    Nv12Converter.Convert([16, 235, 16, 235], 2, [128, 128], 2, 2, 2, output, 8);

    if (!output.AsSpan()
            .SequenceEqual(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 }))
    {
        throw new InvalidDataException("NV12 conversion regression.");
    }

    var mixed = new float[4];

    PcmMixer.Mix([0.5f, -0.75f, float.NaN, 0.25f], [0.75f, -0.75f, 0.5f, -0.25f], 1, 1, mixed);

    if (!mixed.AsSpan().SequenceEqual([1, -1, 0.5f, 0]))
    {
        throw new InvalidDataException("PCM mixer regression.");
    }

    var wave = PcmMixer.ToWave([short.MinValue, short.MaxValue], 48000, 2);

    if (wave.Length != 48 || BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)) != 4 ||
        BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44)) != short.MinValue)
    {
        throw new InvalidDataException("WAVE serialization regression.");
    }

    Console.WriteLine("[media] Primitive checks passed.");
}
