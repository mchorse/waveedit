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

// ---- 6. fade endpoints ----
var fdoc = new AudioDocument(sr, MakeSine(sr, 1, 1000, 1000));
Dsp.FadeIn(fdoc.Channels, 0, 1000);
Check("fade-in zeroes first sample", Math.Abs(fdoc.Channels[0][0]) < 1e-6, $"{fdoc.Channels[0][0]:e2}");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures;
