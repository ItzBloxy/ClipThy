# ClipThy — Design Spec

**Date:** 2026-09-30
**Status:** Design approved in brainstorming; written spec pending user review

## 1. Goal

A lightweight Windows tray app that continuously keeps the last N seconds of the primary monitor, system audio and microphone in memory. When a hotkey is pressed, it saves them as an MP4 into the ShareX screenshots folder, sorted by month.

The main quality bar is "lightweight": near-zero CPU, a few hundred MB of RAM at most, no continuous disk writes, and minimal impact on game frame rates.

## 2. Confirmed decisions

| Topic | Decision |
|---|---|
| Captured | Primary monitor (DISPLAY1, 2560×1440) + system audio + default microphone |
| Frame rate | 60 fps |
| Video codec | H.264 on NVENC (RTX 3070), 20 Mbps VBR by default |
| Clip length | Chosen in the tray menu: 15 / 30 / 60 / 120 s; default 30 s |
| Alt+F10 | Save a clip with one mixed audio track |
| Alt+F11 | Save a clip with three audio tracks: 1 Mix, 2 System, 3 Mic |
| Output | `C:\Users\BLOXY\Documents\ShareX\Screenshots\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4` |
| Feedback | Short sound + Windows notification; clicking it shows the file in Explorer |
| Stack | .NET 10 WinForms tray app + NAudio + ffmpeg 8.0 (on PATH) |

## 3. Environment facts (verified 2026-09-30)

- Both monitors are driven by the RTX 3070: DISPLAY1 is 2560×1440 @ 240 Hz (primary) and DISPLAY2 is 1920×1080 @ 240 Hz. The AMD iGPU drives no display, so frames can stay on the NVIDIA GPU from capture to encoder.
- ffmpeg 8.0.1 (gyan.dev full build) on PATH has `ddagrab`, `h264_nvenc`, `amix` and `alimiter`. It has **no WASAPI input**, so it cannot capture system audio. The app captures audio itself.
- .NET SDK 10.0.203 is installed.
- The ShareX folder already uses `yyyy-MM` subfolders and `clip_yyyy-MM-dd_HH-mm-ss` file names.

## 4. Non-goals

- Second monitor or region capture
- Webcam
- Trimming or editing UI
- Uploading
- Settings window (the config file is the only settings UI)
- Pause/resume
- HEVC, AV1 or HDR
- Per-application audio capture

## 5. Architecture

```
Primary monitor ─ ddagrab ─► h264_nvenc ─► MPEG-TS on stdout ─► VideoBuffer   (compressed, RAM)
  (ffmpeg child process, video only)

System audio ─ WASAPI loopback ─► AudioBuffer "system"   (PCM + capture timestamps, RAM)
Microphone   ─ WASAPI capture  ─► AudioBuffer "mic"      (PCM + capture timestamps, RAM)

Hotkey ─► ClipExporter: cut the video at a keyframe N s back, cut both audio buffers to the
          exact same time span, ffmpeg muxes ─► ShareX\Screenshots\yyyy-MM\clip_….mp4
```

Audio never goes through the capture ffmpeg process. Every buffer stamps its data with the Windows high-resolution clock (QPC, `Stopwatch.GetTimestamp()`). The exporter aligns the three streams by those timestamps at save time. This design:

- removes cross-input timing inside ffmpeg, which is the main A/V sync risk;
- makes long-run clock drift irrelevant, because alignment happens per clip over at most a few minutes;
- lets an audio device change happen without ever restarting video capture;
- avoids continuous audio encoding, because audio is encoded only when a clip is saved.

### 5.1 Components

Each unit has one job and can be tested on its own. All times are QPC ticks.

**`Settings`** loads `%APPDATA%\ClipThy\config.json`, fills in defaults for missing fields, validates values, and saves changes made from the tray. If the JSON is invalid, it uses the defaults, shows a warning notification, and keeps the broken file as `config.json.bad`.

