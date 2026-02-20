# How To Run Tests With A Temporary Unity Project

This document describes the exact workflow used in this repository to:

1. Sync the current package source into a temporary folder.
2. Run Unity PlayMode and EditMode tests from a separate Unity test project.

## Why This Setup

- It avoids importing the package directly from the working repo.
- It keeps Unity-generated files (`Library`, `Temp`, `.meta` churn) out of your main package workspace.
- It matches how a local file package dependency is consumed.

## Paths Used

- Package repo (this workspace): `/Users/arturkoshtei/workspace/turboHTTP`
- Synced package copy: `/tmp/turboHTTP-package`
- Unity test project: `/Users/arturkoshtei/workspace/turboHTTP-testproj`
- Unity binary: `/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity`

## One-Time Setup (Temp Unity Project)

In the Unity test project `Packages/manifest.json`, ensure dependencies include:

```json
{
  "dependencies": {
    "com.turbohttp.complete": "file:/tmp/turboHTTP-package",
    "com.unity.test-framework": "1.1.33"
  },
  "testables": [
    "com.turbohttp.complete"
  ]
}
```

If your package uses Unity modules (audio, webrequest, image conversion), keep those modules in `dependencies` as well.

## Step 1: Sync Current Code To Temp Package

Run from the package repo root:

```bash
rsync -a --delete \
  --exclude='.git/' \
  --exclude='Library/' \
  --exclude='Temp/' \
  --exclude='Logs/' \
  --exclude='Obj/' \
  --exclude='.DS_Store' \
  /Users/arturkoshtei/workspace/turboHTTP/ /tmp/turboHTTP-package/
```

## Step 2: Run PlayMode Tests

```bash
TS=$(date +%Y%m%d-%H%M%S)
UNITY="/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity"
PROJ="/Users/arturkoshtei/workspace/turboHTTP-testproj"
LOG="/Users/arturkoshtei/workspace/turboHTTP-testproj/unity-test-all-playmode-sync-${TS}.log"
XML="/Users/arturkoshtei/workspace/turboHTTP-testproj/test-results-all-playmode-sync-${TS}.xml"

"$UNITY" -batchmode -nographics \
  -projectPath "$PROJ" \
  -runTests -testPlatform PlayMode \
  -testResults "$XML" \
  -logFile "$LOG"
```

## Step 3: Run EditMode Tests

Important: run EditMode **without** `-nographics` if editor-window tests exist.

```bash
TS=$(date +%Y%m%d-%H%M%S)
UNITY="/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity"
PROJ="/Users/arturkoshtei/workspace/turboHTTP-testproj"
LOG="/Users/arturkoshtei/workspace/turboHTTP-testproj/unity-test-all-editmode-sync-${TS}.log"
XML="/Users/arturkoshtei/workspace/turboHTTP-testproj/test-results-all-editmode-sync-${TS}.xml"

"$UNITY" -batchmode \
  -projectPath "$PROJ" \
  -runTests -testPlatform EditMode \
  -testResults "$XML" \
  -logFile "$LOG"
```

## Step 4: Read Result Summary Quickly

```bash
python3 - <<'PY'
import xml.etree.ElementTree as ET
xml = "/path/to/test-results.xml"
root = ET.parse(xml).getroot()
print({k: root.attrib.get(k) for k in [
    "total","passed","failed","inconclusive","skipped","result","duration"
]})
PY
```

## Common Issues

### 1) `Scripts have compiler errors`

- Open the log file and search for `error CS`.
- Fix package compile errors, sync again, rerun.

### 2) EditMode failure: `No graphic device is available to initialize the view`

- Cause: editor window tests in `-nographics`.
- Fix: run EditMode without `-nographics`.

### 3) Intermittent Unity/Mono crash in PlayMode

- Example: `threadpool-io-poll` assertion.
- Rerun after sync. If persistent, keep the failing log and isolate the failing test group with `-testFilter`.

### 4) EditMode shows `0 tests`

- Check test assembly definition and whether any editor test scripts are included.

## Recommended Run Order

1. Sync to `/tmp/turboHTTP-package`
2. PlayMode run
3. EditMode run
4. Review XML summaries and logs

