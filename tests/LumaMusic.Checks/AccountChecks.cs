using System.Reflection;
using System.Text;
using winuitest.Models;
using winuitest.Services;

internal static class AccountChecks
{
    internal static async Task RunAsync()
    {
        VerifyMembershipLabels();
        await VerifyTypedClientAsync();
        if (OperatingSystem.IsWindows()) await VerifyProtectedStorageAsync();
    }

    private static void VerifyMembershipLabels()
    {
        foreach (var (membership, expected) in new[]
        {
            ("standard", "普通用户"), ("music-package", "音乐包"),
            ("black-vip", "黑胶 VIP"), ("black-svip", "黑胶 SVIP")
        })
            Program.Check(new AccountProfile { VipType = 11, Membership = membership }.MembershipLabel == expected,
                "Verified membership did not override a coarse login VIP type.");
        foreach (var (type, expected) in new[] { (0, "普通用户"), (10, "音乐包"), (11, "黑胶 VIP"), (99, "网易云会员") })
            Program.Check(new AccountProfile { VipType = type }.MembershipLabel == expected,
                "Basic membership fallback guessed an unsupported entitlement.");
        Console.WriteLine("PASS account membership: standard/music/black VIP/SVIP labels and conservative fallback");
    }

    private static async Task VerifyTypedClientAsync()
    {
        using var client = new NeteaseMusicClient();
        Program.Check(client.PreferredQuality == "lossless", "Default quality should request lossless with upstream permission fallback.");
        var qr = await client.CreateQrLoginAsync();
        Program.Check(qr.Key == "fixture-key" && qr.QrImage.StartsWith("data:image/png"), "QR challenge model failed.");
        var authorization = await client.CheckQrLoginAsync(qr.Key);
        Program.Check(authorization.Code == 803 && authorization.Cookie == "fixture-session", "QR status model failed.");
        var login = await client.SetSessionAsync(authorization.Cookie);
        Program.Check(login.Profile is { Id: 123, Nickname: "测试账号", VipType: 11 }, "Account profile model failed.");
        var playlists = await client.GetUserPlaylistsAsync(123);
        Program.Check(playlists.More && playlists.Playlists is [{ Id: 456, CreatorId: 123, TrackCount: 7 }], "Playlist page model failed.");
        Program.Check(playlists.Playlists[0].IsOwnedBy(123) && !playlists.Playlists[0].IsOwnedBy(9) && !new MusicPlaylist().IsOwnedBy(0), "Playlist ownership check failed closed.");
        Program.Check((await client.GetPlaylistAsync(456)).IsOwnedBy(123), "Playlist detail lost its owner.");
        var pageTracks = await client.GetPlaylistTracksAsync(new long[] { 42, 43, 42 });
        Program.Check(pageTracks.Count == 2 && pageTracks[0].Id == 42 && pageTracks[1].Id == 43
            && pageTracks.All(track => track.IsMetadataLoaded && !track.IsMetadataLoading && track.AlbumId == 90),
            "Paged metadata lost ordering, identity or legacy loaded defaults.");
        Program.Check(!(await client.GetUserPlaylistsAsync(123, 50)).More, "Playlist pagination parameters were lost.");
        var artists = await client.GetTrackArtistsAsync(42);
        Program.Check(artists is [{ Id: 7, Name: "歌手" }, { Id: 8 }], "Song artist IDs were lost.");
        var track = await client.GetTrackAsync(42);
        Program.Check(track is { Id: 42, AlbumId: 90, Fee: 8 } && track.Artists[0].Id == 7,
            "Song metadata refresh lost the original ID, album or catalogue fee.");
        var artist = await client.GetArtistAsync(7);
        Program.Check(artist is { Id: 7, Description: "介绍" } && artist.Tracks[0].Artists[0].Id == 7, "Artist page model failed.");
        Program.Check((await client.GetArtistSongsAsync(7, 0, 2)).More && !(await client.GetArtistSongsAsync(7, 2, 2)).More, "Artist song paging lost offsets.");
        var albums = await client.GetArtistAlbumsAsync(7);
        Program.Check(albums.More && albums.Albums is [{ Id: 90, PublishTime: 1234567890000, TrackCount: 3 }], "Album metadata model failed.");
        Program.Check(!(await client.GetArtistAlbumsAsync(7, 30)).More, "Album page offset was lost.");
        Program.Check((await client.GetAlbumAsync(90)).Tracks is [{ Id: 42 }], "Album songs model failed.");
        await client.AddPlaylistTracksAsync(456, new long[] { 1, 1, 2 });
        await client.RemovePlaylistTracksAsync(456, Enumerable.Range(1, 1813).Select(id => (long)id));
        var invalidRejected = false;
        try { await client.AddPlaylistTracksAsync(456, new long[] { -1 }); }
        catch (ArgumentException) { invalidRejected = true; }
        Program.Check(invalidRejected, "Invalid mutation IDs reached IPC.");
        var partialReported = false;
        try { await client.AddPlaylistTracksAsync(456, Enumerable.Range(1, 500).Select(id => (long)id).Append(9999)); }
        catch (InvalidOperationException error) { partialReported = error.Message.Contains("500") && error.Message.Contains("刷新"); }
        Program.Check(partialReported, "Partial playlist mutation did not tell the caller to refresh.");
        using (var cancellation = new CancellationTokenSource(30))
        {
            var uncertainReported = false;
            try { await client.AddPlaylistTracksAsync(456, new long[] { 8888 }, cancellation.Token); }
            catch (InvalidOperationException error) { uncertainReported = error.Message.Contains("无法确认") && error.Message.Contains("刷新"); }
            Program.Check(uncertainReported, "Canceled remote mutation was silently treated as never submitted.");
        }
        client.PreferredQuality = "hires";
        var stream = await client.ResolveTrackAsync(1);
        Program.Check(stream.Quality == "hires" && !stream.IsPreview, "Authenticated quality preference was not passed to source.");
        var lyrics = await client.GetLyricsAsync(1);
        Program.Check(lyrics is [{ Translation: "声音", Romanization: "koe" }] && lyrics[0].Words.Count == 1,
            "Multilingual per-word lyric model failed.");

        var host = (NodeMusicHost)typeof(NeteaseMusicClient).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        try { await host.RequestAsync("crash"); }
        catch (InvalidOperationException) { }
        Program.Check((await client.GetAccountStatusAsync()).Profile?.Id == 123, "Restarted source did not restore its verified session.");
        await client.LogoutAsync();
        try { await host.RequestAsync("crash"); }
        catch (InvalidOperationException) { }
        Program.Check((await client.GetAccountStatusAsync()).Profile is null, "Logout session was revived after a source restart.");
        await client.SetSessionAsync("fixture-session");
        await client.ResetSessionAsync();
        Program.Check((await client.GetAccountStatusAsync()).Profile is null, "A canceled uncommitted session was not discarded.");
        await client.SetSessionAsync("fixture-session");
        Program.Check((await client.SetSessionAsync("expired-session")).Profile is null, "Expired login was accepted.");
        try { await host.RequestAsync("crash"); }
        catch (InvalidOperationException) { }
        Program.Check((await client.GetAccountStatusAsync()).Profile is null, "An invalid session revived the prior account after restart.");
        Console.WriteLine("PASS account: typed QR/profile/playlists, ownership, batched mutations, partial failure, quality, translated lyrics, private child-session recovery, logout/invalid-session isolation");
    }

