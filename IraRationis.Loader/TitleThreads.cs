// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/threads.rs and windows.rs.

using System.Runtime.ExceptionServices;

namespace IraRationis.Loader;

public sealed class TitleThreads
{
    private readonly List<LoadedImage> _attached = [];
    private readonly Lock _gate = new();
    private readonly HashSet<nuint> _quiet = [];

    public uint RunMain(LoadedImage main, IReadOnlyList<LoadedImage> dependencies)
    {
        var result = 0U;
        var failure = (ExceptionDispatchInfo?)null;
        var stackSize = checked((int)Math.Max(1UL << 20, main.Info.StackReserve));
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var image in dependencies)
                {
                    Console.WriteLine($"[launch] Attaching {image.Path}");
                    Attach(image);
                    Console.WriteLine("[launch] Attached.");
                }

                Console.WriteLine("[launch] Main image TLS callbacks.");
                Attach(main);
                Console.WriteLine("[launch] Calling native entry point.");
                result = main.ExecuteEntry();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }, stackSize)
        {
            Name = "title-main"
        };

        thread.Start();
        thread.Join();
        failure?.Throw();

        // Do not unmap: native workers may still hold pointers. The host exits after this call.
        return result;
    }

    public void Attach(LoadedImage image)
    {
        image.Notify(1, true);

        lock (_gate)
        {
            _attached.Add(image);
        }
    }

    public bool? DisableThreadCalls(nuint address)
    {
        lock (_gate)
        {
            var image = _attached.Find(item => item.BaseAddress == address);

            if (image is null)
            {
                return null;
            }

            if (image.Info.Tls is not null)
            {
                return false;
            }

            _quiet.Add(address);

            return true;
        }
    }

    public void NotifyThread(bool attaching)
    {
        var snapshot = Snapshot();

        if (!attaching)
        {
            Array.Reverse(snapshot);
        }

        foreach (var (image, dllMain) in snapshot)
        {
            image.Notify(attaching ? 2U : 3U, dllMain);
        }
    }

    private (LoadedImage Image, bool DllMain)[] Snapshot()
    {
        lock (_gate)
        {
            return _attached.Select(image => (image, !_quiet.Contains(image.BaseAddress))).ToArray();
        }
    }
}
