# Step 3C.3: SslStream TLS Provider

**File:** `Runtime/Transport/Tls/SslStreamTlsProvider.cs`  
**Depends on:** Step 3C.1, Step 3C.2  
**Spec:** RFC 7301 (TLS ALPN Extension)

## Purpose

Implement `ITlsProvider` using `System.Net.Security.SslStream`. This is the default TLS provider on platforms where .NET's built-in TLS works reliably. Uses reflection to access ALPN APIs that aren't available in .NET Standard 2.1.

## Type to Implement

### `SslStreamTlsProvider` (class)

```csharp
using System;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// TLS provider using System.Net.Security.SslStream.
    /// Uses reflection to access ALPN APIs on .NET Standard 2.1.
    /// </summary>
    internal sealed class SslStreamTlsProvider : ITlsProvider
    {
        public static readonly SslStreamTlsProvider Instance = new();

        public string ProviderName => "SslStream";

        private static readonly PropertyInfo _alpnOptionsProperty;
        private static readonly PropertyInfo _alpnNegotiatedProperty;
        private static readonly bool _alpnSupported;

        static SslStreamTlsProvider()
        {
            // Reflect into SslClientAuthenticationOptions.ApplicationProtocols
            var optionsType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslClientAuthenticationOptions");
            
            if (optionsType != null)
            {
                _alpnOptionsProperty = optionsType.GetProperty(
                    "ApplicationProtocols",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // Reflect into SslStream.NegotiatedApplicationProtocol
            _alpnNegotiatedProperty = typeof(SslStream).GetProperty(
                "NegotiatedApplicationProtocol",
                BindingFlags.Public | BindingFlags.Instance);

            _alpnSupported = _alpnOptionsProperty != null && _alpnNegotiatedProperty != null;
        }

        public bool IsAlpnSupported() => _alpnSupported;

        public async Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            var sslStream = new SslStream(
                innerStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidateServerCertificate);

            try
            {
                // Authenticate as client
                if (_alpnSupported && alpnProtocols != null && alpnProtocols.Length > 0)
                {
                    await AuthenticateWithAlpnAsync(sslStream, host, alpnProtocols, ct);
                }
                else
                {
                    // Fallback: no ALPN
                    await sslStream.AuthenticateAsClientAsync(
                        host,
                        clientCertificates: null,
                        enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                        checkCertificateRevocation: false);
                }

                // Extract ALPN result
                string negotiatedAlpn = null;
                if (_alpnSupported)
                {
                    var alpnResult = _alpnNegotiatedProperty?.GetValue(sslStream);
                    if (alpnResult != null)
                    {
                        // NegotiatedApplicationProtocol is SslApplicationProtocol struct
                        var protocolProperty = alpnResult.GetType().GetProperty("Protocol");
                        var protocolBytes = (byte[])protocolProperty?.GetValue(alpnResult);
                        if (protocolBytes != null && protocolBytes.Length > 0)
                        {
                            negotiatedAlpn = System.Text.Encoding.ASCII.GetString(protocolBytes);
                        }
                    }
                }

                return new TlsResult(
                    secureStream: sslStream,
                    negotiatedAlpn: negotiatedAlpn,
                    tlsVersion: FormatTlsVersion(sslStream.SslProtocol),
                    cipherSuite: null,  // SslStream doesn't expose this easily
                    providerName: ProviderName);
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        private async Task AuthenticateWithAlpnAsync(
            SslStream sslStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            // Create SslClientAuthenticationOptions via reflection
            var optionsType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslClientAuthenticationOptions");
            var options = Activator.CreateInstance(optionsType);

            // Set TargetHost
            optionsType.GetProperty("TargetHost").SetValue(options, host);

            // Set EnabledSslProtocols
            optionsType.GetProperty("EnabledSslProtocols").SetValue(
                options, SslProtocols.Tls12 | SslProtocols.Tls13);

            // Set ApplicationProtocols (List<SslApplicationProtocol>)
            var alpnListType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslApplicationProtocol");
            var alpnList = typeof(System.Collections.Generic.List<>)
                .MakeGenericType(alpnListType)
                .GetConstructor(Type.EmptyTypes)
                .Invoke(null);

            var addMethod = alpnList.GetType().GetMethod("Add");
            var fromStringMethod = alpnListType.GetMethod(
                "Parse", BindingFlags.Public | BindingFlags.Static);

            foreach (var protocol in alpnProtocols)
            {
                var alpn = fromStringMethod.Invoke(null, new object[] { protocol });
                addMethod.Invoke(alpnList, new[] { alpn });
            }

            _alpnOptionsProperty.SetValue(options, alpnList);

            // AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)
            var authMethod = typeof(SslStream).GetMethod(
                "AuthenticateAsClientAsync",
                new[] { optionsType, typeof(CancellationToken) });

            var task = (Task)authMethod.Invoke(sslStream, new[] { options, ct });
            await task;
        }

        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // Accept all certificates for now (basic validation)
            // TODO: In production, implement proper validation:
            //  - Check sslPolicyErrors
            //  - Verify certificate chain
            //  - Implement certificate pinning (Phase 6)
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        private string FormatTlsVersion(SslProtocols protocol)
        {
            return protocol switch
            {
                SslProtocols.Tls12 => "1.2",
                SslProtocols.Tls13 => "1.3",
                _ => protocol.ToString()
            };
        }
    }
}
```

