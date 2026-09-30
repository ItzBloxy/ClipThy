# ClipThy — Design Spec

**Date:** 2026-09-30
**Status:** Design approved in brainstorming. Revision 2 follows a self-review that measured the pipeline on the target PC. The written spec is pending user review.

## 1. Goal

A lightweight Windows tray app that continuously keeps the last N seconds of the primary monitor, system audio and microphone in memory. When a hotkey is pressed, it saves them as an MP4 into the ShareX screenshots folder, sorted by month.

The main quality bar is "lightweight": near-zero CPU, a few hundred MB of RAM at most, no continuous disk writes, and minimal impact on game frame rates.

## 2. Confirmed decisions

| Topic | Decision |
|---|---|
| Captured | Primary monitor (DISPLAY1, 2560×1440) + system audio + default microphone |
| Frame rate | 60 fps |
| Video codec | H.264 on the RTX 3070's hardware encoder, driven through Media Foundation (`h264_mf`); see §3 for why not `h264_nvenc`. 20 Mbps by default |
| Clip length | Chosen in the tray menu: 15 / 30 / 60 / 120 s; default 30 s |
| Alt+F10 | Save a clip with one mixed audio track |
| Alt+F11 | Save a clip with three audio tracks: 1 Mix, 2 System, 3 Mic |
| Output | `C:\Users\BLOXY\Documents\ShareX\Screenshots\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4` |
| Feedback | Short sound + Windows notification; clicking it shows the file in Explorer |
| Stack | .NET 10 WinForms tray app + NAudio + ffmpeg 8.0 (on PATH) |

## 3. Environment facts (measured on the target PC, 2026-09-30)

**Displays and capture**
- Both monitors are driven by the RTX 3070: DISPLAY1 is 2560×1440 @ 240 Hz (primary) and DISPLAY2 is 1920×1080 @ 240 Hz. The AMD iGPU drives no display.
- `ddagrab` with `output_idx=0` opens the 2560×1440 primary display. Frames stay on the GPU all the way to the encoder.
- ddagrab tops out at about 64 fps on this PC, so 60 fps is close to its ceiling. About 0.3 % of frames were dropped on an idle desktop.

**Encoder**
- The NVIDIA driver is **566.14**. ffmpeg 8.0.1's `h264_nvenc` needs NVENC API 13.0, which means driver ≥ 570, so it fails to open.
- `h264_mf` uses the same encoder chip through Windows ("NVIDIA H.264 Encoder MFT"). It doesn't depend on the NVENC SDK version, so it also survives the pending winget upgrade to ffmpeg 9.0.2.
- Measured with the capture command in §5.2:
  - ffmpeg uses about 3 % of one CPU core and has a 65–75 MB working set.
  - There is a keyframe exactly every 60 frames and there are no B-frames.
  - The 20 Mbps target is honoured: a moving 1440p test pattern came out at 21.2 Mbps including TS overhead.
- Media Foundation holds frames for about **0.35–0.44 s** before releasing them. This is a fixed pipeline depth, not a throughput limit: the encoder alone does 370 fps at 1440p.

**Timestamps**
- ddagrab's own timestamps count from when ffmpeg started, so on their own they can't be matched to audio.
- With `-copyts -use_wallclock_as_timestamps 1`, each frame instead carries the system (UTC) time at which it was captured, before encoding.
- That clock agrees with .NET's `DateTime.UtcNow` within about ±25 ms, with no drift.

**Cutting**
- Cutting the MPEG-TS stream at a keyframe and putting the latest PAT/PMT in front gives a valid MP4. Tested: exact frame count, starts on an I-frame, no decode errors.

**Audio**
- ffmpeg has **no WASAPI input**, so it can't capture system audio. The app captures audio itself.
- NAudio's `WasapiCapture` doesn't expose packet timestamps. `AudioCaptureClient.GetBuffer(out frames, out flags, out devicePosition, out qpcPosition)` does.
- Devices on this PC:
  - HyperX QuadCast S (USB microphone and headphone output)
  - Realtek
  - monitor audio outputs
  - Oculus Virtual Audio, which can become the default device while Quest Link runs
