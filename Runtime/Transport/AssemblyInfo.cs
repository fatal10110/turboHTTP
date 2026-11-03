using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]
[assembly: InternalsVisibleTo("TurboHTTP.Transport.BouncyCastle")]
[assembly: InternalsVisibleTo("TurboHTTP.WebSocket.Transport")]
// The TLS interop harness (Tools~/tls-interop, not shipped): test trust anchors on Linux (#248).
[assembly: InternalsVisibleTo("TurboHTTP.TlsInterop")]
// The HTTP/2 adversarial-server and interop harness (Tools~/h2-interop, not shipped): raw frames and HPACK (#111).
[assembly: InternalsVisibleTo("TurboHTTP.H2Interop")]
// The parser fuzz harness (Tools~/fuzz, not shipped): HTTP/1.1, HPACK and HTTP/2 targets (#112).
[assembly: InternalsVisibleTo("TurboHTTP.Fuzz")]
// The soak and lossy-network harness (Tools~/soak, not shipped): pool counters (#114).
[assembly: InternalsVisibleTo("TurboHTTP.Soak")]
