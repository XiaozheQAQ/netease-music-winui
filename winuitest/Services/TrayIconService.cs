using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace winuitest.Services;

public enum TrayCommand { ShowWindow, PlayPause, Previous, Next, NowPlaying, Playlists, Favorites, Settings, Exit }

public sealed record TrayAudioOutput(string Id, string Name);

public sealed record TrayState
{
    public string TrackTitle { get; init; } = "";
    public string Artist { get; init; } = "";
    public bool CanControl { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsLoading { get; init; }
    public ElementTheme Theme { get; init; }
    public string SelectedAudioOutputId { get; init; } = "";
}

/// <summary>Owns one notification icon and its Windows shortcut menu. Create and dispose on the UI dispatcher.</summary>
public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 103;
    private static readonly Guid IconGuid = new("E478F7A2-F899-482F-866F-095C17D30CBA");
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<TrayState> _getState;
    private readonly Func<TrayCommand, Task> _executeCommand;
    private readonly Func<Task<IReadOnlyList<TrayAudioOutput>>> _getAudioOutputs;
    private readonly Func<string, Task> _selectAudioOutput;
    private readonly TrayNative.WindowProcedure _windowProcedure;
    private readonly string _className = "LumaMusic.Tray." + Guid.NewGuid().ToString("N");
    private readonly nint _module;
    private readonly uint _taskbarCreated;
    private nint _messageWindow;
    private nint _icon;
    private TrayPopupMenu? _menu;
    private readonly TrayNative.MessageFilter _menuKeyFilter;
    private nint _menuKeyHook;
    private TrayCommand? _keyboardCommand;
    private TrayState? _menuState;
    private IReadOnlyList<TrayAudioOutput>? _audioOutputs;
    private Task? _audioRefresh;
    private bool _audioOutputsFailed;
    private bool _menuRequested;
    private bool _menuOpen;
    private bool _foregroundFailureReported;
    private string _tooltip = "Luma Music";
    private bool _disposed;

