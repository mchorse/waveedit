using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveEdit.Audio;

/// <summary>
/// Sample-rate conversion using NAudio's managed WDL resampler (no MediaFoundation /
/// COM dependency, so it works the same everywhere). Operates on the planar float model.
/// </summary>
public static class Resampler
{
    /// <summary>
    /// Return a copy of <paramref name="src"/> converted to <paramref name="dstRate"/> Hz.
    /// If the rate already matches, the original document is returned unchanged.
    /// </summary>
    public static AudioDocument Resample(AudioDocument src, int dstRate)
    {
        if (dstRate <= 0 || src.SampleRate == dstRate || src.Length == 0)
            return src;

        int ch = src.ChannelCount;
        ISampleProvider provider = new DocumentSampleProvider(src, 0, src.Length, 1.0); // interleaved float @ src rate
        var resampler = new WdlResamplingSampleProvider(provider, dstRate);

        var outBuf = new List<float>();
        var tmp = new float[dstRate * ch]; // ~1s blocks
        int read;
        while ((read = resampler.Read(tmp, 0, tmp.Length)) > 0)
            for (int i = 0; i < read; i++) outBuf.Add(tmp[i]);

        long frames = ch > 0 ? outBuf.Count / ch : 0;
        var planar = new float[ch][];
        for (int c = 0; c < ch; c++) planar[c] = new float[frames];
        for (long f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                planar[c][f] = outBuf[(int)(f * ch + c)];

        return new AudioDocument(dstRate, planar);
    }
}
