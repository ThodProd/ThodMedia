using System.Globalization;
using System.Text.Json;

namespace ThodMedia.Worker;

internal sealed class OutputPlanner(string dataDir)
{
    public string DataDir { get; } = dataDir;

    public string DefaultOutputDir(string source)
        => Path.Combine(Path.GetDirectoryName(source) ?? DataDir, "ThodMedia");

    public (string output, string temp) Plan(string source, string? outputDir, string extension,
        string suffix, bool overwrite, string? root, bool preserveStructure)
    {
        var dir = string.IsNullOrWhiteSpace(outputDir) ? DefaultOutputDir(source) : PathUtil.Normalize(outputDir);
        if (preserveStructure && !string.IsNullOrWhiteSpace(root))
        {
            var fullRoot = PathUtil.Normalize(root);
            var relative = Path.GetRelativePath(fullRoot, source);
            if (relative.StartsWith("..", StringComparison.Ordinal)) relative = Path.GetFileName(source);
            var sub = Path.GetDirectoryName(relative);
            if (!string.IsNullOrWhiteSpace(sub)) dir = Path.Combine(dir, sub);
        }
        Directory.CreateDirectory(dir);
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var baseName = Path.GetFileNameWithoutExtension(source) + (suffix ?? "");
        var output = Path.Combine(dir, baseName + ext.ToLowerInvariant());
        if (!overwrite) output = PathUtil.Unique(output);
        var temp = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(output) + ".tmp-" + Guid.NewGuid().ToString("N") + ext);
        return (output, temp);
    }

    public static void Commit(string temp, string output)
    {
        if (!File.Exists(temp)) throw new IOException("Результат не создан.");
        File.Move(temp, output, true);
    }
}

