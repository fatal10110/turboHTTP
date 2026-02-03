# Step 3B.15: Unit & Integration Tests

**Directory:** `Tests/Runtime/Transport/Http2/`
**Depends on:** Steps 3B.1–3B.14 (all implementation steps)

## Purpose

Comprehensive test coverage for all HTTP/2 components. Tests use Unity Test Runner with NUnit. A `TestDuplexStream` helper enables testing `Http2Connection` without a real network.

## Test Files

### 1. `Http2FrameCodecTests.cs`

Tests for frame serialization/deserialization:

```csharp
[TestFixture]
public class Http2FrameCodecTests
{
    // Round-trip tests
    [Test] void DataFrame_RoundTrip()
    [Test] void HeadersFrame_RoundTrip()
    [Test] void SettingsFrame_RoundTrip()
    [Test] void PingFrame_RoundTrip()
    [Test] void GoAwayFrame_RoundTrip()
    [Test] void WindowUpdateFrame_RoundTrip()
    [Test] void RstStreamFrame_RoundTrip()
    [Test] void ContinuationFrame_RoundTrip()

    // Stream ID encoding
    [Test] void StreamId_31BitMasking_HighBitCleared()
    [Test] void StreamId_Zero_ConnectionLevel()
    [Test] void StreamId_MaxValue_2147483647()

    // Payload handling
    [Test] void ZeroPayload_EmptyArray()
    [Test] void MaxDefaultPayload_16384Bytes()
    [Test] void FrameSizeValidation_RejectsOversized()

    // Connection preface
    [Test] void WritePrefaceAsync_Correct24Bytes()

    // Flags
    [Test] void HasFlag_EndStream()
    [Test] void HasFlag_Multiple()
}
```

**Test approach:** Write frame to `MemoryStream`, reset position, read frame back, assert all fields match.

### 2. `HpackStaticTableTests.cs`

```csharp
[TestFixture]
public class HpackStaticTableTests
{
    [Test] void Get_Index1_ReturnsAuthority()
    [Test] void Get_Index2_ReturnsMethodGet()
    [Test] void Get_Index7_ReturnsSchemeHttps()
    [Test] void Get_Index61_ReturnsWwwAuthenticate()
    [Test] void Get_Index0_Throws()
    [Test] void Get_Index62_Throws()

    [Test] void FindMatch_MethodGet_FullMatch_Index2()
    [Test] void FindMatch_MethodPut_NameMatch_Index2()
    [Test] void FindMatch_Status200_FullMatch_Index8()
    [Test] void FindMatch_CustomHeader_None()
    [Test] void FindMatch_AuthorityEmpty_FullMatch_Index1()

    [Test] void TableHas61Entries()
}
```

### 3. `HpackHuffmanTests.cs`

```csharp
[TestFixture]
public class HpackHuffmanTests
{
    // Round-trip
    [Test] void RoundTrip_CommonStrings()
    [Test] void RoundTrip_EmptyString()
    [Test] void RoundTrip_AllByteValues()

    // RFC test vectors
    [Test] void Encode_WwwExampleCom_MatchesRfcVector()
    // "www.example.com" → f1e3c2e5f23a6ba0ab90f4ff (RFC 7541 C.4.1)

    [Test] void Encode_NoCache_MatchesRfcVector()
    // "no-cache" → a8eb10649cbf (RFC 7541 C.4.2)

    [Test] void Encode_CustomKey_MatchesRfcVector()
    // "custom-key" → 25a849e95ba97d7f (RFC 7541 C.4.3)

    [Test] void Encode_CustomValue_MatchesRfcVector()
    // "custom-value" → 25a849e95bb8e8b4bf (RFC 7541 C.4.3)

    // Padding
    [Test] void Encode_PaddingIsAll1Bits()
    [Test] void Decode_InvalidPadding_Throws()

    // GetEncodedLength
    [Test] void GetEncodedLength_MatchesActualOutput()

    // Error cases
    [Test] void Decode_TruncatedInput_Throws()
}
```

### 4. `HpackIntegerCodecTests.cs`