**`HotkeyListener`** parses hotkey strings such as `"Alt+F10"` and registers them with `RegisterHotKey` on a hidden window. It raises `SaveRequested(mode)`, where the mode is `Mixed` or `Multitrack`, and reports any hotkey that failed to register.

**`AudioCapture`** runs as two instances: `Loopback` on the default render device and `Microphone` on the default capture device.
- It uses WASAPI shared-mode, event-driven capture.
- It follows the default device, reopening when the default changes or the device is lost.
- It converts every packet to 48 kHz stereo 16-bit PCM: resampling, mono to stereo, and multichannel downmixed to stereo.
- It stamps each packet with its capture time. This is the QPC position WASAPI reports for the packet, or arrival time minus packet duration if that is unavailable.
- Packets flagged as silent are stored as zeros.
- It pushes packets into its `AudioBuffer`.

**`AudioBuffer`** is a time-indexed ring of PCM packets.
- `Append(packet, qpcStart)` adds a packet.
- `Extract(fromQpc, toQpc)` returns exactly that span as contiguous PCM:
  - Consecutive packets are laid end to end.
  - A gap longer than 15 ms between packets becomes silence (nothing playing, device switching, capture restarting).
  - Overlapping data is dropped.
- It trims anything older than clip length + 3 s.
- It is thread-safe: the capture thread writes and the exporter reads.

**`VideoCapture`** runs the capture ffmpeg.
- It builds the ffmpeg command and runs ffmpeg as a child process inside a Windows Job Object with kill-on-close, so ffmpeg never outlives the app.
- It streams stdout into `VideoBuffer` and keeps the last 50 stderr lines for the log.
- It restarts ffmpeg when:
  - ffmpeg exits;
  - there is a 3 s stall with no packets;
  - the PC resumes from sleep;
  - display settings change.
- Restart backoff is 1 s → 2 s → 5 s → 10 s → 30 s, and resets after 60 s of healthy running.
- Every restart clears `VideoBuffer`.

**`VideoBuffer`** consumes the MPEG-TS stream in 188-byte packets.
- It remembers the latest PAT and PMT packets.
- It indexes every PES start: its packet offset, its PTS, and whether it is a keyframe (`random_access_indicator` set).
- It maps PTS to QPC. The offset is the minimum of (arrival QPC − PTS time) over the last 10 s, which removes pipe and queue jitter. It handles 33-bit PTS wraparound.
- It trims to clip length + 2 s, so a keyframe at or before the cut point always exists.
- `Snapshot(endQpc, seconds)` returns PAT + PMT, then the packets that:
  - start at the last keyframe at or before `endQpc − seconds` (or at the oldest keyframe if less has been buffered);
  - stop before the first frame later than `endQpc`.

  It also returns the chunk's start and end QPC times.

**`ClipExporter`** processes save requests in a serial queue. For each request it does the following in order:
1. Record the press time.
2. Wait 150 ms so both audio buffers have caught up to the press time.
3. Take the video snapshot ending at the press time.
4. Extract both audio buffers for the snapshot's time range, from its start to its end plus one frame.
5. Write `video.ts`, `system.wav` and `mic.wav` to `%TEMP%\ClipThy\<id>\`.
6. Run the ffmpeg mux: video is copied and audio is encoded as AAC 192 kb/s.
7. Write the output to a `.part` file, then rename it to the final name.
8. Delete the temp folder and report the result.

**`OutputPaths`**: `Build(root, localTime)` returns `root\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4` and creates the month folder. If the name is taken it appends `_2`, `_3`, and so on. The timestamp is the hotkey press time.

**`TrayApp`** is the composition root and the only UI. It handles:
- tray icon and menu, tooltip status, notifications and the save sound;
- the single-instance mutex;
- the autostart registry value;
- logging setup;
- the `--sync-test` mode (§10).

### 5.2 ffmpeg commands

These commands are indicative. The exact flags are verified during implementation.

Capture (video only):

```
ffmpeg -hide_banner -loglevel warning
  -f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=1
  -c:v h264_nvenc -preset p4 -tune ll -rc vbr -b:v 20M -maxrate 30M -bufsize 40M
  -g 60 -bf 0
  -f mpegts -flush_packets 1 pipe:1
