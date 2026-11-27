/*
* @file        PresentationProbeWindow.cs
* @description Owns the synthetic presentation probe window on the calling thread.
* @author      Damon William
* @date        2026-11-27
*
* See the repository license and attribution notices.
*/

using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using Win32 = global::TerraFX.Interop.Windows.Windows;

namespace IraRationis.Graphics.Presentation;

internal sealed unsafe class PresentationProbeWindow : IDisposable
{
    private const string ClassName = "IrxPresentationProbe";
    private const string Title = "Ira Rationis | First Light | Synthetic frame probe";
    private const int SystemArrowCursorId = 32512;
    private const int DefaultWindowCoordinate = int.MinValue;
    private bool _ownsClass;
    private HINSTANCE _instance;

    internal HWND Handle { get; private set; }

    internal bool IsOpen
    {
        get
        {
            return Handle != default && Win32.IsWindow(Handle);
        }
    }

    internal int Create(int width, int height)
    {
        _instance = (HINSTANCE)Win32.GetModuleHandleW(null);

        fixed (char* className = ClassName, title = Title)
        {
            var windowClass = new WNDCLASSW
            {
                lpfnWndProc = &WindowProcedure,
                hInstance = _instance,
                hCursor = Win32.LoadCursorW(default, (char*)SystemArrowCursorId),
                lpszClassName = className
            };

            _ownsClass = RegisterClassW(&windowClass) != 0;

            if (!_ownsClass)
            {
                var error = (uint)Marshal.GetLastPInvokeError();

                if (error != ERROR.ERROR_CLASS_ALREADY_EXISTS)
                {
                    return Win32.HRESULT_FROM_WIN32(error);
                }
            }

            const uint style = WS.WS_OVERLAPPED | WS.WS_CAPTION | WS.WS_SYSMENU | WS.WS_MINIMIZEBOX;
            var dimensions = new RECT { right = width, bottom = height };
            _ = Win32.AdjustWindowRect(&dimensions, style, false);

            Handle = CreateWindowExW(0, className, title, style, DefaultWindowCoordinate, DefaultWindowCoordinate,
                dimensions.right - dimensions.left, dimensions.bottom - dimensions.top,
                default, default, _instance, null);

            if (Handle == default)
            {
                return Marshal.GetHRForLastWin32Error();
            }
        }

        _ = Win32.ShowWindow(Handle, SW.SW_SHOW);

        return 0;
    }

    internal void ProcessMessages()
    {
        var message = default(MSG);

        while (Win32.PeekMessageW(&message, default, 0, 0, PM.PM_REMOVE))
        {
            _ = Win32.TranslateMessage(&message);
            _ = Win32.DispatchMessageW(&message);
        }
    }

    public void Dispose()
    {
        if (IsOpen)
        {
            _ = Win32.DestroyWindow(Handle);
        }

        Handle = default;

        if (_ownsClass)
        {
            fixed (char* className = ClassName)
            {
                _ = Win32.UnregisterClassW(className, _instance);
            }

            _ownsClass = false;
        }
    }

    [UnmanagedCallersOnly]
    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wordParameter, LPARAM longParameter)
    {
        if (message == WM.WM_CLOSE)
        {
            _ = Win32.DestroyWindow(window);

            return default;
        }

        return Win32.DefWindowProcW(window, message, wordParameter, longParameter);
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClassW(WNDCLASSW* windowClass);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HWND CreateWindowExW(uint extendedStyle, char* className, char* title, uint style,
        int coordinateX, int coordinateY, int width, int height, HWND parent, HMENU menu, HINSTANCE instance,
        void* parameter);
}