    private static async Task VerifyProtectedStorageAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LumaMusic-AccountChecks-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "account.dat");
        try
        {
            var store = new AccountSessionStore(directory);
            Program.Check(await store.LoadAsync() is null, "A missing credential should not require login recovery.");
            const string cookie = "MUSIC_U=fixture-private-cookie-123; __csrf=fixture-csrf;";
            await store.SaveAsync(cookie);
            var ciphertext = await File.ReadAllBytesAsync(path);
            Program.Check(!Encoding.UTF8.GetString(ciphertext).Contains("fixture-private-cookie"), "A credential was saved in plaintext.");
            Program.Check(await new AccountSessionStore(directory).LoadAsync() == cookie, "DPAPI current-user roundtrip failed.");
            await store.SaveAsync("MUSIC_U=replacement");
            Program.Check(await store.LoadAsync() == "MUSIC_U=replacement", "Atomic credential replacement failed.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await store.SaveAsync("MUSIC_U=cancelled", cancellation.Token); }
            catch (OperationCanceledException) { }
            Program.Check(await store.LoadAsync() == "MUSIC_U=replacement", "A cancelled save replaced the prior credential.");
            ciphertext = await File.ReadAllBytesAsync(path);
            ciphertext[ciphertext.Length / 2] ^= 0x40;
            await File.WriteAllBytesAsync(path, ciphertext);
            Program.Check(await store.LoadAsync() is null, "Tampered ciphertext was not safely rejected.");
            await store.ClearAsync();
            Program.Check(!File.Exists(path) && await store.LoadAsync() is null, "Logout did not remove the durable credential.");
            Program.Check(Directory.GetFiles(directory).Length == 0, "Credential storage left temporary files behind.");
            Console.WriteLine("PASS credentials: DPAPI encrypted roundtrip, atomic replacement, cancellation, tamper rejection, deletion");
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
