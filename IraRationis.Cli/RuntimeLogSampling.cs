// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace IraRationis.Cli;

/// <summary>
///     Opt-in experiment at the diagnostic output boundary. It samples only two
///     known repetitive success messages, not errors or compatibility evidence.
///     The runtime still performs the original string formatting and console locking.
/// </summary>
internal sealed class RuntimeLogSampling : IDisposable
{
    private readonly TextWriter _installed;
    private readonly TextWriter _original;
    private readonly SamplingWriter _sampling;
    private bool _disposed;

    private RuntimeLogSampling()
    {
        _original = Console.Error;
        _sampling = new SamplingWriter(_original);
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

    internal sealed class SamplingWriter(TextWriter target) : TextWriter
    {
        private long _emittedLists;
        private long _emittedResources;
        private long _lists;
        private long _resources;

        public override Encoding Encoding
        {
            get
            {
                return target.Encoding;
            }
        }

        public override IFormatProvider FormatProvider
        {
            get
            {
                return target.FormatProvider;
            }
        }

        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith("[graphics] Resource view: dimension=", StringComparison.Ordinal)
                                  &&
                                  value.EndsWith(" (CPU backing only).", StringComparison.Ordinal) &&
                                  value.AsSpan().IndexOfAny('\r', '\n') < 0)
            {
                if (ShouldEmit(Interlocked.Increment(ref _resources)))
                {
                    target.WriteLine(value);
                    Interlocked.Increment(ref _emittedResources);
                }

                return;
            }

            if (value is not null && IsCommandListCreation(value))
            {
                if (ShouldEmit(Interlocked.Increment(ref _lists)))
                {
                    target.WriteLine(value);
                    Interlocked.Increment(ref _emittedLists);
                }

                return;
            }

            target.WriteLine(value);
        }

        private static bool IsCommandListCreation(string value)
        {
            const string prefix = "[graphics] Command list type ";
            const string suffix = " created.";
            if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
                !value.EndsWith(suffix, StringComparison.Ordinal) ||
                value.Length <= prefix.Length + suffix.Length)
            {
                return false;
            }

            var kind = value.AsSpan(prefix.Length, value.Length - prefix.Length - suffix.Length);
            foreach (var character in kind)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ShouldEmit(long count)
        {
            return count <= 4 || (count & (count - 1)) == 0;
        }

        public override void Write(char value)
        {
            target.Write(value);
        }

        public override void Write(string? value)
        {
            target.Write(value);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            target.Write(buffer);
        }

        public override void WriteLine()
        {
            target.WriteLine();
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            target.WriteLine(buffer);
        }

        public override void Flush()
        {
            target.Flush();
        }

        internal void Report()
        {
            target.WriteLine(FormattableString.Invariant(
                $"[performance] IRX-R50-LOG: resourceLines={Interlocked.Read(ref _resources)}; resourceEmitted={Interlocked.Read(ref _emittedResources)}; listLines={Interlocked.Read(ref _lists)}; listEmitted={Interlocked.Read(ref _emittedLists)}."));
        }
    }
}
