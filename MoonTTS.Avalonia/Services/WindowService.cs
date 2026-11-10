using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MoonTTS.Avalonia.ViewModels;
using MoonTTS.Avalonia.Views;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Avalonia.Services;

/* File Structure:
        - WindowService(MainWindow, IClassicDesktopStyleApplicationLifetime, SettingsManager, IAudioPlayer, DownloadManager, DictionaryManager, HotkeyManager, HistoryManager, ITtsEngineProvider, IWindowOverlayHelper)
        - OnMainWindowOpened(object?, EventArgs) : void
        - OnMainWindowActivated(object?, EventArgs) : void
        - OnMainWindowDeactivated(object?, EventArgs) : void
        - ScheduleDeactivationCheck() : void
        - OnMainWindowPositionChanged(object?, PixelPointEventArgs) : void
        - ShowMainWindow() : void
        - RevealMainWindow() : void
        - FocusMainWindowInput() : void
        - ShowAndPositionMainWindow() : void
        - HideMainWindow() : void
        - HideOnSynth() : void
        - ExitApplication() : void
        - OnSettingsButtonPointerPressed() : void
        - ToggleSettingsWindow() : void
        - HideSettingsWindow() : void
        - ApplyAlwaysOnTop(bool) : void
        - GetHandle(Window) : nint
        - PositionSettingsWindow() : void
*/

public class WindowService : IWindowService
{
    private readonly MainWindow _mainWindow;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SettingsManager _settingsManager;
    private PixelPoint _lastPos;
    private SettingsWindow? _settingsWindow;
    private bool _suppressNextSettingsToggle;
    private readonly IAudioPlayer _audioPlayer;
    private readonly DownloadManager _downloadManager;
    private readonly DictionaryManager _dictionaryManager;
    private readonly HotkeyManager _hotkeyManager;
    private readonly HistoryManager _historyManager;
    private readonly ITtsEngineProvider _engineProvider;
    private readonly IWindowOverlayHelper _overlayHelper;

    /* Initializes window service with main window, desktop lifetime, and settings */
    public WindowService(
        MainWindow mainWindow,
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsManager settingsManager,
        IAudioPlayer audioPlayer,
        DownloadManager downloadManager,
        DictionaryManager dictionaryManager,
        HotkeyManager hotkeyManager,
        HistoryManager historyManager,
        ITtsEngineProvider engineProvider,
        IWindowOverlayHelper overlayHelper)
    {
        _mainWindow = mainWindow;
        _desktop = desktop;
        _settingsManager = settingsManager;
        _audioPlayer = audioPlayer;
        _downloadManager = downloadManager;
        _dictionaryManager = dictionaryManager;
        _hotkeyManager = hotkeyManager;
        _historyManager = historyManager;
        _engineProvider = engineProvider;
        _overlayHelper = overlayHelper;

        _mainWindow.Opened += OnMainWindowOpened;
        _mainWindow.Activated += OnMainWindowActivated;
        _mainWindow.Deactivated += OnMainWindowDeactivated;
        _mainWindow.PositionChanged += OnMainWindowPositionChanged;
        _mainWindow.SettingsButtonPointerPressed = OnSettingsButtonPointerPressed;

        /* When the HUD opens immediately on launch, capture the active external window before it maps */
        if (!_settingsManager.CurrentAppConfig.OpenOnStartup)
        {
            _overlayHelper.PrepareBeforeShow(GetHandle(_mainWindow), _settingsManager.CurrentAppConfig.AlwaysOnTop);
        }
    }

