"""Pinned model/runtime bootstrap helpers. Importing this module has no side effects."""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import re
import subprocess
import sys
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Callable


SHEETSAGE_REVISION = "398b22834dac7dd05e09b9c4e40a39fc479ec502"
MERT_REVISION = "d8ba1c745e733b3908ce6ad16ebeb17ac7600a42"
USER_AGENT = "NTE-GUITAR-AudioTranscription/1.0"
BLOCK_SIZE = 1024 * 1024


@dataclass(frozen=True)
class AppPaths:
    root: Path

    @property
    def models(self) -> Path:
        return self.root / "models"

    @property
    def runtime(self) -> Path:
        return self.root / "audio-transcription" / "runtime"

    @property
    def jobs(self) -> Path:
        return self.root / "audio-transcription" / "jobs"

    def ensure_directories(self) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        self.models.mkdir(parents=True, exist_ok=True)
        self.runtime.mkdir(parents=True, exist_ok=True)
        self.jobs.mkdir(parents=True, exist_ok=True)


def model_specs() -> list[dict[str, Any]]:
    manifest = Path(__file__).with_name("downloads.json")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    return data["models"]


def model_paths(paths: AppPaths | None, model_root: Path | None = None) -> tuple[Path, Path]:
    if paths is None:
        raise RuntimeError("Worker paths have not been initialized.")
    specs = {item["id"]: item for item in model_specs()}
    root = model_root if model_root is not None else paths.models
    sheetsage_root = root / "SheetSage2"
    mert_root = root / "MERT-v2-FullSong"
    sheetsage_revision = sheetsage_root / specs["sheetsage2"]["revision"]
    mert_revision = mert_root / specs["mert"]["revision"]
    return (sheetsage_revision if sheetsage_revision.is_dir() else sheetsage_root,
            mert_revision if mert_revision.is_dir() else mert_root)


def verify_model_snapshots(root: Path) -> None:
    """Verify user-selected local snapshots against the pinned manifest before loading remote code."""
    for spec in model_specs():
        model_dir = root / spec["directory"]
        revision_dir = model_dir / spec["revision"]
        if revision_dir.is_dir():
            model_dir = revision_dir
        for relative, expected in spec.get("pinnedFiles", {}).items():
            file_path = model_dir / relative
            if not file_path.is_file():
                raise FileNotFoundError(f"Pinned model file is missing: {file_path}")
            digest = hashlib.sha256()
            with file_path.open("rb") as stream:
                for block in iter(lambda: stream.read(BLOCK_SIZE), b""):
                    digest.update(block)
            if digest.hexdigest() != expected["sha256"]:
                raise RuntimeError(f"Pinned model file failed SHA-256 verification: {file_path}")


def verify_model_snapshots(root: Path) -> None:
    """Verify user-selected local snapshots against the pinned manifest before loading remote code."""
    for spec in model_specs():
        model_dir = root / spec["directory"]
        revision_dir = model_dir / spec["revision"]
        if revision_dir.is_dir():
            model_dir = revision_dir
        for relative, expected in spec.get("pinnedFiles", {}).items():
            file_path = model_dir / relative
            if not file_path.is_file():
                raise FileNotFoundError(f"Pinned model file is missing: {file_path}")
            digest = hashlib.sha256()
            with file_path.open("rb") as stream:
                for block in iter(lambda: stream.read(BLOCK_SIZE), b""):
                    digest.update(block)
            if digest.hexdigest() != expected["sha256"]:
                raise RuntimeError(f"Pinned model file failed SHA-256 verification: {file_path}")


def _progress(callback: Callable[..., None], stage: str, **fields: Any) -> None:
    callback(stage, **fields)


