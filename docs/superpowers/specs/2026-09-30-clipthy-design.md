# ClipThy — Design Spec

**Date:** 2026-09-30
**Status:** Design approved in brainstorming. Revision 3 moves to NVENC after the NVIDIA driver update, with all encoder facts re-measured on the target PC. The written spec is pending user review.

## 1. Goal

A lightweight Windows tray app that continuously keeps the last N seconds of the primary monitor, system audio and microphone in memory. When a hotkey is pressed, it saves them as an MP4 into the ShareX screenshots folder, sorted by month.

The main quality bar is "lightweight": near-zero CPU, a few hundred MB of RAM at most, no continuous disk writes, and minimal impact on game frame rates.

## 2. Confirmed decisions

| Topic | Decision |
|---|---|
| Captured | Primary monitor (DISPLAY1, 2560×1440) + system audio + default microphone |
| Frame rate | 60 fps |
| Video codec | H.264 on the RTX 3070's NVENC (`h264_nvenc`), 20 Mbps VBR. If NVENC can't start, it falls back automatically to Media Foundation (`h264_mf`) on the same chip |
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
- **Frame pacing and the Windows timer:**
  - ddagrab paces capture with `Sleep()`. On Windows' default 15.625 ms timer it can't hit a 16.7 ms frame grid reliably, and it skips a frame whenever it falls more than one frame behind. It tops out at about 64–79 fps here.
  - On the real desktop at 60 fps, **4–5 % of frames were skipped** by default (1.4–3.4 % in other tests; 0.14 % at 30 fps). The cause is the same with either encoder.
  - Timer resolution has been per-process since Windows 10 2004. ClipThy raising its own timer doesn't help ffmpeg (verified). A registry switch that restores global behaviour appears to be Windows 11-only.
  - **Fix (§5.2):** ClipThy makes its ffmpeg request a 1 ms timer. Measured on the real desktop: **0 skipped frames in 2 × 30 s**, and ffmpeg CPU fell from about 1.5–2 % to 0.6 % of a core.
- This ffmpeg build has no `gfxcapture` source, so ddagrab is the only GPU capture source.

**Encoder**
- The NVIDIA driver is **617.14** (updated 2026-09-30). ffmpeg 8.0.1's `h264_nvenc` needs driver ≥ 570.
- Measured with the capture command in §5.3:
  - ffmpeg uses about 2–3 % of one CPU core and has a working set of about 115 MB.
  - There is a keyframe exactly every 60 frames and there are no B-frames.
  - Encoder delay (capture → packet reaches the reader) is about 10–80 ms.
  - The 20 Mbps target is honoured: a moving 1440p test pattern came out at 22.3 Mbps including TS overhead.
- **Colours:**
  - NVENC converts RGB with the BT.601 matrix and tags the stream `bt470bg`, limited range.
  - Decoded according to that tag, colours match the source (37.4 dB PSNR). A player that assumed BT.709 instead would shift them visibly (24.0 dB).
  - ffmpeg takes the primaries and transfer tags from the frames and silently ignores output options such as `-color_primaries`. The capture graph therefore sets them with `setparams`. Verified: output is `bt709` primaries/transfer with the `bt470bg` matrix.
- `h264_mf` also works on this driver, which makes it the fallback. It is zero-copy, uses 3–4 % of one core, has an 80 MB working set, and a delay of about 35 ms. On the old 566 driver it held frames for about 0.4 s.

**Timestamps**
- ddagrab's own timestamps count from when ffmpeg started, so on their own they can't be matched to audio.
- With `-copyts -use_wallclock_as_timestamps 1`, each frame instead carries the system (UTC) time at which it was captured, before encoding.
- That clock agrees with .NET's `DateTime.UtcNow` within about ±25 ms, with no drift.
- ffmpeg rounds the timestamps to the encoder's 1/60 s time base, so each is within ±8 ms of the true capture time.

**Cutting**
- Cutting the MPEG-TS stream at a keyframe and putting the latest PAT/PMT in front gives a valid MP4 with both encoders. Tested: exact frame count (240 frames = 4.000 s), starts on an I-frame, no decode errors.

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
- ffmpeg 8.0.1 (gyan.dev full build) on PATH also has `amix`, `alimiter`, `setparams` and `scale_d3d11`. winget has an upgrade to 9.0.2 pending.
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

## 5. Architecture

