// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Diagnostics;

internal static partial class InputDiagnosticNativeMethods
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
}
