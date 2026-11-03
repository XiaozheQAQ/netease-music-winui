using System.Reflection;
using System.Text;
using winuitest.Models;
using winuitest.Services;

internal static class AccountChecks
{
    internal static async Task RunAsync()
    {
        await VerifyTypedClientAsync();
        if (OperatingSystem.IsWindows()) await VerifyProtectedStorageAsync();
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
        Program.Check(playlists.More && playlists.Playlists is [{ Id: 456, TrackCount: 7 }], "Playlist page model failed.");
        Program.Check(!(await client.GetUserPlaylistsAsync(123, 50)).More, "Playlist pagination parameters were lost.");
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
        Console.WriteLine("PASS account: typed QR/profile/playlists, quality, translated lyrics, private child-session recovery, logout/invalid-session isolation");
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
