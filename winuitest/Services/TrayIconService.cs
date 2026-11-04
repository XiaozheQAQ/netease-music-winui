using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using winuitest.Views;

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

/// <summary>Owns one notification icon and its native WinUI menu. Create and dispose on the UI dispatcher.</summary>
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
    private readonly string _iconPath;
    private readonly nint _module;
    private readonly uint _taskbarCreated;
    private nint _messageWindow;
    private nint _icon;
    private TrayMenuWindow? _menu;
    private string _tooltip = "Luma Music";
    private bool _disposed;

    public TrayIconService(DispatcherQueue dispatcher, string iconPath, Func<TrayState> getState,
        Func<TrayCommand, Task> executeCommand, Func<Task<IReadOnlyList<TrayAudioOutput>>> getAudioOutputs,
        Func<string, Task> selectAudioOutput)
    {
        _dispatcher = dispatcher;
        _iconPath = iconPath;
        _getState = getState;
        _executeCommand = executeCommand;
        _getAudioOutputs = getAudioOutputs;
        _selectAudioOutput = selectAudioOutput;
        VerifyDispatcher();
        _module = TrayNative.GetModuleHandle(null);
        _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
        _windowProcedure = WindowProcedure;
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
        _menu?.RefreshState();
    }

    public void ShowMenu()
    {
        VerifyDispatcher();
        if (_disposed) return;
        _menu ??= new TrayMenuWindow(_iconPath, _getState, _executeCommand, _getAudioOutputs, _selectAudioOutput);
        var identifier = new TrayNative.IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<TrayNative.IconIdentifier>(), Window = _messageWindow, Id = 1, Guid = IconGuid
        };
        if (TrayNative.ShellNotifyIconGetRect(ref identifier, out var anchor) < 0)
        {
            TrayNative.GetCursorPos(out var point);
            anchor = new TrayNative.Rect(point.X, point.Y, point.X + 1, point.Y + 1);
        }
        _menu.ShowMenu(anchor);
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
        if (!_disposed && message == _taskbarCreated) { AddIcon(); return 0; }
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
                _dispatcher.TryEnqueue(async () =>
                {
                    if (_disposed) return;
                    try { await _executeCommand(TrayCommand.ShowWindow); } catch (Exception error) { Error?.Invoke(this, error.Message); }
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
        _menu?.Destroy();
        _menu = null;
        if (_messageWindow != 0)
        {
            var data = IconData(0x20);
            TrayNative.ShellNotifyIcon(2, ref data);
            TrayNative.DestroyWindow(_messageWindow);
            _messageWindow = 0;
        }
        if (_icon != 0) { TrayNative.DestroyIcon(_icon); _icon = 0; }
        TrayNative.UnregisterClass(_className, _module);
        IsAvailable = false;
    }

    private void VerifyDispatcher()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("TrayIconService must run on its window dispatcher.");
    }
}
