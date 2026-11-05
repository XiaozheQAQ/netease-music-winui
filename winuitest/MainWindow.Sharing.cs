using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using winuitest.Models;
using winuitest.Services;

namespace winuitest;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _trackLinkCopy;

    private void CancelTrackLinkCopy() => _trackLinkCopy?.Cancel();

    private async Task CopyTrackLinksAsync(IEnumerable<Track> tracks)
    {
        if (_closed) return;
        CancelTrackLinkCopy();
        using var request = new CancellationTokenSource();
        _trackLinkCopy = request;
        try
        {
            // playlistPage reads song_detail by ID and returns the corresponding
            // catalogue fee and privilege, including for old saved/virtual rows.
            var links = await TrackShareLink.ResolveAsync(tracks, _client.GetPlaylistTracksAsync, request.Token);
            if (links.Count == 0 || _closed || request.IsCancellationRequested || !ReferenceEquals(request, _trackLinkCopy)) return;
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(string.Join(Environment.NewLine, links.Select(link => link.Address)));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            var audio = links.Count(link => link.IsAudio);
            ShowToast(links.Count == 1 ? audio == 1 ? "已复制公开音频地址" : "已复制网易云歌曲页面"
                : $"已复制 {audio} 个音频地址、{links.Count - audio} 个歌曲页面");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closed && !request.IsCancellationRequested && ReferenceEquals(request, _trackLinkCopy))
                ShowError("无法复制地址", error.Message);
        }
        finally
        {
            if (ReferenceEquals(request, _trackLinkCopy)) _trackLinkCopy = null;
        }
    }
}
