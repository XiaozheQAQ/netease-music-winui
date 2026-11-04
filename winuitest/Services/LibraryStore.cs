using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using winuitest.Models;

namespace winuitest.Services;

/// <summary>Small, device-local library. Saving never blocks music playback on an I/O failure.</summary>
public sealed class LibraryStore
{
    private const int RecentLimit = 100;
    private readonly string _path;
    private readonly object _gate = new();
    private LibraryData _data;

    public LibraryStore(string? directory = null)
    {
        var folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaMusic");
        _path = Path.Combine(folder, "library.json");
        _data = Load();
    }

    public event EventHandler? Changed;
    public string? LastSaveError { get; private set; }

    public IReadOnlyList<Track> Favorites => _data.Favorites.AsReadOnly();
    public IReadOnlyList<Track> Recent => _data.Recent.AsReadOnly();
    public PlaybackSessionSnapshot LastSession => NormalizeSession(_data.LastSession);

    /// <summary>Per-song lyric calibration; positive values delay lyrics without moving audio.</summary>
    public int GetLyricOffsetMs(long trackId)
    {
        lock (_gate)
            return trackId > 0 && _data.LyricOffsetsMs.TryGetValue(trackId, out var offset) ? offset : 0;
    }

    public int SetLyricOffsetMs(long trackId, int offsetMs)
    {
        if (trackId <= 0) return 0;
        var offset = LyricTiming.NormalizeOffsetMs(offsetMs);
        lock (_gate)
        {
            if (GetLyricOffsetMs(trackId) == offset) return offset;
            // A zero offset is the default, so resets leave no redundant durable entry.
            if (offset == 0) _data.LyricOffsetsMs.Remove(trackId);
            else _data.LyricOffsetsMs[trackId] = offset;
            Save();
        }
        return offset;
    }

    public void ResetLyricOffset(long trackId) => SetLyricOffsetMs(trackId, 0);

    public string PlaybackQuality
    {
        get => _data.PlaybackQuality;
        set
        {
            var quality = AudioQuality.Normalize(value);
            if (_data.PlaybackQuality == quality) return;
            _data.PlaybackQuality = quality;
            Save();
        }
    }

    /// <summary>An empty endpoint ID follows the Windows default audio output.</summary>
    public string AudioOutputDeviceId
    {
        get => _data.AudioOutputDeviceId;
        set
        {
            var deviceId = NormalizeAudioOutputDeviceId(value);
            if (_data.AudioOutputDeviceId == deviceId) return;
            _data.AudioOutputDeviceId = deviceId;
            Save();
        }
    }

    private static string NormalizeAudioOutputDeviceId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ? string.Empty : value.Trim();

    public ThemePreference Theme
    {
        get => _data.Theme;
        set
        {
            if (!Enum.IsDefined(value) || _data.Theme == value) return;
            _data.Theme = value;
            Save();
        }
    }

    public void SavePlaybackSession(Track? currentTrack, IEnumerable<Track> queue, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _data.LastSession = NormalizeSession(new PlaybackSessionSnapshot
        {
            CurrentTrack = currentTrack,
            Queue = queue.ToList(),
            PositionMs = Math.Max(0, position.Ticks / TimeSpan.TicksPerMillisecond)
        });
        Save();
    }

    public double Volume
    {
        get => _data.Volume;
        set
        {
            var volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.75;
            if (Math.Abs(_data.Volume - volume) < 0.001) return;
            _data.Volume = volume;
            Save();
        }
    }

    public bool Shuffle
    {
        get => _data.Shuffle;
        set { if (_data.Shuffle != value) { _data.Shuffle = value; Save(); } }
    }

    public RepeatMode Repeat
    {
        get => _data.Repeat;
        set
        {
            if (!Enum.IsDefined(value) || _data.Repeat == value) return;
            _data.Repeat = value;
            Save();
        }
    }

    public bool IsFavorite(long id) => _data.Favorites.Any(track => track.Id == id);