- Desktop apps are allowed to use the microphone. Windows communications ducking is at its default setting: reduce other sounds by 80 %.

**Other**
- ffmpeg 8.0.1 (gyan.dev full build) on PATH also has `amix`, `alimiter` and `scale_d3d11`.
- .NET SDK 10.0.203 and the Windows Desktop runtime 10.0.7 are installed.
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
- A direct NVENC path (it would need a driver update and gains little)

## 5. Architecture

```
Primary monitor ─ ddagrab ─► h264_mf ─► MPEG-TS on stdout ─► VideoBuffer   (compressed, RAM)
  (ffmpeg child process, video only; every frame stamped with its UTC capture time)

System audio ─ WASAPI loopback ─► AudioBuffer "system"   (PCM + capture timestamps, RAM)
Microphone   ─ WASAPI capture  ─► AudioBuffer "mic"      (PCM + capture timestamps, RAM)

Hotkey ─► ClipExporter: cut the video at a keyframe N s back, cut both audio buffers to the
          exact same time span, ffmpeg muxes ─► ShareX\Screenshots\yyyy-MM\clip_….mp4
```

Audio never goes through the capture ffmpeg process. Every buffer is indexed by **capture time on one timeline**, and the exporter aligns the three streams on that timeline at save time. This design:

- makes sync independent of how long the encoder holds frames (about 0.4 s);
- makes long-run clock drift irrelevant, because alignment happens per clip over at most about 2 minutes;
- lets an audio device change happen without ever restarting video capture;
- avoids continuous audio encoding, because audio is encoded only when a clip is saved.

### 5.1 The timeline

The timeline is QPC ticks from `Stopwatch.GetTimestamp()`.

- **Audio:** WASAPI reports each packet's capture time as a QPC position in 100-ns units, which is converted to ticks.
- **Video:** each frame's PTS is its UTC capture time (90 kHz, 33-bit, wrapping every 26.5 h). When a frame's first TS packet arrives, `VideoBuffer` reads `DateTime.UtcNow` and `Stopwatch.GetTimestamp()` back to back and computes:
  - `delay = (arrivalUtc90k − pts) mod 2³³`
  - `captureQpc = arrivalQpc − delay`

  This is exact apart from the ±25 ms clock agreement, and it also handles the wraparound.
- **Sanity check:** a delay outside 0–5 s means the timestamps aren't what we expect. The frame then uses its arrival time and a warning is logged.
- **Clock changes:** a change to the system clock (`SystemEvents.TimeChanged`) restarts video capture.

### 5.2 Components

Each unit has one job and can be tested on its own.

**`Settings`** loads `%APPDATA%\ClipThy\config.json`, fills in defaults for missing fields, validates values, and saves changes made from the tray. If the JSON is invalid, it uses the defaults, shows a warning notification, and keeps the broken file as `config.json.bad`.

**`HotkeyListener`** parses hotkey strings such as `"Alt+F10"` and registers them with `RegisterHotKey` plus `MOD_NOREPEAT`, so holding the key saves once. It uses a hidden window. It raises `SaveRequested(mode)`, where the mode is `Mixed` or `Multitrack`, and reports any hotkey that failed to register.

**`AudioCapture`** runs as two instances: `Loopback` on the default render device and `Microphone` on the default capture device.
- It is built on NAudio's CoreAudio wrappers (`MMDeviceEnumerator`, `AudioClient`, `AudioCaptureClient`), not `WasapiCapture`, because only the lower-level API returns packet timestamps.
- It uses shared-mode capture: event-driven where that works (Windows 10 supports it for loopback), otherwise polling every 10 ms. The timestamps don't depend on which is used.
- It gets devices with the Console role and follows default-device changes through an endpoint notification callback, reopening the new device.
- It converts every packet to 48 kHz stereo 16-bit PCM: resampling, mono to stereo, and multichannel downmixed to stereo.
- Packets flagged as silent are stored as zeros.
- It pushes `(pcm, captureQpc)` into its `AudioBuffer`.

