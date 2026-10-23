using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

/// <summary>Owns the long-lived local Python transcription worker and its JSONL protocol.</summary>
public sealed class AudioTranscriptionService : IAsyncDisposable
{
    private static readonly HttpClient RuntimeHttpClient = new() { Timeout = TimeSpan.FromMinutes(20) };
    private const string Python312InstallerUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".aac" };

    private readonly object _sync = new();
    private readonly Action<string, object?> _publish;
    private readonly string _dataRoot;
    private readonly string _workerPath;
    private readonly string _bootstrapPath;
    private readonly string _modelPathFile;
    private readonly string _torchSelectionPath;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private const string CpuTorchIndex = "https://download.pytorch.org/whl/cpu";
    private string _preferredTorchIndex = CpuTorchIndex;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Dictionary<int, string> _audioPathsByRequest = new();
    private readonly Dictionary<int, BoundedLogTail> _stderrLogTails = new();
    private readonly Queue<int> _stderrLogTailOrder = new();
    private Process? _process;
    private Process? _bootstrapProcess;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _nextRequestId;
    private int? _activeTranscriptionId;
    private int? _activePrepareOperationId;
    private int? _activePrepareWorkerRequestId;
    private bool _prepareWorkerCommandSent;
    private bool _prepareCancelSent;
    private CancellationTokenSource? _prepareCancellation;
    private Task? _prepareTask;
    private bool _disposed;

    public AudioTranscriptionService(Action<string, object?> publish)
    {
        _publish = publish;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataRoot = Path.GetFullPath(Path.Combine(localAppData, "NTE-GUITAR"));
        _modelPathFile = Path.Combine(_dataRoot, "audio-transcription", "model-root.txt");
        _torchSelectionPath = Path.Combine(_dataRoot, "audio-transcription", "torch-index-url.txt");
        _preferredTorchIndex = ReadSelectedTorchIndex();
        _workerPath = ResolveWorkerPath();
        _bootstrapPath = ResolveBootstrapPath();
    }

    public event Action<int, JsonElement>? RequestCompleted;
    public int? ActiveTranscriptionRequestId { get { lock (_sync) return _activeTranscriptionId; } }
    public string? GetAudioPath(int requestId)
    {
        lock (_sync) return _audioPathsByRequest.TryGetValue(requestId, out string? path) ? path : null;
    }
    public IReadOnlyList<string> GetStderrLogTail(int requestId)
    {
        lock (_sync)
            return _stderrLogTails.TryGetValue(requestId, out var tail) ? tail.Snapshot() : Array.Empty<string>();
    }
    public void ForgetTranscriptionRequest(int requestId)
    {
        lock (_sync)
            ForgetTranscriptionRequestLocked(requestId);
    }

    public Task<object> PrepareAsync()
    {
        ThrowIfDisposed();
        if (!_operationGate.Wait(0))
            return Task.FromException<object>(new InvalidOperationException("An audio preparation or transcription request is already active."));
        int operationId = NextId();
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _activePrepareOperationId = operationId;
            _activePrepareWorkerRequestId = null;
            _prepareWorkerCommandSent = false;
            _prepareCancelSent = false;
            _prepareCancellation = cancellation;
            _prepareTask = Task.Run(() => RunPrepareOperationAsync(operationId, cancellation));
        }
        return Task.FromResult<object>(new { requestId = operationId, operationId, started = true });
    }

    private async Task RunPrepareOperationAsync(int operationId, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            _preferredTorchIndex = ReadSelectedTorchIndex();
            await ExtractBundledRuntimeAsync(operationId, token);
            if (!await IsRuntimeReadyAsync(token))
            {
                await InstallRuntimeAsync(operationId, token, _preferredTorchIndex, IsCudaIndex(_preferredTorchIndex) ? "cuda" : "cpu");
                if (!await IsRuntimeReadyAsync(token))
                    throw new InvalidOperationException("Python runtime setup finished without a valid runtime. Review AudioTranscription/README.md and retry.");
            }
            token.ThrowIfCancellationRequested();
            await EnsureWorkerAsync();
            token.ThrowIfCancellationRequested();

            int workerRequestId = NextId();
            lock (_sync)
            {
                _activePrepareWorkerRequestId = workerRequestId;
                _prepareWorkerCommandSent = false;
            }
            await SendCommandAsync(new { requestId = workerRequestId, command = "prepare" });
            bool cancelOnStart;
            lock (_sync)
            {
                _prepareWorkerCommandSent = true;
                cancelOnStart = token.IsCancellationRequested && !_prepareCancelSent;
                if (cancelOnStart) _prepareCancelSent = true;
            }
            if (cancelOnStart) await SendAndObserveAsync("cancel", new { targetRequestId = workerRequestId });

            JsonElement result = await ObserveCommandAsync(workerRequestId, "prepare", throwOnError: true, publishWorkerFailure: false);
            _publish("audioTranscription.completed", new
            {
                requestId = operationId,
                operationId,
                workerRequestId,
                command = "prepare",
                result = result.GetProperty("result")
            });
        }
        catch (OperationCanceledException)
        {
            _publish("audioTranscription.failed", new { requestId = operationId, operationId, canceled = true, error = "Audio preparation was cancelled." });
        }
        catch (Exception ex)
        {
            _publish("audioTranscription.failed", new
            {
                requestId = operationId,
                operationId,
                canceled = token.IsCancellationRequested,
                error = token.IsCancellationRequested ? "Audio preparation was cancelled." : ex.Message
            });
        }
        finally
        {
            lock (_sync)
            {
                if (_activePrepareOperationId == operationId)
                {
                    _activePrepareOperationId = null;
                    _activePrepareWorkerRequestId = null;
                    _prepareWorkerCommandSent = false;
                    _prepareCancelSent = false;
                    _prepareCancellation = null;
                    _bootstrapProcess = null;
                }
            }
            cancellation.Dispose();
            _operationGate.Release();
        }
    }

    public Task<object> InstallCudaRuntimeAsync()
    {
        ThrowIfDisposed();
        if (!_operationGate.Wait(0))
            return Task.FromException<object>(new InvalidOperationException("An audio preparation or transcription request is already active."));
        int operationId = NextId();
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _activePrepareOperationId = operationId;
            _activePrepareWorkerRequestId = null;
            _prepareWorkerCommandSent = false;
            _prepareCancelSent = false;
            _prepareCancellation = cancellation;
            _prepareTask = Task.Run(() => RunCudaRuntimeInstallAsync(operationId, cancellation));
        }
        return Task.FromResult<object>(new { requestId = operationId, operationId, started = true });
    }

    private async Task RunCudaRuntimeInstallAsync(int operationId, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        string previousSelection = ReadSelectedTorchIndex();
        try
        {
            string? cudaIndex = await DetectCudaTorchWheelIndexUrlAsync();
            if (cudaIndex is null)
                throw new InvalidOperationException("没有检测到支持的 NVIDIA CUDA 环境。请更新 NVIDIA 驱动后重新检查；CPU 转写仍可使用。");

            _preferredTorchIndex = cudaIndex;
            _publish("audioTranscription.progress", new
            {
                requestId = operationId, operationId, stage = "cuda_runtime_setup",
                stageName = "下载并安装 CUDA 推理环境",
                message = $"准备下载 PyTorch CUDA 运行环境（{cudaIndex[(cudaIndex.LastIndexOf('/') + 1)..]}）。文件较大，速度取决于网络。"
            });
            await ExtractBundledRuntimeAsync(operationId, token);
            await InstallRuntimeAsync(operationId, token, cudaIndex, "cuda");
            token.ThrowIfCancellationRequested();
            if (!await IsRuntimeReadyAsync(token))
                throw new InvalidOperationException("CUDA 运行环境安装完成，但完整性检查未通过。查看准备日志后重试。");

            await StopWorkerForRuntimeSwitchAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(_torchSelectionPath)!);
            string temporaryPath = _torchSelectionPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, cudaIndex, token);
            File.Move(temporaryPath, _torchSelectionPath, overwrite: true);
            _publish("audioTranscription.completed", new
            {
                requestId = operationId, operationId, command = "cuda_install",
                result = new { ready = true, torchIndexUrl = cudaIndex, runtimePath = ResolvePythonPath() }
            });
        }
        catch (OperationCanceledException)
        {
            _preferredTorchIndex = previousSelection;
            _publish("audioTranscription.failed", new { requestId = operationId, operationId, canceled = true, error = "GPU 环境下载已取消。" });
        }
        catch (Exception ex)
        {
            _preferredTorchIndex = previousSelection;
            _publish("audioTranscription.failed", new { requestId = operationId, operationId, canceled = token.IsCancellationRequested, error = ex.Message });
        }
        finally
        {
            lock (_sync)
            {
                if (_activePrepareOperationId == operationId)
                {
                    _activePrepareOperationId = null;
                    _activePrepareWorkerRequestId = null;
                    _prepareWorkerCommandSent = false;
                    _prepareCancelSent = false;
                    _prepareCancellation = null;
                    _bootstrapProcess = null;
                }
            }
            cancellation.Dispose();
            _operationGate.Release();
        }
    }

    public async Task<object> UseCpuRuntimeAsync()
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0))
            throw new InvalidOperationException("请等待当前音频准备或转写完成，再切换运行模式。");
        try
        {
            await StopWorkerForRuntimeSwitchAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(_torchSelectionPath)!);
            File.Delete(_torchSelectionPath);
            _preferredTorchIndex = CpuTorchIndex;
            return new { selected = "cpu", torchIndexUrl = CpuTorchIndex };
        }
        finally { _operationGate.Release(); }
    }

    public async Task<object> UseCudaRuntimeAsync()
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0))
            throw new InvalidOperationException("请等待当前音频准备或转写完成，再切换运行模式。");
        string previousSelection = _preferredTorchIndex;
        try
        {
            string? cudaIndex = await DetectCudaTorchWheelIndexUrlAsync();
            if (cudaIndex is null) throw new InvalidOperationException("当前没有检测到兼容的 NVIDIA CUDA 驱动。");
            _preferredTorchIndex = cudaIndex;
            if (!await IsRuntimeReadyAsync(CancellationToken.None))
                throw new InvalidOperationException("GPU 环境尚未就绪，请先下载并安装 GPU 环境。");
            await StopWorkerForRuntimeSwitchAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(_torchSelectionPath)!);
            await File.WriteAllTextAsync(_torchSelectionPath + ".tmp", cudaIndex);
            File.Move(_torchSelectionPath + ".tmp", _torchSelectionPath, overwrite: true);
            return new { selected = "cuda", torchIndexUrl = cudaIndex };
        }
        catch
        {
            _preferredTorchIndex = previousSelection;
            throw;
        }
        finally { _operationGate.Release(); }
    }

    private async Task StopWorkerForRuntimeSwitchAsync()
    {
        Process? process;
        Task? stdoutTask;
        Task? stderrTask;
        lock (_sync)
        {
            process = _process is { HasExited: false } running ? running : null;
            stdoutTask = _stdoutTask;
            stderrTask = _stderrTask;
        }
        if (process is null) return;
        int requestId = NextId();
        TaskCompletionSource<JsonElement> terminal = RegisterPending(requestId);
        try
        {
            await SendCommandAsync(new { requestId, command = "shutdown" });
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(requestId);
                if (ReferenceEquals(_process, process)) _process = null;
                _stdoutTask = null;
                _stderrTask = null;
            }
            if (stdoutTask is not null) try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if (stderrTask is not null) try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            process.Dispose();
        }
    }

    private async Task InstallRuntimeAsync(int operationId, CancellationToken cancellationToken,
        string torchIndexUrl, string runtimeName)
    {
        if (!File.Exists(_bootstrapPath)) throw new FileNotFoundException("Audio runtime bootstrap files are missing.", _bootstrapPath);
        await EnsureAppLocalPython312Async(operationId, cancellationToken);
        var python = await FindPython312Async(cancellationToken);
        if (python is null)
            throw new InvalidOperationException("Python 3.12 was not found in the embedded runtime or system PATH. Install 64-bit Python 3.12, then prepare again.");

        var start = new ProcessStartInfo(python.Value.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_bootstrapPath)!
        };
        foreach (string argument in python.Value.PrefixArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(_bootstrapPath);
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(_dataRoot);
        start.ArgumentList.Add("--install-runtime");
        start.ArgumentList.Add("--runtime-name");
        start.ArgumentList.Add(runtimeName);
        start.ArgumentList.Add("--torch-index-url");
        start.ArgumentList.Add(torchIndexUrl);
        start.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _bootstrapProcess = process;
        }
        Task? stdout = null;
        Task? stderr = null;
        var setupLog = new BoundedLogTail();
        string setupLogPath = Path.Combine(_dataRoot, "audio-transcription", "runtime-setup.log");
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start Python runtime setup.");
            _publish("audioTranscription.progress", new
            {
                requestId = operationId, operationId,
                stage = runtimeName == "cuda" ? "cuda_runtime_setup" : "runtime_setup",
                stageName = runtimeName == "cuda" ? "下载 CUDA 推理依赖" : "准备 CPU 推理环境",
                message = runtimeName == "cuda"
                    ? $"正在通过 {torchIndexUrl} 下载 CUDA PyTorch 与依赖。"
                    : "正在验证内置 CPU 推理环境。"
            });
            // Keep draining both pipes through cancellation/termination so the child cannot block on a full pipe.
            stdout = PumpBootstrapOutputAsync(process.StandardOutput, operationId, "stdout", setupLog, runtimeName);
            stderr = PumpBootstrapOutputAsync(process.StandardError, operationId, "stderr", setupLog, runtimeName);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout!, stderr!);
            await PersistBootstrapLogAsync(setupLogPath, setupLog);
            if (process.ExitCode != 0)
            {
                string tail;
                lock (setupLog) tail = string.Join(Environment.NewLine, setupLog.Snapshot().TakeLast(35));
                throw new InvalidOperationException($"Python runtime setup failed with exit code {process.ExitCode}.\nSetup log: {setupLogPath}\n\n{tail}");
            }
            _publish("audioTranscription.progress", new
            {
                requestId = operationId, operationId,
                stage = runtimeName == "cuda" ? "cuda_runtime_ready" : "runtime_ready",
                stageName = runtimeName == "cuda" ? "CUDA 推理环境已就绪" : "CPU 推理环境已就绪",
                message = "Python 运行环境准备完成。"
            });
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if (stdout is not null) try { await stdout.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if (stderr is not null) try { await stderr.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { await PersistBootstrapLogAsync(setupLogPath, setupLog); } catch { }
            lock (_sync) if (ReferenceEquals(_bootstrapProcess, process)) _bootstrapProcess = null;
            process.Dispose();
        }
    }

    private async Task EnsureAppLocalPython312Async(int operationId, CancellationToken cancellationToken)
    {
        string python = ResolveBundledBasePythonPath();
        if (File.Exists(python)) return;

        string installRoot = Path.GetDirectoryName(python)!;
        Directory.CreateDirectory(installRoot);
        string installer = Path.Combine(Path.GetDirectoryName(installRoot)!, "python-3.12.10-amd64.exe");
        if (!File.Exists(installer))
        {
            _publish("audioTranscription.progress", new
            {
                requestId = operationId, operationId, step = 1, totalSteps = 5,
                stage = "python_download", stageName = "下载 Python 3.12",
                message = "正在从 python.org 下载官方 Python 3.12 安装程序。"
            });
            string partial = installer + ".download";
            try
            {
                using var response = await RuntimeHttpClient.GetAsync(Python312InstallerUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                long? length = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true);
                var buffer = new byte[256 * 1024];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    _publish("audioTranscription.progress", new
                    {
                        requestId = operationId, operationId, step = 1, totalSteps = 5,
                        stage = "python_download", stageName = "下载 Python 3.12",
                        progress = length is > 0 ? (double?)received / length.Value : null,
                        bytesDownloaded = received, totalBytes = length,
                        message = length is > 0
                            ? $"已下载 {received / 1024d / 1024d:F0} / {length.Value / 1024d / 1024d:F0} MB"
                            : $"已下载 {received / 1024d / 1024d:F0} MB"
                    });
                }
                output.Close();
                File.Move(partial, installer, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }

        _publish("audioTranscription.progress", new
        {
            requestId = operationId, operationId, step = 1, totalSteps = 5,
            stage = "python_install", stageName = "安装便携 Python 3.12",
            message = "正在将 Python 安装到 NTE-GUITAR 用户数据目录。"
        });
        var start = new ProcessStartInfo(installer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installRoot
        };
        foreach (string argument in new[] { "/quiet", "InstallAllUsers=0", $"TargetDir={installRoot}",
                     "Include_pip=1", "Include_launcher=0", "Include_test=0", "Include_doc=0", "PrependPath=0", "Shortcuts=0" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Python 3.12 安装程序。");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } throw; }
        if (process.ExitCode != 0 || !File.Exists(python))
            throw new InvalidOperationException($"Python 3.12 安装失败，安装程序退出码 {process.ExitCode}。下载文件：{installer}");
    }

    private async Task<bool> IsRuntimeReadyAsync(CancellationToken cancellationToken)
    {
        string python = ResolvePythonPath();
        string markerPath = ResolveRuntimeReadyMarkerPath();
        if (!File.Exists(python) || !File.Exists(markerPath)) return false;

        try
        {
            using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken));
            if (!marker.RootElement.TryGetProperty("format", out JsonElement format) || format.GetInt32() != 1 ||
                !marker.RootElement.TryGetProperty("python", out JsonElement markerPython) ||
                !string.Equals(markerPython.GetString(), Path.GetFileName(python), StringComparison.OrdinalIgnoreCase) ||
                !marker.RootElement.TryGetProperty("torchIndexUrl", out JsonElement markerTorchIndex) ||
                !string.Equals(markerTorchIndex.GetString(), _preferredTorchIndex, StringComparison.OrdinalIgnoreCase))
                return false;

            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                "import sys, torch, torchaudio, transformers, pretty_midi; " +
                "assert sys.version_info[:2] == (3, 12); " +
                "assert torch.__version__.split('+')[0] == '2.8.0'; " +
                $"assert (torch.version.cuda is not None) == {_preferredTorchIndex.EndsWith("cu126", StringComparison.OrdinalIgnoreCase) || _preferredTorchIndex.EndsWith("cu128", StringComparison.OrdinalIgnoreCase)}; " +
                "assert torchaudio.__version__.split('+')[0] == '2.8.0'");
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return false;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                _ = await stdout;
                _ = await stderr;
                return process.ExitCode == 0;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    private async Task ExtractBundledRuntimeAsync(int operationId, CancellationToken cancellationToken)
    {
        string python = ResolveBundledRuntimePythonPath();
        string marker = ResolveBundledRuntimeMarkerPath();
        if (File.Exists(python) && File.Exists(marker))
        {
            RebasePackagedRuntimeConfig(python);
            return;
        }
        Stream? runtimeArchive = typeof(AudioTranscriptionService).Assembly.GetManifestResourceStream("AudioTranscription.python312.zip");
        if (runtimeArchive is null)
        {
            string sidecarArchive = ResolveBundledRuntimeArchivePath();
            if (File.Exists(sidecarArchive))
                runtimeArchive = new FileStream(sidecarArchive, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, useAsync: true);
        }
        if (runtimeArchive is null) return;
        await using Stream resource = runtimeArchive;
        string runtimeParent = Path.GetDirectoryName(ResolveBundledRuntimeDirectory())!;
        Directory.CreateDirectory(runtimeParent);
        string destination = ResolveBundledRuntimeDirectory();
        string staging = destination + ".extract-" + Guid.NewGuid().ToString("N");
        string archivePath = Path.Combine(runtimeParent, "python312-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            _publish("audioTranscription.progress", new { requestId = operationId, operationId, stage = "runtime_extract", message = "解压内置 Python 运行环境到本地应用数据目录" });
            await using (var archiveFile = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                await resource.CopyToAsync(archiveFile, cancellationToken);
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, staging);
            string configPath = Path.Combine(staging, "pyvenv.cfg");
            if (!File.Exists(configPath) || !File.Exists(Path.Combine(staging, "Scripts", "python.exe")) || !File.Exists(Path.Combine(staging, ".nte-runtime-ready.json")))
                throw new InvalidDataException("内置 Python 环境压缩包缺少必要的解释器、配置或就绪标记。");
            string extractedBase = Path.Combine(destination, "base");
            string config = await File.ReadAllTextAsync(configPath, cancellationToken);
            config = System.Text.RegularExpressions.Regex.Replace(config, @"(?m)^home\s*=.*$", "home = " + extractedBase);
            config = System.Text.RegularExpressions.Regex.Replace(config, @"(?m)^executable\s*=.*$", "executable = " + Path.Combine(extractedBase, "python.exe"));
            config = System.Text.RegularExpressions.Regex.Replace(config, @"(?m)^command\s*=.*$", "command = bundled NTE-GUITAR Python runtime");
            await File.WriteAllTextAsync(configPath, config, cancellationToken);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
            _publish("audioTranscription.progress", new { requestId = operationId, operationId, stage = "runtime_ready", message = "内置 Python 运行环境解压完成" });
        }
        finally
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private async Task PumpBootstrapOutputAsync(StreamReader reader, int operationId, string stream, BoundedLogTail log, string runtimeName)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0) break;
            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (character is '\r' or '\n')
                {
                    PublishBootstrapLine(line);
                    line.Clear();
                }
                else if (!char.IsControl(character))
                {
                    line.Append(character);
                    if (line.Length > 8_000) line.Remove(0, line.Length - 8_000);
                }
            }
        }
        PublishBootstrapLine(line);

        void PublishBootstrapLine(StringBuilder content)
        {
            if (content.Length == 0) return;
            string message = content.ToString();
            lock (log) log.Add($"[{stream}] {message}");
            var percentMatch = System.Text.RegularExpressions.Regex.Match(message, @"(?:^|\s)(?<percent>\d{1,3})%");
            double? percent = percentMatch.Success && double.TryParse(percentMatch.Groups["percent"].Value,
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out double parsedPercent)
                ? parsedPercent
                : null;
            _publish("audioTranscription.progress", new
            {
                requestId = operationId, operationId, stage = "runtime_setup", stream, message,
                percent,
                stageName = runtimeName == "cuda" ? "下载 CUDA 推理依赖" : "准备 CPU 推理环境"
            });
        }
    }

    private static async Task PersistBootstrapLogAsync(string logPath, BoundedLogTail log)
    {
        string[] lines;
        lock (log) lines = log.Snapshot().ToArray();
        if (lines.Length == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllLinesAsync(logPath, lines);
    }

    private async Task<(string Executable, string[] PrefixArguments)?> FindPython312Async(CancellationToken cancellationToken)
    {
        var candidates = new List<(string Executable, string[] PrefixArguments)>();
        string bundledBase = ResolveBundledBasePythonPath();
        if (File.Exists(bundledBase)) candidates.Add((bundledBase, Array.Empty<string>()));
        string? launcher = FindOnPath("py.exe") ?? FindOnPath("py");
        if (!string.IsNullOrWhiteSpace(launcher)) candidates.Add((launcher, new[] { "-3.12" }));
        foreach (string name in new[] { "python3.12.exe", "python3.12", "python.exe", "python" })
        {
            string? executable = FindOnPath(name);
            if (!string.IsNullOrWhiteSpace(executable) && candidates.All(candidate => !string.Equals(candidate.Executable, executable, StringComparison.OrdinalIgnoreCase)))
                candidates.Add((executable, Array.Empty<string>()));
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(candidate.Executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (string arg in candidate.PrefixArguments) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')");
            try
            {
                if (!process.Start()) continue;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                string version = (await stdout).Trim();
                _ = await stderr;
                if (process.ExitCode == 0 && version == "3.12") return candidate;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or TimeoutException or OperationCanceledException)
            {
                try { if (process.HasExited == false) process.Kill(entireProcessTree: true); } catch { }
                if (ex is OperationCanceledException) throw;
            }
        }
        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    public async Task<AudioTranscriptionRequest> StartTranscriptionAsync(string audioPath)
    {
        string fullPath = Path.GetFullPath(audioPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Audio file not found.", fullPath);
        if (!SupportedAudioExtensions.Contains(Path.GetExtension(fullPath)))
            throw new ArgumentException("Supported audio files: WAV, MP3, FLAC, M4A, OGG and AAC.", nameof(audioPath));
        long audioLength = new FileInfo(fullPath).Length;
        if (audioLength <= 0) throw new InvalidDataException("所选音频文件为空。");
        if (audioLength > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("音频文件超过 4 GiB，无法安全复制到本地转写任务目录。");
        if (!await _operationGate.WaitAsync(0))
            throw new InvalidOperationException("An audio preparation or transcription request is already active.");

        int requestId = 0;
        try
        {
            await EnsureWorkerAsync();
            int id = requestId = NextId();
            lock (_sync)
            {
                _activeTranscriptionId = id;
                _audioPathsByRequest[id] = fullPath;
                _stderrLogTails[id] = new BoundedLogTail();
                _stderrLogTailOrder.Enqueue(id);
                while (_stderrLogTailOrder.Count > 8)
                    ForgetTranscriptionRequestLocked(_stderrLogTailOrder.Dequeue());
            }
            _publish("audioTranscription.progress", new { requestId = id, step = 1, totalSteps = 5, stage = "preflight", stageName = "检查音频与输出并确定运行设备" });
            await SendCommandAsync(new { requestId = id, command = "transcribe", audioPath = fullPath, allowCpu = false });
            _ = ObserveCommandAsync(id, "transcribe", releaseGate: true);
            return new AudioTranscriptionRequest(id, fullPath);
        }
        catch
        {
            if (requestId != 0) ForgetTranscriptionRequest(requestId);
            _operationGate.Release();
            throw;
        }
    }

    public async Task AllowCpuAsync(int requestId)
    {
        EnsureActive(requestId);
        JsonElement response = await SendAndObserveAsync("allow_cpu", new { targetRequestId = requestId });
        if (!response.GetProperty("result").GetProperty("accepted").GetBoolean())
            throw new InvalidOperationException("CPU confirmation was not accepted by the worker.");
    }

    public async Task CancelAsync(int requestId)
    {
        CancellationTokenSource? prepareCancellation = null;
        Process? bootstrapProcess = null;
        int? prepareWorkerRequestId = null;
        bool sendWorkerCancel = false;
        bool isPrepareOperation;
        lock (_sync)
        {
            isPrepareOperation = _activePrepareOperationId == requestId;
            if (isPrepareOperation)
            {
                prepareCancellation = _prepareCancellation;
                bootstrapProcess = _bootstrapProcess;
                prepareWorkerRequestId = _activePrepareWorkerRequestId;
                if (prepareWorkerRequestId.HasValue && _prepareWorkerCommandSent && !_prepareCancelSent)
                {
                    _prepareCancelSent = true;
                    sendWorkerCancel = true;
                }
            }
        }
        if (isPrepareOperation)
        {
            try { prepareCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            if (bootstrapProcess is { HasExited: false })
            {
                try { bootstrapProcess.Kill(entireProcessTree: true); } catch { }
            }
            if (sendWorkerCancel)
            {
                JsonElement cancelResponse = await SendAndObserveAsync("cancel", new { targetRequestId = prepareWorkerRequestId!.Value });
                if (!cancelResponse.GetProperty("result").GetProperty("cancelled").GetBoolean())
                    throw new InvalidOperationException("The worker no longer has an active preparation request to cancel.");
            }
            return;
        }

        EnsureActive(requestId);
        JsonElement response = await SendAndObserveAsync("cancel", new { targetRequestId = requestId });
        if (!response.GetProperty("result").GetProperty("cancelled").GetBoolean())
            throw new InvalidOperationException("The worker no longer has an active request to cancel.");

        // Python model loading and PyTorch/CUDA kernels can block inside native calls,
        // where the worker cannot observe its cooperative cancellation flag. Once the
        // worker has acknowledged cancellation, stop its process tree so the operation
        // ends promptly. The service will start a fresh worker for the next request.
        Process? worker;
        lock (_sync) worker = _activeTranscriptionId == requestId ? _process : null;
        if (worker is { HasExited: false })
        {
            try
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException) when (worker.HasExited) { }
        }
    }

    public async Task CleanupJobAsync(string jobId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(jobId, @"^audio-job-\d+-[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Invalid transcription job ID.", nameof(jobId));
        await SendAndObserveAsync("cleanup", new { jobId });
    }

    private async Task<JsonElement> SendAndObserveAsync(string command, object extra)
    {
        await EnsureWorkerAsync();
        int id = NextId();
        var data = JsonSerializer.SerializeToElement(extra);
        var record = new Dictionary<string, object?> { ["requestId"] = id, ["command"] = command };
        foreach (var property in data.EnumerateObject()) record[property.Name] = property.Value;
        await SendCommandAsync(record);
        return await ObserveCommandAsync(id, command, throwOnError: true);
    }

    private async Task<JsonElement> ObserveCommandAsync(int id, string command, bool releaseGate = false,
        bool throwOnError = false, bool publishWorkerFailure = true)
    {
        JsonElement response = default;
        try
        {
            response = await RegisterPending(id).Task;
            lock (_sync) _pending.Remove(id);
            if (command == "transcribe") await Task.Delay(60);
            RequestCompleted?.Invoke(id, response);
            if (response.TryGetProperty("type", out var type) && type.GetString() == "error")
            {
                if (publishWorkerFailure) PublishWorkerFailure(id, command, response);
                if (throwOnError) throw new InvalidOperationException(GetWorkerErrorMessage(response));
            }
            return response;
        }
        catch (Exception ex)
        {
            if (response.ValueKind == JsonValueKind.Undefined || !response.TryGetProperty("type", out var responseType) || responseType.GetString() != "error")
                _publish("audioTranscription.failed", new { requestId = id, command, error = ex.Message });
            if (command == "transcribe") ForgetTranscriptionRequest(id);
            if (throwOnError) throw;
            return response;
        }
        finally
        {
            lock (_sync) _pending.Remove(id);
            if (releaseGate)
            {
                lock (_sync) _activeTranscriptionId = null;
                _operationGate.Release();
            }
        }
    }

    private TaskCompletionSource<JsonElement> RegisterPending(int id)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(id, out var pending))
            {
                pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = pending;
            }
            return pending;
        }
    }

    private async Task EnsureWorkerAsync()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_process is { HasExited: false }) return;
        }
        string python = ResolvePythonPath();
        if (!File.Exists(python))
            throw new InvalidOperationException($"Python runtime is not installed. Prepare the audio runtime first. Expected: {python}");
        RebasePackagedRuntimeConfig(python);
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("Audio transcription worker files are missing.", _workerPath);

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!
        };
        // Preload mir_eval and its SciPy native modules on Python's main thread.
        // On Windows, a first import from the transcription executor can hang
        // inside a native SciPy extension before model weights finish loading.
        // Keep this in the EXE launcher so replacing the EXE also fixes an
        // older worker.py left beside an existing portable installation.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import runpy,sys,mir_eval; from pathlib import Path; worker=sys.argv[1]; sys.path.insert(0,str(Path(worker).parent)); sys.argv=sys.argv[1:]; runpy.run_path(worker,run_name='__main__')");
        start.ArgumentList.Add(_workerPath);
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(_dataRoot);
        string? modelRoot = ResolveModelRoot();
        if (modelRoot is not null) { start.ArgumentList.Add("--model-root"); start.ArgumentList.Add(modelRoot); }
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => FailPending(new IOException("Audio transcription worker exited."));
        int helloId = NextId();
        Task? stdoutTask = null;
        Task? stderrTask = null;
        try
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AudioTranscriptionService));
                if (_process is { HasExited: false })
                {
                    process.Dispose();
                    return;
                }
                if (!process.Start()) throw new InvalidOperationException("Could not start the audio transcription worker.");
                _process = process;
            }
            stdoutTask = ReadStdoutAsync(process);
            stderrTask = ReadStderrAsync(process);
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
            await SendCommandAsync(new { requestId = helloId, command = "hello" });
            JsonElement hello = await RegisterPending(helloId).Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!hello.TryGetProperty("type", out var helloType) || helloType.GetString() != "result")
                throw new InvalidOperationException(GetWorkerErrorMessage(hello));
            lock (_sync) _pending.Remove(helloId);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _pending.Remove(helloId);
                if (ReferenceEquals(_process, process)) _process = null;
                _stdoutTask = null;
                _stderrTask = null;
            }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if (stdoutTask is not null) try { await stdoutTask; } catch { }
            if (stderrTask is not null) try { await stderrTask; } catch { }
            process.Dispose();
            throw new InvalidOperationException("Audio transcription worker initialization failed: " + ex.Message, ex);
        }
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    // Preserve the offending JSONL record in diagnostics. Without this context,
                    // a System.Text.Json byte offset alone cannot reveal whether the worker
                    // emitted a log line, an unescaped Windows path, or a truncated response.
                    string preview = line.Length <= 2_000 ? line : line[..2_000] + "… [truncated]";
                    throw new InvalidDataException(
                        $"Audio transcription worker emitted invalid JSON on stdout ({ex.Message}). Record: {preview}", ex);
                }
                using (doc)
                {
                JsonElement root = doc.RootElement.Clone();
                int? id = root.TryGetProperty("requestId", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetInt32() : null;
                string type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "unknown" : "unknown";
                if (id.HasValue && type is "result" or "error")
                {
                    RegisterPending(id.Value).TrySetResult(root);
                    continue;
                }
                if (type == "progress")
                {
                    PublishWorkerProgress(root, id);
                }
                else if (type == "cpu_confirmation_required")
                {
                    _publish("audioTranscription.cpuConfirmationRequired", root);
                }
                else if (type == "worker_started")
                {
                    _publish("audioTranscription.workerStarted", root);
                }
                else if (type == "error")
                {
                    _publish("audioTranscription.failed", root);
                }
                }
            }
            if (!process.HasExited) FailPending(new IOException("Audio transcription worker closed its output stream."));
        }
        catch (Exception ex) { FailPending(ex); }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int? requestId;
                lock (_sync)
                {
                    requestId = _activeTranscriptionId;
                    if (requestId.HasValue && _stderrLogTails.TryGetValue(requestId.Value, out var tail))
                        tail.Add(line);
                }
                _publish("audioTranscription.log", new { requestId, message = line });
            }
        }
        catch { }
    }

    private sealed class BoundedLogTail
    {
        private const int MaxLines = 120;
        private const int MaxCharacters = 24_000;
        private const int MaxLineCharacters = 2_000;
        private readonly Queue<string> _lines = new();
        private int _characters;

        public void Add(string value)
        {
            string line = value.Length <= MaxLineCharacters ? value : value[..MaxLineCharacters];
            while (_lines.Count > 0 && (_lines.Count >= MaxLines || _characters + line.Length > MaxCharacters))
                _characters -= _lines.Dequeue().Length;
            _lines.Enqueue(line);
            _characters += line.Length;
        }

        public IReadOnlyList<string> Snapshot() => _lines.ToArray();
    }

    private void ForgetTranscriptionRequestLocked(int requestId)
    {
        _audioPathsByRequest.Remove(requestId);
        _stderrLogTails.Remove(requestId);
        if (_activeTranscriptionId == requestId) _activeTranscriptionId = null;
        if (_stderrLogTailOrder.Contains(requestId))
        {
            int[] retained = _stderrLogTailOrder.Where(id => id != requestId).ToArray();
            _stderrLogTailOrder.Clear();
            foreach (int id in retained) _stderrLogTailOrder.Enqueue(id);
        }
    }

    private async Task SendCommandAsync(object command)
    {
        await _stdinGate.WaitAsync();
        try
        {
            Process process;
            lock (_sync) process = _process is { HasExited: false } running
                ? running : throw new InvalidOperationException("Audio transcription worker is not running.");
            string json = JsonSerializer.Serialize(command);
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
        }
        finally { _stdinGate.Release(); }
    }

    private void PublishWorkerFailure(int id, string command, JsonElement root)
    {
        JsonElement error = root.TryGetProperty("error", out var e) ? e : default;
        string message = GetWorkerErrorMessage(root);
        _publish("audioTranscription.failed", new { requestId = id, command, error = message, detail = error });
    }

    private void PublishWorkerProgress(JsonElement root, int? workerRequestId)
    {
        int? operationId = null;
        lock (_sync)
            if (workerRequestId.HasValue && _activePrepareWorkerRequestId == workerRequestId)
                operationId = _activePrepareOperationId;

        if (!operationId.HasValue)
        {
            int? step = null;
            string? stageName = null;
            if (workerRequestId.HasValue && root.TryGetProperty("stage", out var stageElement))
            {
                string stage = stageElement.GetString() ?? "";
                (int? Step, string? Name) phase = stage switch
                {
                    "model_loading" or "model_code_prepare" or "model_code_ready" or "model_import"
                        or "model_weights_loading" or "model_weights_loaded" or "device_transfer" or "model_ready"
                        => (2, "加载 SheetSage2 与 MERT 模型"),
                    "audio_decode" or "decode_audio" => (3, "解码音频并准备滑动窗口"),
                    "notation" or "midi_output" or "writing_midi" => (5, "生成 MIDI 并导入曲库"),
                    "transcribing" or "window" or "window_complete" or "encoding" or "decoding" or "inference" => (4, "按滑动窗口推理并提取旋律"),
                    _ => (null, null)
                };
                step = phase.Step;
                stageName = phase.Name;
            }
            if (step.HasValue)
            {
                var transcriptionProgress = new Dictionary<string, object?>();
                foreach (JsonProperty property in root.EnumerateObject()) transcriptionProgress[property.Name] = property.Value;
                transcriptionProgress["requestId"] = workerRequestId;
                transcriptionProgress["step"] = step;
                transcriptionProgress["totalSteps"] = 5;
                transcriptionProgress["stageName"] = stageName;
                _publish("audioTranscription.progress", transcriptionProgress);
            }
            else _publish("audioTranscription.progress", root);
            return;
        }

        var payload = new Dictionary<string, object?>();
        foreach (JsonProperty property in root.EnumerateObject()) payload[property.Name] = property.Value;
        payload["requestId"] = operationId.Value;
        payload["operationId"] = operationId.Value;
        payload["workerRequestId"] = workerRequestId;
        _publish("audioTranscription.progress", payload);
    }

    private static string GetWorkerErrorMessage(JsonElement root)
    {
        JsonElement error = root.TryGetProperty("error", out var e) ? e : default;
        return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
            ? m.GetString() ?? "Worker request failed." : "Worker request failed.";
    }

    private void FailPending(Exception ex)
    {
        lock (_sync)
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
    }

    private int NextId() => Interlocked.Increment(ref _nextRequestId);

    private void EnsureActive(int id)
    {
        lock (_sync)
            if (_activeTranscriptionId != id)
                throw new InvalidOperationException("The requested transcription is no longer active.");
    }

    private string ResolveWorkerPath()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "worker.py");
        if (File.Exists(packaged)) return Path.GetFullPath(packaged);
        string development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "AudioTranscription", "worker.py"));
        if (File.Exists(development)) return development;
        return ExtractEmbeddedAudioFile("AudioTranscription.worker.py", "worker.py");
    }

    private string ResolveBootstrapPath()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "bootstrap.py");
        if (File.Exists(packaged)) return Path.GetFullPath(packaged);
        string development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "AudioTranscription", "bootstrap.py"));
        if (File.Exists(development)) return development;
        return ExtractEmbeddedAudioFile("AudioTranscription.bootstrap.py", "bootstrap.py");
    }

    private string ExtractEmbeddedAudioFile(string resourceName, string fileName)
    {
        string directory = Path.Combine(_dataRoot, "audio-transcription", "app");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        using Stream? resource = typeof(AudioTranscriptionService).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
            throw new FileNotFoundException($"The application is missing its embedded audio transcription resource: {resourceName}");

        string temporary = destination + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            resource.CopyTo(output);
        File.Move(temporary, destination, overwrite: true);

        if (fileName is "bootstrap.py" or "worker.py")
        {
            foreach (string companion in new[] { "downloads.json", "requirements.txt" })
            {
                using Stream? companionResource = typeof(AudioTranscriptionService).Assembly
                    .GetManifestResourceStream($"AudioTranscription.{companion}");
                if (companionResource is null)
                    throw new FileNotFoundException($"The application is missing its embedded audio transcription resource: AudioTranscription.{companion}");
                string companionPath = Path.Combine(directory, companion);
                string companionTemporary = companionPath + ".tmp";
                using (var output = new FileStream(companionTemporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    companionResource.CopyTo(output);
                File.Move(companionTemporary, companionPath, overwrite: true);
            }
        }

        return destination;
    }

    private string ResolvePythonPath()
    {
        if (IsCudaIndex(_preferredTorchIndex))
        {
            string cuda = ResolveLocalRuntimePythonPath("cuda");
            if (File.Exists(cuda)) return cuda;
            return ResolveBundledRuntimePythonPath();
        }
        string bundled = ResolveBundledRuntimePythonPath();
        if (File.Exists(bundled) && BundledRuntimeMatchesPreferredWheel()) return bundled;
        string local = ResolveLocalRuntimePythonPath();
        if (File.Exists(local)) return local;
        string packaged = Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python.exe");
        return File.Exists(packaged) ? packaged : local;
    }

    private string ResolveLocalRuntimePythonPath(string runtimeName = "cpu") =>
        Path.Combine(_dataRoot, "audio-transcription", "runtime",
            runtimeName == "cuda" ? "python312-cuda" : "python312", "Scripts", "python.exe");

    private string ResolveLocalRuntimeMarkerPath(string runtimeName = "cpu") =>
        Path.Combine(_dataRoot, "audio-transcription", "runtime",
            runtimeName == "cuda" ? "python312-cuda" : "python312", ".nte-runtime-ready.json");

    private string ResolveBundledBasePythonPath() =>
        File.Exists(Path.Combine(ResolveBundledRuntimeDirectory(), "base", "python.exe"))
            ? Path.Combine(ResolveBundledRuntimeDirectory(), "base", "python.exe")
            : Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python312", "base", "python.exe");

    private void RebasePackagedRuntimeConfig(string python)
    {
        string packagedRuntime = Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python312");
        string packagedPython = Path.Combine(packagedRuntime, "Scripts", "python.exe");
        if (!string.Equals(Path.GetFullPath(python), Path.GetFullPath(packagedPython), StringComparison.OrdinalIgnoreCase)) return;

        string baseDirectory = Path.Combine(packagedRuntime, "base");
        string basePython = Path.Combine(baseDirectory, "python.exe");
        if (!File.Exists(basePython))
            throw new InvalidOperationException($"Portable Python runtime is incomplete. Missing bundled base interpreter: {basePython}");

        string configPath = Path.Combine(packagedRuntime, "pyvenv.cfg");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Portable Python runtime is incomplete. Missing venv config: {configPath}");

        string config = File.ReadAllText(configPath);
        string rebased = System.Text.RegularExpressions.Regex.Replace(config, @"(?m)^home\s*=.*$", "home = " + baseDirectory);
        rebased = System.Text.RegularExpressions.Regex.Replace(rebased, @"(?m)^executable\s*=.*$", "executable = " + basePython);
        rebased = System.Text.RegularExpressions.Regex.Replace(rebased, @"(?m)^command\s*=.*$", "command = portable NTE-GUITAR Python runtime");
        if (!string.Equals(config, rebased, StringComparison.Ordinal))
            File.WriteAllText(configPath, rebased);
    }

    private string ResolveBundledRuntimePythonPath() =>
        File.Exists(ResolveExtractedRuntimePythonPath())
            ? ResolveExtractedRuntimePythonPath()
            : Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python312", "Scripts", "python.exe");

    private string ResolveExtractedRuntimePythonPath() =>
        Path.Combine(_dataRoot, "audio-transcription", "runtime", "python312-bundled", "Scripts", "python.exe");

    private string ResolveBundledRuntimeDirectory() => Path.GetDirectoryName(Path.GetDirectoryName(ResolveExtractedRuntimePythonPath())!)!;

    private string ResolveBundledRuntimeArchivePath() => Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python312.zip");

    private bool HasBundledRuntimeArchive() =>
        typeof(AudioTranscriptionService).Assembly.GetManifestResourceInfo("AudioTranscription.python312.zip") is not null ||
        File.Exists(ResolveBundledRuntimeArchivePath());

    private string? ResolveBundledFfmpegPath()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(packaged)) return packaged;
        string development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "AudioTranscription", "ffmpeg", "bin", "ffmpeg.exe"));
        if (File.Exists(development)) return development;

        string local = Path.Combine(_dataRoot, "audio-transcription", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        using Stream? resource = typeof(AudioTranscriptionService).Assembly.GetManifestResourceStream("AudioTranscription.ffmpeg.exe");
        if (resource is null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        string temporary = local + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            resource.CopyTo(output);
        File.Move(temporary, local, overwrite: true);
        return local;
    }

    private string ResolveBundledRuntimeMarkerPath() =>
        File.Exists(ResolveExtractedRuntimePythonPath())
            ? Path.Combine(ResolveBundledRuntimeDirectory(), ".nte-runtime-ready.json")
            : Path.Combine(AppContext.BaseDirectory, "AudioTranscription", "python312", ".nte-runtime-ready.json");

    private string ResolveRuntimeReadyMarkerPath()
    {
        if (IsCudaIndex(_preferredTorchIndex)) return ResolveLocalRuntimeMarkerPath("cuda");
        return string.Equals(ResolvePythonPath(), ResolveBundledRuntimePythonPath(), StringComparison.OrdinalIgnoreCase)
            ? ResolveBundledRuntimeMarkerPath()
            : ResolveLocalRuntimeMarkerPath();
    }

    private bool BundledRuntimeMatchesPreferredWheel()
    {
        try
        {
            using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(ResolveBundledRuntimeMarkerPath()));
            return marker.RootElement.TryGetProperty("torchIndexUrl", out var index) &&
                   string.Equals(index.GetString(), _preferredTorchIndex, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public string? ResolveModelRoot()
    {
        string? configured = null;
        try { if (File.Exists(_modelPathFile)) configured = File.ReadAllText(_modelPathFile).Trim(); } catch { }
        var candidates = new[] { configured, Path.Combine(AppContext.BaseDirectory, "Models"), Path.Combine(AppContext.BaseDirectory, "models") };
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                string root = Path.GetFullPath(candidate);
                if (HasModelSnapshot(root, "SheetSage2", "398b22834dac7dd05e09b9c4e40a39fc479ec502") &&
                    HasModelSnapshot(root, "MERT-v2-FullSong", "d8ba1c745e733b3908ce6ad16ebeb17ac7600a42")) return root;
            }
            catch { }
        }
        return null;
    }

    public void SetModelRoot(string path)
    {
        string root = Path.GetFullPath(path);
        if (!HasModelSnapshot(root, "SheetSage2", "398b22834dac7dd05e09b9c4e40a39fc479ec502") ||
            !HasModelSnapshot(root, "MERT-v2-FullSong", "d8ba1c745e733b3908ce6ad16ebeb17ac7600a42"))
            throw new ArgumentException("所选目录必须包含 SheetSage2 和 MERT-v2-FullSong 两个模型文件夹。", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(_modelPathFile)!);
        File.WriteAllText(_modelPathFile, root);
    }

    private static bool HasModelSnapshot(string root, string directory, string revision)
    {
        string model = Path.Combine(root, directory);
        string pinned = Path.Combine(model, revision);
        if (Directory.Exists(pinned)) model = pinned;
        return File.Exists(Path.Combine(model, "config.json")) && File.Exists(Path.Combine(model, "model.safetensors"));
    }

    private bool IsDataRootModelReady()
    {
        string root = Path.Combine(_dataRoot, "models");
        return File.Exists(Path.Combine(root, "SheetSage2", "398b22834dac7dd05e09b9c4e40a39fc479ec502", ".nte-verified.json")) &&
               File.Exists(Path.Combine(root, "MERT-v2-FullSong", "d8ba1c745e733b3908ce6ad16ebeb17ac7600a42", ".nte-verified.json"));
    }

    public async Task<object> InspectEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        string? compatibleCudaIndex = await DetectCudaTorchWheelIndexUrlAsync();
        bool runtimeReady = await IsRuntimeReadyAsync(cancellationToken);
        string python = ResolvePythonPath();
        string? ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg") ?? ResolveBundledFfmpegPath();
        string? device = null;
        string? torch = null;
        string? deviceError = null;
        if (runtimeReady)
        {
            try
            {
                var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("import json,torch; available=torch.cuda.is_available(); device=None; error=None; " +
                    "\ntry:\n device=torch.cuda.get_device_name(0) if available else None\nexcept Exception as exc:\n available=False; error=str(exc)\n" +
                    "print(json.dumps({'torch':torch.__version__,'cuda':torch.version.cuda,'available':available,'device':device,'error':error}))");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inspect PyTorch runtime.");
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string error = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
                using var json = JsonDocument.Parse(output);
                var root = json.RootElement;
                torch = root.GetProperty("torch").GetString();
                device = root.GetProperty("available").GetBoolean() ? root.GetProperty("device").GetString() : "CPU";
                if (root.TryGetProperty("error", out var cudaError) && cudaError.ValueKind == JsonValueKind.String) deviceError = cudaError.GetString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { deviceError = ex.Message; }
        }
        return new
        {
            runtimeReady,
            runtimeBundled = HasBundledRuntimeArchive(),
            selectedRuntime = IsCudaIndex(_preferredTorchIndex) ? "cuda" : "cpu",
            cudaRuntimeReady = File.Exists(ResolveLocalRuntimePythonPath("cuda")) && File.Exists(ResolveLocalRuntimeMarkerPath("cuda")),
            compatibleCudaIndex,
            runtimePath = runtimeReady ? python : null,
            ffmpegReady = !string.IsNullOrWhiteSpace(ffmpeg),
            ffmpegPath = ffmpeg,
            modelReady = ResolveModelRoot() is not null || IsDataRootModelReady(),
            modelRoot = ResolveModelRoot() ?? (IsDataRootModelReady() ? Path.Combine(_dataRoot, "models") : null),
            torchVersion = torch,
            device,
            deviceError,
            cudaAvailable = string.Equals(device, "CUDA", StringComparison.OrdinalIgnoreCase) || (device is not null && device != "CPU"),
            cudaWheelIndex = compatibleCudaIndex,
            selectedTorchIndex = _preferredTorchIndex,
            nvidiaGpuDetected = !string.IsNullOrWhiteSpace(FindOnPath("nvidia-smi.exe") ?? FindOnPath("nvidia-smi")),
            license = "SheetSage2 and MERT-v2-FullSong: CC BY-NC 4.0; non-commercial use only."
        };
    }

    private static bool IsCudaIndex(string indexUrl) =>
        indexUrl.EndsWith("/cu126", StringComparison.OrdinalIgnoreCase) ||
        indexUrl.EndsWith("/cu128", StringComparison.OrdinalIgnoreCase);

    private string ReadSelectedTorchIndex()
    {
        try
        {
            string selected = File.ReadAllText(_torchSelectionPath).Trim();
            if (string.Equals(selected, CpuTorchIndex, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selected, "https://download.pytorch.org/whl/cu126", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selected, "https://download.pytorch.org/whl/cu128", StringComparison.OrdinalIgnoreCase))
                return selected;
        }
        catch { }
        return CpuTorchIndex;
    }

    private static async Task<string?> DetectCudaTorchWheelIndexUrlAsync()
    {
        string? nvidiaSmi = FindOnPath("nvidia-smi.exe") ?? FindOnPath("nvidia-smi");
        if (nvidiaSmi is null) return null;
        try
        {
            var start = new ProcessStartInfo(nvidiaSmi) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var process = Process.Start(start);
            if (process is null) return null;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var match = System.Text.RegularExpressions.Regex.Match(output, @"CUDA Version:\s*(\d+)\.(\d+)");
            if (process.ExitCode != 0 || !match.Success) return null;
            int major = int.Parse(match.Groups[1].Value);
            int minor = int.Parse(match.Groups[2].Value);
            if (major > 12 || major == 12 && minor >= 8) return "https://download.pytorch.org/whl/cu128";
            if (major == 12 && minor >= 6) return "https://download.pytorch.org/whl/cu126";
        }
        catch { }
        return null;
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioTranscriptionService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        Process? bootstrapProcess;
        CancellationTokenSource? prepareCancellation;
        Task? prepareTask;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            bootstrapProcess = _bootstrapProcess;
            prepareCancellation = _prepareCancellation;
            prepareTask = _prepareTask;
        }
        try { prepareCancellation?.Cancel(); } catch { }
        if (bootstrapProcess is { HasExited: false })
        {
            try { bootstrapProcess.Kill(entireProcessTree: true); } catch { }
        }
        if (process is { HasExited: false })
        {
            try
            {
                int id = NextId();
                await SendCommandAsync(new { requestId = id, command = "shutdown" });
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        if (prepareTask is not null) try { await prepareTask.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        if (_stdoutTask is not null) try { await _stdoutTask; } catch { }
        if (_stderrTask is not null) try { await _stderrTask; } catch { }
        process?.Dispose();
    }
}
