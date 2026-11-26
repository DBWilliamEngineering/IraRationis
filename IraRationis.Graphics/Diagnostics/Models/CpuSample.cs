// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

internal readonly record struct CpuSample(uint ThreadId, long CpuTicks, long WallTimestamp);