**`AudioBuffer`** is a time-indexed ring of PCM stored in pooled blocks, with no per-packet allocations.
- `Append(pcm, qpcStart)` adds a packet.
- `Extract(fromQpc, toQpc)` returns exactly that span as contiguous PCM:
  - Consecutive packets are laid end to end.
  - A gap longer than 15 ms becomes silence (nothing playing, device switching, capture restarting). Loopback delivers no packets while nothing is playing.
  - Overlapping data is dropped.
- It trims anything older than clip length + 3 s.
- It is thread-safe: the capture thread writes and the exporter reads.

**`VideoCapture`** runs the capture ffmpeg.
- It builds the command in §5.3 and runs ffmpeg as a child process at normal priority, inside a Windows Job Object with kill-on-close, so ffmpeg never outlives the app.
- It streams stdout into `VideoBuffer`, drains stderr continuously (an undrained pipe would stall ffmpeg), and keeps the last 50 stderr lines for the log.
- It restarts ffmpeg when:
  - ffmpeg exits;
  - there is a 3 s stall with no packets;
  - the PC resumes from sleep;
  - display settings change;
  - the system clock changes;
  - the session is unlocked.
- It stops capture while the session is locked, because the lock screen can't be captured.
- Restart backoff is 1 s → 2 s → 5 s → 10 s → 30 s, and resets after 60 s of healthy running.
- Every restart clears `VideoBuffer`.

**`VideoBuffer`** consumes the MPEG-TS stream in 188-byte packets, stored in pooled blocks.
- It remembers the latest PAT and PMT packets and reads the video PID from the PMT.
- It indexes every PES start: its packet offset, its `captureQpc` (§5.1), and whether it is a keyframe (`random_access_indicator` set).
- It trims to clip length + 2 s of capture time, so a keyframe at or before the cut point always exists.
- `NewestCaptureQpc` is the capture time of the newest complete frame.
- `Snapshot(endQpc, seconds)` returns PAT + PMT, then the packets that:
  - start at the last keyframe with `captureQpc ≤ endQpc − seconds` (or at the oldest keyframe if less has been buffered);
  - stop before the first frame captured after `endQpc`.

  It also returns the chunk's first and last capture times.
- It is cleared when `VideoCapture` restarts ffmpeg.

**`ClipExporter`** turns a press into a file in two stages. The snapshot stage runs for every press straight away. Only the slow stage is queued, so a queued save never depends on buffer contents that may have been trimmed while it waited.

1. **Snapshot** (per press, right away):
   - Record the press time (`endQpc` and local time).
   - Wait until `VideoBuffer.NewestCaptureQpc ≥ endQpc`. This takes about 0.45 s because of the encoder delay, with a minimum of 100 ms so audio packets still in flight arrive, and a timeout of 2 s.
   - Take the video snapshot.
   - Extract both audio buffers for the chunk's capture range, from its first frame to its last frame + 1 frame duration.
   - All of this is held in memory.
