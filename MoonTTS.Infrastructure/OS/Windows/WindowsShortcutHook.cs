using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Infrastructure.OS.Windows;

/* File Structure:
        - InitializeAsync() : Task
        - RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string, ShortcutCombo> phraseShortcuts) : Task
        - RegisterSingleShortcut(string id, ShortcutCombo combo) : void
        - TryGetVirtualKey(string mainKey, out uint vk) : bool
        - EventLoop() : void
        - Dispose() : void
        - LibWin32
*/

public class WindowsShortcutHook : IGlobalShortcutHook
{
    private Thread? _eventThread;
    private int _threadId;
    private readonly ManualResetEvent _readyEvent = new(false);
    
    /* Queue so the UI thread can send registration commands to the background thread */
    private readonly ConcurrentQueue<Action> _dispatchQueue = new();

    /* Maps the Win32 hotkey ID to a shortcut string */
    private readonly Dictionary<int, string> _registeredCombos = new();
    private int _nextHotkeyId = 1;

    public event Action<string>? OnShortcutTriggered;

    /* Starts the dedicated Win32 message-loop thread for global hotkeys */
    public Task InitializeAsync()
    {
        _eventThread = new Thread(EventLoop) {IsBackground = true};
        _eventThread.Start();
        
        /* Pauses until the background thred finished creating its OS message queue */
        _readyEvent.WaitOne();
        
        return Task.CompletedTask;
    }

    /* Enqueues hotkey registration updates onto the Win32 message-loop thread */
    public Task RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string, ShortcutCombo> phraseShortcuts)
    {
        /* Queues the task for the background thread */
        _dispatchQueue.Enqueue(() =>
        {
            /* Unregisters old keys */
            foreach (var id in _registeredCombos.Keys)
            {
                LibWin32.UnregisterHotKey(IntPtr.Zero, id);
            }
            _registeredCombos.Clear();
            _nextHotkeyId = 1;
            
            /* Registers the main toggle shortcut */
            if (toggleShortcut != null) RegisterSingleShortcut("toggle_app", toggleShortcut);

            /* Registers the rest of the custom shortcuts */
            foreach (var kvp in phraseShortcuts)
            {
                RegisterSingleShortcut($"phrase_{kvp.Key}", kvp.Value);
            }
        });
        
        /* Wakes up the bg thread to process our queue */
        LibWin32.PostThreadMessage(_threadId, LibWin32.WM_WAKEUP, IntPtr.Zero, IntPtr.Zero);
        
        return Task.CompletedTask;
    }

    /* Translates a ShortcutCombo into Win32 modifier flags and virtual-key code and registers it */
    private void RegisterSingleShortcut(string id, ShortcutCombo combo)
    {
        /* Prevents hotkey spamming */
        uint modifiers = LibWin32.MOD_NOREPEAT;

        if (combo.HasCtrl) modifiers |= LibWin32.MOD_CONTROL;
        if (combo.HasAlt) modifiers |= LibWin32.MOD_ALT;
        if (combo.HasShift) modifiers |= LibWin32.MOD_SHIFT;
        if (combo.HasWin) modifiers |= LibWin32.MOD_WIN;

        if (!TryGetVirtualKey(combo.MainKey, out uint vk))
            return;
        
        int hotkeyId = _nextHotkeyId++;

        if (LibWin32.RegisterHotKey(IntPtr.Zero, hotkeyId, modifiers, vk))
        {
            _registeredCombos[hotkeyId] = id;
        }
    }

    /* Maps normalized ShortcutCombo key names to Win32 virtual-key codes */
    private static bool TryGetVirtualKey(string mainKey, out uint vk)
    {
        vk = 0;
        string key = mainKey.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key))
            return false;

        /* Single ASCII letter (A-Z) or digit (0-9) maps directly to its ASCII code */
        if (key.Length == 1)
        {
            char c = key[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }
        }

        /* Function keys F1-F24 (VK_F1 = 0x70 .. VK_F24 = 0x87) */
        if (key.Length > 1 && key[0] == 'F' && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fn - 1));
            return true;
        }

        vk = key switch
        {
            "BACKSPACE" => 0x08,
            "TAB" => 0x09,
            "RETURN" or "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "PAGEUP" or "PAGE_UP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PAGE_DOWN" or "PGDN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" or "ARROWLEFT" => 0x25,
            "UP" or "ARROWUP" => 0x26,
            "RIGHT" or "ARROWRIGHT" => 0x27,
            "DOWN" or "ARROWDOWN" => 0x28,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "PLUS" or "ADD" => 0xBB,
            "MINUS" or "SUBTRACT" => 0xBD,
            _ => 0,
        };

        return vk != 0;
    }

    /* Listener waiting for activating or registering hotkeys */
    private void EventLoop()
    {
        _threadId = LibWin32.GetCurrentThreadId();
        
        /* Forces Windows to create a Message Queue for this thread */
        LibWin32.PeekMessage(out _, IntPtr.Zero, 0,0,0);
        _readyEvent.Set();

        LibWin32.MSG msg;

        /* GetMessage freezes the thread until windows sends a message */
        while (LibWin32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            /* Case if Windows notifies that a shortcut has been pressed */
            if (msg.message == LibWin32.WM_HOTKEY)
            {
                int hotkeyId = (int)msg.wParam;
                if (_registeredCombos.TryGetValue(hotkeyId, out string? shortcutId))
                {
                    OnShortcutTriggered?.Invoke(shortcutId);
                }
            } else if (msg.message == LibWin32.WM_WAKEUP)
            {
                /* Case if the UI sends something to register */
                while (_dispatchQueue.TryDequeue(out var action))
                {
                    action();
                }
            }
        }
    }

    /* Stops the Win32 message loop thread */
    public void Dispose()
    {
        LibWin32.PostThreadMessage(_threadId, LibWin32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
    
    /* Native C code that Windows expects to talk to */
    /* https://learn.microsoft.com/en-us/windows/win32/api/winuser/ , mainly RegisterHotKey */

    private static class LibWin32
    {
        public const uint WM_QUIT = 0x0012;
        public const uint WM_HOTKEY = 0x0312;
        public const uint WM_WAKEUP = 0x8001;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL= 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PeekMessage(
            out MSG lpMsg, 
            IntPtr hWnd, 
            uint wMsgFilterMin, 
            uint wMsgFilterMax, 
            uint wRemoveMsg
            );
        
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostThreadMessage(int idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        
        /* Exported by kernel32.dll, not user32.dll */
        [DllImport("kernel32.dll")]
        public static extern int GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }
    }
}
