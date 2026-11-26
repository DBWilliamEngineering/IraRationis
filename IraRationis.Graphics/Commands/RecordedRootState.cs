// SPDX-License-Identifier: GPL-3.0-or-later
// ABI adapted from DexNom/VIctory crates/victory-gpu/src/list.rs and object.rs.


namespace IraRationis.Graphics.Commands;

internal sealed unsafe class RecordedRootState
{
    public readonly Dictionary<uint, uint[]> Constants = new();
    public readonly Dictionary<uint, ulong> Tables = new();
    public readonly Dictionary<uint, (uint Kind, ulong Address)> Views = new();
    public nint Signature;

    public void ClearArguments()
    {
        Tables.Clear();

        Constants.Clear();

        Views.Clear();
    }
}
