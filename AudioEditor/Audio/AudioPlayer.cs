using System;
using NAudio.Wave;

namespace WaveEdit.Audio;

/// <summary>
/// Streams a frame range of an <see cref="AudioDocument"/> as interleaved IEEE-float
/// to the output device. The current read position drives the on-screen playhead.
/// </summary>
internal sealed class DocumentSampleProvider : ISampleProvider
{
    private readonly AudioDocument _doc;
    private readonly long _end;
    private long _pos;

    public WaveFormat WaveFormat { get; }

    /// <summary>Current playback position in frames (absolute within the document).</summary>
    public long Position => System.Threading.Volatile.Read(ref _pos);

    /// <summary>Move the read position (seek). Clamped to the playable range.</summary>
    public void Seek(long frame) =>
        System.Threading.Volatile.Write(ref _pos, Math.Clamp(frame, 0, _end));

    public DocumentSampleProvider(AudioDocument doc, long start, long end)
    {
        _doc = doc;
        _pos = Math.Clamp(start, 0, doc.Length);
        _end = Math.Clamp(end, _pos, doc.Length);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(doc.SampleRate, doc.ChannelCount);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = _doc.ChannelCount;
        int framesWanted = count / ch;
        long framesLeft = _end - _pos;
        int frames = (int)Math.Min(framesWanted, framesLeft);
        int o = offset;
        for (int f = 0; f < frames; f++)
        {
            long src = _pos + f;
            for (int c = 0; c < ch; c++)
                buffer[o++] = _doc.Channels[c][src];
        }
        System.Threading.Volatile.Write(ref _pos, _pos + frames);
        return frames * ch;
    }
}

/// <summary>Owns the output device and exposes simple play/stop with a position read-out.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private DocumentSampleProvider? _provider;

    public event Action? PlaybackStopped;

    public bool IsPlaying => _output != null && _output.PlaybackState == PlaybackState.Playing;

    /// <summary>Current playback position in frames, or -1 when idle.</summary>
    public long PositionFrames => _provider?.Position ?? -1;

    /// <summary>Seek the active playback to a frame position (no-op when idle).</summary>
    public void Seek(long frame) => _provider?.Seek(frame);

    /// <summary>Play frames [start, end) of the document.</summary>
    public void Play(AudioDocument doc, long start, long end)
    {
        Stop();
        if (doc.Length == 0 || end <= start) return;

        _provider = new DocumentSampleProvider(doc, start, end);
        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.PlaybackStopped += OnStopped;
        _output.Init(_provider);
        _output.Play();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackStopped?.Invoke();
    }

    public void Stop()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _provider = null;
    }

    public void Dispose() => Stop();
}
