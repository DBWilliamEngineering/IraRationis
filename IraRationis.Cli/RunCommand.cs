// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from apps/victory/src/run.rs; runtime bring-up, not gameplay parity.

using IraRationis.Core;
using IraRationis.Graphics;
using IraRationis.HLE;
using IraRationis.Loader;

namespace IraRationis.Cli;

internal static class RunCommand
{
    internal static int Execute(string executable, bool bindOnly, bool strict, ConsoleModel console,
        UnimplementedPolicy policy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Title execution requires Windows x64.");
        }

        var path = Path.GetFullPath(executable);
        var titleDirectory =
            Path.GetDirectoryName(path) ?? throw new InvalidDataException("Title directory not found.");
        var config = RuntimeConfig.Load();
        var cache = config.CacheSubdirectory("images");
        ConsoleConfiguration.Current = console;
        var main = LoadMain(path, cache, config);
        RuntimeState.SetMainImage(main.BaseAddress);
        RuntimeState.SetImagePath(main.BaseAddress, path);

        var threads = new TitleThreads();
        var modules = new[]
        {
            KernelThreadModule.Create(threads), KernelImageModule.Create(), XMemModule.Create(), DspModule.Create(),
            RootSignatureModule.Create(), FileSystemModule.Create(), GameInputModule.Create(),
            PhysicalPageModule.Create(), DirectStorageModule.Create(), GraphicsDeviceModule.Create(), XgModule.Create(),
            AcpHalModule.Create()
        };
        var registry = new HleRegistry(modules);
        var host = strict ? new HostPolicy([]) : new HostPolicy();
        var resolver = new HleResolver(registry, host);
        var dependencies = new TitleImages(titleDirectory, cache,
            dll => !host.Allows(dll) && !modules.Any(module => module.Serves(dll)), resolver);
        dependencies.MapDependencies(main.Info);

        foreach (var image in dependencies.Ordered)
        {
            var original = dependencies.GetOriginalPath(image);
            RuntimeState.SetImagePath(image.BaseAddress, original);
            Console.WriteLine($"Mapped {original} at 0x{image.BaseAddress:X}");
        }

        var manifestPath = Path.Combine(titleDirectory, "MicrosoftGame.config");
        var title = Title.FromManifest(File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : string.Empty);
        var storage = config.DataSubdirectory($"{title.Id:X8}/storage");
        RuntimeState.CurrentTitle = title with { Storage = storage };
        FileSystemModule.Mount('G', titleDirectory);
        FileSystemModule.Mount('T', config.DataSubdirectory($"{title.Id:X8}/temp"));
        ConsoleConfiguration.Current = console;
        Console.WriteLine($"Console: {ConsoleConfiguration.GetName(console)}");
        Console.WriteLine($"Title: {title.Id:X8}; storage: {storage}");

        foreach (var image in dependencies.Ordered.Append(main))
        {
            var report = image.BindImports(dependencies, policy);
            PrintReport(image, report);
        }

        if (bindOnly)
        {
            Console.WriteLine("Binding completed. No title code executed.");

            return 0;
        }

        Console.WriteLine(
            "Entering native title code (experimental runtime; unresolved calls may terminate execution).");
        Console.WriteLine("Platform HLE and graphics translation are not yet complete.");

        using var timers = new ConsoleTimers();
        using var handler = new ConsoleInstructionHandler(dependencies.Ordered.Append(main));
        Console.WriteLine("[launch] Native CPU exception handler installed.");
        var result = threads.RunMain(main, dependencies.Ordered);
        Console.WriteLine($"Title entry returned {result} (0x{result:X8}).");

        return unchecked((int)result);
    }

    private static LoadedImage LoadMain(string path, string cache, RuntimeConfig config)
    {
        var prepared = ImagePreparation.Prepare(File.ReadAllBytes(path));

        if (prepared.Info.IsDll || prepared.Info.EntryRva == 0)
        {
            throw new InvalidDataException("Select an x64 executable with an entry point.");
        }

        Console.WriteLine($"Build: {prepared.OriginalHash}");
        Console.WriteLine(
            $"[abi] Main image: {prepared.Info.ImportCount} regular import slots; {prepared.Info.DelayImports.Sum(module => module.Entries.Count)} delay import slots.");
        ConsoleGraphicsDevice.Preflight(Console.Out,
            config.CacheSubdirectory($"compatibility/{prepared.OriginalHash}"));
        var main = LoadedImage.Load(prepared, cache);
        Console.WriteLine($"Mapped {path} at 0x{main.BaseAddress:X} (preferred 0x{main.Info.ImageBase:X})");

        return main;
    }

    private static void PrintReport(LoadedImage image, BindReport report)
    {
        Console.WriteLine(
            $"Bound {Path.GetFileName(image.Path)}: {report.Hle} HLE, {report.Host} host, {report.Title} title, {report.Missing.Count} missing.");

        foreach (var missing in report.Missing)
        {
            Console.WriteLine($"  Missing: {missing.Dll}!{missing.Symbol}");
        }
    }
}
