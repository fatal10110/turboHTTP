using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]

// The Unity bridge reports application resume to the keep-alive (WebSocketConnection.NotifyApplicationResumed, #1132).
[assembly: InternalsVisibleTo("TurboHTTP.Unity.WebSocket")]

// The parser fuzz harness (Tools~/fuzz, not shipped): the ws-frames target drives the internal frame reader and
// message assembler (#112, #1145).
[assembly: InternalsVisibleTo("TurboHTTP.Fuzz")]
