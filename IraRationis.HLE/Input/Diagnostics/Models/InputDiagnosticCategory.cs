// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Input.Diagnostics.Models;

internal enum InputDiagnosticCategory
{
    CurrentReading,
    GuestReading,
    GuestButtonsChanged,
    GuestYPressed,
    GuestYReleased,
    HidReading,
    HidButtonsChanged,
    HidYPressed,
    HidYReleased,
    DeviceConnection,
    ReadingRegistration,
    DeviceRegistration,
    DeviceRegistrationFailure,
    DeviceCallbackEnter,
    DeviceCallbackReturn,
    DeviceCallbackSkipped,
    FindDeviceSuccess,
    FindDeviceFailure
}
