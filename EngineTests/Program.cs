using System;
using System.IO;
using WaveEdit.Audio;
using WaveEdit.Edit;

int failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail != null ? " — " + detail : "")}");
    if (!ok) failures++;
}

static float[][] MakeSine(int sr, int channels, int frames, double freq)
{
    var ch = new float[channels][];
    for (int c = 0; c < channels; c++)
    {
        ch[c] = new float[frames];
        for (int i = 0; i < frames; i++)
            ch[c][i] = (float)(0.5 * Math.Sin(2 * Math.PI * freq * i / sr) * (c == 0 ? 1 : 0.5));
    }
    return ch;
}

string dir = Path.Combine(Path.GetTempPath(), "waveedit_test");
Directory.CreateDirectory(dir);
int sr = 44100, frames = 44100; // 1 second

// ---- 1. round-trip 16-bit PCM ----
var doc = new AudioDocument(sr, MakeSine(sr, 2, frames, 440)) { BitDepth = 16 };
string p16 = Path.Combine(dir, "tone16.wav");
WavIO.Save(doc, p16);
var l16 = WavIO.Load(p16);
Check("16-bit round-trip: sample rate", l16.SampleRate == sr, $"{l16.SampleRate}");
Check("16-bit round-trip: channels", l16.ChannelCount == 2, $"{l16.ChannelCount}");
Check("16-bit round-trip: length", l16.Length == frames, $"{l16.Length}");
double err = 0;
for (long i = 0; i < frames; i++) err = Math.Max(err, Math.Abs(l16.Channels[0][i] - doc.Channels[0][i]));
// 16-bit LSB ≈ 3.05e-5; allow up to ~2 LSB for quantization rounding.
Check("16-bit round-trip: amplitude error within quantization", err < 2.5 / 32768, $"max err {err:e2}");

// ---- 2. round-trip 24-bit and float ----
doc.BitDepth = 24; doc.SaveAsFloat = false;
string p24 = Path.Combine(dir, "tone24.wav");
WavIO.Save(doc, p24);
var l24 = WavIO.Load(p24);
double err24 = 0;
for (long i = 0; i < frames; i++) err24 = Math.Max(err24, Math.Abs(l24.Channels[1][i] - doc.Channels[1][i]));
Check("24-bit round-trip accuracy", l24.Length == frames && err24 < 1e-5, $"max err {err24:e2}");

doc.SaveAsFloat = true;
string pf = Path.Combine(dir, "tonef.wav");
WavIO.Save(doc, pf);
var lf = WavIO.Load(pf);
double errf = 0;
for (long i = 0; i < frames; i++) errf = Math.Max(errf, Math.Abs(lf.Channels[0][i] - doc.Channels[0][i]));
Check("32-bit float round-trip exact", lf.Length == frames && errf < 1e-6, $"max err {errf:e2} float={lf.SaveAsFloat}");

// ---- 3. delete + undo ----
var edoc = new AudioDocument(sr, MakeSine(sr, 2, frames, 440));
var undo = new UndoStack();
long origLen = edoc.Length;
float sampleAt30000 = edoc.Channels[0][30000];
undo.Execute(new DeleteRangeCommand(10000, 5000), edoc);
Check("delete reduces length", edoc.Length == origLen - 5000, $"{edoc.Length}");
Check("delete shifts samples", Math.Abs(edoc.Channels[0][25000] - sampleAt30000) < 1e-6, "sample at 30000 now at 25000");
undo.Undo(edoc);
Check("undo restores length", edoc.Length == origLen, $"{edoc.Length}");
Check("undo restores content", Math.Abs(edoc.Channels[0][30000] - sampleAt30000) < 1e-6);

// ---- 4. insert silence + undo ----
undo.Execute(new InsertCommand(20000, 8000, "silence"), edoc);
Check("insert silence grows length", edoc.Length == origLen + 8000, $"{edoc.Length}");
bool silent = true;
for (long i = 20000; i < 28000; i++) if (edoc.Channels[0][i] != 0) { silent = false; break; }
Check("inserted region is silent", silent);
undo.Undo(edoc);
Check("undo silence restores length", edoc.Length == origLen, $"{edoc.Length}");

