// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.PE.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

public interface IHostFunctionObserver
{
    nuint Observe(string library, ImportSymbol symbol, nuint address);
}
