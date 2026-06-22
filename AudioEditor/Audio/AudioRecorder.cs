using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WaveEdit.Audio;

/// <summary>Lightweight description of a capture endpoint for the UI.</summary>
public sealed record InputDevice(string Id, string Name, bool IsLoopback)
{
    public override string ToString() => Name;
}

/// <summary>
/// Captures from any active input endpoint via WASAPI (shared mode). The device's
/// own mix format is used, then converted to planar float on stop. Also supports
/// loopback capture of the default render device ("what you hear").
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private IWaveIn? _capture;
    private readonly List<float>[] _channelBuffers = Array.Empty<List<float>>();
    private List<float>[] _buffers = Array.Empty<List<float>>();
    private WaveFormat? _format;

    /// <summary>Raised on each buffer with the peak level in [0,1] for metering.</summary>
    public event Action<float>? LevelAvailable;

    public bool IsRecording { get; private set; }

    /// <summary>Enumerate active capture endpoints plus a loopback option per render device.</summary>
    public static List<InputDevice> EnumerateDevices()
    {
        var list = new List<InputDevice>();
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new InputDevice(d.ID, d.FriendlyName, false));
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new InputDevice(d.ID, $"{d.FriendlyName} (loopback)", true));
        return list;
    }

    public void Start(InputDevice device)
    {
        Stop();
        using var en = new MMDeviceEnumerator();
        var mm = en.GetDevice(device.Id);

        _capture = device.IsLoopback ? new WasapiLoopbackCapture(mm) : new WasapiCapture(mm);
        _format = _capture.WaveFormat;
        _buffers = new List<float>[_format.Channels];
        for (int c = 0; c < _format.Channels; c++) _buffers[c] = new List<float>();

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => IsRecording = false;
        _capture.StartRecording();
        IsRecording = true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_format == null) return;
        int ch = _format.Channels;
        float peak = 0f;

        // Interpret the raw bytes per the device format and de-interleave into float.
        var samples = ConvertToFloat(e.Buffer, e.BytesRecorded, _format);
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            _buffers[i % ch].Add(v);
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        LevelAvailable?.Invoke(peak);
    }

    private static float[] ConvertToFloat(byte[] buffer, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int n = bytes / 4;
            var outp = new float[n];
            Buffer.BlockCopy(buffer, 0, outp, 0, n * 4);
            return outp;
        }
        if (fmt.Encoding == WaveFormatEncoding.Pcm)
        {
            switch (fmt.BitsPerSample)
            {
                case 16:
                {
                    int n = bytes / 2;
                    var outp = new float[n];
                    for (int i = 0; i < n; i++)
                        outp[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                    return outp;
                }
                case 24:
                {
                    int n = bytes / 3;
                    var outp = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        int b0 = buffer[i * 3], b1 = buffer[i * 3 + 1], b2 = buffer[i * 3 + 2];
                        int v = (b2 << 16) | (b1 << 8) | b0;
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        outp[i] = v / 8_388_608f;
                    }
                    return outp;
                }
                case 32:
                {
                    int n = bytes / 4;
                    var outp = new float[n];
                    for (int i = 0; i < n; i++)
                        outp[i] = BitConverter.ToInt32(buffer, i * 4) / 2_147_483_648f;
                    return outp;
                }
            }
        }
        // Unknown format: emit silence of the right length rather than crash.
        return new float[Math.Max(0, bytes / Math.Max(1, fmt.BlockAlign)) * fmt.Channels];
    }

    /// <summary>Stop capture and hand back the recorded audio as a document, or null if empty.</summary>
    public AudioDocument? Stop()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch { /* already stopped */ }
            _capture.Dispose();
            _capture = null;
        }
        IsRecording = false;

        if (_format == null || _buffers.Length == 0 || _buffers[0].Count == 0)
            return null;

        var channels = new float[_buffers.Length][];
        for (int c = 0; c < _buffers.Length; c++) channels[c] = _buffers[c].ToArray();
        var doc = new AudioDocument(_format.SampleRate, channels);
        _buffers = Array.Empty<List<float>>();
        return doc;
    }

    public void Dispose()
    {
        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
    }
}
