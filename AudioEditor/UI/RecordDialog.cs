using System;
using System.Drawing;
using System.Windows.Forms;
using WaveEdit.Audio;

namespace WaveEdit.UI;

/// <summary>
/// Modal recorder: pick an input endpoint, record with a live peak meter, and return
/// the captured audio as a new <see cref="AudioDocument"/>.
/// </summary>
public sealed class RecordDialog : Form
{
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _record = new() { Text = "● Record" };
    private readonly Button _stop = new() { Text = "■ Stop", Enabled = false };
    private readonly Button _ok = new() { Text = "Use Recording", Enabled = false, DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { Text = "Idle", AutoSize = true };
    private readonly LevelMeter _meter = new();
    private readonly System.Windows.Forms.Timer _decay = new() { Interval = 50 };

    private readonly AudioRecorder _recorder = new();
    private float _level;
    private DateTime _started;

    public AudioDocument? Result { get; private set; }

    public RecordDialog()
    {
        Text = "Record";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(460, 210);

        var lblDev = new Label { Text = "Input device:", AutoSize = true, Location = new Point(12, 15) };
        _devices.SetBounds(12, 35, 436, 24);

        _meter.SetBounds(12, 70, 436, 26);

        _record.SetBounds(12, 110, 120, 34);
        _stop.SetBounds(140, 110, 120, 34);
        _status.Location = new Point(275, 120);

        _ok.SetBounds(248, 160, 120, 34);
        _cancel.SetBounds(376, 160, 72, 34);

        Controls.AddRange(new Control[] { lblDev, _devices, _meter, _record, _stop, _status, _ok, _cancel });
        AcceptButton = _ok; CancelButton = _cancel;

        Load += (_, _) => PopulateDevices();
        _record.Click += (_, _) => StartRecording();
        _stop.Click += (_, _) => StopRecording();

        _recorder.LevelAvailable += lvl => _level = Math.Max(_level, lvl);
        _decay.Tick += (_, _) =>
        {
            _meter.Level = _level;
            _level *= 0.6f; // visual decay
            if (_recorder.IsRecording)
                _status.Text = $"Recording… {(DateTime.UtcNow - _started).TotalSeconds:0.0}s";
        };
        _decay.Start();

        FormClosing += (_, _) => { _recorder.Dispose(); _decay.Dispose(); };
    }

    private void PopulateDevices()
    {
        _devices.Items.Clear();
        try
        {
            foreach (var d in AudioRecorder.EnumerateDevices()) _devices.Items.Add(d);
            if (_devices.Items.Count > 0) _devices.SelectedIndex = 0;
            else _status.Text = "No input devices found";
        }
        catch (Exception ex)
        {
            _status.Text = "Enumeration failed";
            MessageBox.Show(this, ex.Message, "Device error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StartRecording()
    {
        if (_devices.SelectedItem is not InputDevice dev) return;
        try
        {
            Result = null;
            _recorder.Start(dev);
            _started = DateTime.UtcNow;
            _record.Enabled = false; _stop.Enabled = true; _ok.Enabled = false;
            _devices.Enabled = false;
            _status.Text = "Recording…";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Recording error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ResetButtons();
        }
    }

    private void StopRecording()
    {
        Result = _recorder.Stop();
        ResetButtons();
        if (Result != null)
            _status.Text = $"Captured {Result.DurationSeconds:0.00}s, {Result.ChannelCount}ch @ {Result.SampleRate}Hz";
        else
            _status.Text = "Nothing captured";
        _ok.Enabled = Result != null;
    }

    private void ResetButtons()
    {
        _record.Enabled = true; _stop.Enabled = false; _devices.Enabled = true;
    }
}

/// <summary>Simple horizontal peak meter (green→yellow→red).</summary>
internal sealed class LevelMeter : Control
{
    private float _level;
    public float Level { get => _level; set { _level = Math.Clamp(value, 0, 1); Invalidate(); } }

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(20, 22, 25));
        int w = Width, h = Height;
        int filled = (int)(w * _level);
        for (int x = 0; x < filled; x++)
        {
            float t = (float)x / w;
            Color col = t < 0.7f ? Color.FromArgb(80, 210, 90)
                      : t < 0.9f ? Color.FromArgb(230, 210, 70)
                                 : Color.FromArgb(230, 70, 60);
            using var p = new Pen(col);
            g.DrawLine(p, x, 2, x, h - 2);
        }
        using var border = new Pen(Color.FromArgb(70, 74, 80));
        g.DrawRectangle(border, 0, 0, w - 1, h - 1);
    }
}
