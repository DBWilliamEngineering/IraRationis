// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-pe/src/image.rs and layout.rs, Zettrox/ztrx.

namespace IraRationis.PE;

public sealed record SectionInfo(string Name, uint Rva, uint VirtualSize, uint FileOffset, uint FileSize, uint Characteristics)
{
    public uint BackedSize
    {
        get
        {
            return VirtualSize == 0 ? FileSize : Math.Min(VirtualSize, FileSize);
        }
    }
}