2. **Export** (serial queue, at most 3 pending; beyond that the press is refused with a notification):
   - Write `video.ts`, `system.wav` and `mic.wav` to `%TEMP%\ClipThy\<id>\`.
   - Run the export ffmpeg (§5.3) at below-normal priority, writing `clip.mp4` in the same temp folder.
   - Move the file to the final path from `OutputPaths`.
   - Delete the temp folder and report the result.
   - A half-written file therefore never appears in the ShareX folder.

**`OutputPaths`**: `Build(root, localTime)` returns `root\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4` and creates the month folder. If the name is taken it appends `_2`, `_3`, and so on; this is checked at move time. The timestamp is the press time.

**`TrayApp`** is the composition root and the only UI. It handles:
- tray icon and menu, tooltip status, notifications and the save sound;
- the single-instance mutex;
- the autostart registry value;
- logging setup;
- cleaning up leftover `%TEMP%\ClipThy\` folders at startup;
- the `--sync-test` mode (§10), which skips the single-instance check so it can run beside the tray instance.

### 5.3 ffmpeg commands (tested on the target PC)

Capture (video only):

```
ffmpeg -hide_banner -nostdin -nostats -loglevel warning
  -copyts -use_wallclock_as_timestamps 1
  -f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=1
  -c:v h264_mf -hw_encoding 1 -scenario live_streaming -b:v 20M -g 60
  -muxdelay 0 -muxpreload 0
  -f mpegts -flush_packets 1 pipe:1
```

- `-copyts -use_wallclock_as_timestamps 1 -muxdelay 0 -muxpreload 0` makes every PTS the frame's UTC capture time (§5.1).
- `-hw_encoding 1` refuses Microsoft's software H.264 encoder, so a missing hardware encoder is reported as an error instead of silently eating CPU.
- `-g` is set to the configured fps, which gives a keyframe every second so clips start within 1 s of the requested length.

Export, mixed (Alt+F10):

```
ffmpeg -hide_banner -nostdin -loglevel error
  -i video.ts -i system.wav -i mic.wav
  -filter_complex "[1:a][2:a]amix=inputs=2:normalize=0,alimiter=limit=0.98:level=0:latency=1[mix]"
  -map 0:v -map "[mix]" -c:v copy -c:a aac -b:a 192k
  -metadata:s:a:0 handler_name=Mix -movflags +faststart clip.mp4
```

Export, multitrack (Alt+F11): uses the same filter and adds:

```
  -map 1:a -map 2:a
  -metadata:s:a:1 handler_name=System -metadata:s:a:2 handler_name=Mic
  -disposition:a:0 default -disposition:a:1 0 -disposition:a:2 0
```

- `normalize=0` keeps both sources at their original level.
- `alimiter` prevents clipping when both sources are loud:
  - `level=0` turns off its auto-gain, which is on by default;
  - `latency=1` compensates its 5 ms lookahead, which would otherwise shift the audio.
- MP4 stores track names in `handler_name`; the `title` tag is dropped.
- Track 1 (Mix) is the default track, so players that only play one track still play everything.

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

- **Validation:** `clipSeconds` must be 5–120, `fps` must be 30 or 60, and `videoBitrateMbps` must be 2–100. An invalid value falls back to its default and shows a warning.
- **Memory:** the RAM needed grows with clip length × bitrate (§9), which is why the clip length is capped at 120 s.
- **Tray changes** (clip length) are written back immediately.
- **Hand edits** to the file take effect the next time the app starts. The app does not watch the file.
- **Start with Windows** is not in the config. The `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `ClipThy` is the source of truth.
  - It is created on first run, meaning when no config file exists yet.
  - It is toggled from the tray.
  - While it is enabled, it is rewritten to the current exe path at every start, so moving the app doesn't break autostart.

## 7. Tray UI

Menu:
- Clip length ▸ 15 s / 30 s / 60 s / 120 s. The current length has a check mark. A custom length from the config appears as an extra checked entry.
- Save clip now (mixed)
- Open clips folder (this month's folder, or the root if it doesn't exist yet)
- Start with Windows ✓
- Exit (waits up to 10 s for queued exports to finish)

Tooltip: one of `ClipThy – recording (30 s)`, `ClipThy – paused (locked)`, `ClipThy – restarting capture…` or `ClipThy – error: <short reason>`. The icon is a red dot while recording and grey otherwise.

On save, a short sound plays (if `playSound` is true) and a notification appears, such as "Clip saved – 30 s". The notification shows the actual length, for example 12 s just after startup. Clicking it opens Explorer with the file selected. If a save fails, the notification gives the reason and no sound plays.

