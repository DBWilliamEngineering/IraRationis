// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Diagnostics.Models;

internal readonly record struct RuntimeBridgeQueryKey(
    Guid Provider,
    Guid RequestedInterface,
    bool ProviderReadable = true,
    bool InterfaceReadable = true);
