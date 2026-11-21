using System.Runtime.InteropServices;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Infrastructure.OS.Linux.X11;

/* File Structure:
        - InitializeAsync() : Task
        - RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string, ShortcutCombo> phraseShortcuts) : Task
        - RegisterSingleShortcut(string id, ShortcutCombo combo) : void
        - FormatForX11Keysym(string mainKey) : string
        - EventLoop() : void
        - HandleX11Error(nint display, ref LibX11.XErrorEvent errorEvent) : int
        - Dispose() : void
        - LibX11
            - XOpenDisplay(string? display_name) : nint
            - XDefaultRootWindow(nint display) : ulong
            - XStringToKeysym(string string_name) : nint
            - XKeysymToKeycode(nint display, nint keysym) : uint
            - XGrabKey(
                nint display, 
                int keycode, 
                uint modifiers, 
                ulong grab_window, 
                bool owner_events, 
                int pointer_mode, 
                int keyboard_mode) : int
            - XUngrabKey(nint display, int keycode, uint modifiers, ulong grab_window) : int
            - XPending(nint display) : int
            - XNextEvent(nint display, ref XEvent event_return) : int
            - XFlush(nint display) : int
            - XCloseDisplay(nint display) : int
            - XSetErrorHandler(XErrorHandlerDelegate handler) : nint
            - XErrorHandlerDelegate
            - XErrorEvent
            - XKeyEvent
            - XEvent
*/

/* Shortcut hook for Linux X11 platform */

public class X11ShortcutHook : IGlobalShortcutHook
{
    /* Mask of modifier bits relevant to shortcut matching (ignores CapsLock, NumLock, mouse buttons, and XKB layout groups) */
    private const uint SignificantModifiersMask =
        LibX11.ShiftMask | LibX11.ControlMask | LibX11.Mod1Mask | LibX11.Mod4Mask;

    /* Static reference keeps the error callback alive across the process lifetime for Native AOT */
    private static readonly LibX11.XErrorHandlerDelegate ErrorHandler = HandleX11Error;

    private readonly Lock _displayLock = new();
    private IntPtr _display;
    private ulong _rootWindow;
    private int _x11Fd = -1;
    private int _wakeReadFd = -1;
    private int _wakeWriteFd = -1;
    private Thread? _eventThread;
    private volatile bool _isRunning;
    private readonly Dictionary<string, long> _lastTriggerTicks = new();

    /* Maps the exact (ModifierState, KeyCode) -> Shortcut ID */
    private readonly Dictionary<(uint, uint), string> _registeredCombos = new();

    public event Action<string>? OnShortcutTriggered;

    /* Called once when the app starts */
    public unsafe Task InitializeAsync()
    {
        lock (_displayLock)
        {
            LibX11.XSetErrorHandler(ErrorHandler);

            /* Establishes connection with the X11 server */
            _display = LibX11.XOpenDisplay(null);
            if (_display == IntPtr.Zero)
                throw new InvalidOperationException("Failed to open x11 Display");

            /* Get the desktop root background window to listen globally */
            _rootWindow = LibX11.XDefaultRootWindow(_display);
            _x11Fd = LibX11.XConnectionNumber(_display);

            int* fds = stackalloc int[2];
            if (LibX11.pipe(fds) == 0)
            {
                _wakeReadFd = fds[0];
                _wakeWriteFd = fds[1];
            }
        }

        /* Starts the background thread to listen for X11 events via blocking poll() */
        _isRunning = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "X11ShortcutLoop" };
        _eventThread.Start();

