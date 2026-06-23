using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using WaveEdit.Audio;
using WaveEdit.Edit;
using WaveEdit.Util;

namespace WaveEdit.UI;

public sealed class MainForm : Form
{
    private readonly WaveformView _view = new() { Dock = DockStyle.Fill };
    private readonly AudioPlayer _player = new();
    private readonly UndoStack _undo = new();
    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 30 };

    private AudioDocument _doc = AudioDocument.CreateEmpty(44100, 2);

    // shared dark-UI palette for the menu / status bar
    private static readonly Color UiBack = Color.FromArgb(40, 43, 48);
    private static readonly Color UiText = Color.FromArgb(214, 218, 222);
    private static readonly Color UiTextDim = Color.FromArgb(150, 155, 160);

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblPos = new() { Text = "—" };
    private readonly ToolStripStatusLabel _lblSel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _lblFmt = new();
    private readonly ToolStripStatusLabel _lblSpeed = new();
    private readonly ToolStripStatusLabel _lblZoom = new();

    private ToolStripMenuItem _miUndo = null!, _miRedo = null!;
    private ToolStripMenuItem _recentMenu = null!;
    private readonly RecentFiles _recent = new();

    // playback speed (varispeed — pitch shifts with speed)
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0, 5.0 };
    private int _speedIndex = 3; // 1.0×
    private ToolStripMenuItem[] _speedItems = Array.Empty<ToolStripMenuItem>();

    public MainForm()
    {
        Text = "WaveEdit";
        LoadAppIcon();
        ClientSize = new Size(1100, 620);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.FromArgb(28, 30, 34);

        // dark, readable rendering for menus, dropdowns and the status bar
        ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable())
        {
            RoundedEdges = false,
        };

        BuildMenu();
        BuildStatusBar();

        Controls.Add(_view);
        _view.BringToFront();

        _view.SelectionChanged += UpdateStatus;
        _view.ViewChanged += UpdateStatus;
        _view.CursorMoved += UpdateStatus;
        // Moving the cursor during playback seeks there (click or drag-scrub).
        _view.CursorMoved += () => { if (_player.IsPlaying) { _player.Seek(_view.CursorFrame); _view.SetPlayhead(_view.CursorFrame); } };
        _undo.Changed += () => { _view.ReloadPeaks(); _view.Invalidate(); UpdateUndoMenu(); UpdateStatus(); };

        _player.PlaybackStopped += () => BeginInvoke(StopPlayback);
        _playTimer.Tick += (_, _) =>
        {
            long p = _player.PositionFrames;
            if (p >= 0) { _view.SetPlayhead(p); _view.EnsureVisible(p); }
        };

        _view.SetDocument(_doc, resetView: true);
        UpdateUndoMenu();
        ApplySpeed();
        UpdateStatus();

        EnableFileDrop();

        // Warm the recording-device cache in the background so the first F5 is instant.
        System.Threading.Tasks.Task.Run(Audio.DeviceCache.Prime);

        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; else { _player.Dispose(); } };
    }

    // ===================== drag & drop =====================

    private void EnableFileDrop()
    {
        // The waveform view is docked Fill, so it sits on top of the form and
        // receives the drop; the form covers the menu/toolbar/status strips.
        foreach (Control c in new Control[] { this, _view })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }
    }

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        string? path = TryGetDroppedFile(e);
        if (path == null) return;
        Activate();

        // Empty document -> just load it.
        if (_doc.Length == 0)
        {
            TryOpenPath(path);
            return;
        }

        // Otherwise let the user choose what to do with the dropped file.
        var page = new TaskDialogPage
        {
            Caption = "WaveEdit",
            Heading = "Add dropped file",
            Text = $"\"{Path.GetFileName(path)}\"\n\nOpen it in a new window, or insert it into this document at the playhead?",
            Icon = TaskDialogIcon.Information,
        };
        var insert = new TaskDialogButton("Insert at playhead");
        var newWindow = new TaskDialogButton("Open in new window");
        page.Buttons.Add(insert);
        page.Buttons.Add(newWindow);
        page.Buttons.Add(TaskDialogButton.Cancel);
        page.DefaultButton = insert;

        var result = TaskDialog.ShowDialog(this, page);
        if (result == newWindow) LaunchNewInstance(path);
        else if (result == insert) InsertFileAtPlayhead(path);
    }

    /// <summary>Load a file, conform it to this document, and insert it at the cursor (selecting it).</summary>
    private void InsertFileAtPlayhead(string path)
    {
        AudioDocument incoming;
        try { incoming = WavIO.Load(path); }
        catch (Exception ex) { Error("Could not open file", ex); return; }
        if (incoming.Length == 0) { Beep(); return; }

        StopPlayback();
        int srcRate = incoming.SampleRate;
        var conformed = Resampler.Resample(incoming, _doc.SampleRate);   // match sample rate
        var data = MatchChannels(conformed.Channels, _doc.ChannelCount); // mono<->stereo
        long at = _view.CursorFrame;
        _undo.Execute(new InsertCommand(at, data, "Insert file"), _doc);
        _view.SetSelection(at, at + data[0].LongLength);                 // deselect + select pasted region
        if (srcRate != _doc.SampleRate)
            _lblSel.Text = $"Inserted (resampled {srcRate} → {_doc.SampleRate} Hz).";
    }

    /// <summary>Start another copy of WaveEdit with a file to open.</summary>
    private void LaunchNewInstance(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Error("Could not open a new window", ex);
        }
    }

    /// <summary>Return the first dropped path with a supported audio extension, or null.</summary>
    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        foreach (var f in files)
            if (File.Exists(f) && IsSupportedAudio(f))
                return f;
        return null;
    }

    private static bool IsSupportedAudio(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".aiff" or ".aif" or ".wma" or ".flac";
    }

    // ===================== menu / toolbar =====================

    private void BuildMenu()
    {
        var menu = new MenuStrip { BackColor = UiBack, ForeColor = UiText };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New", Keys.Control | Keys.N, (_, _) => NewDocument()));
        file.DropDownItems.Add(Item("&Open…", Keys.Control | Keys.O, (_, _) => Open()));
        file.DropDownItems.Add(Item("&Save", Keys.Control | Keys.S, (_, _) => Save()));
        file.DropDownItems.Add(Item("Save &As…", Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _recentMenu = new ToolStripMenuItem("Recent &Files") { ForeColor = UiText };
        file.DropDownItems.Add(_recentMenu);
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
        edit.DropDownItems.Add(Item("&Deselect All", Keys.Control | Keys.D, (_, _) => _view.ClearSelection()));

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
        transport.DropDownItems.Add(new ToolStripSeparator());
        transport.DropDownItems.Add(BuildSpeedMenu());

        var view = new ToolStripMenuItem("&View");
        // Plain +/- zoom (handled in ProcessCmdKey); Ctrl +/- is reserved for playback speed.
        var zin = Item("Zoom &In", Keys.None, (_, _) => _view.ZoomIn()); zin.ShortcutKeyDisplayString = "+";
        var zout = Item("Zoom &Out", Keys.None, (_, _) => _view.ZoomOut()); zout.ShortcutKeyDisplayString = "-";
        view.DropDownItems.Add(zin);
        view.DropDownItems.Add(zout);
        view.DropDownItems.Add(Item("Zoom to &Selection", Keys.Control | Keys.E, (_, _) => _view.ZoomToSelection()));
        view.DropDownItems.Add(Item("Zoom to Sample &Level", Keys.None, (_, _) => _view.ZoomToSamples()));
        view.DropDownItems.Add(Item("Zoom &Full", Keys.Control | Keys.F, (_, _) => _view.ZoomFull()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&Shortcuts / About", Keys.F1, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, process, transport, view, help });
        foreach (ToolStripMenuItem top in menu.Items) ColorMenuTree(top);
        MainMenuStrip = menu;
        Controls.Add(menu);
        RebuildRecentMenu();
    }

    // ===================== recent files =====================

    private void RebuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();

        if (_recent.Items.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(no recent files)")
            {
                Enabled = false,
                ForeColor = UiTextDim,
            });
            return;
        }

        int i = 1;
        foreach (var path in _recent.Items)
        {
            string local = path; // capture per-iteration
            var mi = new ToolStripMenuItem($"&{i} {Path.GetFileName(path)}")
            {
                ToolTipText = path,
                ForeColor = UiText,
            };
            mi.Click += (_, _) => OpenRecent(local);
            _recentMenu.DropDownItems.Add(mi);
            i++;
        }

        _recentMenu.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("&Clear Recent Files") { ForeColor = UiText };
        clear.Click += (_, _) => { _recent.Clear(); RebuildRecentMenu(); };
        _recentMenu.DropDownItems.Add(clear);
    }

    private void AddRecent(string path)
    {
        _recent.Add(path);
        RebuildRecentMenu();
    }

    private void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            var r = MessageBox.Show(this,
                $"\"{path}\" no longer exists.\nRemove it from the recent list?",
                "File not found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) { _recent.Remove(path); RebuildRecentMenu(); }
            return;
        }
        if (!ConfirmDiscard()) return;
        LoadPath(path);
    }

    /// <summary>Force light text on every menu item and submenu (they default to near-black).</summary>
    private static void ColorMenuTree(ToolStripMenuItem item)
    {
        item.ForeColor = UiText;
        foreach (ToolStripItem sub in item.DropDownItems)
        {
            if (sub is ToolStripMenuItem mi) ColorMenuTree(mi);
            else sub.ForeColor = UiTextDim; // separators etc.
        }
    }

    private void BuildStatusBar()
    {
        _status.BackColor = UiBack;
        _status.ForeColor = UiText;
        foreach (var lbl in new[] { _lblPos, _lblSel, _lblFmt, _lblSpeed, _lblZoom })
            lbl.ForeColor = UiText;
        _status.Items.AddRange(new ToolStripItem[] { _lblPos, _lblSel, _lblFmt, _lblSpeed, _lblZoom });
        Controls.Add(_status);
    }

    private ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeys = keys, ForeColor = UiText };
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
            AddRecent(path);
            // The previous document and any load scratch are now garbage; hand the
            // pages back so the working set reflects the new file, not the peak.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            Error("Could not open file", ex);
        }
    }

    private bool Save()
    {
        if (string.IsNullOrEmpty(_doc.FilePath)) return SaveAs();
        try { WavIO.Save(_doc, _doc.FilePath); UpdateTitle(); AddRecent(_doc.FilePath); return true; }
        catch (Exception ex) { Error("Could not save file", ex); return false; }
    }

    private bool SaveAs()
    {
        bool isOgg = _doc.FilePath != null &&
                     Path.GetExtension(_doc.FilePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        using var dlg = new SaveFileDialog
        {
            Filter = WavIO.SaveFilter,
            Title = "Save audio as",
            FileName = Path.GetFileName(_doc.FilePath ?? "untitled.wav"),
            FilterIndex = isOgg ? 4 : _doc.SaveAsFloat ? 3 : _doc.BitDepth == 24 ? 2 : 1,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;

        // Ogg Vorbis is lossy and quality-based (no bit depth) — ask for a quality.
        if (Path.GetExtension(dlg.FileName).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            string? q = InputDialog.Show(this, "Ogg Vorbis quality",
                "Quality 0.0 - 1.0 (higher = better, larger file):",
                _doc.OggQuality.ToString("0.0", CultureInfo.InvariantCulture));
            if (q == null) return false;
            if (double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double qv))
                _doc.OggQuality = (float)Math.Clamp(qv, 0.0, 1.0);
        }

        try
        {
            WavIO.SaveWithFilterIndex(_doc, dlg.FileName, dlg.FilterIndex);
            UpdateTitle();
            AddRecent(dlg.FileName);
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
        _lblSel.Text = "Make a selection first (Shift + drag adds more regions).";
        return false;
    }

    /// <summary>Selected regions sorted ascending by start (snapshot copy).</summary>
    private SelRegion[] Regions()
    {
        var src = _view.Regions;
        var arr = new SelRegion[src.Count];
        for (int i = 0; i < src.Count; i++) arr[i] = src[i];
        return arr;
    }

    /// <summary>One delete command per region, ordered right-to-left so positions stay valid.</summary>
    private static IEditCommand BuildMultiDelete(SelRegion[] regions, string name)
    {
        var cmds = new IEditCommand[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            var r = regions[regions.Length - 1 - i]; // descending start
            cmds[i] = new DeleteRangeCommand(r.Start, r.Length);
        }
        return new CompositeCommand(name, cmds);
    }

    private void Cut()
    {
        if (!RequireSelection()) return;
        CopyToClipboard();
        StopPlayback();
        var regions = Regions();
        long at = regions[0].Start;
        _undo.Execute(BuildMultiDelete(regions, regions.Length > 1 ? $"Cut {regions.Length} regions" : "Cut"), _doc);
        _view.SetCursor(at);
    }

    private void Copy()
    {
        if (!RequireSelection()) return;
        CopyToClipboard();
    }

    private void CopyToClipboard()
    {
        // concatenate every selected region and put it on the system clipboard so it
        // can be pasted into other WaveEdit windows (or other audio apps).
        var data = ConcatRegions(Regions());
        try { AudioClipboard.SetAudio(data, _doc.SampleRate); }
        catch (Exception ex) { Error("Could not copy to clipboard", ex); }
    }

    private float[][] ConcatRegions(SelRegion[] regions)
    {
        long total = 0;
        foreach (var r in regions) total += r.Length;
        var outc = new float[_doc.ChannelCount][];
        for (int c = 0; c < _doc.ChannelCount; c++) outc[c] = new float[total];
        long pos = 0;
        foreach (var r in regions)
        {
            for (int c = 0; c < _doc.ChannelCount; c++)
                Array.Copy(_doc.Channels[c], r.Start, outc[c], pos, r.Length);
            pos += r.Length;
        }
        return outc;
    }

    private void Paste()
    {
        var clip = AudioClipboard.TryGetAudio();
        if (clip == null) { Beep(); return; }
        StopPlayback();

        // Conform clipboard audio to this document: resample to its rate, then match channels.
        int clipRate = clip.SampleRate;
        var conformed = Resampler.Resample(clip, _doc.SampleRate);
        var data = MatchChannels(conformed.Channels, _doc.ChannelCount);
        long clipLen = data[0].LongLength;

        long pasteAt;
        if (_view.HasSelection)
        {
            // replace the selection: delete all regions, then insert at the leftmost start
            var regions = Regions();
            pasteAt = regions[0].Start;
            var del = BuildMultiDelete(regions, "Paste");
            var ins = new InsertCommand(pasteAt, data, "Paste");
            _undo.Execute(new CompositeCommand("Paste", new[] { del, ins }), _doc);
        }
        else
        {
            pasteAt = _view.CursorFrame;
            _undo.Execute(new InsertCommand(pasteAt, data, "Paste"), _doc);
        }

        // Clear the old selection and mark the freshly pasted region instead.
        _view.SetSelection(pasteAt, pasteAt + clipLen);

        if (clipRate != _doc.SampleRate)
            _lblSel.Text = $"Pasted (resampled {clipRate} → {_doc.SampleRate} Hz).";
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
        var regions = Regions();
        long at = regions[0].Start;
        _undo.Execute(BuildMultiDelete(regions, regions.Length > 1 ? $"Delete {regions.Length} regions" : "Delete"), _doc);
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

    /// <summary>Apply an in-place processor to every selected region as a single undo step.</summary>
    private void Process(string name, Action<float[][], long, long> op)
    {
        if (!RequireSelection()) return;
        StopPlayback();
        var regions = Regions();
        if (regions.Length == 1)
        {
            _undo.Execute(new ProcessRangeCommand(name, regions[0].Start, regions[0].Length, op), _doc);
        }
        else
        {
            var cmds = new IEditCommand[regions.Length];
            for (int i = 0; i < regions.Length; i++)
                cmds[i] = new ProcessRangeCommand(name, regions[i].Start, regions[i].Length, op);
            _undo.Execute(new CompositeCommand($"{name} ({regions.Length} regions)", cmds), _doc);
        }
    }

    // ===================== playback speed =====================

    private ToolStripMenuItem BuildSpeedMenu()
    {
        var menu = new ToolStripMenuItem("&Speed") { ForeColor = UiText };
        _speedItems = new ToolStripMenuItem[Speeds.Length];
        for (int i = 0; i < Speeds.Length; i++)
        {
            int idx = i;
            var mi = new ToolStripMenuItem(FormatSpeed(Speeds[i])) { ForeColor = UiText };
            mi.Click += (_, _) => SetSpeedIndex(idx);
            _speedItems[i] = mi;
            menu.DropDownItems.Add(mi);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        var faster = Item("Faster", Keys.None, (_, _) => ChangeSpeed(+1));
        faster.ShortcutKeyDisplayString = "Ctrl + =";
        var slower = Item("Slower", Keys.None, (_, _) => ChangeSpeed(-1));
        slower.ShortcutKeyDisplayString = "Ctrl + -";
        menu.DropDownItems.Add(faster);
        menu.DropDownItems.Add(slower);
        return menu;
    }

    private static string FormatSpeed(double s) => $"{s:0.##}×";

    private void ChangeSpeed(int delta) => SetSpeedIndex(_speedIndex + delta);

    private void SetSpeedIndex(int idx)
    {
        idx = Math.Clamp(idx, 0, Speeds.Length - 1);
        if (idx == _speedIndex && _speedItems.Length > 0 && _speedItems[idx].Checked) return;
        _speedIndex = idx;
        ApplySpeed();
    }

    private void ApplySpeed()
    {
        double speed = Speeds[_speedIndex];
        _player.Speed = speed;                       // live if currently playing
        for (int i = 0; i < _speedItems.Length; i++)
            _speedItems[i].Checked = (i == _speedIndex);
        _lblSpeed.Text = $"Speed: {FormatSpeed(speed)}";
    }

    // ===================== transport =====================

    private void TogglePlay()
    {
        if (_player.IsPlaying) { StopPlayback(); return; }
        if (_doc.Length == 0) return;
        // Always play from the cursor (playhead) to the end of the file.
        // Use Play Selection (Transport menu) to audition the selection.
        long start = _view.CursorFrame;
        if (start >= _doc.Length) start = 0;
        _player.Play(_doc, start, _doc.Length);
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
                // empty doc -> adopt the recording outright (keeps its native rate)
                if (!ConfirmDiscard()) return;
                _doc = rec;
                _undo.Clear();
                _view.SetDocument(_doc, resetView: true);
            }
            else
            {
                // conform the recording to the current document: resample to match the
                // document's rate, then up/down-mix channels, then insert at the cursor.
                int srcRate = rec.SampleRate;
                var conformed = Resampler.Resample(rec, _doc.SampleRate);
                var data = MatchChannels(conformed.Channels, _doc.ChannelCount);
                long at = _view.HasSelection ? _view.SelectionStart : _view.CursorFrame;
                _undo.Execute(new InsertCommand(at, data, "Insert recording"), _doc);
                _view.SetCursor(at + data[0].LongLength);
                if (srcRate != _doc.SampleRate)
                    _lblSel.Text = $"Recording resampled {srcRate} → {_doc.SampleRate} Hz and inserted.";
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
            // Ctrl +/- changes playback speed (checked before plain +/- below)
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add: ChangeSpeed(+1); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract: ChangeSpeed(-1); return true;
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
        {
            long total = _view.TotalSelectedFrames;
            string span = $"{FormatPos(_view.SelectionStart, sr)} → {FormatPos(_view.SelectionEnd, sr)}";
            _lblSel.Text = _view.RegionCount > 1
                ? $"{_view.RegionCount} regions: {span}  ({total} smp total, {(double)total / sr:0.000}s)"
                : $"Selection: {span}  ({total} smp, {(double)total / sr:0.000}s)";
        }
        else if (!(_lblSel.Text ?? "").StartsWith("Make") && !(_lblSel.Text ?? "").StartsWith("Pasted"))
            _lblSel.Text = "No selection";

        bool isOgg = _doc.FilePath != null &&
                     Path.GetExtension(_doc.FilePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        string fmt = isOgg ? $"OGG q{_doc.OggQuality:0.0}"
                           : _doc.SaveAsFloat ? "32f" : _doc.BitDepth + "-bit";
        _lblFmt.Text = $"{sr} Hz · {_doc.ChannelCount}ch · {fmt} · {_doc.DurationSeconds:0.00}s";
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
            "  Shift + drag   select / add a region (multi-select)\n" +
            "  Click / drag   move the cursor (selection unchanged)\n" +
            "  Ctrl + D       deselect everything\n" +
            "  Middle drag    pan the timeline\n" +
            "  Wheel          zoom (Shift = scroll, Ctrl = amplitude)\n\n" +
            "Keys:\n" +
            "  Space    play / stop      F5     record\n" +
            "  + / -    zoom in / out    Home/End  go to start/end\n" +
            "  Ctrl + / Ctrl -  playback speed (0.25×…5×, pitch shifts)\n" +
            "  Ctrl+O/S open / save      Ctrl+Z/Y  undo / redo\n" +
            "  Ctrl+X/C/V cut/copy/paste Del    delete selection\n" +
            "  Ctrl+E zoom to selection  Ctrl+F zoom full\n",
            "About WaveEdit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Error(string title, Exception ex) =>
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void Beep() => System.Media.SystemSounds.Beep.Play();

    private void LoadAppIcon()
    {
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("WaveEdit.icon.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { /* fall back to default icon */ }
    }
}

/// <summary>Dark color scheme for the menu / status ToolStrips (hover, dropdown, borders).</summary>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Bar = Color.FromArgb(40, 43, 48);
    private static readonly Color Drop = Color.FromArgb(46, 49, 55);
    private static readonly Color Hover = Color.FromArgb(64, 70, 80);
    private static readonly Color HoverEdge = Color.FromArgb(90, 98, 110);
    private static readonly Color Sep = Color.FromArgb(70, 74, 80);

    public DarkColorTable() => UseSystemColors = false;

    // top menu bar
    public override Color MenuStripGradientBegin => Bar;
    public override Color MenuStripGradientEnd => Bar;
    public override Color ToolStripGradientBegin => Bar;
    public override Color ToolStripGradientMiddle => Bar;
    public override Color ToolStripGradientEnd => Bar;

    // hovered top-level item
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemBorder => HoverEdge;
    public override Color MenuBorder => Sep;

    // pressed (open) top-level item
    public override Color MenuItemPressedGradientBegin => Drop;
    public override Color MenuItemPressedGradientMiddle => Drop;
    public override Color MenuItemPressedGradientEnd => Drop;

    // dropdown body + left image margin
    public override Color ToolStripDropDownBackground => Drop;
    public override Color ImageMarginGradientBegin => Drop;
    public override Color ImageMarginGradientMiddle => Drop;
    public override Color ImageMarginGradientEnd => Drop;

    // separators
    public override Color SeparatorDark => Sep;
    public override Color SeparatorLight => Sep;
}