    public TrayIconService(DispatcherQueue dispatcher, string iconPath, Func<TrayState> getState,
        Func<TrayCommand, Task> executeCommand, Func<Task<IReadOnlyList<TrayAudioOutput>>> getAudioOutputs,
        Func<string, Task> selectAudioOutput)
    {
        _dispatcher = dispatcher;
        _getState = getState;
        _executeCommand = executeCommand;
        _getAudioOutputs = getAudioOutputs;
        _selectAudioOutput = selectAudioOutput;
        VerifyDispatcher();
        _module = TrayNative.GetModuleHandle(null);
        _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
        _windowProcedure = WindowProcedure;
        _menuKeyFilter = FilterMenuKey;
        var windowClass = new TrayNative.WindowClass
        {
            Size = (uint)Marshal.SizeOf<TrayNative.WindowClass>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = _module,
            ClassName = _className
        };
        if (TrayNative.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // A hidden top-level tool window receives TaskbarCreated broadcasts;
            // message-only windows would miss Explorer's restart notification.
            _messageWindow = TrayNative.CreateWindowEx(0x80, _className, "Luma Music tray", 0x80000000,
                0, 0, 0, 0, 0, 0, _module, 0);
            if (_messageWindow == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _icon = TrayNative.LoadImage(0, iconPath, 1, TrayNative.GetSystemMetrics(49), TrayNative.GetSystemMetrics(50), 0x10);
            if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法载入 Luma Music 托盘图标。");
            _tooltip = CreateTooltip(_getState());
            AddIcon();
            _ = RefreshAudioOutputsAsync();
        }
        catch { Dispose(); throw; }
    }

    public bool IsAvailable { get; private set; }
    public event EventHandler<string>? Error;

    public void Refresh()
    {
        VerifyDispatcher();
        if (_disposed) return;
        var tooltip = CreateTooltip(_getState());
        // Playback state updates include the position timer. Do not send a
        // shell modification every 400ms when the tooltip is unchanged.
        if (!string.Equals(_tooltip, tooltip, StringComparison.Ordinal))
        {
            _tooltip = tooltip;
            if (IsAvailable)
            {
                var data = IconData(0x4 | 0x20 | 0x80);
                IsAvailable = TrayNative.ShellNotifyIcon(1, ref data);
            }
        }
        _menuState = _getState();
        if (_menu is not null && _menu.Theme != _menuState.Theme)
            DismissMenu(); // Reopen with freshly measured menus in the new application theme.
        else _menu?.Refresh(_menuState);
    }

    public void ShowMenu()
    {
        VerifyDispatcher();
        if (_disposed || _menuRequested || _menuOpen) return;
        // Queue the native modal loop so this API never blocks its caller.
        // All commands execute after that loop has returned, outside WndProc.
        _menuRequested = true;
        _ = RefreshAudioOutputsAsync();
        if (!_dispatcher.TryEnqueue(() =>
        {
            _menuRequested = false;
            if (_disposed) return;
            try { ShowNativeMenu(); }
            catch (Exception error) { Error?.Invoke(this, error.Message); }
        })) _menuRequested = false;
    }

    internal bool IsMenuOpen => _menuOpen;
    internal nint MenuOwnerHandle => _messageWindow;
    internal bool LastMenuForegroundAccepted { get; private set; }
    internal bool LastMenuOwnerVisible { get; private set; }
    internal string LastMenuStage { get; private set; } = "idle";

    internal void DismissMenu()
    {
        VerifyDispatcher();
        if (_menuOpen) TrayNative.EndMenu();
    }

    private void ShowNativeMenu()
    {
        LastMenuStage = "creating";
        LastMenuForegroundAccepted = LastMenuOwnerVisible = false;
        var identifier = new TrayNative.IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<TrayNative.IconIdentifier>(), Window = _messageWindow, Id = 1, Guid = IconGuid
        };
        if (TrayNative.ShellNotifyIconGetRect(ref identifier, out var anchor) < 0)
        {
            TrayNative.GetCursorPos(out var point);
            anchor = new TrayNative.Rect(point.X, point.Y, point.X + 1, point.Y + 1);
        }
        _menuState = _getState();
        _menu = new TrayPopupMenu(_menuState, _audioOutputs, _audioOutputsFailed);
        _keyboardCommand = null;
        TrayCommand? command = null;
        string? output = null;
        var outputSelected = false;
        try
        {
            // Required for notification menus: without foreground ownership,
            // Windows cannot dismiss the popup on an outside click. A hidden
            // HWND cannot reliably activate: temporarily show the zero-area
            // WS_EX_TOOLWINDOW owner. It draws no pixels, has no taskbar entry,
            // and gains the correct monitor DPI without a separate app surface.
            TrayNative.ShowMenuOwner(_messageWindow, anchor);
            LastMenuOwnerVisible = TrayNative.IsWindowVisible(_messageWindow);
            LastMenuForegroundAccepted = TrayNative.SetForegroundWindow(_messageWindow)
                || TrayNative.GetForegroundWindow() == _messageWindow;
            if (!LastMenuForegroundAccepted)
            {
                LastMenuStage = "foreground-denied";
                if (!_foregroundFailureReported)
                {
                    _foregroundFailureReported = true;
                    Error?.Invoke(this, "Windows 暂未允许打开托盘菜单，请再次右键点击托盘图标。");
                }
                return;
            }
            _foregroundFailureReported = false;
            _menu.PrepareAppearance(_messageWindow);
            _menuKeyHook = TrayNative.SetWindowsHookEx(-1, _menuKeyFilter, 0, TrayNative.GetCurrentThreadId());
            _menuOpen = true;
            LastMenuStage = "tracking";
            var selected = TrayNative.TrackMenu(_menu.Handle, _messageWindow, anchor);
            LastMenuStage = "closed";
            command = _keyboardCommand ?? _menu.GetCommand(selected);
            outputSelected = _menu.TryGetOutput(selected, out output);
        }
        finally
        {
            _menuOpen = false;
            if (_menuKeyHook != 0) TrayNative.UnhookWindowsHookEx(_menuKeyHook);
            _menuKeyHook = 0;
            _menu.Dispose();
            _menu = null;
            _menuState = null;
            // Microsoft's notification-menu contract also requires a benign
            // posted message after dismissal, or the next open may flash shut.
            TrayNative.PostMessage(_messageWindow, 0, 0, 0);
            // Escape or programmatic dismissal must not leave keyboard focus
            // on our hidden owner. An outside click already chose another
            // foreground window; do not take that focus back from the user.
            var returnFocus = !_disposed && command is null && !outputSelected
                && TrayNative.GetForegroundWindow() == _messageWindow;
            TrayNative.ShowWindow(_messageWindow, 0);
            if (returnFocus)
            {
                var data = IconData(0x20);
                TrayNative.ShellNotifyIcon(3, ref data);
            }
            if (_disposed) ReleaseNativeResources();
        }
        if (_disposed) return;
        if (command is not null) _ = ExecuteAsync(command.Value);
        else if (outputSelected) _ = SelectOutputAsync(output!);
    }

