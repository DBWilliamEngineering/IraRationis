// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using IraRationis.Cli.Commands;
using IraRationis.Cli.Diagnostics.Instructions;
using IraRationis.Cli.Diagnostics.Network;
using IraRationis.Cli.Interop;
using IraRationis.Graphics.Device;
using IraRationis.Graphics.Diagnostics;
using IraRationis.Graphics.Presentation;
using IraRationis.Loader.Imports.Models;
using IraRationis.Loader.Instructions;
using IraRationis.Memory.Diagnostics;

namespace IraRationis.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            PrintUsage(Console.Out);

            return 0;
        }

        try
        {
            if (args.Length > 0 && args[0] == "network-trace")
            {
                return NetworkTraceCommand.Execute(args[1..], Console.Out);
            }

            if (args.Length > 0 && args[0] == "media-probe")
            {
                return MediaProbeCommand.Execute(args[1..], Console.Out);
            }

            if (args is ["graphics-probe"])
            {
                return DeviceProbe.Execute(Console.Out);
            }

            switch (args.Length)
            {
                case > 0 when args[0] == "present-probe":
                    return PresentationProbe.Execute(args[1..], Console.Out);

                case > 0 when args[0] == "memory-probe":
                    return MemoryProbe.Execute(args[1..], Console.Out);
            }

            if (args is ["veh-probe"])
            {
                ConsoleInstructionHandler.VerifyManagedExceptionDispatch();

                Console.WriteLine("Native VEH managed exception probe passed.");

                return 0;
            }

            if (args is ["instruction-probe"])
            {
                InstructionMemoryProbe.Run(Console.Out);

                return 0;
            }

            if (args.Length == 0 || args[0] == "run" || (args.Length == 1
                                                         && Path.GetExtension(args[0]).Equals(".exe",
                                                             StringComparison.OrdinalIgnoreCase)))
            {
                return Run(args);
            }

            if (args.Length > 2 || args[0] is not ("inspect" or "shader" or "archive")
                                || (args[0] != "inspect" && args.Length != 2))
            {
                PrintUsage(Console.Error);

                return 2;
            }

            var path = args.Length == 2 ? args[1] : ExecutablePicker.Select();

            if (path is null)
            {
                return 0;
            }

            var data = File.ReadAllBytes(path);

            switch (args[0])
            {
                case "inspect":
                    InspectCommand.Execute(data, Console.Out);

                    break;

                case "shader":
                    ShaderCommand.Execute(data, Console.Out);

                    break;

                case "archive":
                    ArchiveCommand.Execute(data, Console.Out);

                    break;
            }

            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or UnauthorizedAccessException or ArgumentException or OverflowException
                                              or InvalidOperationException or PlatformNotSupportedException
                                              or Win32Exception or DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException or TimeoutException)
        {
            Console.Error.WriteLine(exception.Message);

            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? path = null;
        var bindOnly = false;
        var strict = false;
        var console = ConsoleModel.SeriesX;
        var policy = UnimplementedPolicy.Trap;
        var start = args.Length > 0 && args[0] == "run" ? 1 : 0;

        for (var index = start; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--bind-only":
                    bindOnly = true;

                    break;

                case "--strict":
                    strict = true;

                    break;

                case "--execute":
                    break;

                case "--console" when index + 1 < args.Length:
                    console = args[++index] switch
                    {
                        "series-x" => ConsoleModel.SeriesX,
                        "series-s" => ConsoleModel.SeriesS,
                        _ => throw new ArgumentException("Expected series-x or series-s.")
                    };

                    break;

                case "--unimplemented" when index + 1 < args.Length:
                    policy = args[++index] switch
                    {
                        "trap" => UnimplementedPolicy.Trap,
                        "zero" => UnimplementedPolicy.ReturnZero,
                        _ => throw new ArgumentException("Expected trap or zero.")
                    };

                    break;

                default:
                    if (path is not null || args[index].StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown argument: {args[index]}");
                    }

                    path = args[index];

                    break;
            }
        }

        path ??= ExecutablePicker.Select();

        return path is null
            ? 0
            : RunCommand.Execute(path, bindOnly, strict, console, policy);
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Ira Rationis | Vindex | 0.1.0");

        writer.WriteLine(
            "Usage: irx [file] | irx run [file] [--bind-only] [--strict] [--console series-x|series-s] [--unimplemented trap|zero]");

        writer.WriteLine("       irx veh-probe");

        writer.WriteLine("       irx instruction-probe");

        writer.WriteLine("       irx network-trace <pid> <output.etl> [seconds:1..300] (elevated diagnostic terminal)");

        writer.WriteLine("       irx graphics-probe");

        writer.WriteLine("       irx present-probe [--seconds 1..120] [--fps 1..240] [--gpu-upload]");

        writer.WriteLine("       irx memory-probe [--mib 1..256] [--iterations 1..100] [--adapter N]");

        writer.WriteLine("       irx memory-probe --self-test");

        writer.WriteLine("       irx media-probe <ffmpeg.exe> <local clip> [--play|--stream|--gpu|--stream-gpu]");

        writer.WriteLine("       irx media-probe --mf <local MP4> [--play|--gpu]");

        writer.WriteLine("       irx inspect [file] | irx shader <file> | irx archive <file>");

        writer.WriteLine(
            "Omit the run path to open the Windows executable picker. Run only trusted title files; this is not a sandbox.");
    }
}
