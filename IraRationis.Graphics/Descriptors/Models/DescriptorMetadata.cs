// SPDX-License-Identifier: GPL-3.0-or-later
// Console descriptor ABI adapted from Zettrox/ztrx crates/victory-gpu/src/descriptor.rs.


namespace IraRationis.Graphics.Descriptors.Models;

internal sealed unsafe record DescriptorMetadata(
    uint Kind,
    nint Resource,
    long ResourceGeneration,
    nint Counter,
    long CounterGeneration,
    byte[]? Description,
    byte[]? Extra);
