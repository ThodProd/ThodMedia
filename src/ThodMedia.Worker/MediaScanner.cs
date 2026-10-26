using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThodMedia.Worker;

internal sealed class MediaScanner(Ffmpeg ffmpeg, string dataDir)
{
    private readonly string _thumbDir = Directory.CreateDirectory(Path.Combine(dataDir, "cache", "thumbs")).FullName;
    private readonly ConcurrentDictionary<string, ProbeInfo> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".apng", ".webp", ".bmp", ".dib", ".tif", ".tiff", ".gif",
        ".avif", ".heic", ".heif", ".jxl", ".jp2", ".psd", ".tga", ".exr", ".hdr", ".dds", ".ico"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".m2ts",
        ".mts", ".ts", ".3gp", ".3g2", ".ogv", ".vob", ".rm", ".rmvb", ".asf", ".f4v"
    };

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif",
        ".ape", ".alac", ".mka", ".amr", ".ac3", ".dts"
    };

    public static string KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return "image";
        if (VideoExtensions.Contains(ext)) return "video";
        if (AudioExtensions.Contains(ext)) return "audio";
        return "other";
    }

    public object Scan(string folder, bool recursive)
    {
        folder = PathUtil.Normalize(folder);
        if (!Directory.Exists(folder)) throw new RpcException(-32602, "Папка не найдена.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var items = new List<ScanItem>();
        var truncated = false;
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            var kind = KindOf(file);
            if (kind == "other") continue;
            if (items.Count >= 5000) { truncated = true; break; }
            var info = new FileInfo(file);
            items.Add(new ScanItem(file, Path.GetRelativePath(folder, file), info.Name, kind,
                info.Extension.TrimStart('.').ToLowerInvariant(), info.Length, info.LastWriteTimeUtc, info.DirectoryName ?? folder));
        }
        items.Sort((a, b) =>
        {
            var kindCmp = string.Compare(a.Kind, b.Kind, StringComparison.Ordinal);
            return kindCmp != 0 ? kindCmp : string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase);
        });
        return new
        {
            folder,
            recursive,
            count = items.Count,
            truncated,
            items = items.Select(x => new
            {
                path = x.Path,
                relative = x.Relative,
                name = x.Name,
                kind = x.Kind,
                extension = x.Extension,
                size = x.Size,
                modified = x.Modified,
                directory = x.Directory
            }).ToArray()
        };
    }

    public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken ct)
    {
        path = PathUtil.Normalize(path);
        if (!File.Exists(path)) throw new RpcException(-32602, "Файл не найден.");
        var stamp = File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length;
        var key = path + "|" + stamp;
        if (_probeCache.TryGetValue(key, out var cached)) return cached;

        var psi = new ProcessStartInfo(ffmpeg.FfprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
            psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new IOException("Не удалось запустить ffprobe.");
        process.StandardInput.Close();
        var json = await process.StandardOutput.ReadToEndAsync(ct);
        var err = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException("ffprobe: " + err.Trim());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];
        var video = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "video" && !(x.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1));
        var audio = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");
        static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ToString() : null;
        static double Dbl(JsonElement e, string name, double fallback = 0) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

        var info = new ProbeInfo
        {
            Duration = Dbl(format, "duration"),
            Width = (int)Dbl(video, "width"),
            Height = (int)Dbl(video, "height"),
            VideoCodec = Str(video, "codec_name"),
            AudioCodec = Str(audio, "codec_name"),
            AudioChannels = (int)Dbl(audio, "channels"),
            SampleRate = (int)Dbl(audio, "sample_rate"),
            BitRate = (long)Dbl(format, "bit_rate"),
            Fps = ParseFraction(Str(video, "r_frame_rate")),
            HasVideo = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("codec_type", out _),
            HasAudio = audio.ValueKind == JsonValueKind.Object && audio.TryGetProperty("codec_type", out _),
            FormatName = Str(format, "format_name")
        };
        _probeCache[key] = info;
        return info;
    }

    private static double ParseFraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b != 0)
            return a / b;
        return 0;
    }

    public async Task<object> ThumbAsync(string path, int size, CancellationToken ct)
    {
        path = PathUtil.Normalize(path);
        if (!File.Exists(path)) throw new RpcException(-32602, "Файл не найден.");
        size = Math.Clamp(size <= 0 ? 320 : size, 96, 960);
        var kind = KindOf(path);
        var info = await ProbeAsync(path, ct);
        if (kind == "audio")
            return new { dataUrl = (string?)null, kind, probe = info };

        var stamp = File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length + "|" + size;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "|" + stamp))).ToLowerInvariant();
        var cached = Path.Combine(_thumbDir, hash + ".jpg");
        if (!File.Exists(cached))
        {
            var tmp = Path.Combine(_thumbDir, hash + ".tmp-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                var args = new List<string>();
                if (kind == "video")
                {
                    var seek = info.Duration > 2 ? 1.0 : 0.0;
                    if (seek > 0) { args.Add("-ss"); args.Add(seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
                }
                args.AddRange(["-i", path, "-frames:v", "1", "-vf", $"scale={size}:-2:flags=lanczos", "-q:v", "4", "-f", "image2", tmp]);
                try
                {
                    await ffmpeg.RunAsync(args, 0, null, ct);
                }
                catch
                {
                    if (kind == "video" && args[0] == "-ss")
                    {
                        args.RemoveRange(0, 2);
                        await ffmpeg.RunAsync(args, 0, null, ct);
                    }
                    else throw;
                }
                File.Move(tmp, cached, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        var bytes = await File.ReadAllBytesAsync(cached, ct);
        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        return new { dataUrl, kind, probe = info };
    }

    public void CleanCache(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(_thumbDir))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (Exception ex) { Log.Warn("thumb cache cleanup: " + ex.Message); }
    }
}

internal sealed record ScanItem(string Path, string Relative, string Name, string Kind, string Extension, long Size, DateTime Modified, string Directory);

internal sealed class ProbeInfo
{    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public int SampleRate { get; init; }
    public long BitRate { get; init; }
    public double Fps { get; init; }
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public string? FormatName { get; init; }
}
