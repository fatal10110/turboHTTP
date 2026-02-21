# Testing Module

TurboHTTP provides built-in isolation and mocking tools inside the `TurboHTTP.Testing` module, designed to facilitate robust unit and integration testing without network instability.

## Record / Replay Testing

Record/Replay mode allows you to execute your code against a live server once to record the HTTP traffic, and then subsequent test runs "replay" those exact responses offline instantly.

This guarantees deterministic testing.

```csharp
using TurboHTTP.Testing;

[Test]
public async Task FetchUser_ReturnsCorrectData()
{
    // MODE can be RecordReplayMode.Record or RecordReplayMode.Replay
    var transport = new RecordReplayTransport(
        new RawSocketTransport(),
        RecordReplayMode.Replay, 
        recordingFilePath: "Assets/Tests/Recordings/FetchUser.json"
    );

    var options = new UHttpClientOptions { Transport = transport };
    using var client = new UHttpClient(options);

    var response = await client.Get("https://api.example.com/user/1").SendAsync();
    Assert.IsTrue(response.IsSuccessStatusCode);

    // If in Record mode, save at the end of the test.
    if (transport.Mode == RecordReplayMode.Record)
    {
        transport.SaveRecordings();
    }
}
```

## Mock Server

The in-memory Mock Server intercepts outbound requests at the transport level (bypassing Sockets entirely) and serves pre-configured responses in microseconds.

```csharp
using TurboHTTP.Testing;

var mockServer = new MockHttpServer();

// Setup routing
mockServer.Setup(route => route
    .MatchMethod(HttpMethod.Post)
    .MatchPath("/login"))
    .Returns(new MockResponseBuilder()
        .WithStatusCode(HttpStatusCode.OK)
        .WithJsonBody(new { Token = "mock_token" }));

mockServer.Setup(route => route
    .MatchPath("/data"))
    .Returns(new MockResponseBuilder()
        .WithStatusCode(HttpStatusCode.InternalServerError));

// Assign the MockTransport to your client options
var options = new UHttpClientOptions { Transport = new MockTransport(mockServer) };
var client = new UHttpClient(options);
```
