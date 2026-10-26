using System.Diagnostics;
using System.Text;

namespace ThodMedia.Worker;

internal sealed class Ffmpeg(string dataDir)
{
    private readonly string _cache = Directory.CreateDirectory(Path.Combine(dataDir, "cache")).FullName;
    public string FfmpegPath { get; } = Resolve("ffmpeg.exe", "THODMEDIA_FFMPEG");
    public string FfprobePath { get; } = Resolve("ffprobe.exe", "THODMEDIA_FFPROBE");

    private static string Resolve(string name, string env)
    {
        var explicitPath = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var local = Path.Combine(AppContext.BaseDirectory, "runtime", "ffmpeg", name);
        if (File.Exists(local)) return local;
        var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(x => Path.Combine(x.Trim(), name)).FirstOrDefault(File.Exists);
        if (onPath is not null) return onPath;
        return local;
    }

    public string CacheDirectory => _cache;

    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FfmpegPath)) return "не найден";
        var bytes = await CaptureAsync(["-version"], ct);
        return Encoding.UTF8.GetString(bytes).Split('\n').FirstOrDefault()?.Trim() ?? "ffmpeg";
    }

    public async Task<FfmpegResult> RunAsync(IEnumerable<string> arguments, double durationSeconds,
        Action<double, string>? progress, CancellationToken ct, bool throwOnError = true)
    {
        if (!File.Exists(FfmpegPath)) throw new InvalidOperationException("ffmpeg не найден в пакете. Переустановите ThodMedia.");
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" })
            psi.ArgumentList.Add(a);
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new IOException("Не удалось запустить ffmpeg.");
        process.StandardInput.Close();

        var stderr = new StringBuilder();
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx];
                var value = line[(idx + 1)..];
                if (durationSeconds > 0.05 && key is "out_time_us" or "out_time_ms")
                {
                    if (long.TryParse(value, out var us))
                        progress?.Invoke(Math.Clamp(us / 1_000_000d / durationSeconds * 100d, 0, 99.5), "");
                }
                else if (key == "speed" && value is not "N/A") progress?.Invoke(-1, value);
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            var tail = new StringBuilder();
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > 16_384) tail.Remove(0, tail.Length - 8_192);
            }
            lock (stderr) stderr.Append(tail);
        }, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        try { await Task.WhenAll(stdoutTask, stderrTask); } catch (OperationCanceledException) { }
        var result = new FfmpegResult(process.ExitCode, "", stderr.ToString().Trim());
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException(Explain(result.Stderr));
        return result;
    }

    public async Task<byte[]> CaptureAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        if (!File.Exists(FfmpegPath)) throw new InvalidOperationException("ffmpeg не найден в пакете.");
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }) psi.ArgumentList.Add(a);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new IOException("Не удалось запустить ffmpeg.");
        process.StandardInput.Close();
        using var ms = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await copy;
        var error = await err;
        if (process.ExitCode != 0) throw new InvalidOperationException(Explain(error));
        return ms.ToArray();
    }

    private static string Explain(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "ffmpeg завершился с ошибкой.";
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(6));
    }
}

internal sealed record FfmpegResult(int ExitCode, string Stdout, string Stderr = "");
