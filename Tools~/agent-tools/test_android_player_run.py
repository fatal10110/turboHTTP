"""Tests for build-player.sh --run on Android (#325) and the player smoke's link.xml list (#8 row).

Reuses the fakes of test_wsl_interop.py (stub cmd.exe, wslpath, Unity.exe; plan files make a tool fail N times)
with an adb.exe stub that also answers the calls --run makes: getprop, dumpsys window, am start, pidof (API 26+,
FAKE_ADB_SDK, default 31), ps (older levels: a header, the player unless FAKE_ADB_GONE, the end marker the script asks
for), logcat -d (which prints <state>.logcat, the player's log lines) and input keyevent KEYCODE_HOME (which appends
<state>.logcat.resume to that log once: what a backgrounded player logs after it resumes). --apk runs a prepared
APK without a build.
"""
import pathlib
import re
import unittest

import test_wsl_interop as wsl

PKG = "com.turbohttp.playersmoke"

ADB = wsl.PRELUDE + r"""echo "$*" >> "$FAKE_WSL.adb.calls"
[ "$(plan_next adb)" = ok ] || outage
case "$*" in
  *KEYCODE_HOME*)
    if [ -f "$FAKE_WSL.logcat.resume" ]; then
      cat "$FAKE_WSL.logcat.resume" >> "$FAKE_WSL.logcat"; rm -f "$FAKE_WSL.logcat.resume"
    fi ;;
  *" install "*|install*) printf 'Success\r\n' ;;
  *get-state*) printf 'device\r\n' ;;
  *"getprop ro.build.version.sdk"*) printf '%s\r\n' "${FAKE_ADB_SDK:-31}" ;;
  *getprop*) printf '%s\r\n' "${FAKE_ADB_ABI:-x86_64}" ;;
  *"dumpsys window"*) printf '    mShowingDream=false mDreamingLockscreen=%s\r\n' "${FAKE_ADB_LOCKED:-false}" ;;
  *"logcat -d"*) [ ! -f "$FAKE_WSL.logcat" ] || sed 's/$/\r/' "$FAKE_WSL.logcat" ;;
  *"am start"*) printf 'Starting: Intent { cmp=com.turbohttp.playersmoke/com.unity3d.player.UnityPlayerActivity }\r\nStatus: ok\r\n' ;;
  *"pidof "*) if [ -n "${FAKE_ADB_GONE:-}" ]; then printf 'none\r\n'; else printf '4242\r\n'; fi ;;
  *"ps -A"*)
    printf 'USER  PID  PPID VSZ RSS S NAME\r\n'
    [ -n "${FAKE_ADB_GONE:-}" ] || printf 'u0_a1  4242  1 0 0 S com.turbohttp.playersmoke\r\n'
    case "$*" in *"echo turbohttp-ps-end"*) printf 'turbohttp-ps-end\r\n' ;; esac ;;
esac
"""

UNITY_LINE = "11-08 10:00:00.000  4242  4260 I Unity   : "
PASS_LOG = [
    "TURBOHTTP_SMOKE ROW registration PASS #1 http=TurboHTTP.Transport.RawSocketTransport",
    "TURBOHTTP_SMOKE ROW oauth-refresh PASS #67 access_token=parsed",
    "TURBOHTTP_SMOKE ROW tls-sslstream SKIP #141 network off (build-player.sh --network)",
    "TURBOHTTP_SMOKE PASS http=TurboHTTP.Transport.RawSocketTransport ws=x json=ok bc=absent alpn=true rows=6 skipped=2",
]

# #1210: the HTTP/2-over-TLS rows --network requires per TLS provider when an h2 row server is in play. The tests
# name one with TURBOHTTP_H2_ROWS_URL/_ROOT (h2_server) so build-player.sh never starts Tools~/h2-interop/run.sh.
H2_ROWS = ("download", "upload", "concurrent", "stream-cancel")
H2_URL = "https://127.0.0.1:8443"
H2_ROOT_DER = b"\x30\x03\x02\x01\x01"
H2_ROOT_B64 = "MAMCAQE="


# #1132: the suspend row's ready line (a keep-alive ping in flight) and its result after the resume.
SUSPEND_READY = "TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready ping-in-flight pings=1 port=40000"
SUSPEND_PASS = ("TURBOHTTP_SMOKE ROW ws-suspend-resume PASS #1132 state=Open echo=ok pauses=1 resumes=1 "
                "background=10.2s ping-in-flight-at-pause=yes max-ping-gap=10.9s pings=9 grace-waits=1")
