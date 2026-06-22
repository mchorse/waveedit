using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using WaveEdit.Audio;
using WaveEdit.Edit;

namespace WaveEdit.UI;

public sealed class MainForm : Form
{
    private readonly WaveformView _view = new() { Dock = DockStyle.Fill };
    private readonly AudioPlayer _player = new();
    private readonly UndoStack _undo = new();
    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 30 };

    private AudioDocument _doc = AudioDocument.CreateEmpty(44100, 2);

    // clipboard (app-local; survives across documents)
    private static float[][]? _clip;
    private static int _clipRate;

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblPos = new() { Text = "—" };
    private readonly ToolStripStatusLabel _lblSel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _lblFmt = new();
    private readonly ToolStripStatusLabel _lblZoom = new();

    private ToolStripMenuItem _miUndo = null!, _miRedo = null!;

    public MainForm()
    {
        Text = "WaveEdit";
        ClientSize = new Size(1100, 620);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.FromArgb(28, 30, 34);

        BuildMenu();
        BuildToolbar();
        BuildStatusBar();

        Controls.Add(_view);
        _view.BringToFront();

        _view.SelectionChanged += UpdateStatus;
        _view.ViewChanged += UpdateStatus;
        _view.CursorMoved += UpdateStatus;
        _undo.Changed += () => { _view.ReloadPeaks(); _view.Invalidate(); UpdateUndoMenu(); UpdateStatus(); };

        _player.PlaybackStopped += () => BeginInvoke(StopPlayback);
        _playTimer.Tick += (_, _) =>
        {
            long p = _player.PositionFrames;
            if (p >= 0) { _view.SetPlayhead(p); _view.EnsureVisible(p); }
        };

        _view.SetDocument(_doc, resetView: true);
        UpdateUndoMenu();
        UpdateStatus();

        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; else { _player.Dispose(); } };
    }

    // ===================== menu / toolbar =====================

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New", Keys.Control | Keys.N, (_, _) => NewDocument()));
        file.DropDownItems.Add(Item("&Open…", Keys.Control | Keys.O, (_, _) => Open()));
        file.DropDownItems.Add(Item("&Save", Keys.Control | Keys.S, (_, _) => Save()));
        file.DropDownItems.Add(Item("Save &As…", Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Keys.Alt | Keys.F4, (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        _miUndo = Item("&Undo", Keys.Control | Keys.Z, (_, _) => { _undo.Undo(_doc); });
        _miRedo = Item("&Redo", Keys.Control | Keys.Y, (_, _) => { _undo.Redo(_doc); });
        edit.DropDownItems.Add(_miUndo);
        edit.DropDownItems.Add(_miRedo);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Cu&t", Keys.Control | Keys.X, (_, _) => Cut()));
        edit.DropDownItems.Add(Item("&Copy", Keys.Control | Keys.C, (_, _) => Copy()));
        edit.DropDownItems.Add(Item("&Paste", Keys.Control | Keys.V, (_, _) => Paste()));
        edit.DropDownItems.Add(Item("&Delete", Keys.Delete, (_, _) => Delete()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Insert &silence…", Keys.Control | Keys.Shift | Keys.I, (_, _) => InsertSilence()));
        edit.DropDownItems.Add(Item("Select &All", Keys.Control | Keys.A, (_, _) => _view.SelectAll()));
        edit.DropDownItems.Add(Item("Select &None", Keys.Control | Keys.D, (_, _) => _view.ClearSelection()));

        var process = new ToolStripMenuItem("&Process");
        process.DropDownItems.Add(Item("&Amplify / Gain…", Keys.None, (_, _) => Amplify()));
        process.DropDownItems.Add(Item("&Normalize", Keys.None, (_, _) => Process("Normalize", (c, s, l) => Dsp.Normalize(c, s, l))));
        process.DropDownItems.Add(Item("Fade &In", Keys.None, (_, _) => Process("Fade In", (c, s, l) => Dsp.FadeIn(c, s, l))));
        process.DropDownItems.Add(Item("Fade &Out", Keys.None, (_, _) => Process("Fade Out", (c, s, l) => Dsp.FadeOut(c, s, l))));
        process.DropDownItems.Add(Item("&Silence selection", Keys.None, (_, _) => Process("Silence", (c, s, l) => Dsp.Silence(c, s, l))));

        var transport = new ToolStripMenuItem("&Transport");
        transport.DropDownItems.Add(Item("&Play / Stop", Keys.None, (_, _) => TogglePlay()) /* Space handled in ProcessCmdKey */);
        transport.DropDownItems.Add(Item("Play &Selection", Keys.None, (_, _) => PlaySelection()));
        transport.DropDownItems.Add(Item("&Record…", Keys.F5, (_, _) => Record()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("Zoom &In", Keys.Control | Keys.Oemplus, (_, _) => _view.ZoomIn()));
        view.DropDownItems.Add(Item("Zoom &Out", Keys.Control | Keys.OemMinus, (_, _) => _view.ZoomOut()));
        view.DropDownItems.Add(Item("Zoom to &Selection", Keys.Control | Keys.E, (_, _) => _view.ZoomToSelection()));
        view.DropDownItems.Add(Item("Zoom to Sample &Level", Keys.None, (_, _) => _view.ZoomToSamples()));
        view.DropDownItems.Add(Item("Zoom &Full", Keys.Control | Keys.F, (_, _) => _view.ZoomFull()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&Shortcuts / About", Keys.F1, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, process, transport, view, help });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildToolbar()
    {
        var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tb.Items.Add(TbButton("Open", () => Open()));
        tb.Items.Add(TbButton("Save", () => Save()));
        tb.Items.Add(new ToolStripSeparator());
        tb.Items.Add(TbButton("Play/Stop", () => TogglePlay()));
        tb.Items.Add(TbButton("Rec", () => Record()));
        tb.Items.Add(new ToolStripSeparator());
        tb.Items.Add(TbButton("Cut", () => Cut()));
        tb.Items.Add(TbButton("Copy", () => Copy()));
        tb.Items.Add(TbButton("Paste", () => Paste()));
        tb.Items.Add(new ToolStripSeparator());
        tb.Items.Add(TbButton("Zoom +", () => _view.ZoomIn()));
        tb.Items.Add(TbButton("Zoom -", () => _view.ZoomOut()));
        tb.Items.Add(TbButton("Full", () => _view.ZoomFull()));
        tb.Items.Add(TbButton("Samples", () => _view.ZoomToSamples()));
        Controls.Add(tb);
    }

    private static ToolStripButton TbButton(string text, Action onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BuildStatusBar()
    {
        _status.Items.AddRange(new ToolStripItem[] { _lblPos, _lblSel, _lblFmt, _lblZoom });
        Controls.Add(_status);
    }

    private ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        if (keys == Keys.None) mi.ShortcutKeys = Keys.None;
        mi.Click += onClick;
        return mi;
    }

    // ===================== file ops =====================

    private void NewDocument()
    {
        if (!ConfirmDiscard()) return;
        _doc = AudioDocument.CreateEmpty(44100, 2);
        _undo.Clear();
        _view.SetDocument(_doc, resetView: true);
        UpdateTitle(); UpdateStatus();
    }

    private void Open()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = WavIO.OpenFilter, Title = "Open audio" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        LoadPath(dlg.FileName);
    }

    /// <summary>Open a file passed on the command line / via "Open with".</summary>
    public void TryOpenPath(string path)
    {
        if (!ConfirmDiscard()) return;
        LoadPath(path);
    }

    private void LoadPath(string path)
    {
        try
        {
            StopPlayback();
            _doc = WavIO.Load(path);
            _undo.Clear();
            _view.SetDocument(_doc, resetView: true);
            UpdateTitle(); UpdateStatus();
        }
        catch (Exception ex)
        {
            Error("Could not open file", ex);
        }
    }

    private bool Save()
    {
        if (string.IsNullOrEmpty(_doc.FilePath)) return SaveAs();
        try { WavIO.Save(_doc, _doc.FilePath); UpdateTitle(); return true; }
        catch (Exception ex) { Error("Could not save file", ex); return false; }
    }

    private bool SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = WavIO.SaveFilter,
            Title = "Save audio as",
            FileName = Path.GetFileName(_doc.FilePath ?? "untitled.wav"),
            FilterIndex = _doc.SaveAsFloat ? 3 : _doc.BitDepth == 24 ? 2 : 1,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        try
        {
            WavIO.SaveWithFilterIndex(_doc, dlg.FileName, dlg.FilterIndex);
            UpdateTitle();
            return true;
        }
        catch (Exception ex) { Error("Could not save file", ex); return false; }
    }

    private bool ConfirmDiscard()
    {
        if (!_doc.Modified) return true;
        var r = MessageBox.Show(this, "Discard unsaved changes?", "WaveEdit",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes) return true;
        if (r == DialogResult.No) return false;   // No = cancel the operation
        return false;
    }

    // ===================== edit ops =====================

    private bool RequireSelection()
    {
        if (_view.HasSelection) return true;
        Beep();
        _lblSel.Text = "Make a selection first (Shift + drag).";
        return false;
    }

    private void Cut()
    {
        if (!RequireSelection()) return;
        CopyToClipboard();
        StopPlayback();
        _undo.Execute(new DeleteRangeCommand(_view.SelectionStart, _view.SelectionLength), _doc);
        _view.SetCursor(_view.SelectionStart);
    }

    private void Copy()
    {
        if (!RequireSelection()) return;
        CopyToClipboard();
    }

    private void CopyToClipboard()
    {
        _clip = _doc.ExtractRange(_view.SelectionStart, _view.SelectionLength);
        _clipRate = _doc.SampleRate;
    }

    private void Paste()
    {
        if (_clip == null) { Beep(); return; }
        StopPlayback();
        long at = _view.HasSelection ? _view.SelectionStart : _view.CursorFrame;

        // if a selection is active, paste replaces it
        if (_view.HasSelection)
            _undo.Execute(new DeleteRangeCommand(_view.SelectionStart, _view.SelectionLength), _doc);

        var data = MatchChannels(_clip, _doc.ChannelCount);
        _undo.Execute(new InsertCommand(at, data, "Paste"), _doc);
        _view.SetCursor(at + data[0].LongLength);
        if (_clipRate != _doc.SampleRate)
            _lblSel.Text = $"Pasted at {_clipRate}Hz into {_doc.SampleRate}Hz (no resample).";
    }

    private static float[][] MatchChannels(float[][] src, int targetChannels)
    {
        if (src.Length == targetChannels) return src;
        var outc = new float[targetChannels][];
        for (int c = 0; c < targetChannels; c++)
            outc[c] = src[Math.Min(c, src.Length - 1)];
        return outc;
    }

    private void Delete()
    {
        if (!RequireSelection()) return;
        StopPlayback();
        long at = _view.SelectionStart;
        _undo.Execute(new DeleteRangeCommand(at, _view.SelectionLength), _doc);
        _view.SetCursor(at);
    }

    private void InsertSilence()
    {
        if (_doc.SampleRate <= 0) return;
        string? s = InputDialog.Show(this, "Insert silence", "Duration (seconds):", "1.0");
        if (s == null) return;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) || sec <= 0)
        { Beep(); return; }
        StopPlayback();
        long frames = (long)Math.Round(sec * _doc.SampleRate);
        long at = _view.HasSelection ? _view.SelectionStart : _view.CursorFrame;
        _undo.Execute(new InsertCommand(at, frames, "Insert silence"), _doc);
        _view.SetCursor(at + frames);
    }

    private void Amplify()
    {
        if (!RequireSelection()) return;
        string? s = InputDialog.Show(this, "Amplify", "Gain (dB, negative to attenuate):", "3.0");
        if (s == null) return;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
        { Beep(); return; }
        float gain = (float)Math.Pow(10, db / 20.0);
        Process($"Amplify {db:0.#}dB", (c, st, l) => Dsp.Amplify(c, st, l, gain));
    }

    private void Process(string name, Action<float[][], long, long> op)
    {
        if (!RequireSelection()) return;
        StopPlayback();
        _undo.Execute(new ProcessRangeCommand(name, _view.SelectionStart, _view.SelectionLength, op), _doc);
    }

    // ===================== transport =====================

    private void TogglePlay()
    {
        if (_player.IsPlaying) { StopPlayback(); return; }
        if (_doc.Length == 0) return;
        long start = _view.HasSelection ? _view.SelectionStart : _view.CursorFrame;
        long end = _view.HasSelection ? _view.SelectionEnd : _doc.Length;
        if (end <= start) { start = 0; end = _doc.Length; }
        _player.Play(_doc, start, end);
        _playTimer.Start();
    }

    private void PlaySelection()
    {
        if (!_view.HasSelection) { TogglePlay(); return; }
        StopPlayback();
        _player.Play(_doc, _view.SelectionStart, _view.SelectionEnd);
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        _player.Stop();
        _view.SetPlayhead(-1);
    }

    private void Record()
    {
        StopPlayback();
        using var dlg = new RecordDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            var rec = dlg.Result;
            if (_doc.Length == 0)
            {
                // empty doc -> adopt the recording outright
                if (!ConfirmDiscard()) return;
                _doc = rec;
                _undo.Clear();
                _view.SetDocument(_doc, resetView: true);
            }
            else if (rec.SampleRate == _doc.SampleRate)
            {
                var data = MatchChannels(rec.Channels, _doc.ChannelCount);
                long at = _view.HasSelection ? _view.SelectionStart : _view.CursorFrame;
                _undo.Execute(new InsertCommand(at, data, "Insert recording"), _doc);
                _view.SetCursor(at + data[0].LongLength);
            }
            else
            {
                var r = MessageBox.Show(this,
                    $"Recording is {rec.SampleRate}Hz but the document is {_doc.SampleRate}Hz.\n" +
                    "Open the recording as a new document?", "Sample rate mismatch",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                {
                    if (!ConfirmDiscard()) return;
                    _doc = rec; _undo.Clear(); _view.SetDocument(_doc, resetView: true);
                }
            }
            UpdateTitle(); UpdateStatus();
        }
    }

    // ===================== keyboard =====================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Oemplus:
            case Keys.Add: _view.ZoomIn(); return true;
            case Keys.OemMinus:
            case Keys.Subtract: _view.ZoomOut(); return true;
            case Keys.Home: _view.SetCursor(0); _view.EnsureVisible(0); return true;
            case Keys.End: _view.SetCursor(_doc.Length); _view.EnsureVisible(_doc.Length); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ===================== status / titles =====================

    private void UpdateUndoMenu()
    {
        _miUndo.Enabled = _undo.CanUndo;
        _miRedo.Enabled = _undo.CanRedo;
        _miUndo.Text = _undo.CanUndo ? $"&Undo {_undo.UndoName}" : "&Undo";
        _miRedo.Text = _undo.CanRedo ? $"&Redo {_undo.RedoName}" : "&Redo";
    }

    private void UpdateStatus()
    {
        int sr = _doc.SampleRate;
        _lblPos.Text = $"Cursor: {FormatPos(_view.CursorFrame, sr)}";
        if (_view.HasSelection)
            _lblSel.Text = $"Selection: {FormatPos(_view.SelectionStart, sr)} → {FormatPos(_view.SelectionEnd, sr)}  " +
                           $"({_view.SelectionLength} smp, {(double)_view.SelectionLength / sr:0.000}s)";
        else if (!(_lblSel.Text ?? "").StartsWith("Make") && !(_lblSel.Text ?? "").StartsWith("Pasted"))
            _lblSel.Text = "No selection";

        _lblFmt.Text = $"{sr} Hz · {_doc.ChannelCount}ch · {(_doc.SaveAsFloat ? "32f" : _doc.BitDepth + "-bit")} · {_doc.DurationSeconds:0.00}s";
        _lblZoom.Text = _view.SamplesPerPixel < 1
            ? $"{1 / _view.SamplesPerPixel:0.#} px/sample"
            : $"{_view.SamplesPerPixel:0.#} smp/px";
        UpdateTitle();
    }

    private static string FormatPos(long frame, int sr)
    {
        if (sr <= 0) return frame.ToString();
        double sec = (double)frame / sr;
        var ts = TimeSpan.FromSeconds(sec);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000} ({frame})";
    }

    private void UpdateTitle()
    {
        string name = _doc.FilePath != null ? Path.GetFileName(_doc.FilePath) : "untitled.wav";
        Text = $"{(_doc.Modified ? "*" : "")}{name} — WaveEdit";
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "WaveEdit — a native Windows audio editor\n\n" +
            "Mouse:\n" +
            "  Shift + drag   select a range\n" +
            "  Click          set cursor\n" +
            "  Wheel          zoom (Shift = scroll, Ctrl = amplitude)\n\n" +
            "Keys:\n" +
            "  Space    play / stop      F5     record\n" +
            "  + / -    zoom in / out    Home/End  go to start/end\n" +
            "  Ctrl+O/S open / save      Ctrl+Z/Y  undo / redo\n" +
            "  Ctrl+X/C/V cut/copy/paste Del    delete selection\n" +
            "  Ctrl+E zoom to selection  Ctrl+F zoom full\n",
            "About WaveEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Error(string title, Exception ex) =>
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void Beep() => System.Media.SystemSounds.Beep.Play();
}
