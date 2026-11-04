"""Tests for Tools~/unity/emulator-windows.sh (#353) and the PROJECT_PATH guard of run-allocation-gate.sh (#352).

The Windows side is faked: stub powershell.exe/taskkill.exe on PATH read and edit a process table file, so
the stop and pre-boot paths run on any host with bash.
"""
import os
import pathlib
import re
import subprocess
import tempfile
import unittest

REPO = pathlib.Path(__file__).resolve().parents[1].parent
LIB = REPO / "Tools~" / "unity" / "emulator-windows.sh"
GATE = REPO / "Development~" / "scripts" / "phase19" / "run-allocation-gate.sh"

QEMU = '"C:\\sdk\\emulator\\qemu\\windows-x86_64\\qemu-system-x86_64-headless.exe"'
EMU = '"C:\\sdk\\emulator\\emulator.exe"'

POWERSHELL = """#!/usr/bin/env bash
state="$FAKE_WIN_STATE"
echo "$*" >> "$state.ps-calls"
if [ -f "$state.fail" ]; then
  n="$(cat "$state.fail")"
  if [ "$n" -gt 0 ]; then echo $((n - 1)) > "$state.fail"; exit 1; fi
fi
while IFS= read -r line; do printf '%s\\r\\n' "$line"; done < "$state"
printf 'end-of-list\\r\\n'
"""

# taskkill /PID <pid> /T /F: drops the pid and, for /T, every row whose third field names it as parent.
TASKKILL = """#!/usr/bin/env bash
state="$FAKE_WIN_STATE"
echo "$*" >> "$state.kills"
[ "$(cat "$state.immortal" 2>/dev/null)" = "$2" ] && exit 1
grep -v -e "^$2|" -e "|parent=$2\\$" "$state" > "$state.tmp" || true
mv "$state.tmp" "$state"
"""


class EmulatorWindowsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = pathlib.Path(self.tmp.name)
        bin_dir = root / "bin"
        bin_dir.mkdir()
        for name, body in (("powershell.exe", POWERSHELL), ("taskkill.exe", TASKKILL)):
            path = bin_dir / name
            path.write_text(body)
            path.chmod(0o755)
        self.state = root / "procs"
        self.env = dict(os.environ, PATH=f"{bin_dir}{os.pathsep}{os.environ['PATH']}", FAKE_WIN_STATE=str(self.state))

    def tearDown(self):
        self.tmp.cleanup()

    def procs(self, *rows):
        self.state.write_text("".join(f"{pid}|{cmd}\n" for pid, cmd in rows))

    def run_lib(self, call):
        script = f'set -euo pipefail; . "$1"; emu_poll_s=0; {call}'
        return subprocess.run(["bash", "-c", script, "_", str(LIB)], env=self.env,
                              capture_output=True, text=True, timeout=60)

    def kills(self):
        path = pathlib.Path(f"{self.state}.kills")
        return path.read_text().splitlines() if path.exists() else []

    def test_stop_kills_only_the_named_avd(self):
        self.procs(
            (33100, f"{EMU} -avd turbohttp-api33 -port 5560 -no-window -wipe-data"),
            (33104, f"{QEMU} -avd turbohttp-api33 -port 5560 -no-window -wipe-data|parent=33100"),
            (4242, f"{QEMU} -avd turbohttp-api3 -port 5562"),
            (5150, f"{QEMU} -avd Pixel_7_API_34 -port 5554"),
        )
        result = self.run_lib("win_stop_avd turbohttp-api33 2")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("/PID 33100 /T /F", self.kills())
        self.assertFalse(any("4242" in k or "5150" in k for k in self.kills()), self.kills())
        left = self.state.read_text()
        self.assertNotIn("turbohttp-api33 ", left)
        self.assertIn("4242|", left)
        self.assertIn("5150|", left)

    def test_stop_does_nothing_when_the_emulator_already_exited(self):
        self.procs((5150, f"{QEMU} -avd Pixel_7_API_34 -port 5554"))
        result = self.run_lib("win_stop_avd turbohttp-api33 15")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.kills(), [])

    def test_stop_reports_a_survivor(self):
        self.procs((33104, f"{QEMU} -avd turbohttp-api33 -port 5560"))
        pathlib.Path(f"{self.state}.immortal").write_text("33104")
        result = self.run_lib("win_stop_avd turbohttp-api33 0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("33104", result.stderr)

    def test_lookup_retries_a_failed_interop_call(self):
        self.procs((33104, f"{QEMU} -avd turbohttp-api33 -port 5560"))
        pathlib.Path(f"{self.state}.fail").write_text("7")
        result = self.run_lib("win_avd_pids turbohttp-api33")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.split(), ["33104"])

    def test_lookup_failure_is_not_read_as_no_process(self):
        self.procs((33104, f"{QEMU} -avd turbohttp-api33 -port 5560"))
        pathlib.Path(f"{self.state}.fail").write_text("99")
        result = self.run_lib("win_stop_avd turbohttp-api33 0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("cannot list Windows processes", result.stderr)
        self.assertEqual(self.kills(), [])

    def test_preboot_stops_a_leftover_of_this_script(self):
        self.procs(
            (33104, f"{QEMU} -avd turbohttp-api33 -port 5562 -wipe-data"),
            (2900, f"{QEMU} -avd turbohttp-api29 -port 5560"),
            (5150, f"{QEMU} -avd Pixel_7_API_34 -port 5554"),
        )
        result = self.run_lib("win_free_avd turbohttp-api33 5560")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(sorted(self.kills()), ["/PID 2900 /T /F", "/PID 33104 /T /F"])
        self.assertIn("5150|", self.state.read_text())

    def test_preboot_fails_naming_a_foreign_emulator_on_the_port(self):
        self.procs((5150, f"{QEMU} -avd Pixel_7_API_34 -port 5560"))
        result = self.run_lib("win_free_avd turbohttp-api33 5560")
        self.assertEqual(result.returncode, 1)
        self.assertIn("5150", result.stderr)
        self.assertEqual(self.kills(), [])

    def test_script_uses_the_helpers_on_the_windows_path(self):
        text = (REPO / "Tools~" / "unity" / "run-emulator-tests.sh").read_text()
        self.assertIn('win_free_avd "turbohttp-api$api" "$port"', text)
        self.assertIn('win_stop_avd "$avd" 15', text)
        self.assertIn('win_stop_avd "$a" 5', text.split("cleanup() {", 1)[1].split("\n}\n", 1)[0])

    def run_script_functions(self, call):
        """Runs the boot/stop_emulator/lane/cleanup functions of run-emulator-tests.sh with adb and the
        emulator stubbed: adbs and $emu append their arguments to <state>.adb and <state>.emu."""
        text = (REPO / "Tools~" / "unity" / "run-emulator-tests.sh").read_text()
        funcs = "".join(name + "() {" + text.split("\n" + name + "() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
                        for name in ("cleanup", "boot", "stop_emulator", "lane"))
        out = pathlib.Path(self.tmp.name) / "out"
        out.mkdir(exist_ok=True)
        script = (f'set -euo pipefail; . "$1"; emu_poll_s=0; win=1; out="$2"; repo=/nonexistent; pass=(); '
                  f'emu_pid=""; started=(); started_avds=(); lane_pids=(); '
                  f'adbs() {{ echo "$*" >> "$FAKE_WIN_STATE.adb"; }}; emu="$3"; {funcs}{call}')
        emu = pathlib.Path(self.tmp.name) / "bin" / "emulator.exe"
        emu.write_text('#!/usr/bin/env bash\necho "$*" >> "$FAKE_WIN_STATE.emu"\n')
        emu.chmod(0o755)
        result = subprocess.run(["bash", "-c", script, "_", str(LIB), str(out), str(emu)], env=self.env,
                                capture_output=True, text=True, timeout=60)
        adb = pathlib.Path(f"{self.state}.adb")
        return result, out, adb.read_text().splitlines() if adb.exists() else []

    def test_refused_preboot_sends_no_console_kill_to_the_foreign_emulator(self):
        self.procs((5150, f"{QEMU} -avd Pixel_7_API_34 -port 5560"))
        result, out, adb = self.run_script_functions(
            'started+=(emulator-5560); started_avds+=(turbohttp-api33); lane 33 5560 1; cleanup')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("5150", result.stderr)
        self.assertEqual((out / "api33.code").read_text().strip(), "2")
        self.assertEqual([c for c in adb if "emu kill" in c], [])
        self.assertFalse(pathlib.Path(f"{self.state}.emu").exists())
        self.assertEqual(self.kills(), [])
        self.assertIn("5150|", self.state.read_text())

    def test_cleanup_sends_a_console_kill_to_a_launched_serial(self):
        self.procs()
        result, out, adb = self.run_script_functions(
            ': > "$out/emulator-5560.launched"; started+=(emulator-5560 emulator-5562); cleanup')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([c for c in adb if "emu kill" in c], ["emulator-5560 emu kill"])


class AllocationGateTests(unittest.TestCase):
    def test_requires_project_path(self):
        env = {k: v for k, v in os.environ.items() if k != "PROJECT_PATH"}
        result = subprocess.run(["bash", str(GATE), "gate"], env=env, capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 2)
        self.assertIn("usage: PROJECT_PATH=", result.stderr)


class MachinePathTests(unittest.TestCase):
    """#352: no developer machine paths in the tooling, scripts or docs (the journal and reviews are history)."""

    def test_no_machine_paths(self):
        pattern = re.compile(r"/mnt/[a-z]/|/" + "Users/")
        hits = []
        for top in ("Tools~", "Development~/scripts", "Development~/docs"):
            for path in (REPO / top).rglob("*"):
                rel = path.relative_to(REPO).as_posix()
                if not path.is_file() or "/implementation-journal/" in rel or "/reviews/" in rel:
                    continue
                try:
                    text = path.read_text(encoding="utf-8")
                except (UnicodeDecodeError, OSError):
                    continue
                hits += [f"{rel}:{n}" for n, line in enumerate(text.splitlines(), 1) if pattern.search(line)]
        self.assertEqual(hits, [])


if __name__ == "__main__":
    unittest.main()
