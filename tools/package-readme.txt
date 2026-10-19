ClipThy - instant replay for the Windows desktop
https://github.com/ItzBloxy/ClipThy

QUICK START
 1. Install ffmpeg 8 so it's on PATH. The tested build is gyan.dev's full build:
        winget install Gyan.FFmpeg
    If ClipThy then says it can't capture the screen, sign out and back in so
    it picks up the new PATH.
 2. Put this folder somewhere permanent (for example %LOCALAPPDATA%\Programs\ClipThy)
    and run ClipThy.exe. A red dot appears in the tray while it's recording.
 3. Press Alt+F10 to save the last 30 seconds as an MP4 (one mixed audio track).
    Press Alt+F11 to save with three audio tracks: Mix, System and Mic.

Clips go to Documents\ShareX\Screenshots\yyyy-MM\ by default. Left-click the tray
icon to show the last clip, and right-click it for clip length, "Start with Windows"
and Exit.

ClipThy adds itself to "Start with Windows" on first run. Untick it in the tray
menu if you don't want that.

SETTINGS
%APPDATA%\ClipThy\config.json (hotkeys, output folder, encoder, fps, bitrate,
sound). Edit it while ClipThy isn't running; changes apply at the next start.

LOG
%LOCALAPPDATA%\ClipThy\clipthy.log

The full guide, troubleshooting and source code are at the link above.
