using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MidiAutoPlayer.Bridge;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Services;
using MidiAutoPlayer.Services;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.UI;

public partial class MainForm : Form
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly WebView2 _web = new();
    private readonly SongLibrary _library;
    private readonly AppConfig _config;
    private readonly WheelController _wheelController;
    private readonly WebEventPublisher _eventPublisher;
    private readonly LibraryService _libraryService;
    private readonly PlaybackService _playbackService;
    private readonly CalibrationService _calibrationService;
    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;
    private readonly AnnouncementCheckService _announcementCheckService;
    private readonly ClientPresenceService _clientPresenceService;
    private readonly RecommendedScoreService _recommendedScoreService;
    private readonly PianoProfileService _pianoProfileService;
    private readonly PianoSessionService _pianoSessionService;
    private readonly AudioTranscriptionService _audioTranscriptionService;
    private readonly GtpConversionService _gtpConversionService;
    private readonly WebMessageHandler _bridge;

    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _guitarConfirmHotkey;
    private readonly System.Windows.Forms.Timer _guitarConfirmTimeoutTimer = new() { Interval = 30_000 };
    private bool _awaitingGuitarStartConfirmation;
    private bool _guitarStartConfirmed;
    private bool _allowCloseAfterAudioShutdown;
    private bool _audioShutdownInProgress;
    private string _activePageId = "";
    private GlobalHotkey? _overlayHotkey;
    private Nte.GuitarChart.WinForms.OverlayWindow? _overlayWindow;
    private Nte.GuitarChart.WinForms.SemiAutoController? _semiAutoController;
    private int _overlayChartRequest;
    private readonly object _semiAutoPositionLock = new();
    // The game opens on P1. P5/S1 is the open-string position.
    private int _semiAutoCurrentPage = 1;
    private IntPtr _semiAutoPositionWindow;
    private bool _semiAutoPositionInitialized;
    private readonly System.Windows.Forms.Timer _windowCheckTimer;

    private static string ConfigPath
    {
        get
        {
            string newPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NTE-GUITAR", "config.json");
            string oldPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MidiAutoPlayer", "config.json");

            if (!File.Exists(newPath) && File.Exists(oldPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    File.Copy(oldPath, newPath, true);
                }
                catch { }
            }
            return newPath;
        }
    }

    public MainForm()
    {
        _library = new SongLibrary();
        _config = LoadConfig();
        _wheelController = new WheelController(_config.WheelPreset);
        _eventPublisher = new WebEventPublisher(SynchronizationContext.Current);

        _libraryService = new LibraryService(_library, this);
        _libraryService.SeedBuiltInScoresIfNecessary();
        _playbackService = new PlaybackService(_config, _eventPublisher, _wheelController);
        _playbackService.ConfirmGuitarStart = advice =>
        {
            if (_guitarStartConfirmed)
            {
                _guitarStartConfirmed = false;
                _awaitingGuitarStartConfirmation = false;
                _guitarConfirmTimeoutTimer.Stop();
                _guitarConfirmHotkey?.Unregister();
                return true;
            }

            if (_awaitingGuitarStartConfirmation) return false;

            string mode = advice?.InitialModeName ?? "吉他品格模式";
            int page = advice?.InitialPage ?? 1;
            string pageName = page == 1 ? "P1（第 1 页）" : $"P{page}";
            if (_guitarConfirmHotkey?.Register(0x0D) != true)
            {
                _eventPublisher.Publish("playback.startConfirmationRequired", new
                {
                    message = "Enter 键监听注册失败，请重新点击播放后再试。"
                });
                return false;
            }

            _awaitingGuitarStartConfirmation = true;
            _guitarConfirmTimeoutTimer.Stop();
            _guitarConfirmTimeoutTimer.Start();
            _eventPublisher.Publish("playback.startConfirmationRequired", new
            {
                message = $"请将游戏切回 {mode} · {pageName}，然后按 Enter 开始（30 秒内有效）。"
            });
            return false;
        };
        _playbackService.CancelPendingGuitarStartConfirmation = CancelPendingGuitarStartConfirmation;
        _guitarConfirmTimeoutTimer.Tick += (_, _) =>
        {
            CancelPendingGuitarStartConfirmation();
            _eventPublisher.Publish("playback.startConfirmationRequired", new
            {
                message = "吉他起点确认已超时，请重新点击播放并按 Enter。"
            });
        };
        _playbackService.CurrentSongChanged += OnCurrentSongChanged;
        _calibrationService = new CalibrationService(_config, _wheelController, SaveConfig, TrySaveCalibrationConfigAtomically);
        _wallpaperService = new WallpaperService();
        _settingsService = new SettingsService(_config, SaveConfig, _wallpaperService);
        _announcementCheckService = new AnnouncementCheckService(_config, SaveConfig);
        var clientVersion = typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ClientReportedVersion")?.Value
            ?? typeof(MainForm).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        _clientPresenceService = new ClientPresenceService(
            new Uri("http://81.70.228.51:2888/api/client-presence/heartbeat"),
            clientVersion);
        _recommendedScoreService = new RecommendedScoreService();
        _pianoProfileService = new PianoProfileService(_config, SaveConfig);
        _pianoSessionService = new PianoSessionService(_config, _eventPublisher);
        _audioTranscriptionService = new AudioTranscriptionService((name, data) => _eventPublisher.Publish(name, data));
        _gtpConversionService = new GtpConversionService(this);

        _bridge = new WebMessageHandler(
            _libraryService,
            _playbackService,
            _calibrationService,
            _settingsService,
            config: _config,
            saveConfigCallback: SaveConfig,
            minimizeCallback: () => WindowState = FormWindowState.Minimized,
            closeCallback: () => Close(),
            setWindowSizeLockCallback: SetWindowSizeLock,
            restoreDefaultWindowSizeCallback: RestoreDefaultWindowSize,
            setTopMostCallback: SetTopMost,
            getTopMostStateCallback: GetTopMostState,
            calibrationRebindCallback: RebindForegroundGameWindowAsync,
            calibrationAutoBindCallback: AutoBindAndDetectGuitarSlotsAsync,
            openSemiAutoCallback: OpenSemiAutoOverlayAsync,
            toggleSemiAutoCallback: ToggleSemiAutoOverlayAsync,
            setSemiAutoThemeCallback: SetSemiAutoThemeAsync,
            exportGuitarChartPdfCallback: ExportGuitarChartPdfAsync,
            openSemiAutoWithModeCallback: OpenSemiAutoOverlayWithModeAsync,
            rebindGlobalHotkeyCallback: ApplyGlobalHotkey,
            announcementCheckService: _announcementCheckService,
            recommendedScoreService: _recommendedScoreService,
            pianoProfiles: _pianoProfileService,
            pianoSession: _pianoSessionService,
            setActivePageCallback: pageId => _activePageId = pageId,
            gtpConversion: _gtpConversionService,
            wallpaperService: _wallpaperService,
            audioTranscription: _audioTranscriptionService,
            eventPublisher: _eventPublisher,
            pickAudioCallback: PickAudioFileAsync,
            pickModelFolderCallback: PickModelFolderAsync
        );

        _windowCheckTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _windowCheckTimer.Tick += OnWindowCheckTick;
        _windowCheckTimer.Start();

        FormClosed += (_, _) =>
        {
            _clientPresenceService.Dispose();
            _pianoSessionService.Dispose();
            _recommendedScoreService.Dispose();
        };
        FormClosing += OnMainFormClosing;

        InitializeShell();
    }

    private Task<object?> PickAudioFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",
            Filter = "音频文件 (*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.aac)|*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.aac|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return Task.FromResult<object?>(new { canceled = true });
        return Task.FromResult<object?>(new { canceled = false, audioPath = dialog.FileName, name = Path.GetFileNameWithoutExtension(dialog.FileName) });
    }

    private Task<object?> PickModelFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 SheetSage2 和 MERT-v2-FullSong 文件夹的模型目录" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return Task.FromResult<object?>(new { canceled = true });
        try
        {
            _audioTranscriptionService.SetModelRoot(dialog.SelectedPath);
            return Task.FromResult<object?>(new { canceled = false, path = dialog.SelectedPath });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object?>(new { canceled = false, error = ex.Message });
        }
    }

    private async void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowCloseAfterAudioShutdown) return;
        e.Cancel = true;
        if (_audioShutdownInProgress) return;
        _audioShutdownInProgress = true;
        Enabled = false;
        try
        {
            await _audioTranscriptionService.DisposeAsync();
        }
        catch (Exception ex)
        {
            try { _eventPublisher.Publish("audioTranscription.shutdownFailed", new { error = ex.Message }); } catch { }
        }
        _allowCloseAfterAudioShutdown = true;
        if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(Close));
    }

    private void InitializeShell()
    {
        Text = "NTE-GUITAR V3.4";
        Icon = AppIcon.Value;

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int initW = Math.Clamp(1080, 760, workArea.Width - 20);
        int initH = Math.Clamp(700, 480, workArea.Height - 20);
        Size = new Size(initW, initH);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 21, 27);
        ForeColor = Color.FromArgb(241, 242, 247);

        InitializeWindowSizingPolicy();

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(20, 21, 27);
        Controls.Add(_web);

        Shown += async (_, _) =>
        {
            _clientPresenceService.Start();
            await InitializeWebViewAsync();
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NTE-GUITAR", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            bool isSingleFile = string.IsNullOrWhiteSpace(System.Reflection.Assembly.GetEntryAssembly()?.Location);
            if (isSingleFile)
            {
                // In single-file releases, prefer the web bundle embedded in the EXE;
                // a stale sidecar wwwroot must not override a newly replaced executable.
                webRoot = Utils.EmbeddedAssetExtractor.EnsureWwwRootExtracted();
            }
            else if (!Directory.Exists(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
            {
                // Fallback for development if not yet copied to output.
                string devWebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\wwwroot"));
                if (Directory.Exists(devWebRoot) && File.Exists(Path.Combine(devWebRoot, "index.html"))) webRoot = devWebRoot;
                else webRoot = Utils.EmbeddedAssetExtractor.EnsureWwwRootExtracted();
            }

            string webHost = $"nte-{typeof(MainForm).Assembly.ManifestModule.ModuleVersionId:N}.local";
            core.SetVirtualHostNameToFolderMapping(
                webHost,
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            Directory.CreateDirectory(_wallpaperService.WallpaperDirectory);
            core.SetVirtualHostNameToFolderMapping(
                "nte-media.local",
                _wallpaperService.WallpaperDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            _eventPublisher.Attach(core);

            core.WebMessageReceived += async (_, e) =>
            {
                await _bridge.HandleMessageAsync(e.WebMessageAsJson, json =>
                {
                    if (InvokeRequired)
                        BeginInvoke(() => core.PostWebMessageAsJson(json));
                    else
                        core.PostWebMessageAsJson(json);
                });
            };

            core.Navigate($"https://{webHost}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 初始化失败: {ex.Message}\n\n请确保已安装 Microsoft Edge WebView2 运行时。",
                "运行时错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnWindowCheckTick(object? sender, EventArgs e)
    {
        // Automatically check if game window is detected
        if (_config.TargetWindowHandle == IntPtr.Zero)
        {
            IntPtr fg = KeyboardSender.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != Handle)
            {
                string title = KeyboardSender.GetWindowTitle(fg);
                if (!string.IsNullOrWhiteSpace(title) &&
                    (title.Contains("鸣潮", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Wuthering", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("原神", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Genshin", StringComparison.OrdinalIgnoreCase)))
                {
                    _calibrationService.BindGameWindow(fg, title);
                }
            }
        }
    }

    private async Task<object?> RebindForegroundGameWindowAsync()
    {
        // 避免只在固定时刻抓一次导致用户切窗稍慢就失败；连续两次捕获到同一有效外部窗口后再绑定。
        const int timeoutMs = 8_000;
        const int pollIntervalMs = 100;
        const int stableSamplesRequired = 2;
        var timer = Stopwatch.StartNew();
        IntPtr lastForeground = IntPtr.Zero;
        int stableSamples = 0;
        bool sawExternalWindow = false;
        bool sawUsableExternalWindow = false;

        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr foreground = KeyboardSender.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out uint processId);
                if (processId != 0 && processId != (uint)Environment.ProcessId)
                {
                    sawExternalWindow = true;
                    if (MouseSender.GetWindowScreenInfo(foreground, out _, out _, out _))
                    {
                        sawUsableExternalWindow = true;
                        stableSamples = foreground == lastForeground ? stableSamples + 1 : 1;
                        lastForeground = foreground;

                        if (stableSamples >= stableSamplesRequired)
                        {
                            string title = KeyboardSender.GetWindowTitle(foreground);
                            if (string.IsNullOrWhiteSpace(title) || title == "(Unnamed Window)")
                            {
                                try { title = Process.GetProcessById((int)processId).ProcessName; }
                                catch { title = "未命名游戏窗口"; }
                            }

                            return new { success = true, reason = (string?)null, status = _calibrationService.BindGameWindow(foreground, title) };
                        }
                    }
                    else
                    {
                        stableSamples = 0;
                        lastForeground = IntPtr.Zero;
                    }
                }
                else
                {
                    stableSamples = 0;
                    lastForeground = IntPtr.Zero;
                }
            }

            await Task.Delay(pollIntervalMs);
        }

        string reason = !sawExternalWindow
            ? "8 秒内没有检测到前台游戏窗口，请重新绑定并切换到游戏。"
            : !sawUsableExternalWindow
                ? "检测到了外部窗口，但无法读取其客户区；请确认游戏窗口已打开且未被权限限制。"
                : "检测到的窗口未能稳定保持在前台，请重新绑定后切到游戏并停留片刻。";
        return new { success = false, reason, status = _calibrationService.GetStatus() };
    }

    private async Task<object?> AutoBindAndDetectGuitarSlotsAsync()
    {
        _calibrationService.CancelAutoCalibration();
        const int timeoutMs = 8_000;
        const int pollIntervalMs = 100;
        const int stableSamplesRequired = 2;
        string expected = (_config.WheelPreset.ProcessName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(expected) || expected.Equals("Game", StringComparison.OrdinalIgnoreCase))
            expected = "异环";

        var timer = Stopwatch.StartNew();
        IntPtr previous = IntPtr.Zero;
        IntPtr stable = IntPtr.Zero;
        uint stableProcessId = 0;
        int stableCount = 0;
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr foreground = KeyboardSender.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != Handle)
            {
                GetWindowThreadProcessId(foreground, out uint processId);
                string processName = string.Empty;
                try { if (processId != 0) processName = Process.GetProcessById((int)processId).ProcessName; }
                catch { }
                string title = KeyboardSender.GetWindowTitle(foreground);
                bool isHuanhuan = processName.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                                  title.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                                  processName.Contains("Huanhuan", StringComparison.OrdinalIgnoreCase) ||
                                  title.Contains("Huanhuan", StringComparison.OrdinalIgnoreCase);
                bool usable = isHuanhuan && MouseSender.GetWindowScreenInfo(foreground, out _, out var size, out _) &&
                              size.Width >= 320 && size.Height >= 240;
                if (usable)
                {
                    stableCount = foreground == previous ? stableCount + 1 : 1;
                    previous = foreground;
                    stable = foreground;
                    stableProcessId = processId;
                    if (stableCount >= stableSamplesRequired) break;
                }
                else
                {
                    previous = IntPtr.Zero;
                    stable = IntPtr.Zero;
                    stableProcessId = 0;
                    stableCount = 0;
                }
            }
            else
            {
                previous = IntPtr.Zero;
                stable = IntPtr.Zero;
                stableProcessId = 0;
                stableCount = 0;
            }
            await Task.Delay(pollIntervalMs);
        }

        if (stable == IntPtr.Zero)
        {
            _calibrationService.CancelAutoCalibration();
            return new { success = false, reason = $"8 秒内没有稳定检测到《异环》窗口（目标标识：{expected}），请切换到游戏后重试。" };
        }

        GetWindowThreadProcessId(stable, out uint currentProcessId);
        if (KeyboardSender.GetForegroundWindow() != stable || currentProcessId != stableProcessId ||
            !MouseSender.GetWindowScreenInfo(stable, out var clientOrigin, out var clientSize, out _))
        {
            _calibrationService.CancelAutoCalibration();
            return new { success = false, reason = "异环窗口已失焦或客户区不可用，请重试。" };
        }

        using var screenshot = new Bitmap(clientSize.Width, clientSize.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(screenshot))
                graphics.CopyFromScreen(clientOrigin, Point.Empty, clientSize, CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            _calibrationService.CancelAutoCalibration();
            return new { success = false, reason = $"无法读取异环画面：{ex.Message}。请切换窗口显示模式或使用手动校准。" };
        }

        var detector = new GuitarSlotAutoCalibrator();
        var seeds = _config.WheelPreset.Slots.Select(p => (p.X, p.Y)).ToArray();
        var detection = await Task.Run(() => detector.Detect(screenshot, seeds));
        IReadOnlyList<SlotDetectionCandidate> previewCandidates;
        bool manualFallback = !detection.Success;
        string notice;
        if (detection.Success)
        {
            previewCandidates = detection.Candidates;
            notice = "匹配分为内部排序值，不代表识别准确率。请核对六个标记后确认。";
        }
        else
        {
            if (seeds.Length != 6 || seeds.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X is < 0 or > 1 || p.Y is < 0 or > 1))
            {
                _calibrationService.CancelAutoCalibration();
                return new { success = false, reason = detection.Reason ?? "自动识别失败，且现有六槽坐标不可用于手动预览。" };
            }

            previewCandidates = seeds.Select((point, index) => new SlotDetectionCandidate(index + 1, point.X, point.Y, 0, 0, 0)).ToArray();
            notice = $"自动识别未完成：{detection.Reason} 当前标记为已保存坐标；请在截图上拖动六个编号圆点校正后再确认。";
        }

        string detectedTitle = KeyboardSender.GetWindowTitle(stable);
        if (string.IsNullOrWhiteSpace(detectedTitle) || detectedTitle == "(Unnamed Window)")
        {
            GetWindowThreadProcessId(stable, out uint processId);
            try { detectedTitle = Process.GetProcessById((int)processId).ProcessName; }
            catch { detectedTitle = "异环"; }
        }

        _calibrationService.StageAutoCalibration(stable, stableProcessId, detectedTitle, clientSize, previewCandidates, manualFallback);
        string preview = await Task.Run(() => EncodeCalibrationPreview(screenshot));
        return new
        {
            success = true,
            title = detectedTitle,
            clientWidth = clientSize.Width,
            clientHeight = clientSize.Height,
            previewImage = preview,
            candidates = previewCandidates,
            manualFallback,
            notice
        };
    }

    private static string EncodeCalibrationPreview(Bitmap source)
    {
        const int maxWidth = 1280;
        int width = Math.Min(source.Width, maxWidth);
        int height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        using var preview = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        using var stream = new MemoryStream();
        preview.Save(stream, ImageFormat.Jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private bool TrySaveCalibrationConfigAtomically()
    {
        string? tempPath = null;
        try
        {
            string path = ConfigPath;
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"config.auto-calibration.{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            if (File.Exists(path)) File.Replace(tempPath, path, null);
            else File.Move(tempPath, path);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save automatic calibration: {ex}");
            return false;
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableImmersiveDarkMode(Handle);
        InitializeTopMostPolicy();
        try
        {
            _hotkey = new GlobalHotkey(Handle, 0xBEEF);
            _hotkey.HotkeyPressed += () =>
            {
                var target = PlaybackHotkeyRouter.Resolve(
                    _overlayWindow?.Visible == true && _semiAutoController != null,
                    _activePageId == "game-piano",
                    _pianoSessionService.IsActive,
                    _playbackService.IsPlaying || _playbackService.IsCountdown || _playbackService.IsPaused);
                switch (target)
                {
                    case PlaybackHotkeyTarget.Overlay:
                        _ = _semiAutoController!.TogglePlaybackAsync();
                        break;
                    case PlaybackHotkeyTarget.Piano:
                        _eventPublisher.Publish("piano.hotkey", null);
                        break;
                    default:
                        _playbackService.TogglePlay();
                        break;
                }
            };
            ApplyGlobalHotkey();

            _guitarConfirmHotkey = new GlobalHotkey(Handle, 0xBEF1);
            _guitarConfirmHotkey.HotkeyPressed += () =>
            {
                if (!_awaitingGuitarStartConfirmation) return;
                _guitarConfirmHotkey.Unregister();
                _guitarConfirmTimeoutTimer.Stop();
                _awaitingGuitarStartConfirmation = false;
                _guitarStartConfirmed = true;
                _eventPublisher.Publish("playback.startConfirmationRequired", new
                {
                    message = "起点已确认，开始吉他演奏。"
                });
                BeginInvoke(() => _playbackService.StartPlay());
            };
        }
        catch { }

        try
        {
            _overlayHotkey = new GlobalHotkey(Handle, 0xBEF0);
            _overlayHotkey.HotkeyPressed += () => BeginInvoke(ToggleSemiAutoOverlay);
            _overlayHotkey.Register(0x78); // VK_F9
        }
        catch { }
    }

    private bool ApplyGlobalHotkey() => _hotkey?.Register((uint)_config.GlobalHotkeyVk) == true;

    private void CancelPendingGuitarStartConfirmation()
    {
        _guitarConfirmTimeoutTimer.Stop();
        _guitarConfirmHotkey?.Unregister();
        _awaitingGuitarStartConfirmation = false;
        _guitarStartConfirmed = false;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _windowCheckTimer.Stop();
        _windowCheckTimer.Dispose();
        _hotkey?.Dispose();
        _hotkey = null;
        _guitarConfirmTimeoutTimer.Stop();
        _guitarConfirmTimeoutTimer.Dispose();
        _guitarConfirmHotkey?.Dispose();
        _guitarConfirmHotkey = null;
        _overlayHotkey?.Dispose();
        _overlayHotkey = null;
        _semiAutoController?.Dispose();
        _semiAutoController = null;
        _overlayWindow?.Dispose();
        _overlayWindow = null;
        _playbackService.Dispose();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (_hotkey != null && _hotkey.ProcessMessage(ref m))
        {
            return;
        }
        if (_guitarConfirmHotkey != null && _guitarConfirmHotkey.ProcessMessage(ref m))
        {
            return;
        }
        if (_overlayHotkey != null && _overlayHotkey.ProcessMessage(ref m))
        {
            return;
        }
        if (HandleTopMostWndProc(ref m))
        {
            return;
        }
        base.WndProc(ref m);
        HandleWindowSizeLockWndProc(ref m);
    }

    private async Task EnsureOverlayInitializedAsync()
    {
        if (_overlayWindow == null || _overlayWindow.IsDisposed)
        {
            _overlayWindow = new Nte.GuitarChart.WinForms.OverlayWindow(_config.SemiAutoOverlayWideMode);
            _semiAutoController = new Nte.GuitarChart.WinForms.SemiAutoController(
                _overlayWindow,
                () => CompileCurrentSongChart(),
                autoPosition: ExecuteSemiAutoPosition,
                config: _config,
                saveConfig: SaveConfig,
                listGuitarSongs: query => _libraryService.ListGuitarSongs(query),
                selectGuitarSong: SelectSemiAutoLibrarySongAsync,
                listRecommendedGuitarScores: async query => (object?)await _recommendedScoreService.GetCatalogAsync(query, instrument: "guitar"),
                selectRecommendedGuitarScore: SelectSemiAutoRecommendedScoreAsync);

            string webRoot = Path.Combine(AppContext.BaseDirectory, "Web", "SemiAutoOverlay");
            if (!Directory.Exists(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
            {
                string devRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\Web\SemiAutoOverlay"));
                if (Directory.Exists(devRoot) && File.Exists(Path.Combine(devRoot, "index.html")))
                {
                    webRoot = devRoot;
                }
                else
                {
                    webRoot = Utils.EmbeddedAssetExtractor.EnsureSemiAutoOverlayExtracted();
                }
            }

            var bridgeDispatcher = new Nte.GuitarChart.WinForms.BridgeDispatcher(_semiAutoController);
            var overlayBridge = new Nte.GuitarChart.WinForms.BridgeController(_overlayWindow.WebView, bridgeDispatcher.DispatchAsync);
            await overlayBridge.InitializeAsync(webRoot);
            _semiAutoController.AttachBridge(overlayBridge);

            // Synchronize theme
            string theme = _config.ThemeName.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            await _semiAutoController.SetThemeAsync(theme);
        }
    }

    private Nte.GuitarChart.Core.GuitarChart? CompileCurrentSongChart(string? songId = null)
    {
        var song = !string.IsNullOrEmpty(songId)
            ? _libraryService.GetSong(songId)
            : _playbackService.CurrentSong;

        if (song == null) return null;

        var options = new Nte.GuitarChart.Core.ChartCompileOptions
        {
            SourceName = song.DisplayName,
            PageKeyHoldUs = _config.PageKeyHoldMs * 1000L,
            PageInterTapGapUs = _config.PageInterTapGapMs * 1000L,
            WheelMouseSettleUs = _config.WheelMouseSettleMs * 1000L,
            SafetyMarginUs = _config.PageInputSafetyMs * 1000L,
            UseViterbi = true
        };
        return _libraryService.GetOrGenerateGuitarChart(song.Id, options, extractLeadFromFullSong: true).Chart;
    }

    private async Task<object?> ExportGuitarChartPdfAsync(string songId)
    {
        var song = _libraryService.GetSong(songId);
        if (song == null) return new { success = false, error = "未找到曲目。" };
        string safeName = string.Concat(song.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        using var dialog = new SaveFileDialog
        {
            Title = "导出游戏吉他谱 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(safeName) ? "游戏吉他谱.pdf" : safeName + "_游戏吉他谱.pdf",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return new { success = false, canceled = true };

        try
        {
            var stored = _libraryService.GetOrGenerateGuitarChart(songId, new Nte.GuitarChart.Core.ChartCompileOptions
            {
                SourceName = song.DisplayName,
                PageKeyHoldUs = _config.PageKeyHoldMs * 1000L,
                PageInterTapGapUs = _config.PageInterTapGapMs * 1000L,
                WheelMouseSettleUs = _config.WheelMouseSettleMs * 1000L,
                SafetyMarginUs = _config.PageInputSafetyMs * 1000L,
                UseViterbi = true
            });
            string html = MidiAutoPlayer.SemiAuto.Chart.GuitarChartPdfExporter.BuildHtml(stored);
            string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTE-GUITAR", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            using var host = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Opacity = 0, Size = new Size(900, 650), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
            using var webView = new WebView2 { Dock = DockStyle.Fill };
            host.Controls.Add(webView);
            host.Show();
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(environment);
            var loadedPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? _, CoreWebView2DOMContentLoadedEventArgs __) => loadedPage.TrySetResult(true);
            webView.CoreWebView2.DOMContentLoaded += OnLoaded;
            webView.CoreWebView2.NavigateToString(html);
            await loadedPage.Task.WaitAsync(TimeSpan.FromSeconds(20));
            webView.CoreWebView2.DOMContentLoaded -= OnLoaded;
            bool printed = await webView.CoreWebView2.PrintToPdfAsync(dialog.FileName);
            if (!printed) throw new IOException("WebView2 未能生成 PDF。");
            host.Close();
            var info = new FileInfo(dialog.FileName);
            if (!info.Exists || info.Length == 0) throw new IOException("PDF 文件为空。");
            return new { success = true, filePath = dialog.FileName, bytes = info.Length };
        }
        catch (Exception ex)
        {
            return new { success = false, canceled = false, error = "PDF 导出失败（谱面 JSON 已保留）：" + ex.Message };
        }
    }

    private bool ExecuteSemiAutoPosition(Nte.GuitarChart.Core.GuitarPosition position, int? confirmedCurrentPage = null)
    {
        IntPtr target = _config.TargetWindowHandle;
        if (target == IntPtr.Zero || !KeyboardSender.IsForeground(target)) return false;

        lock (_semiAutoPositionLock)
        {
            if (_semiAutoPositionWindow != target)
            {
                _semiAutoPositionWindow = target;
                _semiAutoCurrentPage = confirmedCurrentPage ?? 1;
                _semiAutoPositionInitialized = false;
            }
            else if (confirmedCurrentPage.HasValue)
            {
                _semiAutoCurrentPage = confirmedCurrentPage.Value;
                _semiAutoPositionInitialized = false;
            }
            if (!_semiAutoPositionInitialized)
            {
                _wheelController.InvalidateCurrentSlot();
                _semiAutoPositionInitialized = true;
            }

            int pageDelta = position.Page - _semiAutoCurrentPage;
            ushort pageKey = pageDelta >= 0
                ? _config.WheelPreset.PageNextScanCode
                : _config.WheelPreset.PagePrevScanCode;
            int steps = Math.Abs(pageDelta);
            int holdMs = Math.Clamp(_config.PageKeyHoldMs, 10, 40);
            int gapMs = Math.Clamp(_config.PageInterTapGapMs, 10, 50);

            for (int i = 0; i < steps; i++)
            {
                if (!KeyboardSender.IsForeground(target)) return false;
                KeyboardSender.SendBatch(new[] { new KeyAction(0, pageKey, isDown: true) });
                Thread.Sleep(holdMs);
                KeyboardSender.SendBatch(new[] { new KeyAction(0, pageKey, isDown: false) });
                if (i < steps - 1) Thread.Sleep(gapMs);
            }

            if (steps > 0)
            {
                _semiAutoCurrentPage = position.Page;
                _wheelController.InvalidateCurrentSlot();
                Thread.Sleep(Math.Clamp(_config.PageInputSafetyMs, 5, 40));
            }

            if (!KeyboardSender.IsForeground(target)) return false;
            bool clicked = _wheelController.SelectSlot(target, position.Slot);
            if (clicked) Thread.Sleep(Math.Clamp(_config.WheelMouseSettleMs, 20, 160));
            return clicked || _wheelController.CurrentSlot == position.Slot;
        }
    }

    public async Task<object?> OpenSemiAutoOverlayAsync(string? songId = null)
    {
        await EnsureOverlayInitializedAsync();
        int request = Interlocked.Increment(ref _overlayChartRequest);
        var chart = await Task.Run(() => CompileCurrentSongChart(songId));
        if (request == Volatile.Read(ref _overlayChartRequest) && _semiAutoController != null)
        {
            if (chart != null)
            {
                InvalidateSemiAutoSlotCache();
                _semiAutoController.LoadChart(chart);
            }
            else _semiAutoController.ClearChart("当前曲目没有可用的吉他 MIDI 音符。");
        }

        _overlayWindow?.Show();
        return new
        {
            opened = true,
            chartId = chart?.ChartId,
            noteCount = chart?.SourceNoteCount ?? 0,
            playabilityRate = chart?.PlayabilityRate ?? 1.0
        };
    }

    private async Task<object?> OpenSemiAutoOverlayWithModeAsync(string? songId, string? mode)
    {
        await EnsureOverlayInitializedAsync();
        var executionMode = mode == "full-manual"
            ? Nte.GuitarChart.Core.PositionExecutionMode.FullManual
            : Nte.GuitarChart.Core.PositionExecutionMode.SemiAuto;
        await _semiAutoController!.SetExecutionModeAsync(executionMode);
        return await OpenSemiAutoOverlayAsync(songId);
    }

    public async Task<object?> ToggleSemiAutoOverlayAsync()
    {
        await EnsureOverlayInitializedAsync();
        if (_overlayWindow != null)
        {
            if (_overlayWindow.Visible)
            {
                _overlayWindow.Hide();
            }
            else
            {
                int request = Interlocked.Increment(ref _overlayChartRequest);
                var chart = await Task.Run(() => CompileCurrentSongChart());
                if (request == Volatile.Read(ref _overlayChartRequest) && _semiAutoController != null)
                {
                    if (chart != null)
                    {
                        InvalidateSemiAutoSlotCache();
                        _semiAutoController.LoadChart(chart);
                    }
                    else _semiAutoController.ClearChart("当前曲目没有可用的吉他 MIDI 音符。");
                }
                _overlayWindow.Show();
            }
        }
        return new { visible = _overlayWindow?.Visible ?? false };
    }

    private void ToggleSemiAutoOverlay()
    {
        _ = ToggleSemiAutoOverlayAsync();
    }

    private async void OnCurrentSongChanged(ConvertedSong song)
    {
        if (_overlayWindow?.Visible != true || _semiAutoController == null) return;

        int request = Interlocked.Increment(ref _overlayChartRequest);
        Nte.GuitarChart.Core.GuitarChart? chart;
        string? compileError = null;
        try { chart = await Task.Run(() => CompileCurrentSongChart(song.Id)); }
        catch (Exception ex) { chart = null; compileError = ex.Message; }
        if (request != Volatile.Read(ref _overlayChartRequest) ||
            _overlayWindow?.Visible != true ||
            _playbackService.CurrentSong?.Id != song.Id ||
            _semiAutoController == null)
            return;

        if (chart != null)
        {
            InvalidateSemiAutoSlotCache();
            _semiAutoController.LoadChart(chart);
        }
        else _semiAutoController.ClearChart(compileError == null
            ? "当前曲目没有可用的吉他 MIDI 音符。"
            : $"新曲目琴谱编译失败：{compileError}");
    }

    private Task SelectSemiAutoLibrarySongAsync(string songId)
    {
        var song = _libraryService.GetSong(songId)
            ?? throw new InvalidOperationException("曲库中找不到所选的吉他曲目。");
        if (SongLibrary.ResolveInstrument(song) != 1)
            throw new InvalidOperationException("所选曲目不是吉他曲目。");

        // PlaybackService raises CurrentSongChanged; the existing handler compiles and
        // publishes the matching overlay chart, keeping the main app and overlay in sync.
        _playbackService.LoadSong(song);
        return Task.CompletedTask;
    }

    private async Task SelectSemiAutoRecommendedScoreAsync(string scoreId)
    {
        var download = await _recommendedScoreService.DownloadAsync(scoreId);
        if (!string.Equals(download.Score.Instrument, "guitar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("悬浮窗当前只支持吉他类推荐谱。");

        object converted = await Task.Run(() => _libraryService.ConvertAndSave(
            download.FilePath,
            download.Score.Title,
            targetInstrument: 1,
            transpose: 0,
            outOfRangeMode: "OctaveFold",
            filterDrumTrack: true,
            enableMelodyExtraction: false,
            melodyStrategy: "FullSong"));
        using var convertedJson = JsonDocument.Parse(JsonSerializer.Serialize(converted));
        string songId = convertedJson.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("推荐谱已转换，但未能取得曲库编号。");
        var song = _libraryService.GetSong(songId)
            ?? throw new InvalidOperationException("推荐谱已转换，但无法从本地曲库读取。");
        _playbackService.LoadSong(song);
    }

    private void InvalidateSemiAutoSlotCache()
    {
        lock (_semiAutoPositionLock)
        {
            _wheelController.InvalidateCurrentSlot();
        }
    }

    private async Task<object?> SetSemiAutoThemeAsync(string? theme)
    {
        string normalizedTheme = theme == "light" ? "light" : "dark";
        string configTheme = normalizedTheme == "light" ? "Light" : "DarkGlass";
        bool currentIsLight = _config.ThemeName.Contains("Light", StringComparison.OrdinalIgnoreCase);
        if (currentIsLight != (normalizedTheme == "light"))
        {
            _config.ThemeName = configTheme;
            SaveConfig();
        }

        if (_semiAutoController != null)
        {
            await _semiAutoController.SetThemeAsync(normalizedTheme);
        }
        return new { theme = normalizedTheme };
    }

    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    bool migrated = false;
                    // 旧版错误地把物理 1~6 弦的 ScanCode 改成游戏键升序，启动时纠正为 6~1。
                    if (cfg.WheelPreset != null)
                    {
                        var normalizedScanCodes = GameScanCodes.NormalizeLegacyStringScanCodes(cfg.WheelPreset.StringScanCodes);
                        if (!ReferenceEquals(normalizedScanCodes, cfg.WheelPreset.StringScanCodes))
                        {
                            cfg.WheelPreset.StringScanCodes = normalizedScanCodes;
                            migrated = true;
                        }
                        if (cfg.SchemaVersion < 3 && cfg.WheelPreset.DownStrumButton == MouseButton.Left &&
                            cfg.WheelPreset.UpStrumButton == MouseButton.Right)
                        {
                            cfg.WheelPreset.DownStrumButton = MouseButton.Right;
                            cfg.WheelPreset.UpStrumButton = MouseButton.Left;
                            migrated = true;
                        }
                    }
                    if (cfg.SchemaVersion < 3) { cfg.SchemaVersion = 3; migrated = true; }
                    if (migrated)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(ConfigPath)!;
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        catch { }
                    }
                    return cfg;
                }
            }
        }
        catch { }

        return new AppConfig();
    }

    private void SaveConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
