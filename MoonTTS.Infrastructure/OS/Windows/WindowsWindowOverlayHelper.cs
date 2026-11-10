using System.Runtime.InteropServices;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Infrastructure.OS.Windows;

/* File Structure:
        - ConfigureOverlayWindow(nint windowHandle, bool alwaysOnTop) : void
        - PrepareBeforeShow(nint mainWindowHandle, bool alwaysOnTop) : void
        - LinkChildOverlayWindow(nint childWindowHandle, nint ownerWindowHandle, bool alwaysOnTop) : void
        - ActivateOverlayWindow(nint windowHandle) : void
        - OnMainWindowDeactivated(nint mainWindowHandle, bool alwaysOnTop) : void
        - OnAfterHide(nint mainWindowHandle) : void
        - IsFullscreenApplication(nint window) : bool
        - MarkFullscreen(nint window, bool fullscreen) : void
        - GetTaskbarList() : nint
        - LibWin32
*/

/* Overlay helper for Windows. While a borderless fullscreen app is the foreground window, explorer.exe keeps the
   taskbar behind it. When the HUD takes focus, explorer.exe would raise the taskbar again unless the HUD is
   marked as a fullscreen window through ITaskbarList2::MarkFullscreenWindow, which is what this helper does.
   Only documented shell / user32 APIs of the Moon-TTS own windows are used; nothing touches other processes. */
public sealed class WindowsWindowOverlayHelper : IWindowOverlayHelper
{
    /* Foreground window before the HUD was shown, focus is handed back to it when the HUD hides */
    private nint _previousForegroundWindow;

    /* Secondary HUD window (e.g. settings) linked to the main window */
    private nint _linkedChildWindow;

    /* True when the window that was active before the HUD appeared is a fullscreen / borderless application */
    private bool _overlayingFullscreen;

    private nint _taskbarList;
    private bool _taskbarListFailed;

    /* Makes the window a tool window (no taskbar button, no Alt+Tab entry) and mirrors the topmost state */
    public void ConfigureOverlayWindow(nint windowHandle, bool alwaysOnTop)
    {
        if (windowHandle == 0) return;

        nint style = LibWin32.GetWindowLongPtrW(windowHandle, LibWin32.GWL_EXSTYLE);
        style |= (nint)LibWin32.WS_EX_TOOLWINDOW;
        style &= ~(nint)LibWin32.WS_EX_APPWINDOW;
        LibWin32.SetWindowLongPtrW(windowHandle, LibWin32.GWL_EXSTYLE, style);

        LibWin32.SetWindowPos(
            windowHandle,
            alwaysOnTop ? LibWin32.HWND_TOPMOST : LibWin32.HWND_NOTOPMOST,
            0, 0, 0, 0,
            LibWin32.SWP_NOMOVE | LibWin32.SWP_NOSIZE | LibWin32.SWP_NOACTIVATE | LibWin32.SWP_FRAMECHANGED);

        if (_overlayingFullscreen)
            MarkFullscreen(windowHandle, true);
    }

    /* Captures the foreground window and, when it is a fullscreen / borderless app, marks the HUD as fullscreen
       so the shell keeps the taskbar hidden once the HUD is activated regardless of alwaysOnTop */
    public void PrepareBeforeShow(nint mainWindowHandle, bool alwaysOnTop)
    {
        if (mainWindowHandle == 0) return;

        nint foreground = LibWin32.GetForegroundWindow();
        if (foreground != 0 && foreground != mainWindowHandle && foreground != _linkedChildWindow)
        {
            _previousForegroundWindow = foreground;
            _overlayingFullscreen = IsFullscreenApplication(foreground);
        }

        MarkFullscreen(mainWindowHandle, _overlayingFullscreen);
    }

    /* Makes the owner window the Win32 owner of the secondary window, so the OS keeps the pair together and activates the
       owner (instead of an unrelated application behind the HUD) when the secondary window hides, and applies the same
       fullscreen marking so focusing it does not raise the taskbar either */
    public void LinkChildOverlayWindow(nint childWindowHandle, nint ownerWindowHandle, bool alwaysOnTop)
    {
        if (childWindowHandle == 0) return;
        _linkedChildWindow = childWindowHandle;

        if (ownerWindowHandle != 0)
            LibWin32.SetWindowLongPtrW(childWindowHandle, LibWin32.GWLP_HWNDPARENT, ownerWindowHandle);

        MarkFullscreen(childWindowHandle, _overlayingFullscreen);
    }

    /* Brings a visible overlay window to the foreground so its text input receives keyboard focus */
    public void ActivateOverlayWindow(nint windowHandle)
    {
        if (windowHandle == 0) return;
        LibWin32.SetForegroundWindow(windowHandle);
    }

    /* Updates the tracked external foreground window when the HUD loses focus while remaining visible,
       and clears fullscreen marking if the window is not configured to stay on top */
    public void OnMainWindowDeactivated(nint mainWindowHandle, bool alwaysOnTop)
    {
        if (mainWindowHandle == 0) return;

        if (!alwaysOnTop)
            MarkFullscreen(mainWindowHandle, false);

        nint foreground = LibWin32.GetForegroundWindow();
        if (foreground != 0 && foreground != mainWindowHandle && foreground != _linkedChildWindow)
        {
            _previousForegroundWindow = foreground;
            _overlayingFullscreen = IsFullscreenApplication(foreground);
        }
    }

