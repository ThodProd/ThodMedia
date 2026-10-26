using System.Text;

namespace ThodMedia.Worker;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.Error.WriteLine($"ThodMedia.Worker {typeof(Program).Assembly.GetName().Version} starting");

        var dataDir = Environment.GetEnvironmentVariable("THODHUB_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThodMedia");
        dataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(dataDir);

        await using var engine = new Engine(dataDir);
        var server = new RpcServer(engine);
        try
        {
            await server.RunAsync(Console.In, Console.Out, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            return 1;
        }
        return 0;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    public static void Info(string message) { lock (Gate) Console.Error.WriteLine($"[info] {message}"); }
    public static void Warn(string message) { lock (Gate) Console.Error.WriteLine($"[warn] {message}"); }
    public static void Error(string message, Exception? ex = null)
    {
        lock (Gate) Console.Error.WriteLine($"[error] {message}" + (ex is null ? "" : ": " + ex.Message));
    }
}
