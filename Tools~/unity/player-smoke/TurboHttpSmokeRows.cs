// The rows TurboHttpPlayerSmoke runs (see its header). Each row returns a one-line detail or throws; no
// row logs a token or credential. Compiled into Assembly-CSharp like the rest of player-smoke/, so it
// reaches public API only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Retry;
using TurboHTTP.Transport.Tls;
using TurboHTTP.WebSocket;
using UnityEngine.Scripting;

public static class TurboHttpSmokeRows
{
    // .invalid never resolves (RFC 6761): the offline rows cannot reach a network even by mistake.
    public const string OfflineBase = "https://smoke.turbohttp.invalid";
    public const string ClassPath = "/json/class";
    public const string StructPath = "/json/struct";
    public const string TokenPath = "/oauth/token";

    // The --network rows: one GET per TLS provider; any HTTP status proves the handshake (default validation).
    public const string NetworkUrl = "https://example.com/";

    // #413: a host that serves h2. HTTP/2 over TLS is selected only through ALPN (RFC 9113 section 3.2),
    // so each TLS row also offers [h2, http/1.1] here through the forced provider and logs the selection.
    public const string AlpnHost = "www.google.com";
    private static readonly string[] AlpnOffer = { "h2", "http/1.1" };

    private const string BouncyCastleProvider =
        "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle";

    // link.xml targets begin
    // Every <type> of the link.xml files whose assembly is in every smoke build (Runtime/Core, JSON, Transport,
    // Unity, WebSocket and WebSocket.Transport; Testing and Observability are not referenced, so their rules
    // never reach UnityLinker). test_android_player_run.py fails when a link.xml rule is missing here. The
    // names are looked up from this array, not from literals at the call site, so UnityLinker cannot keep a
    // type because of this smoke's own Type.GetType call: only the link.xml rule (#8) keeps it.
    private static readonly string[] LinkXmlTargets =
    {
        "TurboHTTP.Core.Internal.RequestBodyReadSession, TurboHTTP.Core",
        "TurboHTTP.Core.Internal.BufferedResponseBodySource, TurboHTTP.Core",
        "TurboHTTP.Core.IResponseBodySource, TurboHTTP.Core",
        "TurboHTTP.Core.UHttpStreamingResponse, TurboHTTP.Core",
        "System.IAsyncDisposable, mscorlib",
        "TurboHTTP.JSON.JsonExtensions, TurboHTTP.JSON",
        "TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON",
        "System.Net.Security.SslStream, System",
        "System.Net.Security.SslClientAuthenticationOptions, System",
        "System.Net.Security.SslApplicationProtocol, System",
        "System.Collections.Generic.List`1, mscorlib",
        "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1, mscorlib",
        "TurboHTTP.Transport.Http2.SingleReaderChannel`1, TurboHTTP.Transport",
        "TurboHTTP.Transport.Http2.Http2ResponseBodySource, TurboHTTP.Transport",
        "TurboHTTP.Transport.TransportModuleInitializer, TurboHTTP.Transport",
        "TurboHTTP.Transport.RawSocketTransport, TurboHTTP.Transport",
        "TurboHTTP.Unity.Decoders.IImageDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.IAudioDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.DecodedImage, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.DecodedAudio, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.DecoderRegistry, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.StbImageSharpDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.WavPcmDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.AiffPcmDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.NVorbisDecoder, TurboHTTP.Unity",
        "TurboHTTP.Unity.Decoders.NLayerMp3Decoder, TurboHTTP.Unity",
        "System.Security.Cryptography.SHA1, mscorlib",
        "System.Security.Cryptography.SHA1Managed, mscorlib",
        "System.Security.Cryptography.RandomNumberGenerator, mscorlib",
        "TurboHTTP.WebSocket.Transport.WebSocketTransportModuleInitializer, TurboHTTP.WebSocket.Transport",
        "TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport, TurboHTTP.WebSocket.Transport",
    };

    // Runtime/Transport/BouncyCastle/link.xml: checked only in a --bouncycastle build.
    private static readonly string[] BouncyCastleLinkXmlTargets =
    {
        "TurboHTTP.Transport.BouncyCastle.BouncyCastleModuleRegistration, TurboHTTP.Transport.BouncyCastle",
        "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
    };
    // link.xml targets end

    public static bool BouncyCastlePresent => Type.GetType(BouncyCastleProvider) != null;