```csharp
[TestFixture]
public class HpackIntegerCodecTests
{
    // RFC 7541 Section C.1 test vectors
    [Test] void Encode_10_Prefix5_SingleByte()      // → 0x0A
    [Test] void Encode_1337_Prefix5_ThreeBytes()     // → 0x1F 0x9A 0x0A
    [Test] void Encode_42_Prefix8_SingleByte()       // → 0x2A

    // Round-trip
    [Test] void RoundTrip_Zero()
    [Test] void RoundTrip_MaxPrefixMinusOne()
    [Test] void RoundTrip_MaxPrefix()
    [Test] void RoundTrip_LargeValue_65535()
    [Test] void RoundTrip_AllPrefixWidths_1Through8()

    // Edge cases
    [Test] void Encode_31_Prefix5_TwoBytes()  // Exactly at boundary
    [Test] void Decode_OverflowDetection_Throws()
    [Test] void Decode_UnexpectedEnd_Throws()

    // Offset advancement
    [Test] void Decode_AdvancesOffset_SingleByte()
    [Test] void Decode_AdvancesOffset_MultiByte()
}
```

### 5. `HpackDynamicTableTests.cs`

```csharp
[TestFixture]
public class HpackDynamicTableTests
{
    [Test] void Add_SingleEntry_AtIndex62()
    [Test] void Add_TwoEntries_NewestAtIndex62()
    [Test] void Add_Eviction_OldestRemoved()
    [Test] void Add_EntrySizeExceedsMax_ClearsTable()
    [Test] void Get_StaticRange_DelegatesToStaticTable()
    [Test] void Get_DynamicRange_ReturnsCorrectEntry()
    [Test] void Get_OutOfRange_Throws()
    [Test] void Get_Index0_Throws()
    [Test] void FindMatch_FullMatch_DynamicTable()
    [Test] void FindMatch_NameMatch_DynamicTable()
    [Test] void FindMatch_PrefersStaticFullMatch()
    [Test] void SetMaxSize_Zero_ClearsAll()
    [Test] void SetMaxSize_Reduced_Evicts()
    [Test] void EntrySize_NamePlusValuePlus32()
    [Test] void CurrentSize_TracksCorrectly()
}
```

### 6. `HpackEncoderDecoderTests.cs`

```csharp
[TestFixture]
public class HpackEncoderDecoderTests
{
    // Round-trip
    [Test] void RoundTrip_SingleHeader()
    [Test] void RoundTrip_MultipleHeaders()
    [Test] void RoundTrip_PseudoHeaders()
    [Test] void RoundTrip_WithDynamicTableReuse()
    [Test] void RoundTrip_SensitiveHeaders_NeverIndexed()

    // RFC 7541 Appendix C.3: Requests without Huffman
    [Test] void Rfc7541_C3_1_FirstRequest()
    [Test] void Rfc7541_C3_2_SecondRequest()
    [Test] void Rfc7541_C3_3_ThirdRequest()

    // RFC 7541 Appendix C.4: Requests with Huffman
    [Test] void Rfc7541_C4_1_FirstRequest()
    [Test] void Rfc7541_C4_2_SecondRequest()
    [Test] void Rfc7541_C4_3_ThirdRequest()

    // RFC 7541 Appendix C.5: Responses without Huffman
    [Test] void Rfc7541_C5_1_FirstResponse()
    [Test] void Rfc7541_C5_2_SecondResponse()
    [Test] void Rfc7541_C5_3_ThirdResponse()

    // RFC 7541 Appendix C.6: Responses with Huffman
    [Test] void Rfc7541_C6_1_FirstResponse()
    [Test] void Rfc7541_C6_2_SecondResponse()
    [Test] void Rfc7541_C6_3_ThirdResponse()

    // Error handling
    [Test] void Decode_Index0_Throws()
    [Test] void Decode_InvalidRepresentation_Throws()

    // Dynamic table size update
    [Test] void DynamicTableSizeUpdate_DecodedCorrectly()
}
```

**RFC 7541 Appendix C test vectors are critical.** These are known-good input/output pairs that validate the entire HPACK pipeline (integer coding + string encoding + table lookups + dynamic table mutations).

### 7. `Http2ConnectionTests.cs`

```csharp
[TestFixture]
public class Http2ConnectionTests
{
    // Uses TestDuplexStream (see below) to simulate server-side frames

    // Connection setup
    [Test] void InitializeAsync_SendsPreface()
    [Test] void InitializeAsync_SendsSettings()
    [Test] void InitializeAsync_AcksServerSettings()
    [Test] void InitializeAsync_WaitsForSettingsAck()

    // Request/response lifecycle
    [Test] void SendGetRequest_ReceiveResponse()
    [Test] void SendPostRequest_WithBody()
    [Test] void SendRequest_HeadersSpanContinuation()
    [Test] void SendRequest_AfterGoaway_Throws()
    [Test] void SendRequest_Cancelled_SendsRstStream()

    // Frame handling
    [Test] void PingFrame_EchoedWithAck()
    [Test] void GoAwayFrame_FailsHigherStreams()
    [Test] void GoAwayFrame_AllowsLowerStreamsToComplete()
    [Test] void RstStreamFrame_FailsSpecificStream()
    [Test] void PushPromise_RejectedWithRstStream()

    // Settings
    [Test] void Settings_InitialWindowSizeChange_AdjustsStreams()
    [Test] void Settings_MaxFrameSizeChange_Applied()
    [Test] void Settings_UnknownId_Ignored()

    // Cleanup
    [Test] void Dispose_FailsAllActiveStreams()
    [Test] void Dispose_SendsBestEffortGoaway()
}
```

