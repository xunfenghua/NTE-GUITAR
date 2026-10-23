"""Long-lived local JSON Lines worker for SheetSage2 audio transcription."""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from pathlib import Path
from typing import Any

from bootstrap import AppPaths, ensure_models, model_paths, verify_model_snapshots


PROTOCOL_VERSION = 1
_protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", buffering=1, encoding="utf-8")
os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
sys.stdout = sys.stderr
_write_lock = threading.Lock()
_state_lock = threading.RLock()
_last_request_id = 0
_active: dict[int, dict[str, Any]] = {}
_executor = concurrent.futures.ThreadPoolExecutor(max_workers=1, thread_name_prefix="sheetsage")
_paths: AppPaths | None = None
_model_root: Path | None = None
_model: Any = None
_torch: Any = None
_shutdown = threading.Event()


def emit(record: dict[str, Any]) -> None:
    """Write exactly one compact JSON record per line to protocol stdout."""
    with _write_lock:
        _protocol.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
        _protocol.flush()


def terminal(request_id: int, ok: bool, **payload: Any) -> None:
    with _state_lock:
        state = _active.get(request_id)
        if state is None or state.get("terminal"):
            return
        state["terminal"] = True
    emit({"type": "result" if ok else "error", "requestId": request_id, **payload})
    with _state_lock:
        _active.pop(request_id, None)


def progress(request_id: int, stage: str, **fields: Any) -> None:
    emit({"type": "progress", "requestId": request_id, "stage": stage, **fields})


def error_payload(code: str, message: str, detail: str | None = None) -> dict[str, Any]:
    result: dict[str, Any] = {"code": code, "message": message}
    if detail:
        result["detail"] = detail[:12000]
    return result


def require_id(record: dict[str, Any]) -> int:
    value = record.get("requestId")
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError("requestId must be a positive integer")
    return value


def ensure_ffmpeg() -> str:
    bundled = Path(__file__).resolve().parent / "ffmpeg" / "bin" / "ffmpeg.exe"
    candidate = str(bundled) if bundled.is_file() else shutil.which("ffmpeg")
    if not candidate:
        raise RuntimeError("ffmpeg was not found. Install ffmpeg and add its bin directory to PATH.")
    probe = subprocess.run([candidate, "-version"], capture_output=True, text=True, timeout=10)
    if probe.returncode != 0:
        raise RuntimeError("ffmpeg could not start: " + (probe.stderr or probe.stdout)[0:1000])
    return candidate


def device_state() -> dict[str, Any]:
    import torch
    cuda_available = bool(torch.cuda.is_available())
    cuda_device = None
    cuda_error = None
    if cuda_available:
        try:
            torch.cuda.init()
            cuda_device = torch.cuda.get_device_name(0)
        except Exception as exc:
            cuda_available = False
            cuda_error = str(exc)
    return {"cudaAvailable": cuda_available, "cudaDevice": cuda_device, "cudaError": cuda_error,
            "pythonVersion": sys.version.split()[0], "torchVersion": torch.__version__}


