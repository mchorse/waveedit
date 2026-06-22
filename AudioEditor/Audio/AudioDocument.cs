using System;

namespace WaveEdit.Audio;

/// <summary>
/// In-memory representation of an audio clip.
/// Samples are stored as 32-bit float in planar (non-interleaved) layout:
/// <c>Channels[ch][frame]</c>. Editing always operates on whole frames across
/// every channel in lockstep so the channels never drift out of sync.
/// </summary>
public sealed class AudioDocument
{
    /// <summary>Sample rate in Hz (e.g. 44100, 48000). Any rate is supported.</summary>
    public int SampleRate { get; set; }

    /// <summary>Planar sample data. Outer = channel, inner = frame. Values nominally in [-1, 1].</summary>
    public float[][] Channels { get; private set; }

    /// <summary>Preferred bit depth when saving (16 / 24 / 32). Ignored when <see cref="SaveAsFloat"/>.</summary>
    public int BitDepth { get; set; } = 16;

    /// <summary>When true the file is written as 32-bit IEEE float instead of PCM.</summary>
    public bool SaveAsFloat { get; set; }

    /// <summary>Path of the file backing this document, or null if never saved.</summary>
    public string? FilePath { get; set; }

    /// <summary>True if there are unsaved edits.</summary>
    public bool Modified { get; set; }

    public int ChannelCount => Channels.Length;

    /// <summary>Number of frames (samples per channel).</summary>
    public long Length => Channels.Length > 0 ? Channels[0].LongLength : 0;

    public double DurationSeconds => SampleRate > 0 ? (double)Length / SampleRate : 0;

    public AudioDocument(int sampleRate, float[][] channels)
    {
        if (channels.Length == 0)
            throw new ArgumentException("At least one channel is required.", nameof(channels));
        long len = channels[0].LongLength;
        foreach (var c in channels)
            if (c.LongLength != len)
                throw new ArgumentException("All channels must have the same length.", nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>Create an empty document (zero frames) with the given format.</summary>
    public static AudioDocument CreateEmpty(int sampleRate, int channelCount)
    {
        var ch = new float[channelCount][];
        for (int i = 0; i < channelCount; i++) ch[i] = Array.Empty<float>();
        return new AudioDocument(sampleRate, ch);
    }

    /// <summary>Replace the backing arrays. Used by edit commands.</summary>
    public void SetChannels(float[][] channels)
    {
        Channels = channels;
        Modified = true;
    }

    /// <summary>Deep-copy a frame range into a new planar array set.</summary>
    public float[][] ExtractRange(long start, long length)
    {
        start = Math.Clamp(start, 0, Length);
        length = Math.Clamp(length, 0, Length - start);
        var outCh = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            var dst = new float[length];
            Array.Copy(Channels[c], start, dst, 0, length);
            outCh[c] = dst;
        }
        return outCh;
    }
}
