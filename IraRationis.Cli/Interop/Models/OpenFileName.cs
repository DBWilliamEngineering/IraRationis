// SPDX-License-Identifier: GPL-3.0-or-later
// Ira Rationis: native Windows file selection, independent of GUI frameworks.

using System.Runtime.InteropServices;

namespace IraRationis.Cli.Interop.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct OpenFileName
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