Changing the clip length to a shorter one takes effect immediately. Changing it to a longer one fills up over time.

Known behaviour:
- **Save sound and notification:** these are recorded like anything else, so they can appear in the *next* clip if the two overlap. Setting `playSound: false` turns the sound off.
- **Games:** Windows Focus Assist hides notifications during games by default, so the sound is the main in-game feedback.
- **Mic indicator:** Windows shows its "microphone in use" icon for as long as ClipThy runs, because the mic is always being buffered.

## 8. Error handling

| Situation | Behaviour |
|---|---|
| ffmpeg exits, or outputs no packets for 3 s (resolution change, UAC secure desktop, driver reset) | Restart with backoff and clear the video buffer. The tooltip shows "restarting". After 5 failures in a row, **one** notification per failure streak, with ffmpeg's last error line |
| Session locked | Capture stops. There is no failure notification, and capture restarts on unlock |
| Sleep/resume, display settings change, system clock change | Restart video capture right away. On resume, audio capture is reopened too |
| Default audio device changes or is unplugged (for example Quest Link switching the default) | `AudioCapture` reopens the new default. The gap is silent in clips |
| No microphone, or mic access blocked in Windows privacy settings | The mic track is silent, so the track layout stays the same. One notification explains why |
| Hotkey registration fails (for example the NVIDIA overlay owns Alt+F10) | Notification at startup naming the hotkey. "Save clip now" still works |
| ffmpeg not found, or the hardware H.264 encoder is unavailable | Error notification and error tooltip. The app stays in the tray; fix the cause and restart the app |
| Save pressed before anything is buffered (just started, or restarting) | "Nothing to save yet" notification |
| Save pressed with less buffered than the clip length | Saves what is there. The notification shows the actual length |
| Save pressed while 3 exports are already pending | "Still saving – try again in a moment" notification |
| Export fails (disk full, folder not writable) | Notification with the reason. The temp folder is deleted and capture continues |
| Second instance launched | Exits immediately |
| Protected content (DRM video) | Recorded as black. This is a Windows limitation |
| Windows HDR turned on | Colours may look washed out, because 8-bit capture is out of scope |

The log is `%LOCALAPPDATA%\ClipThy\clipthy.log` and rolls over to `clipthy.1.log` at 1 MB. It records:
- the settings at startup;
- restarts, with the ffmpeg stderr tail;
- saves (path, clip length, time taken);
- timestamp sanity warnings (§5.1);
- errors.

## 9. Resource budget

Measured (capture ffmpeg, idle desktop): about 3 % of one CPU core and a 65–75 MB working set.

Acceptance targets:
- **CPU:** ≤ 2 % in Task Manager on an idle desktop, app and ffmpeg combined.
- **RAM:** ≤ 300 MB with a 30 s clip length, app and ffmpeg combined.
  - The video buffer is bitrate × (clip length + 2 s), which is about 80 MB at 30 s and about 305 MB at 120 s (20 Mbps, moving content). Static content uses far less.
  - The audio buffers are about 0.38 MB/s for both streams, which is about 13 MB at 30 s and about 47 MB at 120 s.
  - With the 120 s length, expect roughly 450 MB in total.
- **Games:** small FPS impact and smooth clips, measured in one GPU-heavy game.
- **Saving:** a 30 s clip is saved in ≤ 3 s after the press. This includes the ~0.45 s wait for the encoder.
- **Disk:** no writes except when saving and logging.

Implementation notes for lightness:
- Pooled buffers, so there are no per-packet allocations.
- Workstation non-concurrent GC.
- The export ffmpeg runs at below-normal priority.

## 10. Testing

**Unit tests** (xUnit, no hardware):
- `VideoBuffer`:
  - PES and keyframe indexing on a real TS file generated by ffmpeg;
  - UTC-PTS → QPC mapping, including the 33-bit wraparound and the out-of-range fallback;
  - trimming;
  - a snapshot starts on a keyframe with PAT/PMT first and ends at the right frame.
