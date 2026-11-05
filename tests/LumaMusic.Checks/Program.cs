using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using winuitest.Services;

internal static class Program
{
    private static readonly string SourceDirectory = Path.Combine(AppContext.BaseDirectory, "MusicSource");
    private static readonly string ScriptPath = Path.Combine(SourceDirectory, "service.cjs");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--orphan-child")
            {
                using var host = new NodeMusicHost();
                await host.RequestAsync("health");
                // Atomic rename lets the parent read the complete PID before terminating us.
                await File.WriteAllTextAsync(args[1] + ".tmp", ChildOf(host).Id.ToString());
                File.Move(args[1] + ".tmp", args[1], overwrite: true);
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            if (args.Any(argument => argument != "--live")) throw new ArgumentException("Usage: dotnet run --project tests/LumaMusic.Checks [-- --live]");

            PrepareFixture();
            await VerifyOfflineHostAsync();
            await VerifyParentTerminationAsync();
            await VerifyStartupDisposalAsync();
            LibraryChecks.Run();
            await TrackShareLinkChecks.RunAsync();
            LyricTimingChecks.Run();
            LyricGapChecks.Run();
            LyricLayoutChecks.Run();
            await AccountChecks.RunAsync();
            if (args.Contains("--live")) await VerifyLiveSourceAsync();
            Console.WriteLine(args.Contains("--live") ? "ALL OFFLINE AND LIVE CHECKS PASSED" : "ALL OFFLINE CHECKS PASSED (use --live for NetEase search)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void PrepareFixture()
    {
        Directory.CreateDirectory(Path.Combine(SourceDirectory, "node_modules", "@neteasecloudmusicapienhanced", "api"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-source.cjs"), ScriptPath, overwrite: true);
        // Reuse a prepared runtime if available; otherwise NodeMusicHost uses node.exe from PATH.
        var embedded = Path.Combine(FindRepositoryRoot(), "winuitest", "MusicSource", "runtime", "node.exe");
        if (File.Exists(embedded))
        {
            var runtime = Path.Combine(SourceDirectory, "runtime");
            Directory.CreateDirectory(runtime);
            File.Copy(embedded, Path.Combine(runtime, "node.exe"), overwrite: true);
        }
    }

    private static async Task VerifyOfflineHostAsync()
    {
        using var host = new NodeMusicHost();
        var replies = await Task.WhenAll(Enumerable.Range(1, 8).Select(value => host.RequestAsync("health", new { value })));
        for (var index = 0; index < replies.Length; index++)
            Check(replies[index].GetProperty("echo").GetInt32() == index + 1, "Concurrent responses crossed request IDs.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await MustCancelAsync(host.RequestAsync("health", cancellationToken: cancelled.Token));
        using var cancelLater = new CancellationTokenSource(25);
        await MustCancelAsync(host.RequestAsync("slow", cancellationToken: cancelLater.Token));
        Check((await host.RequestAsync("health")).GetProperty("ok").GetBoolean(), "Cancellation stopped the response reader.");
        await Task.Delay(350);
        Check((await host.RequestAsync("health")).GetProperty("ok").GetBoolean(), "Late cancelled responses broke later requests.");
        await MustFailAsync<InvalidOperationException>(host.RequestAsync("malformed"));
        Check((await host.RequestAsync("health")).GetProperty("ok").GetBoolean(), "Malformed response stopped the response reader.");

        var previousId = ChildOf(host).Id;
        var pendingDuringCrash = host.RequestAsync("slow");
        await MustFailAsync<InvalidOperationException>(host.RequestAsync("crash"));
        await MustFailAsync<InvalidOperationException>(pendingDuringCrash);
        Check((await host.RequestAsync("health")).GetProperty("ok").GetBoolean(), "Source did not recover after a crash.");
        Check(ChildOf(host).Id != previousId, "Source process was not restarted.");

        using var child = Process.GetProcessById(ChildOf(host).Id);
        var pendingDuringDispose = host.RequestAsync("slow");
        host.Dispose();
        await MustFailAsync<ObjectDisposedException>(pendingDuringDispose);
        await ExitsAsync(child);
        await MustFailAsync<ObjectDisposedException>(host.RequestAsync("health"));
        Console.WriteLine("PASS host: concurrent IPC, noisy/malformed protocol, cancellation/late replies, crash recovery, dispose cleanup");
    }

    private static async Task VerifyParentTerminationAsync()
    {
        var pidFile = Path.Combine(AppContext.BaseDirectory, "orphan-" + Guid.NewGuid().ToString("N") + ".pid");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        // Also support invoking this assembly directly with `dotnet LumaMusic.Checks.dll`.
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--orphan-child");
        start.ArgumentList.Add(pidFile);
        using var parent = Process.Start(start) ?? throw new InvalidOperationException("Could not start lifecycle fixture.");
        try
        {
            using var timeout = new CancellationTokenSource(5000);
            while (!File.Exists(pidFile))
            {
                Check(!parent.HasExited, "Parent fixture exited before writing its child PID.");
                await Task.Delay(20, timeout.Token);
            }
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
            parent.Kill(entireProcessTree: false);
            await ExitsAsync(parent);
            await ExitsAsync(child);
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            File.Delete(pidFile);
            File.Delete(pidFile + ".tmp");
        }
        Console.WriteLine("PASS lifecycle: abrupt parent termination leaves no Node orphan");
    }

    private static async Task VerifyStartupDisposalAsync()
    {
        for (var index = 0; index < 12; index++)
        {
            using var host = new NodeMusicHost();
            var call = Task.Run(async () =>
            {
                try { await host.RequestAsync("health"); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            await Task.Run(host.Dispose);
            await call;
        }
        Console.WriteLine("PASS lifecycle: 12 startup/disposal races");
    }

    private static async Task VerifyLiveSourceAsync()
    {
        var source = Path.Combine(FindRepositoryRoot(), "winuitest", "MusicSource");
        Check(Directory.Exists(Path.Combine(source, "node_modules", "@neteasecloudmusicapienhanced", "api")), "Run scripts/prepare-source.ps1 before --live.");
        await File.WriteAllTextAsync(ScriptPath, $"require({JsonSerializer.Serialize(Path.Combine(source, "service.cjs"))});\n");
        try
        {
            using var host = new NodeMusicHost();
            using var client = new NeteaseMusicClient();
            var health = host.RequestAsync("health");
            var search = client.SearchAsync("陈奕迅", limit: 3);
            await Task.WhenAll(health, search);
            Check(health.Result.GetProperty("transport").GetString() == "stdio", "Unexpected live transport.");
            Check(search.Result.Total > 0 && search.Result.Tracks.Count > 0, "Live search returned no tracks.");
            Check(search.Result.Tracks.All(track => track.Id > 0 && track.Title.Length > 0), "Live track model deserialization failed.");
            Console.WriteLine($"PASS live: C# client search returned {search.Result.Total} results and valid track models");
            var lyrics = await client.GetLyricsAsync(65766);
            var timed = lyrics.Where(line => line.Words.Count > 0).ToList();
            Check(timed.Count > 0, "The known YRC sample did not return per-word timing.");
            Check(timed.All(line => line.DurationMs > 0 && line.Words.All(word => word.StartMs >= 0 && word.DurationMs >= 0)), "YRC model timing deserialization failed.");
            Console.WriteLine($"PASS live: YRC decoded {timed.Count} timed lines and {timed.Sum(line => line.Words.Count)} supplied word segments");
        }
        finally
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-source.cjs"), ScriptPath, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "winuitest.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Run these checks from the Luma Music repository.");
    }

    private static Process ChildOf(NodeMusicHost host) =>
        (Process)typeof(NodeMusicHost).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static async Task ExitsAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(5000);
        await process.WaitForExitAsync(timeout.Token);
    }

    private static async Task MustCancelAsync(Task task) => await MustFailAsync<OperationCanceledException>(task);

    private static async Task MustFailAsync<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, but the request succeeded.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
