# Changelog

All notable changes to ClipThy are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-19

The first public release.

### Added

- Tray app that keeps the last 15, 30, 60 or 120 seconds of the primary monitor, system audio and microphone in memory.
- **Alt+F10** saves a clip with one mixed audio track. **Alt+F11** saves a clip with three tracks: Mix, System and Mic.
- Hardware H.264 through NVIDIA NVENC, with an automatic fallback to Media Foundation if NVENC can't start.
- 60 fps capture with a 1 ms timer for ffmpeg, so frames aren't skipped (measured 0 skipped frames in 2 × 30 s).
- Audio and video are aligned on one capture timeline at save time, so sync doesn't depend on encoder delay.
- Clips go to `Documents\ShareX\Screenshots\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4`, with a soft chime and a notification. Clicking the notification, or the tray icon, shows the clip in Explorer.
- Recovers on its own from sleep, lock/unlock, display changes, clock changes, ffmpeg crashes and audio device changes.
- `config.json` for hotkeys, output folder, encoder, fps, bitrate, sound and ffmpeg path.
- Start with Windows, a rolling log, and a `--sync-test` mode with `tools/sync-check.ps1` for measuring A/V offset and dropped frames.
- Self-contained and framework-dependent builds for 64-bit Windows.

[1.0.0]: https://github.com/ItzBloxy/ClipThy/releases/tag/v1.0.0