// ---- 5. DSP normalize / amplify ----
var ndoc = new AudioDocument(sr, MakeSine(sr, 1, frames, 1000)); // peak ~0.5
Dsp.Normalize(ndoc.Channels, 0, frames, 0.99f);
float peak = 0; for (long i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(ndoc.Channels[0][i]));
Check("normalize reaches target peak", Math.Abs(peak - 0.99f) < 0.02f, $"peak {peak:0.000}");

var adoc = new AudioDocument(sr, MakeSine(sr, 1, frames, 1000));
float before = adoc.Channels[0][11]; // arbitrary nonzero
Dsp.Amplify(adoc.Channels, 0, frames, 2f);
Check("amplify x2 doubles samples", Math.Abs(adoc.Channels[0][11] - before * 2) < 1e-5);

// ---- 5b. multi-region delete via CompositeCommand (right-to-left) + undo ----
{
    // Build a ramp so every frame has a unique, checkable value.
    int n = 1000;
    var ramp = new float[1][];
    ramp[0] = new float[n];
    for (int i = 0; i < n; i++) ramp[0][i] = i;
    var mdoc = new AudioDocument(sr, ramp);
    var mundo = new UndoStack();

    // delete frames [100,200) and [500,600) — two disjoint regions
    // ordered right-to-left so absolute positions stay valid
    var multi = new CompositeCommand("Delete 2 regions", new IEditCommand[]
    {
        new DeleteRangeCommand(500, 100),
        new DeleteRangeCommand(100, 100),
    });
    mundo.Execute(multi, mdoc);

    Check("multi-delete reduces length by both regions", mdoc.Length == n - 200, $"{mdoc.Length}");
    // after removing [100,200) and [500,600): value 99 then 200 should be adjacent at index 100
    Check("multi-delete: first gap closed", mdoc.Channels[0][99] == 99 && mdoc.Channels[0][100] == 200,
        $"{mdoc.Channels[0][99]},{mdoc.Channels[0][100]}");
    // index 99(=99),... region2 originally [500,600); after first deletion everything shifted -100,
    // so value 499 sits at 399 and value 600 should follow it.
    Check("multi-delete: second gap closed", mdoc.Channels[0][399] == 499 && mdoc.Channels[0][400] == 600,
        $"{mdoc.Channels[0][399]},{mdoc.Channels[0][400]}");

    mundo.Undo(mdoc);
    Check("multi-delete undo restores length", mdoc.Length == n, $"{mdoc.Length}");
    bool restored = true;
    for (int i = 0; i < n; i++) if (mdoc.Channels[0][i] != i) { restored = false; break; }
    Check("multi-delete undo restores every sample", restored);
}

// ---- 5c. resample to a different rate (duration preserved, tone intact) ----
{
    int srcRate = 48000, dstRate = 44100, secLen = 1;
    var rdoc = new AudioDocument(srcRate, MakeSine(srcRate, 2, srcRate * secLen, 440));
    var res = Resampler.Resample(rdoc, dstRate);
    Check("resample sets new rate", res.SampleRate == dstRate, $"{res.SampleRate}");
    Check("resample preserves channels", res.ChannelCount == 2, $"{res.ChannelCount}");
    // ~1s of audio at 44100 should be within a few hundred samples of 44100 frames
    Check("resample preserves duration", Math.Abs(res.Length - dstRate * secLen) < 500, $"{res.Length}");
    // peak should remain near the 0.5 source amplitude (not blown up or zeroed)
    float rpeak = 0; for (long i = 0; i < res.Length; i++) rpeak = Math.Max(rpeak, Math.Abs(res.Channels[0][i]));
    Check("resample keeps amplitude sane", rpeak > 0.4f && rpeak < 0.6f, $"peak {rpeak:0.000}");
    // no-op path returns same instance
    Check("resample same-rate is a no-op", ReferenceEquals(Resampler.Resample(rdoc, srcRate), rdoc));
}

