// SPDX-License-Identifier: GPL-3.0-or-later
// Explicit thread notifications outside the Windows loader lock.


namespace IraRationis.HLE.Kernel.Models;

internal sealed record KernelThreadThreadStart(nint Routine, nint Parameter);
