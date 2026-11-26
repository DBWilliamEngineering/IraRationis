// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Descriptors.Models;

internal readonly record struct ComputeBufferTable(
    BufferDescriptor Constant,
    BufferDescriptor Input,
    BufferDescriptor Output);
