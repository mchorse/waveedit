using System;
using System.IO;
using NAudio.Wave;
using NAudio.Vorbis;

namespace WaveEdit.Audio;

/// <summary>
/// Reads and writes audio files: WAV (PCM 16/24/32 and IEEE float) and Ogg Vorbis,
/// plus anything NAudio can decode on the way in (MP3/AIFF/WMA, etc.). The public
/// surface is format-agnostic so additional encoders can be slotted in later.
/// </summary>
public static class WavIO
{
    /// <summary>Filter string for the open dialog.</summary>
    public const string OpenFilter =
        "Audio files|*.wav;*.ogg;*.mp3;*.aiff;*.aif;*.wma;*.flac|WAV files|*.wav|Ogg Vorbis|*.ogg|All files|*.*";

    /// <summary>Filter string for the save dialog. Index maps to a save format.</summary>
    public const string SaveFilter =
        "WAV PCM 16-bit|*.wav|WAV PCM 24-bit|*.wav|WAV 32-bit float|*.wav|Ogg Vorbis|*.ogg";

    public static AudioDocument Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ogg")
        {
            using var v = new VorbisWaveReader(path);
            long est = (long)(v.TotalTime.TotalSeconds * v.WaveFormat.SampleRate);
            var oggDoc = FillPlanar(v, v.WaveFormat.Channels, v.WaveFormat.SampleRate, est);
            oggDoc.FilePath = path;
            return oggDoc;
        }

        // AudioFileReader normalises everything to interleaved 32-bit float while
        // preserving the original sample rate and channel count.
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        long estFrames = channels > 0 ? reader.Length / 4 / channels : 0;
        var doc = FillPlanar(reader, channels, reader.WaveFormat.SampleRate, estFrames);
        doc.FilePath = path;
        ApplySourceFormat(doc, path);
        return doc;
    }

    /// <summary>
    /// Stream an interleaved float source straight into planar per-channel arrays.
    /// The estimate (exact for WAV, approximate for decoded sources) is grown/trimmed
    /// to the true frame count, so no full-size intermediate copy is made.
    /// </summary>
    private static AudioDocument FillPlanar(ISampleProvider src, int channels, int sampleRate, long estFrames)
    {
        if (estFrames < 0) estFrames = 0;
        var planar = new float[channels][];
        for (int c = 0; c < channels; c++) planar[c] = new float[estFrames];

        var buffer = new float[Math.Max(channels, sampleRate * channels)]; // ~1s blocks
        long frame = 0;
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            int framesRead = read / channels;
            if (frame + framesRead > planar[0].LongLength)
            {
                long grow = Math.Max(frame + framesRead, planar[0].LongLength + planar[0].LongLength / 4 + 4096);
                for (int c = 0; c < channels; c++) Array.Resize(ref planar[c], checked((int)grow));
            }
            int idx = 0;
            for (int f = 0; f < framesRead; f++)
                for (int c = 0; c < channels; c++)
                    planar[c][frame + f] = buffer[idx++];
            frame += framesRead;
        }
        if (frame != planar[0].LongLength)
            for (int c = 0; c < channels; c++) Array.Resize(ref planar[c], checked((int)frame));

        return new AudioDocument(sampleRate, planar);
    }

    private static void ApplySourceFormat(AudioDocument doc, string path)
    {
        // For real WAVs, remember the source bit depth so re-saving is loss-faithful.
        try
        {
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using var wf = new WaveFileReader(path);
                var fmt = wf.WaveFormat;
                if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    doc.SaveAsFloat = true;
                    doc.BitDepth = 32;
                }
                else
                {
                    doc.SaveAsFloat = false;
                    doc.BitDepth = fmt.BitsPerSample == 0 ? 16 : fmt.BitsPerSample;
                }
            }
        }
        catch
        {
            // Decoded (e.g. MP3) sources: default to 16-bit PCM on save.
        }
    }

    /// <summary>Save by extension: .ogg -> Vorbis, otherwise WAV per BitDepth / SaveAsFloat.</summary>
    public static void Save(AudioDocument doc, string path)
    {
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            VorbisEncoderIO.Save(doc, path, doc.OggQuality);
        else if (doc.SaveAsFloat) SaveFloat32(doc, path);
        else SavePcm(doc, path, doc.BitDepth);
        doc.FilePath = path;
        doc.Modified = false;
    }

    /// <summary>Save with an explicit format chosen by the Save dialog filter index (1-based).</summary>
    public static void SaveWithFilterIndex(AudioDocument doc, string path, int filterIndex)
    {
        switch (filterIndex)
        {
            case 2: doc.SaveAsFloat = false; doc.BitDepth = 24; break;
            case 3: doc.SaveAsFloat = true; doc.BitDepth = 32; break;
            case 4: break;                       // Ogg Vorbis — dispatched by extension
            default: doc.SaveAsFloat = false; doc.BitDepth = 16; break;
        }
        Save(doc, path);
    }

    private static void SaveFloat32(AudioDocument doc, string path)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(doc.SampleRate, doc.ChannelCount);
        using var writer = new WaveFileWriter(path, fmt);
        long len = doc.Length;
        var frame = new float[doc.ChannelCount];
        for (long f = 0; f < len; f++)
        {
            for (int c = 0; c < doc.ChannelCount; c++) frame[c] = doc.Channels[c][f];
            writer.WriteSamples(frame, 0, frame.Length);
        }
    }

    private static void SavePcm(AudioDocument doc, string path, int bitDepth)
    {
        if (bitDepth != 16 && bitDepth != 24 && bitDepth != 32) bitDepth = 16;
        var fmt = new WaveFormat(doc.SampleRate, bitDepth, doc.ChannelCount);
        using var writer = new WaveFileWriter(path, fmt);
        long len = doc.Length;
        int ch = doc.ChannelCount;

        // Convert float -> integer PCM with clamping.
        for (long f = 0; f < len; f++)
        {
            for (int c = 0; c < ch; c++)
            {
                float s = doc.Channels[c][f];
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                switch (bitDepth)
                {
                    case 16:
                        writer.WriteSample(s); // NAudio handles float->16 internally
                        break;
                    case 24:
                        WriteSample24(writer, s);
                        break;
                    case 32:
                        WriteSample32(writer, s);
                        break;
                }
            }
        }
    }

    private static void WriteSample24(WaveFileWriter writer, float s)
    {
        int v = (int)Math.Round(s * 8_388_607.0);
        if (v > 8_388_607) v = 8_388_607; else if (v < -8_388_608) v = -8_388_608;
        writer.WriteByte((byte)v);
        writer.WriteByte((byte)(v >> 8));
        writer.WriteByte((byte)(v >> 16));
    }

    private static void WriteSample32(WaveFileWriter writer, float s)
    {
        long v = (long)Math.Round(s * 2_147_483_647.0);
        if (v > int.MaxValue) v = int.MaxValue; else if (v < int.MinValue) v = int.MinValue;
        int iv = (int)v;
        writer.WriteByte((byte)iv);
        writer.WriteByte((byte)(iv >> 8));
        writer.WriteByte((byte)(iv >> 16));
        writer.WriteByte((byte)(iv >> 24));
    }
}
