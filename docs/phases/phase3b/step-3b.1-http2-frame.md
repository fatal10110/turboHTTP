# Step 3B.1: HTTP/2 Frame Types and Constants

**File:** `Runtime/Transport/Http2/Http2Frame.cs`
**Depends on:** Nothing
**Spec:** RFC 7540 Sections 4, 6, 7, and 6.5.2

## Purpose

Define all HTTP/2 wire-level constants: frame types, frame flags, error codes, settings identifiers, the frame data structure, and protocol constants. This is the foundation every other HTTP/2 component depends on.

## Types to Implement

### 1. `Http2FrameType` (enum : byte)

All 10 frame types from RFC 7540 Section 6:

```csharp
public enum Http2FrameType : byte
{
    Data         = 0x0,
    Headers      = 0x1,
    Priority     = 0x2,
    RstStream    = 0x3,
    Settings     = 0x4,
    PushPromise  = 0x5,
    Ping         = 0x6,
    GoAway       = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9
}
```

### 2. `Http2FrameFlags` (enum : byte, [Flags])

```csharp
[Flags]
public enum Http2FrameFlags : byte
{
    None        = 0x0,
    EndStream   = 0x1,  // Valid on DATA, HEADERS
    Ack         = 0x1,  // Valid on SETTINGS, PING (same bit, different frame types)
    EndHeaders  = 0x4,  // Valid on HEADERS, CONTINUATION
    Padded      = 0x8,  // Valid on DATA, HEADERS
    HasPriority = 0x20  // Valid on HEADERS
}
```

**Note:** `EndStream` and `Ack` share the same bit value (0x1). This is correct per RFC 7540 — they apply to different frame types and are never ambiguous in context.

### 3. `Http2ErrorCode` (enum : uint)

All 14 error codes from RFC 7540 Section 7:

```csharp
public enum Http2ErrorCode : uint
{
    NoError            = 0x0,
    ProtocolError      = 0x1,
    InternalError      = 0x2,
    FlowControlError   = 0x3,
    SettingsTimeout    = 0x4,
    StreamClosed       = 0x5,
    FrameSizeError     = 0x6,
    RefusedStream      = 0x7,
    Cancel             = 0x8,
    CompressionError   = 0x9,
    ConnectError       = 0xa,
    EnhanceYourCalm    = 0xb,
    InadequateSecurity = 0xc,
    Http11Required     = 0xd
}
```

### 4. `Http2SettingId` (enum : ushort)

Six settings from RFC 7540 Section 6.5.2:

```csharp
public enum Http2SettingId : ushort
{
    HeaderTableSize      = 0x1,
    EnablePush           = 0x2,
    MaxConcurrentStreams  = 0x3,
    InitialWindowSize    = 0x4,
    MaxFrameSize         = 0x5,
    MaxHeaderListSize    = 0x6
}
```

### 5. `Http2Frame` (class)

Represents a single HTTP/2 frame (9-byte header + payload):

```csharp
public class Http2Frame
{
    /// <summary>Payload length (24-bit, max configurable via SETTINGS_MAX_FRAME_SIZE).</summary>
    public int Length { get; set; }

    /// <summary>Frame type byte.</summary>
    public Http2FrameType Type { get; set; }

    /// <summary>Frame flags byte.</summary>
    public Http2FrameFlags Flags { get; set; }

    /// <summary>Stream identifier (31-bit, high bit reserved and always masked to 0).</summary>
    public int StreamId { get; set; }

    /// <summary>Frame payload bytes. Empty array (not null) for zero-payload frames.</summary>
    public byte[] Payload { get; set; }

    public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) != 0;
}
```

**Design notes:**
- Mutable class (not struct): populated field-by-field by the codec, payload sizes vary.
- `Payload` is never null — use `Array.Empty<byte>()` for zero-payload frames.
- `StreamId` is always masked: `streamId & 0x7FFFFFFF` on read. The high bit (reserved) is discarded per RFC 7540 Section 4.1.

### 6. `Http2Constants` (static class)

```csharp
internal static class Http2Constants
{
    public const int FrameHeaderSize = 9;
    public const int DefaultMaxFrameSize = 16384;        // 2^14
    public const int MaxFrameSizeMin = 16384;             // SETTINGS_MAX_FRAME_SIZE minimum
    public const int MaxFrameSizeMax = 16777215;          // 2^24 - 1
    public const int DefaultInitialWindowSize = 65535;    // 2^16 - 1
    public const int MaxWindowSize = 2147483647;          // 2^31 - 1
    public const int DefaultHeaderTableSize = 4096;
    public const int SettingEntrySize = 6;                // 2 bytes ID + 4 bytes value

    /// <summary>
    /// The HTTP/2 connection preface: "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"
    /// Must be sent by the client before any frames. RFC 7540 Section 3.5.
    /// </summary>
    public static readonly byte[] ConnectionPreface = System.Text.Encoding.ASCII.GetBytes(
        "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
}
```

## Wire Format Reference

HTTP/2 frame header (9 bytes):
```
+-----------------------------------------------+
|                Length (24 bits)                |
+---------------+---------------+---------------+
|  Type (8)     |  Flags (8)    |
+-+-------------+---------------+------
|R|         Stream Identifier (31 bits)         |
+-+---------------------------------------------+
|                Frame Payload ...               |
+-----------------------------------------------+
```

- Length: 3 bytes, big-endian unsigned integer (max 2^24-1, but typically ≤ SETTINGS_MAX_FRAME_SIZE)
- Type: 1 byte
- Flags: 1 byte
- R: 1 reserved bit (always 0)
- Stream ID: 31 bits, big-endian. Stream 0 = connection-level frames.

## Namespace

`TurboHTTP.Transport.Http2` — all types in a single file for this step since they are closely related constants.

## Validation Criteria

- [ ] All enum values match RFC 7540 exactly
- [ ] `Http2Frame.HasFlag()` works correctly
- [ ] `Http2Constants.ConnectionPreface` is exactly 24 bytes: `50 52 49 20 2a 20 48 54 54 50 2f 32 2e 30 0d 0a 0d 0a 53 4d 0d 0a 0d 0a`
- [ ] All types are in `TurboHTTP.Transport.Http2` namespace
- [ ] No Unity engine references (assembly is `noEngineReferences: true`)
