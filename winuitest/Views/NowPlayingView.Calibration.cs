using Microsoft.UI.Xaml;
using winuitest.Services;

namespace winuitest.Views;

public sealed partial class NowPlayingView
{
    private void UpdateCorrectionControls()
    {
        CorrectionValue.Text = _manualLyricOffsetMs == 0 ? "0.0 s" : $"{_manualLyricOffsetMs / 1000d:+0.0;-0.0} s";
        EarlierLyricsButton.IsEnabled = _hasTrack && _manualLyricOffsetMs > LyricTiming.MinimumOffsetMs;
        LaterLyricsButton.IsEnabled = _hasTrack && _manualLyricOffsetMs < LyricTiming.MaximumOffsetMs;
        ResetLyricsButton.IsEnabled = _hasTrack && _manualLyricOffsetMs != 0;
    }

    private void EarlierLyrics_Click(object sender, RoutedEventArgs e) =>
        LyricCorrectionRequested?.Invoke(this, _manualLyricOffsetMs - LyricTiming.StepMs);

    private void LaterLyrics_Click(object sender, RoutedEventArgs e) =>
        LyricCorrectionRequested?.Invoke(this, _manualLyricOffsetMs + LyricTiming.StepMs);

    private void ResetLyrics_Click(object sender, RoutedEventArgs e) =>
        LyricCorrectionRequested?.Invoke(this, 0);
}
