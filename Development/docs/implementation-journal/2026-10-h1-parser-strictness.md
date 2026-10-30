# 2026-10 — HTTP/1.1 response parser strictness (#39, #40, #41, #174)

## What

`Http11ResponseParser` (and the streaming trailer reader in `Http11ResponseBodySource`) now
reject response heads that RFC 9112 calls incomplete or invalid, instead of turning them into
normal responses.

- **#39 EOF in head/trailers (RFC 9112 §8, §7.1.2).** `BufferedStreamReader.ReadLineAsync` no
  longer returns `""` at EOF: it returns `null` when the stream ended before any byte of the
  line, and throws `IOException("Unexpected end of stream inside an HTTP line")` when it ended
  mid-line. The header loop and both trailer loops treat only `""` as the terminator; `null`
  throws `IOException` (mapped to `UHttpException(NetworkError)` by `RawSocketTransport` and
  `Http11ResponseBodySource`).
- **#40 Transfer-Encoding framing (RFC 9112 §6.1, §6.3).** `DetermineBodyKind` reads every
  `Transfer-Encoding` field line (`ClassifyTransferEncoding`, allocation-free), and:
  - frames as chunked only when the coding list is exactly `chunked` (ignoring `identity`
    members); any other coding, a final coding other than chunked, or chunked twice throws
    `NotSupportedException` (as `Transfer-Encoding: gzip` already did);
  - TE + Content-Length: TE wins, `Content-Length` is removed from the response headers, the
    `Http11DualFramingHeaders` timeline event is still recorded, and the connection is not
    reused (`faultyFraming` → `KeepAlive=false`);
  - TE in an HTTP/1.0 response: body read until close, `Content-Length` removed, never reused.
- **#41 field-line syntax (RFC 9112 §5.1, §5.2, §2.2; RFC 9110 §5.1).** New
  `ParseResponseHeaderLine` for the main head: rejects (FormatException) obs-fold / any
  whitespace-led line, whitespace between name and colon, a missing colon, an empty name and
  non-token names. Values keep the OWS trim.
- **#174 status-line version (RFC 9112 §2.3).** `ParseStatusLine` accepts exactly `HTTP/1.0`
  and `HTTP/1.1` (case-sensitive) and stores the interned constant; `IsKeepAlive` compares
  ordinally. Error messages no longer echo peer bytes.

## Files

- `Runtime/Transport/Http1/Http11ResponseParser.cs`
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs` (trailer EOF)
- `Tests/Runtime/Transport/Http11ResponseParserStrictnessTests.cs` (new, 34 cases)
- `Tests/Runtime/Transport/Http11ResponseParserTests.cs` (`gzip, chunked` now expects
  `NotSupportedException`)

## Decisions

- **obs-fold: reject**, not fold (RFC 9112 §5.2 allows either for responses; rejecting is the
  conservative option and also covers whitespace between the start-line and the first field,
  §2.2).
- **Lines without a colon are rejected** on the main head (previously silently skipped); a
  field line is `field-name ":" ...` and a skipped garbage line hides a desync.
- **Zero bytes before the status line** keeps `FormatException("Empty HTTP status line")`
  exactly: `RawSocketTransport.IsConnectionClosedBeforeStatusLine` matches on it (stale-retry
  decision). A partial status line at EOF is now an `IOException`.
- **`gzip, chunked` rejected** with `NotSupportedException`: the framing is valid, but transfer
  codings other than chunked are never decoded, so the old behavior handed undecoded gzip bytes
  to the caller. A non-chunked final coding is also rejected rather than read to close
  (RFC 9112 §6.3 item 4 would read still-encoded content until close).
- **`identity`** (RFC 2616, no longer registered) is still treated as absent so
  `TE: identity` + `Content-Length` frames by length (existing behavior), but the connection is
  closed afterwards because both headers were present.
- **HTTP/1.0 + TE** is delimited by connection close per the issue text ("treat framing as
  faulty", RFC 9112 §6.1), even if the coding is chunked; the TE header stays visible.
- **Trailers** keep the lenient skip for malformed/invalid lines (22b.3 "wire compatibility"
  decision, `Parse_ChunkedBody_MalformedTrailers_AreSkipped`); only EOF inside the trailer
  section is now an error.
- 22a.2 kept both TE and CL in the headers and only recorded an event; that is superseded:
  CL is dropped and the connection closed (#40 comment of 2026-10-30).

## Verification

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build with analyzers).
- The new fixture plus `Http11ResponseParserTests` and `Http11ResponseBodySourceTests` were
  compiled into an ad-hoc net8.0 NUnit project against the shadow-built `TurboHTTP.Core` /
  `TurboHTTP.Transport` DLLs: 104/104 pass. Against a Transport DLL built from `origin/main`,
  29 of the 34 new cases fail (the 5 passing are positive controls and the preserved
  "Empty HTTP status line" signal).
- The rest of `Tests/Runtime/Transport/Http1` ran the same way: every failure was a
  `UnityEngine.CoreModule` load error (no Unity runtime outside the Editor), none parser-related.
- **Not run:** Unity EditMode/PlayMode (CI only on this host), IL2CPP, devices.
