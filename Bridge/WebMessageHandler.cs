using System.Text.Json;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Piano.Models;
using MidiAutoPlayer.Piano.Services;
using MidiAutoPlayer.Services;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Bridge;

public sealed class WebMessageHandler
{
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly LibraryService _library;
    private readonly PlaybackService _playback;
    private readonly CalibrationService _calibration;
    private readonly SettingsService _settings;
    private readonly WallpaperService? _wallpaper;
    private readonly AppConfig? _config;
    private readonly Action? _saveConfigCallback;
    private readonly Action<string>? _replyCallback;
    private readonly Action? _minimizeCallback;
    private readonly Action? _closeCallback;
    private readonly Func<bool, WindowSizeStateDto>? _setWindowSizeLockCallback;
    private readonly Func<WindowSizeStateDto>? _restoreDefaultWindowSizeCallback;
    private readonly Func<bool, TopMostStateDto>? _setTopMostCallback;
    private readonly Func<TopMostStateDto>? _getTopMostStateCallback;
    private readonly Func<Task<object?>>? _calibrationRebindCallback;
    private readonly Func<Task<object?>>? _calibrationAutoBindCallback;
    private readonly Func<string?, Task<object?>>? _openSemiAutoCallback;
    private readonly Func<string?, string?, Task<object?>>? _openSemiAutoWithModeCallback;
    private readonly Func<Task<object?>>? _toggleSemiAutoCallback;
    private readonly Func<string?, Task<object?>>? _setSemiAutoThemeCallback;
    private readonly Func<string, Task<object?>>? _exportGuitarChartPdfCallback;
    private readonly Func<bool>? _rebindGlobalHotkeyCallback;
    private readonly AnnouncementCheckService? _announcementCheckService;
    private readonly RecommendedScoreService? _recommendedScoreService;
    private readonly PianoProfileService? _pianoProfiles;
    private readonly PianoSessionService? _pianoSession;
    private readonly GtpConversionService? _gtpConversion;
    private readonly Action<string>? _setActivePageCallback;
    private readonly AudioTranscriptionService? _audioTranscription;
    private readonly WebEventPublisher? _eventPublisher;
    private readonly Func<Task<object?>>? _pickAudioCallback;
    private readonly Func<Task<object?>>? _pickModelFolderCallback;
    private readonly Dictionary<int, (bool Vocal, bool Instrumental, int TargetInstrument)> _transcriptionRequestIds = new();

    public WebMessageHandler(
        LibraryService library,
        PlaybackService playback,
        CalibrationService calibration,
        SettingsService settings,
        AppConfig? config = null,
        Action? saveConfigCallback = null,
        Action<string>? replyCallback = null,
        Action? minimizeCallback = null,
        Action? closeCallback = null,
        Func<bool, WindowSizeStateDto>? setWindowSizeLockCallback = null,
        Func<WindowSizeStateDto>? restoreDefaultWindowSizeCallback = null,
        Func<bool, TopMostStateDto>? setTopMostCallback = null,
        Func<TopMostStateDto>? getTopMostStateCallback = null,
        Func<Task<object?>>? calibrationRebindCallback = null,
        Func<Task<object?>>? calibrationAutoBindCallback = null,
        Func<string?, Task<object?>>? openSemiAutoCallback = null,
        Func<Task<object?>>? toggleSemiAutoCallback = null,
        Func<string?, Task<object?>>? setSemiAutoThemeCallback = null,
        Func<string, Task<object?>>? exportGuitarChartPdfCallback = null,
        Func<string?, string?, Task<object?>>? openSemiAutoWithModeCallback = null,
        Func<bool>? rebindGlobalHotkeyCallback = null,
        AnnouncementCheckService? announcementCheckService = null,
        RecommendedScoreService? recommendedScoreService = null,
        PianoProfileService? pianoProfiles = null,
        PianoSessionService? pianoSession = null,
        Action<string>? setActivePageCallback = null,
        GtpConversionService? gtpConversion = null,
        WallpaperService? wallpaperService = null,
        AudioTranscriptionService? audioTranscription = null,
        WebEventPublisher? eventPublisher = null,
        Func<Task<object?>>? pickAudioCallback = null,
        Func<Task<object?>>? pickModelFolderCallback = null)
    {
        _library = library;
        _playback = playback;
        _calibration = calibration;
        _settings = settings;
        _wallpaper = wallpaperService;
        _config = config;
        _saveConfigCallback = saveConfigCallback;
        _replyCallback = replyCallback;
        _minimizeCallback = minimizeCallback;
        _closeCallback = closeCallback;
        _setWindowSizeLockCallback = setWindowSizeLockCallback;
        _restoreDefaultWindowSizeCallback = restoreDefaultWindowSizeCallback;
        _setTopMostCallback = setTopMostCallback;
        _getTopMostStateCallback = getTopMostStateCallback;
        _calibrationRebindCallback = calibrationRebindCallback;
        _calibrationAutoBindCallback = calibrationAutoBindCallback;
        _openSemiAutoCallback = openSemiAutoCallback;
        _openSemiAutoWithModeCallback = openSemiAutoWithModeCallback;
        _rebindGlobalHotkeyCallback = rebindGlobalHotkeyCallback;
        _announcementCheckService = announcementCheckService;
        _recommendedScoreService = recommendedScoreService;
        _pianoProfiles = pianoProfiles;
        _pianoSession = pianoSession;
        _gtpConversion = gtpConversion;
        _setActivePageCallback = setActivePageCallback;
        _toggleSemiAutoCallback = toggleSemiAutoCallback;
        _setSemiAutoThemeCallback = setSemiAutoThemeCallback;
        _exportGuitarChartPdfCallback = exportGuitarChartPdfCallback;
        _audioTranscription = audioTranscription;
        _eventPublisher = eventPublisher;
        _pickAudioCallback = pickAudioCallback;
        _pickModelFolderCallback = pickModelFolderCallback;
        if (_audioTranscription is not null) _audioTranscription.RequestCompleted += OnAudioWorkerCompleted;
    }

    public async Task HandleMessageAsync(string messageJson, Action<string> sendReply)
    {
        string? requestId = null;
        await _dispatchGate.WaitAsync();

        try
        {
            try
            {
                using var doc = JsonDocument.Parse(messageJson);
                var root = doc.RootElement;

                requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                string action = root.GetProperty("action").GetString() ?? "";
                var payload = root.TryGetProperty("payload", out var p) ? p : default;

                object? result = await DispatchActionAsync(action, payload);
                var response = WebResponse.Ok(requestId, result);

                string replyJson = JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                sendReply(replyJson);
            }
            catch (Exception ex)
            {
                var failResponse = WebResponse.Fail(requestId, ex.Message);
                string replyJson = JsonSerializer.Serialize(failResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                sendReply(replyJson);
            }
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async Task<object?> PrepareAudioTranscriptionAsync()
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        return await _audioTranscription.PrepareAsync();
    }

    private async Task<object?> InstallCudaAudioRuntimeAsync()
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        return await _audioTranscription.InstallCudaRuntimeAsync();
    }

    private async Task<object?> UseCpuAudioRuntimeAsync()
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        return await _audioTranscription.UseCpuRuntimeAsync();
    }

