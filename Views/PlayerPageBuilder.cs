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

    public static string Build(string title, string videoUrl, string type, string hlsScript)
    {
        var encodedUrl = JsonSerializer.Serialize(videoUrl);
        var encodedTitle = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(title) ? "播放" : title);
        var isHls = IsHls(type, videoUrl);
        var mimeType = isHls ? "application/vnd.apple.mpegurl" : "video/mp4";
        var hlsPlayer = BuildPlayerScript(isHls, encodedUrl, mimeType, hlsScript);

        var html = new StringBuilder(4096 + (isHls ? hlsScript.Length : 0));
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head>");
        html.AppendLine("<meta charset=\"utf-8\" />");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("<title>").Append(encodedTitle).AppendLine("</title>");
        html.AppendLine("<style>html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #000; overflow: hidden; } video { width: 100%; height: 100%; background: #000; } #error { position: fixed; left: 24px; right: 24px; bottom: 24px; padding: 12px 16px; color: #fff; background: rgba(128, 24, 24, .9); font: 14px sans-serif; border-radius: 6px; }</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<video id=\"video\" controls autoplay playsinline></video>");
        html.AppendLine("<div id=\"error\" hidden></div>");
        html.AppendLine("<script>");
        html.AppendLine("const video = document.getElementById('video');");
        html.AppendLine("const error = document.getElementById('error');");
        html.AppendLine("const showError = (message) => { error.textContent = message; error.hidden = false; };");
        html.AppendLine("try {");
        html.AppendLine(hlsPlayer);
        html.AppendLine("} catch (exception) {");
        html.AppendLine("showError(exception && exception.message ? exception.message : '播放器初始化失败');");
        html.AppendLine("}");
        html.AppendLine("window.addEventListener('beforeunload', () => { try { if (window.__hanimeHls) window.__hanimeHls.destroy(); } catch {} });");
        html.AppendLine("</script></body></html>");
        return html.ToString();
    }

    private static string BuildPlayerScript(bool isHls, string encodedUrl, string mimeType, string hlsScript)
    {
        if (!isHls)
        {
            return "video.src = " + encodedUrl + "; video.type = " + JsonSerializer.Serialize(mimeType) + "; video.play().catch(() => {});";
        }

        var script = new StringBuilder(1024 + hlsScript.Length);
        script.Append("const hlsUrl = ").Append(encodedUrl).AppendLine(";");
        if (!string.IsNullOrWhiteSpace(hlsScript))
        {
            script.Append("window.eval(").Append(JsonSerializer.Serialize(hlsScript)).AppendLine(");");
        }
        script.AppendLine("if (video.canPlayType('application/vnd.apple.mpegurl') || video.canPlayType('application/x-mpegURL')) {");
        script.AppendLine("    video.src = hlsUrl;");
        script.AppendLine("    video.play().catch(() => {});");
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
        script.AppendLine("    showError('当前 WebView2 不支持 HLS/M3U8 播放。');");
        script.AppendLine("}");
        return script.ToString();
    }
}
