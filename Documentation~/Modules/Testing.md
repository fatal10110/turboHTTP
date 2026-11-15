# Testing Module

The `TurboHTTP.Testing` module provides mocking and record/replay tools for tests that need no network. It is opt-in: reference the `TurboHTTP.Testing` assembly from your test assembly (it is not auto-referenced).

## MockTransport

`MockTransport` answers requests from memory. Queue responses with `EnqueueResponse`, `EnqueueJsonResponse` or `EnqueueError`, or give it a handler; inspect what the client sent with `LastRequest`, `CapturedRequests` and `RequestCount`.

<!-- snippet: with System.Net.HttpStatusCode created = System.Net.HttpStatusCode.Created; -->
```csharp
using TurboHTTP.JSON;
using TurboHTTP.Testing;

var transport = new MockTransport();
transport.EnqueueJsonResponse(new MyDto { id = 1, name = "Alice" });
transport.EnqueueResponse(System.Net.HttpStatusCode.ServiceUnavailable);

using var client = new UHttpClient(new UHttpClientOptions { Transport = transport });

using var first = await client.Get("https://api.example.com/users/1").SendBufferedAsync();
MyDto dto = first.AsJson<MyDto>(); // [Serializable] DTO, see APIReference.md#json-serializers

using var second = await client.Get("https://api.example.com/users/1").SendBufferedAsync();
// second.StatusCode == 503: error statuses are normal responses.
```

## Mock server

`MockHttpServer` matches requests by method and path and produces responses with `MockResponseBuilder`. `CreateTransport()` returns the `IHttpTransport` to give to the client.

<!-- snippet: with System.Net.HttpStatusCode ok = System.Net.HttpStatusCode.OK; -->
```csharp
using TurboHTTP.Testing;

var server = new MockHttpServer();

server.When(TurboHTTP.Core.HttpMethod.POST, "/login")
    .Respond(r => r.Status(System.Net.HttpStatusCode.OK)
        .Json(new System.Collections.Generic.Dictionary<string, object> { ["token"] = "mock_token" }));

server.When(TurboHTTP.Core.HttpMethod.GET, "/data")
    .Respond(r => r.Status(System.Net.HttpStatusCode.InternalServerError));

var options = new UHttpClientOptions { Transport = server.CreateTransport() };
using var client = new UHttpClient(options);

using var response = await client.Post("https://api.example.com/login").SendBufferedAsync();
server.AssertReceived("/login", 1);
```

`Json(...)` serializes with the registered JSON serializer: anonymous types are not supported, so use a `Dictionary<string, object>` or a `[Serializable]` class with public fields (see [JSON serializers](../APIReference.md#json-serializers)).

Further route options: `Times(n)`, `Priority(n)`, `WithHeader`, `WithQuery`, `WithBody`, `WithJsonBody<T>` predicates, `RespondSequence(...)` for a scripted series of responses, and `Delay(...)` on the response builder. `GetHistory()` lists the requests that arrived.

## Record / Replay

`RecordReplayTransport` wraps a real transport. In `Record` mode it forwards requests and saves the traffic to a file; in `Replay` mode it answers from that file and never touches the network, which makes tests deterministic. `Passthrough` forwards without recording. Sensitive headers, query parameters and JSON fields are redacted in the recording by default (`RecordReplayRedactionPolicy`).

<!-- snippet: with string path = "Assets/Tests/Recordings/FetchUser.json"; -->
```csharp
using TurboHTTP.Testing;
using TurboHTTP.Transport;

var transport = new RecordReplayTransport(new RawSocketTransport(), new RecordReplayTransportOptions
{
    Mode = RecordReplayMode.Replay,          // RecordReplayMode.Record the first time
    RecordingPath = path,
    MismatchPolicy = RecordReplayMismatchPolicy.Strict
});

using var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
using var response = await client.Get("https://api.example.com/user/1").SendBufferedAsync();

transport.SaveRecordings(); // in Record mode; also done on Dispose (AutoFlushOnDispose)
```

What a recording holds:

- **Compressed bodies.** Below `DecompressionInterceptor` the recorder sees the encoded bytes. A JSON or form body sent with `Content-Encoding: gzip`, `x-gzip` or `deflate` is decoded (bounded by `MaxRecordBytes`), redacted and stored without `Content-Encoding`, with `Content-Length` set to the stored length. A JSON or form body with any other coding (`br`, `zstd`, ...) cannot be redacted, so the recording fails and `SaveRecordings` throws: drop that coding from `Accept-Encoding` while recording. Other bodies are stored as received, coding included. `Content-Length` is rewritten whenever redaction changes a body.
- **Request matching.** The request-body part of the match key is an HMAC of the *redacted* body, so requests that differ only in redacted fields (a password, a token) match the same recording. The HMAC key is stored in the file so Replay can recompute keys; it hides nothing from someone holding the file. For the same reason, an `Authorization` or `Cookie` header opted into `MatchHeaderNames` is stored as an HMAC that a guess can be tested against: opt in only high-entropy values. Recordings written before this change (schema version 1, raw-body keys) still replay.
- **Size.** `MaxRecordBytes` (default 16 MB) limits each response; a larger one fails the request and the recording. The file has no total limit, and Replay loads any file Record wrote, including one above the 16 MiB limit that `LiteJsonSerializer` applies to network JSON.
- **JSON.** Recording files are written and read with the built-in `LiteJsonSerializer`, whatever `JsonSerializer.SetDefault` installed.

Use these tools in an NUnit test assembly (`TurboHTTP.Testing` referenced); the recording path is relative to the project.
