// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Media.Presentation.Models;

namespace IraRationis.Media.Presentation;

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
            var windowClass = new VideoWindowClass
            {
                Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                Instance = _instance,
                ClassName = (nint)name
            };

            if (RegisterClass(&windowClass) == 0)
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
        var scaledWidth = Math.Max(1, (int)(_width * scale));
        var scaledHeight = Math.Max(1, (int)(_height * scale));
        var coordinateX = (width - scaledWidth) / 2;
        var coordinateY = (height - scaledHeight) / 2;
        var deviceContext = GetDC(_window);

        if (deviceContext == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            // Clear only the letterbox bars, not the video rectangle.
            if (coordinateX > 0 || width - coordinateX - scaledWidth > 0)
            {
                PatBlt(deviceContext, 0, 0, coordinateX, height, 0x42);

                PatBlt(deviceContext, coordinateX + scaledWidth, 0, width - coordinateX - scaledWidth, height, 0x42);
            }

            if (coordinateY > 0 || height - coordinateY - scaledHeight > 0)
            {
                PatBlt(deviceContext, 0, 0, width, coordinateY, 0x42);

                PatBlt(deviceContext, 0, coordinateY + scaledHeight, width, height - coordinateY - scaledHeight, 0x42);
            }

            var info = new VideoBitmapHeader
                { Size = 40, Width = _width, Height = -_height, Planes = 1, BitCount = 32 };

            fixed (byte* data = pixels)
            {
                var lines = StretchDIBits(deviceContext, coordinateX, coordinateY, scaledWidth, scaledHeight, 0, 0,
                    _width, _height, data, &info, 0, 0x00CC0020);

                if (lines == 0 || lines == -1)
                {
                    throw new InvalidOperationException("StretchDIBits failed.");
                }
            }

            return true;
        }
        finally
        {
            ReleaseDC(_window, deviceContext);
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
    private static partial ushort RegisterClass(VideoWindowClass* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnregisterClass(string name, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int coordinateX, int coordinateY,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PeekMessage(out VideoWindowMessage message, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int TranslateMessage(VideoWindowMessage* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DispatchMessage(VideoWindowMessage* message);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetClientRect(nint hwnd, out VideoWindowRect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseDC(nint hwnd, nint deviceContext);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("gdi32.dll", EntryPoint = "PatBlt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PatBlt(nint deviceContext, int coordinateX, int coordinateY, int width, int height,
        uint rop);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StretchDIBits(nint deviceContext, int coordinateX, int coordinateY, int width,
        int height, int sourceX, int sourceY, int sourceWidth,
        int sourceHeight, byte* data, VideoBitmapHeader* info, uint usage, uint rop);
}
