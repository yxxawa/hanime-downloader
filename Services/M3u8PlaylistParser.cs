using System.IO;
using System.Globalization;

namespace Hanime1Downloader.CSharp.Services;

public static class M3u8PlaylistParser
{
    public static M3u8Playlist Parse(string content, Uri playlistUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(playlistUri);

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var variants = new List<M3u8Variant>();
        var segments = new List<M3u8Segment>();
        M3u8VariantAttributes? pendingVariant = null;
        double pendingDuration = 0;
        M3u8ByteRange? pendingByteRange = null;
        M3u8ByteRange? previousByteRange = null;
        M3u8Key? currentKey = null;
        M3u8InitSegment? initSegment = null;
        long mediaSequence = 0;
        long segmentIndex = mediaSequence;
        var endList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingVariant = ParseVariantAttributes(ParseAttributes(line[18..]));
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(line[22..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSequence))
                {
                    mediaSequence = parsedSequence;
                    segmentIndex = parsedSequence;
                }
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var durationText = line[8..].Split(',', 2)[0].Trim();
                _ = double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                pendingByteRange = ParseByteRange(line[17..].Trim(), previousByteRange);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                currentKey = ParseKey(ParseAttributes(line[11..]), playlistUri);
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributes(line[11..]);
                if (attributes.TryGetValue("URI", out var mapUri) && TryResolveUri(playlistUri, mapUri, out var resolvedMapUri))
                {
                    M3u8ByteRange? mapRange = null;
                    if (attributes.TryGetValue("BYTERANGE", out var rawRange))
                    {
                        mapRange = ParseByteRange(rawRange, null);
                    }
                    initSegment = new M3u8InitSegment(resolvedMapUri, mapRange, currentKey);
                }
                continue;
            }

            if (line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                endList = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (pendingVariant is not null)
            {
                if (TryResolveUri(playlistUri, line, out var variantUri))
                {
                    variants.Add(new M3u8Variant(
                        variantUri,
                        pendingVariant.Bandwidth,
                        pendingVariant.Width,
                        pendingVariant.Height,
                        pendingVariant.Codecs));
                }
                pendingVariant = null;
                continue;
            }

            if (TryResolveUri(playlistUri, line, out var segmentUri))
            {
                segments.Add(new M3u8Segment(segmentUri, pendingDuration, segmentIndex++, pendingByteRange, currentKey));
                previousByteRange = pendingByteRange;
                pendingDuration = 0;
                pendingByteRange = null;
            }
        }

        if (variants.Count == 0 && segments.Count == 0)
        {
            throw new InvalidDataException("M3U8 播放列表没有可下载的变体或分片。");
        }

        return new M3u8Playlist(variants, segments, initSegment, endList, mediaSequence);
    }

    public static M3u8Variant SelectBestVariant(IEnumerable<M3u8Variant> variants)
    {
        return variants
            .OrderByDescending(variant => variant.Bandwidth ?? 0)
            .ThenByDescending(variant => (variant.Width ?? 0) * (variant.Height ?? 0))
            .First();
    }

    private static M3u8VariantAttributes ParseVariantAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        long? bandwidth = null;
        if (attributes.TryGetValue("BANDWIDTH", out var bandwidthText) && long.TryParse(bandwidthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBandwidth))
        {
            bandwidth = parsedBandwidth;
        }

        int? width = null;
        int? height = null;
        if (attributes.TryGetValue("RESOLUTION", out var resolution))
        {
            var dimensions = resolution.Split('x', 2);
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth) && int.TryParse(dimensions[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight))
            {
                width = parsedWidth;
                height = parsedHeight;
            }
        }

        attributes.TryGetValue("CODECS", out var codecs);
        return new M3u8VariantAttributes(bandwidth, width, height, codecs);
    }

    private static M3u8Key? ParseKey(IReadOnlyDictionary<string, string> attributes, Uri playlistUri)
    {
        if (!attributes.TryGetValue("METHOD", out var method) || method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Uri? keyUri = null;
        if (attributes.TryGetValue("URI", out var rawUri) && TryResolveUri(playlistUri, rawUri, out var resolvedUri))
        {
            keyUri = resolvedUri;
        }

        byte[]? iv = null;
        if (attributes.TryGetValue("IV", out var rawIv))
        {
            iv = ParseIv(rawIv);
        }

        return new M3u8Key(method, keyUri, iv);
    }

    private static byte[] ParseIv(string rawIv)
    {
        var hex = rawIv.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }
        if (hex.Length % 2 != 0)
        {
            hex = "0" + hex;
        }

        var parsed = Convert.FromHexString(hex);
        var result = new byte[16];
        Buffer.BlockCopy(parsed, Math.Max(0, parsed.Length - 16), result, Math.Max(0, 16 - parsed.Length), Math.Min(16, parsed.Length));
        return result;
    }

    private static M3u8ByteRange ParseByteRange(string raw, M3u8ByteRange? previous)
    {
        var parts = raw.Trim().Split('@', 2);
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            throw new InvalidDataException($"M3U8 BYTERANGE 无效: {raw}");
        }

        long? offset = null;
        if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset) && parsedOffset >= 0)
        {
            offset = parsedOffset;
        }
        else if (previous is not null)
        {
            offset = previous.Offset + previous.Length;
        }

        return new M3u8ByteRange(length, offset);
    }

    private static Dictionary<string, string> ParseAttributes(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i] == '"')
            {
                quoted = !quoted;
            }

            if (i != raw.Length && (raw[i] != ',' || quoted))
            {
                continue;
            }

            var token = raw[start..i].Trim();
            var separator = token.IndexOf('=');
            if (separator > 0)
            {
                var key = token[..separator].Trim();
                var value = token[(separator + 1)..].Trim().Trim('"');
                result[key] = value;
            }
            start = i + 1;
        }

        return result;
    }

    private static bool TryResolveUri(Uri baseUri, string rawUri, out Uri resolvedUri)
    {
        var cleaned = rawUri.Trim().Trim('"');
        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            resolvedUri = absolute;
            return true;
        }

        if (Uri.TryCreate(baseUri, cleaned, out var relative) && relative.Scheme is "http" or "https")
        {
            resolvedUri = relative;
            return true;
        }

        resolvedUri = new Uri("about:blank");
        return false;
    }

    private sealed record M3u8VariantAttributes(long? Bandwidth, int? Width, int? Height, string? Codecs);
}

public sealed record M3u8Playlist(
    IReadOnlyList<M3u8Variant> Variants,
    IReadOnlyList<M3u8Segment> Segments,
    M3u8InitSegment? InitSegment,
    bool IsEndList,
    long MediaSequence);

public sealed record M3u8Variant(Uri Uri, long? Bandwidth, int? Width, int? Height, string? Codecs);

public sealed record M3u8Segment(
    Uri Uri,
    double Duration,
    long Sequence,
    M3u8ByteRange? ByteRange,
    M3u8Key? Key);

public sealed record M3u8InitSegment(Uri Uri, M3u8ByteRange? ByteRange, M3u8Key? Key);

public sealed record M3u8ByteRange(long Length, long? Offset);

public sealed record M3u8Key(string Method, Uri? Uri, byte[]? Iv);
