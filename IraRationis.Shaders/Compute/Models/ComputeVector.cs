// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Compute.Models;

/// <summary>Four raw 32-bit lanes; floating-point values preserve their bit representation.</summary>
public readonly record struct ComputeVector(uint X, uint Y, uint Z, uint W)
{
    public uint Lane(int index)
    {
        return index switch
        {
            0 => X,
            1 => Y,
            2 => Z,
            3 => W,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}
