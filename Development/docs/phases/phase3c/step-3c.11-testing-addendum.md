# Step 3C.11 Testing Addendum

**Supplements:** [step-3c.11-tests.md](file:///Users/arturkoshtei/workspace/turboHTTP/docs/phases/phase3c/step-3c.11-tests.md)

This document adds missing test scenarios and comprehensive device testing checklists identified during the multi-agent review process.

---

## Additional Test File: `TlsConcurrencyAndEdgeCaseTests.cs`

**Purpose:** Validate edge cases, concurrency, and platform-specific behavior

**New Tests to Add:**
- [ ] `ConcurrentHandshakes_NoRaceConditions()`
- [ ] `WrapAsync_ServerStalls_TimesOut()`  
- [ ] `InvalidCertChain_ThrowsAuthenticationException()`
- [ ] `SslStream_AlpnSupport_MatchesExpectations()`
- [ ] `MemoryProfile_BouncyCastleHandshake()`

### Example Implementation

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.Transport.TlsProvider
{
    public class TlsConcurrencyAndEdgeCaseTests
    {
        [UnityTest, Category("Concurrency")]
        public IEnumerator ConcurrentHandshakes_NoRaceConditions()
        {
            // Start 10 parallel handshakes to www.google.com
            var tasks = new List<Task<TlsResult>>();
            
            for (int i = 0; i < 10; i++)
            {
                var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync("www.google.com", 443);
                yield return new WaitUntil(() => connectTask.IsCompleted);
                
                var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
                var handshakeTask = provider.WrapAsync(
                    tcpClient.GetStream(),
                    "www.google.com",
                    new[] { "h2", "http/1.1" },
                    CancellationToken.None);
                
                tasks.Add(handshakeTask);
            }
            
            yield return new WaitUntil(() => tasks.All(t => t.IsCompleted));
            
            // Verify all succeeded without exceptions
            foreach (var task in tasks)
            {
                Assert.IsFalse(task.IsFaulted, "Handshake should not fault");
                Assert.IsNotNull(task.Result.SecureStream);
            }
        }

        [Test, Category("Platform")]
        public void SslStream_AlpnSupport_MatchesExpectations()
        {
            var supported = SslStreamTlsProvider.Instance.IsAlpnSupported();
            
            #if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                Assert.IsTrue(supported, "Windows should support ALPN");
            #elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                Assert.IsTrue(supported, "macOS should support ALPN");
            #elif UNITY_IOS || UNITY_ANDROID
                // Log result for device validation - may or may not support
                Debug.Log($"Mobile SslStream ALPN: {supported}");
            #endif
        }

        [Test, Category("Performance")]
        public void MemoryProfile_BouncyCastleHandshake()
        {
            var beforeGC = System.GC.GetTotalMemory(forceFullCollection: true);
            
            // Perform 10 handshakes (implementation omitted for brevity)
            // In real test: connect to server, perform handshake, close connection
            
            var afterGC = System.GC.GetTotalMemory(forceFullCollection: true);
            var perHandshake = (afterGC - beforeGC) / 10;
            
            Assert.Less(perHandshake, 100 * 1024, 
                "Each handshake should allocate less than 100KB");
        }
    }
}
```

---

## Device Testing Checklists (MANDATORY)

> [!IMPORTANT]
> **Physical device testing is REQUIRED before declaring Phase 3C complete.**
> 
> These checklists must be executed on real hardware, not simulators/emulators.

### iOS Testing Checklist

#### Build Requirements
- [ ] Build with IL2CPP (not Mono simulator)
- [ ] Deploy to physical iPhone/iPad (iOS 12.0+)
- [ ] Verify BouncyCastle assembly is included in build

#### Functional Tests
- [ ] **Provider Selection:**
  - [ ] Check `SslStreamTlsProvider.IsAlpnSupported()` return value
  - [ ] Verify `TlsBackend.Auto` selects BouncyCastle if ALPN unsupported
  - [ ] Test forced `TlsBackend.BouncyCastle`
  - [ ] Test forced `TlsBackend.SslStream`

- [ ] **ALPN Negotiation:**
  - [ ] Connect to `https://www.google.com` → should negotiate "h2"
  - [ ] Connect to HTTP/1.1-only server → should return null/http/1.1

- [ ] **Certificate Validation:**
  - [ ] Valid cert: `https://www.google.com` → succeeds
  - [ ] Expired cert: `https://expired.badssl.com` → throws AuthenticationException
  - [ ] Self-signed: `https://self-signed.badssl.com` → throws AuthenticationException

- [ ] **Protocol Routing:**
  - [ ] HTTP/2 request completes successfully
  - [ ] HTTP/1.1 fallback works when server doesn't support HTTP/2

#### Performance Tests
- [ ] **Handshake Latency:**
  - [ ] Measure on WiFi (target: <300ms)
  - [ ] Measure on 4G/5G (target: <500ms)
  - [ ] Log: `TlsResult.TlsVersion`, `TlsResult.CipherSuite`, `TlsResult.ProviderName`

- [ ] **Memory Stability:**
  - [ ] 30-minute soak test with periodic HTTPS requests
  - [ ] Monitor memory usage in Xcode Instruments
  - [ ] Verify no leaks detected

- [ ] **Battery Impact:**
  - [ ] Measure idle power consumption
  - [ ] Measure during active TLS usage (10 requests/minute)
  - [ ] Acceptable increase: <5% over baseline

#### Edge Cases
- [ ] Network changes:
  - [ ] WiFi → Cellular handoff during active connection
  - [ ] Airplane mode → offline → online reconnection
- [ ] Concurrency:
  - [ ] 10+ concurrent HTTPS requests
  - [ ] No crashes, no exceptions
- [ ] Background/foreground:
  - [ ] App background → foreground with active connections

#### Expected Results
| Test | Expected Outcome |
|------|------------------|
| `SslStreamTlsProvider.IsAlpnSupported()` | Likely `false` (must verify) |
| Provider Auto-Selection | BouncyCastle (if ALPN unsupported) |
| HTTP/2 to google.com | ✅ Works with BouncyCastle |
| TLS Handshake Time | <500ms on good network |
| 30-min Soak Test | No memory leaks |

---

### Android Testing Checklist

#### Build Requirements
- [ ] Build with IL2CPP ARM64
- [ ] Test on Android 10+ device
- [ ] Test on multiple OEMs:
  - [ ] Samsung Galaxy
  - [ ] Google Pixel
  - [ ] OnePlus (or other)

#### Functional Tests
- [ ] **Provider Selection:**
  - [ ] Check `SslStreamTlsProvider.IsAlpnSupported()` return value
  - [ ] Verify `TlsBackend.Auto` selects BouncyCastle if ALPN unsupported
  - [ ] Test forced `TlsBackend.BouncyCastle`
  - [ ] Test forced `TlsBackend.SslStream`

- [ ] **ALPN Negotiation:**
  - [ ] Connect to `https://www.google.com` → should negotiate "h2"
  - [ ] Connect to HTTP/1.1-only server → should return null/http/1.1

- [ ] **Certificate Validation:**
  - [ ] Valid cert: `https://www.google.com` → succeeds
  - [ ] Expired cert: `https://expired.badssl.com` → throws AuthenticationException
  - [ ] Self-signed: `https://self-signed.badssl.com` → throws AuthenticationException

- [ ] **Protocol Routing:**
  - [ ] HTTP/2 request completes successfully
  - [ ] HTTP/1.1 fallback works

#### Performance Tests
- [ ] **Handshake Latency (per device):**
  - [ ] Samsung: Measure on WiFi + 4G/5G
  - [ ] Pixel: Measure on WiFi + 4G/5G
  - [ ] Other: Measure on WiFi + 4G/5G
  - [ ] Target: <500ms on good network

- [ ] **Memory Stability:**
  - [ ] 30-minute soak test
  - [ ] Monitor via Android Profiler
  - [ ] Verify no memory growth over time

- [ ] **Battery Impact:**
  - [ ] Measure battery drain with Battery Historian
  - [ ] Compare idle vs active TLS usage

#### Edge Cases
- [ ] Network changes:
  - [ ] WiFi → Mobile Data switch
  - [ ] VPN enabled/disabled
  - [ ] Network quality degradation (simulate with network throttling)
- [ ] Concurrency:
  - [ ] 10+ concurrent HTTPS requests
  - [ ] No ANRs, no exceptions
- [ ] Low memory:
  - [ ] Test with background apps consuming memory
  - [ ] Verify graceful handling

#### Expected Results
| Test | Expected Outcome |
|------|------------------|
| `SslStreamTlsProvider.IsAlpnSupported()` | Likely `false` (must verify per OEM) |
| Provider Auto-Selection | BouncyCastle (if ALPN unsupported) |
| HTTP/2 to google.com | ✅ Works with BouncyCastle |
| TLS Handshake Time | <500ms on good network |
| 30-min Soak Test | No memory leaks |
| Battery Impact | <5% increase over baseline |

---

## Validation Sign-Off

| Platform | Tester | Date | Status | Notes |
|----------|--------|------|--------|-------|
| iOS (iPhone) | | | ⏳ Pending | |
| Android (Samsung) | | | ⏳ Pending | |
| Android (Pixel) | | | ⏳ Pending | |

**Phase 3C is NOT complete until all device tests pass.**

---

## References

- Main Test Spec: [step-3c.11-tests.md](file:///Users/arturkoshtei/workspace/turboHTTP/docs/phases/phase3c/step-3c.11-tests.md)
- Multi-Agent Review: [phase3c_agent_review.md](file:///Users/arturkoshtei/.gemini/antigravity/brain/b59b473e-0596-4c1b-b44a-0cbfe3835e66/phase3c_agent_review.md)
