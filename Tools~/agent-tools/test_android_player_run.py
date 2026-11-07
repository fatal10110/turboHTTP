"""Tests for build-player.sh --run on Android (#325) and the player smoke's link.xml list (#8 row).

Reuses the fakes of test_wsl_interop.py (stub cmd.exe, wslpath, Unity.exe; plan files make a tool fail N times)
with an adb.exe stub that also answers the calls --run makes: getprop, dumpsys window, am start, ps and
logcat -d (which prints <state>.logcat, the player's log lines). --apk runs a prepared APK without a build.
"""
import pathlib
import re
import unittest

import test_wsl_interop as wsl

PKG = "com.turbohttp.playersmoke"

ADB = wsl.PRELUDE + r"""echo "$*" >> "$FAKE_WSL.adb.calls"
[ "$(plan_next adb)" = ok ] || outage
case "$*" in
  *" install "*|install*) printf 'Success\r\n' ;;
  *get-state*) printf 'device\r\n' ;;
  *getprop*) printf '%s\r\n' "${FAKE_ADB_ABI:-x86_64}" ;;
  *"dumpsys window"*) printf '    mShowingDream=false mDreamingLockscreen=%s\r\n' "${FAKE_ADB_LOCKED:-false}" ;;
  *"logcat -d"*) [ ! -f "$FAKE_WSL.logcat" ] || sed 's/$/\r/' "$FAKE_WSL.logcat" ;;
  *"am start"*) printf 'Starting: Intent { cmp=com.turbohttp.playersmoke/com.unity3d.player.UnityPlayerActivity }\r\nStatus: ok\r\n' ;;
  *"ps -A"*) [ -n "${FAKE_ADB_GONE:-}" ] || printf 'u0_a1  4242  1 0 0 S com.turbohttp.playersmoke\r\n' ;;
esac
"""

UNITY_LINE = "11-08 10:00:00.000  4242  4260 I Unity   : "
PASS_LOG = [
    "TURBOHTTP_SMOKE ROW registration PASS #1 http=TurboHTTP.Transport.RawSocketTransport",
    "TURBOHTTP_SMOKE ROW oauth-refresh PASS #67 access_token=parsed",
    "TURBOHTTP_SMOKE ROW tls-sslstream SKIP #141 network off (build-player.sh --network)",
    "TURBOHTTP_SMOKE PASS http=TurboHTTP.Transport.RawSocketTransport ws=x json=ok bc=absent alpn=true rows=6 skipped=2",
]

REPO = wsl.REPO
SMOKE_ROWS = REPO / "Tools~" / "unity" / "player-smoke" / "TurboHttpSmokeRows.cs"
# The link.xml files whose assemblies every smoke build contains, and the one only --bouncycastle adds.
SMOKE_LINK_XML = ["Core", "JSON", "Transport", "Unity", "WebSocket", "WebSocket.Transport"]
BC_LINK_XML = "Transport/BouncyCastle"