- `AudioBuffer`: exact-span extraction, gaps becoming silence, overlaps, trimming.
- `ClipExporter` with fakes:
  - the snapshot is taken before queuing;
  - the wait for the video buffer and its timeout;
  - the limit of 3 pending exports.
- `OutputPaths`: month folder, naming, collisions, month rollover.
- `Settings`: defaults, partial file, invalid values, invalid JSON.
- Parsing hotkey strings.
- Building export arguments for both modes.

**Integration tests** (real ffmpeg and hardware encoder, no screen):
- `testsrc2=…,realtime` replaces `ddagrab` as the input to `VideoCapture` and `VideoBuffer`. Use the `realtime` filter, **not** `-re`: `-re` stalls when combined with wall-clock timestamps (verified).
- Synthetic sine-wave packets feed both `AudioBuffer`s.
- Both modes are exported, then `ffprobe` checks:
  - duration is 30–31 s;
  - streams are 1 video + 1 audio, or 1 video + 3 audio;
  - codecs are H.264 and AAC;
  - `handler_name` values are correct;
  - only track 1 is marked as default;
  - `moov` is at the front of the file (faststart).
- **PTS wraparound:** use `-output_ts_offset` to push the stream across the 33-bit wrap mid-clip. The exported MP4 must be continuous with the correct duration.
- **Restart:** killing ffmpeg triggers a restart and clears the video buffer.

**Sync test:**
- `ClipThy.exe --sync-test` shows a window on the primary monitor that flashes white and beeps at the same moment once per second. It plays the beep through the default output device.
- `tools\sync-check.ps1` analyses a saved clip with ffmpeg. It compares brightness jumps with beep onsets and prints the mean A/V offset over all the flashes.
- The target is |mean offset| ≤ 40 ms, both right after startup and after at least 4 h of running.

**Manual checklist** (on this PC):
- the resource targets in §9;
- colours match the screen;
- music keeps its volume while ClipThy runs (no communications ducking, §11);
- headphones unplugged mid-session;
- default mic switched, and Quest Link started and stopped;
- lock/unlock;
- sleep/resume;
- resolution change;
- clip smoothness in a GPU-heavy fullscreen game;
- Alt+F10 does not clash with the NVIDIA overlay.

## 11. Risks to check first in implementation

1. **Communications ducking.** With Windows' default setting, a capture stream that Windows treats as communications makes it lower all other sounds by 80 % for as long as ClipThy runs. Check this first:
   - open the mic with the Console role and the `Other` stream category;
   - play music and confirm its volume doesn't drop.

   If it can't be avoided in code, the documented fix is Sound settings → Communications → "Do nothing".
2. **Colours.** Media Foundation does the BGRA→YUV conversion internally, with an unknown matrix and range. Compare a clip with the screen. If it's off, convert on the GPU first with `scale_d3d11=format=nv12`. That path works, but in a 10 s test its delay kept growing, so it needs checking before use.
3. **Smoothness under GPU load.** ddagrab peaks at about 64 fps here and about 0.3 % of frames drop even on an idle desktop. Check clip smoothness in a GPU-heavy game.
4. **Display-mode changes and the secure desktop.** Find out whether ddagrab exits with an error or freezes. The 3 s stall watchdog covers both.
5. **Clock agreement between ffmpeg and .NET** (measured within about ±25 ms). The sync test verifies it. If it shows a stable bias, add a constant correction setting.

## 12. Project layout

```
E:\ClipThy\
  ClipThy.sln
  src\ClipThy\            WinExe, net10.0-windows, WinForms, NAudio
  tests\ClipThy.Tests\    xUnit
  tools\sync-check.ps1
  docs\superpowers\specs\
```

It is published as a framework-dependent build, using the installed .NET 10 Windows Desktop runtime. ffmpeg is taken from PATH, and the path can be changed in the config.
