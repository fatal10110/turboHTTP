# Step 3C.4: BouncyCastle Package Integration

**File:** `Runtime/Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef`  
**Depends on:** Nothing  
**Spec:** BouncyCastle C# Portable (Modified)

## Purpose

Integrate BouncyCastle as **source code** with a **custom namespace** to avoid conflicts with other Unity plugins. This follows the proven approach used by BestHTTP, ensuring compatibility with IL2CPP and preventing duplicate assembly errors.

## Critical Decision: Source Code vs DLL

**Use BouncyCastle as SOURCE CODE, not a precompiled DLL.**

## Why This Matters

### Problem: Namespace Conflicts in Unity
Many Unity plugins (Firebase, encryption tools, etc.) already include standard BouncyCastle:
- Standard BC namespace: `Org.BouncyCastle.Crypto...`
- Result: **"Duplicate Assembly"** or **"Type already exists"** compilation errors

### Solution: Namespace Repackaging ("Shadowing")
Repackage BouncyCastle into a custom namespace:
- **Standard BC**: `Org.BouncyCastle.Crypto.Tls.TlsClient`
- **TurboHTTP BC**: `TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Tls.TlsClient`

This ensures TurboHTTP's TLS stack **never conflicts** with other plugins.

### Why Source Code, Not DLL?

1. **IL2CPP Compatibility**: Unity's linker can strip unused cryptographic algorithms, reducing app size
2. **AOT Optimization**: Source code compiles to native, DLLs may have issues
3. **No Assembly Conflicts**: Source files can be namespaced freely
4. **Debugging**: Easier to trace issues in production builds

## Implementation Strategy

### 1. Download BouncyCastle Source

