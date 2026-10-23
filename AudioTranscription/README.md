# Local audio transcription worker

The worker runs SheetSage2 on this computer. Audio is read from its local path and is not uploaded to an application service. The model and Python runtime are stored under `%LocalAppData%\NTE-GUITAR\models` and `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime`; transient jobs are stored under `%LocalAppData%\NTE-GUITAR\audio-transcription\jobs`.

Full packages created with `publish_to_ng.ps1 -RuntimeMode Full` embed the prepared Python 3.12 environment and its matching base interpreter as a compressed EXE resource; first use extracts it under `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime\python312-bundled`. The package builder reads the prepared venv from `AudioTranscription\python312` or the current user's app data. Models are not redistributed. Put authorized model snapshots in the EXE sibling `Models\` folder, with `SheetSage2` and `MERT-v2-FullSong` subdirectories. Alternatively use **指定模型目录** in the transcription page and choose a folder containing those two directories (each may contain the pinned revision subfolder or snapshot files directly). The selected path is remembered for this user. If sibling models are not present, the confirmed prepare flow downloads the pinned snapshots to LocalAppData. Use `-RuntimeMode Lite` for the small package; it asks for confirmation before downloading and installing missing runtime dependencies.

## Setup

Install 64-bit Python 3.12 and make `ffmpeg.exe` available on `PATH`. On the first model-prepare request, the app finds Python 3.12 through the Windows `py` launcher or `PATH`, creates a private venv under `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime\python312`, and installs pinned PyTorch 2.8.0, torchaudio 2.8.0, and the requirements from the official CPU wheel index. Runtime setup requires at least 6 GiB free and its progress is shown in the app. The app does not install Python or ffmpeg themselves. Model preparation checks free space before downloading each model and reports the required and available bytes. The model files use about 2.8 GB together; allow additional space for the Python runtime and temporary download files. For CUDA 12.8, users who want GPU wheels can manually use `--torch-index-url https://download.pytorch.org/whl/cu128` with the bootstrap command below; check PyTorch's published installation guidance if the GPU driver requires another CUDA wheel index.

From the application directory, create the dedicated runtime and prepare the pinned model snapshots (these commands download the model files only when run):

```powershell
$data = Join-Path $env:LOCALAPPDATA 'NTE-GUITAR'
py -3.12 AudioTranscription\bootstrap.py --data-root $data --install-runtime
# For a compatible CUDA 12.8 installation, pass --torch-index-url https://download.pytorch.org/whl/cu128
py -3.12 AudioTranscription\bootstrap.py --data-root $data --prepare-models
```

The app should launch `worker.py` using `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime\python312\Scripts\python.exe` and pass `--data-root` pointing at `%LocalAppData%\NTE-GUITAR`. Model files are listed at a fixed revision before download. The downloader uses ModelScope's repository listing and file URLs, with a Hugging Face Hub listing/resolve fallback because `m-a-p/SheetSage2` is not currently present at its ModelScope API path. It resumes partial files with HTTP Range, checks advertised sizes, and verifies pinned SHA-256 values. Every Python file selected from the SheetSage2 and MERT snapshots has a revision-pinned SHA-256 in `downloads.json`; model weights also have pinned hashes. Snapshot markers record calculated hashes for all downloaded files and subsequent prepares recheck all pinned hashes. It never requests Qwen or ASR/aligner files.

## JSON Lines protocol

One JSON object is sent on stdin per line and one JSON object is emitted on stdout per line. `requestId` is a positive, strictly increasing integer. Progress and `cpu_confirmation_required` records are nonterminal; each accepted request ends in one `result` or `error` record. Errors have `{ "code", "message", "detail"? }` fields. Python/native library stdout is not used for logging; diagnostics belong on stderr.

Commands:

- `hello`: ends in a `result` record whose `result` payload includes the protocol version, local roots, device availability, and commands.
- `prepare`: verify ffmpeg and prepare the pinned SheetSage2 and MERT snapshots. Only one `prepare` or `transcribe` request can be active; concurrent work requests receive a terminal `worker_busy` error. Shutdown cancels the active request and waits for its terminal record; queued work requests are not accepted.
- `transcribe`: accepts `audioPath` and optional `allowCpu`. Produces `melodyVocalPath`, `melodyInstrumentalPath`, duration, warnings, device, timing, and metadata. The returned per-job directory remains until the caller has imported it; send `cleanup` with `jobId` afterward. Cancellation deletes partial job output.
- `allow_cpu`: accepts `targetRequestId` for the pending transcription after `cpu_confirmation_required`.
- `cancel`: accepts `targetRequestId` and cancels the active request. Model loading itself is a blocking Transformers call and cannot be interrupted safely; cancellation during loading is recorded and inference is skipped as soon as loading returns. During inference, cancellation raises from the model's per-window progress callback as soon as the pinned model reports progress. Job files are removed on cancellation.
- `cleanup`: accepts `jobId` after a successful import and removes only that job directory.
- `shutdown`: complete the current request and stop the worker.

When CUDA is unavailable, the worker emits `cpu_confirmation_required` and waits. It does not begin inference on CPU without an explicit `allow_cpu` command.

## Attribution and license

SheetSage2 is published by the Image Multimodal Art Projection (m-a-p) team: [model card](https://huggingface.co/m-a-p/SheetSage2) and [source revision](https://huggingface.co/m-a-p/SheetSage2/tree/398b22834dac7dd05e09b9c4e40a39fc479ec502). Its model weights and code are licensed under **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)**. The required MERT-v2-FullSong encoder is attributed to m-a-p at revision `d8ba1c745e733b3908ce6ad16ebeb17ac7600a42`; see its [model card](https://huggingface.co/m-a-p/MERT-v2-FullSong) and [pinned source files](https://huggingface.co/m-a-p/MERT-v2-FullSong/tree/d8ba1c745e733b3908ce6ad16ebeb17ac7600a42). Its model and source are also licensed under **CC BY-NC 4.0**. These licenses restrict commercial use and redistribution. Do not redistribute the model weights or use this feature commercially without obtaining suitable permission from the rights holders. The app downloads model files to the user's local app-data directory only after model preparation is requested.

The upstream model's custom Python code is executed through Transformers `trust_remote_code=True`. The pinned SheetSage2 custom loader explicitly accepts `base_model_path`; the worker uses that reference-supported argument to load the fixed MERT snapshot locally. Both model revisions and expected weight/code hashes are pinned; the worker loads only verified local snapshots with offline mode enabled.
