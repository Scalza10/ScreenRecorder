# Trusted Screen Recorder: Design and Implementation Plan

## Context
You want your own Windows screen recorder so you can trust that nothing leaves your machine. It has no third-party telemetry and no network calls. The app:
1. Records the screen (a whole monitor or a dragged region, with optional microphone audio) to **MP4**.
2. Plays recordings back with **play/pause** and a scrubbable timeline.
3. **Edits** a recording: trims the start and end, cuts out middle sections, crops, and changes speed.
4. Exports **any time range as a GIF** (for example 0:10 to 0:17) while the full MP4 stays saved and unchanged.

The repo is currently empty (only `.gitignore`). Your machine has the .NET 9 SDK, Docker and Python 3.12. FFmpeg is not installed.

**Decisions made with you:** C# / WPF on .NET 9. Dependencies are **pinned and scripted**, not Docker, because a container cannot capture the Windows desktop. Editing covers trim, cut middle, crop and speed. Capture covers full monitor, selected region and microphone (no system audio).

## Architecture
The app is a native WPF app that runs **FFmpeg as a local child process** for capture, editing and GIF encoding. FFmpeg is open source, widely audited and makes no network calls in this use. The app itself has **no third-party runtime NuGet packages**. It uses plain WPF and a small hand-written `ObservableObject`, so the only code involved is yours, Microsoft's .NET, and FFmpeg.

