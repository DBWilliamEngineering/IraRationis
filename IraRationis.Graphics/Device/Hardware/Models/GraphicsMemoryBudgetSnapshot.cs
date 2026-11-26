// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Device.Hardware.Models;

public sealed record GraphicsMemoryBudgetSnapshot
{
    public ulong BudgetBytes { get; }

    public ulong CurrentUsageBytes { get; }

    public ulong HeadroomBytes
    {
        get
        {
            return CurrentUsageBytes >= BudgetBytes ? 0 : BudgetBytes - CurrentUsageBytes;
        }
    }

    public GraphicsMemoryBudgetSnapshot(ulong budgetBytes, ulong currentUsageBytes)
    {
        BudgetBytes = budgetBytes;
        CurrentUsageBytes = currentUsageBytes;
    }
}
