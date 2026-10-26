using System.Globalization;
using System.Text.Json;

namespace ThodMedia.Worker;

internal sealed class Params(JsonElement? value)
{
    private readonly JsonElement _value = value ?? default;

    public bool Has(string name) => _value.ValueKind == JsonValueKind.Object && _value.TryGetProperty(name, out _);

    public string? String(string name)
    {
        if (!Has(name)) return null;
        var e = _value.GetProperty(name);
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public string RequiredString(string name) =>
        string.IsNullOrWhiteSpace(String(name)) ? throw new RpcException(-32602, $"Не задан параметр {name}.") : String(name)!;

    public int Int(string name, int fallback)
    {
        var s = String(name);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public long Long(string name, long fallback)
    {
        var s = String(name);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public double Double(string name, double fallback)
    {
        var s = String(name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (!Has(name)) return fallback;
        var e = _value.GetProperty(name);
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => fallback
        };
    }

    public List<string> Strings(string name)
    {
        if (!Has(name)) return [];
        var e = _value.GetProperty(name);
        if (e.ValueKind == JsonValueKind.Array)
            return e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList();
        var single = String(name);
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    public string Path(string name) => PathUtil.Normalize(RequiredString(name));
}

internal static class PathUtil
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new RpcException(-32602, "Пустой путь.");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new RpcException(-32602, "Недопустимый путь.");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public static void EnsureInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(-32602, "Путь выходит за пределы выбранной папки.");
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать имя файла.");
    }
}