**Reproducible setup, used instead of Docker:**
- `global.json` pins the .NET SDK (9.0.x, roll forward to the latest patch).
- `Directory.Build.props` sets `Nullable=enable`, `TreatWarningsAsErrors`, and `RestorePackagesWithLockFile=true`, so test packages are locked.
- `scripts/ffmpeg.lock.json` holds the pinned FFmpeg release (a gyan.dev *essentials* build, version ≥ 7.1, which includes `ddagrab`, `dshow` and `libx264`), its URL and its **SHA-256**.
- `scripts/setup.ps1` downloads that exact zip, **verifies the SHA-256** (and aborts if it doesn't match), then extracts `ffmpeg.exe` and `ffprobe.exe` into `tools/ffmpeg/`. That folder is gitignored.
- `scripts/publish.ps1` runs `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` and copies FFmpeg next to the exe, which gives a portable folder you can reuse on any PC.

## Solution layout
```
ScreenRecorder.sln, global.json, Directory.Build.props, README.md
scripts/  setup.ps1, publish.ps1, ffmpeg.lock.json
src/ScreenRecorder.Core/        (net9.0, no UI, contains all FFmpeg logic, unit-testable)
  Ffmpeg/   FfmpegLocator.cs    (finds tools/ffmpeg or the exe dir; clear error if missing)
            FfmpegProcess.cs    (starts ffmpeg, streams stderr, parses progress "time=",
                                 graceful stop by writing "q" to stdin, kills on timeout)
  Media/    MediaProbe.cs       (ffprobe -of json -> Duration, Width, Height, HasAudio)
            TimeRange.cs        (Start/End TimeSpan, validation)
  Devices/  AudioDevices.cs     (parses `ffmpeg -list_devices true -f dshow -i dummy`)
  Recording/RecordingOptions.cs (MonitorIndex, Region (physical px, forced even), Fps=30,
                                 MicDevice?, DrawCursor)
            RecordArgsBuilder.cs(builds the ffmpeg argument list, see below)
            RecordingSession.cs (Start / Pause / Resume / Stop; each run writes a .mkv
                                 segment; Stop concatenates the segments into the final .mp4)
  Editing/  EditProject.cs      (Trim: TimeRange, Cuts: List<TimeRange>, Crop: Rect?, Speed)
            EditPlan.cs         (pure logic: kept segments = trim minus cuts; output duration)
            ExportArgsBuilder.cs(filter_complex from EditPlan)
  Gif/      GifOptions.cs       (Range, Fps=15, Width=800 or source, Loop=0)
            GifArgsBuilder.cs
src/ScreenRecorder.App/         (net9.0-windows, WPF, PerMonitorV2 DPI manifest)
  MainWindow        (two tabs: Record | Recordings/Editor)
  Views/RecordView           (monitor picker, "Select region…", mic picker, Record button)
  Views/RegionSelectorWindow (borderless, transparent window over the chosen monitor;
                              drag a rectangle; returns physical-pixel rect)
  Views/RecordingToolbar     (small floating Pause/Resume/Stop + timer; excluded from capture
                              with SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE))
  Views/EditorView           (MediaElement player, Play/Pause, timeline control, edit tools)
  Controls/TimelineControl   (scrubber + playhead; shows trim handles, cut ranges shaded,
                              GIF in/out selection)
  Views/CropOverlay          (drag rectangle over the video to set the crop)
  Views/GifExportDialog      (start/end fields prefilled from selection, fps, width)
  Services/MonitorService    (EnumDisplayMonitors -> index, bounds in physical px, name)
  Services/HotkeyService     (RegisterHotKey: Ctrl+Shift+F9 stop, Ctrl+Shift+F10 pause)
tests/ScreenRecorder.Core.Tests/ (xUnit)
docs/superpowers/specs/2026-09-30-screen-recorder-design.md  (this design, committed first)
```

## Data flow and key FFmpeg commands
**Record.** Uses Desktop Duplication (`ddagrab`), which runs on the GPU and captures in physical pixels, so DPI scaling is handled correctly:
```
ffmpeg -y -f lavfi -i ddagrab=output_idx=N:framerate=30:draw_mouse=1[:offset_x=X:offset_y=Y:video_size=WxH]
       [-f dshow -thread_queue_size 1024 -i audio="<mic>"]
       -vf hwdownload,format=bgra,format=yuv420p -c:v libx264 -preset veryfast -crf 23
       [-c:a aac -b:a 160k -af aresample=async=1]  segment_K.mkv
```
- Segments are written as MKV so a crash doesn't corrupt the recording.
- Pause stops the current segment gracefully (`q`). Resume starts a new segment.
- Stop writes a concat list and runs `ffmpeg -f concat -safe 0 -i list.txt -c copy -movflags +faststart Recording_yyyy-MM-dd_HH-mm-ss.mp4`, then deletes the segments.
- Output folder: `%USERPROFILE%\Videos\ScreenRecorder\`.
- When recording starts, the main window minimises and the floating toolbar appears.

**Play.** A WPF `MediaElement` plays H.264/yuv420p MP4 natively on Windows 11. A `DispatcherTimer` drives the playhead. You can seek by clicking or dragging the timeline, and Space toggles play/pause.

**Edit (non-destructive).**
- The UI changes an `EditProject`. The preview marks cut ranges on the timeline and skips over them during playback, shows the crop rectangle as an overlay, and applies speed through `MediaElement.SpeedRatio`.
- **Export Edited MP4** builds the command: for each kept segment it adds `trim`/`atrim` + `setpts/asetpts=PTS-STARTPTS`, then `concat=n=K:v=1:a=1`, then `crop=w:h:x:y` (even dimensions), then `setpts=PTS/S` and an `atempo` chain (factors kept inside 0.5–2 and chained for larger speeds).
- It encodes with libx264 crf 20 + aac + `+faststart` to `<name>_edited.mp4`. **The original is never modified.**

**GIF.** You mark In/Out on the timeline or type times, then choose **Export GIF**. A single-command palette pass:
```
ffmpeg -ss S -to E -i in.mp4 -vf "fps=15,scale=W:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5" -loop 0 out.gif
```
- Default name is `<name>_0m10s-0m17s.gif`, with a SaveFileDialog to change it.
- The source MP4 is left untouched. GIFs are taken from the currently loaded file, so to GIF an edited version you export the edit first, and it opens automatically.

**Errors.**
- If FFmpeg is missing, you get a message telling you to run `scripts/setup.ps1`.
- A non-zero FFmpeg exit shows the last 20 stderr lines in a dialog.
- If `ddagrab` fails (an old GPU driver or an RDP session), the recorder retries automatically with `gdigrab` using the same region.
- A mic device that disappears triggers a warning, and recording continues without audio.

## Implementation order (TDD for Core)
1. Scaffolding: sln, global.json, Directory.Build.props, projects, `.gitignore` additions (`bin/ obj/ tools/ publish/`), setup/publish scripts plus the SHA-pinned ffmpeg lock. Commit the design spec.
2. Core `FfmpegLocator`, `FfmpegProcess`, `MediaProbe`, with tests using ffmpeg-generated fixtures (`-f lavfi testsrc2` + `sine`).
3. `GifArgsBuilder` + GIF export: a test checks that 10–17s from a 60s fixture gives a GIF of about 7s at the requested width, and that the source MP4's hash is unchanged.
4. `EditPlan` (pure math tests: trim/cuts/overlaps/speed durations) and `ExportArgsBuilder` (integration tests check the output duration and crop dimensions with ffprobe).
5. `RecordArgsBuilder` (argument unit tests) and `RecordingSession` (segment/pause/concat logic, tested with a lavfi source replacing ddagrab so it runs headless).
6. WPF: MonitorService, RecordView, RegionSelectorWindow, RecordingToolbar + hotkeys.
7. WPF: EditorView, TimelineControl, play/pause/seek, trim/cut/crop/speed tools, Export Edited.
8. WPF: GifExportDialog wired to the timeline In/Out selection.
9. README (setup, publish, privacy notes: no network, where files go) and the publish script check.

## Verification
- `pwsh scripts/setup.ps1` downloads FFmpeg and verifies the hash. Changing a byte in the lock file must make it fail.
- `dotnet test`: all Core unit and integration tests pass. The tests generate their own fixture videos, so they need no real screen.
- Manual end-to-end in the running app (`dotnet run --project src/ScreenRecorder.App`):
  1. Record a full monitor with the mic for about 60s, pausing once. The MP4 plays in the app and in the Windows Media Player app, audio is in sync, the paused gap is absent, and the toolbar is not in the video.
  2. Record a dragged region on a 150%-scaled monitor. The output size matches the selection in physical pixels.
  3. Play/pause, scrub and Space toggle all work.
  4. Trim, cut 0:20–0:25, crop and set 1.5× speed, then export. ffprobe duration equals the value `EditPlan` predicts, the dimensions equal the crop, and the original file is unchanged.
  5. Mark 0:10–0:17 and export a GIF. It opens in a browser, loops and lasts about 7s, and the MP4 is still complete.
- Privacy check: grep the source for `HttpClient`, `WebRequest` and `Socket` (expect none outside `setup.ps1`), and optionally watch the app in Resource Monitor's Network tab while recording.

## Risks / notes
- `WDA_EXCLUDEFROMCAPTURE` needs Windows 10 2004 or later (you're on Windows 11, so this is fine). Whether it hides the toolbar from ddagrab will be checked in step 6. The fallback is to hide the toolbar and rely only on the hotkeys.
- A recorded region must lie within a single monitor (a ddagrab limit). The selector clamps the region to that monitor.
- The FFmpeg essentials build is GPL. That's fine for personal use, and the README will note it.