    /* Clears the fullscreen marking and hands the foreground back to the application that was active before */
    public void OnAfterHide(nint mainWindowHandle)
    {
        if (mainWindowHandle == 0) return;

        MarkFullscreen(mainWindowHandle, false);

        nint previous = _previousForegroundWindow;
        _previousForegroundWindow = 0;
        _overlayingFullscreen = false;

        if (previous != 0 && LibWin32.IsWindow(previous) && LibWin32.IsWindowVisible(previous))
            LibWin32.SetForegroundWindow(previous);
    }

    /* A window is a fullscreen application when it covers its whole monitor and is not the desktop itself */
    private static bool IsFullscreenApplication(nint window)
    {
        if (window == LibWin32.GetShellWindow() || window == LibWin32.GetDesktopWindow())
            return false;

        var className = new char[64];
        int length = LibWin32.GetClassNameW(window, className, className.Length);
        string name = new(className, 0, length);
        if (name is "Progman" or "WorkerW")
            return false;

        if (!LibWin32.GetWindowRect(window, out LibWin32.RECT windowRect))
            return false;

        nint monitor = LibWin32.MonitorFromWindow(window, LibWin32.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new LibWin32.MONITORINFO { cbSize = Marshal.SizeOf<LibWin32.MONITORINFO>() };
        if (monitor == 0 || !LibWin32.GetMonitorInfoW(monitor, ref monitorInfo))
            return false;

        LibWin32.RECT area = monitorInfo.rcMonitor;
        return windowRect.left <= area.left
               && windowRect.top <= area.top
               && windowRect.right >= area.right
               && windowRect.bottom >= area.bottom;
    }

    /* Tells the shell whether a window should be treated as fullscreen (taskbar stays behind it) */
    private unsafe void MarkFullscreen(nint window, bool fullscreen)
    {
        nint taskbarList = GetTaskbarList();
        if (taskbarList == 0) return;

        try
        {
            nint* vtable = *(nint**)taskbarList;
            var markFullscreenWindow = (delegate* unmanaged[Stdcall]<nint, nint, int, int>)vtable[LibWin32.VtblMarkFullscreenWindow];
            int hr = markFullscreenWindow(taskbarList, window, fullscreen ? 1 : 0);
            if (hr < 0)
                Console.Error.WriteLine($"[WindowsWindowOverlayHelper] MarkFullscreenWindow failed with HRESULT 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowsWindowOverlayHelper] MarkFullscreenWindow failed: {ex.Message}");
        }
    }

    /* Lazily creates the shell's ITaskbarList2 COM object and returns its raw interface pointer, giving up for good
       (returning 0) if the shell refuses. The pointer is invoked through its vtable, which needs no COM wrapper
       generation and therefore works under Native AOT */
    private unsafe nint GetTaskbarList()
    {
        if (_taskbarList != 0 || _taskbarListFailed)
            return _taskbarList;

        try
        {
            Guid clsid = LibWin32.CLSID_TaskbarList;
            Guid iid = LibWin32.IID_ITaskbarList2;
            int hr = LibWin32.CoCreateInstance(ref clsid, 0, LibWin32.CLSCTX_INPROC_SERVER, ref iid, out nint instance);
            if (hr < 0 || instance == 0)
            {
                Console.Error.WriteLine($"[WindowsWindowOverlayHelper] CoCreateInstance(TaskbarList) failed with HRESULT 0x{hr:X8}");
                _taskbarListFailed = true;
                return 0;
            }

            nint* vtable = *(nint**)instance;
            var hrInit = (delegate* unmanaged[Stdcall]<nint, int>)vtable[LibWin32.VtblHrInit];
            hr = hrInit(instance);
            if (hr < 0)
            {
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[LibWin32.VtblRelease];
                release(instance);
                Console.Error.WriteLine($"[WindowsWindowOverlayHelper] ITaskbarList.HrInit failed with HRESULT 0x{hr:X8}");
                _taskbarListFailed = true;
                return 0;
            }

            /* The helper lives as long as the process, so the reference created by CoCreateInstance is kept on purpose */
            _taskbarList = instance;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowsWindowOverlayHelper] Taskbar COM object unavailable: {ex.Message}");
            _taskbarListFailed = true;
        }

        return _taskbarList;
    }

    /* Native C code that Windows expects to talk to */
    /* https://learn.microsoft.com/en-us/windows/win32/api/winuser/ */

    private static class LibWin32
    {
        public const int GWL_EXSTYLE = -20;
        public const int GWLP_HWNDPARENT = -8; /* Sets the owner (not the parent) of a top-level window */
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        /* Slots of the ITaskbarList2 vtable: IUnknown (0-2), ITaskbarList (3-7), ITaskbarList2 (8) */
        public const int VtblRelease = 2;
        public const int VtblHrInit = 3;
        public const int VtblMarkFullscreenWindow = 8;

        public static readonly nint HWND_TOPMOST = -1;
        public static readonly nint HWND_NOTOPMOST = -2;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint CLSCTX_INPROC_SERVER = 1;

        public static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
        public static readonly Guid IID_ITaskbarList2 = new("602D4995-B13A-429b-A66E-1935E44F4317");

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll")]
        public static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        public static extern nint GetDesktopWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(nint hWnd, [Out] char[] className, int maxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(nint hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        public static extern nint GetWindowLongPtrW(nint hWnd, int index);

        [DllImport("user32.dll")]
        public static extern nint SetWindowLongPtrW(nint hWnd, int index, nint newLong);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }
}
