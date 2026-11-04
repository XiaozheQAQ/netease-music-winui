using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private const int PlaylistPageSize = 60;
    private readonly Dictionary<long, int> _playlistTrackIndex = new();
    private readonly Dictionary<int, Task> _playlistPageRequests = new();
    private readonly HashSet<int> _playlistFailedPages = new();
    private int _playlistPagingGeneration;
    private bool _trackLocatingReady;
    private bool _locatorRefreshPending;
    private bool _locatingCurrent;
    private long _locatorPlayingId;
    private CancellationTokenSource? _trackPlayIntentRequest;
    private int _trackPlayIntentGeneration;
    private bool _playIntentPreparing;
    private bool _trackPlaybackStarted;
    private DispatcherQueueTimer? _playlistViewportTimer;
    private bool _playlistScrollInProgress;
    private bool _playlistViewportSettling;
    private int _playlistViewportFirst = -1;
    private int _playlistViewportLast = -1;

    private void InitializeTrackLocating()
    {
        if (_trackLocatingReady) return;
        _trackLocatingReady = true;
        _tracks.CollectionChanged += (_, _) => QueueTrackLocatorRefresh();
        _player.StateChanged += (_, _) =>
        {
            var id = _player.CurrentTrack?.Id ?? 0;
            if (_locatorPlayingId == id) return;
            _locatorPlayingId = id;
            QueueTrackLocatorRefresh();
        };
        ArtistView.LayoutUpdated += (_, _) => RefreshTrackLocator();
        Closed += (_, _) =>
        {
            _trackPlayIntentRequest?.Cancel();
            _playlistViewportTimer?.Stop();
        };
        QueueTrackLocatorRefresh();
    }

    private void QueueTrackLocatorRefresh()
    {
        if (_closed || _locatorRefreshPending) return;
        _locatorRefreshPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _locatorRefreshPending = false;
            if (!_closed) RefreshTrackLocator();
        });
    }

    private void RefreshTrackLocator()
    {
        if (!_trackLocatingReady || _closed) return;
        var id = _player.CurrentTrack?.Id ?? 0;
        var contains = id > 0 && (_page == "playlist" && _playlistTrackIndex.Count > 0
            ? _playlistTrackIndex.ContainsKey(id) : _tracks.Any(track => track.Id == id));
        LocateCurrentButton.Visibility = contains && TracksView.Visibility == Visibility.Visible && !_pageLoading
            ? Visibility.Visible : Visibility.Collapsed;
        ArtistLocateCurrentButton.Visibility = contains && _page == "artist" && !_artistAlbumsVisible && !_pageLoading
            ? Visibility.Visible : Visibility.Collapsed;
        LocateCurrentButton.IsEnabled = ArtistLocateCurrentButton.IsEnabled = !_locatingCurrent;
    }

    private void ResetPlaylistPaging()
    {
        // Navigation cancels preparation, but an already playing song still
        // finishes its queue in the background while the user browses elsewhere.
        if (!_trackPlaybackStarted) CancelPendingTrackPlayback();
        _playlistPagingGeneration++;
        _playlistTrackIndex.Clear();
        _playlistPageRequests.Clear();
        _playlistFailedPages.Clear();
        _playlistViewportTimer?.Stop();
        _playlistScrollInProgress = _playlistViewportSettling = false;
        _playlistViewportFirst = _playlistViewportLast = -1;
        _locatingCurrent = false;
        QueueTrackLocatorRefresh();
    }

    private void ShowPlaylistTracks(PlaylistDetail playlist)
    {
        var details = playlist.Tracks.Where(track => track.Id > 0).DistinctBy(track => track.Id).ToDictionary(track => track.Id);
        var ids = (playlist.TrackIds.Count > 0 ? playlist.TrackIds : playlist.Tracks.Select(track => track.Id))
            .Where(id => id > 0).Distinct().ToArray();
        playlist.TrackIds = ids.ToList();
        _playlistTrackIndex.Clear();
        var slots = new Track[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            _playlistTrackIndex[ids[index]] = index;
            slots[index] = details.TryGetValue(ids[index], out var track) ? track
                : new Track { Id = ids[index], IsMetadataLoaded = false };
        }
        ShowTracks(playlist.Title, $"{ids.Length:N0} 首歌曲", slots, "CURATED PLAYLIST");
        QueueTrackLocatorRefresh();
    }

    private bool IsCurrentPlaylistPage(CancellationToken token, int generation) => !_closed && !token.IsCancellationRequested
        && generation == _playlistPagingGeneration && _page == "playlist" && _pageRequest?.Token == token;

    private Task EnsurePlaylistPageAsync(int index, bool retry = false)
    {
        if (_page != "playlist" || _pageRequest is null || index < 0 || index >= _tracks.Count) return Task.CompletedTask;
        var page = index / PlaylistPageSize;
        if (_playlistPageRequests.TryGetValue(page, out var pending)) return pending;
        if (_playlistFailedPages.Contains(page) && !retry) return Task.CompletedTask;
        var ids = _tracks.Skip(page * PlaylistPageSize).Take(PlaylistPageSize)
            .Where(track => !track.IsMetadataLoaded).Select(track => track.Id).ToArray();
        if (ids.Length == 0) return Task.CompletedTask;
        _playlistFailedPages.Remove(page);
        var token = _pageRequest.Token;
        var generation = _playlistPagingGeneration;
        var task = LoadPlaylistPageAsync(ids, page, token, generation);
        if (!task.IsCompleted)
        {
            _playlistPageRequests[page] = task;
            _ = CompletePlaylistPageAsync(task, page, token, generation);
        }
        return task;
    }

    private async Task CompletePlaylistPageAsync(Task task, int page, CancellationToken token, int generation)
    {
        await task;
        if (!IsCurrentPlaylistPage(token, generation)) return;
        if (_playlistPageRequests.TryGetValue(page, out var current) && ReferenceEquals(current, task))
            _playlistPageRequests.Remove(page);
        TryLoadVisiblePlaylistPages();
    }

    private async Task LoadPlaylistPageAsync(long[] ids, int page, CancellationToken token, int generation)
    {
        try
        {
            var details = await _client.GetPlaylistTracksAsync(ids, token);
            if (!IsCurrentPlaylistPage(token, generation)) return;
            ApplyPlaylistMetadata(ids, details);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsCurrentPlaylistPage(token, generation)) return;
            _playlistFailedPages.Add(page);
            ShowError("歌曲加载失败", error.Message + " 点击刷新重试。");
        }
    }

    private void ApplyPlaylistMetadata(IEnumerable<long> requestedIds, IEnumerable<Track> details)
    {
        var byId = details.Where(track => track.Id > 0).DistinctBy(track => track.Id).ToDictionary(track => track.Id);
        var selectedIds = TrackList.SelectedItems.OfType<Track>().Select(track => track.Id).ToHashSet();
        foreach (var id in requestedIds)
        {
            if (!_playlistTrackIndex.TryGetValue(id, out var index) || index >= _tracks.Count || _tracks[index].Id != id) continue;
            var track = byId.TryGetValue(id, out var detail) ? detail
                : new Track { Id = id, Title = "歌曲暂不可用", IsUnavailable = true };
            _tracks[index] = track;
            if (selectedIds.Contains(id) && !TrackList.SelectedItems.Contains(track)) TrackList.SelectedItems.Add(track);
        }
        QueueTrackLocatorRefresh();
    }

    private void TryLoadVisiblePlaylistPages()
    {
        if (_closed || _page != "playlist" || _pageLoading || _playlistTrackIndex.Count == 0
            || TracksView.Visibility != Visibility.Visible || _playlistPageRequests.Count >= 2
            || TrackList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) return;
        // LayoutUpdated can run before ViewChanged reports the same movement.
        // Debounce index changes as well, so dragging a scrollbar or an animated
        // jump loads its destination rather than every page it passes through.
        if (_playlistViewportFirst != panel.FirstVisibleIndex || _playlistViewportLast != panel.LastVisibleIndex)
        {
            _playlistViewportFirst = panel.FirstVisibleIndex;
            _playlistViewportLast = panel.LastVisibleIndex;
            SchedulePlaylistViewportLoad();
            return;
        }
        if (_playlistScrollInProgress || _playlistViewportSettling) return;
        // Native virtualization supplies indices even when the user drags the
        // scrollbar directly to the middle of a 20,000-song collection.
        var first = Math.Max(0, panel.FirstVisibleIndex - 4);
        var last = Math.Min(_tracks.Count - 1, Math.Max(first, panel.LastVisibleIndex) + 8);
        for (var page = first / PlaylistPageSize; page <= last / PlaylistPageSize && _playlistPageRequests.Count < 2; page++)
            _ = EnsurePlaylistPageAsync(page * PlaylistPageSize);
    }

    private void PlaylistViewportChanged(bool intermediate)
    {
        if (_page != "playlist") return;
        _playlistScrollInProgress = intermediate;
        SchedulePlaylistViewportLoad();
    }

    private void SchedulePlaylistViewportLoad()
    {
        _playlistViewportSettling = true;
        if (_playlistViewportTimer is null)
        {
            _playlistViewportTimer = DispatcherQueue.CreateTimer();
            _playlistViewportTimer.IsRepeating = false;
            _playlistViewportTimer.Interval = TimeSpan.FromMilliseconds(90);
            _playlistViewportTimer.Tick += (_, _) =>
            {
                if (_closed || _playlistScrollInProgress) return;
                _playlistViewportSettling = false;
                TryLoadVisiblePlaylistPages();
            };
        }
        _playlistViewportTimer.Stop();
        if (!_playlistScrollInProgress) _playlistViewportTimer.Start();
    }

    private async Task<Track[]> ResolveTracksForPlaybackAsync(IEnumerable<Track> source, CancellationToken token)
    {
        var tracks = source.ToArray();
        var missing = tracks.Where(track => !track.IsMetadataLoaded).Select(track => track.Id).Distinct().ToArray();
        var byId = new Dictionary<long, Track>();
        var generation = _playlistPagingGeneration;
        var sourcePage = _pageRequest;
        var batches = missing.Chunk(500).ToArray();
        var nextBatch = 0;
        await Task.WhenAll(Enumerable.Range(0, Math.Min(4, batches.Length)).Select(async _ =>
        {
            while (nextBatch < batches.Length)
            {
                token.ThrowIfCancellationRequested();
                var batch = batches[nextBatch++];
                var details = await _client.GetPlaylistTracksAsync(batch, token);
                foreach (var track in details) byId[track.Id] = track;
                if (!_closed && !token.IsCancellationRequested && generation == _playlistPagingGeneration
                    && _page == "playlist" && ReferenceEquals(sourcePage, _pageRequest)) ApplyPlaylistMetadata(batch, details);
            }
        }));
        token.ThrowIfCancellationRequested();
        return tracks.Select(track => track.IsMetadataLoaded ? track : byId.GetValueOrDefault(track.Id))
            .Where(track => track is not null).Cast<Track>().ToArray();
    }

    private async Task RunResolvedTrackActionAsync(IEnumerable<Track> tracks, Func<Track[], Task> action)
    {
        var token = _pageRequest?.Token ?? _accountLifetime.Token;
        try
        {
            var ready = await ResolveTracksForPlaybackAsync(tracks, token);
            if (!_closed && ready.Length > 0) await action(ready);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError("歌曲信息加载失败", error.Message); }
    }

    private void CancelPendingTrackPlayback()
    {
        _trackPlayIntentGeneration++;
        _trackPlayIntentRequest?.Cancel();
        _trackPlayIntentRequest = null;
        _playIntentPreparing = false;
        _trackPlaybackStarted = false;
        if (PlayAllButton is not null) PlayAllButton.IsEnabled = _tracks.Count > 0;
    }

    private async Task PlayTracksWithIntentAsync(Track track, IEnumerable<Track>? source, bool expandInBackground = false)
    {
        if (_closed) return;
        CancelPendingTrackPlayback();
        var generation = _trackPlayIntentGeneration;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountLifetime.Token);
        var playbackStarted = false;
        using var pageRegistration = (_pageRequest?.Token ?? CancellationToken.None).Register(() =>
        {
            if (!playbackStarted) request.Cancel();
        });
        _trackPlayIntentRequest = request;
        var token = request.Token;
        var tracks = source?.ToArray();
        _playIntentPreparing = !track.IsMetadataLoaded || tracks?.Any(item => !item.IsMetadataLoaded) == true;
        PlayAllButton.IsEnabled = _tracks.Count > 0 && !_playIntentPreparing;
        try
        {
            if (expandInBackground && tracks is not null && tracks.Any(item => !item.IsMetadataLoaded))
            {
                var target = track.IsMetadataLoaded ? track
                    : (await ResolveTracksForPlaybackAsync(new[] { track }, token)).FirstOrDefault();
                if (_closed || token.IsCancellationRequested || generation != _trackPlayIntentGeneration) return;
                if (target is null) { ShowToast("这首歌曲暂时不可用"); return; }
                var initialQueue = tracks.Select(item => item.Id == target.Id ? target : item)
                    .Where(item => item.IsMetadataLoaded).ToArray();
                var expectedIds = initialQueue.Select(item => item.Id).ToArray();
                _playIntentPreparing = false;
                PlayAllButton.IsEnabled = _tracks.Count > 0;
                playbackStarted = _trackPlaybackStarted = true;
                await _player.PlayAsync(target, initialQueue);
                try
                {
                    var complete = await ResolveTracksForPlaybackAsync(tracks, token);
                    if (!_closed && !token.IsCancellationRequested && generation == _trackPlayIntentGeneration)
                        _player.TryExpandQueue(complete, target.Id, expectedIds);
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (!_closed && generation == _trackPlayIntentGeneration)
                        ShowError("队列加载失败", error.Message + " 当前歌曲会继续播放。");
                }
                return;
            }
            var ready = tracks is null ? await ResolveTracksForPlaybackAsync(new[] { track }, token)
                : await ResolveTracksForPlaybackAsync(tracks, token);
            if (_closed || token.IsCancellationRequested || generation != _trackPlayIntentGeneration) return;
            if (ready.FirstOrDefault(item => item.Id == track.Id) is not Track selected)
            {
                ShowToast("这首歌曲暂时不可用");
                return;
            }
            // The final intent check and PlayAsync execute on the same UI turn.
            // The player takes over stream-resolution cancellation from here.
            playbackStarted = _trackPlaybackStarted = true;
            await _player.PlayAsync(selected, tracks is null ? null : ready);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closed && generation == _trackPlayIntentGeneration) ShowError("暂时无法播放", error.Message);
        }
        finally
        {
            if (generation == _trackPlayIntentGeneration)
            {
                _trackPlayIntentRequest = null;
                _playIntentPreparing = false;
                _trackPlaybackStarted = false;
                if (!_closed) PlayAllButton.IsEnabled = _tracks.Count > 0;
            }
        }
    }

    private async void LocateCurrent_Click(object sender, RoutedEventArgs args)
    {
        if (_locatingCurrent || _player.CurrentTrack is not Track playing || _pageRequest is null) return;
        var token = _pageRequest.Token;
        var generation = _playlistPagingGeneration;
        var id = playing.Id;
        _locatingCurrent = true;
        RefreshTrackLocator();
        try
        {
            var list = _page == "artist" ? ArtistSongs : TrackList;
            if (_page == "playlist" && _playlistTrackIndex.TryGetValue(id, out var index))
                await EnsurePlaylistPageAsync(index, retry: true);
            token.ThrowIfCancellationRequested();
            if (_closed || _player.CurrentTrack?.Id != id) return;
            var target = list.Items.OfType<Track>().FirstOrDefault(track => track.Id == id && track.IsMetadataLoaded);
            if (target is null) return;
            // Metadata replacement invalidates the virtualizing panel's layout.
            // ScrollIntoView may create a recycled container before its final
            // position/ScrollViewer offset has committed; correcting that stale
            // position immediately would undo the pending jump back to zero.
            list.UpdateLayout();
            await Task.Delay(16, token);
            list.UpdateLayout();
            if (ReferenceEquals(list, TrackList)) _resetCollectionScroll = false;
            list.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            var scroller = ReferenceEquals(list, TrackList) ? _trackListScroller : _artistSongScroller;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(16, token);
                list.UpdateLayout();
                if (list.ContainerFromItem(target) is ListViewItem { ActualHeight: > 0 } realized && scroller is not null)
                {
                    var realizedY = realized.TransformToVisual(scroller).TransformPoint(new Point()).Y;
                    if (realizedY >= -realized.ActualHeight && realizedY < scroller.ViewportHeight) break;
                }
                if (attempt is 5 or 20) list.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            }
            token.ThrowIfCancellationRequested();
            if (_closed || _player.CurrentTrack?.Id != id || list.ContainerFromItem(target) is not ListViewItem container) return;
            if (scroller is not null)
            {
                var headingInset = ReferenceEquals(list, TrackList) && _collectionHeaderCompact ? CompactCollectionHeader.ActualHeight : 0;
                var y = container.TransformToVisual(scroller).TransformPoint(new Point()).Y;
                var offset = Math.Clamp(scroller.VerticalOffset + y - headingInset - 12, 0, scroller.ScrollableHeight);
                scroller.ChangeView(null, offset, null, !_motion.AnimationsEnabled);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !_closed) ShowError("暂时无法定位歌曲", error.Message); }
        finally
        {
            if (generation == _playlistPagingGeneration)
            {
                _locatingCurrent = false;
                RefreshTrackLocator();
            }
        }
    }
}
