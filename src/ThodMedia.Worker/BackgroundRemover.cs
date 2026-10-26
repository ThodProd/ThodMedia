using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ThodMedia.Worker;

internal sealed class BackgroundRemover : IDisposable
{
    private const int ModelSize = 1024;
    private readonly Ffmpeg _ffmpeg;
    private readonly string _dataDir;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;

    public BackgroundRemover(Ffmpeg ffmpeg, string dataDir)
    {
        _ffmpeg = ffmpeg;
        _dataDir = dataDir;
        _modelPath = Path.Combine(AppContext.BaseDirectory, "models", "isnet-general-use.onnx");
    }

    public bool ModelAvailable => File.Exists(_modelPath);
    public string ModelPath => _modelPath;

    private InferenceSession EnsureSession()
    {
        if (_session is not null) return _session;
        if (!File.Exists(_modelPath)) throw new InvalidOperationException("Модель вырезания фона не найдена в пакете. Переустановите ThodMedia.");
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
            InterOpNumThreads = 1,
            EnableCpuMemArena = true
        };
        _session = new InferenceSession(_modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        Log.Info($"background model loaded: input={_inputName} output={_outputName}");
        return _session;
    }

    public async Task<object> PreviewAsync(Params p, JobContext ctx)
    {
        var path = p.Path("path");
        if (!File.Exists(path)) throw new FileNotFoundException("Изображение не найдено.");
        var maxSize = Math.Clamp(p.Int("maxSize", 1400), 480, 2000);
        ctx.Report(2, "Читаю изображение");
        using var original = await ImageLoader.DecodeAsync(_ffmpeg, path, 2400, _ffmpeg.CacheDirectory, ctx.Token);
        ctx.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        var modelMask = await RunModelAsync(original, ctx);
        sw.Stop();
        ctx.Report(75, "Готовлю предпросмотр");

        using var preview = ImageLoader.ResizeToFit(original, maxSize);
        using var modelMaskBitmap = ImageLoader.MaskToBitmap(modelMask, ModelSize, ModelSize);
        using var previewMask = ImageLoader.Resize(modelMaskBitmap, preview.Width, preview.Height);

        var result = new
        {
            image = ImageLoader.ToDataUrl(preview),
            mask = ImageLoader.ToDataUrl(previewMask),
            width = preview.Width,
            height = preview.Height,
            originalWidth = original.Width,
            originalHeight = original.Height,
            modelMs = sw.ElapsedMilliseconds
        };
        ctx.Report(100, "Готово");
        return result;
    }

    private async Task<float[]> RunModelAsync(Bitmap original, JobContext ctx)
    {
        await _gate.WaitAsync(ctx.Token);
        try
        {
            var session = EnsureSession();
            var tensor = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });
            using var square = new Bitmap(ModelSize, ModelSize, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(square))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(original, new Rectangle(0, 0, ModelSize, ModelSize));
            }
            var data = square.LockBits(new Rectangle(0, 0, ModelSize, ModelSize), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * ModelSize];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < ModelSize; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < ModelSize; x++)
                    {
                        var i = row + x * 3;
                        var idx = y * ModelSize + x;
                        tensor[0, 0, y, x] = (bytes[i + 2] / 255f - 0.5f);
                        tensor[0, 1, y, x] = (bytes[i + 1] / 255f - 0.5f);
                        tensor[0, 2, y, x] = (bytes[i] / 255f - 0.5f);
                    }
                }
            }
            finally { square.UnlockBits(data); }

            ctx.Report(20, "Нейросеть выделяет объект");
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName!, tensor) };
            using var results = session.Run(inputs);
            var output = results.First();
            var values = output.AsEnumerable<float>().ToArray();
            if (values.Length < ModelSize * ModelSize) throw new InvalidDataException("Модель вернула неожиданный результат.");

            var mask = new float[ModelSize * ModelSize];
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var i = 0; i < mask.Length; i++)
            {
                var v = values[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            var range = max - min;
            if (range < 1e-6f) throw new InvalidDataException("Не удалось выделить объект: маска пустая.");
            for (var i = 0; i < mask.Length; i++) mask[i] = (values[i] - min) / range;
            return mask;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> ApplyAsync(Params p, JobContext ctx)
    {
        var path = p.Path("path");
        if (!File.Exists(path)) throw new FileNotFoundException("Изображение не найдено.");
        var maskData = p.RequiredString("mask");
        var outputDir = p.String("outputDir");
        var suffix = p.String("suffix") ?? "-cutout";
        var overwrite = p.Bool("overwrite", false);
        var feather = Math.Clamp(p.Int("feather", 2), 0, 40);
        var refine = p.Bool("refine", true);
        if (string.IsNullOrWhiteSpace(outputDir)) outputDir = Path.Combine(Path.GetDirectoryName(path)!, "ThodMedia");
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + suffix + ".png");
        if (!overwrite) output = PathUtil.Unique(output);
        var temp = Path.Combine(outputDir, "." + Path.GetFileNameWithoutExtension(output) + ".tmp-" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            ctx.Report(2, "Читаю изображение");
            using var original = await ImageLoader.DecodeAsync(_ffmpeg, path, 12000, _ffmpeg.CacheDirectory, ctx.Token);
            ctx.Report(15, "Разбираю маску");
            using var maskBitmap = ImageLoader.FromBase64(maskData);
            if (maskBitmap.Width < 2 || maskBitmap.Height < 2) throw new InvalidDataException("Маска пустая.");

            var scale = Math.Min(1d, 1800d / Math.Max(original.Width, original.Height));
            var workW = Math.Max(2, (int)Math.Round(original.Width * scale));
            var workH = Math.Max(2, (int)Math.Round(original.Height * scale));
            var mask = ImageLoader.MaskFromBitmap(maskBitmap, workW, workH);
            ctx.Report(30, "Уточняю края");
            if (refine)
            {
                var guide = ImageLoader.Grayscale(original, workW, workH);
                mask = GuidedFilter.Refine(mask, guide, workW, workH);
            }
            if (feather > 0)
            {
                var scaledFeather = Math.Max(1, (int)Math.Round(feather * workW / (double)Math.Max(1, maskBitmap.Width)));
                mask = GuidedFilter.Feather(mask, workW, workH, scaledFeather);
            }
            ctx.Report(65, "Собираю PNG");
            using var workMask = ImageLoader.MaskToBitmap(mask, workW, workH);
            using var fullMask = ImageLoader.Resize(workMask, original.Width, original.Height);
            using var result = new Bitmap(original);
            ImageLoader.ApplyAlpha(result, fullMask);
            result.Save(temp, ImageFormat.Png);
            OutputPlanner.Commit(temp, output);
            ctx.Report(100, "Готово");
            return new { output, width = result.Width, height = result.Height, size = new FileInfo(output).Length };
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
    }
}
