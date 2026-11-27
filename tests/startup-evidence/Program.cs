// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Diagnostics;
using IraRationis.Media.Playback;

namespace StartupEvidence.Contracts;

internal static class Program
{
    private static int Main()
    {
        TestTimeline();

        TestLogSampling();

        Console.WriteLine("IRX-R50 CPU contracts passed.");

        return 0;
    }

    private static void TestTimeline()
    {
        var timeline = new HostFrameTimeline(60);

        Require(timeline.TrySelect(0, out var first) && first == 0, "Opening frame");

        timeline.Record(true);

        Require(!timeline.TrySelect(0.01, out _), "No duplicate opening frame");

        Require(timeline.TrySelect(0.11, out var later) && later == 3, "Clock catchup");

        timeline.Record(false);

        Require(timeline.Presented == 1 && timeline.Skipped == 3, "Rejected and missed frames counted");

        Require(!timeline.TrySelect(0.05, out _), "Clock regression cannot repeat frames");

        Require(timeline.TrySelect(2, out var last) && last == 59, "End clamped to last frame");

        timeline.Record(true);

        timeline.Finish();

        Require(timeline.Presented == 2 && timeline.Skipped == 58, "Complete accounting");

        var complete = new HostFrameTimeline(3);

        for (var index = 0; index < 3; index++)
        {
            Require(complete.TrySelect((index + 0.1) / 30, out var actual) && actual == index,
                "Sequential frame");

            complete.Record(true);
        }

        complete.Finish();

        Require(complete.Presented == 3 && complete.Skipped == 0, "No dropped frames");

        var early = new HostFrameTimeline(3);

        Require(early.TrySelect(0, out _), "First frame selected");

        early.Record(true);

        early.Finish();

        Require(early.Skipped == 2, "Early completion accounts for remaining frames");
    }

    private static void TestLogSampling()
    {
        var sampling = new GraphicsLogSampling(sampleGraphics: true, sampleListKinds: false, verbose: false);
        var emittedResources = 0;
        var emittedLists = 0;

        for (var index = 0; index < 4601; index++)
        {
            if (sampling.ShouldEmitResourceView())
            {
                emittedResources++;
            }
        }

        for (var index = 0; index < 625; index++)
        {
            if (sampling.ShouldEmitCommandList(0, out _))
            {
                emittedLists++;
            }
        }

        Require(emittedResources == 14, "Resource success samples");
        Require(emittedLists == 11, "Command list success samples");
        Require(sampling.FormatSummary() ==
                "[performance] IRX-R50-LOG: resourceLines=4601; resourceEmitted=14; listLines=625; listEmitted=11.",
            "Sampling summary");

        var perKind = new GraphicsLogSampling(sampleGraphics: false, sampleListKinds: true, verbose: false);
        for (var index = 1; index <= 4; index++)
        {
            Require(perKind.ShouldEmitCommandList(0, out var kindCount) && kindCount == index,
                "Command list kind initial samples");
        }

        Require(!perKind.ShouldEmitCommandList(0, out _), "Command list kind sample skipped");
        Require(perKind.ShouldEmitCommandList(1, out var secondKindCount) && secondKindCount == 1,
            "Each command list kind has an independent sequence");
        Require(perKind.ShouldEmitCommandList(uint.MaxValue, out var unknownKindCount) && unknownKindCount == 0,
            "Unknown command list kinds avoid the per-kind counter");

        var verbose = new GraphicsLogSampling(sampleGraphics: true, sampleListKinds: true, verbose: true);
        Require(!verbose.Enabled, "Verbose logging disables success sampling");
        for (var index = 0; index < 9; index++)
        {
            Require(verbose.ShouldEmitResourceView() && verbose.ShouldEmitCommandList(0, out var kindCount) &&
                    kindCount == 0, "Verbose logging emits every original success message");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
