// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Media;

/// <summary>Thread-affine Win32 software presenter for decoded BGRA frames.</summary>
internal sealed unsafe partial class WindowsVideoWindow : IDisposable
{
    private readonly string _className = $"IRX-Media-{Guid.NewGuid():N}";
    private readonly int _height;
    private readonly nint _instance;
    private readonly int _width;
    private bool _registered;
    private nint _window;

    internal WindowsVideoWindow(int width, int height)
    {
        _width = width;
        _height = height;
        _instance = GetModuleHandle(null);
        fixed (char* name = _className)
        {
            var wc = new WindowClass
            {
                Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                Instance = _instance,
                ClassName = (nint)name
            };
            if (RegisterClass(&wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        _registered = true;
        try
        {
            _window = CreateWindowEx(0, _className, "Ira Rationis | Media playback", 0x10CF0000,
                unchecked((int)0x80000000), unchecked((int)0x80000000), 960, 580, 0, 0, _instance, 0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch
        {
            Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }

        if (_registered)
        {
            UnregisterClass(_className, _instance);
            _registered = false;
        }
    }

    internal bool Pump()
    {
        while (PeekMessage(out var message, _window, 0, 0, 1) != 0)
        {
            if (message.Message is 0x0010 or 0x0012 || (message.Message == 0x0100 && message.WParam == 27))
            {
                return false;
            }

            TranslateMessage(&message);
            DispatchMessage(&message);
        }

        return IsWindow(_window) != 0;
    }

    internal bool Draw(ReadOnlySpan<byte> pixels)
    {
        if (GetClientRect(_window, out var rect) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || IsIconic(_window) != 0)
        {
            return false;
        }

        var scale = Math.Min(width / (double)_width, height / (double)_height);
        var w = Math.Max(1, (int)(_width * scale));
        var h = Math.Max(1, (int)(_height * scale));
        var x = (width - w) / 2;
        var y = (height - h) / 2;
        var dc = GetDC(_window);
        if (dc == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            // Clear only the letterbox bars, not the video rectangle.
            if (x > 0 || width - x - w > 0)
            {
                PatBlt(dc, 0, 0, x, height, 0x42);
                PatBlt(dc, x + w, 0, width - x - w, height, 0x42);
            }

            if (y > 0 || height - y - h > 0)
            {
                PatBlt(dc, 0, 0, width, y, 0x42);
                PatBlt(dc, 0, y + h, width, height - y - h, 0x42);
            }

            var info = new BitmapHeader { Size = 40, Width = _width, Height = -_height, Planes = 1, BitCount = 32 };
            fixed (byte* data = pixels)
            {
                var lines = StretchDIBits(dc, x, y, w, h, 0, 0, _width, _height, data, &info, 0, 0x00CC0020);
                if (lines == 0 || lines == -1)
                {
                    throw new InvalidOperationException("StretchDIBits failed.");
                }
            }

            return true;
        }
        finally
        {
            ReleaseDC(_window, dc);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        return message == 0x0014 ? 1 : DefWindowProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterClass(WindowClass* wc);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnregisterClass(string name, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PeekMessage(out MessageData message, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int TranslateMessage(MessageData* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DispatchMessage(MessageData* message);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("gdi32.dll", EntryPoint = "PatBlt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PatBlt(nint dc, int x, int y, int width, int height, uint rop);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StretchDIBits(nint dc, int x, int y, int width, int height, int sx, int sy, int sw,
        int sh, byte* data, BitmapHeader* info, uint usage, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageData
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint ColorsUsed, Important;
    }
}
