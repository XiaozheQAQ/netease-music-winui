using System;
using System.Collections.Generic;
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
