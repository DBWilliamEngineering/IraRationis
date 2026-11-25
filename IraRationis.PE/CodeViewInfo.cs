// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-pe/src/image.rs and layout.rs, Zettrox/ztrx.

namespace IraRationis.PE;

public sealed record CodeViewInfo(string PdbPath, Guid Guid, uint Age)
{
    public string SymbolKey
    {
        get
        {
            return $"{Guid:N}{Age:X}".ToUpperInvariant();
        }
    }
}