class AndroidPlayerRunTests(unittest.TestCase):
    setUp_base = wsl.WslInteropTests.setUp
    tearDown = wsl.WslInteropTests.tearDown
    write_exe = staticmethod(wsl.WslInteropTests.write_exe)
    plan = wsl.WslInteropTests.plan
    calls = wsl.WslInteropTests.calls
    run_script = wsl.WslInteropTests.run_script

    def setUp(self):
        self.setUp_base()
        self.write_exe(self.unity.parent / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools"
                       / "adb.exe", ADB)
        self.apk = self.root / "builds" / "TurboHttpSmoke.apk"
        self.apk.parent.mkdir()
        self.apk.write_bytes(b"PK fake apk")
        self.env.update(TURBOHTTP_SMOKE_POLL_S="0", TURBOHTTP_SMOKE_WAIT_S="20")

    def logcat(self, lines):
        pathlib.Path(f"{self.state}.logcat").write_text("".join(UNITY_LINE + line + "\n" for line in lines))

    def run_apk(self, *args, **env):
        return self.run_script("build-player.sh", "--target", "android-il2cpp", "--apk", str(self.apk), *args, **env)

    def test_pass_marker_installs_launches_and_passes(self):
        self.logcat(PASS_LOG)
        result = self.run_apk()
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, out)
        self.assertIn("build-player: TURBOHTTP_SMOKE PASS", out)
        self.assertIn("TURBOHTTP_SMOKE ROW oauth-refresh PASS #67", out)
        calls = self.calls("adb")
        self.assertTrue(any(c.startswith("install -r ") and c.endswith("TurboHttpSmoke.apk") for c in calls), calls)
        start = [c for c in calls if "am start" in c]
        self.assertEqual(start, [f"shell am start -W -n {PKG}/com.unity3d.player.UnityPlayerActivity"])
        order = [next(i for i, c in enumerate(calls) if key in c)
                 for key in ("install -r", "am force-stop", "KEYCODE_WAKEUP", "dumpsys window", "logcat -c", "am start")]
        self.assertEqual(order, sorted(order), calls)
        self.assertEqual(calls[-1], f"shell am force-stop {PKG}")
        self.assertTrue(list(self.apk.parent.glob("logcat-*.txt")), "logcat not kept next to the APK")

    def test_fail_marker_exits_nonzero(self):
        self.logcat(["TURBOHTTP_SMOKE ROW oauth-refresh FAIL #67 InvalidOperationException: token response not parsed",
                     "TURBOHTTP_SMOKE FAIL rows=oauth-refresh"])
        result = self.run_apk()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("TURBOHTTP_SMOKE ROW oauth-refresh FAIL", result.stdout)
        self.assertIn("build-player: FAIL", result.stdout)

    def test_no_marker_from_a_player_that_is_gone_exits_nonzero(self):
        self.logcat(["TURBOHTTP_SMOKE ROW registration PASS #1 http=x"])
        result = self.run_apk(FAKE_ADB_GONE="1")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("exited without a marker", result.stdout)
        self.assertIn("no TURBOHTTP_SMOKE marker", result.stdout)

    def test_no_marker_within_the_bound_exits_nonzero(self):
        result = self.run_apk(TURBOHTTP_SMOKE_WAIT_S="0")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no marker within 0 s", result.stdout)

    def test_adb_outage_then_recovery_still_passes(self):
        # get-state and install hit an interop outage (adb.exe and the cmd.exe probe both fail), then answer.
        self.logcat(PASS_LOG)
        self.plan("adb", "fail fail ok ok fail ok")
        self.plan("cmd", "fail ok fail ok")
        result = self.run_apk()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_long_adb_outage_is_interop_unavailable(self):
        self.plan("adb", "fail*")
        self.plan("cmd", "fail*")
        wsl.WslInteropTests.assert_interop_exit(self, self.run_apk(), "build-player")

    def test_serial_selects_the_device_for_every_adb_call(self):
        self.logcat(PASS_LOG)
        result = self.run_apk("--serial", "emulator-5554")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls("adb")
        self.assertTrue(calls)
        for call in calls:
            self.assertTrue(call.startswith("-s emulator-5554 "), call)

    def test_lock_screen_is_a_setup_error_before_launch(self):
        self.logcat(PASS_LOG)
        result = self.run_apk(FAKE_ADB_LOCKED="true")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("shows its lock screen", result.stderr)
        self.assertFalse([c for c in self.calls("adb") if "am start" in c])

    def test_missing_device_is_a_setup_error(self):
        self.write_exe(self.unity.parent / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools"
                       / "adb.exe", wsl.PRELUDE + 'echo "$*" >> "$FAKE_WSL.adb.calls"; exit 1\n')
        result = self.run_apk()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("no Android device", result.stderr)

    def test_network_passes_the_intent_extra_and_requires_tls_pass_rows(self):
        self.logcat(PASS_LOG)
        result = self.run_apk("--network")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no 'TURBOHTTP_SMOKE ROW tls-sslstream PASS'", result.stdout)
        self.assertIn("--es turbohttp_smoke_network 1", [c for c in self.calls("adb") if "am start" in c][0])
        self.logcat(PASS_LOG[:2] + ["TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 backend=SslStream status=200"]
                    + PASS_LOG[3:])
        self.assertEqual(self.run_apk("--network").returncode, 0)

    def test_bouncycastle_requires_the_provider_and_its_tls_row_with_network(self):
        self.logcat(PASS_LOG)
        result = self.run_apk("--bouncycastle")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("BouncyCastle provider not found", result.stdout)
        bc_log = PASS_LOG[:2] + ["TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 status=200",
                                 PASS_LOG[3].replace("bc=absent", "bc=present")]
        self.logcat(bc_log)
        result = self.run_apk("--bouncycastle", "--network")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no 'TURBOHTTP_SMOKE ROW tls-bouncycastle PASS'", result.stdout)
        self.logcat(bc_log + ["TURBOHTTP_SMOKE ROW tls-bouncycastle PASS #258 status=200"])
        self.assertEqual(self.run_apk("--bouncycastle", "--network").returncode, 0)

    def test_usage_errors(self):
        for args in (["--target", "win64-mono", "--run"], ["--target", "android-il2cpp", "--arch", "armv7"],
                     ["--target", "android-il2cpp", "--network"], ["--target", "android-il2cpp", "--serial"],
                     ["--target", "android-il2cpp", "--apk", str(self.root / "missing.apk")]):
            with self.subTest(args):
                self.assertEqual(self.run_script("build-player.sh", *args).returncode, 2)
        self.assertEqual(self.calls("adb"), [])

    def test_run_builds_for_the_device_abi_before_launching(self):
        # A prepared project; the fake Unity writes its log but no APK, so the run stops at the assembly check.
        work = self.root / "smoke"
        proj = work / "testproj"
        (proj / "ProjectSettings").mkdir(parents=True)
        (proj / "Packages").mkdir()
        (proj / "ProjectSettings" / "ProjectSettings.asset").write_text("PlayerSettings:\n  scriptingDefineSymbols: {}\n")
        (proj / "Packages" / "manifest.json").write_text("{}")
        result = self.run_script("build-player.sh", "--target", "android-il2cpp", "--run", "--serial", "emulator-5554",
                                 UNITY_SMOKE_DIR=str(work), FAKE_ADB_ABI="x86_64")
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1, out)
        self.assertIn("device ABI x86_64: building --arch x86_64", out)
        build = [c for c in self.calls("unity") if "-executeMethod TurboHttpPlayerBuild.Build" in c]
        self.assertEqual(len(build), 1, self.calls("unity"))
        self.assertIn("-smokeArch x86_64", build[0])
        self.assertIn("android-il2cpp-x86_64-nobc-", build[0])
        self.assertFalse([c for c in self.calls("adb") if "install" in c])


