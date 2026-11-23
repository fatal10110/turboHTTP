// Player smoke for Tools~/unity/build-player.sh. The script copies this folder into the throwaway
// project's Assets/TurboHttpSmoke/ with no .asmdef, so it compiles into Assembly-CSharp: that proves
// the auto-referenced modules (Core, JSON, Retry, Auth, Unity, WebSocket) are visible to a plain Assets/
// script (#7 AC1; QuickStartExample.cs is the README Quick Start verbatim). At startup it adds this
// component, which runs the rows of TurboHttpSmokeRows.cs one after another, WITHOUT calling any
// EnsureRegistered(), and prints one line per row and one aggregate line, then quits (exit code 0/1):
//   TURBOHTTP_SMOKE ROW <id> PASS|FAIL|SKIP <issue> <detail>
//   TURBOHTTP_SMOKE PASS http=<transport type> ws=<transport type> json=ok bc=<present|absent> alpn=<true|false|n/a> rows=<n> skipped=<n>
//   TURBOHTTP_SMOKE FAIL rows=<failed row ids>
// Rows: registration (#1: the bootstrap registered the default transports by itself in a player),
// reflection-targets (#8), ws-json (#192), net-config (#450: ServicePointManager's configuration init,
// which every SslStream handshake runs first, survives the stripping level), android-cleartext-policy (#875:
// on Android the Complete bootstrap installed the cleartext platform policy; SKIP elsewhere), oauth-refresh (#67), json-coroutine-class/-struct (#95), and
// tls-sslstream / tls-bouncycastle (#141, #258 AC2). Every row but the TLS rows is offline: the HTTP rows
// use an in-process transport (TurboHttpSmokeRows.OfflineTransport). The TLS rows send one HTTPS request
// per provider to a public host with default certificate validation, require a 200 (#450) and log the protocol an h2 host
// selects for that provider's ALPN offer (alpn=h2|http/1.1|none; FAIL when the provider can negotiate
// ALPN on this runtime and the selection is not h2, #413; Unity's SslStream cannot, #418), only when the
// run asks for the network (Windows: the -turbohttpSmokeNetwork argument; Android: the turbohttp_smoke_network=1 intent
// extra that build-player.sh --network passes to am start); otherwise they are SKIP.
// With the network, the HTTP/2-over-TLS rows h2-<download|upload|concurrent|stream-cancel>-<sslstream|bouncycastle>
// (#1210, H2Rows/TurboHttpH2Rows.cs) run against the h2-interop file server build-player.sh names (Windows: the
// -turbohttpSmokeH2Url <url> and -turbohttpSmokeH2Root <base64 DER> arguments; Android: the turbohttp_smoke_h2_url and
// turbohttp_smoke_h2_root intent extras). The BouncyCastle rows trust that root and require HTTP/2; the SslStream rows
// are SKIP with a root (SslStream trusts the platform store only) and run against a publicly trusted server without one.
// With the suspend option (the -turbohttpSmokeSuspend argument or TURBOHTTP_SMOKE_SUSPEND=1 in the environment, for the
// iOS procedure in testing.md; Android: the turbohttp_smoke_suspend=1 intent extra that build-player.sh --suspend passes) the ws-suspend-resume row (#1132, TurboHttpSuspendRow.cs) keeps a WebSocket with a
// short keep-alive open across the backgrounding the script (or, on iOS, the tester) does; otherwise it is SKIP.
// Every option is read once at startup: a relaunch that brings the app back may replace the Android intent.
// QuickStartExample is compiled, never attached to a scene.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.PlayerSmoke;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.Scripting;

public sealed class TurboHttpPlayerSmoke : MonoBehaviour
{
    public const string Marker = "TURBOHTTP_SMOKE";
    public const string NetworkArgument = "-turbohttpSmokeNetwork";
    public const string NetworkExtra = "turbohttp_smoke_network";
    public const string H2UrlArgument = "-turbohttpSmokeH2Url";
    public const string H2UrlExtra = "turbohttp_smoke_h2_url";
    public const string H2RootArgument = "-turbohttpSmokeH2Root";
    public const string H2RootExtra = "turbohttp_smoke_h2_root";
    public const string SuspendArgument = "-turbohttpSmokeSuspend";
    public const string SuspendExtra = "turbohttp_smoke_suspend";
    public const string SuspendEnvironment = "TURBOHTTP_SMOKE_SUSPEND";

    // Bound per row: the offline rows finish in milliseconds; the TLS rows include DNS, TCP and a handshake.
    private const float RowTimeoutSeconds = 60f;

    // The HTTP/2 rows move up to 4 MiB or 101 requests; each request has its own 60 s timeout.
    private const float H2RowTimeoutSeconds = 120f;

    // #1132: the suspend row waits up to 180 s for the backgrounding and resume, then about 10 s more.
    private const float SuspendRowTimeoutSeconds = 300f;

    private readonly List<string> _failed = new List<string>();
    private int _passed;
    private int _skipped;

