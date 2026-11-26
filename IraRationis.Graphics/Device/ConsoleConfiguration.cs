// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-gpu/src/console.rs.


namespace IraRationis.Graphics.Device;

public static class ConsoleConfiguration
{
    private const ulong GiB = 1UL << 30;
    private static int _current;

    public static ConsoleModel Current
    {
        get
        {
            return (ConsoleModel)Volatile.Read(ref _current);
        }
        set
        {
            Volatile.Write(ref _current, (int)value);
        }
    }

    public static (ulong GpuOptimal, ulong Standard) GetTitleMemory(ConsoleModel model)
    {
        return model switch
        {
            ConsoleModel.SeriesX => (10 * GiB, 7 * GiB / 2),
            ConsoleModel.SeriesS => (0UL, 7_776UL << 20),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
    }

    public static string GetName(ConsoleModel model)
    {
        return model switch
        {
            ConsoleModel.SeriesX => "Xbox Series X",
            ConsoleModel.SeriesS => "Xbox Series S",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
    }
}
