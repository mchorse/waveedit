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
    private readonly double _end;
    private readonly object _sync = new();
    private double _pos;     // fractional read position, in frames
    private double _speed;   // frames consumed per output frame (varispeed; shifts pitch)

    public WaveFormat WaveFormat { get; }

    /// <summary>Current playback position in frames (absolute within the document).</summary>
    public long Position { get { lock (_sync) return (long)_pos; } }

    /// <summary>Playback speed multiplier. Resamples on the fly, so pitch shifts with speed.</summary>
    public double Speed
    {
        get { lock (_sync) return _speed; }
        set { lock (_sync) _speed = Math.Clamp(value, 0.05, 16.0); }
    }

    /// <summary>Move the read position (seek). Clamped to the playable range.</summary>
    public void Seek(long frame) { lock (_sync) _pos = Math.Clamp(frame, 0, _end); }

    public DocumentSampleProvider(AudioDocument doc, long start, long end, double speed)
    {
        _doc = doc;
        double s = Math.Clamp(start, 0, doc.Length);
        _pos = s;
        _end = Math.Clamp(end, s, doc.Length);
        _speed = Math.Clamp(speed, 0.05, 16.0);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(doc.SampleRate, doc.ChannelCount);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = _doc.ChannelCount;
        int framesWanted = count / ch;
        int o = offset;
        int produced = 0;

        lock (_sync)
        {
            double pos = _pos;
            double speed = _speed;
            long last = _doc.Length - 1;
            for (int f = 0; f < framesWanted; f++)
            {
                if (pos >= _end || last < 0) break;
                long i0 = (long)pos;
                long i1 = Math.Min(i0 + 1, last);
                double frac = pos - i0;
                for (int c = 0; c < ch; c++)
                {
                    var data = _doc.Channels[c];
                    float a = data[i0], b = data[i1];
                    buffer[o++] = (float)(a + (b - a) * frac); // linear interpolation
                }
                pos += speed;
                produced++;
            }
            _pos = pos;
        }
        return produced * ch;
    }
}

/// <summary>Owns the output device and exposes simple play/stop with a position read-out.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private DocumentSampleProvider? _provider;

    public event Action? PlaybackStopped;

    private double _speed = 1.0;

    public bool IsPlaying => _output != null && _output.PlaybackState == PlaybackState.Playing;

    /// <summary>Current playback position in frames, or -1 when idle.</summary>
    public long PositionFrames => _provider?.Position ?? -1;

    /// <summary>Playback speed multiplier (applied live if playing). Pitch shifts with speed.</summary>
    public double Speed
    {
        get => _speed;
        set { _speed = value; if (_provider != null) _provider.Speed = value; }
    }

    /// <summary>Seek the active playback to a frame position (no-op when idle).</summary>
    public void Seek(long frame) => _provider?.Seek(frame);

    /// <summary>Play frames [start, end) of the document.</summary>
    public void Play(AudioDocument doc, long start, long end)
    {
        Stop();
        if (doc.Length == 0 || end <= start) return;

        _provider = new DocumentSampleProvider(doc, start, end, _speed);
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
