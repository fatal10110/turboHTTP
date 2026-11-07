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
// reflection-targets (#8), ws-json (#192), oauth-refresh (#67), json-coroutine-class/-struct (#95), and
// tls-sslstream / tls-bouncycastle (#141, #258 AC2). Every row but the TLS rows is offline: the HTTP rows
// use an in-process transport (TurboHttpSmokeRows.OfflineTransport). The TLS rows send one HTTPS request
// per provider to a public host with default certificate validation, only when the run asks for the
// network (Windows: the -turbohttpSmokeNetwork argument; Android: the turbohttp_smoke_network=1 intent
// extra that build-player.sh --network passes to am start); otherwise they are SKIP.
// QuickStartExample is compiled, never attached to a scene.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.Scripting;

public sealed class TurboHttpPlayerSmoke : MonoBehaviour
{
    public const string Marker = "TURBOHTTP_SMOKE";
    public const string NetworkArgument = "-turbohttpSmokeNetwork";
    public const string NetworkExtra = "turbohttp_smoke_network";

    // Bound per row: the offline rows finish in milliseconds; the TLS rows include DNS, TCP and a handshake.
    private const float RowTimeoutSeconds = 60f;

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

    private IEnumerator Start()
    {
        string summary = null;
        Row("registration", "#1", () => summary = TurboHttpSmokeRows.Registration());
        Row("reflection-targets", "#8", TurboHttpSmokeRows.ReflectionTargets);
        Row("ws-json", "#192", TurboHttpSmokeRows.WebSocketJson);
        yield return AsyncRow("oauth-refresh", "#67", TurboHttpSmokeRows.OAuthRefreshAsync);
        yield return JsonCoroutineRow<TurboHttpSmokeRows.ClassPayload>(
            "json-coroutine-class", TurboHttpSmokeRows.ClassPath, TurboHttpSmokeRows.VerifyClassPayload);
        yield return JsonCoroutineRow<TurboHttpSmokeRows.StructPayload>(
            "json-coroutine-struct", TurboHttpSmokeRows.StructPath, TurboHttpSmokeRows.VerifyStructPayload);

        if (!NetworkRequested())
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

        if (_failed.Count == 0 && summary != null)
            Debug.Log(Marker + " PASS " + summary + " rows=" + _passed + " skipped=" + _skipped);
        else
            Debug.Log(Marker + " FAIL rows=" + string.Join(",", _failed));

        Application.Quit(_failed.Count == 0 ? 0 : 1);
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
    private IEnumerator AsyncRow(string id, string issue, Func<CancellationToken, Task<string>> check)
    {
        var outcome = new Outcome();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RowTimeoutSeconds));
        _ = Capture(check, cts.Token, outcome);
        var deadline = Time.realtimeSinceStartup + RowTimeoutSeconds + 5f;
        while (!outcome.Done && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (!outcome.Done)
        {
            // The task still holds the token: the source is left to the process exit that follows.
            Fail(id, issue, "no result in " + (RowTimeoutSeconds + 5f) + " s");
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

    private static bool NetworkRequested()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg == NetworkArgument)
                return true;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                return intent.Call<string>("getStringExtra", NetworkExtra) == "1";
            }
        }
        catch (Exception)
        {
            // Extra not readable: the TLS rows are SKIP, and build-player.sh --network fails on the missing PASS rows.
            return false;
        }
#else
        return false;
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