### 8. `Http2FlowControlTests.cs`

```csharp
[TestFixture]
public class Http2FlowControlTests
{
    [Test] void WindowUpdate_IncreasesConnectionWindow()
    [Test] void WindowUpdate_IncreasesStreamWindow()
    [Test] void WindowUpdate_Zero_ProtocolError()
    [Test] void WindowUpdate_Overflow_FlowControlError()
    [Test] void DataSending_RespectsConnectionWindow()
    [Test] void DataSending_RespectsStreamWindow()
    [Test] void DataSending_BlocksWhenWindowExhausted()
    [Test] void WindowUpdate_UnblocksPendingSend()
    [Test] void DataReceiving_SendsWindowUpdate()
    [Test] void ConnectionAndStreamWindows_Independent()
}
```

## Test Helper: `TestDuplexStream`

A bidirectional in-memory stream for testing `Http2Connection` without real TCP/TLS:

```csharp
/// <summary>
/// A duplex stream that connects two endpoints (client and server).
/// Write on one side, read on the other.
/// Used for Http2Connection tests.
/// </summary>
internal class TestDuplexStream
{
    public Stream ClientStream { get; }
    public Stream ServerStream { get; }

    public TestDuplexStream()
    {
        // Two MemoryStreams connected via cross-wired read/write
        // Or use System.IO.Pipelines if available
    }
}
```

**Implementation options:**
- **Dual `MemoryStream` + byte queues:** Simple but requires manual synchronization.
- **`System.IO.Pipelines.Pipe`:** Clean async, but may not be available in Unity 2021.3.
- **BlockingCollection-backed Stream:** Each side writes to the other's read queue.

For Phase 3, use a simple approach:
1. Server writes pre-crafted frame bytes to a `MemoryStream`.
2. Client reads from that `MemoryStream`.
3. Client writes are captured to a separate `MemoryStream` for assertion.
4. For bi-directional tests, use a `BlockingStream` wrapper that blocks `ReadAsync` until data is written from the other side.

## Test Location

```
Tests/Runtime/Transport/Http2/
    Http2FrameCodecTests.cs
    HpackStaticTableTests.cs
    HpackHuffmanTests.cs
    HpackIntegerCodecTests.cs
    HpackDynamicTableTests.cs
    HpackEncoderDecoderTests.cs
    Http2ConnectionTests.cs
    Http2FlowControlTests.cs
    Helpers/TestDuplexStream.cs
```

Ensure the test assembly (`TurboHTTP.Tests.Runtime.asmdef`) references `TurboHTTP.Transport` so internal types are accessible. If `InternalsVisibleTo` is needed, add `[assembly: InternalsVisibleTo("TurboHTTP.Tests.Runtime")]` to the Transport assembly.

## Integration Tests (Manual / CI)

Not included as automated tests (require network), but documented for manual verification:

1. **GET to h2 server:** `https://www.google.com` — verify response status 200, body non-empty
2. **Multiple concurrent requests:** 5 parallel GETs to `https://www.google.com` — verify all complete on one TCP connection
3. **Fallback to h1.1:** Request to a server that doesn't support h2 — verify HTTP/1.1 used
4. **Large response:** Download a multi-MB response — verify flow control (WINDOW_UPDATEs sent)

## Validation Criteria

- [ ] All RFC 7541 Appendix C test vectors pass (12 test cases)
- [ ] Frame codec round-trips all 10 frame types
- [ ] Integer codec round-trips RFC 7541 C.1 vectors
- [ ] Huffman codec round-trips RFC test strings
- [ ] Dynamic table add/evict/lookup works correctly
- [ ] Http2Connection handles full request/response lifecycle
- [ ] Flow control blocking and unblocking works
- [ ] GOAWAY, RST_STREAM, PING handlers tested
- [ ] `InternalsVisibleTo` configured if needed for test access
