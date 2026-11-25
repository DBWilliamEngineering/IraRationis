// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-pe/src/image.rs and layout.rs, Zettrox/ztrx.

namespace IraRationis.PE;

public readonly record struct DirectoryEntry(uint Rva, uint Size)
{
    public bool Present
    {
        get
        {
            return Rva != 0 && Size != 0;
        }
    }
}