    [Preserve]
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Run()
    {
        if (Application.isEditor)
            return;

        // One line per marker: without this every Debug.Log carries a stack trace in logcat and player.log.
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        var host = new GameObject("TurboHttpPlayerSmoke");
        DontDestroyOnLoad(host);
        host.AddComponent<TurboHttpPlayerSmoke>();
    }

    // #1132: the suspend row's view of the backgrounding (Unity calls this on the main thread).
    private void OnApplicationPause(bool paused)
    {
        TurboHttpSuspendRow.ApplicationPaused(paused);
    }

    private IEnumerator Start()
    {
        var network = Flag(NetworkArgument, NetworkExtra);
        var suspend = Flag(SuspendArgument, SuspendExtra) || Environment.GetEnvironmentVariable(SuspendEnvironment) == "1";
        var h2Url = network ? Option(H2UrlArgument, H2UrlExtra) : null;
        var h2Root = network ? Option(H2RootArgument, H2RootExtra) : null;
        string summary = null;
        Row("registration", "#1", () => summary = TurboHttpSmokeRows.Registration());
        Row("reflection-targets", "#8", TurboHttpSmokeRows.ReflectionTargets);
        Row("ws-json", "#192", TurboHttpSmokeRows.WebSocketJson);
        Row("net-config", "#450", TurboHttpSmokeRows.NetworkConfiguration);
#if UNITY_ANDROID && !UNITY_EDITOR
        Row("android-cleartext-policy", "#875", TurboHttpSmokeRows.AndroidCleartextPolicy);
#else
        Skip("android-cleartext-policy", "#875", "Android players only");
#endif
        yield return AsyncRow("oauth-refresh", "#67", TurboHttpSmokeRows.OAuthRefreshAsync);
        yield return JsonCoroutineRow<TurboHttpSmokeRows.ClassPayload>(
            "json-coroutine-class", TurboHttpSmokeRows.ClassPath, TurboHttpSmokeRows.VerifyClassPayload);
        yield return JsonCoroutineRow<TurboHttpSmokeRows.StructPayload>(
            "json-coroutine-struct", TurboHttpSmokeRows.StructPath, TurboHttpSmokeRows.VerifyStructPayload);

        if (!suspend)
        {
            Skip(TurboHttpSuspendRow.Id, TurboHttpSuspendRow.Issue,
                "suspend off (build-player.sh --suspend on android-il2cpp --run; iOS by hand, testing.md)");
        }
        else
        {
            yield return AsyncRow(TurboHttpSuspendRow.Id, TurboHttpSuspendRow.Issue,
                ct => TurboHttpSuspendRow.RunAsync(line => Debug.Log(line), ct), SuspendRowTimeoutSeconds);
        }

        if (!network)
        {
            Skip("tls-sslstream", "#141", "network off (build-player.sh --network)");
            Skip("tls-bouncycastle", "#258", "network off (build-player.sh --network)");
        }
        else
        {
            yield return AsyncRow("tls-sslstream", "#141",
                ct => TurboHttpSmokeRows.TlsRequestAsync(TlsBackend.SslStream, ct));
            if (TurboHttpSmokeRows.BouncyCastlePresent)
            {
                yield return AsyncRow("tls-bouncycastle", "#258",
                    ct => TurboHttpSmokeRows.TlsRequestAsync(TlsBackend.BouncyCastle, ct));
            }
            else
            {
                Skip("tls-bouncycastle", "#258", "BouncyCastle not in this build (build-player.sh --bouncycastle)");
            }
        }

        yield return H2Rows(network, h2Url, h2Root);

        if (_failed.Count == 0 && summary != null)
            Debug.Log(Marker + " PASS " + summary + " rows=" + _passed + " skipped=" + _skipped);
        else
            Debug.Log(Marker + " FAIL rows=" + string.Join(",", _failed));

        Application.Quit(_failed.Count == 0 ? 0 : 1);
    }

    // #1210: the four HTTP/2-over-TLS rows per TLS provider, one after another.
    private IEnumerator H2Rows(bool network, string url, string root)
    {
        foreach (var backend in new[] { TlsBackend.SslStream, TlsBackend.BouncyCastle })
        {
            string skip = null;
            if (!network)
                skip = "network off (build-player.sh --network)";
            else if (string.IsNullOrEmpty(url))
                skip = "no HTTP/2 row server (" + H2UrlArgument + " / " + H2UrlExtra + ")";
            else if (backend == TlsBackend.BouncyCastle && !TurboHttpSmokeRows.BouncyCastlePresent)
                skip = "BouncyCastle not in this build (build-player.sh --bouncycastle)";
            else if (backend == TlsBackend.SslStream && !string.IsNullOrEmpty(root))
                skip = "SslStream trusts the platform store only, not the row server's test root (needs a publicly trusted server)";

            foreach (var row in TurboHttpH2Rows.Rows)
            {
                var id = TurboHttpH2Rows.RowId(row, backend);
                if (skip != null)
                {
                    Skip(id, TurboHttpH2Rows.Issue, skip);
                    continue;
                }

                yield return AsyncRow(id, TurboHttpH2Rows.Issue,
                    ct => TurboHttpH2Rows.RunAsync(row, backend, url, root, ct), H2RowTimeoutSeconds);
            }
        }
    }

