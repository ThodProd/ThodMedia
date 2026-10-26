using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ThodMedia.Worker;

internal sealed class Job
{
    private readonly object _gate = new();
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Kind { get; }
    public JsonElement? Params { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public string State { get; private set; } = "queued";
    public double Progress { get; private set; }
    public string Message { get; private set; } = "";
    public object? Result { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; private set; }

    public Job(string kind, JsonElement? parameters)
    {
        Kind = kind;
        Params = parameters;
    }

    public void Report(double progress, string message)
    {
        lock (_gate)
        {
            if (State is "done" or "error" or "canceled") return;
            if (progress >= 0) Progress = Math.Clamp(progress, 0, 100);
            if (!string.IsNullOrWhiteSpace(message)) Message = message;
        }
    }

    public void Running()
    {
        lock (_gate) { State = "running"; Message = "Запуск"; }
    }

    public void Done(object result)
    {
        lock (_gate) { State = "done"; Progress = 100; Result = result; Message = "Готово"; FinishedAt = DateTimeOffset.Now; }
    }

    public void Fail(string error)
    {
        lock (_gate) { State = "error"; Error = error; Message = "Ошибка"; FinishedAt = DateTimeOffset.Now; }
    }

    public void Canceled()
    {
        lock (_gate) { State = "canceled"; Message = "Отменено"; FinishedAt = DateTimeOffset.Now; }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                id = Id,
                kind = Kind,
                state = State,
                progress = Math.Round(Progress, 1),
                message = Message,
                result = Result,
                error = Error,
                createdAt = CreatedAt,
                finishedAt = FinishedAt
            };
        }
    }
}

internal sealed class JobContext(Job job, CancellationToken token)
{
    public CancellationToken Token => token;
    public void Report(double progress, string message) => job.Report(progress, message);
    public void ThrowIfCancellationRequested() => token.ThrowIfCancellationRequested();
}

internal sealed class JobManager : IAsyncDisposable
{
    public static readonly HashSet<string> AllowedKinds =
    [
        "image.convert", "video.convert", "audio.extract", "video.muxAudio", "bg.preview", "bg.apply"
    ];

    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Func<Job, JobContext, Task<object>> _run;
    private readonly Task _loop;

    public JobManager(Func<Job, JobContext, Task<object>> run)
    {
        _run = run;
        _loop = Task.Run(LoopAsync);
    }

    public Job Start(string kind, JsonElement? parameters)
    {
        if (!AllowedKinds.Contains(kind)) throw new RpcException(-32602, "Неизвестный тип задания.");
        Cleanup();
        var job = new Job(kind, parameters);
        _jobs[job.Id] = job;
        if (!_queue.Writer.TryWrite(job)) throw new InvalidOperationException("Очередь заданий переполнена.");
        return job;
    }

    public Job Get(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) throw new RpcException(-32602, "Задание не найдено.");
        return job;
    }

    public object Status(string id) => Get(id).Snapshot();
    public object List() => _jobs.Values.OrderByDescending(x => x.CreatedAt).Take(30).Select(x => x.Snapshot()).ToArray();

    public object Cancel(string id)
    {
        var job = Get(id);
        try { job.Cancellation.Cancel(); } catch { }
        if (job.State == "queued") job.Canceled();
        return job.Snapshot();
    }

    public object Clear()
    {
        foreach (var job in _jobs.Values.Where(x => x.State is "done" or "error" or "canceled").ToArray())
        {
            _jobs.TryRemove(job.Id, out _);
            job.Cancellation.Dispose();
        }
        return new { removed = true };
    }

    private void Cleanup()
    {
        var finished = _jobs.Values.Where(x => x.State is "done" or "error" or "canceled")
            .OrderByDescending(x => x.FinishedAt).Skip(40).ToArray();
        foreach (var job in finished) _jobs.TryRemove(job.Id, out _);
    }

    private async Task LoopAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync())
        {
            if (job.Cancellation.IsCancellationRequested) { job.Canceled(); continue; }
            job.Running();
            try
            {
                var result = await _run(job, new JobContext(job, job.Cancellation.Token));
                if (job.Cancellation.IsCancellationRequested) job.Canceled();
                else job.Done(result);
            }
            catch (OperationCanceledException) { job.Canceled(); }
            catch (Exception ex)
            {
                Log.Error($"job {job.Kind}", ex);
                job.Fail(ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        foreach (var job in _jobs.Values) { try { job.Cancellation.Cancel(); } catch { } }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        foreach (var job in _jobs.Values) job.Cancellation.Dispose();
    }
}
