using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]

// The Unity bridge reports application resume to the keep-alive (WebSocketConnection.NotifyApplicationResumed, #1132).
[assembly: InternalsVisibleTo("TurboHTTP.Unity.WebSocket")]
