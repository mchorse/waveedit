# WaveEdit

A native **Windows** audio editor written in C# (.NET 8 / WinForms + NAudio). Single-window,
no project files — open a `.wav`, edit samples directly, save back out.

![status](https://img.shields.io/badge/platform-Windows-blue) ![net](https://img.shields.io/badge/.NET-8.0-512BD4)

## Features

| # | Capability | Notes |
|---|------------|-------|
| 1 | **Open / Save WAV + OGG** | `Ctrl+O` / `Ctrl+S`, native file dialogs. Reads WAV PCM 16/24/32 & float, **Ogg Vorbis**, plus MP3/AIFF/WMA/FLAC decode. Saves WAV (PCM 16/24, 32-bit float) and **Ogg Vorbis** (lossy, quality 0–1). |
| 2 | **Playback** | Mono or stereo, **any** sample rate. `Space` plays from the cursor to the end (toggles stop). *Transport ▸ Play Selection* auditions the selected region instead. |
| 3 | **Multi-region selection** | **Shift + drag** selects a range and adds more disjoint regions; **Alt + drag** subtracts a range (splitting/trimming regions); plain click/drag only moves the cursor and never alters the selection. **Ctrl + D** deselects all. Cut/copy/paste and all effects apply to every region at once. Sample-accurate. |
| 4 | **Cut** | `Ctrl+X` / `Del` removes the selected waveform (with undo). |
| 5 | **Insert silence** | `Ctrl+Shift+I`, prompts for a duration in seconds. |
| 6 | **Zoom to individual samples** | Mouse wheel zooms from whole-file down to ~64 px per sample, drawing sample dots and stems. |
| 7 | **Native dialogs** | Standard Win32 open/save. |
| 8 | **Recording** | `F5` — pick any active input endpoint (mic, line-in, or render **loopback**) via WASAPI, with a live level meter. The dialog **remembers your last-picked device** (system default mic the first time). Into an existing document, the take is **auto-resampled** to the document's rate and channel-matched, then inserted at the cursor. Into an empty document it keeps its native rate. |
| 9 | **Windows only** | Targets `net8.0-windows`, WinForms, WASAPI/WaveOut. |

### Extras included
- **Undo / redo** (`Ctrl+Z` / `Ctrl+Y`) with named history — memory-efficient (each command stores only its own inverse, not a full-file snapshot).
- **Copy / paste** of audio regions (`Ctrl+C` / `Ctrl+V`) via the **Windows clipboard** (standard `WaveAudio`/CF_WAVE), so it works **across WaveEdit windows** and to/from other audio apps. Lossless (32-bit float), auto-resampled to the target document's rate; paste replaces the active selection.
- **Processing** (Process menu): Amplify/Gain (dB), Normalize, Fade In, Fade Out, Silence selection.
- **Live playhead** that follows playback and auto-scrolls.
- **Playback speed** (`Ctrl + =` / `Ctrl + -`, or *Transport ▸ Speed*): 0.25× … 5× (0.25/0.5/0.75/1/1.25/1.5/1.75/2/3/5), adjustable live during playback. Varispeed — pitch shifts with speed (no pitch-preserving time-stretch).
- **Waveform display**: two-tone envelope (darker peak min/max + a lighter **average/RMS** core when zoomed out), faint dashed **±1.0 full-scale** guides per channel, and per-channel clipping so peaks never spill into the ruler or the neighbouring channel.
- **Status bar**: cursor position (time + sample index), selection length, format, and zoom level.
- **Drag & drop**: drop an audio file onto an empty document to load it. Drop onto a document that already has audio and you're asked whether to **open it in a new window** or **insert it at the playhead** (auto-resampled, channel-matched, with the inserted region selected).
- **Open-with**: pass a file path on the command line (or drop a file on the `.exe`).

## Build & run

```powershell
# from the repo root
dotnet build AudioEditor.sln -c Release
dotnet run  --project AudioEditor/AudioEditor.csproj
```

The output binary is `AudioEditor/bin/Release/net8.0-windows/WaveEdit.exe`.

### Packaging scripts (`scripts/`)

All are no-admin and self-contained. Run from the repo root:

```powershell
# Portable release zip for itch.io -> dist\WaveEdit-portable-win-x64.zip
powershell -ExecutionPolicy Bypass -File scripts\Build-Portable.ps1

# Install (self-contained) to %LocalAppData%\Programs\WaveEdit and register the .wav association
powershell -ExecutionPolicy Bypass -File scripts\Publish-WaveEdit.ps1

# Start Menu + Desktop shortcuts to the installed copy
powershell -ExecutionPolicy Bypass -File scripts\Create-Shortcuts.ps1

# Remove the .wav association
powershell -ExecutionPolicy Bypass -File scripts\Unregister-WaveEditWav.ps1
```

`Build-Portable.ps1` reads the version from the built exe, so just bump `<Version>` in the
csproj and re-run — the zip name's contents and the bundled readme update automatically.

### Engine tests
Pure (non-UI) audio logic — WAV round-trips, cut/insert/undo, DSP — is covered by a small test runner:

```powershell
dotnet run --project EngineTests/EngineTests.csproj
```

## Make WaveEdit open `.wav` files (optional)

WaveEdit ships **two** icons: the app icon (blue half-disc + lighter-blue play triangle, matching the waveform peak/average colors) and a
**document icon** ([wav-document.ico](AudioEditor/wav-document.ico) — a page with a waveform
and the play-mark badged in the corner) that `.wav` files use once WaveEdit is their default.

A no-admin, per-user (HKCU) registration is provided:

```powershell
# register (points at the Release build by default; pass -ExePath to override)
powershell -ExecutionPolicy Bypass -File scripts\Register-WaveEditWav.ps1

# undo completely
powershell -ExecutionPolicy Bypass -File scripts\Unregister-WaveEditWav.ps1
```

After registering, **choose the default yourself** — Windows 10/11 deliberately block scripts
from setting it: right-click a `.wav` ▸ *Open with* ▸ *Choose another app* ▸ pick **WaveEdit** ▸
*Always*; or Settings ▸ Apps ▸ Default apps ▸ search `.wav` ▸ WaveEdit. Explorer's icon cache may
need a moment (or an Explorer restart) to show the new icon.

> The script registers the path of your **built** exe. If you move the project or do a clean
> rebuild that removes `bin\`, re-run the register script (or `dotnet publish` to a fixed folder
> and pass that `-ExePath`).

## Controls

| Mouse | Action |
|-------|--------|
| Shift + drag | Select a range / add another region (multi-select) |
| Alt + drag | Subtract a range from the selection |
| Click / drag | Move the cursor (selection unchanged) |
| Ctrl + D | Deselect everything |
| Middle-button drag | Pan the timeline |
| Wheel | Zoom in/out (centered on pointer) |
| Shift + Wheel | Scroll horizontally |
| Ctrl + Wheel | Vertical amplitude zoom |

| Key | Action | Key | Action |
|-----|--------|-----|--------|
| `Space` | Play / Stop | `F5` | Record |
| `Ctrl + =` / `Ctrl + -` | Faster / slower playback | | |
| `+` / `-` | Zoom in / out | `Home`/`End` | Go to start / end |
| `Ctrl+O` / `Ctrl+S` | Open / Save | `Ctrl+Shift+S` | Save As |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo | `Del` | Delete selection |
| `Ctrl+X` / `C` / `V` | Cut / Copy / Paste | `Ctrl+A` / `Ctrl+D` | Select all / none |
| `Ctrl+E` | Zoom to selection | `Ctrl+F` | Zoom to full file |
| `Ctrl+Shift+I` | Insert silence | `F1` | Help / About |

## Architecture

```
AudioEditor/
  Program.cs                 entry point (STA, high-DPI, open-with)
  icon.ico                   app icon (blue half-disc + lighter-blue play triangle, soft shadow)
  Audio/
    AudioDocument.cs         planar float32 sample model (Channels[ch][frame])
    WavIO.cs                 load (AudioFileReader) / save (PCM 16/24/32, float)
    Dsp.cs                   amplify, normalize, fade, silence
    AudioPlayer.cs           ISampleProvider over the doc + WaveOutEvent
    AudioRecorder.cs         WASAPI capture + loopback, device enumeration, metering
  Edit/
    EditCommands.cs          Delete / Insert / ProcessRange commands (carry their own undo data)
    UndoStack.cs             undo/redo history
  UI/
    MainForm.cs              menus, toolbar, shortcuts, status bar, transport
    WaveformView.cs          custom GDI+ canvas: envelope + sample rendering, peak cache, ruler
    RecordDialog.cs          device picker + level meter
    InputDialog.cs           minimal text prompt
EngineTests/                 headless verification of the audio engine
```

**Internal sample format:** all audio is held as 32-bit float in planar layout. Editing operates
on whole frames across every channel in lockstep, so channels never desync. Conversion to the
target bit depth happens only at save time.

## Design choices (and where to extend)

- **WinForms + GDI+** for direct pixel control over the waveform. For very large files or
  higher-density rendering you can swap `WaveformView`'s GDI+ paint for **SkiaSharp** without
  touching the audio engine — the view only depends on `AudioDocument`.
- **NAudio** handles all device I/O and file decode. New encoders (MP3 via `MediaFoundationEncoder`,
  FLAC, etc.) slot into `WavIO` behind the existing `Save`/`Load` surface.
- **Resampling** uses NAudio's managed WDL resampler (`Audio/Resampler.cs`). Recording and cross-rate
  **paste** both conform automatically to the target document's sample rate.
- **Peak cache** (min/max over 256-sample blocks) bounds repaint cost when zoomed out. It is rebuilt
  after every edit; for multi-hour files a multi-level mip pyramid would be the next optimization.

## Known limitations / possible next steps
- Audio is held as **32-bit float** internally, so a 16-bit file uses ~2× its on-disk size in RAM (e.g. a 50 MB 16-bit WAV ≈ 100 MB of samples, ~150 MB working set with the runtime). This is the cost of lossless float editing/DSP. Loading streams straight into the final arrays (no transient copy). Storing native bit depth would halve it but complicate every edit — not currently done.
- Undo stores removed/overwritten audio in memory; very large cuts on huge files cost RAM.
- Mono/stereo only follows the source file; no channel up/down-mix UI yet.
- No spectral view, no time-stretch.
- Multi-region **normalize** scales each region to its own peak independently (not a shared peak).
- **Playback speed is varispeed** (resample-on-the-fly via linear interpolation), so pitch rises/falls with speed. Pitch-preserving time-stretch (WSOLA / phase-vocoder) is the natural next step in `DocumentSampleProvider`.
- **Play Selection** plays the bounding span across multiple regions (gaps included).
