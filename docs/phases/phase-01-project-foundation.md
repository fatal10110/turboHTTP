# Phase 1: Project Foundation & Structure

**Milestone:** M0 (Spike)
**Dependencies:** None
**Estimated Complexity:** Low
**Critical:** Yes - Foundation for everything else

## Overview

Establish the Unity Package Manager (UPM) structure, modular assembly definitions, and basic project organization. This phase creates the scaffolding that all subsequent code will build upon.

## Goals

1. Create proper Unity package structure with `package.json`
2. Set up modular assembly definition files (.asmdef) for all modules
3. Create directory structure for runtime, editor, tests, and samples
4. Configure package metadata and dependencies
5. Ensure package imports cleanly into Unity Editor

## Tasks

### Task 1.1: Create Package Manifest

**File:** `package.json`

```json
{
  "name": "com.turbohttp.complete",
  "displayName": "TurboHTTP - Complete HTTP Client",
  "version": "1.0.0",
  "unity": "2021.3",
  "description": "Production-grade modular HTTP client for Unity with advanced features: retry logic, caching, observability, file downloads, and Unity asset handlers. Designed for games and applications requiring reliable network communication.",
  "keywords": [
    "http",
    "networking",
    "rest",
    "api",
    "download",
    "upload",
    "json",
    "cache",
    "retry",
    "middleware"
  ],
  "category": "Network",
  "author": {
    "name": "Your Name/Company",
    "email": "support@yourcompany.com",
    "url": "https://yourcompany.com"
  },
  "license": "Proprietary",
  "dependencies": {},
  "samples": [
    {
      "displayName": "Basic Usage",
      "description": "Simple GET and POST requests",
      "path": "Samples~/01-BasicUsage"
    },
    {
      "displayName": "JSON API Integration",
      "description": "Working with REST APIs and JSON",
      "path": "Samples~/02-JsonApi"
    },
    {
      "displayName": "File Downloads",
      "description": "Large file downloads with resume support",
      "path": "Samples~/03-FileDownload"
    },
    {
      "displayName": "Authentication",
      "description": "Token-based authentication and refresh",
      "path": "Samples~/04-Authentication"
    },
    {
      "displayName": "Advanced Features",
      "description": "Using all modules together",
      "path": "Samples~/05-AdvancedFeatures"
    }
  ]
}
```

**Notes:**
- Version starts at `1.0.0` (will be updated at release)
- Unity minimum version: `2021.3` (.NET Standard 2.1)
- License: `Proprietary` (closed source, Asset Store)
- No external dependencies (self-contained)

### Task 1.2: Create Directory Structure

**Execute:**
```bash
mkdir -p Runtime/Core
mkdir -p Runtime/Transport
mkdir -p Runtime/Pipeline/Middlewares
mkdir -p Runtime/Retry
mkdir -p Runtime/Cache
mkdir -p Runtime/Auth
mkdir -p Runtime/RateLimit
mkdir -p Runtime/Observability
mkdir -p Runtime/Files
mkdir -p Runtime/Unity
mkdir -p Runtime/Testing
mkdir -p Runtime/Performance
mkdir -p Runtime/Utils
mkdir -p Editor/Monitor
mkdir -p Editor/Settings
mkdir -p Tests/Runtime/Core
mkdir -p Tests/Runtime/Retry
mkdir -p Tests/Runtime/Cache
mkdir -p Tests/Runtime/Integration
mkdir -p Tests/Editor
mkdir -p Samples~/01-BasicUsage
mkdir -p Samples~/02-JsonApi
mkdir -p Samples~/03-FileDownload
mkdir -p Samples~/04-Authentication
mkdir -p Samples~/05-AdvancedFeatures
mkdir -p Documentation~
```

**Result:** Clean, organized structure ready for implementation

### Task 1.3: Create Core Assembly Definition

**File:** `Runtime/Core/TurboHTTP.Core.asmdef`