        return Task.CompletedTask;
    }

    /* Called every time the user saves their settings. Cleans the registered keybinds and rebinds them one by one */
    public Task RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string, ShortcutCombo> phraseShortcuts)
    {
        lock (_displayLock)
        {
            if (_display == IntPtr.Zero) return Task.CompletedTask;
            
            /* Ungrabs all previous keys in case the user changed them in settings */
            LibX11.XUngrabKey(_display, LibX11.AnyKey, LibX11.AnyModifier, _rootWindow);
            _registeredCombos.Clear();

            if (toggleShortcut != null) RegisterSingleShortcut("toggle_app", toggleShortcut);

            foreach (var kvp in phraseShortcuts)
            {
                RegisterSingleShortcut($"phrase_{kvp.Key}", kvp.Value);
            }

            LibX11.XFlush(_display);
        }
        
        return Task.CompletedTask;
    }

    /* Breaks apart the shortcut command and registers it with the X11 server */
    private void RegisterSingleShortcut(string id, ShortcutCombo combo)
    {
        uint modifiers = 0;
        
        /* Parses the modifiers in 4 bit binary flags */
        if (combo.HasCtrl) modifiers |= LibX11.ControlMask;
        if (combo.HasAlt) modifiers |= LibX11.Mod1Mask;
        if (combo.HasShift) modifiers |= LibX11.ShiftMask;
        if (combo.HasWin) modifiers |= LibX11.Mod4Mask;
        
        /* Asks X11 to convert the case-sensitive keysym string into a hardware KeyCode */
        string keysymName = FormatForX11Keysym(combo.MainKey);
        IntPtr keysym = LibX11.XStringToKeysym(keysymName);
        if (keysym == IntPtr.Zero) return;
        
        uint keycode = LibX11.XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return;
        
        /* Safety net in case user has CapsLock or NumLock enabled */
        uint[] ignoredModifiers =
        {
            0,
            LibX11.LockMask,
            LibX11.Mod2Mask,
            LibX11.LockMask | LibX11.Mod2Mask,
        };

        foreach (var ignore in ignoredModifiers)
        {
            uint finalModifiers = modifiers | ignore;
            
            /* XGrabKey asks the OS to send only to it said keystroke */
            LibX11.XGrabKey(
                _display,
                (int)keycode,
                finalModifiers,
                _rootWindow,
                true,
                LibX11.GrabModeAsync,
                LibX11.GrabModeAsync);
        }

        _registeredCombos[(modifiers, keycode)] = id;
    }

    /* Converts a normalized ShortcutCombo MainKey into X11's case-sensitive keysym name */
    private static string FormatForX11Keysym(string mainKey)
    {
        string rawKey = mainKey.Trim();
        return rawKey.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => "Return",
            "ESC" or "ESCAPE" => "Escape",
            "SPACE" => "space",
            "TAB" => "Tab",
            "BACKSPACE" => "BackSpace",
            "DELETE" or "DEL" => "Delete",
            "INSERT" or "INS" => "Insert",
            "HOME" => "Home",
            "END" => "End",
            "PAGEUP" or "PAGE_UP" or "PGUP" => "Page_Up",
            "PAGEDOWN" or "PAGE_DOWN" or "PGDN" => "Page_Down",
            "UP" or "ARROWUP" => "Up",
            "DOWN" or "ARROWDOWN" => "Down",
            "LEFT" or "ARROWLEFT" => "Left",
            "RIGHT" or "ARROWRIGHT" => "Right",
            "PLUS" or "ADD" => "plus",
            "MINUS" or "SUBTRACT" => "minus",
            /* Function keys F1-F24 require uppercase 'F' in X11 */
            var k when k.Length > 1 && k[0] == 'F' && int.TryParse(k[1..], out _) => k,
            /* Single alphanumeric characters are lowercase in X11 */
            var k when k.Length == 1 => k.ToLowerInvariant(),
            _ => rawKey,
        };
    }

    /* Blocks on poll() for X11 socket readiness or shutdown wakeup pipe (0% idle CPU, <1ms latency) */
    private unsafe void EventLoop()
    {
        LibX11.XEvent xevent = new LibX11.XEvent();
        LibX11.PollFd* fds = stackalloc LibX11.PollFd[2];
        var triggeredIds = new List<string>(2);

        while (_isRunning)
        {
            triggeredIds.Clear();

            lock (_displayLock)
            {
                while (_display != IntPtr.Zero && LibX11.XPending(_display) > 0)
                {
                    LibX11.XNextEvent(_display, ref xevent);

                    if (xevent.type == LibX11.KeyPress)
                    {
                        uint keycode = xevent.xkey.keycode;
                        uint state = xevent.xkey.state & SignificantModifiersMask;

                        if (_registeredCombos.TryGetValue((state, keycode), out string? shortcutId))
                        {
                            /* Per-shortcut debounce suppresses key-repeat while still allowing distinct shortcuts back-to-back */
                            long now = Environment.TickCount64;
                            if (!_lastTriggerTicks.TryGetValue(shortcutId, out long lastTick) || (now - lastTick) > 350)
                            {
                                _lastTriggerTicks[shortcutId] = now;
                                triggeredIds.Add(shortcutId);
                            }
                        }
                    }
                }
            }

            foreach (string id in triggeredIds)
            {
                OnShortcutTriggered?.Invoke(id);
            }

            if (!_isRunning)
                break;

            if (_x11Fd >= 0 && _wakeReadFd >= 0)
            {
                fds[0] = new LibX11.PollFd { fd = _x11Fd, events = LibX11.POLLIN };
                fds[1] = new LibX11.PollFd { fd = _wakeReadFd, events = LibX11.POLLIN };

                int ready = LibX11.poll(fds, 2, -1);
                if (ready > 0 && (fds[1].revents & LibX11.POLLIN) != 0)
                {
                    byte dummy = 0;
                    _ = LibX11.read(_wakeReadFd, &dummy, 1);
                }
            }
            else
            {
                Thread.Sleep(50);
            }
        }
    }

    /* Logs non-fatal X11 errors (such as BadAccess when another client already grabbed a key) instead of aborting */
    private static int HandleX11Error(IntPtr display, ref LibX11.XErrorEvent errorEvent)
    {
        Console.Error.WriteLine($"[X11ShortcutHook] X11 error code={errorEvent.error_code}, request={errorEvent.request_code}");
        return 0;
    }

    /* Disposes the process and wakes the blocked poll() loop */
    public unsafe void Dispose()
    {
        _isRunning = false;

        if (_wakeWriteFd >= 0)
        {
            byte signal = 1;
            _ = LibX11.write(_wakeWriteFd, &signal, 1);
        }

        _eventThread?.Join(500);

        lock (_displayLock)
        {
            if (_display != IntPtr.Zero)
            {
                LibX11.XUngrabKey(_display, LibX11.AnyKey, LibX11.AnyModifier, _rootWindow);
                LibX11.XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
            _x11Fd = -1;

            if (_wakeReadFd >= 0) { LibX11.close(_wakeReadFd); _wakeReadFd = -1; }
            if (_wakeWriteFd >= 0) { LibX11.close(_wakeWriteFd); _wakeWriteFd = -1; }
        }
    }

    /* Native C code that x11 expects to talk to */
    /* Here, https://xorg.freedesktop.org/releases/current/doc/libX11/libX11/libX11.html , Good luck :) */
    
    private static class LibX11
    {
        private const string LibName = "libX11.so.6";

        public const int KeyPress = 2;
        public const int GrabModeAsync = 1;
        public const int AnyKey = 0;
        public const uint AnyModifier = 0x8000;
        public const uint ShiftMask = 1 << 0;
        public const uint LockMask = 1 << 1;
        public const uint ControlMask = 1 << 2;
        public const uint Mod1Mask = 1 << 3; /* Alt */
        public const uint Mod2Mask = 1 << 4; /* NumLock */
        public const uint Mod4Mask = 1 << 6; /* Super/Windows/Command */

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int XErrorHandlerDelegate(IntPtr display, ref XErrorEvent error_event);

        public const short POLLIN = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport(LibName)]
        public static extern int XConnectionNumber(IntPtr display);

        [DllImport("libc")]
        public static extern unsafe int poll(PollFd* fds, nuint nfds, int timeout);

        [DllImport("libc")]
        public static extern unsafe int pipe(int* fds);

        [DllImport("libc")]
        public static extern unsafe nint read(int fd, byte* buf, nuint count);

        [DllImport("libc")]
        public static extern unsafe nint write(int fd, byte* buf, nuint count);

        [DllImport("libc")]
        public static extern int close(int fd);

        [DllImport(LibName)]
        public static extern IntPtr XOpenDisplay(string? display_name);

        [DllImport(LibName)]
        public static extern ulong XDefaultRootWindow(IntPtr display);

        [DllImport(LibName)]
        public static extern IntPtr XStringToKeysym(string string_name);
        
        [DllImport(LibName)]
        public static extern uint XKeysymToKeycode(IntPtr display, IntPtr keysym);

        [DllImport(LibName)]
        public static extern int XGrabKey(
            IntPtr display,
            int keycode,
            uint modifiers,
            ulong grab_window,
            bool owner_events,
            int pointer_mode,
            int keyboard_mode);
        
        [DllImport(LibName)]
        public static extern int XUngrabKey(
            IntPtr display, 
            int keycode, 
            uint modifiers, 
            ulong grab_window);

        [DllImport(LibName)]
        public static extern int XPending(IntPtr display);
        
        [DllImport(LibName)]
        public static extern int XNextEvent(IntPtr display, ref XEvent event_return);

        [DllImport(LibName)]
        public static extern int XFlush(IntPtr display);
        
        [DllImport(LibName)]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport(LibName)]
        public static extern IntPtr XSetErrorHandler(XErrorHandlerDelegate handler);

        [StructLayout(LayoutKind.Sequential)]
        public struct XErrorEvent
        {
            public int type;
            public IntPtr display;
            public ulong resourceid;
            public ulong serial;
            public byte error_code;
            public byte request_code;
            public byte minor_code;
        }
        
        /* Memory mapping of the C struct "XKeyEvent" from "/usr/include/X11/Xlib.h" */

        [StructLayout(LayoutKind.Sequential)]
        public struct XKeyEvent
        {
            public int type;
            public ulong serial;
            public int send_event; /* Boolean in C is 4 bytes */
            public IntPtr display; /* Pointers in C are 8 bytes */
            public ulong window;   /* "Window" (XID) is 8 bytes */
            public ulong root;
            public ulong subwindow;
            public ulong time;     /* "Time" is 8 bytes */
            public int x, y;
            public int x_root, y_root;
            public uint state;     /* Modifiers Mask */
            public uint keycode;   /* HArdware keycode */
            public int same_screen;
        }

        [StructLayout(LayoutKind.Explicit, Size = 192)]
        public struct XEvent
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(0)] public XKeyEvent xkey;
        }
    }
}
