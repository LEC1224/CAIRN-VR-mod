"""Exercise mode selection against a fixture executable and a multi-game VDF."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from steam_options import updated


HERE = Path(__file__).resolve().parent
WIN_CSC = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
FIXTURE = r'''
using System;
using System.IO;
public class Game {
    public static int Main(string[] args) {
        using (var log = File.CreateText("launch-result.txt")) {
            foreach (var key in new[] {"CAIRN_VR", "DOORSTOP_DISABLE", "XR_API_LAYER_PATH",
                    "XR_ENABLE_API_LAYERS", "CAIRNVR_GAZE", "CAIRNVR_REACH", "CAIRNVR_MARKERS",
                    "CAIRNVR_POINT", "CAIRNVR_LEFTHANDED"}) {
                log.WriteLine(key + "=" + Environment.GetEnvironmentVariable(key));
            }
            foreach (var arg in args) log.WriteLine("arg=" + arg);
        }
        return 23;
    }
}'''


class LauncherTests(unittest.TestCase):
    def test_child_launch(self):
        with tempfile.TemporaryDirectory(prefix="Cairn VR test ") as temporary:
            stage = Path(temporary)
            launcher = stage / "CairnVRLauncher.exe"
            subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            HERE / "build.ps1", "-OutputDirectory", stage], check=True, capture_output=True)
            source = stage / "Game.cs"
            source.write_text(FIXTURE)
            game = stage / "Cairn.exe"
            subprocess.run([WIN_CSC, "/nologo", "/target:winexe", "/out:" + str(game), source], check=True)
            profile = stage / "CairnVR"
            profile.mkdir()
            (profile / "controls.ini").write_text("gaze=1\nreach=1\npoint=1\nlefthanded=1\nmarkers=1\n")
            env = dict(os.environ, LOCALAPPDATA=str(stage), DOORSTOP_DISABLE="test",
                       XR_ENABLE_API_LAYERS="testlayer", XR_API_LAYER_PATH="existing")
            arguments = ["", "folder with space\\", 'a"b', "-flag", "x&y", "a%b"]
            for mode in ["normal", "vr"]:
                result = subprocess.run([launcher, "--" + mode, game] + arguments, env=env, timeout=15)
                self.assertEqual(result.returncode, 23)
                lines = (stage / "launch-result.txt").read_text().splitlines()
                vr = mode == "vr"
                self.assertIn("CAIRN_VR=" + ("1" if vr else "0"), lines)
                self.assertIn("DOORSTOP_DISABLE=" + ("" if vr else "TRUE"), lines)
                expected = arguments + (["-force-d3d11"] if vr else []) + ["--doorstop-enabled", str(vr).lower()]
                self.assertEqual([x[4:] for x in lines if x.startswith("arg=")], expected)
                if vr:
                    self.assertIn("XR_ENABLE_API_LAYERS=XR_APILAYER_CAIRN_controls;testlayer", lines)
                    self.assertIn("CAIRNVR_GAZE=1", lines)
                    self.assertIn("CAIRNVR_REACH=0", lines)
                    self.assertIn("CAIRNVR_POINT=1", lines)
                    self.assertIn("CAIRNVR_LEFTHANDED=1", lines)
                    self.assertIn("CAIRNVR_MARKERS=1", lines)
                    self.assertIn("XR_API_LAYER_PATH=" + str(stage / r"BepInEx\plugins\CairnVR\openxr") + ";existing", lines)
                else:
                    self.assertIn("XR_ENABLE_API_LAYERS=testlayer", lines)
                    self.assertIn("XR_API_LAYER_PATH=existing", lines)

    def test_vdf_round_trip(self):
        original = (b'\xef\xbb\xbf"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" {\r\n'
                    b'\t"42" { "LaunchOptions" "-other" }\r\n'
                    b'\t"1588550"\r\n\t{\r\n\t\t"conf" { "nested" "}" }\r\n'
                    b'\t}\r\n} } } } }\r\n')
        executable = Path(r"C:/Program Files (x86)/Steam/steamapps/common/Cairn/CairnVRLauncher.exe")
        for field in (b'', b'\t\t"LaunchOptions"\t\t"-windowed"\r\n',
                      b'\t\t"LaunchOptions"\t\t"before %command% after"\r\n'):
            data = original.replace(b'\t\t"conf"', field + b'\t\t"conf"')
            installed = updated(data, executable, True)
            self.assertNotEqual(data, installed)
            self.assertEqual(installed, updated(installed, executable, True))
            self.assertEqual(data, updated(installed, executable, False))


if __name__ == "__main__":
    unittest.main()
