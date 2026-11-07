// Player smoke for Tools~/unity/build-player.sh. The script copies this folder into the throwaway
// project's Assets/TurboHttpSmoke/ with no .asmdef, so it compiles into Assembly-CSharp: that proves
// the auto-referenced modules (Core, JSON, Retry, Auth, WebSocket) are visible to a plain Assets/
// script (#7 AC1; QuickStartExample.cs is the README Quick Start verbatim). At startup it builds a client the README Quick Start way and a WebSocket client
// WITHOUT calling any EnsureRegistered(), so it only passes when TurboHttpBootstrap registered the
// default transports by itself in a player (#1 AC1). It prints one marker line and quits:
//   TURBOHTTP_SMOKE PASS http=<transport type> ws=<transport type> json=ok bc=<present|absent> alpn=<true|false|n/a>
//   TURBOHTTP_SMOKE FAIL <exception>
// It never sends a request (no network); QuickStartExample is compiled, never attached to a scene.
using System;
using System.Collections.Generic;
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

        return "http=" + http + " ws=" + ws + " json=ok bc=" + bc + " alpn=" + ProbeSslStreamAlpn();
    }

    // SslStream ALPN probe (#303): the provider's type initializer builds the ALPN options
    // (SslClientAuthenticationOptions with a List<SslApplicationProtocol>) once; "true" means the
    // IL2CPP player has the AOT code and members for them, "false" means TLS runs without ALPN
    // (HTTP/1.1). The provider is internal, so the probe goes through reflection; "n/a" means the
    // type or member was not found. It says nothing about what a server negotiates (device row).
    private static string ProbeSslStreamAlpn()
    {
        var type = Type.GetType("TurboHTTP.Transport.Tls.SslStreamTlsProvider, TurboHTTP.Transport");
        var instance = type?.GetField("Instance")?.GetValue(null);
        var probe = type?.GetMethod("IsAlpnSupported", Type.EmptyTypes);
        if (instance == null || probe == null)
            return "n/a";
        return (bool)probe.Invoke(instance, null) ? "true" : "false";
    }
}
