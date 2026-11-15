using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]
// The Editor HTTP Monitor applies the capture policy to its display and JSON export (#1009).
[assembly: InternalsVisibleTo("TurboHTTP.Editor")]
[assembly: InternalsVisibleTo("TurboHTTP.Tests.Editor")]