**From:** [BouncyCastle C# GitHub](https://github.com/bcgit/bc-csharp)

**Version:** 2.2.1 or later

**Extract only TLS-related files:**
```
crypto/src/tls/          # TLS 1.2/1.3 protocol
crypto/src/crypto/       # Core cryptography
crypto/src/asn1/         # ASN.1 encoding (for certificates)
crypto/src/x509/         # X.509 certificate parsing
crypto/src/math/         # Big integer math
crypto/src/security/     # SecureRandom
crypto/src/utilities/    # Helper classes
```

**Exclude (not needed for TLS):**
- OpenPGP support
- CMS/PKCS support
- SSH support
- Legacy algorithms (DES, RC4, MD5)

### 2. Namespace Repackaging Script

**File:** `Editor/RepackageBouncyCastle.cs` (run once)

```csharp
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

public class RepackageBouncyCastle
{
    [MenuItem("Tools/TurboHTTP/Repackage BouncyCastle")]
    public static void Repackage()
    {
        var sourceDir = "Assets/TurboHTTP/ThirdParty/BouncyCastle-Source";
        var targetDir = "Assets/TurboHTTP/Runtime/Transport/BouncyCastle/Lib";
        
        // Replace all namespace declarations
        foreach (var file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            
            // Replace namespace declarations
            content = Regex.Replace(content, 
                @"namespace Org\.BouncyCastle",
                "namespace TurboHTTP.SecureProtocol.Org.BouncyCastle");
            
            // Replace using statements
            content = Regex.Replace(content,
                @"using Org\.BouncyCastle",
                "using TurboHTTP.SecureProtocol.Org.BouncyCastle");
            
            // Save to target directory
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.WriteAllText(targetPath, content);
        }
        
        AssetDatabase.Refresh();
        UnityEngine.Debug.Log("BouncyCastle repackaged successfully!");
    }
}
```

### 3. Assembly Definition

**File:** `Runtime/Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef`

```json
{
    "name": "TurboHTTP.Transport.BouncyCastle",
    "rootNamespace": "TurboHTTP.Transport.BouncyCastle",
    "references": [
        "TurboHTTP.Core",
        "TurboHTTP.Transport"
    ],
    "includePlatforms": [],
    "excludePlatforms": ["WebGL"],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": true
}
```

**Note:** `overrideReferences: false` and empty `precompiledReferences` because we're using source code.

### 4. Directory Structure After Repackaging

```
Runtime/Transport/BouncyCastle/
├── TurboHTTP.Transport.BouncyCastle.asmdef
├── BouncyCastleTlsProvider.cs          # Your provider implementation
├── TurboTlsClient.cs                    # Your TLS client
├── TurboTlsAuthentication.cs            # Your auth handler
└── Lib/                                 # Repackaged BouncyCastle source
    └── Org/
        └── BouncyCastle/
            ├── Tls/                      # TLS 1.2/1.3 implementation
            ├── Crypto/                   # Cryptographic primitives
            ├── Asn1/                     # ASN.1 encoding
            ├── X509/                     # Certificate handling
            ├── Math/                     # BigInteger
            ├── Security/                 # SecureRandom
            └── Utilities/                # Helpers
```

**All files in `Lib/` use the namespace:** `TurboHTTP.SecureProtocol.Org.BouncyCastle.*`

## BouncyCastle Version & Size

- **Recommended version:** 2.2.1 or later
- **License:** MIT License (compatible with Unity Asset Store)
- **Full BC size:** ~3.5 MB source code
- **Stripped for TLS:** ~800 KB - 1.2 MB (after removing unused algorithms)
- **After IL2CPP stripping:** ~500 KB - 800 KB (Unity's linker removes unused code)

## Assembly Configuration Explained

### `excludePlatforms: ["WebGL"]`

WebGL doesn't support raw TCP sockets, so TLS providers aren't relevant.

### `autoReferenced: false`

This assembly is **not** auto-referenced. Only projects that explicitly reference it (or depend on `TurboHTTP.Transport`) will include it.

This allows:
- Desktop-only projects to exclude BouncyCastle entirely
- Mobile projects to include it for ALPN support

### `overrideReferences: true`

Allows manual control of precompiled DLL references.

### `precompiledReferences`

References the BouncyCastle DLL. Unity will search for this DLL in:
1. The same directory as the `.asmdef`
2. `Plugins/` subdirectories
3. Other assembly definition folders

## Namespace

`TurboHTTP.Transport.BouncyCastle`

All BouncyCastle-related code will live in this namespace.

## Validation Criteria

- [ ] `.asmdef` file is valid JSON
- [ ] Assembly compiles without errors in Unity
- [ ] BouncyCastle DLL is found and referenced correctly
- [ ] No build errors on iOS/Android IL2CPP builds
- [ ] WebGL builds exclude this assembly (no errors)

## IL2CPP Optimization

With source code, Unity's IL2CPP linker can:
- ✅ **Strip unused algorithms**: Remove AES-256 if you only use AES-128
- ✅ **Dead code elimination**: Remove entire cipher suites if unused
- ✅ **Method inlining**: Optimize hot paths in TLS handshake
- ✅ **Native compilation**: Convert directly to ARM64/x86_64

Expected size reduction: **60-70%** compared to including full DLL.

## Stripping Strategy

### Keep (Essential for TLS 1.2/1.3)
- TLS protocol state machines
- ECDHE key exchange (secp256r1, x25519)
- AES-GCM cipher suites
- ChaCha20-Poly1305
- SHA-256, SHA-384
- X.509 certificate parsing
- RSA/ECDSA signature verification

### Remove (Not Needed)
- Legacy ciphers: 3DES, RC4, DES
- Legacy hashes: MD5, SHA-1 (except for certificates)
- OpenPGP, CMS, PKCS#7 support
- SSH protocol
- Older ECC curves (secp192r1, etc.)

## Distribution Notes

### Unity Asset Store Distribution

✅ **Include repackaged source code**, not DLL:
- Clearly document the custom namespace in README
- Provide the repackaging script for users who want to update BC version
- Document the MIT license
- Estimated size impact: **500 KB - 1.2 MB** (after IL2CPP stripping)

### Avoiding Conflicts

If users have other plugins with BouncyCastle:
- **TurboHTTP's BC**: `TurboHTTP.SecureProtocol.Org.BouncyCastle.*`
- **Other plugin's BC**: `Org.BouncyCastle.*`
- **Result**: ✅ No conflicts, both coexist peacefully

## Implementation Checklist

- [ ] Download BouncyCastle 2.2.1+ source from GitHub
- [ ] Run namespace repackaging script
- [ ] Verify all namespaces changed to `TurboHTTP.SecureProtocol.Org.BouncyCastle.*`
- [ ] Test compilation in Unity (no errors)
- [ ] Test IL2CPP build on iOS/Android
- [ ] Verify no conflicts with Firebase or other BC-using plugins
- [ ] Measure final binary size impact

## References

- [BouncyCastle C# GitHub](https://github.com/bcgit/bc-csharp)
- [BestHTTP Asset (Reference Implementation)](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)
- [Unity IL2CPP Code Stripping](https://docs.unity3d.com/Manual/IL2CPP-BytecodeStripping.html)
- [Unity Assembly Definitions](https://docs.unity3d.com/Manual/ScriptCompilationAssemblyDefinitionFiles.html)
