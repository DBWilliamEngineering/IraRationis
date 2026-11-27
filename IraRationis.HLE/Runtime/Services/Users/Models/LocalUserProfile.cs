// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Services.Users.Models;

internal sealed record LocalUserProfile(ulong LocalId, ulong UserId, string Gamertag);
