// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Commands.Models;

internal readonly record struct RecordedTargetView(
    nint Resource,
    long ResourceGeneration,
    nint HostResource,
    nuint CpuHandle,
    uint Format);
