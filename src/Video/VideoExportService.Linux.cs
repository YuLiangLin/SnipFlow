using System.Diagnostics;
using System.Globalization;
using System.Text;
using SnipFlow.Services;

namespace SnipFlow.Video;

internal static partial class VideoExportService
{
    internal static async partial Task<string> ExportAsync(VideoEditDocument document, string destination, bool overwrite,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var snapshot = document.CreateSnapshot();
        snapshot.Validate();
        var ffmpeg = ExternalTool.Find("ffmpeg")
            ?? throw new InvalidOperationException(I18n.T("匯出影片需要 ffmpeg。請安裝：sudo apt install ffmpeg"));
        var output = VideoFileGuard.ValidateOutput(destination, snapshot.Source.SourcePath, ".mp4", overwrite);
        var partial = VideoFileGuard.CreatePartial(output, ".mp4");
        ExportWorkspace? workspace = null;
        var published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspace = new ExportWorkspace();
            var source = VideoFileGuard.ResolveFile(snapshot.Source.SourcePath);
            var probe = await VideoMetadata.ProbeAsync(source, cancellationToken);
            if (probe.Info.Width != snapshot.Source.Width || probe.Info.Height != snapshot.Source.Height
                || Math.Abs((probe.Info.Duration - snapshot.Source.Duration).TotalMilliseconds) > 100)
                throw new InvalidOperationException(I18n.T("原始影片已變更，無法套用這份編輯專案。"));

            // Input seeking re-bases the kept range to t = 0; overlay times are relative to TrimStart.
            // "file:" keeps names that contain ':' from being read as a protocol.
            var arguments = new List<string>
            {
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y",
                "-ss", Seconds(snapshot.TrimStart), "-t", Seconds(snapshot.ExportDuration), "-i", "file:" + source
            };
            var filters = new List<string>();
            var label = "0:v";
            var input = 1;
            foreach (var item in snapshot.Overlays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Kind == VideoOverlayKind.Text && string.IsNullOrWhiteSpace(item.Text)) continue;
                var start = item.ShowEntireVideo ? snapshot.TrimStart : Max(item.Start, snapshot.TrimStart);
                var end = item.ShowEntireVideo ? snapshot.TrimEnd : Min(item.End, snapshot.TrimEnd);
                if (end <= start) continue;
                var png = workspace.CreateFile(".png");
                // Rendered on the UI dispatcher with the preview's layout code.
                using (var image = VideoOverlayRenderer.Render(item)) ImageTools.SavePng(image, png);
                arguments.AddRange(["-i", "file:" + png]);
                // [start, end) like the editor preview; a still image repeats for the whole range.
                filters.Add($"[{label}][{input}:v]overlay=x={Pixels(item.X)}:y={Pixels(item.Y)}:eof_action=repeat"
                    + $":enable='gte(t,{Seconds(start - snapshot.TrimStart)})*lt(t,{Seconds(end - snapshot.TrimStart)})'[v{input}]");
                label = "v" + input.ToString(CultureInfo.InvariantCulture);
                input++;
            }
            // H.264 4:2:0 needs even dimensions; drop the odd last row or column instead of resampling.
            filters.Add($"[{label}]crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0,format=yuv420p[video]");
            arguments.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[video]"]);
            arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"]);
            if (probe.HasAudio)
            {
                arguments.AddRange(["-map", "0:a:0", "-c:a", "aac", "-b:a", "192k"]);
                if (probe.AudioChannels > 2) arguments.AddRange(["-ac", "2"]);
            }
            else arguments.Add("-an");
            arguments.AddRange(["-sn", "-dn", "-movflags", "+faststart", "-f", "mp4", "file:" + partial]);

            await RunFfmpegAsync(ffmpeg, arguments, snapshot.ExportDuration, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(partial).Length < 128)
                throw new InvalidOperationException(I18n.T("影片匯出未產生完整檔案。"));
            VideoFileGuard.ValidateOutput(output, snapshot.Source.SourcePath, ".mp4", overwrite);
            File.Move(partial, output, overwrite);
            published = true;
            progress?.Report(100);
            return output;
        }
        finally
        {
            // ffmpeg has exited before this point, so only this job's owned files are removed.
            workspace?.Dispose();
            if (!published) VideoFileGuard.DeleteOwnedFile(partial);
        }
    }

    /// <summary>Runs ffmpeg, reporting "-progress" output; cancellation kills it and waits for the exit.</summary>
    private static async Task RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> arguments, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = ExternalTool.CreateStartInfo(ffmpeg, arguments) };
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var errors = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            // Keep only the tail; a damaged input can print many lines.
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                errors.AppendLine(line);
                if (errors.Length > 16384) errors.Remove(0, errors.Length - 8192);
            }
        }, CancellationToken.None);
        var total = Math.Max(1, duration.TotalMilliseconds * 1000);
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line.AsSpan(0, separator);
            if ((key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
                && long.TryParse(line.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                && microseconds >= 0 && !cancellationToken.IsCancellationRequested)
                progress?.Report(Math.Clamp(microseconds / total * 100, 0, 99.5));
        }
        await process.WaitForExitAsync(CancellationToken.None);
        await errorTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(I18n.F("影片匯出失敗：{0}", VideoMetadata.ErrorSummary(errors.ToString()) is { Length: > 0 } message
                ? message : process.ExitCode.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Seconds(TimeSpan value) => Math.Max(0, value.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture);
    private static string Pixels(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
