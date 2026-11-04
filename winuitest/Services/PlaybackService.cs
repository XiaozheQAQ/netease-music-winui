using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using winuitest.Models;

namespace winuitest.Services;

/// <summary>One native audio player; Node resolves URLs, while Windows owns decoding and playback.</summary>
public sealed class PlaybackService : IDisposable
{
    private readonly NeteaseMusicClient _client;
    private readonly DispatcherQueue _dispatcher;
    private MediaPlayer _player;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly List<Track> _queue = new();
    private readonly Stack<long> _history = new();
    private CancellationTokenSource? _playRequest;
    private MediaSource? _source;
    private int _generation;
    private bool _disposed;
    private bool _endHandled;
    private TimeSpan? _pendingStartPosition;
    private bool _playWhenOpened = true;
    private bool _shuffle;
    private double _volume = 0.75;
    private RepeatMode _repeat;
    private readonly CancellationTokenSource _audioOutputLifetime = new();
    private readonly Dictionary<string, DeviceInformation> _watchedAudioOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableAudioOutputs = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _audioOutputWatcher;
    private DeviceInformation? _selectedAudioOutput;
    private int _audioOutputSelectionGeneration;

    public PlaybackService(NeteaseMusicClient client, DispatcherQueue dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        _player = CreatePlayer();
        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(400);
        _positionTimer.Tick += (_, _) => RefreshPosition();
        _positionTimer.Start();
        StartAudioOutputWatcher();
    }

