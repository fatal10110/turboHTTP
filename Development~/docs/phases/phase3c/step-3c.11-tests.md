# Step 3C.11: Unit & Integration Tests

**Files:** `Tests/Runtime/Transport/TlsProviderTests/` (6 new test files)  
**Depends on:** Steps 3C.1–3C.10  
**Spec:** Comprehensive Test Coverage

## Purpose

Validate all TLS provider implementations with unit tests and integration tests against real servers. Ensure both SslStream and BouncyCastle work correctly on all platforms.

## Test Files to Create

### 1. `TlsProviderSelectorTests.cs`

**Tests:**
- [ ] `Auto_OnDesktop_UsesSslStream()`
- [ ] `Auto_OnMobile_UsesBouncyCastleIfAlpnUnsupported()`
- [ ] `ForceSslStream_ReturnsCorrectProvider()`
- [ ] `ForceBouncyCastle_ReturnsCorrectProvider()`
- [ ] `ForceBouncyCastle_WhenNotAvailable_ThrowsException()`

```csharp
[Test]
public void Auto_OnDesktop_UsesSslStream()
{
    #if UNITY_EDITOR || UNITY_STANDALONE
        var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
        Assert.AreEqual("SslStream", provider.ProviderName);
    #endif
}

[Test]
public void ForceSslStream_ReturnsCorrectProvider()
{
    var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
    Assert.AreEqual("SslStream", provider.ProviderName);
}

[Test]
public void ForceBouncyCastle_ReturnsCorrectProvider()
{
    var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
    Assert.AreEqual("BouncyCastle", provider.ProviderName);
}
```

### 2. `SslStreamProviderTests.cs`

**Tests:**
- [ ] `IsAlpnSupported_ReturnsExpectedValue()`
- [ ] `WrapAsync_ValidServer_Succeeds()`
- [ ] `WrapAsync_ExpiredCert_ThrowsAuthenticationException()`
- [ ] `WrapAsync_WithAlpn_NegotiatesH2()` (integration test)
- [ ] `WrapAsync_WithAlpn_NegotiatesHttp11()` (integration test)

```csharp
[UnityTest]
public IEnumerator WrapAsync_WithAlpn_NegotiatesH2()
{
    var provider = SslStreamTlsProvider.Instance;
    var tcpClient = new TcpClient();
    
    var connectTask = tcpClient.ConnectAsync("www.google.com", 443);
    yield return new WaitUntil(() => connectTask.IsCompleted);
    
    var wrapTask = provider.WrapAsync(
        tcpClient.GetStream(),
        "www.google.com",
        new[] { "h2", "http/1.1" },
        CancellationToken.None);
    
    yield return new WaitUntil(() => wrapTask.IsCompleted);
    
    var result = wrapTask.Result;
    Assert.IsNotNull(result.SecureStream);
    Assert.AreEqual("h2", result.NegotiatedAlpn);
    Assert.AreEqual("SslStream", result.ProviderName);
}

[UnityTest]
public IEnumerator WrapAsync_ExpiredCert_ThrowsAuthenticationException()
{
    var provider = SslStreamTlsProvider.Instance;
    var tcpClient = new TcpClient();
    
    var connectTask = tcpClient.ConnectAsync("expired.badssl.com", 443);
    yield return new WaitUntil(() => connectTask.IsCompleted);
    
    var wrapTask = provider.WrapAsync(
        tcpClient.GetStream(),
        "expired.badssl.com",
        Array.Empty<string>(),
        CancellationToken.None);
    
    yield return new WaitUntil(() => wrapTask.IsCompleted);
    
    Assert.IsTrue(wrapTask.IsFaulted);
    Assert.IsInstanceOf<AuthenticationException>(wrapTask.Exception.InnerException);
}
```

### 3. `BouncyCastleProviderTests.cs`

**Tests:**
- [ ] `IsAlpnSupported_AlwaysReturnsTrue()`
- [ ] `WrapAsync_ValidServer_Succeeds()`
- [ ] `WrapAsync_ExpiredCert_ThrowsFatalAlert()`
- [ ] `WrapAsync_WithAlpn_NegotiatesH2()` (integration test)
- [ ] `WrapAsync_WildcardCert_Succeeds()` (integration test)

```csharp
[UnityTest]
public IEnumerator WrapAsync_WithAlpn_NegotiatesH2()
{
    var provider = BouncyCastleTlsProvider.Instance;
    var tcpClient = new TcpClient();
    
    var connectTask = tcpClient.ConnectAsync("www.google.com", 443);
    yield return new WaitUntil(() => connectTask.IsCompleted);
    
    var wrapTask = provider.WrapAsync(
        tcpClient.GetStream(),
        "www.google.com",
        new[] { "h2", "http/1.1" },
        CancellationToken.None);
    
    yield return new WaitUntil(() => wrapTask.IsCompleted);
    
    var result = wrapTask.Result;
    Assert.IsNotNull(result.SecureStream);
    Assert.AreEqual("h2", result.NegotiatedAlpn);
    Assert.AreEqual("BouncyCastle", result.ProviderName);
    Assert.IsNotNull(result.CipherSuite);
}

[Test]
public void IsAlpnSupported_AlwaysReturnsTrue()
{
    var provider = BouncyCastleTlsProvider.Instance;
    Assert.IsTrue(provider.IsAlpnSupported());
}
```