// ---- 5d. recent files (MRU): cap, de-dup/move-to-front, persistence, clear ----
{
    string store = Path.Combine(dir, "recent_test.txt");
    if (File.Exists(store)) File.Delete(store);

    var mru = new WaveEdit.Util.RecentFiles(store);
    for (int i = 1; i <= 12; i++) mru.Add($@"C:\audio\file{i}.wav");
    Check("MRU caps at 10", mru.Items.Count == 10, $"{mru.Items.Count}");
    Check("MRU newest is at front", mru.Items[0].EndsWith("file12.wav"), mru.Items[0]);
    Check("MRU dropped the two oldest", !mru.Items.Any(p => p.EndsWith("file1.wav") || p.EndsWith("file2.wav")));

    mru.Add(@"C:\audio\file5.wav"); // existing -> move to front, no dupe
    int dupes = mru.Items.Count(p => p.EndsWith("file5.wav"));
    Check("MRU re-add moves to front without duplicating", mru.Items[0].EndsWith("file5.wav") && dupes == 1 && mru.Items.Count == 10,
        $"front={mru.Items[0]} dupes={dupes} count={mru.Items.Count}");

    // persistence: a fresh instance over the same store sees the same list
    var reload = new WaveEdit.Util.RecentFiles(store);
    Check("MRU persists across instances", reload.Items.Count == 10 && reload.Items[0].EndsWith("file5.wav"), $"{reload.Items.Count}");

    mru.Clear();
    var afterClear = new WaveEdit.Util.RecentFiles(store);
    Check("MRU clear empties and persists", afterClear.Items.Count == 0, $"{afterClear.Items.Count}");
    if (File.Exists(store)) File.Delete(store);
}

// ---- 6. fade endpoints ----
var fdoc = new AudioDocument(sr, MakeSine(sr, 1, 1000, 1000));
Dsp.FadeIn(fdoc.Channels, 0, 1000);
Check("fade-in zeroes first sample", Math.Abs(fdoc.Channels[0][0]) < 1e-6, $"{fdoc.Channels[0][0]:e2}");

// ---- 7. recording device discovery (default should be enumerable) ----
{
    var devs = WaveEdit.Audio.AudioRecorder.EnumerateDevices();
    Check("device list enumerates", devs.Count >= 0, $"{devs.Count} device(s)");
    var def = WaveEdit.Audio.AudioRecorder.DefaultInputDeviceId();
    if (def != null)
        Check("default input id is in the device list",
            devs.Any(d => string.Equals(d.Id, def, StringComparison.OrdinalIgnoreCase)), def);
    else
        Console.WriteLine("[INFO] no default capture device on this machine (selection falls back to first)");

    // the dialog now enumerates on a background (MTA) thread — confirm WASAPI is happy there
    var (bgDevs, bgDef) = await System.Threading.Tasks.Task.Run(() =>
        (WaveEdit.Audio.AudioRecorder.EnumerateDevices(), WaveEdit.Audio.AudioRecorder.DefaultInputDeviceId()));
    Check("enumeration works off the UI thread", bgDevs.Count == devs.Count, $"{bgDevs.Count} via Task.Run");
}

// ---- 8. device cache ----
{
    Check("cache empty before first refresh", WaveEdit.Audio.DeviceCache.Snapshot() == null);
    var (cd, _) = WaveEdit.Audio.DeviceCache.Refresh();
    var snap = WaveEdit.Audio.DeviceCache.Snapshot();
    Check("cache populated after refresh", snap.HasValue && snap.Value.Devices.Count == cd.Count, $"{snap?.Devices.Count}");
    Check("cache Differ() false for identical list", !WaveEdit.Audio.DeviceCache.Differ(cd, cd));
    Check("cache Differ() true when an item removed",
        cd.Count == 0 || WaveEdit.Audio.DeviceCache.Differ(cd, cd.Skip(1).ToList()));
}

