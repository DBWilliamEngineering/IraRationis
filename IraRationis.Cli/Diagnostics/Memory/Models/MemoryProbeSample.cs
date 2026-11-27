/*
* @file        MemoryProbeSample.cs
* @description Holds the timings and checksum results from one memory probe iteration.
* @author      Damon William
* @date        2026-11-27
*/
// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Cli.Diagnostics.Memory.Models;

internal readonly record struct MemoryProbeSample(double StagingMs, double CopyMs, double ComputeMs,
    double SubmitMs, uint Mismatches);
