// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Diagnostics.Models;

internal readonly record struct RuntimeBridgeImage(string Name, nuint BaseAddress, uint Size);