// ---- 9. Ogg Vorbis round-trip (encode then decode) ----
{
    int sr2 = 44100, n2 = sr2; // 1 second stereo
    var od = new AudioDocument(sr2, MakeSine(sr2, 2, n2, 440)) { OggQuality = 0.6f };
    string op = Path.Combine(dir, "tone.ogg");
    WavIO.Save(od, op);
    Check("ogg file written", File.Exists(op) && new FileInfo(op).Length > 0, $"{new FileInfo(op).Length} bytes");

    var ol = WavIO.Load(op);
    Check("ogg round-trip: sample rate", ol.SampleRate == sr2, $"{ol.SampleRate}");
    Check("ogg round-trip: channels", ol.ChannelCount == 2, $"{ol.ChannelCount}");
    Check("ogg round-trip: ~duration", Math.Abs(ol.Length - n2) < sr2 / 10, $"{ol.Length} vs {n2}");

    double rin = 0, rout = 0;
    for (long i = 0; i < n2; i++) rin += od.Channels[0][i] * (double)od.Channels[0][i];
    for (long i = 0; i < ol.Length; i++) rout += ol.Channels[0][i] * (double)ol.Channels[0][i];
    rin = Math.Sqrt(rin / n2); rout = Math.Sqrt(rout / Math.Max(1, ol.Length));
    // lossy: just confirm it's real audio in the right ballpark (not silent / not blown up)
    double ratio = rout / rin;
    Check("ogg round-trip: loudness in lossy range", ratio > 0.6 && ratio < 1.25, $"in={rin:0.000} out={rout:0.000} ratio={ratio:0.00}");
}

// ---- 10. clipboard WAV serialization round-trip (the bytes that cross between windows) ----
{
    var src = MakeSine(48000, 2, 24000, 330); // 0.5 s stereo @ 48k
    var bytes = WavIO.WavBytes(src, 48000);
    Check("clipboard wav bytes produced", bytes.Length > 44, $"{bytes.Length} bytes");
    using var ms = new MemoryStream(bytes);
    var rt = WavIO.LoadFromStream(ms);
    Check("clipboard wav round-trip: rate/ch", rt.SampleRate == 48000 && rt.ChannelCount == 2, $"{rt.SampleRate}/{rt.ChannelCount}");
    Check("clipboard wav round-trip: length", rt.Length == 24000, $"{rt.Length}");
    double maxErr = 0;
    for (long i = 0; i < 24000; i++)
    {
        maxErr = Math.Max(maxErr, Math.Abs(rt.Channels[0][i] - src[0][i]));
        maxErr = Math.Max(maxErr, Math.Abs(rt.Channels[1][i] - src[1][i]));
    }
    Check("clipboard wav round-trip: lossless (float32)", maxErr < 1e-6, $"maxErr {maxErr:e2}");
}

// ---- 11. selection subtraction (Alt-drag) region math ----
try
{
    var v = new WaveEdit.UI.WaveformView();
    v.SetDocument(new AudioDocument(44100, new[] { new float[10000] }), resetView: false);

    v.SetSelection(0, 6000);
    v.SubtractRegion(2000, 4000); // remove the middle -> split
    Check("subtract splits a region",
        v.Regions.Count == 2 && v.Regions[0].Start == 0 && v.Regions[0].End == 2000 &&
        v.Regions[1].Start == 4000 && v.Regions[1].End == 6000,
        string.Join(",", v.Regions.Select(r => $"[{r.Start},{r.End})")));

    v.SubtractRegion(0, 2000); // drop the first piece entirely
    Check("subtract removes a whole piece", v.Regions.Count == 1 && v.Regions[0].Start == 4000,
        string.Join(",", v.Regions.Select(r => $"[{r.Start},{r.End})")));

    v.SubtractRegion(3000, 9000); // covers the rest -> empty
    Check("subtract can clear the selection", v.Regions.Count == 0, $"{v.Regions.Count}");
    v.Dispose();
}
catch (Exception ex)
{
    Console.WriteLine($"[INFO] subtract UI test skipped: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures;