```
Primary monitor ─ ddagrab ─► h264_nvenc ─► MPEG-TS on stdout ─► VideoBuffer   (compressed, RAM)
  (ffmpeg child process, video only; every frame stamped with its UTC capture time)

System audio ─ WASAPI loopback ─► AudioBuffer "system"   (PCM + capture timestamps, RAM)
Microphone   ─ WASAPI capture  ─► AudioBuffer "mic"      (PCM + capture timestamps, RAM)

Hotkey ─► ClipExporter: cut the video at a keyframe N s back, cut both audio buffers to the
          exact same time span, ffmpeg muxes ─► ShareX\Screenshots\yyyy-MM\clip_….mp4
```

Audio never goes through the capture ffmpeg process. Every buffer is indexed by **capture time on one timeline**, and the exporter aligns the three streams on that timeline at save time. This design:

- makes sync independent of encoder delay, which differs by encoder and driver (§3);
- makes long-run clock drift irrelevant, because alignment happens per clip over at most about 2 minutes;
- lets an audio device change happen without ever restarting video capture;
- avoids continuous audio encoding, because audio is encoded only when a clip is saved.

### 5.1 The timeline

The timeline is QPC ticks from `Stopwatch.GetTimestamp()`.

- **Audio:** WASAPI reports each packet's capture time as a QPC position in 100-ns units, which is converted to ticks.
- **Video:** each frame's PTS is its UTC capture time (90 kHz, 33-bit, wrapping every 26.5 h). When a frame's first TS packet arrives, `VideoBuffer` reads `DateTime.UtcNow` and `Stopwatch.GetTimestamp()` back to back and computes:
  - `delay = (arrivalUtc90k − pts) mod 2³³`
  - `captureQpc = arrivalQpc − delay`

  This is accurate to about ±25 ms overall and also handles the wraparound.
- **Sanity check:** a delay outside −1…5 s means the timestamps aren't what we expect. The frame then uses its arrival time and a warning is logged.
  - Small negative delays are normal. ffmpeg's clock can run a few ms ahead of .NET's, and NVENC often delivers a frame within that margin. The integration tests caught this, and the earlier probe saw −12 to −17 ms with the real pipeline.
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
- **1 ms timer for ffmpeg:** right after starting ffmpeg, `ProcessTimerResolution` starts one thread inside it that calls `kernel32!timeBeginPeriod(1)`.
  - kernel32 is mapped at the same address in every process, so nothing is written into ffmpeg.
  - The request ends automatically when ffmpeg exits.
  - If the call fails (for example because security software blocks it), capture continues on the default timer and the reason is logged.
- **Encoder choice:**
  - With `encoder: "auto"` (the default), it starts with NVENC.
  - If ffmpeg's stderr shows that NVENC couldn't start, it switches to Media Foundation for the rest of the session and shows one notification suggesting a driver update. Examples of such messages: "Driver does not support the required nvenc API version", "minimum required Nvidia driver", "Could not open encoder". Such a failure can come back after an ffmpeg upgrade that needs a newer driver.
  - `"nvenc"` or `"mf"` forces one encoder, with no fallback.
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
   - Wait until `VideoBuffer.NewestCaptureQpc ≥ endQpc`. This is usually under 0.1 s with NVENC, with a minimum of 100 ms so audio packets still in flight arrive, and a timeout of 2 s.
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

Capture (video only) with NVENC:

```
ffmpeg -hide_banner -nostdin -nostats -loglevel warning
  -copyts -use_wallclock_as_timestamps 1
  -f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=1,setparams=color_primaries=bt709:color_trc=bt709
  -c:v h264_nvenc -preset p4 -tune ll -rc vbr -b:v 20M -maxrate 30M -bufsize 40M -g 60 -bf 0 -delay 0
  -muxdelay 0 -muxpreload 0
  -f mpegts -flush_packets 1 pipe:1
```

Media Foundation fallback: the same command with the encoder options replaced by:

```
  -c:v h264_mf -hw_encoding 1 -scenario live_streaming -b:v 20M -g 60
```

- `-copyts -use_wallclock_as_timestamps 1 -muxdelay 0 -muxpreload 0` makes every PTS the frame's UTC capture time (§5.1).
- `-tune ll -bf 0 -delay 0`: no B-frames and no output delay, so packets reach ClipThy tens of ms after capture and saves start almost immediately.
- `-maxrate 30M` caps bursts above the 20 Mbps average.
- `-g` is set to the configured fps, which gives a keyframe every second so clips start within 1 s of the requested length.
- `setparams` tags BT.709 primaries and transfer. NVENC tags the BT.601 matrix it actually uses. Output options can't set these tags (§3).
- `-hw_encoding 1` (Media Foundation only) refuses Microsoft's software H.264 encoder, so a missing hardware encoder is reported as an error instead of silently eating CPU.

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
- The video is stream-copied, so its colour tags carry over.

## 6. Settings

