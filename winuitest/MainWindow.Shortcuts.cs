using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly List<(string Keys, string Description)> _shortcutHelp = new();
    private readonly Dictionary<(VirtualKey Key, VirtualKeyModifiers Modifiers), ShortcutBinding> _shortcutBindings = new();
    private readonly HashSet<VirtualKey> _shortcutKeysDown = new();
    private readonly Dictionary<Popup, UIElement> _shortcutPopups = new();
    private readonly ConditionalWeakTable<KeyRoutedEventArgs, object> _handledShortcutPreviewEvents = new();
    private KeyEventHandler? _shortcutPreviewKeyDown;
    private KeyEventHandler? _shortcutPreviewKeyUp;
    private bool _shortcutsDialogOpen;

    private sealed record ShortcutBinding(KeyboardAccelerator Accelerator, Func<Task> Action, bool TypingSensitive, bool AllowRepeat);

    private void InitializeShortcuts()
    {
        var ctrl = VirtualKeyModifiers.Control;
        void Sync(VirtualKey key, VirtualKeyModifiers modifiers, string label, string description, Action action, bool typingSensitive = false, bool allowRepeat = false) =>
            RegisterShortcut(key, modifiers, label, description, () => { action(); return Task.CompletedTask; }, typingSensitive, allowRepeat);

        Sync(VirtualKey.Space, VirtualKeyModifiers.None, "Space", "播放 / 暂停", () => PlayPause_Click(PlayPauseButton, new RoutedEventArgs()), true);
        Sync(VirtualKey.P, ctrl, "Ctrl + P", "播放 / 暂停", () => PlayPause_Click(PlayPauseButton, new RoutedEventArgs()));
        RegisterShortcut(VirtualKey.Left, ctrl, "Ctrl + ←", "上一首", () => _player.PreviousAsync());
        RegisterShortcut(VirtualKey.Right, ctrl, "Ctrl + →", "下一首", () => _player.NextAsync());
        Sync(VirtualKey.Left, VirtualKeyModifiers.None, "← / →", "后退 / 前进 5 秒", () => SeekTo(Math.Max(0, _player.Position.TotalSeconds - 5)), true, true);
        Sync(VirtualKey.Right, VirtualKeyModifiers.None, "", "", () => SeekTo(_player.Position.TotalSeconds + 5), true, true);
        Sync(VirtualKey.Up, ctrl, "Ctrl + ↑ / ↓", "增减音量", () => VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5), allowRepeat: true);
        Sync(VirtualKey.Down, ctrl, "", "", () => VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5), allowRepeat: true);
        Sync(VirtualKey.M, ctrl, "Ctrl + M", "静音 / 恢复", () => Volume_Click(VolumeSlider, new RoutedEventArgs()));
        Sync(VirtualKey.F, ctrl, "Ctrl + F", "搜索", () => { CloseNowPlaying(); SearchBox.Focus(FocusState.Keyboard); });
        Sync(VirtualKey.L, ctrl, "Ctrl + L", "打开 / 收起歌词", () => { if (_immersiveOpen) CloseNowPlaying(); else OpenNowPlaying(); });
        Sync(VirtualKey.Q, ctrl, "Ctrl + Q", "待播清单", () => { CloseNowPlaying(); SetInspector(true, true); });
        Sync(VirtualKey.T, ctrl | VirtualKeyModifiers.Shift, "Ctrl + Shift + T", "托盘播放菜单", () => _tray?.ShowMenu());
        Sync(VirtualKey.D, ctrl, "Ctrl + D", "喜欢 / 取消喜欢", () => PlayerFavorite_Click(PlayerFavorite, new RoutedEventArgs()));
        Sync(VirtualKey.S, ctrl | VirtualKeyModifiers.Shift, "Ctrl + Shift + S", "随机播放", () => Shuffle_Click(ShuffleButton, new RoutedEventArgs()));
        Sync(VirtualKey.R, ctrl, "Ctrl + R", "切换循环模式", () => Repeat_Click(RepeatButton, new RoutedEventArgs()));
        Sync((VirtualKey)188, ctrl, "Ctrl + ,", "设置", () => { CloseNowPlaying(); BeginPage("settings"); });
        Sync(VirtualKey.F5, VirtualKeyModifiers.None, "F5", "刷新当前页面", () => Refresh_Click(RefreshButton, new RoutedEventArgs()));
        Sync(VirtualKey.F11, VirtualKeyModifiers.None, "F11", "进入 / 退出全屏歌词", () => { OpenNowPlaying(); SetLyricsFullScreen(!_lyricsFullScreen); });
        _exitLyricsShortcut = RegisterShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, "Esc", "退出全屏 / 收起歌词或待播清单", () =>
        {
            if (_lyricsFullScreen) SetLyricsFullScreen(false);
            else if (_immersiveOpen) CloseNowPlaying();
            else if (_inspectorOpen) SetInspector(_queueVisible, true);
            return Task.CompletedTask;
        });
        _exitLyricsShortcut.IsEnabled = false;
        RegisterShortcut(VirtualKey.F1, VirtualKeyModifiers.None, "F1", "查看快捷键", ShowShortcutsAsync);

        // Preview events tunnel before a focused control's accelerator/OnKeyDown.
        // Handling both phases prevents a focused Button from turning Space's
        // release into Click, including the artwork button that opens lyrics.
        _shortcutPreviewKeyDown = Shortcut_PreviewKeyDown;
        _shortcutPreviewKeyUp = Shortcut_PreviewKeyUp;
        Root.AddHandler(UIElement.PreviewKeyDownEvent, _shortcutPreviewKeyDown, true);
        Root.AddHandler(UIElement.PreviewKeyUpEvent, _shortcutPreviewKeyUp, true);
        FocusManager.GettingFocus += Shortcuts_GettingFocus;
        Activated += Shortcuts_WindowActivated;
        Closed += Shortcuts_WindowClosed;
    }

    private KeyboardAccelerator RegisterShortcut(VirtualKey key, VirtualKeyModifiers modifiers, string label,
        string description, Func<Task> action, bool typingSensitive = false, bool allowRepeat = false)
    {
        if (label.Length > 0) _shortcutHelp.Add((label, description));
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        var binding = new ShortcutBinding(shortcut, action, typingSensitive, allowRepeat);
        _shortcutBindings.Add((key, modifiers), binding);
        shortcut.Invoked += async (_, args) =>
        {
            if (!CanRunShortcut(binding)) return;
            args.Handled = true;
            // A handled PreviewKeyDown never reaches the accelerator stage.
            // Popups can receive KeyUp on a different visual tree, so the
            // release-tracking set must not suppress later native invocations.
            await RunShortcutAsync(binding);
        };
        Root.KeyboardAccelerators.Add(shortcut);
        return shortcut;
    }

    private async void Shortcut_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_startupReady) { args.Handled = true; return; }
        // The same routed event can reach more than one registered input scope.
        // Track that event, rather than the physical key, to preserve key repeat.
        if (_handledShortcutPreviewEvents.TryGetValue(args, out _)) { args.Handled = true; return; }
        if (!_shortcutBindings.TryGetValue((args.Key, CurrentShortcutModifiers()), out var binding) || !CanRunShortcut(binding)) return;
        _handledShortcutPreviewEvents.Add(args, new object());
        args.Handled = true;
        _shortcutKeysDown.Add(args.Key);
        // Holding Space/F11/next-track must not repeatedly toggle or restart an
        // operation. Seeking and volume adjustments deliberately support repeat.
        if (args.KeyStatus.WasKeyDown && !binding.AllowRepeat) return;
        await RunShortcutAsync(binding);
    }

    private void Shortcut_PreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_shortcutKeysDown.Remove(args.Key)) args.Handled = true;
    }

    private bool CanRunShortcut(ShortcutBinding binding)
    {
        var shortcut = binding.Accelerator;
        if (_closed || !_startupReady || _loginDialogOpen || _shortcutsDialogOpen || _trackDialogOpen || !shortcut.IsEnabled) return false;
        // Dismiss the focused flyout/dropdown before Escape closes the lyrics
        // underneath it. Other app commands, including Space, keep priority.
        if (shortcut.Key == VirtualKey.Escape && FocusIsInShortcutPopup()) return false;
        if (binding.TypingSensitive && FocusOwnsKey(shortcut.Key)) return false;
        return !(shortcut.Modifiers == VirtualKeyModifiers.Control &&
            (shortcut.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) && IsEditingText());
    }

    private async Task RunShortcutAsync(ShortcutBinding binding)
    {
        try { await binding.Action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) ShowError("操作未完成", ex.Message); }
    }

    private static VirtualKeyModifiers CurrentShortcutModifiers()
    {
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        var modifiers = VirtualKeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    private void Shortcuts_WindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) _shortcutKeysDown.Clear();
    }

    private void Shortcuts_WindowClosed(object sender, WindowEventArgs args)
    {
        FocusManager.GettingFocus -= Shortcuts_GettingFocus;
        foreach (var popup in new List<Popup>(_shortcutPopups.Keys)) DetachShortcutPopup(popup);
        if (_shortcutPreviewKeyDown is not null) Root.RemoveHandler(UIElement.PreviewKeyDownEvent, _shortcutPreviewKeyDown);
        if (_shortcutPreviewKeyUp is not null) Root.RemoveHandler(UIElement.PreviewKeyUpEvent, _shortcutPreviewKeyUp);
        _shortcutKeysDown.Clear();
        _handledShortcutPreviewEvents.Clear();
        Activated -= Shortcuts_WindowActivated;
        Closed -= Shortcuts_WindowClosed;
    }

    private void Shortcuts_GettingFocus(object? sender, GettingFocusEventArgs args)
    {
        // Popup contents are outside Root's routed-event ancestry. GettingFocus
        // is synchronous, so wire their child before it receives the first key.
        var xamlRoot = Root.XamlRoot;
        if (_closed || xamlRoot is null || args.NewFocusedElement is not UIElement focused || focused.XamlRoot != xamlRoot) return;
        foreach (var popup in new List<Popup>(_shortcutPopups.Keys))
            if (!popup.IsOpen || popup.XamlRoot != xamlRoot) DetachShortcutPopup(popup);
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is not UIElement child) continue;
            if (_shortcutPopups.TryGetValue(popup, out var previous))
            {
                if (ReferenceEquals(previous, child)) continue;
                DetachShortcutPopup(popup);
            }
            child.AddHandler(UIElement.PreviewKeyDownEvent, _shortcutPreviewKeyDown!, true);
            child.AddHandler(UIElement.PreviewKeyUpEvent, _shortcutPreviewKeyUp!, true);
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
        foreach (var pair in new List<KeyValuePair<Popup, UIElement>>(_shortcutPopups))
            if (ReferenceEquals(pair.Value, sender)) DetachShortcutPopup(pair.Key);
    }

    private void DetachShortcutPopup(Popup popup)
    {
        if (!_shortcutPopups.Remove(popup, out var child)) return;
        child.RemoveHandler(UIElement.PreviewKeyDownEvent, _shortcutPreviewKeyDown!);
        child.RemoveHandler(UIElement.PreviewKeyUpEvent, _shortcutPreviewKeyUp!);
        popup.Closed -= ShortcutPopup_Closed;
        if (child is FrameworkElement element) element.Unloaded -= ShortcutPopupChild_Unloaded;
    }

    private bool FocusIsInShortcutPopup()
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            foreach (var child in _shortcutPopups.Values)
                if (ReferenceEquals(element, child)) return true;
        return false;
    }

    private bool FocusOwnsKey(VirtualKey key)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox) return true;
            // Space is app-wide, including on buttons, sliders and navigation.
            // Arrow keys retain native value changes and keyboard list browsing.
            if (key != VirtualKey.Space && element is ComboBox or ComboBoxItem or MenuFlyoutPresenter or Slider or ListViewBase or NavigationView) return true;
        }
        return false;
    }

    private bool IsEditingText()
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox) return true;
        return false;
    }

    private async void Shortcuts_Click(object sender, RoutedEventArgs e) => await ShowShortcutsAsync();

    private async Task ShowShortcutsAsync()
    {
        if (_shortcutsDialogOpen || _loginDialogOpen || _closed) return;
        _shortcutsDialogOpen = true;
        try
        {
            var rows = new StackPanel { Spacing = 13, MinWidth = 360 };
            foreach (var entry in _shortcutHelp)
            {
                var row = new Grid { ColumnSpacing = 24 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = entry.Description, FontSize = 13 });
                var key = new TextBlock { Text = entry.Keys, FontSize = 12, Foreground = ThemeBrush("LumaTextSecondaryBrush") };
                Grid.SetColumn(key, 1);
                row.Children.Add(key);
                rows.Children.Add(row);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "键盘快捷键", CloseButtonText = "完成",
                Content = new ScrollViewer { Content = rows, MaxHeight = 440, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            await dialog.ShowAsync();
        }
        finally { _shortcutsDialogOpen = false; }
    }
}
