# Screen Recorder

A private, local screen recorder for Windows. Record your screen (whole monitor or a dragged area, with an optional
microphone) to MP4, play it back, edit it, and save any part of it as a GIF.

- **Record** a whole monitor or part of one, with or without a microphone. Pause and resume as often as you like.
  The recording controls float on screen but are hidden from the video.
- **Play** recordings with play/pause and a scrubbable timeline.
- **Edit** without ever touching the original: keep only a range (trim), cut sections out of the middle, crop,
  and change speed (0.25×–4×). Export writes a new `…_edited.mp4`.
- **GIF**: select a range (e.g. 0:10–0:17) and export just that part as an animated GIF. The MP4 stays complete.

## Why you can trust it

- **No network access.** The app never connects to the internet. Recording, editing and GIF creation are done by
  [FFmpeg](https://ffmpeg.org), which runs as a local program on your files. (The only download is the one-time
  setup script below, which fetches FFmpeg itself.)
- **No third-party app code.** The app is plain C#/.NET 9 and WPF: no NuGet packages at runtime. The test project
  uses xUnit only.
- **Pinned, verified FFmpeg.** `scripts/setup.ps1` downloads one exact FFmpeg release and refuses to install it unless
  its SHA-256 hash matches the value committed in [`scripts/ffmpeg.lock.json`](scripts/ffmpeg.lock.json). The app only
  runs that copy (it never picks up another `ffmpeg` from your PATH).
- **Your files stay yours.** Everything is saved to `%USERPROFILE%\Videos\ScreenRecorder`.

You can check the no-network claim yourself: `git grep -n -E "HttpClient|WebRequest|Socket"` finds nothing in
`src/`, and Resource Monitor's Network tab shows no traffic from `ScreenRecorder.exe` or `ffmpeg.exe` while recording.

## Getting started

Requirements: Windows 10 (2004+) or 11, and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
# 1. One-time: download FFmpeg 9.0.2 and verify its hash (into tools\ffmpeg, which git ignores)
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1

# 2. Run the app
dotnet run --project src\ScreenRecorder.App

# 3. Run the tests (they generate their own test videos and delete them afterwards)
dotnet test
#    Optional: also test real screen capture (records 2 s of your screen, then deletes it)
$env:SCREENRECORDER_SCREEN_TESTS = "1"; dotnet test
```

### A portable build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

This produces `publish\ScreenRecorder.exe` with `ffmpeg.exe`/`ffprobe.exe` beside it. The folder is self-contained
(no .NET install needed) and can be copied to any Windows PC.

### Why not Docker?

A container cannot see or capture the Windows desktop, so Docker can't run a screen recorder. Instead, everything the
app depends on is pinned so any machine gets the identical setup: the .NET SDK version (`global.json`), NuGet
packages (lock files) and the exact FFmpeg build (`scripts/ffmpeg.lock.json` + hash check).

## Using it

| Where | How |
|---|---|
| Record tab | Pick the screen, *Whole screen* or *Part of the screen* (drag an area), the microphone, then **Start recording**. |
| While recording | The floating toolbar has **Pause/Resume** and **Stop & save**. Hotkeys: **Ctrl+Shift+F10** pause/resume, **Ctrl+Shift+F9** stop. |
| Play & edit tab | **Space** play/pause · **←/→** 1 s (Shift: 5 s) · **I**/**O** set selection start/end · Shift+drag on the timeline to select · **Delete** cuts the selection · **Ctrl+Z** undo |
| Edits | *Keep only selection* (trim), *Cut selection*, *Restore selection*, *Crop* (drag over the video), *Speed*. The timeline shows removed parts hatched in red, and playback skips them. |
| Export | **Export edited video** saves `…_edited.mp4` and opens it. **Export GIF…** saves the selected range (or times you type) as a GIF. |

## Troubleshooting

- **The window is blank/white.** GPU rendering isn't available to the app (other WPF apps are affected too; this has
  been seen while Windows was locked or the display was off). Start the app with `ScreenRecorder.exe --software-render`
  (or `dotnet run --project src\ScreenRecorder.App -- --software-render`); restarting Windows or updating the
  graphics driver may also help.
- **"Desktop Duplication capture is unavailable…"** The app fell back to GDI capture, which works everywhere but
  uses more CPU (large screens may record below 30 fps). Desktop Duplication is refused over Remote Desktop, for
  monitors on a second GPU, while Windows is locked, and sometimes when the graphics driver is in a bad state.
- **The recording has no sound / silent audio.** Check *Settings → Privacy & security → Microphone* and that the mic
  isn't muted. If the microphone can't be opened, the app records without audio and tells you.
- **FFmpeg not found.** Run `scripts\setup.ps1` (or put `ffmpeg.exe` and `ffprobe.exe` next to `ScreenRecorder.exe`).
- **The app crashed or was closed while recording.** Nothing is lost: recordings are written in parts while you
  record, and the next time the app starts it offers to recover them. If saving fails (e.g. the disk is full), the
  parts are kept and **Try saving again** retries. If the microphone disconnects mid-recording, the video continues
  and the rest is silent.

## How it works

```
src/ScreenRecorder.Core   All FFmpeg logic, no UI (fully unit/integration tested)
  Recording/  RecordingSession: one MKV segment per start/resume; Stop joins them into the MP4
  Editing/    EditProject (immutable edit list) → EditPlan → one FFmpeg filter graph for export
  Gif/        palettegen/paletteuse single-pass GIF export of a time range
src/ScreenRecorder.App    WPF (Fluent theme) UI, monitor/DXGI interop, hotkeys
tests/                    xUnit tests using FFmpeg-generated fixture videos
```

Recording notes:
- Capture uses Desktop Duplication (`ddagrab`), falling back to GDI (`gdigrab`).
- Recording pauses by closing the current segment. Each segment is a crash-safe MKV.
- Every FFmpeg process is placed in a Windows job object, so Windows stops it if the app exits for any reason; it can
  never keep recording in the background.
- The microphone usually starts delivering audio ~0.5–1 s after the first screen frame. Both inputs share a wall-clock
  origin, so this becomes leading silence instead of shifting the sound out of sync.

## License

The code in this repository is released under the [MIT License](LICENSE).

FFmpeg is not part of this repository. `scripts\setup.ps1` downloads it separately. The "essentials" build is
GPL-licensed, and its license is copied to `tools\ffmpeg\FFMPEG-LICENSE.txt`. The portable build from
`scripts\publish.ps1` includes `ffmpeg.exe`, so if you give that folder to others, the GPL applies to those FFmpeg
files: keep the license with them and point to the FFmpeg source.
