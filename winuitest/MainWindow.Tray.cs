using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using winuitest.Services;

namespace winuitest;

public sealed partial class MainWindow
{
    private TrayIconService? _tray;
    private bool _audioOutputReady;

    private void InitializeTray()
    {
        if (_tray is not null || _closed) return;
        try
        {
            _tray = new TrayIconService(DispatcherQueue,
                Path.Combine(AppContext.BaseDirectory, "Assets", "Luma.ico"),
                GetTrayState, ExecuteTrayCommandAsync, GetTrayAudioOutputsAsync, SelectTrayAudioOutputAsync);
            _tray.Error += (_, message) => ShowError("托盘操作未完成", message);
            TraceStartup(_tray.IsAvailable ? "tray-ready" : "tray-unavailable");
            RunTrayDiagnostics();
        }
        catch (Exception error) { ShowError("托盘暂时不可用", error.Message); }
    }

    // The optional native integration harness is linked only in check builds.
    // Release builds erase both this declaration and its call.
    partial void RunTrayDiagnostics();

    private TrayState GetTrayState() => new()
    {
        TrackTitle = _player.CurrentTrack?.Title ?? "Luma Music",
        Artist = _player.CurrentTrack?.Artist ?? string.Empty,
        CanControl = _startupReady && _player.CurrentTrack is not null,
        IsPlaying = _player.IsPlaying,
        IsLoading = _player.IsLoading,
        Theme = Root.ActualTheme,
        SelectedAudioOutputId = _player.SelectedAudioOutputId
    };

    private void RefreshTray()
    {
        if (_closed) return;
        if (_audioOutputReady) _library.AudioOutputDeviceId = _player.SelectedAudioOutputId;
        _tray?.Refresh();
    }

    private async Task RestoreAudioOutputAsync(CancellationToken token)
    {
        try
        {
            if (_library.AudioOutputDeviceId.Length > 0)
                await _player.SelectAudioOutputAsync(_library.AudioOutputDeviceId, token);
            if (_closed) return;
            _audioOutputReady = true;
            RefreshTray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_closed) return;
            await _player.SelectAudioOutputAsync(null, token);
            _audioOutputReady = true;
            RefreshTray();
            ShowError("音频设备不可用", error.Message + " 已使用系统默认输出。");
        }
    }

    private async Task<IReadOnlyList<TrayAudioOutput>> GetTrayAudioOutputsAsync()
    {
        var outputs = await _player.GetAudioOutputsAsync(_startupLifetime.Token);
        return outputs.Select(output => new TrayAudioOutput(output.Id, output.Name)).ToArray();
    }

    private async Task SelectTrayAudioOutputAsync(string deviceId)
    {
        if (_closed) return;
        await _player.SelectAudioOutputAsync(deviceId, _startupLifetime.Token);
        if (_closed) return;
        _audioOutputReady = true;
        RefreshTray();
    }

    private void ShowMainWindow()
    {
        if (_closed) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    private async Task ExecuteTrayCommandAsync(TrayCommand command)
    {
        if (_closed) return;
        if (command == TrayCommand.Exit) { Close(); return; }
        if (command == TrayCommand.ShowWindow) { ShowMainWindow(); return; }
        if (!_startupReady) return;
        switch (command)
        {
            case TrayCommand.PlayPause: _player.TogglePlayPause(); break;
            case TrayCommand.Previous: await _player.PreviousAsync(); break;
            case TrayCommand.Next: await _player.NextAsync(); break;
            case TrayCommand.NowPlaying:
                ShowMainWindow();
                OpenNowPlaying();
                break;
            case TrayCommand.Playlists:
                ShowMainWindow();
                CloseNowPlaying();
                await OpenCloudAsync();
                break;
            case TrayCommand.Favorites:
                ShowMainWindow();
                CloseNowPlaying();
                OpenLibrary("favorites");
                break;
            case TrayCommand.Settings:
                ShowMainWindow();
                CloseNowPlaying();
                BeginPage("settings");
                break;
        }
    }
}
