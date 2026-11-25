// SPDX-License-Identifier: GPL-3.0-or-later
// Ira Rationis: native Windows file selection, independent of GUI frameworks.

using System.Runtime.InteropServices;

namespace IraRationis.Cli;

internal static unsafe partial class ExecutablePicker
{
    private const uint Explorer = 0x00080000;
    private const uint FileMustExist = 0x00001000;
    private const uint PathMustExist = 0x00000800;
    private const uint NoChangeDirectory = 0x00000008;
    private const int BufferLength = 32768;

    internal static string? Select()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Executable selection requires Windows.");
        }

        var buffer = new char[BufferLength];
        const string filter = "Executable files (*.exe)\0*.exe\0\0";
        const string title = "Ira Rationis | Select the title executable";

        fixed (char* file = buffer, filterPointer = filter, titlePointer = title)
        {
            var options = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName),
                Owner = 0,
                Instance = 0,
                Filter = filterPointer,
                CustomFilter = null,
                MaxCustomFilter = 0,
                FilterIndex = 1,
                File = file,
                MaxFile = BufferLength,
                FileTitle = null,
                MaxFileTitle = 0,
                InitialDirectory = null,
                Title = titlePointer,
                Flags = Explorer | FileMustExist | PathMustExist | NoChangeDirectory,
                FileOffset = 0,
                FileExtension = 0,
                DefaultExtension = null,
                CustomData = 0,
                Hook = 0,
                TemplateName = null,
                Reserved = 0,
                ReservedValue = 0,
                ExtendedFlags = 0
            };

            if (GetOpenFileNameW(&options) == 0)
            {
                var error = CommDlgExtendedError();

                if (error != 0)
                {
                    throw new InvalidOperationException($"Windows file selection failed (0x{error:X8}).");
                }

                return null;
            }

            var path = new string(file);

            if (!File.Exists(path)
                || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Select an existing executable file.");
            }

            return path;
        }
    }

    [LibraryImport("comdlg32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetOpenFileNameW(OpenFileName* options);

    [LibraryImport("comdlg32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint Size;
        public nint Owner;
        public nint Instance;
        public char* Filter;
        public char* CustomFilter;
        public uint MaxCustomFilter;
        public uint FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDirectory;
        public char* Title;
        public uint Flags;
        public ushort FileOffset;
        public ushort FileExtension;
        public char* DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public char* TemplateName;
        public nint Reserved;
        public uint ReservedValue;
        public uint ExtendedFlags;
    }
}
