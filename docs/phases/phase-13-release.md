# Phase 13: CI/CD & Release

**Milestone:** M3 (v1.0 "production")
**Dependencies:** Phase 12 (Editor Tooling)
**Estimated Complexity:** Medium
**Critical:** Yes - Release preparation

## Overview

Set up continuous integration and continuous deployment (CI/CD) pipeline, prepare Asset Store submission package, and create the release process. This phase ensures consistent quality and streamlined releases.

## Goals

1. Set up CI/CD pipeline (GitHub Actions or similar)
2. Automate testing on all platforms
3. Create Asset Store submission package
4. Write release checklist
5. Create versioning strategy
6. Set up automated builds
7. Prepare marketing materials
8. Create support infrastructure

## Tasks

### Task 13.1: GitHub Actions CI

**File:** `.github/workflows/ci.yml`

```yaml
name: TurboHTTP CI

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  test:
    name: Test in Unity ${{ matrix.unityVersion }}
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        unityVersion:
          - 2021.3.0f1
          - 2022.3.0f1
        testMode:
          - PlayMode
          - EditMode

    steps:
      - name: Checkout repository
        uses: actions/checkout@v3

      - name: Cache Unity Library
        uses: actions/cache@v3
        with:
          path: Library
          key: Library-${{ matrix.unityVersion }}

      - name: Run tests
        uses: game-ci/unity-test-runner@v2
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
        with:
          unityVersion: ${{ matrix.unityVersion }}
          testMode: ${{ matrix.testMode }}
          artifactsPath: test-results
          githubToken: ${{ secrets.GITHUB_TOKEN }}

      - name: Upload test results
        uses: actions/upload-artifact@v3
        if: always()
        with:
          name: Test results (${{ matrix.unityVersion }}-${{ matrix.testMode }})
          path: test-results

  build:
    name: Build for ${{ matrix.targetPlatform }}
    runs-on: ubuntu-latest
    needs: test
    strategy:
      fail-fast: false
      matrix:
        targetPlatform:
          - StandaloneWindows64
          - StandaloneOSX
          - StandaloneLinux64
          - iOS
          - Android

    steps:
      - name: Checkout repository
        uses: actions/checkout@v3

      - name: Cache Unity Library
        uses: actions/cache@v3
        with:
          path: Library
          key: Library-2021.3.0f1-${{ matrix.targetPlatform }}

      - name: Build project
        uses: game-ci/unity-builder@v2
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
        with:
          unityVersion: 2021.3.0f1
          targetPlatform: ${{ matrix.targetPlatform }}
          buildMethod: BuildScript.Build

      - name: Upload build
        uses: actions/upload-artifact@v3
        with:
          name: Build-${{ matrix.targetPlatform }}
          path: build

  coverage:
    name: Code Coverage
    runs-on: ubuntu-latest
    needs: test

    steps:
      - name: Checkout repository
        uses: actions/checkout@v3

      - name: Run tests with coverage
        uses: game-ci/unity-test-runner@v2
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
        with:
          unityVersion: 2021.3.0f1
          testMode: PlayMode
          coverageOptions: 'generateAdditionalMetrics;generateHtmlReport;generateBadgeReport;assemblyFilters:+TurboHTTP.*'

      - name: Upload coverage report
        uses: actions/upload-artifact@v3
        with:
          name: Coverage Report
          path: CodeCoverage

      - name: Check coverage threshold
        run: |
          COVERAGE=$(grep -oP 'Line Coverage: \K[\d.]+' CodeCoverage/Summary.xml)
          echo "Coverage: $COVERAGE%"
          if (( $(echo "$COVERAGE < 80" | bc -l) )); then
            echo "Coverage is below 80%"
            exit 1
          fi
```

### Task 13.2: Build Script

**File:** `Editor/Build/BuildScript.cs`

```csharp
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TurboHTTP.Editor.Build
{
    /// <summary>
    /// Build script for CI/CD.
    /// </summary>
    public static class BuildScript
    {
        private static readonly string[] Scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        [MenuItem("Build/Build All Platforms")]
        public static void BuildAll()
        {
            Build(BuildTarget.StandaloneWindows64);
            Build(BuildTarget.StandaloneOSX);
            Build(BuildTarget.StandaloneLinux64);
            Build(BuildTarget.iOS);
            Build(BuildTarget.Android);
        }

        public static void Build()
        {
            var targetPlatform = GetBuildTarget();
            Build(targetPlatform);
        }

        private static void Build(BuildTarget target)
        {
            Debug.Log($"Building for {target}...");

            var options = new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = $"build/{target}/{GetBuildName(target)}",
                target = target,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"Build succeeded: {report.summary.totalSize} bytes");
            }
            else
            {
                Debug.LogError($"Build failed: {report.summary.result}");
                EditorApplication.Exit(1);
            }
        }

        private static BuildTarget GetBuildTarget()
        {
            var targetPlatform = Environment.GetEnvironmentVariable("BUILD_TARGET");
            return targetPlatform switch
            {
                "StandaloneWindows64" => BuildTarget.StandaloneWindows64,
                "StandaloneOSX" => BuildTarget.StandaloneOSX,
                "StandaloneLinux64" => BuildTarget.StandaloneLinux64,
                "iOS" => BuildTarget.iOS,
                "Android" => BuildTarget.Android,
                _ => EditorUserBuildSettings.activeBuildTarget
            };
        }

        private static string GetBuildName(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows64 => "TurboHTTP.exe",
                BuildTarget.StandaloneOSX => "TurboHTTP.app",
                BuildTarget.StandaloneLinux64 => "TurboHTTP",
                BuildTarget.iOS => "TurboHTTP-iOS",
                BuildTarget.Android => "TurboHTTP.apk",
                _ => "TurboHTTP"
            };
        }
    }
}
```

