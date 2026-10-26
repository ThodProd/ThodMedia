namespace ThodMedia.Worker;

internal static class GuidedFilter
{
    public static float[] Refine(float[] mask, float[] guide, int width, int height, int radius = 8, float eps = 1e-4f)
    {
        var n = width * height;
        var meanI = BoxBlur(guide, width, height, radius);
        var meanP = BoxBlur(mask, width, height, radius);
        var corrI = new float[n];
        var corrIP = new float[n];
        for (var i = 0; i < n; i++)
        {
            corrI[i] = guide[i] * guide[i];
            corrIP[i] = guide[i] * mask[i];
        }
        corrI = BoxBlur(corrI, width, height, radius);
        corrIP = BoxBlur(corrIP, width, height, radius);

        var a = new float[n];
        var b = new float[n];
        for (var i = 0; i < n; i++)
        {
            var varI = corrI[i] - meanI[i] * meanI[i];
            var covIP = corrIP[i] - meanI[i] * meanP[i];
            var ai = covIP / (varI + eps);
            a[i] = ai;
            b[i] = meanP[i] - ai * meanI[i];
        }
        var meanA = BoxBlur(a, width, height, radius);
        var meanB = BoxBlur(b, width, height, radius);
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            var q = Math.Clamp(meanA[i] * guide[i] + meanB[i], 0f, 1f);
            result[i] = mask[i] <= 0.02f ? 0f : mask[i] >= 0.98f ? 1f : q;
        }
        return result;
    }

    public static float[] Feather(float[] mask, int width, int height, int radius)
    {
        if (radius <= 0) return mask;
        var r = Math.Max(1, radius / 2);
        var result = BoxBlur(mask, width, height, r);
        result = BoxBlur(result, width, height, r);
        result = BoxBlur(result, width, height, r);
        for (var i = 0; i < result.Length; i++) result[i] = Math.Clamp(result[i], 0f, 1f);
        return result;
    }

    public static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        if (radius <= 0) return (float[])source.Clone();
        var tmp = new float[source.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            double sum = 0;
            var count = 0;
            for (var x = 0; x <= Math.Min(radius, width - 1); x++) { sum += source[row + x]; count++; }
            for (var x = 0; x < width; x++)
            {
                tmp[row + x] = (float)(sum / count);
                var add = x + radius + 1;
                if (add < width) { sum += source[row + add]; count++; }
                var remove = x - radius;
                if (remove >= 0) { sum -= source[row + remove]; count--; }
            }
        }
        var result = new float[source.Length];
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            var count = 0;
            for (var y = 0; y <= Math.Min(radius, height - 1); y++) { sum += tmp[y * width + x]; count++; }
            for (var y = 0; y < height; y++)
            {
                result[y * width + x] = (float)(sum / count);
                var add = y + radius + 1;
                if (add < height) { sum += tmp[add * width + x]; count++; }
                var remove = y - radius;
                if (remove >= 0) { sum -= tmp[remove * width + x]; count--; }
            }
        }
        return result;
    }
}
