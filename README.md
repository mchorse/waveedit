# WaveEdit

A native **Windows** audio editor written in C# (.NET 8 / WinForms + NAudio). Single-window,
no project files — open a `.wav`, edit samples directly, save back out.

![status](https://img.shields.io/badge/platform-Windows-blue) ![net](https://img.shields.io/badge/.NET-8.0-512BD4)

## Features

| # | Capability | Notes |
|---|------------|-------|
| 1 | **Open / Save WAV** | `Ctrl+O` / `Ctrl+S`, native file dialogs. Reads PCM 16/24/32 & IEEE float (plus MP3/AIFF/WMA/FLAC decode on the way in). Saves WAV PCM 16/24 or 32-bit float. |
| 2 | **Playback** | Mono or stereo, **any** sample rate. `Space` plays from the cursor to the end (toggles stop). *Transport ▸ Play Selection* auditions the selected region instead. |
| 3 | **Multi-region selection** | **Shift + drag** selects a range and adds more disjoint regions; plain click/drag only moves the cursor and never alters the selection. **Ctrl + D** deselects all. Cut/copy/paste and all effects apply to every region at once. Sample-accurate. |
| 4 | **Cut** | `Ctrl+X` / `Del` removes the selected waveform (with undo). |
| 5 | **Insert silence** | `Ctrl+Shift+I`, prompts for a duration in seconds. |
| 6 | **Zoom to individual samples** | Mouse wheel zooms from whole-file down to ~64 px per sample, drawing sample dots and stems. |
| 7 | **Native dialogs** | Standard Win32 open/save. |
| 8 | **Recording** | `F5` — pick any active input endpoint (mic, line-in, or render **loopback**) via WASAPI, with a live level meter. Into an existing document, the take is **auto-resampled** to the document's rate and channel-matched, then inserted at the cursor. Into an empty document it keeps its native rate. |
| 9 | **Windows only** | Targets `net8.0-windows`, WinForms, WASAPI/WaveOut. |

### Extras included
- **Undo / redo** (`Ctrl+Z` / `Ctrl+Y`) with named history — memory-efficient (each command stores only its own inverse, not a full-file snapshot).
- **Copy / paste** of audio regions (`Ctrl+C` / `Ctrl+V`); paste replaces the active selection.
- **Processing** (Process menu): Amplify/Gain (dB), Normalize, Fade In, Fade Out, Silence selection.
- **Live playhead** that follows playback and auto-scrolls.
- **Status bar**: cursor position (time + sample index), selection length, format, and zoom level.
- **Drag & drop**: drop an audio file onto an empty document to load it; drop onto a document that already has audio and it opens in a **new window**, leaving your current work untouched.
- **Open-with**: pass a file path on the command line (or drop a file on the `.exe`).

## Build & run

```powershell
# from the repo root
dotnet build AudioEditor.sln -c Release
dotnet run  --project AudioEditor/AudioEditor.csproj
```

The output binary is `AudioEditor/bin/Release/net8.0-windows/WaveEdit.exe`.

### Engine tests
Pure (non-UI) audio logic — WAV round-trips, cut/insert/undo, DSP — is covered by a small test runner:

```powershell
dotnet run --project EngineTests/EngineTests.csproj
```

## Controls

| Mouse | Action |
|-------|--------|
| Shift + drag | Select a range / add another region (multi-select) |
| Click / drag | Move the cursor (selection unchanged) |
| Ctrl + D | Deselect everything |
| Middle-button drag | Pan the timeline |
| Wheel | Zoom in/out (centered on pointer) |
| Shift + Wheel | Scroll horizontally |
| Ctrl + Wheel | Vertical amplitude zoom |

| Key | Action | Key | Action |
|-----|--------|-----|--------|
| `Space` | Play / Stop | `F5` | Record |
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
  icon.ico                   app icon (red half-disc + green play triangle, soft shadow)
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
- **Resampling** uses NAudio's managed WDL resampler (`Audio/Resampler.cs`). Recording into an existing
  document conforms automatically. Cross-rate **paste** still inserts raw frames without resampling (the
  clipboard carries its own rate) — routing paste through `Resampler` too is a small next step.
- **Peak cache** (min/max over 256-sample blocks) bounds repaint cost when zoomed out. It is rebuilt
  after every edit; for multi-hour files a multi-level mip pyramid would be the next optimization.

## Known limitations / possible next steps
- Undo stores removed/overwritten audio in memory; very large cuts on huge files cost RAM.
- Mono/stereo only follows the source file; no channel up/down-mix UI yet.
- No spectral view, no time-stretch.
- Multi-region **normalize** scales each region to its own peak independently (not a shared peak).
- **Play Selection** plays the bounding span across multiple regions (gaps included).
