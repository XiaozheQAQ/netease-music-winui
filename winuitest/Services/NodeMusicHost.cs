using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace winuitest.Services;

/// <summary>A single private Node process; JSON-lines IPC never opens a listening port.</summary>
public sealed class NodeMusicHost : IDisposable
{
    private sealed record Pending(int Generation, TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly object _processLock = new();
    private Process? _process;
    private long _nextId;
    private int _generation;
    private int _bootstrappedGeneration;
    private string? _sessionCookie;
    private volatile bool _disposed;

    public string Status
    {
        get
        {
            lock (_processLock)
                return !_disposed && _process is { HasExited: false } ? "网易云音源已连接 · stdio" : "网易云音源待连接";
        }
    }
    public string Diagnostics => string.Join(Environment.NewLine, _diagnostics.ToArray());

    /// <summary>Keeps the verified session in memory so a restarted child can resume it via private stdin.</summary>
    public void SetSessionCookie(string? cookie)
    {
        lock (_processLock)
            Interlocked.Exchange(ref _sessionCookie, _disposed ? null : cookie);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var process = EnsureStarted();
                if (_bootstrappedGeneration != _generation)
                {
                    var cookie = Volatile.Read(ref _sessionCookie);
                    if (!string.IsNullOrWhiteSpace(cookie))
                    {
                        // Reserved id 0 has no pending caller. Ordered before every normal request
                        // to this generation; credentials never enter command arguments or environment.
                        var resume = JsonSerializer.Serialize(new { id = 0, method = "authResume", @params = new { cookie } });
                        await process.StandardInput.WriteLineAsync(resume.AsMemory(), timeout.Token).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                    }
                    _bootstrappedGeneration = _generation;
                }
                _pending[id] = new Pending(_generation, completion);
                var line = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
                await process.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接网易云音乐超时，请检查网络后重试。");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException("找不到 Node.js 运行时，请先运行 scripts/prepare-source.ps1。", error);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Process EnsureStarted()
    {
        lock (_processLock) return StartProcessCore();
    }

    private Process StartProcessCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }) return _process;
        _process?.Dispose();
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "MusicSource");
        var script = Path.Combine(sourceDirectory, "service.cjs");
        if (!File.Exists(script)) throw new InvalidOperationException("未找到音源服务文件，请重新生成应用。");
        if (!Directory.Exists(Path.Combine(sourceDirectory, "node_modules", "@neteasecloudmusicapienhanced", "api")))
            throw new InvalidOperationException("音源依赖尚未准备好，请先运行 scripts/prepare-source.ps1，再重新生成应用。");
        var embedded = Path.Combine(sourceDirectory, "runtime", "node.exe");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(embedded) ? embedded : "node.exe",
            WorkingDirectory = sourceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(script);
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaMusic", "SourceCache");
        Directory.CreateDirectory(cacheDirectory);
        start.Environment["TEMP"] = cacheDirectory;
        start.Environment["TMP"] = cacheDirectory;
        start.Environment["ENABLE_GENERAL_UNBLOCK"] = "false";
        start.Environment["DOTENV_CONFIG_QUIET"] = "true";
        var generation = ++_generation;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => FailPending(generation, new InvalidOperationException("网易云音源进程已退出，请重试。若持续失败，请重新准备音源依赖。"));
        process.Start();
        _process = process;
        _ = ReadResponsesAsync(process, generation);
        _ = ReadDiagnosticsAsync(process);
        return process;
    }

    private async Task ReadResponsesAsync(Process process, int generation)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id)) continue;
                    if (!_pending.TryGetValue(id, out var pending) || pending.Generation != generation) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) &&
                            text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                        pending.Completion.TrySetException(new InvalidOperationException(message ?? "音源请求失败。"));
                    }
                    else if (root.TryGetProperty("result", out var result)) pending.Completion.TrySetResult(result.Clone());
                    else pending.Completion.TrySetException(new InvalidOperationException("音源返回了无效的协议响应。"));
                }
                catch (JsonException) { AddDiagnostic("已忽略音源的非协议输出。"); }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AddDiagnostic("音源通信已结束。");
        }
        finally
        {
            FailPending(generation, new InvalidOperationException("网易云音源连接已断开，请重试。"));
        }
    }

    private async Task ReadDiagnosticsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) AddDiagnostic(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void AddDiagnostic(string value)
    {
        // Keep a bounded in-memory log; cookies and raw upstream responses are never persisted.
        _diagnostics.Enqueue(value.Length > 500 ? value[..500] : value);
        while (_diagnostics.Count > 30) _diagnostics.TryDequeue(out _);
    }

    private void FailPending(int generation, Exception error)
    {
        foreach (var pending in _pending.Values)
            if (pending.Generation == generation) pending.Completion.TrySetException(error);
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
            _sessionCookie = null;
            foreach (var pending in _pending.Values) pending.Completion.TrySetException(new ObjectDisposedException(nameof(NodeMusicHost)));
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.StandardInput.Close();
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
            _process?.Dispose();
        }
    }
}
