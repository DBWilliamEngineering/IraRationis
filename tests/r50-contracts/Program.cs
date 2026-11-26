// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Cli;
using IraRationis.Media;

namespace IraRationis.R50.Contracts;

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
        using var output = new StringWriter();
        using var writer = new RuntimeLogSampling.SamplingWriter(output);

        for (var index = 0; index < 4601; index++)
        {
            writer.WriteLine("[graphics] Resource view: dimension=3, width=32, layout=0x0, address=0x1000 (CPU backing only).");
        }

        for (var index = 0; index < 625; index++)
        {
            writer.WriteLine("[graphics] Command list type 0 created.");
        }

        writer.WriteLine("[graphics] Command list type 0 creation failed.");

        writer.WriteLine("[graphics] Resource view: dimension=3 refused.");

        writer.WriteLine("[audio] untouched");

        writer.Report();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Require(lines.Count(line => line.EndsWith(" (CPU backing only).", StringComparison.Ordinal)) == 14,
            "Resource success samples");

        Require(lines.Count(line => line == "[graphics] Command list type 0 created.") == 11,
            "Command list success samples");

        Require(lines.Contains("[graphics] Command list type 0 creation failed."), "Errors preserved");

        Require(lines.Contains("[graphics] Resource view: dimension=3 refused."), "Refusals preserved");

        Require(lines.Contains("[audio] untouched"), "Unrelated messages preserved");

        Require(string.Equals(lines[^1],
            "[performance] IRX-R50-LOG: resourceLines=4601; resourceEmitted=14; listLines=625; listEmitted=11.",
            StringComparison.Ordinal), $"Summary counters: {lines[^1]}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
