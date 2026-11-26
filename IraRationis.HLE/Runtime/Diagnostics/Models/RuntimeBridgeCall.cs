// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Diagnostics.Models;

internal readonly record struct RuntimeBridgeCall(
    RuntimeBridgeDiagnostics? Session,
    RuntimeBridgeOperation Operation,
    long Id,
    RuntimeBridgeQueryObservation? Query = null);