    public bool ToggleFavorite(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var existing = _data.Favorites.FindIndex(item => item.Id == track.Id);
        var added = existing < 0;
        if (added) _data.Favorites.Insert(0, track);
        else _data.Favorites.RemoveAt(existing);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Remember(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _data.Recent.RemoveAll(item => item.Id == track.Id);
        _data.Recent.Insert(0, track);
        if (_data.Recent.Count > RecentLimit) _data.Recent.RemoveRange(RecentLimit, _data.Recent.Count - RecentLimit);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRecent()
    {
        _data.Recent.Clear();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private LibraryData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new LibraryData();
            var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(_path));
            if (data is null) return new LibraryData();
            data.Favorites = (data.Favorites ?? new()).Where(t => t is not null && t.Id > 0).DistinctBy(t => t.Id).ToList();
            data.Recent = (data.Recent ?? new()).Where(t => t is not null && t.Id > 0).DistinctBy(t => t.Id).Take(RecentLimit).ToList();
            data.Volume = double.IsFinite(data.Volume) ? Math.Clamp(data.Volume, 0, 1) : 0.75;
            if (!Enum.IsDefined(data.Repeat)) data.Repeat = RepeatMode.Off;
            if (!Enum.IsDefined(data.Theme)) data.Theme = ThemePreference.System;
            data.PlaybackQuality = AudioQuality.Normalize(data.PlaybackQuality);
            data.AudioOutputDeviceId = NormalizeAudioOutputDeviceId(data.AudioOutputDeviceId);
            data.LastSession = NormalizeSession(data.LastSession);
            data.LyricOffsetsMs = (data.LyricOffsetsMs ?? new())
                .Where(entry => entry.Key > 0)
                .Select(entry => new KeyValuePair<long, int>(entry.Key, LyricTiming.NormalizeOffsetMs(entry.Value)))
                .Where(entry => entry.Value != 0)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastSaveError = "本地音乐资料库无法读取，已使用空资料库。";
            return new LibraryData();
        }
    }

    private static PlaybackSessionSnapshot NormalizeSession(PlaybackSessionSnapshot? session)
    {
        if (session is null) return new PlaybackSessionSnapshot();
        var queue = (session.Queue ?? new()).Where(track => track is not null && track.Id > 0)
            .DistinctBy(track => track.Id).Select(CopyTrack).ToList();
        var current = session.CurrentTrack is { Id: > 0 } currentTrack ? CopyTrack(currentTrack) : null;
        if (current is not null && !queue.Any(item => item.Id == current.Id)) queue.Insert(0, current);
        var maximum = current?.DurationMs > 0 ? current.DurationMs : long.MaxValue / TimeSpan.TicksPerMillisecond;
        return new PlaybackSessionSnapshot
        {
            CurrentTrack = current,
            Queue = queue,
            PositionMs = current is null ? 0 : Math.Clamp(session.PositionMs, 0, maximum)
        };
    }

    private static Track CopyTrack(Track track) => new()
    {
        Id = track.Id,
        Title = track.Title ?? string.Empty,
        Artist = track.Artist ?? string.Empty,
        Album = track.Album ?? string.Empty,
        ArtworkUrl = track.ArtworkUrl ?? string.Empty,
        DurationMs = Math.Clamp(track.DurationMs, 0, long.MaxValue / TimeSpan.TicksPerMillisecond)
    };

    private void Save()
    {
        lock (_gate)
        {
            var temporaryPath = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Replace the old file only after the complete new document is written.
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, _data);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
                LastSaveError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastSaveError = "无法保存本地音乐资料库：" + ex.Message;
            }
        }
    }

    public sealed class LibraryData
    {
        public List<Track> Favorites { get; set; } = new();
        public List<Track> Recent { get; set; } = new();
        public double Volume { get; set; } = 0.75;
        public bool Shuffle { get; set; }
        public RepeatMode Repeat { get; set; }
        public ThemePreference Theme { get; set; } = ThemePreference.System;
        public string PlaybackQuality { get; set; } = AudioQuality.Default;
        public string AudioOutputDeviceId { get; set; } = string.Empty;
        public PlaybackSessionSnapshot LastSession { get; set; } = new();
        public Dictionary<long, int> LyricOffsetsMs { get; set; } = new();
    }
}