    // #1: the README Quick Start shape plus the Retry and Auth modules. Default options resolve
    // HttpTransportFactory.Default in the constructor, which throws "No default transport configured"
    // unless the bootstrap registered the raw-socket transport; WebSocketTransportFactory.Create throws
    // "No WebSocket transport factory configured" unless it registered the WebSocket transport.
    public static string Registration()
    {
        var options = new UHttpClientOptions { BaseUrl = "https://api.example.com" };
        options.Interceptors.Add(new RetryInterceptor(new RetryPolicy()));
        options.Interceptors.Add(new AuthInterceptor(new StaticTokenProvider("smoke-token")));
        string http;
        using (var client = new UHttpClient(options))
        {
            http = HttpTransportFactory.Default.GetType().FullName;
        }

        string ws;
        using (var transport = WebSocketTransportFactory.Create())
        {
            ws = transport.GetType().FullName;
        }

        using (var wsClient = new WebSocketClient())
        {
        }

        // A dictionary goes through the built-in shapes of whichever serializer is the default.
        var data = new Dictionary<string, object> { ["name"] = "smoke" };
        var json = JsonSerializer.Serialize(data, typeof(Dictionary<string, object>));
        var back = JsonSerializer.Deserialize(json, typeof(Dictionary<string, object>)) as Dictionary<string, object>;
        if (back == null || !back.TryGetValue("name", out var name) || (string)name != "smoke")
            throw new InvalidOperationException("JSON round trip failed: " + json);

        // BouncyCastle is opt-in (TURBOHTTP_BOUNCYCASTLE): report whether its provider shipped.
        var bc = BouncyCastlePresent ? "present" : "absent";
        return "http=" + http + " ws=" + ws + " json=ok bc=" + bc + " alpn=" + ProbeSslStreamAlpn();
    }

    // #8: every type a TurboHTTP link.xml roots resolves by name in the stripped player and kept its members
    // (preserve="all").
    public static string ReflectionTargets()
    {
        var missing = new List<string>();
        var count = Resolve(LinkXmlTargets, missing);
        var bouncyCastle = BouncyCastlePresent;
        if (bouncyCastle)
            count += Resolve(BouncyCastleLinkXmlTargets, missing);
        if (missing.Count > 0)
            throw new InvalidOperationException("not in the player: " + string.Join("; ", missing));
        return "types=" + count + " (link.xml of Core, JSON, Transport, Unity, WebSocket, WebSocket.Transport" +
               (bouncyCastle ? ", BouncyCastle" : string.Empty) + ")";
    }

    private static int Resolve(string[] names, List<string> missing)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static;
        foreach (var name in names)
        {
            var type = Type.GetType(name, false);
            if (type == null)
                missing.Add(name);
            else if (type.GetMembers(all).Length == 0)
                missing.Add(name + " (no members)");
        }