The settings file is `%APPDATA%\ClipThy\config.json`. It is created with these defaults on first run:

```json
{
  "clipSeconds": 30,
  "hotkeyMixed": "Alt+F10",
  "hotkeyMultitrack": "Alt+F11",
  "outputRoot": "C:\\Users\\BLOXY\\Documents\\ShareX\\Screenshots",
  "encoder": "auto",
  "fps": 60,
  "videoBitrateMbps": 20,
  "playSound": true,
  "ffmpegPath": "ffmpeg"
}
```

- **Validation:**
  - `clipSeconds` must be 5–120.
  - `encoder` must be `auto`, `nvenc` or `mf`.
  - `fps` must be 30 or 60.
  - `videoBitrateMbps` must be 2–100. `maxrate` is 1.5× this value and `bufsize` is 2×.
  - An invalid value falls back to its default and shows a warning.
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

Tooltip: one of `ClipThy – recording (30 s, NVENC)`, `ClipThy – paused (locked)`, `ClipThy – restarting capture…` or `ClipThy – error: <short reason>`. The icon is a red dot while recording and grey otherwise.

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
| ffmpeg can't be given the 1 ms timer (for example blocked by security software) | Capture continues on the default timer, and the log says so. Expect about 4–5 % skipped frames at 60 fps |
| NVENC can't start (for example an ffmpeg upgrade needs a newer NVIDIA driver) | `auto`: switch to Media Foundation for this session, with one notification suggesting a driver update. `nvenc`: error notification and error tooltip |
| Session locked | Capture stops. There is no failure notification, and capture restarts on unlock |
| Sleep/resume, display settings change, system clock change | Restart video capture right away. On resume, audio capture is reopened too |
| Default audio device changes or is unplugged (for example Quest Link switching the default) | `AudioCapture` reopens the new default. The gap is silent in clips |
| No microphone, or mic access blocked in Windows privacy settings | The mic track is silent, so the track layout stays the same. One notification explains why |
| Hotkey registration fails (for example the NVIDIA overlay owns Alt+F10) | Notification at startup naming the hotkey. "Save clip now" still works |
| ffmpeg not found, or no hardware H.264 encoder at all | Error notification and error tooltip. The app stays in the tray; fix the cause and restart the app |
| Save pressed before anything is buffered (just started, or restarting) | "Nothing to save yet" notification |
| Save pressed with less buffered than the clip length | Saves what is there. The notification shows the actual length |
| Save pressed while 3 exports are already pending | "Still saving – try again in a moment" notification |
| Export fails (disk full, folder not writable) | Notification with the reason. The temp folder is deleted and capture continues |
| Second instance launched | Exits immediately |
| Protected content (DRM video) | Recorded as black. This is a Windows limitation |
| Windows HDR turned on | Colours may look washed out, because 8-bit capture is out of scope |

The log is `%LOCALAPPDATA%\ClipThy\clipthy.log` and rolls over to `clipthy.1.log` at 1 MB. It records:
- the settings and the encoder in use at startup;
- restarts, with the ffmpeg stderr tail;
- encoder fallbacks;
- saves (path, clip length, time taken);
- timestamp sanity warnings (§5.1), with their count;
- errors.

## 9. Resource budget

Measured (capture ffmpeg with NVENC, idle desktop): about 2–3 % of one CPU core and a working set of about 115 MB.

Acceptance targets:
- **CPU:** ≤ 2 % in Task Manager on an idle desktop, app and ffmpeg combined.
- **RAM:** ≤ 300 MB with a 30 s clip length, app and ffmpeg combined. The app is about 40 MB, the buffers are listed below, and ffmpeg is about 115 MB.
  - The video buffer is bitrate × (clip length + 2 s):

    | Clip length | Typical (20 Mbps) | Worst case (30 Mbps cap) |
    |---|---|---|
    | 30 s | ~80 MB | ~120 MB |
    | 120 s | ~305 MB | ~460 MB |

    Static content uses far less.
  - The audio buffers are about 0.38 MB/s for both streams, which is about 13 MB at 30 s and about 47 MB at 120 s.
  - With the 120 s length, expect about 0.5–0.7 GB in total.
- **Games:** small FPS impact and smooth clips, measured in one GPU-heavy game.
- **Saving:** a 30 s clip is saved in ≤ 3 s after the press.
- **Disk:** no writes except when saving and logging.

Implementation notes for lightness:
- Pooled buffers, so there are no per-packet allocations.
- Workstation non-concurrent GC.
- The export ffmpeg runs at below-normal priority.
- The 1 ms timer that ffmpeg requests makes Windows tick faster while ClipThy records. That costs slightly more idle power, which is negligible on a desktop; many games and browsers do the same.