### Task 13.3: Asset Store Package Builder

**File:** `Editor/Build/AssetStorePackageBuilder.cs`

```csharp
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor.Build
{
    /// <summary>
    /// Creates the Asset Store submission package.
    /// </summary>
    public static class AssetStorePackageBuilder
    {
        [MenuItem("Build/Export Asset Store Package")]
        public static void ExportPackage()
        {
            var packageName = "TurboHTTP_v1.0.0.unitypackage";
            var outputPath = Path.Combine(Application.dataPath, "..", "AssetStore", packageName);

            // Ensure output directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            // Files to include in package
            var assetPaths = new[]
            {
                "Packages/com.turbohttp.complete",
                "Assets/TurboHTTP" // If there are any assets in the project
            };

            // Export package
            AssetDatabase.ExportPackage(
                assetPaths,
                outputPath,
                ExportPackageOptions.Recurse
            );

            Debug.Log($"Asset Store package exported to: {outputPath}");

            // Open folder
            EditorUtility.RevealInFinder(outputPath);
        }

        [MenuItem("Build/Validate Asset Store Package")]
        public static void ValidatePackage()
        {
            var errors = 0;

            // Check package.json
            if (!File.Exists("Packages/com.turbohttp.complete/package.json"))
            {
                Debug.LogError("package.json not found");
                errors++;
            }

            // Check README
            if (!File.Exists("Packages/com.turbohttp.complete/README.md"))
            {
                Debug.LogError("README.md not found");
                errors++;
            }

            // Check CHANGELOG
            if (!File.Exists("Packages/com.turbohttp.complete/CHANGELOG.md"))
            {
                Debug.LogError("CHANGELOG.md not found");
                errors++;
            }

            // Check documentation
            if (!Directory.Exists("Packages/com.turbohttp.complete/Documentation~"))
            {
                Debug.LogError("Documentation~ folder not found");
                errors++;
            }

            // Check samples
            if (!Directory.Exists("Packages/com.turbohttp.complete/Samples~"))
            {
                Debug.LogError("Samples~ folder not found");
                errors++;
            }

            if (errors == 0)
            {
                Debug.Log("Asset Store package validation: PASSED");
            }
            else
            {
                Debug.LogError($"Asset Store package validation: FAILED ({errors} errors)");
            }
        }
    }
}
```

### Task 13.4: Release Checklist

**File:** `RELEASE_CHECKLIST.md`

```markdown
# Release Checklist for TurboHTTP

Use this checklist before releasing a new version.

## Pre-Release (1 week before)

### Code Quality
- [ ] All unit tests pass on all platforms
- [ ] Integration tests pass with real endpoints
- [ ] Code coverage is 80%+
- [ ] No compiler warnings
- [ ] No TODOs in code
- [ ] Code review completed

### Testing
- [ ] Tested on Unity 2021.3 LTS
- [ ] Tested on Unity 2022.3 LTS
- [ ] Tested on Windows Editor
- [ ] Tested on Mac Editor
- [ ] Tested on Linux Editor
- [ ] Tested on Windows Standalone (Mono and IL2CPP)
- [ ] Tested on Mac Standalone (IL2CPP)
- [ ] Tested on Linux Standalone (IL2CPP)
- [ ] Tested on iOS device (IL2CPP)
- [ ] Tested on Android device (IL2CPP)
- [ ] Stress tested (10,000+ requests)
- [ ] Memory leak tested (no leaks detected)

### Documentation
- [ ] README.md up to date
- [ ] CHANGELOG.md updated with new version
- [ ] API Reference complete
- [ ] Quick Start guide tested
- [ ] All samples working
- [ ] Platform notes updated
- [ ] Troubleshooting guide updated
- [ ] XML documentation complete

### Package
- [ ] package.json version updated
- [ ] Assembly version updated
- [ ] Dependencies verified
- [ ] License file present
- [ ] Third-party notices (if any)

## Release Day

### Build
- [ ] CI/CD pipeline passes
- [ ] Asset Store package exported
- [ ] Package validated
- [ ] Package tested in clean Unity project

### Asset Store Submission
- [ ] Package uploaded to Asset Store
- [ ] Screenshots updated
- [ ] Description updated
- [ ] Video tutorial uploaded (if available)
- [ ] Price confirmed
- [ ] Category confirmed (Network)
- [ ] Keywords updated

### Distribution
- [ ] GitHub release created
- [ ] Release notes published
- [ ] Tag created (v1.0.0)
- [ ] Package published

### Communication
- [ ] Release announcement prepared
- [ ] Support email ready
- [ ] Documentation site updated
- [ ] Social media posts prepared

## Post-Release (1 week after)

### Monitoring
- [ ] No critical bugs reported
- [ ] Support tickets addressed
- [ ] User feedback collected
- [ ] Analytics reviewed

### Documentation
- [ ] Known issues documented
- [ ] FAQ updated based on support questions
- [ ] Troubleshooting guide updated

## Version Numbers

TurboHTTP uses Semantic Versioning:

- **Major (X.0.0):** Breaking changes
- **Minor (1.X.0):** New features, backwards compatible
- **Patch (1.0.X):** Bug fixes, backwards compatible

Current version: **1.0.0**
Next version: ______
```

