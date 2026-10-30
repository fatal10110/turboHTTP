# Issue seed schema

Each `issues/*.json` file is a JSON array of objects:

```json
{
  "key": "H2-01",                       // unique stable id, prefix per file
  "title": "[HTTP/2] Short imperative/defect title",
  "milestone": "<one of the milestone titles below, exact>",
  "labels": ["type:bug", "area:http2", "priority:P0", "source:review-2026-10"],
  "projects": []                        // optional; normally empty, script derives projects
}
```
plus `"body"`: GitHub markdown string.

## Milestones (exact titles)
- `v1.0-alpha: Release blockers`
- `v1.0-beta: Protocol & correctness hardening`
- `v1.0 GA: Docs, validation & release`
- `v1.1: Post-release hardening`
- `v1.2: Features & performance`
- `v2.0: Advanced protocols`

## Labels (use only these)
- type: `type:bug` `type:security` `type:enhancement` `type:docs` `type:chore` `type:test` `type:phase` `type:plan` `type:architecture`
- area: `area:tls` `area:http1` `area:http2` `area:transport` `area:proxy` `area:websocket` `area:core` `area:cache` `area:auth` `area:retry` `area:files` `area:middleware` `area:observability` `area:unity` `area:mobile` `area:json` `area:ratelimit` `area:testing` `area:editor` `area:packaging` `area:docs` `area:ci`
- priority: `priority:P0` (release blocker / critical) `priority:P1` (high) `priority:P2` (medium) `priority:P3` (low)
- source: `source:review-2026-10` (finding from the 2026-10 review) `source:roadmap` (existing plan phase)
- optional: `platform:il2cpp` `needs-device-validation` `confidence:plausible`

## Body template (findings)
```
## Summary
One or two sentences.

## Location
- `path/File.cs:line` — what is there

## Problem
What goes wrong, concrete failure scenario.

## Spec reference
RFC section(s), if applicable.

## Suggested fix
Concrete steps.

## Acceptance criteria
- [ ] behavior change
- [ ] regression test that fails before the fix

## Context
Source: 2026-10 full review (`Development/docs/review-2026-10-full-library-review.md`). Confidence: Confirmed | Plausible (needs device/runtime confirmation).
```

## Project derivation (done by script)
- milestone v1.0-* -> "TurboHTTP v1.0 Release"
- milestone v1.1/v1.2/v2.0 -> "TurboHTTP Post-1.0 Roadmap"
- `type:security`, or `type:bug` with area in {tls, http1, http2, transport, proxy, websocket} -> also "TurboHTTP Protocol & Security"
