using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using winuitest.Services;

namespace winuitest.Views;

/// <summary>A native WinUI surface kept separate from the main window's visibility.</summary>
public sealed partial class TrayMenuWindow : Window
{
    private readonly Func<TrayState> _getState;
    private readonly Func<TrayCommand, Task> _executeCommand;
    private readonly Func<Task<IReadOnlyList<TrayAudioOutput>>> _getAudioOutputs;
    private readonly Func<string, Task> _selectAudioOutput;
    private bool _destroying;
    private bool _open;
    private bool _updatingOutputs;
    private bool _selectingOutput;
    private int _outputsGeneration;
    private readonly Dictionary<Popup, UIElement> _shortcutPopups = new();
    private readonly HashSet<VirtualKey> _shortcutKeysDown = new();
    private readonly ConditionalWeakTable<KeyRoutedEventArgs, object> _handledPreviewEvents = new();
    private readonly KeyEventHandler _previewKeyDown;
    private readonly KeyEventHandler _previewKeyUp;

    internal TrayMenuWindow(string iconPath, Func<TrayState> getState,
        Func<TrayCommand, Task> executeCommand,
        Func<Task<IReadOnlyList<TrayAudioOutput>>> getAudioOutputs,
        Func<string, Task> selectAudioOutput)
    {
        _getState = getState;
        _executeCommand = executeCommand;
        _getAudioOutputs = getAudioOutputs;
        _selectAudioOutput = selectAudioOutput;
        InitializeComponent();
        _previewKeyDown = Surface_PreviewKeyDown;
        _previewKeyUp = Surface_PreviewKeyUp;
        Surface.AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyDown, true);
        Surface.AddHandler(UIElement.PreviewKeyUpEvent, _previewKeyUp, true);
        FocusManager.GettingFocus += Shortcuts_GettingFocus;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.SetIcon(iconPath);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        TrayNative.SetRoundedCorners(WinRT.Interop.WindowNative.GetWindowHandle(this));
        AppWindow.Closing += (_, args) => { if (!_destroying) { args.Cancel = true; HideMenu(); } };
        Activated += (_, args) =>
        {
            if (!_open || args.WindowActivationState != WindowActivationState.Deactivated) return;
            _shortcutKeysDown.Clear();
            // ComboBox popups may receive activation in an owned HWND. Defer
            // until that HWND is foreground before deciding to dismiss.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_open && !TrayNative.ForegroundBelongsTo(WinRT.Interop.WindowNative.GetWindowHandle(this))) HideMenu();
            });
        };
    }

    internal void ShowMenu(TrayNative.Rect anchor)
    {
        if (_destroying) return;
        RefreshState();
        TrayNative.PositionMenu(this, anchor, 320, 532);
        _open = true;
        AppWindow.Show();
        Activate();
        _ = LoadAudioOutputsAsync();
    }

    internal void HideMenu()
    {
        if (!_open) return;
        _open = false;
        _outputsGeneration++;
        AudioOutputs.IsDropDownOpen = false;
        _shortcutKeysDown.Clear();
        AppWindow.Hide();
    }

    internal void Destroy()
    {
        if (_destroying) return;
        _destroying = true;
        _open = false;
        _outputsGeneration++;
        FocusManager.GettingFocus -= Shortcuts_GettingFocus;
        foreach (var popup in _shortcutPopups.Keys.ToArray()) DetachShortcutPopup(popup);
        Surface.RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyDown);
        Surface.RemoveHandler(UIElement.PreviewKeyUpEvent, _previewKeyUp);
        _shortcutKeysDown.Clear();
        _handledPreviewEvents.Clear();
        Close();
    }

    internal void RefreshState()
    {
        if (_destroying) return;
        var state = _getState();
        Surface.RequestedTheme = state.Theme;
        TrackTitle.Text = string.IsNullOrWhiteSpace(state.TrackTitle) ? "Luma Music" : state.TrackTitle;
        TrackArtist.Text = state.Artist;
        TrackArtist.Visibility = string.IsNullOrWhiteSpace(state.Artist) ? Visibility.Collapsed : Visibility.Visible;
        PlayPauseIcon.Kind = state.IsPlaying ? "Pause" : "Play";
        PlayPauseButton.IsEnabled = state.CanControl && !state.IsLoading;
        PreviousButton.IsEnabled = NextButton.IsEnabled = LyricsButton.IsEnabled = state.CanControl;
        if (!_selectingOutput && AudioOutputs.ItemsSource is IReadOnlyList<TrayAudioOutput> outputs)
        {
            _updatingOutputs = true;
            AudioOutputs.SelectedValue = outputs.Any(output => output.Id == state.SelectedAudioOutputId) ? state.SelectedAudioOutputId : "";
            _updatingOutputs = false;
        }
    }

    private async Task LoadAudioOutputsAsync()
    {
        var generation = ++_outputsGeneration;
        _updatingOutputs = true;
        AudioOutputs.ItemsSource = new[] { new TrayAudioOutput("", "系统默认输出") };
        AudioOutputs.SelectedValue = "";
        AudioOutputs.IsEnabled = false;
        _updatingOutputs = false;
        ShowStatus("正在查找设备…");
        try
        {
            var devices = await _getAudioOutputs();
            if (!_open || _destroying || generation != _outputsGeneration) return;
            var outputs = new[] { new TrayAudioOutput("", "系统默认输出") }
                .Concat(devices.Where(device => !string.IsNullOrEmpty(device.Id)).DistinctBy(device => device.Id)).ToArray();
            _updatingOutputs = true;
            AudioOutputs.ItemsSource = outputs;
            var selected = _getState().SelectedAudioOutputId;
            AudioOutputs.SelectedValue = outputs.Any(output => output.Id == selected) ? selected : "";
            _updatingOutputs = false;
            ShowStatus(null);
        }
        catch (Exception)
        {
            if (_open && !_destroying && generation == _outputsGeneration) ShowStatus("暂时无法获取输出设备");
        }
        finally
        {
            if (_open && !_destroying && generation == _outputsGeneration) AudioOutputs.IsEnabled = true;
        }
    }

    private async void AudioOutputs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingOutputs || _destroying || !_open || AudioOutputs.SelectedItem is not TrayAudioOutput selected) return;
        _selectingOutput = true;
        AudioOutputs.IsEnabled = false;
        ShowStatus(null);
        try { await _selectAudioOutput(selected.Id); }
        catch (Exception) { if (!_destroying && _open) ShowStatus("输出设备切换失败，请重试"); }
        finally
        {
            _selectingOutput = false;
            if (!_destroying) { AudioOutputs.IsEnabled = true; RefreshState(); }
        }
    }

    private async void Command_Click(object sender, RoutedEventArgs args)
    {
        if (_destroying || sender is not Button { Tag: string name } || !Enum.TryParse<TrayCommand>(name, out var command)) return;
        if (command is not (TrayCommand.PlayPause or TrayCommand.Previous or TrayCommand.Next)) HideMenu();
        try { await _executeCommand(command); }
        catch (Exception) { if (!_destroying && _open) ShowStatus("操作未完成，请重试"); }
        if (!_destroying) RefreshState();
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Surface_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_handledPreviewEvents.TryGetValue(args, out _)) { args.Handled = true; return; }
        if (args.Key == VirtualKey.Escape) { args.Handled = true; HideMenu(); return; }
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        TrayCommand? command = null;
        if (args.Key == VirtualKey.Space && !Down(VirtualKey.Control) && !Down(VirtualKey.Menu))
            command = TrayCommand.PlayPause;
        else if (Down(VirtualKey.Control) && !Down(VirtualKey.Menu))
            command = args.Key switch { VirtualKey.Left => TrayCommand.Previous, VirtualKey.Right => TrayCommand.Next, _ => null };
        if (command is null) return;
        // Preview wins over the focused Button/ComboBox: Space always controls
        // music, including when a navigation row currently owns keyboard focus.
        _handledPreviewEvents.Add(args, new object());
        _shortcutKeysDown.Add(args.Key);
        args.Handled = true;
        if (args.KeyStatus.WasKeyDown || _destroying) return;
        var state = _getState();
        if (!state.CanControl || command == TrayCommand.PlayPause && state.IsLoading) return;
        try { await _executeCommand(command.Value); }
        catch (Exception) { if (!_destroying && _open) ShowStatus("操作未完成，请重试"); }
        if (!_destroying) RefreshState();
    }

    private void Surface_PreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_shortcutKeysDown.Remove(args.Key)) args.Handled = true;
    }

    private void Shortcuts_GettingFocus(object? sender, GettingFocusEventArgs args)
    {
        // ComboBox popup children do not tunnel through Surface. Attach before
        // their first key arrives, scoped to this window's own XamlRoot.
        var root = Surface.XamlRoot;
        if (_destroying || !_open || root is null || args.NewFocusedElement is not UIElement focused || focused.XamlRoot != root) return;
        foreach (var popup in _shortcutPopups.Keys.ToArray())
            if (!popup.IsOpen || popup.XamlRoot != root) DetachShortcutPopup(popup);
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is not UIElement child) continue;
            if (_shortcutPopups.TryGetValue(popup, out var previous))
            {
                if (ReferenceEquals(previous, child)) continue;
                DetachShortcutPopup(popup);
            }
            child.AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyDown, true);
            child.AddHandler(UIElement.PreviewKeyUpEvent, _previewKeyUp, true);
            _shortcutPopups.Add(popup, child);
            popup.Closed += ShortcutPopup_Closed;
            if (child is FrameworkElement element) element.Unloaded += ShortcutPopupChild_Unloaded;
        }
    }

    private void ShortcutPopup_Closed(object? sender, object args)
    {
        if (sender is Popup popup) DetachShortcutPopup(popup);
    }

    private void ShortcutPopupChild_Unloaded(object sender, RoutedEventArgs args)
    {
        foreach (var pair in _shortcutPopups.ToArray())
            if (ReferenceEquals(pair.Value, sender)) DetachShortcutPopup(pair.Key);
    }

    private void DetachShortcutPopup(Popup popup)
    {
        if (!_shortcutPopups.Remove(popup, out var child)) return;
        child.RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyDown);
        child.RemoveHandler(UIElement.PreviewKeyUpEvent, _previewKeyUp);
        popup.Closed -= ShortcutPopup_Closed;
        if (child is FrameworkElement element) element.Unloaded -= ShortcutPopupChild_Unloaded;
    }
}
