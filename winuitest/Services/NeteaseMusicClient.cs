using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using winuitest.Models;

namespace winuitest.Services;

public sealed class NeteaseMusicClient : IDisposable
{
    private readonly NodeMusicHost _host = new();
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private int _sessionVersion;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public string Status => _host.Status;
    public string Diagnostics => _host.Diagnostics;
    private string _preferredQuality = AudioQuality.Default;
    public string PreferredQuality
    {
        get => _preferredQuality;
        set => _preferredQuality = AudioQuality.Normalize(value);
    }

    public Task<HomeFeed> DiscoverAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<HomeFeed>("discover", null, cancellationToken);

    public Task<SearchResult> SearchAsync(string keywords, int offset = 0, int limit = 30, CancellationToken cancellationToken = default) =>
        RequestAsync<SearchResult>("search", new { keywords, offset, limit }, cancellationToken);

    public Task<PlaylistDetail> GetPlaylistAsync(long id, CancellationToken cancellationToken = default) =>
        RequestAsync<PlaylistDetail>("playlist", new { id }, cancellationToken);

    public Task<StreamInfo> ResolveTrackAsync(long id, CancellationToken cancellationToken = default) =>
        RequestAsync<StreamInfo>("stream", new { id, quality = PreferredQuality }, cancellationToken);

    public Task<QrLoginSession> CreateQrLoginAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<QrLoginSession>("authQrCreate", null, cancellationToken);

    public Task<QrLoginStatus> CheckQrLoginAsync(string key, CancellationToken cancellationToken = default) =>
        RequestAsync<QrLoginStatus>("authQrCheck", new { key }, cancellationToken);

    public async Task<AccountStatus> SetSessionAsync(string cookie, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _sessionVersion);
            _host.SetSessionCookie(null);
            var status = await RequestAsync<AccountStatus>("authSetSession", new { cookie }, cancellationToken).ConfigureAwait(false);
            _host.SetSessionCookie(status.Profile is { Id: > 0 } ? cookie : null);
            return status;
        }
        finally { _accountGate.Release(); }
    }

    public async Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default)
    {
        var version = Volatile.Read(ref _sessionVersion);
        var status = await RequestAsync<AccountStatus>("authStatus", null, cancellationToken).ConfigureAwait(false);
        if (status.Profile is not { Id: > 0 } && version == Volatile.Read(ref _sessionVersion))
            _host.SetSessionCookie(null);
        return status;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _sessionVersion);
            _host.SetSessionCookie(null);
            _ = await _host.RequestAsync("authLogout", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _accountGate.Release(); }
    }

    /// <summary>Discards a pending/uncommitted login without signing out an existing session on NetEase.</summary>
    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _sessionVersion);
            _host.SetSessionCookie(null);
            _ = await _host.RequestAsync("authResume", new { cookie = string.Empty }, cancellationToken).ConfigureAwait(false);
        }
        finally { _accountGate.Release(); }
    }

    public Task<UserPlaylistPage> GetUserPlaylistsAsync(long uid, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) =>
        RequestAsync<UserPlaylistPage>("userPlaylists", new { uid, offset, limit }, cancellationToken);

    public Task AddPlaylistTracksAsync(long playlistId, IEnumerable<long> trackIds, CancellationToken cancellationToken = default) =>
        MutatePlaylistTracksAsync(playlistId, trackIds, "add", cancellationToken);

    public Task RemovePlaylistTracksAsync(long playlistId, IEnumerable<long> trackIds, CancellationToken cancellationToken = default) =>
        MutatePlaylistTracksAsync(playlistId, trackIds, "del", cancellationToken);

    private async Task MutatePlaylistTracksAsync(long playlistId, IEnumerable<long> trackIds, string operation,
        CancellationToken cancellationToken)
    {
        const long largestSafeId = 9007199254740991;
        if (playlistId <= 0 || playlistId > largestSafeId) throw new ArgumentOutOfRangeException(nameof(playlistId), "歌单 ID 无效。");
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Distinct().Take(20001).ToArray();
        if (ids.Length == 0 || ids.Length > 20000 || ids.Any(id => id <= 0 || id > largestSafeId))
            throw new ArgumentException("请选择 1–20000 首有效歌曲。", nameof(trackIds));

        var version = Volatile.Read(ref _sessionVersion);
        var completed = 0;
        var requestStarted = false;
        try
        {
            // Keep the pipe request below its 64 KiB bound. Each write is sent once;
            // retrying a timed-out mutation could repeat a successful remote edit.
            foreach (var batch in ids.Chunk(500))
            {
                await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (version != Volatile.Read(ref _sessionVersion))
                        throw new InvalidOperationException("登录账号已改变，请重新选择歌单。");
                    requestStarted = true;
                    var response = await _host.RequestAsync("playlistTracks", new { id = playlistId, operation, trackIds = batch }, cancellationToken).ConfigureAwait(false);
                    if (!response.TryGetProperty("count", out var count) || !count.TryGetInt32(out var changed) || changed != batch.Length)
                        throw new InvalidOperationException("歌单操作未得到完整确认，请刷新歌单检查结果。");
                    completed += batch.Length;
                }
                finally { _accountGate.Release(); }
            }
        }
        catch (Exception error) when (completed > 0)
        {
            throw new InvalidOperationException($"已完成前 {completed} 首，后续操作未完成。请刷新歌单确认结果后再操作。{error.Message}", error);
        }
        catch (Exception error) when (requestStarted && error is OperationCanceledException or TimeoutException)
        {
            throw new InvalidOperationException("请求已中断，无法确认歌单是否修改成功。请刷新歌单确认结果后再操作。", error);
        }
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await _host.RequestAsync("lyrics", new { id }, cancellationToken).ConfigureAwait(false);
        return response.GetProperty("lines").Deserialize<List<LyricLine>>(JsonOptions) ?? new List<LyricLine>();
    }

    private async Task<T> RequestAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var response = await _host.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return response.Deserialize<T>(JsonOptions) ?? throw new InvalidOperationException("音源没有返回有效数据。");
    }

    public void Dispose() => _host.Dispose();
}
