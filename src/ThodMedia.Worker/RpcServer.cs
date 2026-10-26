using System.Text.Json;

namespace ThodMedia.Worker;

internal sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed class RpcServer(Engine engine)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        var writeGate = new SemaphoreSlim(1, 1);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        var stdinClosed = shutdown.Token;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await input.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            _ = Task.Run(async () =>
            {
                string? id = null;
                object? response;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new RpcException(-32600, "Ожидался объект JSON-RPC.");
                    if (root.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                        id = idNode.GetString();
                    if (id is null) return;
                    if (!root.TryGetProperty("method", out var methodNode) || methodNode.ValueKind != JsonValueKind.String)
                        throw new RpcException(-32600, "Не указан метод.");
                    var method = methodNode.GetString()!;
                    JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : null;
                    var result = await engine.HandleAsync(method, parameters, stdinClosed);
                    response = new { jsonrpc = "2.0", id, result };
                }
                catch (RpcException ex)
                {
                    response = new { jsonrpc = "2.0", id, error = new { code = ex.Code, message = ex.Message } };
                }
                catch (OperationCanceledException)
                {
                    response = new { jsonrpc = "2.0", id, error = new { code = -32800, message = "Запрос отменён." } };
                }
                catch (Exception ex)
                {
                    Log.Error("request failed", ex);
                    response = new { jsonrpc = "2.0", id, error = new { code = -32000, message = ex.Message } };
                }
                if (id is null || response is null) return;
                var json = JsonSerializer.Serialize(response, JsonOptions);
                await writeGate.WaitAsync();
                try
                {
                    await output.WriteLineAsync(json);
                    await output.FlushAsync();
                }
                finally { writeGate.Release(); }
            }, CancellationToken.None);
        }

        shutdown.Cancel();
        await engine.ShutdownAsync();
    }
}