    private MediaPlayer CreatePlayer(bool autoPlay = true)
    {
        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = autoPlay,
            Volume = _volume
        };
        if (_selectedAudioOutput is not null)
        {
            try { player.AudioDevice = _selectedAudioOutput; }
            catch (Exception error) when (IsAudioDeviceError(error))
            {
                // The endpoint can disappear between a watcher notification and a track change.
                // A new player already uses the system default, so it can continue opening.
                _selectedAudioOutput = null;
            }
        }
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        // MediaPlayer provides SMTC play/pause/seek. Queue navigation belongs to this service.
        player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.NextReceived += (_, args) =>
        {
            args.Handled = true;
            DispatchAsync(async () => { if (ReferenceEquals(player, _player)) await NextAsync(); });
        };
        player.CommandManager.PreviousReceived += (_, args) =>
        {
            args.Handled = true;
            DispatchAsync(async () => { if (ReferenceEquals(player, _player)) await PreviousAsync(); });
        };
        player.CommandManager.ShuffleReceived += (_, args) =>
        {
            args.Handled = true;
            Dispatch(() => { if (ReferenceEquals(player, _player)) Shuffle = args.IsShuffleRequested; });
        };
        player.CommandManager.AutoRepeatModeReceived += (_, args) =>
        {
            args.Handled = true;
            Dispatch(() =>
            {
                if (!ReferenceEquals(player, _player)) return;
                Repeat = args.AutoRepeatMode switch
                {
                    MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                    MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                    _ => RepeatMode.Off
                };
            });
        };
        player.SystemMediaTransportControls.ShuffleEnabled = _shuffle;
        player.SystemMediaTransportControls.AutoRepeatMode = _repeat switch
        {
            RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
            RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None
        };
        return player;
    }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Error;
    public Track? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsPreview { get; private set; }
    public string Quality { get; private set; } = string.Empty;
    public TimeSpan Position { get; private set; }
    /// <summary>Upstream preview start within the full song; playback/seek remain clip-relative.</summary>
    public TimeSpan LyricOffset { get; private set; }
    public TimeSpan LyricPosition => Position + LyricOffset;
    /// <summary>Read the native playback clock on the UI dispatcher for smooth karaoke rendering.</summary>
    public TimeSpan GetLyricPosition()
    {
        if (_disposed) return LyricPosition;
        try { return _player.Source is null || _pendingStartPosition.HasValue ? LyricPosition : _player.PlaybackSession.Position + LyricOffset; }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return LyricPosition;
        }
    }
    public TimeSpan Duration { get; private set; }
    public IReadOnlyList<Track> Queue => _queue.AsReadOnly();

    /// <summary>An empty ID follows the Windows default output instead of pinning an endpoint.</summary>
    public string SelectedAudioOutputId => _selectedAudioOutput?.Id ?? string.Empty;

    /// <summary>Returns actual enabled endpoints only; callers provide the system-default option.</summary>
    public async Task<IReadOnlyList<AudioOutputOption>> GetAudioOutputsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AudioOutputOption> result = Array.Empty<AudioOutputOption>();
        await OnUiAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectionGeneration = _audioOutputSelectionGeneration;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _audioOutputLifetime.Token);
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(request.Token);
            if (_disposed) return;
            request.Token.ThrowIfCancellationRequested();
            var available = devices.Where(IsAvailableAudioOutput).ToArray();
            if (selectionGeneration == _audioOutputSelectionGeneration && _selectedAudioOutput is not null &&
                !available.Any(device => SameAudioOutput(device.Id, SelectedAudioOutputId)))
                ResetUnavailableAudioOutput();
            result = available.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .Select(device => new AudioOutputOption(device.Id,
                    string.IsNullOrWhiteSpace(device.Name) ? "音频输出设备" : device.Name,
                    SameAudioOutput(device.Id, SelectedAudioOutputId))).ToArray();
        });
        return result;
    }

    /// <summary>Changes the live player's route without changing its source, position or play/pause state.</summary>
    public Task SelectAudioOutputAsync(string? deviceId, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestGeneration = ++_audioOutputSelectionGeneration;
        var requestedId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
        if (requestedId.Length == 0)
        {
            ApplyAudioOutput(null);
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _audioOutputLifetime.Token);
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(request.Token);
        if (_disposed || requestGeneration != _audioOutputSelectionGeneration) return;
        request.Token.ThrowIfCancellationRequested();
        var device = devices.FirstOrDefault(item => SameAudioOutput(item.Id, requestedId) && IsAvailableAudioOutput(item));
        if (device is null)
        {
            ResetUnavailableAudioOutput();
            return;
        }

        try { ApplyAudioOutput(device); }
        catch (Exception error) when (IsAudioDeviceError(error))
        {
            ResetUnavailableAudioOutput();
            Error?.Invoke(this, "所选音频设备暂时不可用，已切换到系统默认输出。");
        }
    });

    private void ApplyAudioOutput(DeviceInformation? device)
    {
        // Setting null restores MediaPlayer's normal Windows default-device routing.
        _player.AudioDevice = device!;
        _selectedAudioOutput = device;
        RaiseStateChanged();
    }

    private void ResetUnavailableAudioOutput()
    {
        // Also forget an unplugged endpoint if Windows currently has no usable default.
        // The next track will then create a player with default routing.
        _selectedAudioOutput = null;
        try { _player.AudioDevice = null!; }
        catch (Exception error) when (IsAudioDeviceError(error))
        {
            Error?.Invoke(this, "音频输出设备不可用，请检查 Windows 声音设置。");
        }
        RaiseStateChanged();
    }

    private bool IsAvailableAudioOutput(DeviceInformation device) =>
        device.IsEnabled && !_unavailableAudioOutputs.Contains(device.Id);

    private static bool SameAudioOutput(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioDeviceError(Exception error) =>
        error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException;

    private void StartAudioOutputWatcher()
    {
        try
        {
            _audioOutputWatcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
            _audioOutputWatcher.Added += OnAudioOutputAdded;
            _audioOutputWatcher.Updated += OnAudioOutputUpdated;
            _audioOutputWatcher.Removed += OnAudioOutputRemoved;
            _audioOutputWatcher.Start();
        }
        catch (Exception error) when (IsAudioDeviceError(error))
        {
            // Enumeration still works on demand when device notifications are unavailable.
            StopAudioOutputWatcher();
        }
    }

    private void OnAudioOutputAdded(DeviceWatcher sender, DeviceInformation device) => Dispatch(() =>
    {
        _watchedAudioOutputs[device.Id] = device;
        if (device.IsEnabled) _unavailableAudioOutputs.Remove(device.Id);
        else _unavailableAudioOutputs.Add(device.Id);
    });

    private void OnAudioOutputUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => Dispatch(() =>
    {
        if (_watchedAudioOutputs.TryGetValue(update.Id, out var device))
        {
            device.Update(update);
            if (device.IsEnabled) _unavailableAudioOutputs.Remove(device.Id);
            else _unavailableAudioOutputs.Add(device.Id);
        }
        if (_selectedAudioOutput is not null && SameAudioOutput(update.Id, SelectedAudioOutputId))
        {
            // The selected object may come from a later FindAllAsync rather than the watcher.
            if (!ReferenceEquals(_selectedAudioOutput, device)) _selectedAudioOutput.Update(update);
            if (!_selectedAudioOutput.IsEnabled || _unavailableAudioOutputs.Contains(update.Id)) ResetUnavailableAudioOutput();
        }
    });

    private void OnAudioOutputRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => Dispatch(() =>
    {
        _watchedAudioOutputs.Remove(update.Id);
        _unavailableAudioOutputs.Add(update.Id);
        if (SameAudioOutput(update.Id, SelectedAudioOutputId)) ResetUnavailableAudioOutput();
    });

    private void StopAudioOutputWatcher()
    {
        var watcher = _audioOutputWatcher;
        _audioOutputWatcher = null;
        if (watcher is null) return;
        watcher.Added -= OnAudioOutputAdded;
        watcher.Updated -= OnAudioOutputUpdated;
        watcher.Removed -= OnAudioOutputRemoved;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
        catch (Exception error) when (IsAudioDeviceError(error)) { }
        _watchedAudioOutputs.Clear();
        _unavailableAudioOutputs.Clear();
    }

    public double Volume
    {
        get => _volume;
        set => Dispatch(() =>
        {
            _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.75;
            _player.Volume = _volume;
            RaiseStateChanged();
        });
    }

    public bool Shuffle
    {
        get => _shuffle;
        set => Dispatch(() =>
        {
            _shuffle = value;
            _player.SystemMediaTransportControls.ShuffleEnabled = value;
            RaiseStateChanged();
        });
    }

    public RepeatMode Repeat
    {
        get => _repeat;
        set => Dispatch(() =>
        {
            if (!Enum.IsDefined(value)) return;
            _repeat = value;
            _player.SystemMediaTransportControls.AutoRepeatMode = value switch
            {
                RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            RaiseStateChanged();
        });
    }

    public Task PlayAsync(Track track, IEnumerable<Track>? queue = null) => OnUiAsync(() => PlayCoreAsync(track, queue));

    public Task RestoreSessionAsync(PlaybackSessionSnapshot snapshot, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        var queue = (snapshot.Queue ?? new()).Where(track => track is not null && track.Id > 0).DistinctBy(track => track.Id).ToList();
        if (snapshot.CurrentTrack is not { Id: > 0 } current)
        {
            _queue.Clear();
            _queue.AddRange(queue);
            RaiseStateChanged();
            return;
        }
        var milliseconds = Math.Clamp(snapshot.PositionMs, 0, long.MaxValue / TimeSpan.TicksPerMillisecond);
        await PlayCoreAsync(current, queue, rememberHistory: false, autoPlay: false,
            startPosition: TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond),
            externalCancellationToken: cancellationToken);
    });

    public Task ReloadCurrentAsync(CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (CurrentTrack is null) return;
        var position = GetLyricPosition();
        var autoPlay = IsLoading ? _playWhenOpened : IsPlaying;
        await PlayCoreAsync(CurrentTrack, rememberHistory: false, autoPlay: autoPlay, startPosition: position, positionIsSongTime: true,
            externalCancellationToken: cancellationToken);
    });

    private async Task PlayCoreAsync(Track track, IEnumerable<Track>? queue = null, bool rememberHistory = true,
        bool autoPlay = true, TimeSpan? startPosition = null, bool positionIsSongTime = false,
        CancellationToken externalCancellationToken = default)
    {
        if (_disposed) return;
        externalCancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(track);
        var requestGeneration = ++_generation;
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        var cancellationToken = _playRequest.Token;

        if (queue is not null)
        {
            var tracks = queue.Where(item => item is not null && item.Id > 0).DistinctBy(item => item.Id).ToList();
            _queue.Clear();
            _queue.AddRange(tracks);
            _history.Clear();
        }
        else if (rememberHistory && CurrentTrack is not null && CurrentTrack.Id != track.Id)
        {
            _history.Push(CurrentTrack.Id);
        }
        if (!_queue.Any(item => item.Id == track.Id)) _queue.Add(track);

        // A fresh native player gives callbacks an unambiguous source identity. Windows may
        // deliver an old player's events after a new song has already begun resolving.
        var outgoingPlayer = _player;
        outgoingPlayer.Pause();
        // A restored source must open without emitting audio before its saved seek is applied.
        _player = CreatePlayer(autoPlay && !startPosition.HasValue);
        ClosePlayer(outgoingPlayer);
        _source?.Dispose();
        _source = null;
        _endHandled = false;
        CurrentTrack = track;
        LyricOffset = TimeSpan.Zero;
        Duration = TimeSpan.FromTicks(Math.Clamp(track.DurationMs, 0, long.MaxValue / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
        Position = startPosition.HasValue ? ClampPosition(startPosition.Value, Duration) : TimeSpan.Zero;
        _pendingStartPosition = startPosition.HasValue ? Position : null;
        _playWhenOpened = autoPlay;
        IsPlaying = false;
        IsLoading = true;
        IsPreview = false;
        Quality = string.Empty;
        RaiseStateChanged();

        try
        {
            var stream = await _client.ResolveTrackAsync(track.Id, cancellationToken);
            if (_disposed || requestGeneration != _generation) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new InvalidOperationException("这首歌曲暂时无法播放，可能需要登录或没有可用音源。");

            IsPreview = stream.IsPreview;
            Quality = stream.Quality;
            LyricOffset = stream.IsPreview
                ? TimeSpan.FromMilliseconds(Math.Clamp(stream.StartOffsetMs, 0, Math.Max(0, track.DurationMs)))
                : TimeSpan.Zero;
            if (positionIsSongTime && startPosition.HasValue)
            {
                Position = startPosition.Value > LyricOffset ? startPosition.Value - LyricOffset : TimeSpan.Zero;
                _pendingStartPosition = Position;
            }
            _source = MediaSource.CreateFromUri(uri);
            var item = new MediaPlaybackItem(_source);
            var properties = item.GetDisplayProperties();
            properties.Type = MediaPlaybackType.Music;
            properties.MusicProperties.Title = track.Title;
            properties.MusicProperties.Artist = track.Artist;
            properties.MusicProperties.AlbumTitle = track.Album;
            if (Uri.TryCreate(track.ArtworkUrl, UriKind.Absolute, out var artworkUri) && artworkUri.Scheme is "https" or "http")
                properties.Thumbnail = RandomAccessStreamReference.CreateFromUri(artworkUri);
            item.ApplyDisplayProperties(properties);
            _player.Source = item;
            if (autoPlay && !_pendingStartPosition.HasValue) _player.Play();
            RaiseStateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_disposed || requestGeneration != _generation) return;
            // A bounded startup restore may stop resolving the URL. Keep the
            // restored queue, track and pending seek so Play can retry normally.
            IsLoading = false;
            IsPlaying = false;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            if (_disposed || requestGeneration != _generation) return;
            IsLoading = false;
            IsPlaying = false;
            RaiseStateChanged();
            Error?.Invoke(this, ex.Message);
        }
    }

    public void TogglePlayPause() => Dispatch(() =>
    {
        if (CurrentTrack is null || IsLoading) return;
        if (_player.Source is null)
        {
            var resumePosition = _pendingStartPosition ?? (Position > TimeSpan.Zero ? Position : (TimeSpan?)null);
            DispatchAsync(() => PlayCoreAsync(CurrentTrack, startPosition: resumePosition));
            return;
        }
        if (IsPlaying) _player.Pause();
        else
        {
            if (Duration > TimeSpan.Zero && Position >= Duration) Seek(0);
            _player.Play();
        }
    });

    public Task NextAsync() => OnUiAsync(() => AdvanceAsync(ended: false));

    public Task PreviousAsync() => OnUiAsync(async () =>
    {
        if (_queue.Count == 0) return;
        if (Position.TotalSeconds > 3 && _player.Source is not null)
        {
            Seek(0);
            return;
        }
        Track? previous = null;
        while (_history.Count > 0 && previous is null)
        {
            var id = _history.Pop();
            previous = _queue.FirstOrDefault(track => track.Id == id);
        }
        var index = _queue.FindIndex(track => track.Id == CurrentTrack?.Id);
        previous ??= _queue[index > 0 ? index - 1 : (_repeat == RepeatMode.All ? _queue.Count - 1 : 0)];
        await PlayCoreAsync(previous, rememberHistory: false);
    });

    private async Task AdvanceAsync(bool ended)
    {
        if (_queue.Count == 0 || _disposed) return;
        if (ended && _repeat == RepeatMode.One && CurrentTrack is not null)
        {
            await PlayCoreAsync(CurrentTrack, rememberHistory: false);
            return;
        }
        var index = _queue.FindIndex(track => track.Id == CurrentTrack?.Id);
        int next;
        if (_shuffle && _queue.Count > 1)
        {
            // Select among the other tracks, so shuffle never immediately repeats the same song.
            next = Random.Shared.Next(_queue.Count - 1);
            if (next >= index && index >= 0) next++;
        }
        else
        {
            next = index + 1;
            if (next >= _queue.Count)
            {
                if (_repeat != RepeatMode.All)
                {
                    _player.Pause();
                    IsPlaying = false;
                    RaiseStateChanged();
                    return;
                }
                next = 0;
            }
        }
        await PlayCoreAsync(_queue[next]);
    }

    public void Seek(double seconds) => Dispatch(() =>
    {
        if (_player.Source is null || !double.IsFinite(seconds) || !_player.PlaybackSession.CanSeek) return;
        var duration = _player.PlaybackSession.NaturalDuration;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, duration.TotalSeconds)));
        if (_player.PlaybackSession.Position < duration) _endHandled = false;
        RefreshPosition();
    });

    public void SeekAndPlay(double seconds) => Dispatch(() =>
    {
        if (CurrentTrack is null || IsLoading || !double.IsFinite(seconds) || seconds < 0) return;
        if (_player.Source is null)
        {
            DispatchAsync(() => PlayCoreAsync(CurrentTrack, startPosition: TimeSpan.FromSeconds(seconds)));
            return;
        }
        if (!_player.PlaybackSession.CanSeek) return;
        Seek(seconds);
        _player.Play();
    });

    public void AddToQueue(Track track) => AddToQueue(new[] { track });

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var snapshot = tracks.Where(track => track.Id > 0).ToArray();
        Dispatch(() =>
        {
            var ids = _queue.Select(track => track.Id).ToHashSet();
            var added = snapshot.Where(track => ids.Add(track.Id)).ToArray();
            if (added.Length == 0) return;
            _queue.AddRange(added);
            RaiseStateChanged();
        });
    }

    public void RemoveFromQueue(Track track) => RemoveFromQueue(new[] { track });

    public void RemoveFromQueue(IEnumerable<Track> tracks)
    {
        var ids = tracks.Select(track => track.Id).ToHashSet();
        Dispatch(() =>
        {
            // Keep the current track until playback moves on, preserving a consistent next index.
            if (CurrentTrack is not null) ids.Remove(CurrentTrack.Id);
            if (_queue.RemoveAll(item => ids.Contains(item.Id)) > 0) RaiseStateChanged();
        });
    }

    public void ClearQueue() => Dispatch(() =>
    {
        _queue.Clear();
        if (CurrentTrack is not null) _queue.Add(CurrentTrack);
        _history.Clear();
        RaiseStateChanged();
    });

    private void OnMediaOpened(MediaPlayer sender, object args) => Dispatch(() =>
    {
        if (!ReferenceEquals(sender, _player) || _player.Source is null) return;
        if (_pendingStartPosition is TimeSpan requestedPosition)
        {
            var duration = _player.PlaybackSession.NaturalDuration;
            if (duration > TimeSpan.Zero) Duration = duration;
            if (_player.PlaybackSession.CanSeek)
            {
                Position = ClampPosition(requestedPosition, Duration);
                _player.PlaybackSession.Position = Position;
            }
            else Position = TimeSpan.Zero;
            _pendingStartPosition = null;
        }
        if (_playWhenOpened) _player.Play();
        else _player.Pause();
        IsLoading = false;
        RefreshPosition();
        RaiseStateChanged();
    });

    private void OnMediaEnded(MediaPlayer sender, object args) => DispatchAsync(async () =>
    {
        if (!ReferenceEquals(sender, _player) || _player.Source is null || IsLoading || _endHandled) return;
        _endHandled = true;
        await AdvanceAsync(ended: true);
    });

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Dispatch(() =>
    {
        if (!ReferenceEquals(sender, _player) || _player.Source is null) return;
        IsLoading = false;
        IsPlaying = false;
        _player.Source = null;
        RaiseStateChanged();
        Error?.Invoke(this, "播放失败，音源可能已失效。请重试或选择其他歌曲。");
    });

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args) => Dispatch(() =>
    {
        if (!ReferenceEquals(sender, _player.PlaybackSession)) return;
        IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        if (_player.Source is not null)
            IsLoading = _pendingStartPosition.HasValue || sender.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        RaiseStateChanged();
    });

    private void RefreshPosition()
    {
        if (_disposed || _player.Source is null) return;
        // Preserve the saved position while opening; the native clock is still zero then.
        if (!_pendingStartPosition.HasValue) Position = _player.PlaybackSession.Position;
        var duration = _player.PlaybackSession.NaturalDuration;
        if (duration > TimeSpan.Zero) Duration = duration;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration) =>
        TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, duration > TimeSpan.Zero ? duration.Ticks : long.MaxValue));

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => { if (!_disposed) action(); });
    }

    private void DispatchAsync(Func<Task> action) => Dispatch(async () =>
    {
        try { await action(); }
        catch (Exception ex) { if (!_disposed) Error?.Invoke(this, ex.Message); }
    });

    private Task OnUiAsync(Func<Task> action)
    {
        if (_disposed) return Task.CompletedTask;
        if (_dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { if (!_disposed) await action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        ++_audioOutputSelectionGeneration;
        _audioOutputLifetime.Cancel();
        StopAudioOutputWatcher();
        _audioOutputLifetime.Dispose();
        _positionTimer.Stop();
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        ClosePlayer(_player);
        _source?.Dispose();
    }

    private void ClosePlayer(MediaPlayer player)
    {
        player.MediaOpened -= OnMediaOpened;
        player.MediaEnded -= OnMediaEnded;
        player.MediaFailed -= OnMediaFailed;
        player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        player.Pause();
        player.Source = null;
        player.Dispose();
    }
}
