// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Cli.Diagnostics.Network.Models;

internal readonly record struct NetworkTraceStatistics(uint EventsLost, uint BuffersLost, uint BuffersWritten);
