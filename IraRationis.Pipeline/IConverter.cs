// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-pipeline/src/lib.rs.

namespace IraRationis.Pipeline;

public interface IConverter
{
    string Id { get; }

    uint Version { get; }

    byte[] Convert(ReadOnlySpan<byte> input);
}