```

- Frames stay on the GPU from Desktop Duplication to NVENC, with no CPU copy.
- `-g 60` puts a keyframe every second, so clips start within 1 s of the requested length.
- The low-latency settings (`-tune ll`, `-bf 0`, packet flushing) keep stdout arrival within a few ms of capture. The PTS→QPC mapping relies on this.

Export, mixed (Alt+F10):

```
ffmpeg -i video.ts -i system.wav -i mic.wav
  -filter_complex "[1:a][2:a]amix=inputs=2:normalize=0,alimiter=limit=0.98[mix]"
  -map 0:v -map "[mix]" -c:v copy -c:a aac -b:a 192k
  -metadata:s:a:0 title=Mix -movflags +faststart out.mp4.part
```

Export, multitrack (Alt+F11): uses the same mix filter and adds `-map 1:a -map 2:a`. The tracks are titled `Mix`, `System` and `Mic`. Track 1 (Mix) is the default track, so players that only play one track still play everything.

`normalize=0` keeps both sources at their original level, and the limiter prevents clipping when both are loud.

## 6. Settings

The settings file is `%APPDATA%\ClipThy\config.json`. It is created with these defaults on first run:

```json
{
  "clipSeconds": 30,
  "hotkeyMixed": "Alt+F10",
  "hotkeyMultitrack": "Alt+F11",
  "outputRoot": "C:\\Users\\BLOXY\\Documents\\ShareX\\Screenshots",
  "fps": 60,
  "videoBitrateMbps": 20,
  "playSound": true,
  "ffmpegPath": "ffmpeg"
}
```

- **Validation:** `clipSeconds` must be 5–300, `fps` must be 30 or 60, and `videoBitrateMbps` must be 2–100. An invalid value falls back to its default and shows a warning.
- **Tray changes** (clip length) are written back immediately.
- **Hand edits** to the file take effect the next time the app starts. The app does not watch the file.
- **Start with Windows** is not in the config. The `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `ClipThy` is the source of truth. It is created on first run and toggled from the tray.

## 7. Tray UI

Menu:
- Clip length ▸ 15 s / 30 s / 60 s / 120 s. The current length has a check mark. A custom length from the config appears as an extra checked entry.
- Save clip now (mixed)
- Open clips folder (this month's folder, or the root if it doesn't exist yet)
- Start with Windows ✓
- Exit

Tooltip: one of `ClipThy – recording (30 s)`, `ClipThy – restarting capture…` or `ClipThy – error: <short reason>`. The icon is a red dot while recording and grey otherwise.

On save, a short sound plays (if `playSound` is true) and a notification appears, such as "Clip saved – 30 s". The notification shows the actual length, for example 12 s just after startup. Clicking it opens Explorer with the file selected. If a save fails, the notification gives the reason and no sound plays.

Changing the clip length to a shorter one takes effect immediately. Changing it to a longer one fills up over time.

Known behaviour: the save sound and notification are recorded like anything else. They can appear in the *next* clip if the two overlap. Setting `playSound: false` turns the sound off.

## 8. Error handling

| Situation | Behaviour |
|---|---|
| ffmpeg exits, or outputs no packets for 3 s (resolution change, UAC secure desktop, driver reset) | Restart with backoff and clear the video buffer. The tooltip shows "restarting". A notification appears only after 5 failures in a row, with ffmpeg's last error line |
| Sleep/resume, display settings change | Restart video capture right away. On resume, audio capture is reopened too |
| Default audio device changes or is unplugged | `AudioCapture` reopens the new default. The gap is silent in clips |
| No microphone | The mic track is silent, so the track layout stays the same |
| Hotkey registration fails (for example the NVIDIA overlay owns Alt+F10) | Notification at startup naming the hotkey. "Save clip now" still works |
| ffmpeg not found | Error notification and error tooltip. The app stays in the tray; fix `ffmpegPath` and restart the app |
| Export fails (disk full, folder not writable) | Notification with the reason. Temp and `.part` files are deleted and capture continues |
| Save pressed while another export is running | Queued. Every press produces its own clip |
| Save pressed with less buffered than the clip length | Saves what is there. The notification shows the actual length |
| Second instance launched | Exits immediately |
| Protected content (DRM video) | Recorded as black. This is a Windows limitation |

