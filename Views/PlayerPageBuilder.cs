using System.Text;
using System.Text.Json;

namespace Hanime1Downloader.CSharp.Views;

public static class PlayerPageBuilder
{
    public static bool IsHls(string? type, string? url)
    {
        return (!string.IsNullOrWhiteSpace(type) && type.Contains("m3u8", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
    }

    public static string Build(string title, string videoUrl, string type, double? restoredPosition = null, double? restoredVolume = null)
    {
        var encodedUrl = JsonSerializer.Serialize(videoUrl);
        var encodedTitle = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(title) ? "播放" : title);
        var isHls = IsHls(type, videoUrl);
        var mimeType = isHls ? "application/vnd.apple.mpegurl" : "video/mp4";
        var hlsPlayer = BuildPlayerScript(isHls, encodedUrl, mimeType);

        var html = new StringBuilder(4096);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head>");
        html.AppendLine("<meta charset=\"utf-8\" />");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("<title>").Append(encodedTitle).AppendLine("</title>");
        html.AppendLine("<style>html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #000; overflow: hidden; } video { width: 100%; height: 100%; background: #000; }</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<video id=\"video\" controls autoplay playsinline></video>");
        html.AppendLine("<script>");
        html.AppendLine("const video = document.getElementById('video');");
        // 播放失败不再显示遮挡画面的红色横幅，仅在控制台留一条记录（无可见 UI）。
        html.AppendLine("const showError = (message) => { try { console.warn('[player] ' + message); } catch (e) {} };");
        if (restoredPosition is > 3)
        {
            // 恢复上次播放位置：loadedmetadata 后 seek。
            html.Append("const restoredPosition = ").Append(restoredPosition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(";");
            html.AppendLine("video.addEventListener('loadedmetadata', () => { if (restoredPosition > 0 && restoredPosition < video.duration - 3) { try { video.currentTime = restoredPosition; } catch {} } }, { once: true });");
        }
        if (restoredVolume is double vol && vol >= 0)
        {
            html.Append("video.volume = ").Append(vol.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(";");
        }
        html.AppendLine("try {");
        html.AppendLine(hlsPlayer);
        html.AppendLine("} catch (exception) {");
        html.AppendLine("showError(exception && exception.message ? exception.message : '播放器初始化失败');");
        html.AppendLine("}");
        html.AppendLine("window.addEventListener('beforeunload', () => { try { if (window.__hanimeHls) window.__hanimeHls.destroy(); } catch {} });");
        html.AppendLine("</script></body></html>");
        return html.ToString();
    }

    private static string BuildPlayerScript(bool isHls, string encodedUrl, string mimeType)
    {
        if (!isHls)
        {
            return
                "video.src = " + encodedUrl + "; video.type = " + JsonSerializer.Serialize(mimeType) + ";" +
                "video.addEventListener('error', () => showError('视频加载失败: ' + (video.error ? (video.error.code + ' ' + (video.error.message || '')) : '未知错误')));" +
                "video.play().catch(() => showError('自动播放被阻止，请点击播放按钮。'));";
        }

        var script = new StringBuilder(1024);
        script.Append("const hlsUrl = ").Append(encodedUrl).AppendLine(";");
        // hls.min.js 已由 PlayerWindow 通过 AddScriptToExecuteOnDocumentCreatedAsync 注入，这里直接用 window.Hls。
        script.AppendLine("if (video.canPlayType('application/vnd.apple.mpegurl') || video.canPlayType('application/x-mpegURL')) {");
        script.AppendLine("    video.src = hlsUrl;");
        script.AppendLine("    video.addEventListener('error', () => showError('视频加载失败: ' + (video.error ? video.error.code : '未知错误')));");
        script.AppendLine("    video.play().catch(() => showError('自动播放被阻止，请点击播放按钮。'));");
        script.AppendLine("} else if (window.Hls && window.Hls.isSupported()) {");
        script.AppendLine("    const hls = new window.Hls({ enableWorker: true, lowLatencyMode: false, backBufferLength: 30 });");
        script.AppendLine("    window.__hanimeHls = hls;");
        script.AppendLine("    hls.on(window.Hls.Events.ERROR, (_, data) => {");
        script.AppendLine("        if (data && data.fatal) {");
        script.AppendLine("            showError('HLS 播放失败: ' + (data.details || data.type || '未知错误'));");
        script.AppendLine("            hls.destroy();");
        script.AppendLine("        }");
        script.AppendLine("    });");
        script.AppendLine("    hls.loadSource(hlsUrl);");
        script.AppendLine("    hls.attachMedia(video);");
        script.AppendLine("} else {");
        script.AppendLine("    showError('播放器脚本加载失败或当前环境不支持 HLS 播放。');");
        script.AppendLine("}");
        return script.ToString();
    }
}
