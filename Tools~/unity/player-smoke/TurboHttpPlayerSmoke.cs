// Player smoke for Tools~/unity/build-player.sh. The script copies this folder into the throwaway
// project's Assets/TurboHttpSmoke/ with no .asmdef, so it compiles into Assembly-CSharp: that proves
// the auto-referenced modules (Core, JSON, Retry, Auth, WebSocket) are visible to a plain Assets/
// script (#7 AC1). At startup it builds a client the README Quick Start way and a WebSocket client
// WITHOUT calling any EnsureRegistered(), so it only passes when TurboHttpBootstrap registered the
// default transports by itself in a player (#1 AC1). It prints one marker line and quits:
//   TURBOHTTP_SMOKE PASS http=<transport type> ws=<transport type> json=ok bc=<present|absent>
//   TURBOHTTP_SMOKE FAIL <exception>
// It never sends a request (no network); the Quick Start send path below is compiled, not run.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Retry;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.Scripting;

public static class TurboHttpPlayerSmoke
{
    public const string Marker = "TURBOHTTP_SMOKE";

    [Preserve]
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Run()
    {
        if (Application.isEditor)
            return;

        var exitCode = 0;
        try
        {
            Debug.Log(Marker + " PASS " + Check());
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Debug.Log(Marker + " FAIL " + ex.GetType().FullName + ": " + ex.Message);
        }

        Application.Quit(exitCode);
    }

    private static string Check()
    {
        // README Quick Start shape plus the Retry and Auth modules. Default options resolve
        // HttpTransportFactory.Default in the constructor, which throws "No default transport
        // configured" unless the bootstrap registered the raw-socket transport.
        var options = new UHttpClientOptions { BaseUrl = "https://api.example.com" };
        options.Interceptors.Add(new RetryInterceptor(new RetryPolicy()));
        options.Interceptors.Add(new AuthInterceptor(new StaticTokenProvider("smoke-token")));
        string http;
        using (var client = new UHttpClient(options))
        {
            http = HttpTransportFactory.Default.GetType().FullName;
        }

        // WebSocketTransportFactory.Create throws "No WebSocket transport factory configured"
        // unless the bootstrap registered the raw-socket WebSocket transport.
        string ws;
        using (var transport = WebSocketTransportFactory.Create())
        {
            ws = transport.GetType().FullName;
        }

        using (var wsClient = new WebSocketClient())
        {
        }

        // The built-in serializer handles dictionaries, lists and primitives (custom types need a
        // registered IJsonSerializer), so the round trip uses a dictionary.
        var data = new Dictionary<string, object> { ["name"] = "smoke" };
        var json = JsonSerializer.Serialize(data, typeof(Dictionary<string, object>));
        var back = JsonSerializer.Deserialize(json, typeof(Dictionary<string, object>)) as Dictionary<string, object>;
        if (back == null || !back.TryGetValue("name", out var name) || (string)name != "smoke")
            throw new InvalidOperationException("JSON round trip failed: " + json);

        // BouncyCastle is opt-in (TURBOHTTP_BOUNCYCASTLE): report whether its provider shipped.
        var bc = Type.GetType(
            "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle") != null
            ? "present"
            : "absent";

        return "http=" + http + " ws=" + ws + " json=ok bc=" + bc;
    }

    // Quick Start send path (README.md "Quick Start", with the current SendBufferedAsync: the
    // README still shows the removed SendAsync, #98): compiled into the player to prove the
    // extension methods resolve from Assembly-CSharp; never called by the smoke.
    public static async Task<Dictionary<string, object>> QuickStartGet(UHttpClient client)
    {
        using (var response = await client.Get("/data")
                   .WithBearerToken("token")
                   .WithTimeout(TimeSpan.FromSeconds(10))
                   .SendBufferedAsync())
        {
            return response.IsSuccessStatusCode ? response.AsJson<Dictionary<string, object>>() : null;
        }
    }
}
