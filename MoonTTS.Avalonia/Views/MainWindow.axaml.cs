using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Avalonia.Views;

/* File Structure:
        - MainWindow()
        - IsExiting : bool
        - SettingsButtonPointerPressed : Action?
        - OnSettingsButtonPointerPressed(object?, PointerPressedEventArgs) : void
        - OnInputBoxKeyDown(object?, KeyEventArgs) : void
        - OnClosing(WindowClosingEventArgs) : void
        - PositionAtBottomCenter(int, int?) : void
        - DragButton_PointerPressed(object?, PointerPressedEventArgs) : void
        - DragButton_PointerReleased(object?, PointerReleasedEventArgs) : void
        - OnLoaded(RoutedEventArgs) : void
        - FocusInputBox() : void
*/

public partial class MainWindow : Window
{
    /* Initializes the main HUD window dimensions and wires pointer tunnel handlers */
    public MainWindow()
    {
        InitializeComponent();
        
        Width = 776;
        Height = 56;

        SettingsBtn.AddHandler(
            PointerPressedEvent,
            OnSettingsButtonPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        /* Tunnel so the arrows are caught before the TextBox moves its caret */
        InputBox.AddHandler(KeyDownEvent, OnInputBoxKeyDown, RoutingStrategies.Tunnel);
    }

    /* Browses the speech history with Up / Down while the input box is focused */
    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MoonTTS.Avalonia.ViewModels.MainViewModel vm || e.KeyModifiers != KeyModifiers.None)
            return;

        if (e.Key == Key.Up)
            vm.NavigateHistoryUp();
        else if (e.Key == Key.Down)
            vm.NavigateHistoryDown();
        else
            return;

        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        e.Handled = true;
    }

    /* Indicates whether the application is shutting down rather than hiding to the tray */
    public bool IsExiting { get; set; } = false;

    /* Callback invoked on mouse-down over the settings button before focus changes complete */
    public Action? SettingsButtonPointerPressed { get; set; }

    /* Notifies the window service when the settings button is pressed so an open settings popup is closed cleanly */
    private void OnSettingsButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            SettingsButtonPointerPressed?.Invoke();
    }

    /* Cancels window destruction and hides the window instead unless the application is exiting */
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /* Positions the window at the bottom center of the designated monitor */
    /* Uses working area to respect desktop docks and panels */
    public void PositionAtBottomCenter(int bottomMargin = 12, int? monitorIndex = 0)
    {
        Screen? targetScreen = null;

        if (monitorIndex.HasValue && monitorIndex.Value > 0 && monitorIndex.Value < Screens.All.Count)
        {
            targetScreen = Screens.All[monitorIndex.Value];
        }
        else
        {
            targetScreen = Screens.Primary ?? Screens.ScreenFromWindow(this) ?? Screens.All.FirstOrDefault();
        }
        
        if (targetScreen == null)
            return;

        var workingArea = targetScreen.WorkingArea;
        double scaling = RenderScaling > 0 ? RenderScaling : targetScreen.Scaling;

        int windowWidthPx = (int)Math.Round(Width * scaling);
        int windowHeightPx = (int)Math.Round(Height * scaling);
        int marginPx = (int)Math.Round(bottomMargin * scaling);

        int x = workingArea.X + (workingArea.Width - windowWidthPx) / 2;
        int y = workingArea.Y + workingArea.Height - windowHeightPx - marginPx;

        Position = new PixelPoint(x, y);
    }
    
    /* Starts window move-drag when the drag button is pressed with the left mouse button */
    public void DragButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (sender is Border b) b.Classes.Add("Pressed");
            BeginMoveDrag(e);
        }
    }
    
    /* Clears the pressed visual state when the drag button is released */
    public void DragButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border b) b.Classes.Remove("Pressed");
    }

    /* Focuses the text input box once the window visual tree is loaded */
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        InputBox.Focus();
    }

    /* Gives keyboard focus to the text input so typing can start as soon as the window is revealed */
    public void FocusInputBox()
    {
        InputBox.Focus();
    }
}