class SmokeReflectionTargetTests(unittest.TestCase):
    """The #8 row checks every type the smoke build's link.xml files root: the list must not drift."""

    @staticmethod
    def link_xml_types(rel):
        text = (REPO / "Runtime" / rel / "link.xml").read_text()
        text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
        types = set()
        for assembly, body in re.findall(r'<assembly fullname="([^"]+)"\s*>(.*?)</assembly>', text, flags=re.S):
            for name in re.findall(r'<type fullname="([^"]+)"', body):
                types.add(f"{name}, {assembly}")
        return types

    @staticmethod
    def smoke_list(field):
        text = SMOKE_ROWS.read_text()
        block = text.split("// link.xml targets begin", 1)[1].split("// link.xml targets end", 1)[0]
        body = block.split(f"string[] {field} =", 1)[1].split("};", 1)[0]
        return set(re.findall(r'"([^"]+)"', body))

    def test_smoke_list_matches_the_link_xml_rules(self):
        expected = set().union(*(self.link_xml_types(rel) for rel in SMOKE_LINK_XML))
        self.assertEqual(self.smoke_list("LinkXmlTargets"), expected)
        self.assertEqual(self.smoke_list("BouncyCastleLinkXmlTargets"), self.link_xml_types(BC_LINK_XML))

    def test_every_runtime_link_xml_is_classified(self):
        # A new module's link.xml must be added to the smoke list or listed here as not in the smoke build.
        not_in_smoke = {"Testing", "Observability"}
        found = {str(p.parent.relative_to(REPO / "Runtime")) for p in (REPO / "Runtime").rglob("link.xml")
                 if "BouncyCastle/Lib" not in str(p)}
        self.assertEqual(found, set(SMOKE_LINK_XML) | {BC_LINK_XML} | not_in_smoke)


if __name__ == "__main__":
    unittest.main()