LAUNCHER_START = (f"shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER "
                  f"-n {PKG}/com.unity3d.player.UnityPlayerActivity")


def h2_log(provider, protocol="h2"):
    backend = "BouncyCastle" if provider == "bouncycastle" else "SslStream"
    return [f"TURBOHTTP_SMOKE ROW h2-{row}-{provider} PASS #1210 backend={backend} protocol={protocol} ms=1"
            for row in H2_ROWS]


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
        self.env.update(TURBOHTTP_SMOKE_POLL_S="0", TURBOHTTP_SMOKE_WAIT_S="20", TURBOHTTP_SMOKE_SUSPEND_S="0")
        for name in ("TURBOHTTP_H2_ROWS_URL", "TURBOHTTP_H2_ROWS_ROOT"):
            self.env.pop(name, None)

    def h2_server(self, url=H2_URL, root=True):
        """Env naming a running h2 row server (what Tools~/h2-interop/run.sh --serve prints) and its test root."""
        env = {"TURBOHTTP_H2_ROWS_URL": url}
        if root:
            path = self.root / "h2-root.der"
            path.write_bytes(H2_ROOT_DER)
            env["TURBOHTTP_H2_ROWS_ROOT"] = str(path)
        return env

    def logcat(self, lines):
        pathlib.Path(f"{self.state}.logcat").write_text("".join(UNITY_LINE + line + "\n" for line in lines))

    def resume_logcat(self, lines):
        pathlib.Path(f"{self.state}.logcat.resume").write_text("".join(UNITY_LINE + line + "\n" for line in lines))

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
        for sdk in ("31", "23"):  # pidof, then the ps listing of older levels
            with self.subTest(sdk=sdk):
                result = self.run_apk(FAKE_ADB_GONE="1", FAKE_ADB_SDK=sdk)
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

    def test_lock_screen_field_of_api_33_is_recognized(self):
        # API 33 (emulator image 13/TE1A.220922.034) prints "KeyguardShowing=<bool>" in dumpsys window.
        self.write_exe(self.unity.parent / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools"
                       / "adb.exe", ADB.replace("mShowingDream=false mDreamingLockscreen=%s", "KeyguardShowing=%s"))
        self.logcat(PASS_LOG)
        self.assertEqual(self.run_apk().returncode, 0)
        self.assertEqual(self.run_apk(FAKE_ADB_LOCKED="true").returncode, 2)

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

    def test_suspend_backgrounds_the_app_after_the_ready_line_and_requires_the_row_pass(self):
        # #1132: the row logs the ready line, then waits for the resume; its PASS is logged only after it.
        self.logcat(PASS_LOG[:2] + [SUSPEND_READY])
        self.resume_logcat([SUSPEND_PASS] + PASS_LOG[2:])
        result = self.run_apk("--suspend")
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, out)
        self.assertIn("TURBOHTTP_SMOKE ROW ws-suspend-resume PASS #1132", out)
        calls = self.calls("adb")
        starts = [i for i, c in enumerate(calls) if "am start" in c]
        self.assertEqual(len(starts), 2, calls)
        self.assertIn("--es turbohttp_smoke_suspend 1", calls[starts[0]])
        home = [i for i, c in enumerate(calls) if "KEYCODE_HOME" in c]
        self.assertEqual(home, [calls.index("shell input keyevent KEYCODE_HOME")], calls)
        self.assertLess(starts[0], home[0])
        self.assertLess(home[0], starts[1])
        self.assertEqual(calls[starts[1]], LAUNCHER_START)
        self.assertEqual(calls[-1], f"shell am force-stop {PKG}")

    def test_suspend_fails_without_the_row_pass_or_the_ready_line(self):
        cases = (
            # The row FAILed after the resume (the #1132 regression: PongTimeout).
            ([SUSPEND_READY], ["TURBOHTTP_SMOKE ROW ws-suspend-resume FAIL #1132 connection not Open after the resume "
                               "(#1132: closed with PongTimeout)", "TURBOHTTP_SMOKE FAIL rows=ws-suspend-resume"],
             "no 'TURBOHTTP_SMOKE ROW ws-suspend-resume PASS'"),
            # A player that never got the option: the row is SKIP and the aggregate PASS is not enough.
            ([], ["TURBOHTTP_SMOKE ROW ws-suspend-resume SKIP #1132 suspend off"] + PASS_LOG[2:],
             "so the app was never backgrounded"),
        )
        for before, after, message in cases:
            with self.subTest(message):
                calls = len(self.calls("adb"))
                if before:
                    self.logcat(PASS_LOG[:2] + before)
                    self.resume_logcat(after)
                else:
                    self.logcat(PASS_LOG[:2] + after)
                result = self.run_apk("--suspend")
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn(message, result.stdout)
                self.assertIn("no 'TURBOHTTP_SMOKE ROW ws-suspend-resume PASS'", result.stdout)
                home = [c for c in self.calls("adb")[calls:] if "KEYCODE_HOME" in c]
                self.assertEqual(len(home), 1 if before else 0)

    def test_bouncycastle_requires_the_provider_and_its_tls_row_with_network(self):
        self.logcat(PASS_LOG)
        result = self.run_apk("--bouncycastle")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("BouncyCastle provider not found", result.stdout)
        bc_log = PASS_LOG[:2] + ["TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 status=200",
                                 PASS_LOG[3].replace("bc=absent", "bc=present")]
        # --bouncycastle --network also requires the h2 rows (#1210); a given server keeps the run hermetic.
        h2 = self.h2_server()
        self.logcat(bc_log + h2_log("bouncycastle"))
        result = self.run_apk("--bouncycastle", "--network", **h2)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no 'TURBOHTTP_SMOKE ROW tls-bouncycastle PASS'", result.stdout)
        self.logcat(bc_log + ["TURBOHTTP_SMOKE ROW tls-bouncycastle PASS #258 status=200"] + h2_log("bouncycastle"))
        result = self.run_apk("--bouncycastle", "--network", **h2)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_bouncycastle_network_requires_the_h2_rows_over_http2(self):
        # #1210: with --bouncycastle the four h2-*-bouncycastle rows must PASS with protocol=h2; with a test root the
        # SslStream h2 rows are not required (SslStream trusts the platform store only).
        tls = (PASS_LOG[:2] + ["TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 status=200",
                               "TURBOHTTP_SMOKE ROW tls-bouncycastle PASS #258 status=200",
                               PASS_LOG[3].replace("bc=absent", "bc=present")])
        h2 = self.h2_server()
        self.logcat(tls)
        result = self.run_apk("--bouncycastle", "--network", **h2)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        for row in H2_ROWS:
            self.assertIn(f"no 'TURBOHTTP_SMOKE ROW h2-{row}-bouncycastle PASS'", result.stdout)
        self.assertNotIn("-sslstream", result.stdout.replace("tls-sslstream", ""))
        calls = self.calls("adb")
        [start] = [c for c in calls if "am start" in c]
        self.assertIn(f"--es turbohttp_smoke_h2_url {H2_URL}", start)
        self.assertIn(f"--es turbohttp_smoke_h2_root {H2_ROOT_B64}", start)
        # The loopback server is the host's: the device port is reversed to it for the run and removed after.
        self.assertIn("reverse tcp:8443 tcp:8443", calls)
        self.assertIn("reverse --remove tcp:8443", calls)
        self.assertLess(calls.index("reverse tcp:8443 tcp:8443"), calls.index(start))

        self.logcat(tls + h2_log("bouncycastle", protocol="http/1.1"))
        result = self.run_apk("--bouncycastle", "--network", **h2)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("h2-upload-bouncycastle did not run over HTTP/2", result.stdout)

        self.logcat(tls + h2_log("bouncycastle"))
        result = self.run_apk("--bouncycastle", "--network", **h2)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_h2_rows_without_a_test_root_require_sslstream_and_none_runs_without_a_server(self):
        tls = (PASS_LOG[:2] + ["TURBOHTTP_SMOKE ROW tls-sslstream PASS #141 status=200"] + PASS_LOG[3:])
        # A publicly trusted server (no root): the SslStream h2 rows must PASS; no adb reverse off loopback.
        public = self.h2_server(url="https://h2.example.test/", root=False)
        self.logcat(tls)
        result = self.run_apk("--network", **public)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no 'TURBOHTTP_SMOKE ROW h2-download-sslstream PASS'", result.stdout)
        self.logcat(tls + h2_log("sslstream"))
        result = self.run_apk("--network", **public)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls("adb")
        self.assertFalse([c for c in calls if "reverse" in c], calls)
        self.assertNotIn("turbohttp_smoke_h2_root", [c for c in calls if "am start" in c][-1])
        # No --bouncycastle and no server: no provider can trust a test root, so the rows are neither run nor required.
        self.logcat(tls)
        result = self.run_apk("--network")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("HTTP/2 rows not run", result.stdout)
        self.assertNotIn("turbohttp_smoke_h2_url", [c for c in self.calls("adb") if "am start" in c][-1])

    def test_h2_row_server_url_must_be_https(self):
        self.logcat(PASS_LOG)
        result = self.run_apk("--bouncycastle", "--network", **self.h2_server(url="http://127.0.0.1:8080", root=False))
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("must be an https URL", result.stderr)

    def test_usage_errors(self):
        for args in (["--target", "win64-mono", "--run"], ["--target", "android-il2cpp", "--arch", "armv7"],
                     ["--target", "android-il2cpp", "--network"], ["--target", "android-il2cpp", "--serial"],
                     ["--target", "android-il2cpp", "--suspend"], ["--target", "win64-il2cpp", "--suspend"],
                     ["--target", "win64-mono", "--network", "--suspend"],
                     ["--target", "android-il2cpp", "--apk", str(self.root / "missing.apk")],
                     ["--target", "win64-il2cpp", "--stripping", "Disabled"], ["--target", "win64-mono", "--stripping"],
                     ["--target", "win64-mono", "--stripping", "Minimal"],
                     ["--target", "android-il2cpp", "--stripping", "Low", "--apk", str(self.root / "missing.apk")]):
            with self.subTest(args):
                self.assertEqual(self.run_script("build-player.sh", *args).returncode, 2)
        self.assertEqual(self.calls("adb"), [])

    def test_stripping_reaches_the_build_and_names_a_non_default_variant(self):
        # #450: Mono builds at a chosen level; the fake Unity writes no player, so each run stops at the assembly check.
        (self.unity.parent / "Data" / "PlaybackEngines" / "windowsstandalonesupport" / "Variations" /
         "win64_player_nondevelopment_mono").mkdir(parents=True)
        work = self.root / "smoke"
        proj = work / "testproj"
        (proj / "ProjectSettings").mkdir(parents=True)
        (proj / "Packages").mkdir()
        (proj / "ProjectSettings" / "ProjectSettings.asset").write_text("PlayerSettings:\n  scriptingDefineSymbols: {}\n")
        (proj / "Packages" / "manifest.json").write_text("{}")
        for args, stripping, variant in ((["--stripping", "High"], "High", "win64-mono-high-nobc-"),
                                         ([], "Disabled", "win64-mono-nobc-"),
                                         (["--stripping", "Disabled"], "Disabled", "win64-mono-nobc-")):
            with self.subTest(args):
                before = len(self.calls("unity"))
                result = self.run_script("build-player.sh", "--target", "win64-mono", *args, UNITY_SMOKE_DIR=str(work))
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                build = [c for c in self.calls("unity")[before:] if "-executeMethod TurboHttpPlayerBuild.Build" in c]
                self.assertEqual(len(build), 1, self.calls("unity"))
                self.assertIn("-smokeStripping " + stripping + " ", build[0])
                self.assertIn(variant, build[0])

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
        for assembly, body in re.findall(r'<assembly fullname="([^"]+)"[^>]*>(.*?)</assembly>', text, flags=re.S):
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
        # Mono-only rules (#450: the configuration host IL2CPP's class library lacks) are their own list.
        everywhere, mono_only = self.smoke_list("LinkXmlTargets"), self.smoke_list("MonoLinkXmlTargets")
        self.assertEqual(everywhere | mono_only, expected)
        self.assertFalse(everywhere & mono_only)
        self.assertEqual(mono_only, {"System.Configuration.ExeConfigurationHost, System.Configuration"})
        self.assertEqual(self.smoke_list("BouncyCastleLinkXmlTargets"), self.link_xml_types(BC_LINK_XML))

    def test_every_runtime_link_xml_is_classified(self):
        # A new module's link.xml must be added to the smoke list or listed here as not in the smoke build.
        not_in_smoke = {"Testing", "Observability"}
        found = {str(p.parent.relative_to(REPO / "Runtime")) for p in (REPO / "Runtime").rglob("link.xml")
                 if "BouncyCastle/Lib" not in str(p)}
        self.assertEqual(found, set(SMOKE_LINK_XML) | {BC_LINK_XML} | not_in_smoke)


if __name__ == "__main__":
    unittest.main()
