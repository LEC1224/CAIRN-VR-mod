"""Check the standalone launch wrapper with a mock Proton command and Win64 child."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from test_launcher import FIXTURE, HERE, WIN_CSC


class FrameTests(unittest.TestCase):
    def test_standalone_chain(self):
        bash = Path(os.environ["ProgramFiles"]) / "Git/bin/bash.exe"
        with tempfile.TemporaryDirectory(prefix="Frame Test ") as temporary:
            stage = Path(temporary)
            subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            HERE / "build-frame.ps1", "-OutputDirectory", stage], check=True, capture_output=True)
            source = stage / "Game.cs"
            source.write_text(FIXTURE)
            subprocess.run([WIN_CSC, "/nologo", "/target:winexe", "/out:" + str(stage / "Cairn.exe"), source], check=True)
            proton = stage / "fake-proton.sh"
            proton.write_bytes(b'''#!/bin/bash
test "$1" = waitforexitandrun || exit 3
shift
printf '%s' "$WINEDLLOVERRIDES" > "$TEST_DIR/wine-override"
exec "$@"
''')
            env = dict(os.environ, TEST_DIR=stage.as_posix(), XDG_CONFIG_HOME=stage.as_posix(),
                       WINEDLLOVERRIDES="other=n", CAIRNVR_GAZE="1", CAIRNVR_REACH="1",
                       CAIRNVR_MARKERS="0", DISPLAY="", WAYLAND_DISPLAY="", MSYS2_ARG_CONV_EXCL="*")
            (stage / "cairn-vr").mkdir()
            (stage / "cairn-vr/frame-controls").write_text("point|lefthanded\n")
            for mode in ("--normal", "--vr", None):
                command = [bash, "cairn-frame.sh"] + ([mode] if mode else [])
                command.extend(["bash", "fake-proton.sh", "waitforexitandrun", "./Cairn.exe", "two words", ""])
                run = subprocess.run(command, cwd=stage, env=env, capture_output=True)
                self.assertEqual(run.returncode, 23, run.stderr)
                lines = (stage / "launch-result.txt").read_text().splitlines()
                vr = mode == "--vr"
                self.assertIn("CAIRN_VR=" + ("1" if vr else "0"), lines)
                self.assertEqual((stage / "wine-override").read_text(), "other=n;winhttp=n,b" if vr else "other=n")
                args = [x[4:] for x in lines if x.startswith("arg=")]
                self.assertEqual(args, ["two words", ""] + (["-force-d3d11", "--doorstop-enabled", "true"] if vr else []))
                if vr:
                    self.assertIn("XR_API_LAYER_PATH=" + str(stage / r"BepInEx\plugins\CairnVR\openxr"), lines)
                    self.assertIn("XR_ENABLE_API_LAYERS=XR_APILAYER_CAIRN_controls", lines)
                    self.assertIn("CAIRNVR_GAZE=1", lines)
                    self.assertIn("CAIRNVR_POINT=1", lines)
                    self.assertIn("CAIRNVR_LEFTHANDED=1", lines)


if __name__ == "__main__":
    unittest.main()
