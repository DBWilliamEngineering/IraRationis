// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Cli.Diagnostics;

/// <summary>
///     Opt-in experiment at the diagnostic output boundary. It samples only two
///     known repetitive success messages, not errors or compatibility evidence.
///     The runtime still performs the original string formatting and console locking.
/// </summary>
internal sealed class RuntimeLogSampling : IDisposable
{
    private readonly TextWriter _installed;
    private readonly TextWriter _original;
    private readonly SamplingLogWriter _sampling;
    private bool _disposed;

    private RuntimeLogSampling()
    {
        _original = Console.Error;

        _sampling = new SamplingLogWriter(_original);

        Console.SetError(_sampling);

        _installed = Console.Error;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (ReferenceEquals(Console.Error, _installed))
        {
            Console.SetError(_original);
        }

        try
        {
            _sampling.Report();
        }
        catch (Exception)
        {
            // A closed diagnostic output must not replace the runtime result.
        }
    }

    internal static RuntimeLogSampling? Begin()
    {
        if (Environment.GetEnvironmentVariable("IRX_SAMPLE_GRAPHICS_LOGS") != "1" ||
            Environment.GetEnvironmentVariable("IRX_VERBOSE_GRAPHICS") == "1")
        {
            return null;
        }

        return new RuntimeLogSampling();
    }
}
