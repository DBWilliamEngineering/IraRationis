// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Cli.Diagnostics.Models;

internal enum StartupPhase
{
    Preparing,
    ImageLoaded,
    DependenciesMapped,
    ImportsBound,
    BindOnlyComplete,
    EnteringNative,
    NativeReturned
}