def load_model(request_id: int, allow_cpu: bool) -> tuple[Any, Any, str]:
    global _model, _torch
    import torch

    cuda_available = bool(torch.cuda.is_available())
    cpu = not cuda_available or allow_cpu
    if not cuda_available and not allow_cpu:
        raise CpuConfirmationRequired("没有可用 CUDA 设备。确认后将使用 CPU 转写，速度可能明显较慢。")
    if cuda_available and not allow_cpu:
        try:
            torch.cuda.init()
            torch.cuda.get_device_name(0)
        except Exception as exc:
            raise CpuConfirmationRequired(f"CUDA 初始化失败：{exc}。可以确认后改用 CPU 转写。") from exc
    sheetsage_path, mert_path = model_paths(_paths, _model_root)
    if not sheetsage_path.is_dir() or not mert_path.is_dir():
        raise FileNotFoundError("Model snapshots are incomplete; run prepare first.")
    progress(request_id, "model_loading", message="Loading verified local SheetSage2 and MERT snapshots",
             device="cuda" if not cpu else "cpu",
             deviceName=torch.cuda.get_device_name(0) if not cpu else "CPU")
    device = "cuda" if not cpu else "cpu"
    device_name = torch.cuda.get_device_name(0) if not cpu else "CPU"
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    # Transformers caches trust_remote_code modules separately from the verified
    # model snapshot. A previous interrupted load can leave a partial module tree
    # (for example, without chord_spelling_sheetsage2.py), which is then reused and
    # fails even though the pinned source file is present. Keep this cache app-local
    # and copy every verified source file into this revision before loading it.
    modules_cache = _paths.runtime / "huggingface-modules"
    os.environ["HF_MODULES_CACHE"] = str(modules_cache)
    cached_revision = modules_cache / "transformers_modules" / sheetsage_path.name
    progress(request_id, "model_code_prepare", message="正在准备应用内模型代码缓存", device=device, deviceName=device_name)
    # Removing the entire cache can fail on Windows when a .pyc is still open.
    # Transformers' dynamic-module dependency scanner can also omit a relative
    # import (tokenization_sheetsage2 imports chord_spelling_sheetsage2).
    # Seed every source file without removing a usable cache tree.
    cached_revision.mkdir(parents=True, exist_ok=True)
    for source_file in sheetsage_path.rglob("*.py"):
        relative_path = source_file.relative_to(sheetsage_path)
        cached_file = cached_revision / relative_path
        cached_file.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_file, cached_file)
    progress(request_id, "model_code_ready", message="模型代码缓存准备完成", device=device, deviceName=device_name)
    progress(request_id, "model_import", message="正在加载 Transformers 推理组件", device=device, deviceName=device_name)
    from transformers import AutoModel

    progress(request_id, "model_weights_loading",
             message="正在读取 SheetSage2 与 MERT 权重；模型加载器没有提供细分百分比，此阶段会显示动态等待状态",
             device=device, deviceName=device_name)
    model = AutoModel.from_pretrained(
        str(sheetsage_path), trust_remote_code=True, local_files_only=True,
        base_model_path=str(mert_path),
    ).eval()
    progress(request_id, "model_weights_loaded", message="模型权重读取完成", device=device, deviceName=device_name)
    if not cpu:
        progress(request_id, "device_transfer", message="正在将模型载入 CUDA 显存", device=device, deviceName=device_name)
        try:
            model = model.to("cuda")
        except Exception as exc:
            del model
            raise CpuConfirmationRequired(f"CUDA 推理设备初始化失败：{exc}。可以确认后改用 CPU 转写。") from exc
    else:
        model = model.to("cpu")
    progress(request_id, "model_ready", message=f"模型已载入并就绪（{device_name}）", device=device, deviceName=device_name)
    _model, _torch = model, torch
    return model, torch, "cpu" if cpu else "cuda"


class CpuConfirmationRequired(Exception):
    pass


def run_prepare(request_id: int) -> None:
    try:
        ffmpeg_path = ensure_ffmpeg()
        progress(request_id, "preparing", message="Checking pinned model snapshots")
        if _model_root is not None:
            sheetsage_path, mert_path = model_paths(_paths, _model_root)
            if not sheetsage_path.is_dir() or not mert_path.is_dir():
                raise FileNotFoundError(f"Model snapshots not found under {_model_root}; expected SheetSage2 and MERT-v2-FullSong revision folders.")
            verify_model_snapshots(_model_root)
        else:
            ensure_models(_paths, lambda stage, **fields: progress(request_id, stage, **fields),
                          lambda: _active.get(request_id, {}).get("cancelled", False))
        if _active.get(request_id, {}).get("cancelled"):
            raise InterruptedError("Model preparation was cancelled.")
        result = {"prepared": True, "ffmpegPath": ffmpeg_path,
                  "models": {"sheetsage2": str(model_paths(_paths, _model_root)[0]), "mert": str(model_paths(_paths, _model_root)[1])},
                  "device": device_state()}
        terminal(request_id, True, result=result)
    except Exception as exc:
        terminal(request_id, False, error=error_payload(
            "cancelled" if isinstance(exc, InterruptedError) else "prepare_failed",
            str(exc), traceback.format_exc()))


