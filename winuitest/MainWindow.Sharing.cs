using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using winuitest.Models;
using winuitest.Services;

namespace winuitest;

public sealed partial class MainWindow
{
    private void CopyTrackLinks(IEnumerable<Track> tracks)
    {
        if (_closed) return;
        var selection = tracks.Where(track => track.Id > 0).DistinctBy(track => track.Id).ToArray();
        if (selection.Length == 0) return;
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(string.Join(Environment.NewLine, selection.Select(TrackShareLink.Create)));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowToast(selection.Length == 1 ? "已复制播放地址" : $"已复制 {selection.Length} 首歌曲地址");
        }
        catch (Exception error) { ShowError("无法复制地址", error.Message); }
    }
}
