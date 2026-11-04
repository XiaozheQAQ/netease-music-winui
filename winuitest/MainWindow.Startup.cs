using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _startupLifetime = new();
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();
    private bool _startupReady;

    private async Task InitializeStartupAsync()
    {
        var lifetime = _startupLifetime.Token;
        try
        {
            // Let the opaque, vector-only brand surface paint before starting
            // Node or performing any remote I/O. The shell stays in the layout
            // tree so its templates, bindings and images can load underneath.
            await WaitForStartupFrameAsync(lifetime);
            TraceStartup("logo-presented");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            _accountRestoreTask = RestoreAccountAsync(budget.Token);
            var restore = RestoreAccountAndPlaybackAsync(budget.Token);
            try
            {
                await Task.WhenAll(LoadHomeAsync(cancellationToken: budget.Token), restore, RestoreAudioOutputAsync(budget.Token));
                await _startupCloudSync.WaitAsync(budget.Token);
                budget.Token.ThrowIfCancellationRequested();
                Shell.UpdateLayout();
                await WaitForStartupFrameAsync(budget.Token);
                await PreloadStartupImagesAsync(budget.Token);
                TraceStartup("preload-complete");
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                // Failed/slow remote resources must never trap the listener on
                // the logo. Local library data is retained and playback retries.
                TraceStartup("preload-timeout");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (!_closed) ShowError("部分内容未能载入", error.Message + " 请刷新重试。");
            TraceStartup("preload-failed");
        }
        finally
        {
            if (!_closed) await RevealStartupShellAsync();
        }
    }

    private async Task RevealStartupShellAsync()
    {
        // Prime both visual values in the same dispatcher turn: there is no
        // intermediate frame with the shell exposed or both surfaces missing.
        ElementCompositionPreview.GetElementVisual(Shell).Opacity = 0;
        Shell.Opacity = 1;
        // The transparent parent remains an input shield during the crossfade.
        // Record the shell's normal hit-testing value in MotionCoordinator.
        Shell.IsHitTestVisible = true;
        await Task.WhenAll(
            _motion.RevealAsync(Shell, offset: 8, durationMs: 460),
            _motion.HideAsync(StartupScreen, offset: 0, durationMs: 420));
        if (_closed) return;
        StartupScreen.Stop();
        StartupLayer.Visibility = Visibility.Collapsed;
        Shell.IsHitTestVisible = true;
        _startupReady = true;
        InitializeTray();
        SetTitleBar(DragRegion);
        UpdateResponsiveLayout();
        UpdateCaptionInset();
        TraceStartup("shell-visible");
        if (_player.CurrentTrack is { } track) _ = LoadLyricsAsync(track.Id);
    }

    private static async Task WaitForStartupFrameAsync(CancellationToken token)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => frame.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        try { await frame.Task.WaitAsync(TimeSpan.FromMilliseconds(200), token); }
        // Minimized windows may not render. Startup still has to be cancellable.
        catch (TimeoutException) { }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private async Task PreloadStartupImagesAsync(CancellationToken token)
    {
        using var imageBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        imageBudget.CancelAfter(TimeSpan.FromSeconds(2));
        var bitmaps = new HashSet<BitmapImage>();
        var ready = new List<Task>();
        void Visit(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            var bitmap = node switch
            {
                Image image => image.Source as BitmapImage,
                PersonPicture person => person.ProfilePicture as BitmapImage,
                _ => null
            };
            if (bitmap is not null && bitmaps.Add(bitmap))
            {
                // Explicit sizing also enables decode while the shell is
                // transparent, rather than waiting until the reveal to decode.
                var element = (FrameworkElement)node;
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.DecodePixelWidth = (int)Math.Clamp(element.ActualWidth, 48, 512);
                ready.Add(WaitForStartupImageAsync(bitmap, imageBudget.Token));
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(Shell);
        // Images have a smaller budget than essential metadata. Failure leaves
        // the existing neutral artwork surface and can be retried by WinUI.
        try { await Task.WhenAll(ready); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { TraceStartup("artwork-deferred"); }
    }

    private static async Task WaitForStartupImageAsync(BitmapImage image, CancellationToken token)
    {
        if (image.PixelWidth > 0) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler opened = (_, _) => completion.TrySetResult();
        ExceptionRoutedEventHandler failed = (_, _) => completion.TrySetResult();
        image.ImageOpened += opened;
        image.ImageFailed += failed;
        try
        {
            if (image.PixelWidth > 0) return;
            await completion.Task.WaitAsync(token);
        }
        finally
        {
            image.ImageOpened -= opened;
            image.ImageFailed -= failed;
        }
    }

    private void TraceStartup(string phase)
    {
        // Opt-in, phase-only development trace; no account or music payloads.
        var path = Environment.GetEnvironmentVariable("LUMA_STARTUP_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, $"{_startupClock.ElapsedMilliseconds} {phase}{Environment.NewLine}"); } catch { }
    }
}