    private Task RefreshAudioOutputsAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_audioRefresh is { IsCompleted: false }) return _audioRefresh;
        return _audioRefresh = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var outputs = await _getAudioOutputs();
                if (_disposed) return;
                _audioOutputs = outputs.Where(output => !string.IsNullOrEmpty(output.Id)).DistinctBy(output => output.Id).ToArray();
                _audioOutputsFailed = false;
            }
            catch (Exception) { if (!_disposed) _audioOutputsFailed = true; }
        }
    }

    private async Task ExecuteAsync(TrayCommand command)
    {
        if (_disposed) return;
        try { await _executeCommand(command); }
        catch (Exception error) { if (!_disposed) Error?.Invoke(this, error.Message); }
    }

    private async Task SelectOutputAsync(string output)
    {
        try { await _selectAudioOutput(output); }
        catch (Exception error) { if (!_disposed) Error?.Invoke(this, error.Message); }
        if (!_disposed) _ = RefreshAudioOutputsAsync();
    }

    private nint FilterMenuKey(int code, nuint wParam, nint lParam)
    {
        // WH_MSGFILTER is scoped to this UI thread and exists only while our
        // native menu tracks. It never hooks other applications or global input.
        if (code == 2 && _menuOpen && !_disposed)
        {
            var message = Marshal.PtrToStructure<TrayNative.Message>(lParam);
            var control = (TrayNative.GetKeyState(0x11) & 0x8000) != 0;
            var alt = (TrayNative.GetKeyState(0x12) & 0x8000) != 0;
            var key = (uint)message.WParam;
            var command = !alt && key == 0x20 && !control ? TrayCommand.PlayPause
                : !alt && control && key == 0x25 ? TrayCommand.Previous
                : !alt && control && key == 0x27 ? TrayCommand.Next : (TrayCommand?)null;
            if (command is not null && message.Id is 0x100 or 0x101 or 0x102)
            {
                if (message.Id == 0x100 && (message.LParam.ToInt64() & 0x40000000) == 0
                    && _menuState is { CanControl: true } state && (command != TrayCommand.PlayPause || !state.IsLoading))
                {
                    _keyboardCommand = command;
                    TrayNative.EndMenu();
                }
                return 1;
            }
        }
        return TrayNative.CallNextHookEx(_menuKeyHook, code, wParam, lParam);
    }

    private void AddIcon()
    {
        if (_disposed) return;
        var data = IconData(0x1 | 0x2 | 0x4 | 0x20 | 0x80);
        IsAvailable = TrayNative.ShellNotifyIcon(0, ref data);
        if (IsAvailable)
        {
            data.TimeoutOrVersion = 4;
            TrayNative.ShellNotifyIcon(4, ref data);
        }
    }

    private TrayNative.NotifyIconData IconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<TrayNative.NotifyIconData>(), Window = _messageWindow, Id = 1,
        Flags = flags, CallbackMessage = CallbackMessage, Icon = _icon, Tip = _tooltip,
        Info = "", InfoTitle = "", Guid = IconGuid
    };

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message is 0x2B or 0x2C && _menu?.HandleMessage(message, lParam) == true) return 1;
        if (!_disposed && message == _taskbarCreated) { AddIcon(); return 0; }
        if (!_disposed && message == 0x117 && _menu is not null && (nint)wParam == _menu.AudioHandle)
        {
            // Device enumeration stays async. Refresh the child just before
            // Windows measures it, avoiding layout jumps in an already open menu.
            try { _menu.PopulateAudio(_audioOutputs, _audioOutputsFailed, _getState().SelectedAudioOutputId); }
            catch (Exception) { /* The previous valid submenu remains usable. */ }
        }
        if (!_disposed && message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            if (notification is 0x7B or 0x205 or 0x401)
                _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    try { ShowMenu(); } catch (Exception error) { Error?.Invoke(this, error.Message); }
                });
            else if (notification is 0x202 or 0x203 or 0x400)
                _dispatcher.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    DismissMenu();
                    _ = ExecuteAsync(TrayCommand.ShowWindow);
                });
            return 0;
        }
        return TrayNative.DefWindowProc(window, message, wParam, lParam);
    }

    private static string CreateTooltip(TrayState state)
    {
        var text = string.IsNullOrWhiteSpace(state.TrackTitle) ? "Luma Music"
            : $"Luma Music · {state.TrackTitle}" + (string.IsNullOrWhiteSpace(state.Artist) ? "" : $" — {state.Artist}");
        if (text.Length <= 127) return text;
        var length = char.IsHighSurrogate(text[126]) ? 126 : 127;
        return text[..length];
    }

    public void Dispose()
    {
        VerifyDispatcher();
        if (_disposed) return;
        _disposed = true;
        if (_menuOpen) TrayNative.EndMenu();
        if (_messageWindow != 0)
        {
            var data = IconData(0x20);
            TrayNative.ShellNotifyIcon(2, ref data);
        }
        IsAvailable = false;
        // A callback can dispose the service from the native nested loop.
        // Its HMENU and owner must stay alive until TrackPopupMenuEx unwinds.
        if (!_menuOpen) ReleaseNativeResources();
    }

    private void ReleaseNativeResources()
    {
        if (_messageWindow != 0) { TrayNative.DestroyWindow(_messageWindow); _messageWindow = 0; }
        if (_icon != 0) { TrayNative.DestroyIcon(_icon); _icon = 0; }
        TrayNative.UnregisterClass(_className, _module);
    }

    private void VerifyDispatcher()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("TrayIconService must run on its window dispatcher.");
    }
}