### 4. `TlsHostnameValidationTests.cs`

**Tests for `TurboTlsAuthentication`:**
- [ ] `ValidateHostname_ExactMatch_Succeeds()`
- [ ] `ValidateHostname_WildcardMatch_Succeeds()`
- [ ] `ValidateHostname_WildcardMismatch_Throws()`
- [ ] `ValidateHostname_MismatchedHost_Throws()`
- [ ] `ValidateValidity_ExpiredCert_Throws()`
- [ ] `ValidateValidity_NotYetValid_Throws()`

```csharp
[Test]
public void ValidateHostname_ExactMatch_Succeeds()
{
    // Create mock certificate with SAN "example.com"
    var auth = new TurboTlsAuthentication("example.com");
    // ... mock certificate creation ...
    // Assert.DoesNotThrow(() => auth.NotifyServerCertificate(cert));
}

[Test]
public void ValidateHostname_WildcardMatch_Succeeds()
{
    // Certificate for "*.example.com", connecting to "www.example.com"
    var auth = new TurboTlsAuthentication("www.example.com");
    // ... should succeed ...
}

[Test]
public void ValidateHostname_MismatchedHost_Throws()
{
    // Certificate for "example.com", connecting to "attacker.com"
    var auth = new TurboTlsAuthentication("attacker.com");
    // ... should throw TlsFatalAlert ...
}
```

### 5. `TlsIntegrationTests.cs`

**End-to-end tests with real servers:**
- [ ] `HttpClient_WithSslStream_CanFetchGoogle()`
- [ ] `HttpClient_WithBouncyCastle_CanFetchGoogle()`
- [ ] `HttpClient_Auto_SelectsCorrectProvider()`
- [ ] `HttpClient_Http2_Works()`

```csharp
[UnityTest]
public IEnumerator HttpClient_WithBouncyCastle_CanFetchGoogle()
{
    var options = new HttpClientOptions
    {
        TlsBackend = TlsBackend.BouncyCastle
    };
    var client = new HttpClient(options);
    
    var request = new HttpRequest
    {
        Method = HttpMethod.GET,
        Url = "https://www.google.com"
    };
    
    var responseTask = client.SendAsync(request);
    yield return new WaitUntil(() => responseTask.IsCompleted);
    
    var response = responseTask.Result;
    Assert.AreEqual(200, response.StatusCode);
    Assert.IsTrue(response.Body.Length > 0);
}
```

### 6. `TlsPerformanceBenchmarkTests.cs`

**Performance comparisons (optional, manual tests):**
- [ ] `Benchmark_SslStream_HandshakeTime()`
- [ ] `Benchmark_BouncyCastle_HandshakeTime()`
- [ ] Compare memory allocations

```csharp
[Test, Category("Performance")]
public void Benchmark_SslStream_HandshakeTime()
{
    var provider = SslStreamTlsProvider.Instance;
    var stopwatch = Stopwatch.StartNew();
    
    for (int i = 0; i < 10; i++)
    {
        // Perform handshake with www.google.com
        // Measure total time
    }
    
    stopwatch.Stop();
    Debug.Log($"SslStream handshake (10x): {stopwatch.ElapsedMilliseconds}ms");
}
```

## Platform Test Matrix

| Test | Windows | macOS | Linux | iOS | Android |
|------|---------|-------|-------|-----|---------|
| SslStream ALPN | ✅ | ✅ | ✅ | ⚠️ | ⚠️ |
| BouncyCastle ALPN | ✅ | ✅ | ✅ | ✅ | ✅ |
| Auto selection | ✅ | ✅ | ✅ | ✅ | ✅ |
| Certificate validation | ✅ | ✅ | ✅ | ✅ | ✅ |

**Legend:**
- ✅ Must pass on this platform
- ⚠️ May fail (expected on IL2CPP)

## Test Servers

Use public test servers:
1. **Valid HTTPS + HTTP/2**: `https://www.google.com`
2. **Valid HTTPS + HTTP/1.1 only**: `https://example.com`
3. **Expired certificate**: `https://expired.badssl.com`
4. **Self-signed certificate**: `https://self-signed.badssl.com`
5. **Wildcard certificate**: Find a live example

## Validation Criteria

- [ ] All unit tests pass on all platforms
- [ ] Integration tests pass with both SslStream and BouncyCastle
- [ ] Certificate validation correctly rejects invalid certs
- [ ] ALPN negotiation works as expected
- [ ] Performance benchmarks show acceptable overhead for BouncyCastle

## Running Tests

**In Unity Test Runner:**
1. Open Window → General → Test Runner
2. Select "PlayMode" tab
3. Run all tests in `TlsProviderTests/` folder

**On physical devices:**
1. Build test project for iOS/Android
2. Run on device
3. Check Unity logs for test results

## References

- [Unity Test Framework](https://docs.unity3d.com/Packages/com.unity.test-framework@latest)
- [NUnit Assert API](https://docs.nunit.org/articles/nunit/writing-tests/assertions/assertion-models/constraint.html)
