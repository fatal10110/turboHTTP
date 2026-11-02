# 2026-11 Open-source licence, package metadata and third-party notices (#4, #3, #5)

## What

Owner decision (2026-11-02): TurboHTTP is MIT open source, not company-owned. The commercial-licence
text, template package metadata and incomplete third-party notices are replaced.

## Files

- `LICENSE.md`: standard MIT text, `Copyright (c) 2026 fatal10110 and TurboHTTP contributors`.
- `CONTRIBUTING.md`: "License of contributions" section near the top (MIT, inbound = outbound, no CLA);
  the trailing generic licence section is gone; heading `Coding Standards` fixed.
- `package.json`: version `1.0.0-pre.1`, license `MIT`, author `fatal10110` + GitHub URL (no email),
  `documentationUrl`, `changelogUrl`, `licensesUrl`, `repository`; keyword `middleware` dropped.
- `CHANGELOG.md`: top entry `[1.0.0-pre.1] - 2026-11-02` (the former Unreleased content) with a module list
  limited to existing assemblies; the stale `[1.0.0] - TBD` block (Performance module, "middleware") is gone.
- `README.md`: MIT badge, no "Enterprise Security" wording, `MockTransport` instead of "Mock Server",
  License section points to `LICENSE.md` and the notices.
- `Third-Party Notices.md`: package licence statement, identical copyright line for the Turbo-owned block,
  and a "Not bundled" part: decoder adapters (NLayer, NVorbis, StbImageSharp), UniTask, the Gradle
  `androidx.work:work-runtime:2.7.1` build dependency, own-code areas.
- `Tools~/ci/check_repo.py` (`check_json`): fails on placeholder strings in `package.json` and when its
  version differs from the top CHANGELOG entry (makes the #3 criteria machine-checked).

## Decisions

- Standard MIT keeps the word "sublicense", so the old `grep -ci sublicense == 0` criterion of #4 no longer
  applies; the legal-entity, legal-review, CLA and visibility criteria were dropped by the owner decision
  (repository is public, MIT).
- Licences of NLayer, NVorbis, StbImageSharp and UniTask are not restated (not verifiable offline, not
  bundled); the notices point to the upstream repositories.
- BouncyCastle provenance (upstream commit, strip commit) was already recorded by #141 in
  `Third-Party Notices.md` and `Runtime/Transport/BouncyCastle/README.md`; not redone.

## Deferrals

- Shipping the notices in every release artifact and the release-checklist step: #101 (release-layout lane).
- Asset Store submission criteria of #5: dropped (not an Asset Store product).
- Version policy beyond `1.0.0-pre.1`: #10.

## Verification

`'./Tools~/ci/check.sh'` (see the PR). Unity not run: docs and metadata only; no IL2CPP or device run. No
NUnit test: no behavior change.
