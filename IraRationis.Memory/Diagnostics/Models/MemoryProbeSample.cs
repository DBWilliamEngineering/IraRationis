// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Memory.Diagnostics.Models;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MemoryProbeSample
{
    public double StagingMs;
    public double CopyMs;
    public double ComputeMs;
    public double SubmitMs;
    public uint Mismatches;
    public uint Reserved;
}
