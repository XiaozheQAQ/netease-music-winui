namespace winuitest;

public sealed partial class MainWindow
{
    private void RefreshThemeDependentVisuals()
    {
        // These foregrounds are assigned in code to distinguish the playing
        // track from selection. Unlike ThemeResource bindings they keep the
        // old palette until explicitly refreshed, even if the song did not change.
        // Only realized XAML rows are visited, never the entire music library.
        UpdatePlayingTrackIndicators();
        UpdateTrackHearts(QueueList);
        UpdateTrackHearts(ArtistSongs);
    }
}