```json
{
  "name": "TurboHTTP.Core",
  "rootNamespace": "TurboHTTP.Core",
  "references": [],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**Notes:**
- `rootNamespace`: `TurboHTTP.Core` - all core types in this namespace
- `autoReferenced`: true - automatically available to all projects
- No platform restrictions - works everywhere Unity supports
- No unsafe code needed

### Task 1.4: Create Optional Module Assembly Definitions

**File:** `Runtime/Retry/TurboHTTP.Retry.asmdef`

```json
{
  "name": "TurboHTTP.Retry",
  "rootNamespace": "TurboHTTP.Retry",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Cache/TurboHTTP.Cache.asmdef`

```json
{
  "name": "TurboHTTP.Cache",
  "rootNamespace": "TurboHTTP.Cache",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Auth/TurboHTTP.Auth.asmdef`

```json
{
  "name": "TurboHTTP.Auth",
  "rootNamespace": "TurboHTTP.Auth",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/RateLimit/TurboHTTP.RateLimit.asmdef`

```json
{
  "name": "TurboHTTP.RateLimit",
  "rootNamespace": "TurboHTTP.RateLimit",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Observability/TurboHTTP.Observability.asmdef`

```json
{
  "name": "TurboHTTP.Observability",
  "rootNamespace": "TurboHTTP.Observability",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Files/TurboHTTP.Files.asmdef`

```json
{
  "name": "TurboHTTP.Files",
  "rootNamespace": "TurboHTTP.Files",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Unity/TurboHTTP.Unity.asmdef`

```json
{
  "name": "TurboHTTP.Unity",
  "rootNamespace": "TurboHTTP.Unity",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Testing/TurboHTTP.Testing.asmdef`

```json
{
  "name": "TurboHTTP.Testing",
  "rootNamespace": "TurboHTTP.Testing",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Runtime/Performance/TurboHTTP.Performance.asmdef`

```json
{
  "name": "TurboHTTP.Performance",
  "rootNamespace": "TurboHTTP.Performance",
  "references": ["TurboHTTP.Core"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

### Task 1.5: Create Editor Assembly Definition

**File:** `Editor/TurboHTTP.Editor.asmdef`

```json
{
  "name": "TurboHTTP.Editor",
  "rootNamespace": "TurboHTTP.Editor",
  "references": ["TurboHTTP.Core", "TurboHTTP.Observability"],
  "includePlatforms": ["Editor"],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**Notes:**
- `includePlatforms`: `["Editor"]` - only compiles in Editor
- References both Core and Observability (for HTTP Monitor)

### Task 1.6: Create Test Assembly Definitions

**File:** `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`

```json
{
  "name": "TurboHTTP.Tests.Runtime",
  "rootNamespace": "TurboHTTP.Tests",
  "references": [
    "TurboHTTP.Core",
    "TurboHTTP.Retry",
    "TurboHTTP.Cache",
    "TurboHTTP.Auth",
    "TurboHTTP.RateLimit",
    "TurboHTTP.Observability",
    "TurboHTTP.Files",
    "TurboHTTP.Unity",
    "TurboHTTP.Testing",
    "TurboHTTP.Performance",
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner"
  ],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": true,
  "precompiledReferences": [
    "nunit.framework.dll"
  ],
  "autoReferenced": false,
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**File:** `Tests/Editor/TurboHTTP.Tests.Editor.asmdef`

```json
{
  "name": "TurboHTTP.Tests.Editor",
  "rootNamespace": "TurboHTTP.Tests.Editor",
  "references": [
    "TurboHTTP.Core",
    "TurboHTTP.Editor",
    "TurboHTTP.Observability",
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner"
  ],
  "includePlatforms": ["Editor"],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": true,
  "precompiledReferences": [
    "nunit.framework.dll"
  ],
  "autoReferenced": false,
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "versionDefines": [],
  "noEngineReferences": false
}
```

### Task 1.7: Create README.md

**File:** `README.md`

```markdown
# TurboHTTP - Complete HTTP Client for Unity

A production-grade, modular HTTP client for Unity with advanced features designed for games and applications requiring reliable network communication.

## Features

- **Modular Architecture:** Use only the modules you need
- **Cross-Platform:** Works on Editor, Standalone, iOS, Android (WebGL in v1.x)
- **Advanced Retry Logic:** Intelligent retries with idempotency awareness
- **HTTP Caching:** ETag-based caching for optimized bandwidth
- **Timeline Tracing:** Detailed observability for every request
- **File Downloads:** Resume support and integrity verification
- **Unity Integration:** Native support for Texture2D, AudioClip, and more
- **Testing Tools:** Record/replay mode for deterministic testing
- **Editor Monitor:** Inspect HTTP traffic directly in Unity Editor

## Quick Start

```csharp
using TurboHTTP.Core;

var client = new UHttpClient();
var response = await client.Get("https://api.example.com/data").SendAsync();

if (response.IsSuccessStatusCode)
{
    var text = System.Text.Encoding.UTF8.GetString(response.Body);
    Debug.Log(text);
}
```

## Documentation

See the [Documentation](Documentation~/QuickStart.md) folder for:
- Quick Start Guide
- Module Documentation
- API Reference
- Platform Notes

## Requirements

- Unity 2021.3 LTS or higher
- .NET Standard 2.1

## Support

For support, please contact: support@yourcompany.com

## License

Proprietary - Unity Asset Store License
```

### Task 1.8: Create CHANGELOG.md

**File:** `CHANGELOG.md`

```markdown
# Changelog

All notable changes to TurboHTTP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial project structure
- Modular assembly definitions

## [1.0.0] - TBD

### Added
- Core HTTP client with fluent API
- Retry middleware with idempotency awareness
- Cache middleware with ETag support
- Authentication middleware
- Rate limiting middleware
- Observability module with timeline tracing
- File downloader with resume support
- Unity content handlers (Texture2D, AudioClip)
- Testing module with record/replay
- Performance module with memory pooling
- Editor HTTP Monitor window
- Comprehensive samples and documentation
```

### Task 1.9: Create LICENSE.md

**File:** `LICENSE.md`

```markdown
# License

Copyright (c) [Year] [Your Company Name]

This software is proprietary and distributed through the Unity Asset Store.

## Asset Store License

This package is licensed under the Unity Asset Store End User License Agreement (EULA).
For the full terms, visit: https://unity.com/legal/as-terms

## Key Points

- Licensed per seat (per developer)
- Can be used in unlimited projects
- Cannot redistribute source code
- Cannot resell or sub-license
- Support provided via [support email/forum]

For questions about licensing, contact: support@yourcompany.com
```

## Validation Criteria

### Success Criteria

- [ ] `package.json` exists and has correct metadata
- [ ] All directory structure created
- [ ] All 10 runtime module .asmdef files created
- [ ] Editor .asmdef file created
- [ ] Test .asmdef files created (runtime and editor)
- [ ] README.md created with quick start
- [ ] CHANGELOG.md created
- [ ] LICENSE.md created with Asset Store license

### Unity Editor Validation

1. **Import Package:**
   - Copy `turboHTTP` folder to Unity project's `Packages/` directory
   - Or add as local package via Package Manager

2. **Check Package Manager:**
   - Open Window → Package Manager
   - Select "Packages: In Project"
   - Verify "TurboHTTP - Complete HTTP Client" appears
   - Verify version, description correct

3. **Check Assembly Definitions:**
   - Project window should show no compile errors
   - All .asmdef files should appear with proper icons
   - No assembly reference errors

4. **Check Samples:**
   - Package Manager → TurboHTTP → Samples section
   - All 5 samples listed (not importable yet, but listed)

5. **Check Structure:**
   - Runtime folder has 10 module folders
   - Editor folder exists
   - Tests folder exists with Runtime/Editor subfolders
   - Samples~ folder exists (hidden in Unity, visible in file system)

### Common Issues & Fixes

**Issue:** Package doesn't appear in Package Manager
**Fix:** Ensure `package.json` is in the package root, not a subfolder

**Issue:** Assembly definition errors
**Fix:** Verify all .asmdef files are valid JSON, no trailing commas

**Issue:** "Circular dependency" errors
**Fix:** Check that module .asmdef files only reference Core, not each other

**Issue:** Samples folder visible in Project window
**Fix:** Rename to `Samples~` (with tilde) to hide from Unity

## Next Steps

Once Phase 1 is complete and validated:

1. Move to [Phase 2: Core Type System](phase-02-core-types.md)
2. Begin implementing the foundational request/response types
3. Create the transport abstraction layer

## Notes

- All assembly definitions set `autoReferenced: true` for convenience
- Users can disable specific modules by excluding them from builds
- No code exists yet - just structure and configuration
- This phase is quick but critical - foundation for everything else