    private void Row(string id, string issue, Func<string> check)
    {
        try
        {
            Pass(id, issue, check());
        }
        catch (Exception ex)
        {
            Fail(id, issue, Describe(ex));
        }
    }

    // The row's task reports through Outcome; this coroutine owns it and waits on the main thread, bounded.
    private IEnumerator AsyncRow(
        string id,
        string issue,
        Func<CancellationToken, Task<string>> check,
        float timeoutSeconds = RowTimeoutSeconds)
    {
        var outcome = new Outcome();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        _ = Capture(check, cts.Token, outcome);
        var deadline = Time.realtimeSinceStartup + timeoutSeconds + 5f;
        while (!outcome.Done && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (!outcome.Done)
        {
            // The task still holds the token: the source is left to the process exit that follows.
            Fail(id, issue, "no result in " + (timeoutSeconds + 5f) + " s");
            yield break;
        }

        cts.Dispose();
        if (outcome.Error != null)
            Fail(id, issue, Describe(outcome.Error));
        else
            Pass(id, issue, outcome.Detail);
    }

    // Never throws: the row's result or exception lands in outcome, which AsyncRow polls.
    private static async Task Capture(
        Func<CancellationToken, Task<string>> check,
        CancellationToken ct,
        Outcome outcome)
    {
        try
        {
            outcome.Detail = await check(ct);
        }
        catch (Exception ex)
        {
            outcome.Error = ex;
        }
        finally
        {
            outcome.Done = true;
        }
    }

    // #95: GetJsonCoroutine<T> pumped by this MonoBehaviour, for a class and a struct T.
    private IEnumerator JsonCoroutineRow<T>(string id, string path, Func<T, string> verify)
    {
        const string issue = "#95";
        UHttpClient client = null;
        var done = false;
        var value = default(T);
        Exception error = null;
        try
        {
            client = TurboHttpSmokeRows.CreateOfflineClient();
            StartCoroutine(client.GetJsonCoroutine<T>(
                TurboHttpSmokeRows.OfflineBase + path,
                v =>
                {
                    value = v;
                    done = true;
                },
                e =>
                {
                    error = e;
                    done = true;
                },
                callbackOwner: this));
        }
        catch (Exception ex)
        {
            error = ex;
            done = true;
        }

        var deadline = Time.realtimeSinceStartup + RowTimeoutSeconds;
        while (!done && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (!done)
            Fail(id, issue, "no callback in " + RowTimeoutSeconds + " s");
        else if (error != null)
            Fail(id, issue, Describe(error));
        else
            Row(id, issue, () => verify(value));

        client?.Dispose();
    }

    private void Pass(string id, string issue, string detail)
    {
        _passed++;
        Debug.Log(Marker + " ROW " + id + " PASS " + issue + " " + detail);
    }

    private void Fail(string id, string issue, string detail)
    {
        _failed.Add(id);
        Debug.Log(Marker + " ROW " + id + " FAIL " + issue + " " + detail);
    }

    private void Skip(string id, string issue, string reason)
    {
        _skipped++;
        Debug.Log(Marker + " ROW " + id + " SKIP " + issue + " " + reason);
    }

    // Type and message of the exception and its inner exceptions on one line, bounded.
    private static string Describe(Exception ex)
    {
        var text = string.Empty;
        for (var e = ex; e != null; e = e.InnerException)
            text += (text.Length == 0 ? string.Empty : " <- ") + e.GetType().Name + ": " + e.Message;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= 400 ? text : text.Substring(0, 400) + "...";
    }

    // True for the <argument> switch on the command line, else (Android) for the string intent extra "1".
    private static bool Flag(string argument, string extra)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg == argument)
                return true;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                return intent.Call<string>("getStringExtra", extra) == "1";
            }
        }
        catch (Exception)
        {
            // Extra not readable: the rows are SKIP, and build-player.sh --network/--suspend fails on the missing PASS rows.
            return false;
        }
#else
        return false;
#endif
    }

    // The value of "<argument> <value>" on the command line, else (Android) the string intent extra, else null.
    private static string Option(string argument, string extra)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == argument)
                return args[i + 1];
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                return intent.Call<string>("getStringExtra", extra);
            }
        }
        catch (Exception)
        {
            // Extra not readable: the rows are SKIP, and build-player.sh --network fails on the missing PASS rows.
            return null;
        }
#else
        return null;
#endif
    }

    private sealed class Outcome
    {
        private volatile bool _done;

        public bool Done
        {
            get => _done;
            set => _done = value;
        }

        public string Detail { get; set; }

        public Exception Error { get; set; }
    }
}
