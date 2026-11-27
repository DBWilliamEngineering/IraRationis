// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Media.Models;

internal sealed class HostMediaShaderEntry(string source, TextWriter report, long lastUse)
{
    public readonly Lazy<byte[]> Bytecode = new(
        () => ConsoleGraphicsDevice.CompileUncachedHostMediaShader(source, report),
        LazyThreadSafetyMode.ExecutionAndPublication);
    public bool Accounted;
    public long LastUse = lastUse;
    public int Size;
}