    /* Handles initial window positioning from remembered coordinates or bottom-center default */
    private void OnMainWindowOpened(object? sender, System.EventArgs e)
    {
        var config = _settingsManager.CurrentAppConfig;

        /* The window is mapped by now, so the overlay hints can be applied to it */
        _mainWindow.Topmost = config.AlwaysOnTop;
        _overlayHelper.ConfigureOverlayWindow(GetHandle(_mainWindow), config.AlwaysOnTop);

        if (config.RememberPosition && (config.PosX != 0 || config.PosY != 0))
        {
            var savedPos = new PixelPoint(config.PosX, config.PosY);

            /* Ensures the saved coords are available on an active display */
            var screen = _mainWindow.Screens.ScreenFromPoint(savedPos);
            if (screen != null)
            {
                _mainWindow.Position = savedPos;
                _lastPos = savedPos;
                return;
            }
        }
        /* Fallback in case it's disconnected or off */
        _mainWindow.PositionAtBottomCenter(monitorIndex: config.DefaultMonitor);
        _lastPos = _mainWindow.Position;
    }

    /* Re-applies overlay hints when the main window gains focus (e.g. when clicked directly with the mouse) */
    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (!_mainWindow.IsVisible) return;
        _overlayHelper.ConfigureOverlayWindow(GetHandle(_mainWindow), _settingsManager.CurrentAppConfig.AlwaysOnTop);
    }

    /* Schedules a focus check when the main window loses focus while still visible */
    private void OnMainWindowDeactivated(object? sender, EventArgs e)
    {
        ScheduleDeactivationCheck();
    }

    /* Checks after focus settles whether focus moved to an external app while the HUD remains visible */
    private void ScheduleDeactivationCheck()
    {
        if (!_mainWindow.IsVisible) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible || _mainWindow.IsActive) return;
            if (_settingsWindow != null && _settingsWindow.IsActive) return;

            _overlayHelper.OnMainWindowDeactivated(
                GetHandle(_mainWindow),
                _settingsManager.CurrentAppConfig.AlwaysOnTop);
        }, DispatcherPriority.Background);
    }
    
    /* Updates cached coordinate state and auto-persists when RememberPosition is enabled */ 
    private void OnMainWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_mainWindow.IsVisible)
        {
            _lastPos = e.Point;
            var config = _settingsManager.CurrentAppConfig;
            if (!config.RememberPosition) return;
            config.PosX = e.Point.X;
            config.PosY = e.Point.Y;
            _settingsManager.SaveAppConfig(config);
        }
    }

    /* Shows and activates the main window, focuses the input if visible but unfocused, or hides it if already focused */
    public void ShowMainWindow()
    {
        if (!_mainWindow.IsVisible)
        {
            ShowAndPositionMainWindow();
        }
        else if (!_mainWindow.IsActive)
        {
            FocusMainWindowInput();
        }
        else
        {
            /* Block so clicking on the tray icon or pressing the toggle shortcut hides the window if visible */
            HideMainWindow();
        }
    }

    /* Brings the main window back if it is hidden, without toggling it away when already visible */
    public void RevealMainWindow()
    {
        if (!_mainWindow.IsVisible)
            ShowAndPositionMainWindow();
        else if (!_mainWindow.IsActive)
            FocusMainWindowInput();
    }

    /* Brings an already-visible main window to the front and focuses its text input */
    private void FocusMainWindowInput()
    {
        HideSettingsWindow();

        var config = _settingsManager.CurrentAppConfig;
        nint handle = GetHandle(_mainWindow);

        _overlayHelper.PrepareBeforeShow(handle, config.AlwaysOnTop);
        _mainWindow.Topmost = config.AlwaysOnTop;
        _overlayHelper.ConfigureOverlayWindow(handle, config.AlwaysOnTop);
        _overlayHelper.ActivateOverlayWindow(handle);
        _mainWindow.Activate();
        _mainWindow.FocusInputBox();
        Dispatcher.UIThread.Post(_mainWindow.FocusInputBox, DispatcherPriority.Input);
    }

    /* Shows, positions and activates the main window according to the position settings */
    private void ShowAndPositionMainWindow()
    {
        _desktop.MainWindow ??= _mainWindow;

        var config = _settingsManager.CurrentAppConfig;
        nint handle = GetHandle(_mainWindow);

        /* Captures the active app and links the HUD to it before showing, so the OS keeps a fullscreen / borderless app
           (and its hidden taskbar) in front once the HUD takes focus */
        _overlayHelper.PrepareBeforeShow(handle, config.AlwaysOnTop);
        _mainWindow.Topmost = config.AlwaysOnTop;

        _mainWindow.Show();
        if (config.RememberPosition && _lastPos != default)
        {
            _mainWindow.Position = _lastPos;
        } 
        else if (!config.RememberPosition)
        {
            _mainWindow.PositionAtBottomCenter(monitorIndex: config.DefaultMonitor);
        }
        _overlayHelper.ConfigureOverlayWindow(handle, config.AlwaysOnTop);
        _overlayHelper.ActivateOverlayWindow(handle);
        _mainWindow.Activate();
        _mainWindow.FocusInputBox();
        Dispatcher.UIThread.Post(_mainWindow.FocusInputBox, DispatcherPriority.Input);
    }

    /* Caches the current screen position and hides the main window */
    public void HideMainWindow()
    {
        /* Hides settings window if open when main window hides */
        HideSettingsWindow();

        /* Saves pos before hiding */
        _lastPos = _mainWindow.Position;
        nint handle = GetHandle(_mainWindow);
        _mainWindow.Hide();

        /* Hands focus back to the app that was active before the HUD appeared after the hide request flushes */
        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
                _overlayHelper.OnAfterHide(handle);
        }, DispatcherPriority.Background);
    }

    /* Hides the main window when synthesis starts if HideOnSynth is configured */
    public void HideOnSynth()
    {
        var config = _settingsManager.CurrentAppConfig;
        if (config.HideOnSynth)
        {
            HideMainWindow();
        }
    }

    /* Saves window coordinates and shuts down the desktop application */
    public void ExitApplication()
    {
        var config = _settingsManager.CurrentAppConfig;
        if (config.RememberPosition)
        {
            /* Uses cached pos if window is hidden */
            var finalPos = _mainWindow.IsVisible ? _mainWindow.Position : _lastPos;
            if (finalPos != default)
            {
                config.PosX = finalPos.X;
                config.PosY = finalPos.Y;
                _settingsManager.SaveAppConfig(config);
            }
        }

        if (_settingsWindow != null)
        {
            _settingsWindow.IsExiting = true;
            _settingsWindow.Close();
        }

        _mainWindow.IsExiting = true;
        _mainWindow.Close();

        _desktop.Shutdown();
    }

    /* Records when a mouse press on the HUD settings button starts while the settings popup is open (or just
       deactivated by that same press) so the subsequent button click release does not reopen the popup */
    private void OnSettingsButtonPointerPressed()
    {
        if (_settingsWindow == null) return;

        bool wasOpenOnPress = _settingsWindow.IsVisible
            || (Environment.TickCount64 - _settingsWindow.LastDeactivatedTick) < 250;

        if (wasOpenOnPress)
        {
            _suppressNextSettingsToggle = true;
            _settingsWindow.LastDeactivatedTick = 0;
            HideSettingsWindow();
        }
        else
        {
            _suppressNextSettingsToggle = false;
        }
    }

    /* Toggles the settings window visibility anchored above the HUD settings button */
    public void ToggleSettingsWindow()
    {
        if (_suppressNextSettingsToggle)
        {
            _suppressNextSettingsToggle = false;
            HideSettingsWindow();
            return;
        }

        if (_settingsWindow != null
            && (_settingsWindow.IsVisible || (Environment.TickCount64 - _settingsWindow.LastDeactivatedTick) < 250))
        {
            _settingsWindow.LastDeactivatedTick = 0;
            HideSettingsWindow();
            return;
        }

        if (_settingsWindow == null)
        {
            var viewModel = new SettingsViewModel(
                _settingsManager,
                _audioPlayer,
                this,
                _downloadManager,
                _dictionaryManager,
                _hotkeyManager,
                _historyManager,
                _engineProvider);
            _settingsWindow = new SettingsWindow
            {
                DataContext = viewModel
            };
            _settingsWindow.SetOwnerWindow(_mainWindow);
            _settingsWindow.Deactivated += (_, _) => ScheduleDeactivationCheck();
        }

        bool alwaysOnTop = _settingsManager.CurrentAppConfig.AlwaysOnTop;
        _settingsWindow.Topmost = alwaysOnTop;

        /* Always reset to Quick Settings on open and calculate absolute position */
        _settingsWindow.ResetToQuickView();

        /* Links settings to the HUD before showing so both stay in the same overlay layer */
        _overlayHelper.LinkChildOverlayWindow(GetHandle(_settingsWindow), GetHandle(_mainWindow), alwaysOnTop);
        _settingsWindow.Show();
        _overlayHelper.LinkChildOverlayWindow(GetHandle(_settingsWindow), GetHandle(_mainWindow), alwaysOnTop);
        _overlayHelper.ConfigureOverlayWindow(GetHandle(_settingsWindow), alwaysOnTop);
        _settingsWindow.Activate();
    }
    
    /* Hides the settings window if currently visible */
    public void HideSettingsWindow()
    {
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Hide();
        }
    }
    
    /* Applies the always-on-top state to the visible windows immediately; hidden ones pick it up when shown */
    public void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        _mainWindow.Topmost = alwaysOnTop;
        if (_mainWindow.IsVisible)
            _overlayHelper.ConfigureOverlayWindow(GetHandle(_mainWindow), alwaysOnTop);

        if (_settingsWindow != null)
        {
            _settingsWindow.Topmost = alwaysOnTop;
            if (_settingsWindow.IsVisible)
                _overlayHelper.ConfigureOverlayWindow(GetHandle(_settingsWindow), alwaysOnTop);
        }
    }

    /* Returns the native handle of a window (XID, HWND or NSWindow), or 0 when it has none yet */
    private static nint GetHandle(Window window) => window.TryGetPlatformHandle()?.Handle ?? 0;

    /* Positions the settings window directly above the HUD Settings button */
    private void PositionSettingsWindow()
    {
        if (_settingsWindow == null) return;

        double scaling = _mainWindow.RenderScaling > 0 ? _mainWindow.RenderScaling : 1.0;

        /* Settings button horizontal offset in MainWindow: Hyperlink(48) + Spacing(12) + Drag(48) + Spacing(12) = 120px */
        int btnOffsetX = (int)Math.Round(120 * scaling);
        int targetX = _mainWindow.Position.X + btnOffsetX;

        /* Position directly above the HUD with 8px margin */
        int targetY = _mainWindow.Position.Y - (int)Math.Round((_settingsWindow.Height + 8) * scaling);

        var screen = _mainWindow.Screens.ScreenFromWindow(_mainWindow) ?? _mainWindow.Screens.Primary;
        if (screen != null)
        {
            var workingArea = screen.WorkingArea;
            int winWidthPx = (int)Math.Round(_settingsWindow.Width * scaling);

            /* Clamps X within screen working area boundaries */
            if (targetX + winWidthPx > workingArea.Right)
            {
                targetX = workingArea.Right - winWidthPx - (int)Math.Round(12 * scaling);
            }
            if (targetX < workingArea.BottomLeft.X)
            {
                targetX = workingArea.BottomLeft.X + (int)Math.Round(12 * scaling);
            }

            /* If not enough room above HUD, flips to below the HUD */
            if (targetY < workingArea.TopLeft.Y)
            {
                targetY = _mainWindow.Position.Y + (int)Math.Round((_mainWindow.Height + 8) * scaling);
            }
        }

        _settingsWindow.Position = new PixelPoint(targetX, targetY);
    }
}