// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Core.Scheduling.Models;

internal readonly record struct FenceRetirementEntry<T>(ulong Fence, T Resource) where T : class, IDisposable;
