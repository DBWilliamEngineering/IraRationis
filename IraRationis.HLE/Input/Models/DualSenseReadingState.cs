// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.


namespace IraRationis.HLE.Input.Models;

internal sealed record DualSenseReadingState(GameInputPadState Pad);
