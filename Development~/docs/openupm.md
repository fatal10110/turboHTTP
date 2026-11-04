# OpenUPM submission

How to list `com.turbohttp.complete` on [OpenUPM](https://openupm.com) (#101). Submitting is an owner
action and has not been done yet. Once the package is listed, OpenUPM builds every new semver tag on its own.

## Requirements (all met by the repository)

- A public GitHub repository with `package.json` at the root: `https://github.com/fatal10110/turboHTTP`.
- A valid UPM `name` (`com.turbohttp.complete`), `version`, `unity` (`2021.3`) and an SPDX license (`MIT`).
- Git tags in semver form. The tags are `vX.Y.Z[-pre.N]` ([`release-checklist.md`](release-checklist.md)).
  OpenUPM reads the version from `package.json` at each tag, so every tag must match its `package.json`.
  The Release workflow enforces this.
- At least one tag pushed before submission (the first is `v1.0.0-pre.1`).
- Package contents filtered by `.npmignore`. Preview them with `npm pack --dry-run` from the repository root.

## Submit

1. Open the package form at `https://openupm.com/packages/add/` and enter the repository URL. The form
   fills in the package name, display name and license from `package.json`. Or open a pull request against
   [`openupm/openupm`](https://github.com/openupm/openupm) that adds `data/packages/com.turbohttp.complete.yml`:

   ```yaml
   name: com.turbohttp.complete
   displayName: TurboHTTP - Complete HTTP Client
   description: HTTP/1.1, HTTP/2 and WebSocket client for Unity on raw sockets, with retry, caching, observability and Unity asset handlers.
   repoUrl: 'https://github.com/fatal10110/turboHTTP'
   parentRepoUrl: null
   licenseSpdxId: MIT
   licenseName: MIT License
   topics:
     - network
   hunter: fatal10110
   gitTagPrefix: ''
   gitTagIgnore: ''
   minVersion: '1.0.0-pre.1'
   image: null
   createdAt: <epoch milliseconds, filled by the form>
   ```

   Copy the field set from a recent entry in `data/packages/` when you open the PR. The form and the schema
   may change, and OpenUPM's own guide (`https://openupm.com/docs/adding-upm-package.html`) is authoritative.
2. After the PR merges, the OpenUPM build pipeline packs each matching tag and publishes it to
   `https://package.openupm.com`. Check the result at `https://openupm.com/packages/com.turbohttp.complete/`.
3. Update the README "Installation" section: drop "once the package is listed".

## Install (for users)

```text
openupm add com.turbohttp.complete
```

Or add the scoped registry to `Packages/manifest.json` by hand. The README shows the snippet.

## Pre-releases

OpenUPM lists `-pre.N` versions as pre-releases. Unity's Package Manager shows them when
**Enable Pre-release Packages** is on (Project Settings > Package Manager). Pinning one in `manifest.json`
always works.
