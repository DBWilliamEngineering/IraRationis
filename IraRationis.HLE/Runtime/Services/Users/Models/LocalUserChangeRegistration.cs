// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Services.Users.Models;

internal sealed record LocalUserChangeRegistration(nint Queue, nint Context, nint Callback);
