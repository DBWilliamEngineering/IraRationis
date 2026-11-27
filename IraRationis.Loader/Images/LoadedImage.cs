// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/windows.rs.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using IraRationis.Core.Assets;
using IraRationis.Loader.Images.Models;
using IraRationis.Loader.Imports;
using IraRationis.Loader.Imports.Models;
using IraRationis.Loader.Memory;
using IraRationis.Loader.Threads;
using IraRationis.PE.Models;
using IraRationis.PE.Parsing;

namespace IraRationis.Loader.Images;

public sealed unsafe class LoadedImage
{
    private readonly nint _module;
    private bool _bound;

    private LoadedImage(nint module, string path, PreparedImage prepared)
    {
        _module = module;

        Path = path;

        Info = prepared.Info;

        OriginalHash = prepared.OriginalHash;
    }

    public nuint BaseAddress
    {
        get
        {
            return (nuint)_module;
        }
    }

    public PeImage Info
    {
        get;
    }

    public string Path
    {
        get;
    }

    public string OriginalHash
    {
        get;
    }

    public static LoadedImage Load(PreparedImage prepared, string directory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Native title mapping requires Windows x64.");
        }

        var folder = AssetPolicy.EnsureOutsideRepository(directory);

        Directory.CreateDirectory(folder);

        var path = System.IO.Path.Combine(folder, prepared.FileName);

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(prepared.Bytes))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllBytes(temporary, prepared.Bytes);

                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        var module = WindowsNative.LoadLibraryExW(path, 0, 0x800);

        if (module == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot map {path}");
        }

        var image = new LoadedImage(module, path, prepared);

        try
        {
            if (!image.Info.IsDll)
            {
                var count = TlsRedirector.Redirect(image.BaseAddress, image.Info);

                Console.WriteLine($"TLS loads redirected: {count}");
            }
        }
        catch
        {
            _ = WindowsNative.FreeLibrary(module);

            throw;
        }

        return image;
    }

    public nuint? Export(ImportSymbol symbol)
    {
        // Do not ask the OS to resolve a forwarded title export: it might load
        // an unprepared DLL outside the title resolver's search policy.
        var export = Info.Exports.FirstOrDefault(item => symbol.Name is { } name
            ? string.Equals(item.Name, name, StringComparison.Ordinal)
            : item.Ordinal == symbol.HintOrOrdinal);

        if (export?.Rva is { } rva)
        {
            return AddressAt(rva, 1);
        }

        return null;
    }

    public BindReport BindImports(IImportResolver resolver, UnimplementedPolicy policy)
    {
        if (_bound)
        {
            throw new InvalidOperationException("Image imports already bound.");
        }

        var missing = new List<MissingImport>();
        var slots = new List<(uint Rva, nuint Address)>();
        var hle = 0;
        var host = 0;
        var title = 0;

        foreach (var module in Info.Imports.Concat(Info.DelayImports))
        {
            foreach (var entry in module.Entries)
            {
                var resolution = resolver.Resolve(module.Dll, entry.Symbol);

                if (resolution is not { Address: not 0 } found)
                {
                    missing.Add(new MissingImport(module.Dll, entry.Symbol.ToString(), entry.IatSlotRva));

                    continue;
                }

                switch (found.Provenance)
                {
                    case ImportProvenance.Hle:
                        hle++;

                        break;

                    case ImportProvenance.Host:
                        host++;

                        break;

                    case ImportProvenance.Title:
                        title++;

                        break;
                }

                slots.Add((entry.IatSlotRva, found.Address));
            }
        }

        if (missing.Count > 0)
        {
            var thunks = new ImportThunks(missing.Count);

            foreach (var import in missing)
            {
                slots.Add((import.SlotRva, thunks.Emit(import, policy)));
            }

            thunks.Seal();
        }

        var bytes = new byte[8];

        foreach (var (rva, address) in slots)
        {
            var slot = AddressAt(rva, 8);

            BinaryPrimitives.WriteUInt64LittleEndian(bytes, address);

            ExecutableMemory.Write((nint)slot, bytes, false);
        }

        _bound = true;

        return new BindReport(hle, host, title, missing);
    }

    public void Notify(uint reason, bool dllMain)
    {
        EnsureBound();

        if (Info.Tls is { } tls)
        {
            foreach (var rva in tls.CallbackRvas)
            {
                var callback = (delegate* unmanaged[Stdcall]<nint, uint, nint, void>)ExecutableAddressAt(rva);

                callback(_module, reason, 0);
            }
        }

        if (dllMain && Info.IsDll && Info.EntryRva != 0)
        {
            var entry = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)ExecutableAddressAt(Info.EntryRva);
            var result = entry(_module, reason, 0);

            if (reason == 1 && result == 0)
            {
                throw new InvalidOperationException($"DllMain failed: {Path}");
            }
        }
    }

    public uint ExecuteEntry()
    {
        EnsureBound();

        if (Info.IsDll || Info.EntryRva == 0)
        {
            throw new InvalidOperationException("Expected an executable with an entry point.");
        }

        var entry = (delegate* unmanaged[Stdcall]<nint, uint>)ExecutableAddressAt(Info.EntryRva);

        return entry(0);
    }

    private nuint AddressAt(uint rva, uint size)
    {
        if ((ulong)rva + size > Info.SizeOfImage)
        {
            throw new InvalidDataException("Address exceeds mapped image.");
        }

        return BaseAddress + rva;
    }

    private nuint ExecutableAddressAt(uint rva)
    {
        if (!Info.Sections.Any(section =>
                (section.Characteristics & 0x20000000) != 0 && rva >= section.Rva
                                                            && (ulong)rva - section.Rva < section.VirtualSize))
        {
            throw new InvalidDataException($"Entry point 0x{rva:X} is not executable.");
        }

        return AddressAt(rva, 1);
    }

    private void EnsureBound()
    {
        if (!_bound)
        {
            throw new InvalidOperationException("Bind imports before entering title code.");
        }
    }
}
