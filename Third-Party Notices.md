# Third-Party Notices

TurboHTTP itself is distributed under the MIT License (`LICENSE.md`).

The package bundles one third-party component, Bouncy Castle for .NET (first section below); each
section names the files it covers and reproduces the licence it is distributed under. The sections
under "Not bundled" list libraries that TurboHTTP can use when the application installs them. No
source or binary of those libraries ships in this package, so no licence text of theirs is
reproduced here; their licences apply to the application that installs them.

## Bouncy Castle for .NET (bc-csharp)

Covers `Runtime/Transport/BouncyCastle/Lib/` (assembly `TurboHTTP.SecureProtocol`, namespace
`TurboHTTP.SecureProtocol.Org.BouncyCastle`): a reduced, namespace-rewritten copy of Bouncy Castle for
.NET, upstream https://github.com/bcgit/bc-csharp at commit `7c9c8d5a12b6342529936f08899cdb752280d538`,
produced by the strip at fatal10110/bc-csharp `4df803aac3927927fa2cb9c8c892b0291ba26518`. Local
modifications (the edge-cut list) are recorded in `Runtime/Transport/BouncyCastle/README.md`.

```text
========================================================================
Bouncy Castle for .NET (bc-csharp)
========================================================================

MIT License (https://opensource.org/licenses/MIT)

Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

**THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.**

========================================================================
Turbo-owned files (MIT License)
========================================================================

Lib/turbo/TurboKeyFactory.cs
Lib/turbo/TurboSignatureNames.cs

Copyright (c) 2026 fatal10110 and TurboHTTP contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

# Not bundled

Nothing in this part is part of the TurboHTTP package. It documents optional, user-installed
dependencies so that the package contents can be audited against it.

## Optional decoder libraries (adapters only)

`Runtime/Unity/Decoders/NLayerMp3Decoder.cs`, `NVorbisDecoder.cs` and `StbImageSharpDecoder.cs` are
TurboHTTP-owned adapter classes (MIT, like the rest of the package). They compile against the
libraries below only when the application installs one and defines the matching scripting symbol
(`TURBOHTTP_NLAYER`, `TURBOHTTP_NVORBIS`, `TURBOHTTP_STBIMAGESHARP`); otherwise they throw a
"provider not installed" error. `AiffPcmDecoder.cs` and `WavPcmDecoder.cs` are TurboHTTP-owned and use
no third-party code.

| Library | Used by | Upstream | Licence |
| --- | --- | --- | --- |
| NLayer (MP3 decoding) | `NLayerMp3Decoder` | https://github.com/naudio/NLayer | see the upstream repository |
| NVorbis (Ogg Vorbis decoding) | `NVorbisDecoder` | https://github.com/NVorbis/NVorbis | see the upstream repository |
| StbImageSharp (image decoding) | `StbImageSharpDecoder` | https://github.com/StbSharp/StbImageSharp | see the upstream repository |

## UniTask

`Runtime/UniTask/` (assembly `TurboHTTP.UniTask`) is TurboHTTP-owned glue that compiles only when the
application has the `com.cysharp.unitask` package (version 2.0.0 or newer; it defines
`TURBOHTTP_UNITASK`). UniTask is not bundled; install it from https://github.com/Cysharp/UniTask and
see its repository for its licence.

## Android background plugin build dependency

`Plugins/Android/TurboHttpBackground.androidlib/build.gradle` declares
`implementation 'androidx.work:work-runtime:2.7.1'` (AndroidX WorkManager, Apache License 2.0,
https://developer.android.com/jetpack/androidx/releases/work). Gradle resolves it from Google's Maven
repository when the application builds for Android with the experimental background plugin; no
WorkManager binary is part of this package.

## Other

The iOS plugins (`Plugins/iOS/`), `Runtime/JSON/LiteJson/` and the samples (`Samples~/`) are
TurboHTTP-owned and contain no third-party code. The Unity Editor and Unity engine assemblies are not
bundled.