## Implementation Details

### Static Reflection Setup

Reflection is performed **once** in the static constructor:
- Finds `SslClientAuthenticationOptions.ApplicationProtocols` property
- Finds `SslStream.NegotiatedApplicationProtocol` property
- Sets `_alpnSupported` flag based on whether both properties exist

This approach:
- Minimizes reflection overhead (only happens once per app lifetime)
- Gracefully degrades if ALPN is unavailable (just sets `_alpnSupported = false`)

### ALPN Handling

**If ALPN is supported:**
1. Create `SslClientAuthenticationOptions` via reflection
2. Set `ApplicationProtocols` to list of desired protocols
3. Call `AuthenticateAsClientAsync(options, ct)`
4. Extract `NegotiatedApplicationProtocol` after handshake

**If ALPN is NOT supported:**
- Fall back to basic `AuthenticateAsClientAsync(host, ...)`
- `NegotiatedAlpn` in result will be `null`

### Certificate Validation

Current implementation accepts all valid certificates (`sslPolicyErrors == None`).

**TODO for production:**
- Implement proper chain validation
- Add certificate pinning support (Phase 6)
- Handle custom trust anchors

### TLS Version Detection

Maps `SslProtocols` enum to simple version strings:
- `SslProtocols.Tls12` → `"1.2"`
- `SslProtocols.Tls13` → `"1.3"`

### Error Handling

On any exception during `AuthenticateAsClientAsync`:
- Dispose the `SslStream` (which closes the `innerStream`)
- Re-throw the exception for caller to handle

## Namespace

`TurboHTTP.Transport.Tls`

## Validation Criteria

- [ ] Compiles without errors on .NET Standard 2.1
- [ ] `IsAlpnSupported()` returns correct value based on platform
- [ ] ALPN negotiation works when supported (test with `https://www.google.com` which supports h2)
- [ ] Falls back gracefully when ALPN not supported
- [ ] Certificate validation rejects invalid certificates
- [ ] No Unity engine references

## Security Notes

⚠️ **Current certificate validation is BASIC**. For production:
1. Implement proper `SslPolicyErrors` handling
2. Verify certificate chain
3. Add hostname verification
4. Support certificate pinning (Phase 6)

## Platform Notes

This provider should work on:
- ✅ Windows (Editor, Standalone)
- ✅ macOS (Editor, Standalone)
- ✅ Linux (Editor, Standalone with Mono)
- ⚠️ iOS (ALPN may not work on IL2CPP)
- ⚠️ Android (ALPN may not work on IL2CPP)

On mobile platforms where ALPN fails, `TlsProviderSelector` will automatically use BouncyCastle.