def run_transcribe(request_id: int, record: dict[str, Any]) -> None:
    job_dir: Path | None = None
    keep_dir = False
    try:
        audio = Path(str(record.get("audioPath", ""))).expanduser().resolve(strict=True)
        if not audio.is_file():
            raise ValueError("Audio path must name an existing file.")
        if audio.stat().st_size <= 0:
            raise ValueError("Audio file is empty.")
        if audio.suffix.lower() not in {".wav", ".mp3", ".flac", ".m4a", ".ogg", ".aac"}:
            raise ValueError("Supported audio files: WAV, MP3, FLAC, M4A, OGG and AAC.")
        ensure_ffmpeg()
        allowed = bool(record.get("allowCpu", False))
        try:
            model, torch, device = load_model(request_id, allowed)
        except CpuConfirmationRequired as exc:
            cancelled = False
            with _state_lock:
                state = _active.get(request_id)
                if state is None or state.get("cancelled"):
                    cancelled = True
                else:
                    state["waiting_cpu"] = True
                    state["transcribe_record"] = record
                    # Publish while holding the state lock so cancel cannot terminalize
                    # this request before its confirmation-required event is written.
                    emit({"type": "cpu_confirmation_required", "requestId": request_id,
                          "message": str(exc),
                          "estimatedPerformance": "CPU transcription may take substantially longer."})
            if cancelled:
                terminal(request_id, False, error=error_payload("cancelled", "Transcription was cancelled."))
            return

        if _active.get(request_id, {}).get("cancelled"):
            raise InterruptedError("Transcription was cancelled.")
        job_dir = Path(tempfile.mkdtemp(prefix=f"audio-job-{request_id}-", dir=_paths.jobs))
        job_audio = job_dir / ("input" + audio.suffix.lower())
        shutil.copy2(audio, job_audio)
        output_dir = job_dir / "output"
        output_dir.mkdir()
        started = time.monotonic()
        progress(request_id, "audio_decode", message="Decoding audio and preparing overlapping inference windows")
        progress(request_id, "transcribing", message="Transcribing audio with overlapping windows")

        def report(event: dict[str, Any]) -> None:
            if _active.get(request_id, {}).get("cancelled"):
                raise InterruptedError("Transcription was cancelled.")
            fields = {key: value for key, value in event.items() if key != "stage"}
            progress(request_id, str(event.get("stage", "transcribing")), **fields)

        if _active.get(request_id, {}).get("cancelled"):
            raise InterruptedError("Transcription was cancelled.")
        result = model.transcribe(str(job_audio), output_dir=str(output_dir), melody_only=True, progress=report)
        if _active.get(request_id, {}).get("cancelled"):
            raise InterruptedError("Transcription was cancelled.")
        vocal = output_dir / "melody_vocal.mid"
        instrumental = output_dir / "melody_instrumental.mid"
        if not vocal.is_file() and isinstance(result, dict) and isinstance(result.get("melody_vocal"), bytes):
            vocal.write_bytes(result["melody_vocal"])
        if not instrumental.is_file() and isinstance(result, dict) and isinstance(result.get("melody_instrumental"), bytes):
            instrumental.write_bytes(result["melody_instrumental"])
        metadata_path = output_dir / "result.json"
        metadata: dict[str, Any] = {}
        if metadata_path.is_file():
            try:
                metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                metadata = {}
        duration = result.get("duration_seconds") if isinstance(result, dict) else None
        warnings = result.get("warnings", []) if isinstance(result, dict) else []
        if isinstance(warnings, str):
            warnings = [warnings]
        if _active.get(request_id, {}).get("cancelled"):
            raise InterruptedError("Transcription was cancelled.")
        keep_dir = True
        terminal(request_id, True, result={
            "jobId": job_dir.name, "jobDirectory": str(job_dir),
            "melodyVocalPath": str(vocal) if vocal.is_file() else None,
            "melodyInstrumentalPath": str(instrumental) if instrumental.is_file() else None,
            "durationSeconds": duration, "warnings": warnings,
            "metadata": metadata, "device": device, "elapsedSeconds": time.monotonic() - started,
        })
    except Exception as exc:
        was_cancelled = (isinstance(exc, InterruptedError) or
                         bool(_active.get(request_id, {}).get("cancelled")))
        code = "cancelled" if was_cancelled else "transcription_failed"
        message = "Transcription was cancelled." if was_cancelled else str(exc)
        terminal(request_id, False, error=error_payload(code, message, traceback.format_exc()))
    finally:
        if job_dir and not keep_dir:
            shutil.rmtree(job_dir, ignore_errors=True)


