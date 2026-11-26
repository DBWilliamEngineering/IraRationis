// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-loader/src/prepare.rs.

using IraRationis.PE.Parsing;

namespace IraRationis.Loader.Images.Models;

public sealed record PreparedImage(PeImage Info, byte[] Bytes, string OriginalHash)
{
    public string FileName
    {
        get
        {
            return $"irx-image-{OriginalHash[..16]}.dll";
        }
    }
}