        return names.Length;
    }

    // #192: JsonWebSocketSerializer<T> with a DTO holding a nested list and a dictionary, and a dictionary
    // payload with a nested list and dictionary. WebSocketMessage has no public constructor, so the receive
    // side makes the call JsonWebSocketSerializer<T>.Deserialize makes after reading the text frame: the
    // registered serializer (ProjectJsonBridge -> JsonSerializer.Default) for typeof(T).
    public static string WebSocketJson()
    {
        var serializer = new JsonWebSocketSerializer<ChatDto>();
        if (serializer.MessageType != WebSocketOpcode.Text)
            throw new InvalidOperationException("MessageType " + serializer.MessageType + ", expected Text");

        var dto = new ChatDto
        {
            room = "lobby",
            items = new List<ChatItem>
            {
                new ChatItem { id = "a", tags = new List<string> { "x", "y" } },
                new ChatItem { id = "b", tags = new List<string>() },
            },
            counts = new Dictionary<string, int> { ["a"] = 1 },
        };
        var json = Encoding.UTF8.GetString(serializer.Serialize(dto).ToArray());
        var back = JsonSerializer.Deserialize(json, typeof(ChatDto)) as ChatDto;
        if (back == null || back.room != "lobby" || back.items == null || back.items.Count != 2 ||
            back.items[0].id != "a" || back.items[0].tags == null || back.items[0].tags.Count != 2 ||
            back.items[0].tags[1] != "y" || back.items[1].id != "b")
        {
            throw new InvalidOperationException("DTO round trip failed: " + json);
        }

        // JsonUtility (the default TurboHTTP.Complete installs) documents dictionary fields as not serialized.
        var dictionaryField = back.counts == null ? "skipped" : "kept=" + back.counts.Count;

        var mapSerializer = new JsonWebSocketSerializer<Dictionary<string, object>>();
        var map = new Dictionary<string, object>
        {
            ["room"] = "lobby",
            ["items"] = new List<object> { new Dictionary<string, object> { ["id"] = "a" } },
            ["counts"] = new Dictionary<string, object> { ["a"] = 1L },
        };
        var mapJson = Encoding.UTF8.GetString(mapSerializer.Serialize(map).ToArray());
        var mapBack = JsonSerializer.Deserialize(mapJson, typeof(Dictionary<string, object>)) as Dictionary<string, object>;
        if (mapBack == null ||
            !(mapBack.TryGetValue("items", out var items) && items is List<object> list && list.Count == 1 &&
              list[0] is Dictionary<string, object> item && item.TryGetValue("id", out var id) && (string)id == "a") ||
            !(mapBack.TryGetValue("counts", out var counts) && counts is Dictionary<string, object> countMap &&
              countMap.TryGetValue("a", out var a) && Convert.ToInt64(a) == 1))
        {
            throw new InvalidOperationException("dictionary round trip failed: " + mapJson);
        }

        return "dto=ok nested-list=ok dictionary-field=" + dictionaryField + " dictionary-payload=ok";
    }

    // #67: OAuthClient.RefreshTokenAsync parses a token response (the parse that went through reflection
    // into TurboHTTP.JSON before) and keeps the rotated refresh token.
    public static async Task<string> OAuthRefreshAsync(CancellationToken ct)
    {
        using (var client = CreateOfflineClient())
        using (var oauth = new OAuthClient(client))
        {
            var token = await oauth.RefreshTokenAsync(new OAuthRefreshRequest
            {
                Config = new OAuthConfig
                {
                    ClientId = "smoke",
                    AuthorizationEndpoint = new Uri(OfflineBase + "/oauth/authorize"),
                    TokenEndpoint = new Uri(OfflineBase + TokenPath),
                    RedirectUri = new Uri("com.turbohttp.playersmoke:/oauth"),
                    Scopes = new[] { "read" },
                },
                RefreshToken = "smoke-refresh-1",
            }, ct);

            if (token.AccessToken != "smoke-access" || token.RefreshToken != "smoke-refresh-2" ||
                token.TokenType != "Bearer" || token.Scope != "read" ||
                token.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(30))
            {
                throw new InvalidOperationException("token response not parsed (token_type=" + token.TokenType +
                                                    " scope=" + token.Scope + " expires=" + token.ExpiresAtUtc.ToString("o") + ")");
            }

            return "access_token=parsed refresh_token=rotated token_type=Bearer scope=read expires_in=3600";
        }
    }

    public static string VerifyClassPayload(ClassPayload payload)
    {
        if (payload == null || payload.id != 1 || payload.name != "class" || payload.tags == null ||
            payload.tags.Count != 2 || payload.tags[1] != "b")
        {
            throw new InvalidOperationException("class payload not deserialized");
        }

        return "T=class id=1 tags=2";
    }

    public static string VerifyStructPayload(StructPayload payload)
    {
        if (payload.id != 2 || payload.name != "struct")
            throw new InvalidOperationException("struct payload not deserialized (id=" + payload.id + ")");
        return "T=struct id=2";
    }

    // --network: one request through the forced provider, then its ALPN selection against AlpnHost. A
    // certificate the platform rejects fails the row, and so does a provider that can negotiate ALPN on this
    // runtime but does not get h2 from an h2 host (an ALPN offer that was dropped, #413).
    public static async Task<string> TlsRequestAsync(TlsBackend backend, CancellationToken ct)
    {
        string request;
        using (var client = new UHttpClient(new UHttpClientOptions { TlsBackend = backend }))
        using (var response = await client.Get(NetworkUrl).SendBufferedAsync(ct))
        {
            request = "backend=" + backend + " host=" + new Uri(NetworkUrl).Host + " status=" + (int)response.StatusCode;
        }

        return request + " " + await NegotiateAlpnAsync(backend, ct);
    }

    // The protocol the server selected for the forced provider's [h2, http/1.1] offer: the public response
    // does not carry its HTTP version, so the row runs the provider's handshake itself and reads its result.
    // alpn-reported is the provider's claim, alpn-runtime whether this runtime can negotiate at all.
    private static async Task<string> NegotiateAlpnAsync(TlsBackend backend, CancellationToken ct)
    {
        var provider = TlsProviderSelector.GetProvider(backend);
        var reported = provider.IsAlpnSupported();
        var runtime = reported && (backend != TlsBackend.SslStream || SslStreamReportsAlpn());
        using (var tcp = await ConnectAsync(AlpnHost, 443, ct))
        {
            var result = await provider.WrapAsync(tcp.GetStream(), AlpnHost, AlpnOffer, ct);
            using (result.SecureStream)
            {
                var negotiated = result.NegotiatedAlpn ?? "none";
                var detail = "alpn-host=" + AlpnHost + " alpn-reported=" + (reported ? "true" : "false") +
                             " alpn-runtime=" + (runtime ? "true" : "false") + " alpn=" + negotiated;
                if (runtime && negotiated != "h2")
                    throw new InvalidOperationException(
                        provider.ProviderName + " can negotiate ALPN but " + AlpnHost + " selected " + negotiated +
                        " instead of h2 (" + detail + ")");
                if (reported && !runtime)
                    detail += " (#418: this runtime's SslStream has no ALPN, mono#12880)";
                return detail;
            }
        }
    }

    // One client per resolved address, in order, each in that address's family: a parameterless TcpClient is
    // IPv4-only on Mono, which an IPv6-only (NAT64) network cannot reach.
    private static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        Exception last = null;
        foreach (var address in await System.Net.Dns.GetHostAddressesAsync(host))
        {
            ct.ThrowIfCancellationRequested();
            var tcp = new TcpClient(address.AddressFamily);
            try
            {
                // ConnectAsync takes no token here: disposing the client on cancellation ends the connect.
                using (ct.Register(tcp.Dispose))
                {
                    await tcp.ConnectAsync(address, port);
                }

                return tcp;
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                tcp.Dispose();
                ct.ThrowIfCancellationRequested();
                last = ex;
            }
        }

        if (last == null)
            throw new SocketException((int)SocketError.HostNotFound);
        throw new InvalidOperationException("no address of " + host + " accepted a connection", last);
    }

    // Unity's Mono class library throws PlatformNotSupportedException from NegotiatedApplicationProtocol
    // (mono#12880) and ignores the ALPN options, although the provider's probe reports ALPN (#418).
    private static bool SslStreamReportsAlpn()
    {
        using (var sslStream = new SslStream(new MemoryStream()))
        {
            try
            {
                _ = sslStream.NegotiatedApplicationProtocol;
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // A runtime with ALPN may refuse the read before a handshake; it still has the API.
                return true;
            }
        }
    }

    public static UHttpClient CreateOfflineClient()
    {
        return new UHttpClient(new UHttpClientOptions { Transport = new OfflineTransport(), DisposeTransport = true });
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

    [Serializable]
    public sealed class ClassPayload
    {
        public int id;
        public string name;
        public List<string> tags;
    }

    [Serializable]
    public struct StructPayload
    {
        public int id;
        public string name;
    }

    [Serializable]
    public sealed class ChatItem
    {
        public string id;
        public List<string> tags;
    }

    [Serializable]
    public sealed class ChatDto
    {
        public string room;
        public List<ChatItem> items;
        public Dictionary<string, int> counts;
    }

    // Answers the offline rows in-process: no socket, no DNS. Unknown paths get a 404.
    [Preserve]
    private sealed class OfflineTransport : IHttpTransport
    {
        public Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handler.OnRequestStart(request, context);
            string body;
            switch (request.Uri.AbsolutePath)
            {
                case TokenPath:
                    body = "{\"access_token\":\"smoke-access\",\"token_type\":\"Bearer\",\"expires_in\":3600," +
                           "\"refresh_token\":\"smoke-refresh-2\",\"scope\":\"read\"}";
                    break;
                case ClassPath:
                    body = "{\"id\":1,\"name\":\"class\",\"tags\":[\"a\",\"b\"]}";
                    break;
                case StructPath:
                    body = "{\"id\":2,\"name\":\"struct\"}";
                    break;
                default:
                    body = null;
                    break;
            }

            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");
            var bytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
            return handler.OnResponseStartAsync(body == null ? 404 : 200, headers, new BufferedBody(bytes), context).AsTask();
        }

        public void Dispose()
        {
        }
    }

    private sealed class BufferedBody : IResponseBodySource
    {
        private readonly byte[] _data;
        private int _offset;

        public BufferedBody(byte[] data)
        {
            _data = data;
        }

        public long? Length => _data.Length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = _data;
            return true;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            body = default;
            return false;
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(destination.Length, _data.Length - _offset);
            if (count <= 0)
                return new ValueTask<int>(0);
            new ReadOnlyMemory<byte>(_data, _offset, count).CopyTo(destination);
            _offset += count;
            return new ValueTask<int>(count);
        }

        public ValueTask DrainAsync(CancellationToken ct)
        {
            _offset = _data.Length;
            return default;
        }

        public void Abort()
        {
            _offset = _data.Length;
        }

        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            return new ValueTask<HttpHeaders>(new HttpHeaders());
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }
}
