// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Commands.Models;

internal readonly record struct RectangleUploadBuffer(nint Device, nint Host, ulong GpuAddress, int Bytes);
