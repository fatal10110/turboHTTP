# Third-Party Notices

TurboHTTP bundles the third-party components listed below. Each section names the files it covers
and reproduces the licence they are distributed under.

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

Copyright (c) 2026 fatal10110 and contributors

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
