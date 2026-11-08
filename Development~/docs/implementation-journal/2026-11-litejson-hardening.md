# 2026-11 LiteJson hardening (#75)

Lane `litejson`, wave w1110. Issue #75 (v1.1): the engine-free `LiteJsonSerializer` (the default
without TurboHTTP.Complete, on WebGL and after `JsonSerializer.ResetToDefault()`) was lax and
failed POCOs with an unhelpful message.

## What changed

- **POCO / unsupported targets.** `LiteJsonSerializer.Deserialize` (string, `Type` and
  `ReadOnlySequence<byte>` overloads, so `AsJson<T>`) checks the target type before decoding or
  parsing and throws `NotSupportedException` for anything it can never produce (custom classes and
  structs, typed collections such as `List<int>` or `Dictionary<string, string>`). The message names
  TurboHTTP.Complete's `JsonUtility`-backed serializer, a System.Text.Json/Newtonsoft adapter via
  `JsonSerializer.SetDefault`, and the `Dictionary<string, object>` route. Supported targets: primitives,
  enums, string, decimal, `DateTime`, `DateTimeOffset`, `Guid`, `Uri`, their nullables, and any type
  assignable from `Dictionary<string, object>` or `List<object>` (`object`, `IDictionary<string, object>`,
  `IReadOnlyList<object>`, ...). `JsonWriter` throws the same `NotSupportedException` for a custom type
  (top-level or nested); `LiteJsonSerializer.Serialize` no longer rewraps it.
- **BOM.** `JsonReader.Parse` skips one leading U+FEFF (RFC 8259 s8.1); the byte path decodes
  `EF BB BF` to that char. A BOM anywhere else is still an error.
- **Timestamps.** `DateTime` targets parse with `DateTimeStyles.RoundtripKind`: `Z` gives
  `DateTimeKind.Utc` with the same instant, no zone gives `Unspecified`.
- **Strict grammar (RFC 8259 s2, s6, s7).** Whitespace is only SP/HT/LF/CR (was `char.IsWhiteSpace`);
  numbers dispatch on ASCII digits only, reject leading zeros, and need at least one digit in the int,
  fraction and exponent parts; raw chars below U+0020 inside strings throw; `\uXXXX` takes exactly four
  hex digits (the old `int.TryParse(HexNumber)` allowed surrounding white space).
- **Input size.** `LiteJsonSerializer.DefaultMaxInputBytes = 16 MiB`; `new LiteJsonSerializer(int
  maxInputBytes)` sets an immutable `MaxInputBytes`; `Instance` keeps the default. The byte path checks
  `input.Length` before copying/decoding; the string path counts UTF-8 bytes (only when
  `3 * chars` could exceed the cap). Over the cap → `JsonSerializationException` without an inner
  exception (the parser never ran).
- **64-bit integers.** Already exact (reader returns `long`, then `ulong`; writer uses invariant
  `ToString`); now covered by tests for ±2^53+1, `long.MinValue/MaxValue`, `ulong.MaxValue`.

## Files

- `Runtime/JSON/LiteJson/JsonReader.cs`, `LiteJsonSerializer.cs`, `JsonWriter.cs`
- `Runtime/JSON/JsonExtensions.cs` (XML docs: `NotSupportedException` on `AsJson`/`TryAsJson`)
- `Tests/Runtime/JSON/LiteJsonHardeningTests.cs` (new), `Tests/Runtime/JSON/JsonWriterTests.cs`
  (unsupported type now `NotSupportedException`)

## Decisions

- The issue's `JsonException` does not exist in this codebase; adding a public type with the
  System.Text.Json name would clash in user code. Grammar errors stay `JsonParseException` from
  `JsonReader` and `JsonSerializationException` (inner `JsonParseException`) from the serializer; the
  table tests assert both.
- `NotSupportedException` is thrown outside the serializer's filtered `try` (deserialize) or excluded
  from its filter (serialize), keeping one catch clause (IL2CPP #320).
- `TryAsJson` lets `NotSupportedException` propagate: an unmappable type is a configuration error,
  and returning `false` forever hid it. Projects with Complete are unaffected (`JsonUtilitySerializer`
  only delegates built-in shapes to LiteJson).
- `MaxInputBytes` is a constructor option (immutable, no shared mutable state) rather than a static
  setting; to change the limit used by `AsJson`, register `new LiteJsonSerializer(n)` with
  `JsonSerializer.SetDefault`.
- Under the Complete bootstrap, `JsonUtilitySerializer` routes built-in shapes and `T[]` through
  `LiteJsonSerializer.Instance` (default 16 MiB cap, not configurable there); its `JsonUtility` POCO path
  is not capped by this change.
- `TryAsJson` returns false for bad data but lets `NotSupportedException` (unmappable target type)
  propagate; pinned by `TryAsJson_Poco_PropagatesNotSupported_InvalidJsonReturnsFalse`.

## Deferred

- Opt-in `decimal` reading for fractions (issue's "decimal option", not an acceptance criterion): #394.

## Verification

- `'./Tools~/ci/check.sh'`: pass (invariants, format, shadow build with analyzers).
- Unity EditMode, scoped `--filter 'TurboHTTP.Tests.JSON'`: see the PR "Local gates" section.
- Not run: full Unity suites (periodic host-editor run), IL2CPP player, devices.