### Task 13.5: Versioning Strategy

**File:** `Editor/Settings/VersionInfo.cs`

```csharp
using UnityEngine;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Version information for TurboHTTP.
    /// </summary>
    public static class VersionInfo
    {
        public const string Version = "1.0.0";
        public const string ReleaseDate = "2024-01-15";
        public const string MinUnityVersion = "2021.3";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void LogVersion()
        {
            Debug.Log($"[TurboHTTP] Version {Version} ({ReleaseDate})");
        }

        public static void CheckCompatibility()
        {
#if !UNITY_2021_3_OR_NEWER
            Debug.LogError($"TurboHTTP requires Unity {MinUnityVersion} or newer");
#endif
        }
    }
}
```

### Task 13.6: Asset Store Metadata

**File:** `AssetStore/Description.txt`

```text
TurboHTTP - Complete HTTP Client for Unity

A production-grade, modular HTTP client for Unity with advanced features designed for games and applications requiring reliable network communication.

KEY FEATURES:

✓ Modular Architecture - Use only the modules you need
✓ Cross-Platform - Editor, Standalone, iOS, Android (WebGL in v1.x)
✓ Advanced Retry Logic - Intelligent retries with idempotency awareness
✓ HTTP Caching - ETag-based caching for optimized bandwidth
✓ Timeline Tracing - Detailed observability for every request
✓ File Downloads - Resume support and integrity verification
✓ Unity Integration - Native support for Texture2D, AudioClip, and more
✓ Testing Tools - Record/replay mode for deterministic testing
✓ Editor Monitor - Inspect HTTP traffic directly in Unity Editor
✓ High Performance - Memory pooling, <1KB GC per request
✓ Production Ready - 80%+ code coverage, comprehensive tests

MODULES:

• Core - HTTP client, request/response types, pipeline
• Retry - Automatic retries with exponential backoff
• Cache - HTTP caching with ETag support
• Auth - Authentication middleware
• RateLimit - Rate limiting per host
• Observability - Timeline tracing and metrics
• Files - File downloads with resume support
• Unity - Texture2D, AudioClip, Sprite handlers
• Testing - Record/replay and mock transports
• Performance - Memory pooling and concurrency control
• Editor - HTTP Monitor window

DOCUMENTATION:

• Quick Start Guide - Get started in under 5 minutes
• API Reference - Complete API documentation
• 5 Sample Projects - Real-world examples
• Platform Notes - iOS, Android, IL2CPP guides
• Troubleshooting Guide - Common issues and solutions

REQUIREMENTS:

• Unity 2021.3 LTS or higher
• .NET Standard 2.1

SUPPORT:

• Email: support@yourcompany.com
• Documentation: Included in package
• Samples: 5 complete examples

Perfect for REST APIs, file downloads, authentication, and any HTTP communication in Unity!
```

**File:** `AssetStore/KeyWords.txt`

```
HTTP, REST, API, networking, client, request, download, upload, JSON, cache, retry, authentication, OAuth, token, middleware, async, await, WebRequest, HttpClient, networking, web, internet, online, multiplayer, backend
```

## Validation Criteria

### Success Criteria

- [ ] CI/CD pipeline passes all tests
- [ ] Builds successfully on all platforms
- [ ] Asset Store package exports without errors
- [ ] Package validates successfully
- [ ] Release checklist complete
- [ ] Documentation ready
- [ ] Support infrastructure ready

### Pre-Submission Checklist

- [ ] Clean Unity project imports package successfully
- [ ] All samples work in clean project
- [ ] No console errors
- [ ] File size under 10MB
- [ ] Screenshots high quality
- [ ] Video tutorial (optional but recommended)

## Next Steps

Once Phase 13 is complete and validated:

1. Submit to Unity Asset Store
2. Wait for approval (typically 1-2 weeks)
3. Publish when approved
4. Monitor for issues
5. Plan v1.1 features (Phase 14)
6. M3 milestone **COMPLETE** - v1.0 shipped!

## Notes

- Asset Store review takes 1-2 weeks
- Be responsive to reviewer feedback
- First release is most scrutinized
- Have support infrastructure ready before launch
- Monitor closely in first week
- Collect user feedback for v1.1