internal sealed class MediaOps(Ffmpeg ffmpeg, MediaScanner scanner, OutputPlanner planner)
{
    public async Task<object> ConvertImagesAsync(Params p, JobContext ctx)
    {
        var paths = p.Strings("paths");
        if (paths.Count == 0) throw new RpcException(-32602, "Не выбраны файлы.");
        var format = (p.String("format") ?? "png").ToLowerInvariant();
        var ext = format switch
        {
            "jpg" or "jpeg" => ".jpg",
            "png" => ".png",
            "webp" => ".webp",
            "avif" => ".avif",
            "bmp" => ".bmp",
            "tiff" or "tif" => ".tiff",
            "gif" => ".gif",
            _ => throw new RpcException(-32602, "Неизвестный формат изображения.")
        };
        var quality = Math.Clamp(p.Int("quality", 92), 1, 100);
        var mode = (p.String("resizeMode") ?? "none").ToLowerInvariant();
        var width = p.Int("width", 0);
        var height = p.Int("height", 0);
        var percent = p.Double("percent", 100);
        var allowUpscale = p.Bool("allowUpscale", true);
        var keepMetadata = p.Bool("keepMetadata", false);
        var background = (p.String("background") ?? "#ffffff").TrimStart('#');
        var outputDir = p.String("outputDir");
        var suffix = p.String("suffix") ?? "";
        var overwrite = p.Bool("overwrite", false);
        var root = p.String("root");
        var preserve = p.Bool("preserveStructure", false);

        var outputs = new List<object>();
        var errors = new List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            ctx.ThrowIfCancellationRequested();
            var source = PathUtil.Normalize(paths[i]);
            try
            {
                if (!File.Exists(source)) throw new InvalidOperationException("Файл не найден.");
                ctx.Report(i * 100d / paths.Count, Path.GetFileName(source));
                var probe = await scanner.ProbeAsync(source, ctx.Token);
                var (w, h) = TargetSize(probe.Width, probe.Height, mode, width, height, percent, allowUpscale);
                var (output, temp) = planner.Plan(source, outputDir, ext, suffix, overwrite, root, preserve);
                var args = new List<string> { "-i", source, "-frames:v", "1" };
                if (format is "jpg" or "jpeg")
                {
                    var color = NormalizeColor(background);
                    var filter = $"color=c=0x{color}:s={w}x{h}:d=1[bg];[0:v]scale={w}:{h}:flags=lanczos,format=rgba[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2:format=auto,format=rgb24[out]";
                    args.AddRange(["-filter_complex", filter, "-map", "[out]", "-q:v", JpegQuality(quality).ToString(CultureInfo.InvariantCulture)]);
                }
                else if (w > 0 && h > 0 && (mode != "none" || probe.Width != w || probe.Height != h))
                {
                    args.AddRange(["-vf", $"scale={w}:{h}:flags=lanczos"]);
                }
                switch (format)
                {
                    case "png": args.AddRange(["-c:v", "png", "-compression_level", "9"]); break;
                    case "webp": args.AddRange(["-c:v", "libwebp", "-quality", quality.ToString(CultureInfo.InvariantCulture), "-compression_level", "6"]); break;
                    case "avif": args.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-crf", Math.Clamp(63 - quality * 60 / 100, 0, 63).ToString(CultureInfo.InvariantCulture), "-cpu-used", "5"]); break;
                    case "tiff": args.AddRange(["-c:v", "tiff", "-compression", "alz"]); break;
                    case "gif": args.AddRange(["-loop", "0"]); break;
                }
                if (!keepMetadata) args.AddRange(["-map_metadata", "-1"]);
                args.Add(temp);
                await ffmpeg.RunAsync(args, 0, null, ctx.Token);
                OutputPlanner.Commit(temp, output);
                outputs.Add(new { source, output, width = w, height = h, size = new FileInfo(output).Length });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"image {source}: {ex.Message}");
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                CleanupTemp(outputDir ?? planner.DefaultOutputDir(source));
            }
            ctx.Report((i + 1) * 100d / paths.Count, "");
        }
        return new { outputs, errors, count = outputs.Count };
    }

    private static void CleanupTemp(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, ".*.tmp-*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private static string NormalizeColor(string value)
    {
        value = value.TrimStart('#');
        if (value.Length == 3) value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length != 6 || value.Any(c => !Uri.IsHexDigit(c))) return "ffffff";
        return value.ToLowerInvariant();
    }

    private static int JpegQuality(int quality) => Math.Clamp(31 - (int)Math.Round(quality * 29d / 100d), 1, 31);

    public static (int w, int h) TargetSize(int srcW, int srcH, string mode, int width, int height, double percent, bool allowUpscale)
    {
        if (srcW <= 0 || srcH <= 0) return (0, 0);
        double w = srcW, h = srcH;
        switch (mode)
        {
            case "percent":
                var factor = Math.Clamp(percent, 1, 800) / 100d;
                w = srcW * factor; h = srcH * factor;
                break;
            case "fit":
                if (width > 0 && height > 0)
                {
                    var scale = Math.Min(width / (double)srcW, height / (double)srcH);
                    if (!allowUpscale) scale = Math.Min(scale, 1);
                    w = srcW * scale; h = srcH * scale;
                }
                else if (width > 0) { w = width; h = srcH * width / (double)srcW; }
                else if (height > 0) { h = height; w = srcW * height / (double)srcH; }
                break;
            case "width":
                if (width > 0) { w = width; h = srcH * width / (double)srcW; }
                break;
            case "height":
                if (height > 0) { h = height; w = srcW * height / (double)srcH; }
                break;
            case "exact":
                if (width > 0) w = width;
                if (height > 0) h = height;
                break;
            case "longEdge":
                var edge = Math.Max(width, height);
                if (edge > 0)
                {
                    var scale = edge / (double)Math.Max(srcW, srcH);
                    if (!allowUpscale) scale = Math.Min(scale, 1);
                    w = srcW * scale; h = srcH * scale;
                }
                break;
        }
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }
}