The log is `%LOCALAPPDATA%\ClipThy\clipthy.log` and rolls over to `clipthy.1.log` at 1 MB. It records:
- the settings at startup;
- restarts, with the ffmpeg stderr tail;
- saves (path, clip length, time taken);
- errors.

## 9. Resource budget (acceptance targets)

- CPU on an idle desktop, app and ffmpeg combined: ≤ 2 % in Task Manager.
- RAM with a 30 s clip length, app and ffmpeg combined: ≤ 300 MB. The video buffer is about 75 MB at 20 Mbps and the audio buffers about 13 MB.
- Game FPS impact is small, measured before and after in one game.
- Saving a 30 s clip finishes in ≤ 3 s.
- No disk writes except when saving and logging.

## 10. Testing

**Unit tests** (xUnit, no hardware):
- `VideoBuffer`:
  - PES and keyframe indexing on a real TS file generated by ffmpeg;
  - trimming;
  - a snapshot starts on a keyframe with PAT/PMT first;
  - PTS→QPC mapping with jittered arrival times;
  - PTS wraparound.
- `AudioBuffer`: exact-span extraction, gaps becoming silence, overlaps, trimming.
- `OutputPaths`: month folder, naming, collisions, month rollover.
- `Settings`: defaults, partial file, invalid values, invalid JSON.
- Parsing hotkey strings.
- Building export commands for both modes.

**Integration tests** (real ffmpeg and NVENC, no screen):
- `testsrc2` replaces `ddagrab` as the input to `VideoCapture` and `VideoBuffer`.
- Synthetic sine-wave packets feed both `AudioBuffer`s.
- Both modes are exported, then `ffprobe` checks:
  - duration is 30–31 s;
  - streams are 1 video + 1 audio, or 1 video + 3 audio;
  - codecs are H.264 and AAC;
  - track titles are correct;
  - `moov` is at the front of the file (faststart).

**Sync test:**
- `ClipThy.exe --sync-test` shows a window on the primary monitor that flashes white and beeps at the same moment once per second.
- `tools\sync-check.ps1` analyses a saved clip with ffmpeg. It compares brightness jumps with beep onsets and prints the A/V offset.
- The target is |offset| ≤ 30 ms, both right after startup and after at least 4 h of running.

**Manual checklist** (on this PC):
- the resource targets in §9;
- colours match the screen;
- headphones unplugged mid-session;
- default mic switched;
- sleep/resume;
- resolution change;
- a fullscreen game is captured;
- Alt+F10 does not clash with the NVIDIA overlay.

## 11. Risks to check first in implementation

1. `ddagrab output_idx=0` captures DISPLAY1 on the RTX 3070 and feeds NVENC without CPU copies. ffmpeg's CPU use should stay low.
2. NVENC's RGB→YUV conversion gives correct colours. If it doesn't, add a GPU-side conversion step before the encoder.
3. The low-latency settings keep stdout arrival close to capture time. Measure the spread of arrival − PTS.
4. NAudio exposes WASAPI's QPC packet timestamps. If it doesn't, use NAudio's lower-level CoreAudio wrappers directly, or the arrival-time fallback.
5. How ddagrab behaves on a display-mode change and on the secure desktop: does it exit with an error, or freeze? The 3 s stall watchdog covers both.

## 12. Project layout

```
E:\ClipThy\
  ClipThy.sln
  src\ClipThy\            WinExe, net10.0-windows, WinForms, NAudio
  tests\ClipThy.Tests\    xUnit
  tools\sync-check.ps1
  docs\superpowers\specs\
```

It is published as a framework-dependent build, using the installed .NET 10 runtime. ffmpeg is taken from PATH, and the path can be changed in the config.