@contextmanager
def _exclusive_file_lock(lock_path: Path):
    """Serialize runtime installation across separate app/CLI processes."""
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a+b") as lock_file:
        lock_file.seek(0, os.SEEK_END)
        if lock_file.tell() == 0:
            lock_file.write(b"0")
            lock_file.flush()

        if os.name == "nt":
            import msvcrt

            waiting_since: float | None = None
            while True:
                try:
                    lock_file.seek(0)
                    msvcrt.locking(lock_file.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    if waiting_since is None:
                        waiting_since = time.monotonic()
                        print("Another NTE-GUITAR instance is preparing the Python runtime; waiting to reuse it.", flush=True)
                    time.sleep(1)
            try:
                yield
            finally:
                lock_file.seek(0)
                msvcrt.locking(lock_file.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl

            fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)


def _runtime_marker_is_valid(venv_dir: Path, ready_marker: Path, torch_index_url: str) -> bool:
    python = venv_dir / ("Scripts" if os.name == "nt" else "bin") / ("python.exe" if os.name == "nt" else "python")
    try:
        marker = json.loads(ready_marker.read_text(encoding="utf-8"))
        return (python.is_file() and marker.get("format") == 1
                and marker.get("python") == python.name
                and marker.get("torch") == "2.8.0"
                and marker.get("torchaudio") == "2.8.0"
                and marker.get("torchIndexUrl") == torch_index_url
                and subprocess.run(
                    [str(python), "-c",
                     "import sys, torch, torchaudio, transformers, pretty_midi; "
                     "assert sys.version_info[:2] == (3, 12); "
                     "assert torch.__version__.split('+')[0] == '2.8.0'; "
                     f"assert (torch.version.cuda is not None) == {torch_index_url.endswith(('cu126', 'cu128'))}; "
                     "assert torchaudio.__version__.split('+')[0] == '2.8.0'"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60, check=False,
                ).returncode == 0)
    except (AttributeError, OSError, TypeError, ValueError, subprocess.SubprocessError):
        return False


def _request_json(url: str) -> Any:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.loads(response.read().decode("utf-8"))


def _matches(path: str, patterns: list[str]) -> bool:
    # Model code and weights live at repo root. Do not fetch tests, renders, or other bundled assets.
    if "\\" in path or ":" in path or "\x00" in path:
        raise RuntimeError("Repository listing returned a path with Windows-special characters.")
    return ("/" not in path and
            any(fnmatch.fnmatchcase(PurePosixPath(path).name, pattern) for pattern in patterns))


def _safe_destination(destination_root: Path, repository_path: str) -> Path:
    if "\\" in repository_path or ":" in repository_path or "\x00" in repository_path:
        raise RuntimeError("Repository returned a path with Windows-special characters.")
    relative = PurePosixPath(repository_path)
    if relative.is_absolute() or ".." in relative.parts:
        raise RuntimeError("Repository returned an unsafe model path.")
    root = destination_root.resolve()
    target = root.joinpath(*relative.parts).resolve()
    if not target.is_relative_to(root):
        raise RuntimeError("Repository path resolves outside the model snapshot directory.")
    return target


def _modelscope_files(repo: str, revision: str) -> list[dict[str, Any]]:
    url = f"https://modelscope.cn/api/v1/models/{repo}/repo/files?Revision={urllib.parse.quote(revision)}&Recursive=true"
    root = _request_json(url)
    if not isinstance(root, dict) or root.get("Success") is False:
        raise RuntimeError("ModelScope repository listing failed: " + str(root.get("Message", "invalid response")))
    data = root.get("Data", root)
    if isinstance(data, dict):
        files = data.get("Files") or data.get("files") or data.get("FilesList") or data.get("filesList")
    else:
        files = data
    if not isinstance(files, list):
        raise RuntimeError("ModelScope listing did not contain a file list.")
    result = []
    for item in files:
        if not isinstance(item, dict):
            continue
        path = item.get("Path") or item.get("path") or item.get("Name") or item.get("name")
        if path and (item.get("Type") or item.get("type") or "file").lower() not in {"tree", "directory"}:
            result.append({"path": str(path), "size": item.get("Size", item.get("size")),
                           "sha256": item.get("Sha256") or item.get("sha256")})
    if not result:
        raise RuntimeError("ModelScope repository listing was empty.")
    return result


def _huggingface_files(repo: str, revision: str) -> list[dict[str, Any]]:
    url = f"https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=true"
    root = _request_json(url)
    if not isinstance(root, list):
        raise RuntimeError("Hugging Face repository listing did not return a file list.")
    result = []
    for item in root:
        if not isinstance(item, dict) or item.get("type") != "file":
            continue
        path = item.get("path")
        lfs = item.get("lfs") if isinstance(item.get("lfs"), dict) else {}
        result.append({"path": path, "size": item.get("size"),
                       "sha256": lfs.get("oid") if re.fullmatch(r"[a-fA-F0-9]{64}", str(lfs.get("oid", ""))) else None,
                       "gitOid": item.get("oid")})
    return result


def _listing(spec: dict[str, Any]) -> tuple[list[dict[str, Any]], str]:
    try:
        return _modelscope_files(spec["repo"], spec["revision"]), "modelscope"
    except Exception as modelscope_error:
        try:
            return _huggingface_files(spec["repo"], spec["revision"]), "huggingface"
        except Exception as huggingface_error:
            raise RuntimeError(f"Could not list {spec['repo']} at pinned revision. ModelScope: {modelscope_error}; Hugging Face: {huggingface_error}") from huggingface_error


def _download_url(source: str, repo: str, revision: str, path: str) -> str:
    if source == "modelscope":
        return (f"https://modelscope.cn/api/v1/models/{repo}/repo?Revision={urllib.parse.quote(revision)}"
                f"&FilePath={urllib.parse.quote(path, safe='')}")
    return f"https://huggingface.co/{repo}/resolve/{revision}/{urllib.parse.quote(path, safe='/')}"


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(BLOCK_SIZE):
            digest.update(block)
    return digest.hexdigest()


def _download(url: str, destination: Path, expected_size: int | None,
              progress: Callable[[int, int | None], None], cancelled: Callable[[], bool]) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".part")
    if not partial.resolve().is_relative_to(destination.parent.resolve()):
        raise RuntimeError("Partial download path resolves outside the model snapshot directory.")
    offset = partial.stat().st_size if partial.exists() else 0
    if expected_size is not None and offset == int(expected_size):
        partial.replace(destination)
        return
    headers = {"User-Agent": USER_AGENT}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    request = urllib.request.Request(url, headers=headers)
    try:
        response = urllib.request.urlopen(request, timeout=60)
    except urllib.error.HTTPError as exc:
        if offset and exc.code == 416:
            partial.unlink(missing_ok=True)
            return _download(url, destination, expected_size, progress, cancelled)
        raise
    with response:
        status = getattr(response, "status", 200)
        append = False
        if offset > 0 and status == 206:
            content_range = response.headers.get("Content-Range", "")
            match = re.match(r"bytes\s+(\d+)-\d+/(?:\d+|\*)", content_range, re.IGNORECASE)
            if match and int(match.group(1)) == offset:
                append = True
            else:
                response.close()
                partial.unlink(missing_ok=True)
                return _download(url, destination, expected_size, progress, cancelled)
        if offset and not append:
            offset = 0
        mode = "ab" if append else "wb"
        downloaded = offset
        with partial.open(mode) as stream:
            while True:
                if cancelled():
                    raise InterruptedError("Model preparation was cancelled.")
                block = response.read(BLOCK_SIZE)
                if not block:
                    break
                stream.write(block)
                downloaded += len(block)
                progress(downloaded, expected_size)
    if expected_size is not None and partial.stat().st_size != int(expected_size):
        raise IOError(f"Downloaded size mismatch for {destination.name}.")
    partial.replace(destination)


def ensure_models(paths: AppPaths, emit_progress: Callable[..., None], cancelled: Callable[[], bool]) -> None:
    paths.ensure_directories()
    for spec in model_specs():
        revision = spec["revision"]
        destination_root = paths.models / spec["directory"] / revision
        marker = destination_root / ".nte-verified.json"
        if marker.is_file():
            try:
                receipt = json.loads(marker.read_text(encoding="utf-8"))
                pinned = spec.get("pinnedFiles", {})
                pinned_files_valid = all(
                    (destination_root / Path(name)).is_file() and
                    _sha256(destination_root / Path(name)).lower() == metadata["sha256"].lower()
                    for name, metadata in pinned.items() if metadata.get("sha256")
                )
                cached_files_present = all(
                    (destination_root / Path(name)).is_file()
                    for name in receipt.get("sha256", {})
                )
                if (receipt.get("repo") == spec["repo"] and receipt.get("revision") == revision
                        and pinned_files_valid and cached_files_present):
                    _progress(emit_progress, "model_ready", model=spec["id"], revision=revision)
                    continue
            except (OSError, json.JSONDecodeError):
                pass
        files, source = _listing(spec)
        selected = [item for item in files if item.get("path") and _matches(item["path"], spec["include"])]
        required = {"config.json", "model.safetensors"} if spec["id"] == "sheetsage2" else {"config.json", "model.safetensors", "configuration_mert2.py", "modeling_mert2.py"}
        available = {item["path"] for item in selected}
        missing = required - available
        if missing:
            raise RuntimeError(f"Pinned {spec['id']} snapshot is missing required files: {', '.join(sorted(missing))}")
        pinned = spec.get("pinnedFiles", {})
        unpinned_code = [item["path"] for item in selected
                         if item["path"].endswith(".py") and not pinned.get(item["path"], {}).get("sha256")]
        if unpinned_code:
            raise RuntimeError(f"Executable Python files lack pinned SHA-256 values: {', '.join(sorted(unpinned_code))}")
        missing_files = [item for item in selected
                         if not _safe_destination(destination_root, item["path"]).is_file()
                         or (pinned.get(item["path"], {}).get("sha256") and
                             _sha256(_safe_destination(destination_root, item["path"])).lower() !=
                             str(pinned[item["path"]]["sha256"]).lower())]
        unknown_sizes = [item["path"] for item in missing_files if item.get("size") is None]
        if unknown_sizes:
            raise RuntimeError("Cannot verify free disk space because the model listing omitted file sizes: " +
                               ", ".join(unknown_sizes[:8]))
        required_bytes = sum(int(item["size"]) for item in missing_files) + 512 * 1024 * 1024
        free_bytes = shutil.disk_usage(paths.models).free
        _progress(emit_progress, "disk_check", model=spec["id"],
                  requiredBytes=required_bytes, availableBytes=free_bytes)
        if free_bytes < required_bytes:
            raise OSError(f"Not enough free disk space for {spec['id']}: "
                          f"need about {required_bytes / (1024 ** 3):.1f} GiB, "
                          f"available {free_bytes / (1024 ** 3):.1f} GiB.")
        _progress(emit_progress, "model_listing", model=spec["id"], fileCount=len(selected), source=source)
        verified: dict[str, str] = {}
        for index, item in enumerate(selected, start=1):
            if cancelled():
                raise InterruptedError("Model preparation was cancelled.")
            target = _safe_destination(destination_root, item["path"])
            expected = pinned.get(item["path"], {}).get("sha256") or item.get("sha256")
            if target.is_file() and (expected is None or _sha256(target).lower() == str(expected).lower()):
                actual = _sha256(target)
                verified[item["path"]] = actual
                continue
            _progress(emit_progress, "model_file", model=spec["id"], file=item["path"],
                      fileIndex=index, fileCount=len(selected), bytesDownloaded=0, bytesTotal=item.get("size"))
            download_started = time.monotonic()
            def on_bytes(done: int, total: int | None, name: str = item["path"]) -> None:
                elapsed = max(0.001, time.monotonic() - download_started)
                speed = done / elapsed
                _progress(emit_progress, "model_download", model=spec["id"], file=name,
                          bytesDownloaded=done, bytesTotal=total, bytesPerSecond=speed,
                          remainingSeconds=max(0, (total - done) / speed) if total else None)
            url = _download_url(source, spec["repo"], revision, item["path"])
            _download(url, target, int(item["size"]) if item.get("size") is not None else None,
                      on_bytes, cancelled)
            actual = _sha256(target)
            if expected and actual.lower() != str(expected).lower():
                target.unlink(missing_ok=True)
                raise IOError(f"SHA-256 verification failed for {spec['repo']}:{item['path']}.")
            verified[item["path"]] = actual
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text(json.dumps({"repo": spec["repo"], "revision": revision,
                                      "sha256": verified}, indent=2), encoding="utf-8")
        _progress(emit_progress, "model_ready", model=spec["id"], revision=revision)


def runtime_venv_path(paths: AppPaths, runtime_name: str = "cpu") -> Path:
    if runtime_name == "cpu":
        return paths.runtime / "python312"
    if runtime_name == "cuda":
        return paths.runtime / "python312-cuda"
    raise ValueError("runtime_name must be 'cpu' or 'cuda'")


def install_runtime(paths: AppPaths, python_exe: str, torch_index_url: str,
                    runtime_name: str = "cpu") -> Path:
    venv_dir = runtime_venv_path(paths, runtime_name)
    ready_marker = venv_dir / ".nte-runtime-ready.json"
    venv_dir.parent.mkdir(parents=True, exist_ok=True)
    with _exclusive_file_lock(venv_dir.parent / ".python312-install.lock"):
        # Another app instance may have completed setup while this process waited.
        if _runtime_marker_is_valid(venv_dir, ready_marker, torch_index_url):
            return venv_dir / ("Scripts" if os.name == "nt" else "bin") / ("python.exe" if os.name == "nt" else "python")

        # The lock prevents deleting a venv while another installer is using its Python.
        ready_marker.unlink(missing_ok=True)
        if venv_dir.exists():
            shutil.rmtree(venv_dir)
        minimum_free_bytes = 6 * 1024**3
        free_bytes = shutil.disk_usage(venv_dir.parent).free
        if free_bytes < minimum_free_bytes:
            raise RuntimeError(
                f"Runtime installation needs at least 6 GiB of free disk space; "
                f"only {free_bytes / 1024**3:.1f} GiB is available."
            )

        subprocess.run([python_exe, "-m", "venv", str(venv_dir)], check=True)
        scripts = venv_dir / ("Scripts" if os.name == "nt" else "bin")
        python = scripts / ("python.exe" if os.name == "nt" else "python")
        subprocess.run([str(python), "-m", "pip", "install", "--progress-bar", "raw", "--upgrade", "pip"], check=True)
        subprocess.run([str(python), "-m", "pip", "install", "--progress-bar", "raw", "--index-url", torch_index_url,
                        "torch==2.8.0", "torchaudio==2.8.0"], check=True)
        requirements = Path(__file__).with_name("requirements.txt")
        subprocess.run([str(python), "-m", "pip", "install", "--progress-bar", "raw", "-r", str(requirements)], check=True)

        # Import the runtime dependencies before marking this venv reusable. A failed or
        # interrupted install leaves no marker and will be rebuilt on the next attempt.
        subprocess.run([
            str(python), "-c",
            "import sys, torch, torchaudio, transformers, pretty_midi; "
            "assert sys.version_info[:2] == (3, 12); "
            "assert torch.__version__.split('+')[0] == '2.8.0'; "
            f"assert (torch.version.cuda is not None) == {torch_index_url.endswith(('cu126', 'cu128'))}; "
            "assert torchaudio.__version__.split('+')[0] == '2.8.0'",
        ], check=True)
        marker_tmp = ready_marker.with_suffix(ready_marker.suffix + ".tmp")
        marker_tmp.write_text(json.dumps({
            "format": 1,
            "python": python.name,
            "torch": "2.8.0",
            "torchaudio": "2.8.0",
            "torchIndexUrl": torch_index_url,
        }, indent=2), encoding="utf-8")
        marker_tmp.replace(ready_marker)
        return python


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--install-runtime", action="store_true")
    parser.add_argument("--python", default=sys.executable, help="Python 3.12 executable used to create venv")
    parser.add_argument("--torch-index-url", default="https://download.pytorch.org/whl/cpu",
                        help="PyTorch wheel index; default installs CPU wheels")
    parser.add_argument("--runtime-name", choices=("cpu", "cuda"), default="cpu",
                        help="Install into the CPU or optional CUDA runtime")
    parser.add_argument("--prepare-models", action="store_true")
    args = parser.parse_args()
    paths = AppPaths(args.data_root.expanduser().resolve())
    paths.ensure_directories()
    if args.install_runtime:
        completed = subprocess.run([args.python, "-c", "import sys; print(sys.version_info[:2])"],
                                    check=True, capture_output=True, text=True)
        if "(3, 12)" not in completed.stdout:
            parser.error("--python must point to Python 3.12")
        if not args.torch_index_url.startswith("https://"):
            parser.error("--torch-index-url must use HTTPS")
        print(install_runtime(paths, args.python, args.torch_index_url, args.runtime_name))
    if args.prepare_models:
        ensure_models(paths, lambda stage, **info: print(json.dumps({"stage": stage, **info}, ensure_ascii=False)),
                      lambda: False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
