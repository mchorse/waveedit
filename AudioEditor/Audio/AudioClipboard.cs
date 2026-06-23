using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WaveEdit.Audio;

/// <summary>
/// Copy/paste of audio via the Windows system clipboard (standard CF_WAVE format),
/// so regions can move between WaveEdit windows — and to/from other audio apps.
/// Audio is carried as a 32-bit-float WAV, so transfers are lossless.
/// </summary>
public static class AudioClipboard
{
    public static bool HasAudio()
    {
        try { return Clipboard.ContainsAudio(); }
        catch { return false; }
    }

    /// <summary>Put planar audio on the clipboard (persists after this window closes).</summary>
    public static void SetAudio(float[][] channels, int sampleRate)
    {
        var bytes = WavIO.WavBytes(channels, sampleRate);
        var data = new DataObject();
        data.SetData(DataFormats.WaveAudio, new MemoryStream(bytes));
        // copy: true flushes the data to the OS clipboard so it survives app exit.
        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    /// <summary>Read clipboard audio into a document, or null if none/unsupported.</summary>
    public static AudioDocument? TryGetAudio()
    {
        try
        {
            if (!Clipboard.ContainsAudio()) return null;
            using var stream = Clipboard.GetData(DataFormats.WaveAudio) as Stream;
            if (stream == null) return null;
            stream.Position = 0;
            return WavIO.LoadFromStream(stream);
        }
        catch
        {
            return null; // clipboard busy, or not a WAV we can parse
        }
    }

    // The clipboard can be momentarily locked by another process; retry a few times.
    private static void Retry(Action action)
    {
        for (int i = 0; i < 5; i++)
        {
            try { action(); return; }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        action(); // last attempt; let any error surface
    }
}
