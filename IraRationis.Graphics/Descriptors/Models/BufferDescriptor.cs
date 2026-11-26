// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Descriptors.Models;

internal readonly record struct BufferDescriptor(
    DescriptorResourceIdentity Resource,
    BufferViewKind Kind,
    ulong Offset,
    ulong Length);
