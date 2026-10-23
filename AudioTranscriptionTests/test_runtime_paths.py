import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "AudioTranscription"))
import bootstrap


class RuntimePathTests(unittest.TestCase):
    def test_cpu_and_cuda_runtimes_use_distinct_directories(self):
        with tempfile.TemporaryDirectory() as temp:
            paths = bootstrap.AppPaths(Path(temp))

            cpu = bootstrap.runtime_venv_path(paths, "cpu")
            cuda = bootstrap.runtime_venv_path(paths, "cuda")

            self.assertEqual(cpu, Path(temp) / "audio-transcription" / "runtime" / "python312")
            self.assertEqual(cuda, Path(temp) / "audio-transcription" / "runtime" / "python312-cuda")
            self.assertNotEqual(cpu, cuda)


if __name__ == "__main__":
    unittest.main()