    private async Task<object?> UseCudaAudioRuntimeAsync()
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        return await _audioTranscription.UseCudaRuntimeAsync();
    }

    private async Task<object?> InspectAudioTranscriptionEnvironmentAsync()
    {
        if (_audioTranscription is null)
            return new { error = "音频转谱服务不可用。" };

        return await _audioTranscription.InspectEnvironmentAsync();
    }

    private async Task<object?> StartAudioTranscriptionAsync(JsonElement payload)
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        string audioPath = payload.TryGetProperty("audioPath", out var pathElement) ? pathElement.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(audioPath)) throw new ArgumentException("请选择音频文件。");
        bool includeVocal = !payload.TryGetProperty("includeVocal", out var vocalElement) || vocalElement.ValueKind != JsonValueKind.False;
        bool includeInstrumental = !payload.TryGetProperty("includeInstrumental", out var instrumentalElement) || instrumentalElement.ValueKind != JsonValueKind.False;
        if (!includeVocal && !includeInstrumental) throw new ArgumentException("至少选择人声或器乐主旋律。");
        int targetInstrument = payload.TryGetProperty("targetInstrument", out var targetElement) && targetElement.TryGetInt32(out int requestedTarget)
            ? requestedTarget : LibraryInstrument.GamePiano;
        if (targetInstrument is not (LibraryInstrument.Keyboard or LibraryInstrument.Guitar or LibraryInstrument.GamePiano))
            throw new ArgumentOutOfRangeException(nameof(targetInstrument), "请选择键盘谱、吉他谱或 Piano 曲库分类。");
        var request = await _audioTranscription.StartTranscriptionAsync(audioPath);
        if (_audioTranscription.ActiveTranscriptionRequestId == request.RequestId)
        {
            lock (_transcriptionRequestIds)
            {
                _transcriptionRequestIds[request.RequestId] = (includeVocal, includeInstrumental, targetInstrument);
            }
        }
        return new { requestId = request.RequestId, started = true, audioPath = request.AudioPath };
    }

    private async Task<object?> AllowAudioTranscriptionCpuAsync(JsonElement payload)
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        int requestId = payload.GetProperty("requestId").GetInt32();
        await _audioTranscription.AllowCpuAsync(requestId);
        return new { accepted = true, requestId };
    }

    private async Task<object?> CancelAudioTranscriptionAsync(JsonElement payload)
    {
        if (_audioTranscription is null) throw new InvalidOperationException("音频转谱服务不可用。");
        int requestId = payload.GetProperty("requestId").GetInt32();
        await _audioTranscription.CancelAsync(requestId);
        return new { cancelled = true, requestId };
    }

    private void OnAudioWorkerCompleted(int requestId, JsonElement response)
    {
        (bool Vocal, bool Instrumental, int TargetInstrument) selected = (true, true, LibraryInstrument.GamePiano);
        bool wasTracked;
        lock (_transcriptionRequestIds)
        {
            wasTracked = _transcriptionRequestIds.TryGetValue(requestId, out selected);
            if (wasTracked) _transcriptionRequestIds.Remove(requestId);
        }
        if (!wasTracked && _audioTranscription?.ActiveTranscriptionRequestId != requestId) return;
        if (!response.TryGetProperty("type", out var type) || type.GetString() != "result")
        {
            _audioTranscription?.ForgetTranscriptionRequest(requestId);
            return;
        }
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("jobId", out var jobId) || jobId.ValueKind != JsonValueKind.String)
        {
            _audioTranscription?.ForgetTranscriptionRequest(requestId);
            string? jobDirectory = result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("jobDirectory", out var directoryElement) && directoryElement.ValueKind == JsonValueKind.String
                    ? directoryElement.GetString() : null;
            _ = Task.Run(() => SaveTranscribedMelodyAsync(requestId, response, null, jobDirectory, selected.Vocal, selected.Instrumental, selected.TargetInstrument));
            return;
        }
        string? jobDir = result.TryGetProperty("jobDirectory", out var jobDirectoryElement) && jobDirectoryElement.ValueKind == JsonValueKind.String
            ? jobDirectoryElement.GetString() : null;
        _ = Task.Run(() => SaveTranscribedMelodyAsync(requestId, response, jobId.GetString(), jobDir, selected.Vocal, selected.Instrumental, selected.TargetInstrument));
    }

    private async Task SaveTranscribedMelodyAsync(int requestId, JsonElement response, string? jobId, string? jobDirectory, bool includeVocal, bool includeInstrumental, int targetInstrument)
    {
        bool savedSuccessfully = false;
        try
        {
            JsonElement result = response.GetProperty("result");
            string? vocalPath = result.TryGetProperty("melodyVocalPath", out var vocal) ? vocal.GetString() : null;
            string? instrumentalPath = result.TryGetProperty("melodyInstrumentalPath", out var instrumental) ? instrumental.GetString() : null;
            string? midiPath = null;
            string? alternateMidiPath = null;
            var parsedNotes = new List<RawMidiNote>();
            var selectedTracks = new List<string>();
            string? vocalIssue = null;
            string? instrumentalIssue = null;
            (string Sha256, List<RawMidiNote> Notes, List<int> TrackIds, List<TrackMetadata> Tracks)? vocalMidi = null;
            (string Sha256, List<RawMidiNote> Notes, List<int> TrackIds, List<TrackMetadata> Tracks)? instrumentalMidi = null;

            if (!string.IsNullOrWhiteSpace(vocalPath) && File.Exists(vocalPath))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(vocalPath) || !File.Exists(vocalPath)) throw new FileNotFoundException("Vocal melody MIDI was not produced.");
                    vocalMidi = MidiLoader.Load(vocalPath);
                    if (vocalMidi.Value.Notes.Count == 0) throw new InvalidDataException("Vocal melody MIDI contained no notes.");
                }
                catch (Exception ex) { vocalIssue = ex.Message; }
            }
            if (!string.IsNullOrWhiteSpace(instrumentalPath) && File.Exists(instrumentalPath))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(instrumentalPath) || !File.Exists(instrumentalPath)) throw new FileNotFoundException("Instrumental melody MIDI was not produced.");
                    instrumentalMidi = MidiLoader.Load(instrumentalPath);
                    if (instrumentalMidi.Value.Notes.Count == 0) throw new InvalidDataException("Instrumental melody MIDI contained no notes.");
                }
                catch (Exception ex) { instrumentalIssue = ex.Message; }
            }

            bool useVocal = includeVocal && vocalMidi.HasValue;
            bool useInstrumental = includeInstrumental && instrumentalMidi.HasValue;
            string? fallbackSource = null;
            if (!useVocal && !useInstrumental)
            {
                useVocal = vocalMidi.HasValue;
                useInstrumental = !useVocal && instrumentalMidi.HasValue;
                fallbackSource = useVocal ? "vocal" : useInstrumental ? "instrumental" : null;
            }
            if (fallbackSource is null && includeVocal && !vocalMidi.HasValue && useInstrumental) fallbackSource = "instrumental";
            if (fallbackSource is null && includeInstrumental && !instrumentalMidi.HasValue && useVocal) fallbackSource = "vocal";
            if (!useVocal && !useInstrumental)
                throw new InvalidDataException($"两种主旋律都没有可用 MIDI。人声：{vocalIssue ?? "未生成"}；器乐：{instrumentalIssue ?? "未生成"}");

            if (useVocal && useInstrumental)
            {
                parsedNotes.AddRange(vocalMidi.GetValueOrDefault().Notes.Select(note => { note.TrackId = 1; return note; }));
                parsedNotes.AddRange(instrumentalMidi.GetValueOrDefault().Notes.Select(note => { note.TrackId = 2; return note; }));
                string outputDirectory = jobDirectory is { Length: > 0 } && Directory.Exists(jobDirectory) ? jobDirectory : Path.GetDirectoryName(vocalPath)!;
                midiPath = Path.Combine(outputDirectory, "melody_combined.mid");
                await File.WriteAllBytesAsync(midiPath, MidiWriter.Write(parsedNotes, vocalPath));
                selectedTracks.Add("vocal");
                selectedTracks.Add("instrumental");
            }
            else if (useVocal)
            {
                midiPath = vocalPath;
                parsedNotes.AddRange(vocalMidi.GetValueOrDefault().Notes);
                selectedTracks.Add("vocal");
            }
            else if (useInstrumental)
            {
                midiPath = instrumentalPath;
                parsedNotes.AddRange(instrumentalMidi.GetValueOrDefault().Notes);
                selectedTracks.Add("instrumental");
            }
            else throw new InvalidDataException($"所选旋律没有可用 MIDI。人声：{vocalIssue ?? "未选择"}；器乐：{instrumentalIssue ?? "未选择"}");

            if (useVocal && !useInstrumental && !string.IsNullOrWhiteSpace(instrumentalPath) && File.Exists(instrumentalPath)) alternateMidiPath = instrumentalPath;
            else if (useInstrumental && !useVocal && !string.IsNullOrWhiteSpace(vocalPath) && File.Exists(vocalPath)) alternateMidiPath = vocalPath;
            else if (useVocal && useInstrumental && !string.IsNullOrWhiteSpace(instrumentalPath) && File.Exists(instrumentalPath)) alternateMidiPath = instrumentalPath;

            string? sourceAudioPath = _audioTranscription?.GetAudioPath(requestId);
            if (string.IsNullOrWhiteSpace(sourceAudioPath) || !File.Exists(sourceAudioPath))
                throw new FileNotFoundException("The original audio file is no longer available.", sourceAudioPath);
            string name = Path.GetFileNameWithoutExtension(sourceAudioPath);
            string diagnostics = JsonSerializer.Serialize(new
            {
                requestId,
                sourceAudioPath,
                selectedMelodyMidi = midiPath,
                alternateMelodyMidi = alternateMidiPath,
                selectedModes = new { vocal = includeVocal, instrumental = includeInstrumental },
                selectedTracks,
                fallbackSource,
                mergedTracks = selectedTracks.Count > 1 ? new[] { new { trackId = 1, name = "人声主旋律" }, new { trackId = 2, name = "器乐主旋律" } } : Array.Empty<object>(),
                targetInstrument,
                vocalIssue,
                instrumentalIssue,
                stderrLogTail = _audioTranscription?.GetStderrLogTail(requestId) ?? Array.Empty<string>(),
                worker = result,
                completedAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
            _eventPublisher?.Publish("audioTranscription.progress", new { requestId, step = 5, totalSteps = 5, stage = "library_save", stageName = "生成 MIDI 并导入曲库" });
            if (midiPath is null) throw new InvalidDataException("MIDI 主旋律输出路径为空。");
            object saved = _library.SaveTranscribedSource(midiPath, sourceAudioPath, alternateMidiPath, diagnostics, targetInstrument);
            savedSuccessfully = true;
            using var savedDoc = JsonDocument.Parse(JsonSerializer.Serialize(saved));
            string songId = savedDoc.RootElement.GetProperty("id").GetString()!;
            _eventPublisher?.Publish("audioTranscription.completed", new
            {
                requestId,
                songId,
                name,
                noteCount = parsedNotes.Count,
                targetInstrument,
                fallbackSource,
                command = "saved"
            });
        }
        catch (Exception ex)
        {
            _eventPublisher?.Publish("audioTranscription.failed", new
            {
                requestId,
                error = ex.Message,
                localJobId = jobId,
                localJobDirectory = jobDirectory,
                stderrLogTail = _audioTranscription?.GetStderrLogTail(requestId) ?? Array.Empty<string>()
            });
        }
        finally
        {
            if (savedSuccessfully && !string.IsNullOrWhiteSpace(jobId) && _audioTranscription is not null)
            {
                try { await _audioTranscription.CleanupJobAsync(jobId); }
                catch { /* the service has already surfaced the worker's cleanup error */ }
            }
            _audioTranscription?.ForgetTranscriptionRequest(requestId);
        }
    }

    private async Task<object?> ImportWallpaperAsync()
    {
        if (_wallpaper is null) throw new InvalidOperationException("壁纸功能当前不可用。");
        return await _wallpaper.ImportAsync();
    }

    private Task<object?> DispatchActionAsync(string action, JsonElement payload)
    {
        switch (action)
        {
            case "announcement.check":
                return _announcementCheckService?.CheckAsync() ?? Task.FromResult<object?>(new { status = "unavailable" });

            case "announcement.markSeen":
            {
                string fingerprint = payload.TryGetProperty("fingerprint", out var fingerprintElement)
                    ? fingerprintElement.GetString() ?? ""
                    : "";
                return Task.FromResult<object?>(_announcementCheckService?.MarkSeen(fingerprint) == true);
            }

            case "announcement.openDownload":
            {
                string url = payload.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new ArgumentException("下载链接不是有效的 HTTP/HTTPS 地址。");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult<object?>(new { opened = true });
            }

            case "recommended.list":
            {
                if (_recommendedScoreService is null)
                    return Task.FromResult<object?>(new { status = "unavailable", offline = false, items = Array.Empty<object>() });
                string query = payload.TryGetProperty("query", out var queryElement) ? queryElement.GetString() ?? "" : "";
                int? level = payload.TryGetProperty("level", out var levelElement) && levelElement.ValueKind == JsonValueKind.Number
                    ? levelElement.GetInt32()
                    : null;
                string? instrument = payload.TryGetProperty("instrument", out var instrumentElement)
                    ? instrumentElement.GetString()
                    : null;
                return GetRecommendedCatalogAsync(query, level, instrument);
            }

            case "recommended.use":
            case "recommended.prepareManual":
            {
                if (_recommendedScoreService is null) throw new InvalidOperationException("推荐谱服务暂不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                return UseRecommendedScoreAsync(id, action == "recommended.prepareManual");
            }

            case "recommended.prepareGameChart":
            {
                if (_recommendedScoreService is null) throw new InvalidOperationException("推荐谱服务暂不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                return PrepareRecommendedGameChartAsync(id);
            }

            case "recommended.pickSubmissionMidi":
                return Task.FromResult(_library.PickMidiFile());

            case "recommended.submit":
            {
                if (_recommendedScoreService is null) throw new InvalidOperationException("推荐谱服务暂不可用。");
                string title = payload.GetProperty("title").GetString() ?? "";
                string uploader = payload.GetProperty("uploaderName").GetString() ?? "";
                string instrument = payload.GetProperty("instrument").GetString() ?? "guitar";
                int level = payload.GetProperty("difficultyLevel").GetInt32();
                string? label = payload.TryGetProperty("difficultyLabel", out var labelElement) ? labelElement.GetString() : null;
                string filePath = payload.GetProperty("filePath").GetString() ?? "";
                _settings.UpdateRecommendedSubmissionUploaderName(uploader);
                return SubmitRecommendedScoreAsync(title, uploader, instrument, level, label, filePath);
            }

            case "recommended.uploaderName.update":
            {
                string uploader = payload.GetProperty("uploaderName").GetString() ?? "";
                return Task.FromResult<object?>(_settings.UpdateRecommendedSubmissionUploaderName(uploader));
            }

            case "library.submitRecommended":
            {
                if (_recommendedScoreService is null) throw new InvalidOperationException("推荐谱服务暂不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                return SubmitLocalLibrarySongAsync(id);
            }

            // Library
            case "library.list":
                return Task.FromResult<object?>(_library.List(payload));

            case "library.generateGuitarChart":
            {
                string id = payload.GetProperty("id").GetString() ?? "";
                var options = new Nte.GuitarChart.Core.ChartCompileOptions
                {
                    SourceName = _library.GetSong(id)?.DisplayName ?? "曲目",
                    PageKeyHoldUs = (_config?.PageKeyHoldMs ?? 25) * 1000L,
                    PageInterTapGapUs = (_config?.PageInterTapGapMs ?? 12) * 1000L,
                    WheelMouseSettleUs = (_config?.WheelMouseSettleMs ?? 55) * 1000L,
                    SafetyMarginUs = (_config?.PageInputSafetyMs ?? 8) * 1000L,
                    UseViterbi = true
                };
                return Task.Run<object?>(() =>
                {
                    var stored = _library.GenerateGuitarChart(id, options);
                    return new
                    {
                        songId = id,
                        chartId = stored.Chart.ChartId,
                        noteCount = stored.Chart.SourceNoteCount,
                        playableNoteCount = stored.Chart.PlayableNoteCount,
                        atRiskNoteCount = stored.Chart.AtRiskNoteCount,
                        unplayableNoteCount = stored.Chart.UnplayableNoteCount,
                        canExportPdf = _exportGuitarChartPdfCallback != null
                    };
                });
            }

            // Independent Game Piano mode
            case "ui.setActivePage":
            {
                string pageId = payload.GetProperty("pageId").GetString() ?? "";
                _setActivePageCallback?.Invoke(pageId);
                return Task.FromResult<object?>(new { pageId });
            }

            case "piano.getState":
                return Task.FromResult<object?>(new
                {
                    profile = _pianoProfiles?.GetState(),
                    playback = _pianoSession?.GetState()
                });

            case "piano.setLayout":
            {
                if (_pianoProfiles is null) throw new InvalidOperationException("游戏钢琴配置服务不可用。");
                string layoutName = payload.GetProperty("layoutKind").GetString() ?? "";
                if (!Enum.TryParse<PianoLayoutKind>(layoutName, ignoreCase: false, out var layout) || !Enum.IsDefined(layout))
                    throw new ArgumentException("不支持的游戏钢琴布局。");
                var profile = _pianoProfiles.SetActiveLayout(layout);
                return Task.FromResult<object?>(new { profile });
            }

            case "piano.updateScale":
            {
                if (_pianoProfiles is null) throw new InvalidOperationException("游戏钢琴配置服务不可用。");
                string profileId = payload.GetProperty("profileId").GetString() ?? "";
                string displayName = payload.GetProperty("displayName").GetString() ?? "";
                int tonic = payload.GetProperty("tonicMidiPitchAtMiddleRow").GetInt32();
                var intervals = payload.GetProperty("intervals").EnumerateArray().Select(item => item.GetInt32()).ToList();
                return Task.FromResult<object?>(_pianoProfiles.UpdateScale(profileId, displayName, tonic, intervals));
            }

            case "piano.compilePreview":
            {
                if (_pianoProfiles is null || _pianoSession is null) throw new InvalidOperationException("游戏钢琴服务不可用。");
                string songId = payload.GetProperty("songId").GetString() ?? "";
                string profileId = payload.TryGetProperty("profileId", out var profileIdElement)
                    ? profileIdElement.GetString() ?? _pianoProfiles.GetActiveProfile().Id
                    : _pianoProfiles.GetActiveProfile().Id;
                var source = _library.GetPianoSource(songId);
                var result = _pianoSession.CompilePreview(source.Notes, source.SourceHash, _pianoProfiles.GetProfile(profileId), ReadPianoOptions(payload));
                return Task.FromResult<object?>(new { song = new { source.Id, source.Name, source.SourceHash }, result });
            }

            case "piano.start":
            {
                if (_pianoProfiles is null || _pianoSession is null) throw new InvalidOperationException("游戏钢琴服务不可用。");
                if (_playback.IsPlaying || _playback.IsCountdown || _playback.IsPaused) throw new InvalidOperationException("其他模式正在演奏，请先停止后再启动游戏钢琴。");
                string songId = payload.GetProperty("songId").GetString() ?? "";
                string profileId = payload.GetProperty("profileId").GetString() ?? _pianoProfiles.GetActiveProfile().Id;
                var source = _library.GetPianoSource(songId);
                return Task.FromResult<object?>(_pianoSession.Start(
                    source.Notes,
                    source.SourceHash,
                    _pianoProfiles.GetProfile(profileId),
                    ReadPianoOptions(payload)));
            }

            case "piano.pause":
                return Task.FromResult<object?>(_pianoSession?.Pause());

            case "piano.resume":
                return Task.FromResult<object?>(_pianoSession?.Resume());

            case "piano.stop":
                return Task.FromResult<object?>(_pianoSession?.Stop());

            case "guitarchart.exportPdf":
            {
                string id = payload.GetProperty("id").GetString() ?? "";
                return _exportGuitarChartPdfCallback != null
                    ? _exportGuitarChartPdfCallback(id)
                    : Task.FromResult<object?>(new { success = false, error = "PDF 导出不可用。" });
            }

            case "gtp.pickFile":
                return Task.FromResult<object?>(_gtpConversion?.PickFile());

            case "gtp.parse":
            {
                var service = _gtpConversion ?? throw new InvalidOperationException("GTP 转换服务不可用。");
                string path = payload.GetProperty("filePath").GetString() ?? "";
                return Task.Run<object?>(() => service.Parse(path));
            }

            case "gtp.convert":
            {
                var service = _gtpConversion ?? throw new InvalidOperationException("GTP 转换服务不可用。");
                string path = payload.GetProperty("filePath").GetString() ?? "";
                string name = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                string target = payload.TryGetProperty("target", out var targetElement) ? targetElement.GetString() ?? "guitar" : "guitar";
                string selection = payload.TryGetProperty("selection", out var selectionElement) ? selectionElement.GetString() ?? "full" : "full";
                int transpose = payload.TryGetProperty("transpose", out var transposeElement) ? transposeElement.GetInt32() : 0;
                var trackIds = payload.TryGetProperty("trackIds", out var trackIdsElement) && trackIdsElement.ValueKind == JsonValueKind.Array
                    ? trackIdsElement.EnumerateArray().Select(x => x.GetInt32()).ToList()
                    : new List<int>();
                return Task.Run<object?>(() => service.Convert(path, name, trackIds, target, selection, transpose));
            }

            case "gtp.list":
                return Task.FromResult<object?>(new { items = _gtpConversion?.List() ?? Array.Empty<GtpLibraryItem>() });

            case "gtp.preview":
            {
                var service = _gtpConversion ?? throw new InvalidOperationException("GTP 转换服务不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                return Task.Run<object?>(() => service.Preview(id));
            }

            case "gtp.play":
            {
                EnsurePianoNotPlaying();
                if (_playback.IsPlaying || _playback.IsCountdown || _playback.IsPaused)
                    throw new InvalidOperationException("其他模式正在演奏，请先停止后再载入 GTP 转换结果。");
                var service = _gtpConversion ?? throw new InvalidOperationException("GTP 转换服务不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                var song = service.Load(id);
                if (SongLibrary.ResolveInstrument(song) == LibraryInstrument.GamePiano)
                {
                    if (_pianoSession is null || _pianoProfiles is null)
                        throw new InvalidOperationException("游戏钢琴服务不可用。");
                    var notes = song.RawNotes ?? throw new InvalidOperationException("GTP 钢琴曲目缺少原始音符。");
                    var pianoState = _pianoSession.Start(
                        notes,
                        song.SourceHash,
                        _pianoProfiles.GetActiveProfile(),
                        new PianoCompileOptions { ModeId = "GamePiano" });
                    return Task.FromResult<object?>(new { id = song.Id, name = song.DisplayName, target = "piano", playback = pianoState });
                }
                var playback = _playback.LoadSong(song);
                return Task.FromResult<object?>(new { id = song.Id, name = song.DisplayName, target = "guitar-or-keyboard", playback });
            }

            case "gtp.delete":
            {
                var service = _gtpConversion ?? throw new InvalidOperationException("GTP 转换服务不可用。");
                string id = payload.GetProperty("id").GetString() ?? "";
                return Task.FromResult<object?>(new { deleted = service.Delete(id) });
            }

            case "library.delete":
            {
                string id = payload.GetProperty("id").GetString()!;
                return Task.FromResult<object?>(_library.Delete(id));
            }

            case "library.rename":
            {
                string id = payload.GetProperty("id").GetString()!;
                string name = payload.GetProperty("name").GetString()!;
                return Task.FromResult<object?>(_library.Rename(id, name));
            }

            case "library.pickFile":
            {
                return Task.FromResult<object?>(_library.PickMidiFile());
            }

            case "library.parseMidi":
            {
                string path = payload.GetProperty("filePath").GetString()!;
                return Task.FromResult<object?>(_library.ParseMidiFile(path));
            }

            case "library.editorLoad":
            {
                string id = payload.GetProperty("id").GetString() ?? "";
                return Task.FromResult<object?>(_library.GetEditableSongSnapshot(id));
            }

            case "library.editorOpenMidi":
            {
                string path = payload.GetProperty("filePath").GetString() ?? "";
                return Task.FromResult<object?>(_library.OpenMidiInEditor(path));
            }

            case "library.editorSave":
                return Task.FromResult<object?>(_library.SaveEditedSong(payload));

            case "library.editorExport":
                return Task.FromResult(_library.ExportEditedMidi(payload));

            case "library.getSongForReconvert":
            {
                string id = payload.GetProperty("id").GetString()!;
                return Task.FromResult<object?>(_library.GetSongForReconvert(id));
            }

            case "library.convertAndSave":
            {
                string path = payload.GetProperty("filePath").GetString()!;
                string name = payload.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                int inst = payload.TryGetProperty("instrument", out var i) ? i.GetInt32() : 1;
                int transpose = payload.TryGetProperty("transpose", out var tr) ? tr.GetInt32() : 0;
                string range = payload.TryGetProperty("outOfRange", out var o) ? (o.GetString() ?? "OctaveFold") : "OctaveFold";
                bool drum = payload.TryGetProperty("filterDrum", out var d) && d.GetBoolean();
                bool melody = payload.TryGetProperty("enableMelody", out var m) && m.GetBoolean();
                string strat = payload.TryGetProperty("melodyStrategy", out var s) ? (s.GetString() ?? "AutoLeadFusion") : "AutoLeadFusion";
                string? existingSongId = payload.TryGetProperty("existingSongId", out var esid) ? esid.GetString() : null;

                List<int>? enabledTracks = null;
                if (payload.TryGetProperty("enabledTracks", out var et) && et.ValueKind == JsonValueKind.Array)
                {
                    enabledTracks = et.EnumerateArray().Select(x => x.GetInt32()).ToList();
                }

                return Task.FromResult<object?>(_library.ConvertAndSave(path, name, inst, transpose, range, drum, melody, strat, enabledTracks, existingSongId));
            }

            case "library.saveRawPianoSource":
            {
                string path = payload.GetProperty("filePath").GetString()!;
                string name = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                return Task.FromResult<object?>(_library.SaveRawPianoSource(path, name));
            }

            case "library.seedBuiltIn":
            {
                bool force = payload.TryGetProperty("force", out var f) && f.GetBoolean();
                int count = _library.SeedBuiltInScoresIfNecessary(force: force);
                return Task.FromResult<object?>(new { seededCount = count });
            }

            case "audioTranscription.pickAudio":
                return _pickAudioCallback?.Invoke() ?? Task.FromResult<object?>(new { canceled = true });

            case "audioTranscription.pickModelFolder":
                return _pickModelFolderCallback?.Invoke() ?? Task.FromResult<object?>(new { canceled = true });

            case "audioTranscription.inspect":
                return InspectAudioTranscriptionEnvironmentAsync();

            case "audioTranscription.prepare":
                return PrepareAudioTranscriptionAsync();

            case "audioTranscription.installCuda":
                return InstallCudaAudioRuntimeAsync();

            case "audioTranscription.useCuda":
                return UseCudaAudioRuntimeAsync();

            case "audioTranscription.useCpu":
                return UseCpuAudioRuntimeAsync();

            case "audioTranscription.transcribe":
                return StartAudioTranscriptionAsync(payload);

            case "audioTranscription.allowCpu":
                return AllowAudioTranscriptionCpuAsync(payload);

            case "audioTranscription.cancel":
                return CancelAudioTranscriptionAsync(payload);

            case "gamepad.repair":
                return VirtualGamepadRepairService.RepairAsync();

            case "library.reconvert":
            {
                string id = payload.GetProperty("id").GetString()!;
                int inst = payload.TryGetProperty("instrument", out var i) ? i.GetInt32() : 1;
                int transpose = payload.TryGetProperty("transpose", out var tr) ? tr.GetInt32() : 0;
                string strat = payload.TryGetProperty("melodyStrategy", out var s) ? (s.GetString() ?? "AutoLeadFusion") : "AutoLeadFusion";
                string range = payload.TryGetProperty("outOfRange", out var o) ? (o.GetString() ?? "OctaveFold") : "OctaveFold";

                List<int>? enabledTracks = null;
                if (payload.TryGetProperty("enabledTracks", out var et) && et.ValueKind == JsonValueKind.Array)
                {
                    enabledTracks = et.EnumerateArray().Select(x => x.GetInt32()).ToList();
                }

                return Task.FromResult<object?>(_library.ReconvertSong(id, inst, transpose, strat, range, enabledTracks));
            }

            case "library.previewExtraction":
            {
                string? filePath = payload.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
                string? songId = payload.TryGetProperty("songId", out var sid) ? sid.GetString() : null;
                int inst = payload.TryGetProperty("instrument", out var i) ? i.GetInt32() : 1;
                int transpose = payload.TryGetProperty("transpose", out var tr) ? tr.GetInt32() : 0;
                string range = payload.TryGetProperty("outOfRange", out var o) ? (o.GetString() ?? "OctaveFold") : "OctaveFold";
                string strat = payload.TryGetProperty("melodyStrategy", out var s) ? (s.GetString() ?? "ContinuousMelody") : "ContinuousMelody";

                List<int>? enabledTracks = null;
                if (payload.TryGetProperty("enabledTracks", out var et) && et.ValueKind == JsonValueKind.Array)
                {
                    enabledTracks = et.EnumerateArray().Select(x => x.GetInt32()).ToList();
                }

                return Task.FromResult<object?>(_library.PreviewExtraction(filePath, songId, inst, transpose, range, strat, enabledTracks));
            }

            case "playback.auditionCustom":
            {
                EnsurePianoNotPlaying();
                string? filePath = payload.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
                string? songId = payload.TryGetProperty("songId", out var sid) ? sid.GetString() : null;
                int transpose = payload.TryGetProperty("transpose", out var tr) ? tr.GetInt32() : 0;
                string strat = payload.TryGetProperty("melodyStrategy", out var s) ? (s.GetString() ?? "ContinuousMelody") : "ContinuousMelody";

                List<int>? enabledTracks = null;
                if (payload.TryGetProperty("enabledTracks", out var et) && et.ValueKind == JsonValueKind.Array)
                {
                    enabledTracks = et.EnumerateArray().Select(x => x.GetInt32()).ToList();
                }

                var notes = _library.GetNotesForAudition(filePath, songId, transpose, strat, enabledTracks);
                _playback.StartAuditionNotes(notes);
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            case "playback.stopAudition":
            {
                _playback.Stop();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            // Playback
            case "playback.load":
            {
                string id = payload.GetProperty("id").GetString()!;
                var song = _library.GetSong(id);
                if (song == null) throw new InvalidOperationException("未找到曲目");
                return Task.FromResult<object?>(_playback.LoadSong(song));
            }

            case "playback.play":
            {
                EnsurePianoNotPlaying();
                if (_playback.IsPlaying || _playback.IsCountdown)
                {
                    _playback.Pause();
                }
                else if (_playback.IsPaused)
                {
                    _playback.Resume();
                }
                else
                {
                    long offset = payload.TryGetProperty("offsetMs", out var off) ? off.GetInt64() : 0;
                    _playback.StartPlay(offset);
                }
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            case "playback.togglePlay":
            {
                EnsurePianoNotPlaying();
                _playback.TogglePlay();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            case "playback.pause":
                _playback.Pause();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());

            case "playback.resume":
                _playback.Resume();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());

            case "playback.stop":
                _pianoSession?.Stop();
                _playback.Stop();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());

            case "playback.seek":
            {
                long pos = payload.GetProperty("positionMs").GetInt64();
                _playback.Seek(pos);
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            case "playback.audition":
            {
                EnsurePianoNotPlaying();
                long offset = payload.TryGetProperty("offsetMs", out var off) ? off.GetInt64() : 0;
                _playback.StartAudition(offset);
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());
            }

            case "playback.setSpeed":
            {
                double multiplier = payload.GetProperty("multiplier").GetDouble();
                return Task.FromResult<object?>(_playback.SetPlaybackSpeed(multiplier));
            }

            case "playback.autoSpeed":
                return Task.FromResult<object?>(_playback.ResetAutoPlaybackSpeed());

            case "playback.getState":
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());

            case "playback.recoverState":
                _playback.RecoverCurrentState();
                return Task.FromResult<object?>(_playback.GetPlaybackStateDto());

            // Calibration
            case "calibration.getStatus":
                return Task.FromResult<object?>(_calibration.GetStatus());

            case "calibration.rebindGameWindow":
                return _calibrationRebindCallback?.Invoke()
                    ?? Task.FromResult<object?>(new { success = false, status = _calibration.GetStatus() });

            case "calibration.autoBindAndDetect":
                return _calibrationAutoBindCallback?.Invoke()
                    ?? Task.FromResult<object?>(new { success = false, reason = "自动校准服务未连接。" });

            case "calibration.confirmAutoCalibration":
            {
                double[]? slotXs = null;
                double[]? slotYs = null;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("slotXs", out var xsElement))
                    slotXs = xsElement.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("slotYs", out var ysElement))
                    slotYs = ysElement.EnumerateArray().Select(y => y.GetDouble()).ToArray();
                return Task.FromResult<object?>(_calibration.ConfirmAutoCalibration(slotXs, slotYs));
            }

            case "calibration.cancelAutoCalibration":
                _calibration.CancelAutoCalibration();
                return Task.FromResult<object?>(new { success = true });

            case "calibration.testSlot":
            {
                int slot = payload.GetProperty("slot").GetInt32();
                return Task.FromResult<object?>(_calibration.TestSlot(slot));
            }

            case "calibration.savePreset":
            {
                var xs = payload.GetProperty("slotXs").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                var ys = payload.GetProperty("slotYs").EnumerateArray().Select(y => y.GetSingle()).ToArray();
                float strumX = payload.GetProperty("strumX").GetSingle();
                float strumY = payload.GetProperty("strumY").GetSingle();
                return Task.FromResult<object?>(_calibration.SavePreset(xs, ys, strumX, strumY));
            }

            case "calibration.resetDefaults":
                return Task.FromResult<object?>(_calibration.ResetDefaults());

            // Settings
            case "settings.get":
                return Task.FromResult<object?>(_settings.GetSettings());

            case "wallpaper.import":
            {
                if (_wallpaper is null) throw new InvalidOperationException("壁纸功能当前不可用。");
                return ImportWallpaperAsync();
            }

            case "wallpaper.apply":
            {
                if (_wallpaper is null) throw new InvalidOperationException("壁纸功能当前不可用。");
                string fileName = payload.GetProperty("fileName").GetString() ?? "";
                string mediaType = payload.GetProperty("mediaType").GetString() ?? "";
                double scale = payload.GetProperty("scale").GetDouble();
                double offsetX = payload.GetProperty("offsetX").GetDouble();
                double offsetY = payload.GetProperty("offsetY").GetDouble();
                return Task.FromResult<object?>(_settings.ApplyWallpaper(fileName, mediaType, scale, offsetX, offsetY));
            }

            case "wallpaper.clear":
                return Task.FromResult<object?>(_settings.ClearWallpaper());

            case "settings.update":
            {
                string key = payload.GetProperty("key").GetString()!;
                var val = payload.GetProperty("value");
                object parsed = val.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => val.TryGetInt32(out int i) ? i : val.GetDouble(),
                    _ => val.GetString() ?? ""
                };
                int previousHotkey = _config?.GlobalHotkeyVk ?? 0;
                var updated = _settings.UpdateSetting(key, parsed);
                if (key == "globalHotkeyVk" && _rebindGlobalHotkeyCallback is not null && !_rebindGlobalHotkeyCallback())
                {
                    if (_config is not null && previousHotkey != 0)
                    {
                        _config.GlobalHotkeyVk = previousHotkey;
                        _saveConfigCallback?.Invoke();
                        _rebindGlobalHotkeyCallback();
                    }
                    throw new InvalidOperationException("该热键无法注册，可能已被其他程序占用；已恢复原设置。");
                }
                return RecompileAfterSettingsUpdateAsync(updated);
            }

            case "settings.resetTimingDefaults":
            {
                var resetSettings = _settings.ResetTimingDefaults();
                return RecompileAfterSettingsUpdateAsync(resetSettings);
            }

            case "settings.getAlgorithms":
                return Task.FromResult<object?>(_settings.GetAlgorithmSections());

            case "settings.getTutorial":
                return Task.FromResult<object?>(_settings.GetTutorialSteps());

            case "settings.exportDiagnostics":
                return Task.FromResult<object?>(_settings.ExportDiagnostics(_playback.CurrentSong));

            // Tools & Dialogs
            case "tools.openPresetEditor":
            {
                var thread = new Thread(() =>
                {
                    string presetName = _config?.CurrentPresetName ?? "默认36键";
                    using var form = new PresetEditorForm(presetName);
                    form.OnPresetSaved += newPresetName =>
                    {
                        if (_config != null)
                        {
                            _config.CurrentPresetName = newPresetName;
                            _saveConfigCallback?.Invoke();
                            _playback.RecompileSong();
                        }
                    };
                    form.ShowDialog();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return Task.FromResult<object?>(true);
            }

            case "tools.openCompatibilityTest":
            {
                var thread = new Thread(() =>
                {
                    using var dlg = new CompatibilityTestDialog();
                    dlg.ShowDialog();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return Task.FromResult<object?>(true);
            }

            case "tools.openWheelCalibration":
            {
                var thread = new Thread(() =>
                {
                    var preset = _config?.WheelPreset ?? new WheelPreset();
                    using var dlg = new WheelCalibrationDialog(preset);
                    if (dlg.ShowDialog() == DialogResult.OK && _config != null)
                    {
                        _config.WheelPreset = dlg.ResultPreset;
                        _saveConfigCallback?.Invoke();
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return Task.FromResult<object?>(true);
            }

            // Window
            case "window.minimize":
                _minimizeCallback?.Invoke();
                return Task.FromResult<object?>(true);

            case "window.close":
                _closeCallback?.Invoke();
                return Task.FromResult<object?>(true);

            case "window.setSizeLock":
            {
                bool locked = payload.GetProperty("locked").GetBoolean();
                return Task.FromResult<object?>(_setWindowSizeLockCallback?.Invoke(locked));
            }

            case "window.resetToRecommendedSize":
            case "window.restoreDefaultSize":
                return Task.FromResult<object?>(_restoreDefaultWindowSizeCallback?.Invoke());

            case "window.setTopMost":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                return Task.FromResult<object?>(_setTopMostCallback?.Invoke(enabled));
            }

            case "window.getTopMostState":
                return Task.FromResult<object?>(_getTopMostStateCallback?.Invoke());

            // SemiAuto Guitar Mode (V3.0)
            case "semiauto.open":
            {
                string? songId = payload.TryGetProperty("songId", out var sid) ? sid.GetString() : null;
                if (_openSemiAutoCallback != null)
                {
                    return _openSemiAutoCallback(songId);
                }
                return Task.FromResult<object?>(new { success = false, message = "未配置半自动悬浮窗回调" });
            }

            case "semiauto.openWithMode":
            {
                string? songId = payload.TryGetProperty("songId", out var sid) ? sid.GetString() : null;
                string? mode = payload.TryGetProperty("mode", out var modeValue) ? modeValue.GetString() : null;
                if (mode is not ("full-manual" or "semi-auto"))
                    throw new ArgumentException("无效的位置执行方式。");
                if (_openSemiAutoWithModeCallback != null)
                    return _openSemiAutoWithModeCallback(songId, mode);
                return Task.FromResult<object?>(new { success = false, message = "未配置指定模式启动回调" });
            }

            case "semiauto.toggle":
            {
                if (_toggleSemiAutoCallback != null)
                {
                    return _toggleSemiAutoCallback();
                }
                return Task.FromResult<object?>(new { success = false, message = "未配置半自动悬浮窗回调" });
            }

            case "semiauto.setTheme":
            {
                string? theme = payload.TryGetProperty("theme", out var themeValue) ? themeValue.GetString() : null;
                if (_setSemiAutoThemeCallback != null)
                {
                    return _setSemiAutoThemeCallback(theme);
                }
                return Task.FromResult<object?>(new { success = false, message = "未配置悬浮窗主题回调" });
            }

            case "semiauto.compileChart":
            {
                string? songId = payload.TryGetProperty("songId", out var sid) ? sid.GetString() : null;
                var song = !string.IsNullOrEmpty(songId) ? _library.GetSong(songId) : _playback.CurrentSong;
                if (song == null) throw new InvalidOperationException("未找到曲目");

                var rawNotes = song.RawNotes ?? MidiAutoPlayer.Midi.MidiRawCache.TryGet(song.SourceHash);
                if (rawNotes == null || rawNotes.Count == 0) throw new InvalidOperationException("未找到源音符数据");

                var compiler = new Nte.GuitarChart.Core.GuitarChartCompiler(new Nte.GuitarChart.Core.WheelPositionTimingModel());
                var adapterNotes = Nte.GuitarChart.Core.RawMidiNoteAdapter.FromExistingList(rawNotes);
                var chart = compiler.Compile(adapterNotes, new Nte.GuitarChart.Core.ChartCompileOptions
                {
                    SourceName = song.DisplayName,
                    SimultaneousToleranceUs = 20_000,
                    UseViterbi = true
                });

                return Task.FromResult<object?>(chart);
            }

            default:
                throw new NotSupportedException($"不支持的命令: {action}");
        }
    }

    private async Task<object?> UseRecommendedScoreAsync(string id, bool manual)
    {
        var download = await _recommendedScoreService!.DownloadAsync(id);
        if (manual)
        {
            return new
            {
                filePath = download.FilePath,
                fileName = download.Score.Title + ".mid",
                title = download.Score.Title,
                instrument = download.Score.Instrument,
                wasCached = download.WasCached
            };
        }

        object converted = download.Score.Instrument switch
        {
            "piano" => await Task.Run(() => _library.SaveRawPianoSource(download.FilePath, download.Score.Title)),
            "keyboard" => await Task.Run(() => _library.ConvertAndSave(
                download.FilePath, download.Score.Title, LibraryInstrument.Keyboard, 0, "OctaveFold", true, false, "FullSong")),
            "guitar" => await Task.Run(() => _library.ConvertAndSave(
                download.FilePath, download.Score.Title, LibraryInstrument.Guitar, 0, "OctaveFold", true, false, "FullSong")),
            _ => throw new InvalidOperationException("推荐谱乐器类型不支持，未写入本地曲库。")
        };
        using var convertedJson = JsonDocument.Parse(JsonSerializer.Serialize(converted));
        string songId = convertedJson.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("推荐谱已转换，但未能取得曲库编号。");
        var song = _library.GetSong(songId) ?? throw new InvalidOperationException("推荐谱已转换，但无法从本地曲库读取。");
        var playback = download.Score.Instrument == "piano" ? null : _playback.LoadSong(song);
        return new
        {
            id = songId,
            name = song.DisplayName,
            instrument = download.Score.Instrument,
            wasCached = download.WasCached,
            playback
        };
    }

    private async Task<object?> RecompileAfterSettingsUpdateAsync(object? result)
    {
        await _playback.RecompileSongAsync();
        return result;
    }

    private async Task<object?> GetRecommendedCatalogAsync(string query, int? level, string? instrument) =>
        await _recommendedScoreService!.GetCatalogAsync(query, level, instrument: instrument);

    private async Task<object?> SubmitRecommendedScoreAsync(string title, string uploader, string instrument, int level, string? label, string filePath)
    {
        var submission = await _recommendedScoreService!.SubmitAsync(title, uploader, instrument, level, label, filePath);
        return new { submitted = true, id = submission.Id, status = submission.Status };
    }

    private async Task<object?> SubmitLocalLibrarySongAsync(string songId)
    {
        if (_recommendedScoreService is null) throw new InvalidOperationException("推荐谱服务暂不可用。");
        string uploaderName = (_config?.RecommendedSubmissionUploaderName ?? "").Trim();
        if (uploaderName.Length is < 1 or > 80)
            throw new InvalidOperationException("请先填写有效的投稿者名称（1–80 个字符）。");

        var song = _library.GetSong(songId) ?? throw new FileNotFoundException("未找到所选本地曲目。", songId);
        string instrument = SongLibrary.ResolveInstrument(song) switch
        {
            LibraryInstrument.Keyboard => "keyboard",
            LibraryInstrument.GamePiano => "piano",
            LibraryInstrument.Guitar => "guitar",
            _ => throw new InvalidDataException("该本地曲目的乐器类型不支持投稿。")
        };
        string midiPath = _library.GetOriginalMidiPath(songId);
        var submission = await _recommendedScoreService.SubmitAsync(
            song.DisplayName, uploaderName, instrument, level: 1, label: null, filePath: midiPath);
        return new { submitted = true, id = submission.Id, status = submission.Status, title = song.DisplayName };
    }

    private async Task<object?> PrepareRecommendedGameChartAsync(string id)
    {
        var download = await _recommendedScoreService!.DownloadAsync(id);
        if (!string.Equals(download.Score.Instrument, "guitar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("游戏专用吉他谱仅支持吉他类推荐谱。");
        object converted = await Task.Run(() => _library.ConvertAndSave(
            download.FilePath, download.Score.Title, targetInstrument: 1, transpose: 0,
            outOfRangeMode: "OctaveFold", filterDrumTrack: true,
            enableMelodyExtraction: false, melodyStrategy: "FullSong"));
        using var convertedJson = JsonDocument.Parse(JsonSerializer.Serialize(converted));
        string songId = convertedJson.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("推荐谱已转换，但未能取得曲库编号。");
        return new { id = songId, title = download.Score.Title, wasCached = download.WasCached };
    }

    private void EnsurePianoNotPlaying()
    {
        if (_pianoSession?.IsActive == true)
            throw new InvalidOperationException("游戏钢琴正在演奏，请先暂停或停止后再启动其他模式。");
    }

    private static PianoCompileOptions ReadPianoOptions(JsonElement payload)
    {
        JsonElement options = payload.TryGetProperty("options", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : payload;
        int transpose = options.TryGetProperty("transposeSemitones", out var transposeElement)
            ? transposeElement.GetInt32()
            : 0;
        int speed = options.TryGetProperty("speedPercent", out var speedElement)
            ? speedElement.GetInt32()
            : 100;
        bool filterDrums = !options.TryGetProperty("filterDrumChannel10", out var filterElement) || filterElement.GetBoolean();
        UnplayableNotePolicy policy = UnplayableNotePolicy.AutoTransposeMaxCoverage;
        if (options.TryGetProperty("unplayableNotePolicy", out var policyElement))
        {
            string policyName = policyElement.GetString() ?? "";
            if (!Enum.TryParse(policyName, ignoreCase: false, out policy) || !Enum.IsDefined(policy))
                throw new ArgumentException("不可演奏音符策略无效。");
        }
        transpose = Math.Clamp(transpose, -127, 127);
        speed = Math.Clamp(speed, 25, 200);
        return new PianoCompileOptions
        {
            ModeId = "GamePiano",
            TransposeSemitones = transpose,
            SpeedPercent = speed,
            FilterDrumChannel10 = filterDrums,
            UnplayableNotePolicy = policy
        };
    }
}