def accept(record: dict[str, Any]) -> None:
    global _last_request_id
    try:
        request_id = require_id(record)
    except ValueError as exc:
        emit({"type": "error", "requestId": record.get("requestId"),
              "error": error_payload("invalid_request", str(exc))})
        return
    with _state_lock:
        if request_id <= _last_request_id:
            emit({"type": "error", "requestId": request_id,
                  "error": error_payload("request_id_order", "requestId values must increase monotonically.")})
            return
        _last_request_id = request_id
        _active[request_id] = {"terminal": False, "cancelled": False,
                               "command": record.get("command")}

    command = record.get("command")
    if command == "hello":
        try:
            terminal(request_id, True, result={
                "protocolVersion": PROTOCOL_VERSION, "workerVersion": "1.0.0",
                "dataRoot": str(_paths.root), "modelsRoot": str(_paths.models),
                "runtimeRoot": str(_paths.runtime), "device": device_state(),
                "commands": ["prepare", "transcribe", "cancel", "allow_cpu", "cleanup", "shutdown"],
            })
        except Exception as exc:
            terminal(request_id, False, error=error_payload("worker_unavailable", str(exc)))
    elif command in {"prepare", "transcribe"}:
        with _state_lock:
            busy = any(not v.get("terminal") and v.get("command") in {"transcribe", "prepare"}
                       for k, v in _active.items()
                       if k != request_id and k > 0)
        if busy:
            terminal(request_id, False, error=error_payload(
                "worker_busy", "A prepare or transcription request is already active."))
        elif command == "prepare":
            _executor.submit(run_prepare, request_id)
        else:
            _executor.submit(run_transcribe, request_id, record)
    elif command == "allow_cpu":
        target = record.get("targetRequestId")
        with _state_lock:
            state = _active.get(target) if isinstance(target, int) else None
            if state is not None and state.get("waiting_cpu"):
                state["waiting_cpu"] = False
                pending = state.get("transcribe_record", {})
                pending["allowCpu"] = True
            else:
                pending = None
        if pending is None:
            terminal(request_id, False, error=error_payload("invalid_cpu_confirmation", "No pending CPU confirmation was found."))
        else:
            terminal(request_id, True, result={"accepted": True, "targetRequestId": target})
            _executor.submit(run_transcribe, target, pending)
    elif command == "cancel":
        target = record.get("targetRequestId")
        with _state_lock:
            target_state = _active.get(target) if isinstance(target, int) else None
            if target_state is not None:
                target_state["cancelled"] = True
        terminal(request_id, True, result={"cancelled": target_state is not None, "targetRequestId": target})
        if target_state is not None and target_state.get("waiting_cpu"):
            terminal(target, False, error=error_payload("cancelled", "Transcription was cancelled."))
    elif command == "cleanup":
        job_id = record.get("jobId")
        if not isinstance(job_id, str) or not re.fullmatch(r"audio-job-\d+-[a-zA-Z0-9_-]+", job_id):
            terminal(request_id, False, error=error_payload("invalid_job_id", "jobId is invalid."))
            return
        candidate = (_paths.jobs / job_id).resolve()
        if candidate.parent != _paths.jobs.resolve() or not candidate.is_dir():
            terminal(request_id, False, error=error_payload("job_not_found", "The local transcription job was not found."))
            return
        shutil.rmtree(candidate, ignore_errors=False)
        terminal(request_id, True, result={"cleaned": True, "jobId": job_id})
    elif command == "shutdown":
        terminal(request_id, True, result={"shuttingDown": True})
        pending_cpu: list[int] = []
        with _state_lock:
            for active_id, state in list(_active.items()):
                state["cancelled"] = True
                if state.get("waiting_cpu"):
                    pending_cpu.append(active_id)
        for active_id in pending_cpu:
            terminal(active_id, False, error=error_payload("cancelled", "Worker is shutting down."))
        _shutdown.set()
    else:
        terminal(request_id, False, error=error_payload("unknown_command", f"Unknown command: {command}"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True, help="Application data root under LocalAppData/NTE-GUITAR")
    parser.add_argument("--model-root", help="Optional model directory containing SheetSage2 and MERT-v2-FullSong snapshots")
    args = parser.parse_args()
    global _paths, _model_root
    _paths = AppPaths(Path(args.data_root))
    _model_root = Path(args.model_root).expanduser().resolve() if args.model_root else None
    _paths.ensure_directories()
    # SheetSage2 imports mir_eval (and SciPy's native extensions) from the
    # transcription executor. On Windows, that first native import can block
    # indefinitely in the worker thread. Initialize it on the protocol thread
    # before accepting work, while cancellation remains available afterward.
    import mir_eval  # noqa: F401
    emit({"type": "worker_started", "protocolVersion": PROTOCOL_VERSION, "pid": os.getpid()})
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError("Each input line must be a JSON object.")
            accept(value)
        except Exception as exc:
            emit({"type": "error", "requestId": None,
                  "error": error_payload("invalid_json", str(exc), traceback.format_exc())})
        if _shutdown.is_set():
            break
    _shutdown.set()
    # No queued jobs are accepted: prepare/transcribe are rejected while one is active.
    # Wait for the active job to observe cancellation and emit its single terminal record.
    _executor.shutdown(wait=True, cancel_futures=True)
    with _state_lock:
        unfinished = [request_id for request_id, state in _active.items() if not state.get("terminal")]
    for request_id in unfinished:
        terminal(request_id, False, error=error_payload("worker_shutdown", "Worker stopped before this request completed."))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
