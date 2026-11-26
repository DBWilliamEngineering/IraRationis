// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using IraRationis.Graphics;
using IraRationis.Media;

namespace IraRationis.GpuMedia.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--clip"))
        {
            Console.Error.WriteLine("Usage: GpuMedia [--clip <local authorized MP4>]");

            return 2;
        }

        TestInvalidInput();
        TestPattern(2, 2);
        TestPattern(320, 180);
        TestPattern(514, 34);

        if (args.Length == 2)
        {
            TestClip(args[1]);
        }

        Console.WriteLine("IRX-R42 GPU media contracts passed.");

        return 0;
    }

    private static void TestPattern(int width, int height)
    {
        // Padded source rows are intentional, distinct from D3D12's upload pitches.
        var yStride = width + 7;
        var uvStride = width + 13;
        var y = new byte[checked(yStride * height)];
        var uv = new byte[checked(uvStride * height / 2)];
        var random = new Random(42 + width);

        random.NextBytes(y);
        random.NextBytes(uv);

        // Explicit neutral black and white pixels, and a non-neutral chroma block.
        y[0] = 16;
        y[1] = 235;
        y[yStride] = 81;
        y[yStride + 1] = 145;
        uv[0] = 128;
        uv[1] = 128;

        if (width >= 4)
        {
            uv[2] = 16;
            uv[3] = 240;
        }

        var frame = Nv12UploadFrame.Capture(y, yStride, uv, uvStride, width, height, 0);

        VerifyFrame(frame, y, yStride, uv, uvStride, "synthetic");
    }

    private static void TestClip(string path)
    {
        var clip = MediaFoundationDecoder.Decode(path);
        var yBytes = checked(clip.Width * clip.Height);
        var indexes = new[] { 0, clip.FrameCount / 2, clip.FrameCount - 1 };

        foreach (var index in indexes.Distinct())
        {
            var pixels = clip.GetFrame(index);
            var y = pixels[..yBytes];
            var uv = pixels[yBytes..];
            var frame = Nv12UploadFrame.Capture(y, clip.Width, uv, clip.Width,
                clip.Width, clip.Height, index * 10_000_000L / 30);

            VerifyFrame(frame, y, clip.Width, uv, clip.Width, $"clip frame {index}");
        }
    }

    private static void VerifyFrame(Nv12UploadFrame frame, ReadOnlySpan<byte> y, int yStride,
        ReadOnlySpan<byte> uv, int uvStride, string name)
    {
        var bytes = checked(frame.Width * frame.Height * 4);
        var expected = new byte[bytes];
        var actual = new byte[bytes];

        frame.ConvertToBgra(expected, frame.Width * 4);
        actual.AsSpan().Fill(0xCD);

        ConsoleGraphicsDevice.ConvertNv12FrameOnGpu(y, yStride, uv, uvStride,
            frame.Width, frame.Height, actual, Console.Out);

        var mismatches = 0;
        var maximumError = 0;
        var first = -1;

        for (var i = 0; i < actual.Length; i++)
        {
            var error = Math.Abs(actual[i] - expected[i]);
            maximumError = Math.Max(maximumError, error);

            if (error != 0)
            {
                if (first < 0)
                {
                    first = i;
                }

                mismatches++;
            }
        }

        Console.WriteLine($"[media] IRX-R42: {name}; size={frame.Width}x{frame.Height}; " +
                          $"channelsCompared={bytes}; mismatches={mismatches}; maxError={maximumError}; " +
                          $"SHA256={Convert.ToHexString(SHA256.HashData(actual))}.");

        if (first >= 0)
        {
            throw new InvalidOperationException($"GPU/CPU mismatch at byte {first}: " +
                                                $"expected {expected[first]}, actual {actual[first]}.");
        }
    }

    private static void TestInvalidInput()
    {
        try
        {
            ConsoleGraphicsDevice.ConvertNv12FrameOnGpu(new byte[4], 2, new byte[2], 2,
                3, 2, new byte[24], Console.Out);
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.WriteLine("[media] IRX-R42: invalid dimensions rejected before native submission.");

            return;
        }

        throw new InvalidOperationException("Odd NV12 dimensions were accepted.");
    }
}
