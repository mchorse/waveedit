using System;

namespace WaveEdit.Audio;

/// <summary>
/// Sample-domain processing primitives. Each method mutates a planar channel set in
/// place over a frame range. They are pure with respect to length (frame count is
/// unchanged) so undo can simply restore the previous block.
/// </summary>
public static class Dsp
{
    public static void Amplify(float[][] channels, long start, long length, float gain)
    {
        for (int c = 0; c < channels.Length; c++)
        {
            var data = channels[c];
            long end = Math.Min(start + length, data.LongLength);
            for (long i = start; i < end; i++) data[i] *= gain;
        }
    }

    public static void Silence(float[][] channels, long start, long length)
    {
        for (int c = 0; c < channels.Length; c++)
        {
            var data = channels[c];
            long end = Math.Min(start + length, data.LongLength);
            for (long i = start; i < end; i++) data[i] = 0f;
        }
    }

    /// <summary>Scale so the loudest sample in the range reaches <paramref name="targetPeak"/> (0..1).</summary>
    public static void Normalize(float[][] channels, long start, long length, float targetPeak = 0.99f)
    {
        float peak = 0f;
        for (int c = 0; c < channels.Length; c++)
        {
            var data = channels[c];
            long end = Math.Min(start + length, data.LongLength);
            for (long i = start; i < end; i++)
            {
                float a = Math.Abs(data[i]);
                if (a > peak) peak = a;
            }
        }
        if (peak <= 1e-9f) return; // silence; nothing to do
        Amplify(channels, start, length, targetPeak / peak);
    }

    public static void FadeIn(float[][] channels, long start, long length)
    {
        if (length <= 1) return;
        for (int c = 0; c < channels.Length; c++)
        {
            var data = channels[c];
            long end = Math.Min(start + length, data.LongLength);
            long n = end - start;
            for (long i = start, k = 0; i < end; i++, k++)
                data[i] *= (float)((double)k / (n - 1));
        }
    }

    public static void FadeOut(float[][] channels, long start, long length)
    {
        if (length <= 1) return;
        for (int c = 0; c < channels.Length; c++)
        {
            var data = channels[c];
            long end = Math.Min(start + length, data.LongLength);
            long n = end - start;
            for (long i = start, k = 0; i < end; i++, k++)
                data[i] *= (float)(1.0 - (double)k / (n - 1));
        }
    }
}
