// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Resources.Models;

internal sealed class VersionedUploadBufferSnapshot(ulong version, int offset, byte[] bytes)
{
    internal ulong Version
    {
        get;
    } = version;

    internal int Offset
    {
        get;
    } = offset;

    internal ReadOnlySpan<byte> Bytes
    {
        get
        {
            return bytes;
        }
    }
}
