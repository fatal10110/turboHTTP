using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]
[assembly: InternalsVisibleTo("TurboHTTP.Transport.BouncyCastle")]
[assembly: InternalsVisibleTo("TurboHTTP.WebSocket.Transport")]
// The TLS interop harness (Tools~/tls-interop, not shipped): test trust anchors on Linux (#248).
[assembly: InternalsVisibleTo("TurboHTTP.TlsInterop")]
