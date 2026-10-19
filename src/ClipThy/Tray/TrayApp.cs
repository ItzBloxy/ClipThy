using System.Diagnostics;
using System.Media;
using ClipThy.Audio;
using ClipThy.Config;
using ClipThy.Export;
using ClipThy.Hotkeys;
using ClipThy.Infrastructure;
using ClipThy.Timing;
using ClipThy.Video;
using Microsoft.Win32;

namespace ClipThy.Tray;

/// <summary>Composition root and the only UI: tray icon, menu, notifications, save sound, system events.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext _ui = new WindowsFormsSynchronizationContext();
    private readonly FileLog _log = new(FileLog.DefaultPath);
    private readonly SettingsStore _store = new(SettingsStore.DefaultPath);
    private readonly Autostart _autostart = new();
    private readonly JobObject _job = new();
    private readonly VideoBuffer _video = new(Stopwatch.Frequency);
    private readonly AudioBuffer _systemAudio = new(Stopwatch.Frequency);
    private readonly AudioBuffer _micAudio = new(Stopwatch.Frequency);
    private readonly AudioCapture _systemCapture;
    private readonly AudioCapture _micCapture;
    private readonly VideoCapture _capture;
    private readonly ClipExporter _exporter;
    private readonly HotkeyListener _hotkeys = new();
    private readonly NotifyIcon _icon = new();
    private readonly Icon _recordingIcon = TrayAssets.Dot(Color.Red);
    private readonly Icon _idleIcon = TrayAssets.Dot(Color.Gray);
    private readonly SoundPlayer _saveSound = TrayAssets.SaveSound();
    private readonly ToolStripMenuItem _lengthMenu = new("Clip length");
    private readonly ToolStripMenuItem _autostartItem = new("Start with Windows");
    private AppSettings _settings;
    private CaptureStatus _status;
    private string? _balloonPath;
    private string? _lastClipPath;

    public TrayApp()
    {
        SettingsLoadResult loaded = _store.Load();
        _settings = loaded.Settings;
        _log.Write($"ClipThy {Application.ProductVersion} starting. Settings: {_settings}");

        string exePath = Environment.ProcessPath!;
        if (loaded.CreatedNew || _autostart.IsEnabled) _autostart.Enable(exePath);

        string tempRoot = Path.Combine(Path.GetTempPath(), "ClipThy");
        ClipExporter.CleanTempRoot(tempRoot);
        ApplyRetention();

        _systemCapture = new AudioCapture(AudioSourceKind.SystemLoopback, _systemAudio, _log.Write);
        _micCapture = new AudioCapture(AudioSourceKind.Microphone, _micAudio, _log.Write);
        _systemCapture.Problem += message => _ui.Post(_ => ShowBalloon("ClipThy audio", message, ToolTipIcon.Warning), null);
        _micCapture.Problem += message => _ui.Post(_ => ShowBalloon("ClipThy audio", message, ToolTipIcon.Warning), null);

        EncoderMode mode = EncoderPolicy.ParseMode(_settings.Encoder);
        int fps = _settings.Fps;
        int bitrate = _settings.VideoBitrateMbps;
        _capture = new VideoCapture(
            _video,
            encoder => CaptureArgs.Build(encoder, fps, bitrate, CaptureArgs.DesktopGraph(fps)),
            _settings.FfmpegPath,
            mode,
            _job,
            _log.Write);
        _status = new CaptureStatus(CaptureState.Starting, EncoderPolicy.Initial(mode));
        _capture.StatusChanged += status => _ui.Post(_ => UpdateStatus(status), null);
        _capture.EncoderFellBack += message => _ui.Post(_ => ShowBalloon("ClipThy", message, ToolTipIcon.Info), null);
        _capture.PersistentFailure += line => _ui.Post(_ => ShowBalloon("ClipThy can't capture the screen", line, ToolTipIcon.Error), null);

        _exporter = new ClipExporter(
            _video,
            _systemAudio,
            _micAudio,
            new ProcessFfmpegRunner(() => _settings.FfmpegPath, OnExportStarted),
            SystemClock.Instance,
            () => _settings,
            tempRoot);
        _exporter.Completed += result => _ui.Post(_ => OnSaved(result), null);

        BuildTrayIcon();
        RegisterHotkeys();
        foreach (string warning in loaded.Warnings) ShowBalloon("ClipThy settings", warning, ToolTipIcon.Warning);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _systemCapture.Start();
        _micCapture.Start();
        _capture.Start();
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_lengthMenu);
        menu.Items.Add("Save clip now", null, (_, _) => RequestSave(SaveMode.Mixed));
        menu.Items.Add("Open clips folder", null, (_, _) => OpenClipsFolder());
        _autostartItem.Checked = _autostart.IsEnabled;
        _autostartItem.Click += (_, _) => ToggleAutostart();
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        RebuildLengthMenu();

        _icon.ContextMenuStrip = menu;
        _icon.BalloonTipClicked += (_, _) => OnNotificationClicked();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RevealClip(_lastClipPath, "Tray icon clicked");
        };
        UpdateStatus(_status);
        _icon.Visible = true;
    }

    private void RebuildLengthMenu()
    {
        _lengthMenu.DropDownItems.Clear();
        IEnumerable<int> lengths = AppSettings.ClipLengthPresets.Contains(_settings.ClipSeconds)
            ? AppSettings.ClipLengthPresets
            : AppSettings.ClipLengthPresets.Append(_settings.ClipSeconds).Order();
        foreach (int seconds in lengths)
        {
            var item = new ToolStripMenuItem($"{seconds} s") { Checked = seconds == _settings.ClipSeconds };
            item.Click += (_, _) => SetClipLength(seconds);
            _lengthMenu.DropDownItems.Add(item);
        }
    }

    private void RegisterHotkeys()
    {
        _hotkeys.SaveRequested += RequestSave;
        TryRegister(_settings.HotkeyMixed, SaveMode.Mixed);
        TryRegister(_settings.HotkeyMultitrack, SaveMode.Multitrack);
    }

    private void TryRegister(string text, SaveMode mode)
    {
        if (Hotkey.TryParse(text, out Hotkey hotkey) && _hotkeys.Register(hotkey, mode)) return;
        _log.Write($"Could not register hotkey {text}.");
        ShowBalloon("ClipThy hotkey unavailable",
            $"{text} is already used by another program. \"Save clip now\" in the tray menu still works; you can change the hotkey in config.json.",
            ToolTipIcon.Warning);
    }

    private void RequestSave(SaveMode mode)
    {
        if (_exporter.RequestSave(mode) == SaveRequestOutcome.Busy)
        {
            ShowBalloon("ClipThy", "Still saving – try again in a moment.", ToolTipIcon.Info);
        }
    }

    private void OnSaved(SaveResult result)
    {
        if (!result.Success)
        {
            _log.Write($"Save failed: {result.Error}");
            ShowBalloon("ClipThy couldn't save the clip", result.Error!, ToolTipIcon.Error);
            return;
        }
        _log.Write($"Saved {result.Path} ({result.LengthSeconds:F1} s, {result.Mode}).");
        _lastClipPath = result.Path;
        if (_settings.PlaySound) _saveSound.Play();
        ShowBalloon($"Clip saved – {Math.Round(result.LengthSeconds)} s", Path.GetFileName(result.Path!), ToolTipIcon.Info, result.Path);
    }

    private void OnExportStarted(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            _job.Add(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Write($"Could not adjust export ffmpeg: {ex.Message}");
        }
    }

    private void SetClipLength(int seconds)
    {
        _settings = _settings with { ClipSeconds = seconds };
        _store.Save(_settings);
        ApplyRetention();
        RebuildLengthMenu();
        UpdateStatus(_status);
    }

    private void ApplyRetention()
    {
        _video.Retention = TimeSpan.FromSeconds(_settings.ClipSeconds + 2);
        _systemAudio.Retention = TimeSpan.FromSeconds(_settings.ClipSeconds + 3);
        _micAudio.Retention = TimeSpan.FromSeconds(_settings.ClipSeconds + 3);
    }

    private void ToggleAutostart()
    {
        if (_autostart.IsEnabled) _autostart.Disable();
        else _autostart.Enable(Environment.ProcessPath!);
        _autostartItem.Checked = _autostart.IsEnabled;
    }

    private void OpenClipsFolder()
    {
        string folder = OutputPaths.MonthFolder(_settings.OutputRoot, DateTime.Now);
        if (!Directory.Exists(folder)) folder = _settings.OutputRoot;
        Directory.CreateDirectory(folder);
        ExplorerReveal.OpenFolder(folder);
    }

    private void OnNotificationClicked()
    {
        if (_balloonPath is null)
        {
            _log.Write("Notification clicked (it has no clip to show).");
            return;
        }
        RevealClip(_balloonPath, "Notification clicked");
    }

    /// <summary>Opens Explorer with the clip selected; with no clip yet, opens the clips folder instead.</summary>
    private void RevealClip(string? path, string trigger)
    {
        if (path is null)
        {
            _log.Write($"{trigger}: no clip saved yet, opening the clips folder.");
            OpenClipsFolder();
            return;
        }
        _log.Write($"{trigger}: showing {path}");
        if (!ExplorerReveal.TryReveal(path, out string? error)) _log.Write($"Could not show the clip: {error}");
    }

    private void UpdateStatus(CaptureStatus status)
    {
        _status = status;
        string encoder = status.Encoder == EncoderKind.Nvenc ? "NVENC" : "Media Foundation";
        string text = status.State switch
        {
            CaptureState.Recording => $"ClipThy – recording ({_settings.ClipSeconds} s, {encoder})",
            CaptureState.Paused => "ClipThy – paused (locked)",
            CaptureState.Failed => $"ClipThy – error: {status.Detail}",
            _ => "ClipThy – restarting capture…",
        };
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _icon.Icon = status.State == CaptureState.Recording ? _recordingIcon : _idleIcon;
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon, string? path = null)
    {
        _balloonPath = path;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _capture.RequestRestart("resumed from sleep");
        _systemCapture.Restart();
        _micCapture.Restart();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _capture.RequestRestart("display settings changed");

    private void OnTimeChanged(object? sender, EventArgs e) => _capture.RequestRestart("system clock changed");

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _capture.Pause();
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _capture.Resume();
    }

    private void Exit()
    {
        _log.Write("ClipThy exiting.");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hotkeys.Dispose();
        _capture.Dispose();
        _systemCapture.Dispose();
        _micCapture.Dispose();
        _exporter.WhenIdleAsync().Wait(TimeSpan.FromSeconds(10));
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}
