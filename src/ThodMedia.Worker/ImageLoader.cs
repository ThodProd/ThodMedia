using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ThodMedia.Worker;

internal static class ImageLoader
{
    public static async Task<Bitmap> DecodeAsync(Ffmpeg ffmpeg, string path, int maxDimension, string cacheDir, CancellationToken ct)
    {
        var tmp = Path.Combine(cacheDir, "decode-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var args = new List<string> { "-i", path, "-frames:v", "1" };
            if (maxDimension > 0)
                args.AddRange(["-vf", $"scale='min(iw,{maxDimension})':'min(ih,{maxDimension})':force_original_aspect_ratio=decrease:flags=lanczos"]);
            args.AddRange(["-pix_fmt", "rgba", "-f", "image2", tmp]);
            await ffmpeg.RunAsync(args, 0, null, ct);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) throw new InvalidDataException("Не удалось прочитать изображение.");
            using var loaded = new Bitmap(tmp);
            return new Bitmap(loaded);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static Bitmap Resize(Bitmap source, int width, int height, bool highQuality = true)
    {
        var result = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = highQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    public static Bitmap ResizeToFit(Bitmap source, int maxDimension)
    {
        var scale = Math.Min(1d, maxDimension / (double)Math.Max(source.Width, source.Height));
        if (scale >= 0.999) return new Bitmap(source);
        return Resize(source, (int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale));
    }

    public static string ToDataUrl(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    public static Bitmap FromBase64(string data)
    {
        var comma = data.IndexOf(',');
        if (comma >= 0) data = data[(comma + 1)..];
        var bytes = Convert.FromBase64String(data);
        using var ms = new MemoryStream(bytes);
        using var loaded = new Bitmap(ms);
        return new Bitmap(loaded);
    }

    public static float[] Grayscale(Bitmap source, int width, int height)
    {
        using var resized = Resize(source, width, height);
        var data = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var result = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    result[y * width + x] = (0.2126f * bytes[i + 2] + 0.7152f * bytes[i + 1] + 0.0722f * bytes[i]) / 255f;
                }
            }
            return result;
        }
        finally { resized.UnlockBits(data); }
    }

    public static float[] MaskFromBitmap(Bitmap mask, int width, int height)
    {
        using var resized = Resize(mask, width, height);
        var data = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var result = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    result[y * width + x] = bytes[i + 2] / 255f;
                }
            }
            return result;
        }
        finally { resized.UnlockBits(data); }
    }

    public static Bitmap MaskToBitmap(float[] mask, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var v = (byte)Math.Clamp((int)Math.Round(mask[y * width + x] * 255), 0, 255);
                    var i = row + x * 4;
                    bytes[i] = v; bytes[i + 1] = v; bytes[i + 2] = v; bytes[i + 3] = 255;
                }
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public static void ApplyAlpha(Bitmap target, Bitmap mask)
    {
        using var resized = Resize(mask, target.Width, target.Height);
        var targetData = target.LockBits(new Rectangle(0, 0, target.Width, target.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var maskData = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var targetBytes = new byte[targetData.Stride * target.Height];
            Marshal.Copy(targetData.Scan0, targetBytes, 0, targetBytes.Length);
            var maskBytes = new byte[maskData.Stride * resized.Height];
            Marshal.Copy(maskData.Scan0, maskBytes, 0, maskBytes.Length);
            for (var y = 0; y < target.Height; y++)
            {
                var trow = y * targetData.Stride;
                var mrow = y * maskData.Stride;
                for (var x = 0; x < target.Width; x++)
                {
                    var ti = trow + x * 4;
                    var mi = mrow + x * 4;
                    var alpha = maskBytes[mi + 2];
                    targetBytes[ti + 3] = (byte)(targetBytes[ti + 3] * alpha / 255);
                }
            }
            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            target.UnlockBits(targetData);
            resized.UnlockBits(maskData);
        }
    }
}