## 10. Testing

**Unit tests** (xUnit, no hardware):
- `VideoBuffer`:
  - PES and keyframe indexing on a real TS file generated by ffmpeg;
  - UTC-PTS → QPC mapping, including the 33-bit wraparound, small negative delays, and the out-of-range fallback;
  - trimming;
  - a snapshot starts on a keyframe with PAT/PMT first and ends at the right frame.
- `AudioBuffer`: exact-span extraction, gaps becoming silence, overlaps, trimming.
- `VideoCapture` encoder choice: recognising NVENC start-up failures from sample stderr lines, `auto` → Media Foundation fallback, and forced modes.
- `ClipExporter` with fakes:
  - the snapshot is taken before queuing;
  - the wait for the video buffer and its timeout;
  - the limit of 3 pending exports.
- `OutputPaths`: month folder, naming, collisions, month rollover.
- `Settings`: defaults, partial file, invalid values, invalid JSON.
- Parsing hotkey strings.
- Building capture and export arguments for both encoders and both modes.

**Integration tests** (real ffmpeg and hardware encoder, no screen). Run them with each encoder (NVENC and Media Foundation).
- `testsrc2=…,realtime` replaces `ddagrab` as the input to `VideoCapture` and `VideoBuffer`. Use the `realtime` filter, **not** `-re`: `-re` stalls when combined with wall-clock timestamps (verified).
- Synthetic sine-wave packets feed both `AudioBuffer`s.
- Both modes are exported, then `ffprobe` checks:
  - duration is 30–31 s;
  - streams are 1 video + 1 audio, or 1 video + 3 audio;
  - codecs are H.264 and AAC;
  - `handler_name` values are correct;
  - only track 1 is marked as default;
  - the video colour tags are `bt470bg` matrix with `bt709` primaries/transfer;
  - `moov` is at the front of the file (faststart).
- **PTS wraparound:** use `-output_ts_offset` to push the stream across the 33-bit wrap mid-clip. The exported MP4 must be continuous with the correct duration.
- **Restart:** killing ffmpeg triggers a restart and clears the video buffer.

**Hardware tests** (need a real microphone, speakers and display):
- Microphone and loopback packets carry current timestamps.
- `Desktop_capture_keeps_a_steady_60_fps` runs the production desktop capture into memory only for about 8 s. Its frames are never saved; the test reads their timestamps and requires ≤ 1 % skipped 1/60 slots. It failed at 2.4 % before the timer fix.

**Sync and smoothness test:**
- `ClipThy.exe --sync-test` shows a window on the primary monitor that flashes white and beeps at the same moment once per second. It plays the beep through the default output device.
- `tools\sync-check.ps1` analyses a saved clip with ffmpeg and prints:
  - the mean A/V offset over all the flashes, found by comparing brightness jumps with beep onsets;
  - the dropped-frame count, found from gaps between frame timestamps.
- The targets are |mean offset| ≤ 40 ms and dropped frames ≤ 1 %, both right after startup and after at least 4 h of running.

**Manual checklist** (on this PC):
- the resource targets in §9;
- colours look right when played in Discord, a browser and Windows Photos;
- music keeps its volume while ClipThy runs (no communications ducking, §11);
- headphones unplugged mid-session;
- default mic switched, and Quest Link started and stopped;
- lock/unlock;
- sleep/resume;
- resolution change;
- clip smoothness in a GPU-heavy fullscreen game, using `sync-check.ps1`'s dropped-frame count;
- Alt+F10 does not clash with the NVIDIA overlay (Instant Replay uses the same key).

## 11. Risks to check first in implementation

1. **Communications ducking.** With Windows' default setting, a capture stream that Windows treats as communications makes it lower all other sounds by 80 % for as long as ClipThy runs. Check this first:
   - open the mic with the Console role and the `Other` stream category;
   - play music and confirm its volume doesn't drop.

   If it can't be avoided in code, the documented fix is Sound settings → Communications → "Do nothing".
2. **Dropped frames at 60 fps — resolved (2026-09-30).**
   - **Cause:** ffmpeg's `Sleep()`-based pacing on the default 15.6 ms timer, which skipped 4–5 % of frames on the real desktop.
   - **Fix:** the per-process 1 ms timer (§5.2), which brought it to 0 skipped frames in 2 × 30 s.
   - **Still to check:** smoothness in a GPU-heavy game with `sync-check.ps1`, because GPU contention could add other delays.
3. **Colours in players.** NVENC's BT.601 matrix is tagged correctly (§3). A player that ignores the tag would shift colours. Check playback in Discord, a browser and Windows Photos.
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
