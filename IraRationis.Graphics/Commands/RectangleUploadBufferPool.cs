// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands.Models;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Commands;

internal sealed class RectangleUploadBufferPool(int smallPageBytes)
{
    private const int MaximumIdleBuffers = 48;
    private const int MaximumIdleBytes = 64 * 1024 * 1024;
    private const int MaximumIdleSmallPages = 8;
    private readonly Lock _gate = new();
    private readonly RectangleUploadBuffer[] _idleBuffers = new RectangleUploadBuffer[MaximumIdleBuffers];
    private nint _device;
    private int _idleBytes;
    private int _idleCount;
    private int _idleSmallPages;

    internal bool TryRent(nint device, int bytes, out RectangleUploadBuffer buffer)
    {
        lock (_gate)
        {
            if (_device != device)
            {
                for (var index = 0; index < _idleCount; index++)
                {
                    _ = GraphicsDevice.HostRelease(_idleBuffers[index].Host);
                    _idleBuffers[index] = default;
                }

                _device = device;
                _idleCount = 0;
                _idleBytes = 0;
                _idleSmallPages = 0;
            }

            // O(1) maximum work: the idle list is capped at 48 buffers.
            for (var index = _idleCount - 1; index >= 0; index--)
            {
                if (_idleBuffers[index].Bytes != bytes)
                {
                    continue;
                }

                buffer = _idleBuffers[index];
                _idleCount--;
                _idleBuffers[index] = _idleBuffers[_idleCount];
                _idleBuffers[_idleCount] = default;
                _idleBytes -= bytes;
                if (bytes == smallPageBytes)
                {
                    _idleSmallPages--;
                }

                return true;
            }

            buffer = default;

            return false;
        }
    }

    internal bool TryReturn(RectangleUploadBuffer buffer)
    {
        lock (_gate)
        {
            var smallPage = buffer.Bytes == smallPageBytes;
            if (buffer.Host == 0 || buffer.Bytes <= 0 || buffer.Device != _device || _idleCount == MaximumIdleBuffers ||
                buffer.Bytes > MaximumIdleBytes - _idleBytes || (smallPage && _idleSmallPages == MaximumIdleSmallPages))
            {
                return false;
            }

            _idleBuffers[_idleCount] = buffer;
            _idleCount++;
            _idleBytes += buffer.Bytes;
            if (smallPage)
            {
                _idleSmallPages++;
            }

            return true;
        }
    }
}