internal sealed class VideoOps(Ffmpeg ffmpeg, MediaScanner scanner, OutputPlanner planner)
{
    public async Task<object> ConvertAsync(Params p, JobContext ctx)
    {
        var paths = p.Strings("paths");
        if (paths.Count == 0) throw new RpcException(-32602, "Не выбраны видео.");
        var preset = (p.String("preset") ?? "mp4-h264").ToLowerInvariant();
        var spec = Preset(preset);
        var crf = Math.Clamp(p.Int("crf", 23), 0, 51);
        var speed = (p.String("speed") ?? "medium").ToLowerInvariant();
        var width = p.Int("width", 0);
        var height = p.Int("height", 0);
        var fps = p.Double("fps", 0);
        var trimStart = ParseTime(p.String("trimStart"));
        var trimEnd = ParseTime(p.String("trimEnd"));
        var fastTrim = p.Bool("fastTrim", false);
        var audioMode = (p.String("audio") ?? "keep").ToLowerInvariant();
        var audioBitrate = Math.Clamp(p.Int("audioBitrate", 192), 32, 512);
        var outputDir = p.String("outputDir");
        var suffix = p.String("suffix") ?? "";
        var overwrite = p.Bool("overwrite", false);
        var root = p.String("root");
        var preserve = p.Bool("preserveStructure", false);

        var outputs = new List<object>();
        var errors = new List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            ctx.ThrowIfCancellationRequested();
            var source = PathUtil.Normalize(paths[i]);
            try
            {
                if (!File.Exists(source)) throw new InvalidOperationException("Файл не найден.");
                var probe = await scanner.ProbeAsync(source, ctx.Token);
                if (!probe.HasVideo) throw new InvalidOperationException("В файле нет видеодорожки.");
                var start = Math.Clamp(trimStart, 0, Math.Max(0, probe.Duration - 0.05));
                var end = trimEnd > 0 ? Math.Min(trimEnd, probe.Duration) : probe.Duration;
                if (end <= start) throw new InvalidOperationException("Конец обрезки должен быть позже начала.");
                var duration = end - start;
                var (output, temp) = planner.Plan(source, outputDir, spec.Extension, suffix, overwrite, root, preserve);

                var args = new List<string>();
                if (fastTrim && start > 0)
                {
                    args.AddRange(["-ss", Fmt(start)]);
                }
                args.AddRange(["-i", source]);
                if (!fastTrim && start > 0) args.AddRange(["-ss", Fmt(start)]);
                if (duration < probe.Duration - 0.05) args.AddRange(["-t", Fmt(duration)]);

                var scale = BuildScale(probe.Width, probe.Height, width, height);
                var videoFilter = new List<string>();
                if (scale is not null) videoFilter.Add($"scale={scale.Value.Width}:{scale.Value.Height}:flags=lanczos");
                if (fps > 0) videoFilter.Add($"fps={Fmt(fps)}");

                if (preset == "gif")
                {
                    var chain = string.Join(",", videoFilter.Concat(["split[s0][s1];[s0]palettegen=max_colors=256[p];[s1][p]paletteuse=dither=sierra2_4a"]));
                    args.AddRange(["-filter_complex", chain, "-an"]);
                }
                else
                {
                    if (videoFilter.Count > 0) args.AddRange(["-vf", string.Join(",", videoFilter)]);
                    if (fastTrim && spec.CopyFriendly)
                    {
                        args.AddRange(["-c", "copy"]);
                    }
                    else
                    {
                        args.AddRange(["-c:v", spec.VideoCodec]);
                        if (spec.VideoCodec.StartsWith("libx264", StringComparison.Ordinal) || spec.VideoCodec.StartsWith("libx265", StringComparison.Ordinal))
                            args.AddRange(["-preset", speed, "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"]);
                        else if (spec.VideoCodec == "libvpx-vp9")
                            args.AddRange(["-crf", crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p"]);
                        else if (spec.VideoCodec == "prores_ks")
                            args.AddRange(["-profile:v", "3", "-pix_fmt", "yuv422p10le"]);
                    }
                    if (spec.Tag is not null) args.AddRange(["-tag:v", spec.Tag]);
                    if (spec.MovFlags is not null) args.AddRange(["-movflags", spec.MovFlags]);

                    if (audioMode == "remove")
                    {
                        args.Add("-an");
                    }
                    else
                    {
                        args.AddRange(["-map", "0:v:0", "-map", "0:a?"]);
                        args.AddRange(["-c:a", spec.AudioCodec]);
                        if (spec.AudioCodec is "aac" or "libmp3lame" or "libopus")
                            args.AddRange(["-b:a", $"{audioBitrate}k"]);
                    }
                }
                if (!p.Bool("keepMetadata", false)) args.AddRange(["-map_metadata", "-1"]);
                args.Add(temp);
                await ffmpeg.RunAsync(args, duration, (pct, msg) => ctx.Report((i + (pct > 0 ? pct / 100d : 0)) * 100d / paths.Count, msg), ctx.Token);
                OutputPlanner.Commit(temp, output);
                outputs.Add(new { source, output, size = new FileInfo(output).Length, duration, width = scale?.Width ?? probe.Width, height = scale?.Height ?? probe.Height });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"video {source}: {ex.Message}");
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
            ctx.Report((i + 1) * 100d / paths.Count, "");
        }
        return new { outputs, errors, count = outputs.Count };
    }

    public async Task<object> ExtractAudioAsync(Params p, JobContext ctx)
    {
        var paths = p.Strings("paths");
        if (paths.Count == 0) throw new RpcException(-32602, "Не выбраны файлы.");
        var format = (p.String("format") ?? "mp3").ToLowerInvariant();
        var (codec, ext) = format switch
        {
            "mp3" => ("libmp3lame", ".mp3"),
            "wav" => ("pcm_s16le", ".wav"),
            "flac" => ("flac", ".flac"),
            "aac" => ("aac", ".m4a"),
            "opus" => ("libopus", ".opus"),
            "copy" => ("copy", ".mka"),
            _ => throw new RpcException(-32602, "Неизвестный аудиоформат.")
        };
        var bitrate = Math.Clamp(p.Int("bitrate", 192), 32, 512);
        var start = ParseTime(p.String("trimStart"));
        var end = ParseTime(p.String("trimEnd"));
        var outputDir = p.String("outputDir");
        var suffix = p.String("suffix") ?? "";
        var overwrite = p.Bool("overwrite", false);
        var root = p.String("root");
        var preserve = p.Bool("preserveStructure", false);
        var outputs = new List<object>();
        var errors = new List<string>();

        for (var i = 0; i < paths.Count; i++)
        {
            var source = PathUtil.Normalize(paths[i]);
            try
            {
                if (!File.Exists(source)) throw new InvalidOperationException("Файл не найден.");
                var probe = await scanner.ProbeAsync(source, ctx.Token);
                if (!probe.HasAudio) throw new InvalidOperationException("В файле нет звуковой дорожки.");
                var actualExt = format == "copy" ? GuessCopyExtension(probe.AudioCodec) : ext;
                var (output, temp) = planner.Plan(source, outputDir, actualExt, suffix, overwrite, root, preserve);
                var args = new List<string> { "-i", source, "-vn", "-map", "0:a:0" };
                if (start > 0) args.AddRange(["-ss", Fmt(start)]);
                var duration = end > start ? end - start : Math.Max(0, probe.Duration - start);
                if (end > 0 && duration > 0) args.AddRange(["-t", Fmt(duration)]);
                args.AddRange(["-c:a", codec]);
                if (codec is "libmp3lame" or "aac" or "libopus") args.AddRange(["-b:a", $"{bitrate}k"]);
                args.Add(temp);
                await ffmpeg.RunAsync(args, duration, (pct, msg) => ctx.Report((i + (pct > 0 ? pct / 100d : 0)) * 100d / paths.Count, msg), ctx.Token);
                OutputPlanner.Commit(temp, output);
                outputs.Add(new { source, output, size = new FileInfo(output).Length, duration });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"audio {source}: {ex.Message}");
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
            ctx.Report((i + 1) * 100d / paths.Count, "");
        }
        return new { outputs, errors, count = outputs.Count };
    }

    public async Task<object> MuxAudioAsync(Params p, JobContext ctx)
    {
        var video = PathUtil.Normalize(p.RequiredString("videoPath"));
        var audio = PathUtil.Normalize(p.RequiredString("audioPath"));
        if (!File.Exists(video)) throw new FileNotFoundException("Видео не найдено.");
        if (!File.Exists(audio)) throw new FileNotFoundException("Аудио не найдено.");
        var mode = (p.String("mode") ?? "replace").ToLowerInvariant();
        var bitrate = Math.Clamp(p.Int("audioBitrate", 192), 32, 512);
        var outputDir = p.String("outputDir");
        var suffix = p.String("suffix") ?? "-audio";
        var overwrite = p.Bool("overwrite", false);
        var vprobe = await scanner.ProbeAsync(video, ctx.Token);
        var aprobe = await scanner.ProbeAsync(audio, ctx.Token);
        if (!vprobe.HasVideo) throw new InvalidOperationException("В первом файле нет видео.");
        if (!aprobe.HasAudio) throw new InvalidOperationException("Во втором файле нет звука.");
        var (output, temp) = planner.Plan(video, outputDir, ".mp4", suffix, overwrite, null, false);
        var args = new List<string> { "-i", video, "-i", audio };
        if (mode == "mix" && vprobe.HasAudio)
        {
            args.AddRange(["-filter_complex", "[0:a][1:a]amix=inputs=2:duration=longest:dropout_transition=0[a]", "-map", "0:v:0", "-map", "[a]"]);
            args.AddRange(["-c:v", "copy", "-c:a", "aac", "-b:a", $"{bitrate}k", "-shortest"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", $"{bitrate}k", "-shortest"]);
        }
        args.AddRange(["-movflags", "+faststart", "-map_metadata", "0", temp]);
        await ffmpeg.RunAsync(args, Math.Min(vprobe.Duration, aprobe.Duration), (pct, msg) => ctx.Report(pct, msg), ctx.Token);
        OutputPlanner.Commit(temp, output);
        return new { output, size = new FileInfo(output).Length, duration = Math.Min(vprobe.Duration, aprobe.Duration) };
    }

    private static string GuessCopyExtension(string? codec) => codec switch
    {
        "aac" => ".m4a",
        "mp3" => ".mp3",
        "flac" => ".flac",
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "pcm_s16le" or "pcm_s24le" => ".wav",
        _ => ".mka"
    };

    private static (int Width, int Height)? BuildScale(int srcW, int srcH, int width, int height)
    {
        if (srcW <= 0 || srcH <= 0) return null;
        double w, h;
        if (width > 0 && height > 0) { w = width; h = height; }
        else if (width > 0) { w = width; h = srcH * width / (double)srcW; }
        else if (height > 0) { w = srcW * height / (double)srcH; h = height; }
        else return null;
        var iw = Math.Max(2, ((int)Math.Round(w)) & ~1);
        var ih = Math.Max(2, ((int)Math.Round(h)) & ~1);
        return (iw, ih);
    }

    public static double ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
            total = total * 60 + value;
        }
        return Math.Max(0, total);
    }

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record PresetSpec(string Extension, string VideoCodec, string AudioCodec, string? Tag = null, string? MovFlags = null, bool CopyFriendly = false);

    private static PresetSpec Preset(string name) => name switch
    {
        "mp4-h264" => new(".mp4", "libx264", "aac", MovFlags: "+faststart"),
        "mp4-h265" => new(".mp4", "libx265", "aac", Tag: "hvc1", MovFlags: "+faststart"),
        "webm-vp9" => new(".webm", "libvpx-vp9", "libopus"),
        "mkv-h264" => new(".mkv", "libx264", "aac"),
        "mov-prores" => new(".mov", "prores_ks", "pcm_s16le"),
        "gif" => new(".gif", "gif", "none"),
        "copy" => new(".mp4", "copy", "copy", CopyFriendly: true),
        _ => throw new RpcException(-32602, "Неизвестный пресет видео.")
    };

    public static object Presets() => new[]
    {
        new { id = "mp4-h264", name = "MP4 · H.264 + AAC", hint = "Универсально, играет везде" },
        new { id = "mp4-h265", name = "MP4 · H.265 + AAC", hint = "Меньше размер, нужен современный плеер" },
        new { id = "webm-vp9", name = "WebM · VP9 + Opus", hint = "Открытый формат для веба" },
        new { id = "mkv-h264", name = "MKV · H.264 + AAC", hint = "Гибкий контейнер" },
        new { id = "mov-prores", name = "MOV · ProRes", hint = "Монтаж, большой размер" },
        new { id = "gif", name = "GIF", hint = "Без звука, 15 fps по умолчанию" },
        new { id = "copy", name = "Без перекодирования", hint = "Быстро, но только совместимые контейнеры" }
    };
}
