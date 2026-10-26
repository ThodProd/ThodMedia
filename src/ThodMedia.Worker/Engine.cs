using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace ThodMedia.Worker;

internal sealed class Engine : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly Ffmpeg _ffmpeg;
    private readonly MediaScanner _scanner;
    private readonly OutputPlanner _planner;
    private readonly MediaOps _media;
    private readonly VideoOps _video;

    private readonly JobManager _jobs;

    public Engine(string dataDir)
    {
        _dataDir = dataDir;
        _ffmpeg = new Ffmpeg(dataDir);
        _scanner = new MediaScanner(_ffmpeg, dataDir);
        _planner = new OutputPlanner(dataDir);
        _media = new MediaOps(_ffmpeg, _scanner, _planner);
        _video = new VideoOps(_ffmpeg, _scanner, _planner);

        _jobs = new JobManager(RunJobAsync);
        _scanner.CleanCache(TimeSpan.FromDays(14));
    }

    public async Task<object> HandleAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        var p = new Params(parameters);
        switch (method)
        {
            case "ping":
                return new { ok = true, version = Version(), dataDir = _dataDir };
            case "environment":
                var ffmpegVersion = await _ffmpeg.VersionAsync(ct);
                return new
                {
                    version = Version(),
                    dataDir = _dataDir,
                    ffmpeg = ffmpegVersion,
                    ffmpegFound = File.Exists(_ffmpeg.FfmpegPath),
                    ffprobeFound = File.Exists(_ffmpeg.FfprobePath),
                    modelFound = false,
                    platform = "win-x64",
                    cores = Environment.ProcessorCount,
                    cacheDir = _ffmpeg.CacheDirectory
                };
            case "scan":
                return _scanner.Scan(p.RequiredString("folder"), p.Bool("recursive", true));
            case "thumb":
                return await _scanner.ThumbAsync(p.RequiredString("path"), p.Int("size", 320), ct);
            case "probe":
                return await _scanner.ProbeAsync(p.RequiredString("path"), ct);
            case "presets":
                return new
                {
                    video = VideoOps.Presets(),
                    image = new[]
                    {
                        new { id = "png", name = "PNG", hint = "Без потерь, прозрачность" },
                        new { id = "jpg", name = "JPEG", hint = "Фото, компактно" },
                        new { id = "webp", name = "WebP", hint = "Малый размер, прозрачность" },
                        new { id = "avif", name = "AVIF", hint = "Очень малый размер, медленно" },
                        new { id = "bmp", name = "BMP", hint = "Без сжатия" },
                        new { id = "tiff", name = "TIFF", hint = "Печать и архивирование" },
                        new { id = "gif", name = "GIF", hint = "Первый кадр, 256 цветов" }
                    },
                    audio = new[]
                    {
                        new { id = "mp3", name = "MP3", hint = "Универсально" },
                        new { id = "wav", name = "WAV", hint = "Без сжатия" },
                        new { id = "flac", name = "FLAC", hint = "Без потерь" },
                        new { id = "aac", name = "AAC (M4A)", hint = "Эффективно" },
                        new { id = "opus", name = "Opus", hint = "Речь и веб" },
                        new { id = "copy", name = "Без перекодирования", hint = "Как в исходнике" }
                    }
                };
            case "job.start":
                var kind = p.RequiredString("kind");
                JsonElement? jobParams = parameters is { } root && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("params", out var inner)
                    ? inner.Clone()
                    : (JsonElement?)null;
                var job = _jobs.Start(kind, jobParams);
                return new { jobId = job.Id };
            case "job.status":
                return _jobs.Status(p.RequiredString("id"));
            case "job.list":
                return _jobs.List();
            case "job.cancel":
                return _jobs.Cancel(p.RequiredString("id"));
            case "job.clear":
                return _jobs.Clear();
            case "reveal":
                Reveal(p.RequiredString("path"));
                return new { ok = true };
            case "open":
                Open(p.RequiredString("path"));
                return new { ok = true };
            default:
                throw new RpcException(-32601, $"Метод {method} не поддерживается.");
        }
    }

    private Task<object> RunJobAsync(Job job, JobContext ctx)
    {
        var p = new Params(job.Params);
        return job.Kind switch
        {
            "image.convert" => _media.ConvertImagesAsync(p, ctx),
            "video.convert" => _video.ConvertAsync(p, ctx),
            "audio.extract" => _video.ExtractAudioAsync(p, ctx),
            "video.muxAudio" => _video.MuxAudioAsync(p, ctx),


            _ => throw new RpcException(-32602, "Неизвестный тип задания.")
        };
    }

    private static void Reveal(string path)
    {
        path = PathUtil.Normalize(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new RpcException(-32602, "Файл не найден.");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void Open(string path)
    {
        path = PathUtil.Normalize(path);
        if (!File.Exists(path)) throw new RpcException(-32602, "Файл не найден.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string Version() => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _jobs.DisposeAsync();

    }

    public Task ShutdownAsync() => DisposeAsync().AsTask();

    private int _disposed;
}
