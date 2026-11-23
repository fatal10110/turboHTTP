using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]

// An app that uses only TurboHTTP.WebSocket (WebSocketClient, ResilientWebSocketClient) never references
// this assembly, so without this UnityLinker skips it entirely and the [RuntimeInitializeOnLoadMethod]
// WebSocketResumeListener.Install never runs: the keep-alive then gets no resume signal in stripped
// players (#1280, #1132).
[assembly: AlwaysLinkAssembly]
