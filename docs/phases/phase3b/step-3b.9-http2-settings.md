# Step 3B.9: HTTP/2 Connection Settings

**File:** `Runtime/Transport/Http2/Http2Settings.cs`
**Depends on:** Step 3B.1 (Http2SettingId, Http2ErrorCode)
**Spec:** RFC 7540 Section 6.5, 6.5.2

## Purpose

Manage HTTP/2 connection settings — both the local settings (what we send to the server) and remote settings (what the server sends to us). Settings are exchanged via SETTINGS frames and affect connection behavior such as max concurrent streams, flow control window sizes, and frame sizes.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class Http2Settings
    {
        public int HeaderTableSize { get; private set; }
        public bool EnablePush { get; private set; }
        public int MaxConcurrentStreams { get; private set; }
        public int InitialWindowSize { get; private set; }
        public int MaxFrameSize { get; private set; }
        public int MaxHeaderListSize { get; private set; }

        public Http2Settings();
        public void Apply(Http2SettingId id, uint value);
        public byte[] SerializeClientSettings();
        public static List<(Http2SettingId Id, uint Value)> ParsePayload(byte[] payload);
    }
}
```

## Default Values (RFC 7540 Section 6.5.2)

| Setting | Default | Notes |
|---------|---------|-------|
| HEADER_TABLE_SIZE | 4096 | Dynamic table size for HPACK |
| ENABLE_PUSH | 1 (true) | We always disable (send 0) |
| MAX_CONCURRENT_STREAMS | int.MaxValue | No limit until server sets one |
| INITIAL_WINDOW_SIZE | 65535 | 2^16-1, per-stream flow control |
| MAX_FRAME_SIZE | 16384 | 2^14, minimum allowed |
| MAX_HEADER_LIST_SIZE | int.MaxValue | No limit (advisory) |

## Method Details

### `Apply(Http2SettingId id, uint value)`

Apply a single setting received from the server. Validates per RFC 7540 Section 6.5.2:

```csharp
public void Apply(Http2SettingId id, uint value)
{
    switch (id)
    {
        case Http2SettingId.HeaderTableSize:
            HeaderTableSize = (int)value;
            break;

        case Http2SettingId.EnablePush:
            if (value > 1)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "ENABLE_PUSH must be 0 or 1");
            EnablePush = value == 1;
            break;

        case Http2SettingId.MaxConcurrentStreams:
            MaxConcurrentStreams = (int)value;
            break;

        case Http2SettingId.InitialWindowSize:
            if (value > 2147483647) // 2^31-1
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                    "INITIAL_WINDOW_SIZE exceeds maximum");
            InitialWindowSize = (int)value;
            break;

        case Http2SettingId.MaxFrameSize:
            if (value < 16384 || value > 16777215) // 2^14 to 2^24-1
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "MAX_FRAME_SIZE out of range");
            MaxFrameSize = (int)value;
            break;

        case Http2SettingId.MaxHeaderListSize:
            MaxHeaderListSize = (int)value;
            break;

        default:
            // Unknown settings MUST be ignored (RFC 7540 Section 6.5.2)
            break;
    }
}
```

### `SerializeClientSettings()`

Build the SETTINGS frame payload to send during connection initialization:

```csharp
public byte[] SerializeClientSettings()
{
    // We send these settings:
    // ENABLE_PUSH = 0 (reject server push)
    // MAX_CONCURRENT_STREAMS = 100 (reasonable default)
    // INITIAL_WINDOW_SIZE = 65535 (default, keep simple for Phase 3)
    var settings = new List<(Http2SettingId, uint)>
    {
        (Http2SettingId.EnablePush, 0),
        (Http2SettingId.MaxConcurrentStreams, 100),
    };

    byte[] payload = new byte[settings.Count * 6]; // 6 bytes per setting
    for (int i = 0; i < settings.Count; i++)
    {
        var (id, value) = settings[i];
        int offset = i * 6;
        // 2-byte ID (big-endian)
        payload[offset]     = (byte)(((ushort)id >> 8) & 0xFF);
        payload[offset + 1] = (byte)((ushort)id & 0xFF);
        // 4-byte value (big-endian)
        payload[offset + 2] = (byte)((value >> 24) & 0xFF);
        payload[offset + 3] = (byte)((value >> 16) & 0xFF);
        payload[offset + 4] = (byte)((value >> 8) & 0xFF);
        payload[offset + 5] = (byte)(value & 0xFF);
    }
    return payload;
}
```

### `ParsePayload(byte[] payload)`

Parse a SETTINGS frame payload into individual settings:

```csharp
public static List<(Http2SettingId Id, uint Value)> ParsePayload(byte[] payload)
{
    if (payload.Length % 6 != 0)
        throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
            "SETTINGS payload length must be a multiple of 6");

    var result = new List<(Http2SettingId, uint)>(payload.Length / 6);
    for (int i = 0; i < payload.Length; i += 6)
    {
        var id = (Http2SettingId)((payload[i] << 8) | payload[i + 1]);
        uint value = ((uint)payload[i + 2] << 24) | ((uint)payload[i + 3] << 16) |
                     ((uint)payload[i + 4] << 8) | payload[i + 5];
        result.Add((id, value));
    }
    return result;
}
```

## SETTINGS Frame Wire Format

Each setting is 6 bytes:
```
+-------------------------------+
| Identifier (16 bits)          |
+-------------------------------+-------------------------------+
| Value (32 bits)                                               |
+---------------------------------------------------------------+
```

A SETTINGS frame may contain 0 or more settings. The payload length must be a multiple of 6.

## SETTINGS ACK

A SETTINGS frame with the ACK flag (0x1) and empty payload acknowledges receipt of the peer's SETTINGS. If a SETTINGS ACK has a non-empty payload, that's a FRAME_SIZE_ERROR.

## Http2ProtocolException

A new exception class for HTTP/2 protocol violations:

```csharp
internal class Http2ProtocolException : Exception
{
    public Http2ErrorCode ErrorCode { get; }

    public Http2ProtocolException(Http2ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
```

This is used by `Http2Connection` to determine the error code for GOAWAY/RST_STREAM responses.

## INITIAL_WINDOW_SIZE Change Impact

When the server changes `INITIAL_WINDOW_SIZE` via SETTINGS, all active streams' window sizes must be adjusted by the delta:

```
newStreamWindow = oldStreamWindow + (newInitialWindowSize - oldInitialWindowSize)
```

This is handled by `Http2Connection.HandleSettingsFrameAsync`, not by `Http2Settings` itself. The settings class just stores values; the connection class applies the side effects.

## Validation Criteria

- [ ] Default values match RFC 7540 Section 6.5.2
- [ ] `Apply(EnablePush, 2)` throws ProtocolError
- [ ] `Apply(InitialWindowSize, 0x80000000)` throws FlowControlError
- [ ] `Apply(MaxFrameSize, 16383)` throws ProtocolError
- [ ] `Apply(MaxFrameSize, 16777216)` throws ProtocolError
- [ ] `Apply(MaxFrameSize, 16384)` succeeds (minimum)
- [ ] `Apply(MaxFrameSize, 16777215)` succeeds (maximum)
- [ ] Unknown setting ID is silently ignored
- [ ] `SerializeClientSettings` produces valid 6-byte-per-setting payload
- [ ] `ParsePayload` round-trips with `SerializeClientSettings`
- [ ] `ParsePayload` throws on non-multiple-of-6 payload length
