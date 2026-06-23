using System;
using System.IO;
using OggVorbisEncoder;

namespace WaveEdit.Audio;

/// <summary>
/// Encodes the planar float document to an Ogg Vorbis file using the managed
/// OggVorbisEncoder (a libvorbis port). Lossy; <paramref name="quality"/> is the
/// Vorbis base quality in [-0.1, 1.0] (higher = better, larger file).
/// </summary>
internal static class VorbisEncoderIO
{
    public static void Save(AudioDocument doc, string path, float quality)
    {
        int channels = doc.ChannelCount;
        int sampleRate = doc.SampleRate;
        float q = Math.Clamp(quality, -0.1f, 1.0f);

        var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, q);

        // deterministic, non-zero stream serial (no RNG needed)
        int serial = unchecked((int)(doc.Length * 2654435761u) ^ (sampleRate * 31) ^ 0x5715);
        var oggStream = new OggStream(serial);

        var comments = new Comments();
        comments.AddTag("ENCODER", "WaveEdit");
        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

        using var outStream = File.Create(path);
        FlushPages(oggStream, outStream, force: true);

        var state = ProcessingState.Create(info);
        long len = doc.Length;
        const int block = 4096;
        var chunk = new float[channels][];
        for (int c = 0; c < channels; c++) chunk[c] = new float[block];

        for (long pos = 0; pos < len; pos += block)
        {
            int count = (int)Math.Min(block, len - pos);
            for (int c = 0; c < channels; c++)
                Array.Copy(doc.Channels[c], pos, chunk[c], 0, count);

            state.WriteData(chunk, count);
            DrainPackets(state, oggStream, outStream);
        }

        state.WriteEndOfStream();
        DrainPackets(state, oggStream, outStream);
        FlushPages(oggStream, outStream, force: true);
    }

    private static void DrainPackets(ProcessingState state, OggStream oggStream, Stream output)
    {
        while (!oggStream.Finished && state.PacketOut(out OggPacket packet))
        {
            oggStream.PacketIn(packet);
            FlushPages(oggStream, output, force: false);
        }
    }

    private static void FlushPages(OggStream oggStream, Stream output, bool force)
    {
        while (oggStream.PageOut(out OggPage page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }
}
