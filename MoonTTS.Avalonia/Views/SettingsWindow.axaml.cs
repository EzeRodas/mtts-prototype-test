using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MoonTTS.Avalonia.ViewModels;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Avalonia.Views;

/* File Structure:
            - SettingsWindow
            - IsExiting : bool
            - LastDeactivatedTick : long
            - SettingsWindow()
            - SetOwnerWindow(MainWindow) : void
            - ResetToQuickView() : void
            - OnWindowDeactivated(object?, EventArgs) : void
            - OnWindowPreviewKeyDown(object?, KeyEventArgs) : void
            - IsPureModifierKey(Key) : bool
            - ConvertKeyToStandardString(Key) : string?
            - OnClosing(WindowClosingEventArgs) : void
            - OnDataContextChanged(EventArgs) : void
            - OnViewModelPropertyChanged(object?, PropertyChangedEventArgs) : void
            - RepositionWindow(bool) : void
*/

public partial class SettingsWindow : Window
{
    public const double QuickWidth = 320;
    public const double QuickHeight = 520;
    public const double AdvancedWidth = 760;
    public const double AdvancedHeight = 540;

    private MainWindow? _mainWindow;
    
    /* Indicates whether the application is shutting down rather than hiding the settings window */
    public bool IsExiting { get; set; } = false;

    /* Monotonic tick recorded when the window auto-hides on deactivation so clicking the HUD settings button does not immediately reopen it */
    public long LastDeactivatedTick { get; set; }

    /* Initializes the settings popup dimensions and event handlers */
    public SettingsWindow()
    {
        InitializeComponent();

        Width = QuickWidth;
        Height = QuickHeight;

        Deactivated += OnWindowDeactivated;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /* Links the main HUD window for absolute coordinate tracking */
    public void SetOwnerWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    /* Resets window geometry and view-state back to compact Quick Settings */
    public void ResetToQuickView()
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.IsAdvancedView = false;
        }

        Width = QuickWidth;
        Height = QuickHeight;
        RepositionWindow(isAdvanced: false);
    }

    /* Automatically hides and resets to Quick Settings when clicking outside. The hide is deferred until the pending
       activation of the clicked window has finished: hiding the active window in the middle of its deactivation makes
       Windows pick an unrelated application behind the HUD to activate, which stole focus from the HUD controls */
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CancelRecording();
        }
        LastDeactivatedTick = Environment.TickCount64;

        bool keepMainFocused = _mainWindow != null && (_mainWindow.IsActive || _mainWindow.IsPointerOver);
        Dispatcher.UIThread.Post(() =>
        {
            /* The settings window was re-activated before the deferred hide ran */
            if (IsActive)
                return;

            ResetToQuickView();
            Hide();

            if (keepMainFocused && _mainWindow is { IsVisible: true })
                _mainWindow.Activate();
        }, DispatcherPriority.Input);
    }

    /* Intercepts key presses during hotkey recording before any child controls process them */
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            DataContext is SettingsViewModel vm
            && (vm.IsRecordingToggleHotkey || vm.IsRecordingPhraseHotkey)
        )
        {
            if (e.Key == Key.Escape)
            {
                vm.CancelRecording();
                e.Handled = true;
                return;
            }

            if (IsPureModifierKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool win = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (!ctrl && !alt && !shift && !win)
            {
                vm.CancelRecording();
                e.Handled = true;
                return;
            }

            string? keyName = ConvertKeyToStandardString(e.Key);
            if (!string.IsNullOrEmpty(keyName))
            {
                try
                {
                    var combo = new ShortcutCombo(ctrl, alt, shift, win, keyName);
                    vm.ApplyRecordedHotkey(combo.ToString());
                }
                catch
                {
                    vm.CancelRecording();
                }
                e.Handled = true;
            }
        }
    }

    /* Checks if the key is purely a modifier or system/lock key that should not terminate recording */
    private static bool IsPureModifierKey(Key key) =>
        key
            is Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LeftShift
                or Key.RightShift
                or Key.LWin
                or Key.RWin
                or Key.System
                or Key.None
                or Key.CapsLock
                or Key.NumLock;

    /* Formats the pressed key to match ShortcutCombo standard names */
    private static string? ConvertKeyToStandardString(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return key.ToString();

        if (key >= Key.D0 && key <= Key.D9)
            return ((int)key - (int)Key.D0).ToString();

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return ((int)key - (int)Key.NumPad0).ToString();

        if (key >= Key.F1 && key <= Key.F24)
            return key.ToString();

        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Return",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null,
        };
    }

    /* Intercepts close to hide window and reset state instead of destroying window handle */
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!IsExiting)
        {
            e.Cancel = true;
            ResetToQuickView();
            Hide();
        }
        base.OnClosing(e);
    }

    /* Tracks ViewModel state changes */
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /* Responds to IsAdvancedView toggles */
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(SettingsViewModel.IsAdvancedView)
            && DataContext is SettingsViewModel vm
        )
        {
            RepositionWindow(vm.IsAdvancedView);
        }
    }

    /* Calculates absolute coordinates: Quick anchors above Settings button; Advanced centers horizontally */
    private void RepositionWindow(bool isAdvanced)
    {
        if (_mainWindow == null)
            return;

        double targetWidth = isAdvanced ? AdvancedWidth : QuickWidth;
        double targetHeight = isAdvanced ? AdvancedHeight : QuickHeight;
        double scaling = _mainWindow.RenderScaling > 0 ? _mainWindow.RenderScaling : 1.0;

        Width = targetWidth;
        Height = targetHeight;

        int targetX;
        if (!isAdvanced)
        {
            /* Quick Settings: Anchor directly above the Settings button (offset 120px) */
            targetX = _mainWindow.Position.X + (int)Math.Round(120 * scaling);
        }
        else
        {
            /* Advanced Settings: Horizontally centered with MainWindow (width 768px) */
            int mainCenterX =
                _mainWindow.Position.X + (int)Math.Round((_mainWindow.Width / 2.0) * scaling);
            targetX = mainCenterX - (int)Math.Round((targetWidth / 2.0) * scaling);
        }

        /* Y sits directly above the HUD */
        int targetY = _mainWindow.Position.Y - (int)Math.Round((targetHeight + 8) * scaling);

        /* Clamping within active monitor working area */
        var screen =
            _mainWindow.Screens.ScreenFromWindow(_mainWindow) ?? _mainWindow.Screens.Primary;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            int winWidthPx = (int)Math.Round(targetWidth * scaling);

            if (targetX + winWidthPx > wa.Right)
                targetX = wa.Right - winWidthPx - (int)Math.Round(12 * scaling);
            if (targetX < wa.X)
                targetX = wa.X + (int)Math.Round(12 * scaling);

            /* If not enough room above HUD, flip to below HUD */
            if (targetY < wa.Y)
                targetY =
                    _mainWindow.Position.Y + (int)Math.Round((_mainWindow.Height + 8) * scaling);
        }

        Position = new PixelPoint(targetX, targetY);
    }
}
