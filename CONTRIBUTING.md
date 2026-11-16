# Contributing to TurboHTTP

Thank you for your interest in contributing to TurboHTTP!

## License of contributions

TurboHTTP is open source under the [MIT License](LICENSE.md). By submitting a contribution (a pull
request, a patch or any other material) you agree that it is licensed under the MIT License, the same
terms as the project (inbound = outbound). There is no contributor license agreement to sign.

## Reporting Bugs

If you encounter a bug, please create a new issue and include:

*   **Steps to reproduce:** A minimal, reproducible example is highly appreciated.
*   **Expected behavior:** What you expected to happen.
*   **Actual behavior:** What actually happened.
*   **Environment:** Unity version, platform (Editor, Standalone, iOS, Android), scripting backend (Mono/IL2CPP).
*   **Logs:** Relevant stack traces or logs.

## Feature Requests

We welcome feature requests! Please describe the feature you'd like to see, why it's useful, and any potential implementation ideas.

## Pull Requests

1.  **Fork the repository.**
2.  **Create a branch:** `git checkout -b feature/my-feature` or `fix/issue-number`.
3.  **Make your changes.** Ensure code follows the existing style.
4.  **Run the local gate:** `'./Tools~/ci/check.sh'` before every push (see Coding Standards). Add a
    deterministic NUnit test under `Tests/Runtime/<Area>/` for every behavior change.
5.  **Commit:** logical commits with clear messages.
6.  **Push:** `git push origin my-branch`.
7.  **Open a Pull Request.** Describe your changes and reference any related issues. For a user-visible
    change (new or changed public API, behavior, defaults, a breaking change or a notable fix) add a line
    to the `## [Unreleased]` section of `CHANGELOG.md` in the same PR, under Added, Changed, Fixed or
    Breaking, with the issue number. Docs-only and test-only PRs need no entry.

## Coding Standards

*   Use standard C# naming conventions.
*   Keep methods focused and concise.
*   Write unit tests for new functionality.
*   Document public APIs with XML comments (CS1591 is enforced for Core and Transport).

## Local checks

Run `'./Tools~/ci/check.sh'` before every push. It checks repository invariants, `dotnet format` on
changed C# files and a shadow `dotnet build` of every runtime assembly with analyzers. Flags:

*   `--full`: also the iOS/Android shadow builds and the compile-only opt-in define variant; run it before
    opening a PR.
*   `--fix`: apply whitespace formatting instead of only verifying it.
*   `--no-unitask`: offline run that skips the pinned UniTask fetch (the UniTask adapters are not built).

If it reports missing Unity managed DLLs, run `'./Tools~/ci/fetch-unity-dlls.sh'` once (needs Docker).
A new analyzer warning is fixed, not added to `Tools~/ci/analyzer-baseline.txt`.

Gates worth knowing:

*   **`.meta` files.** Unity needs a committed `.meta` for every file or folder it imports. Create one with
    `python3 'Tools~/ci/gen_meta.py' <path>`; the invariants fail without it.
*   **Core public API baseline.** The public types of `TurboHTTP.Core` must match
    `Tools~/ci/api-baseline/core-public-types.txt` (`python3 'Tools~/ci/check_repo.py' coreapi`, at most
    60 types). Prefer `internal`; a deliberate public addition updates the baseline and is called out in
    the PR.
*   **Doc snippets.** C# blocks in `Documentation~/` and `README.md` are extracted by
    `python3 'Tools~/ci/doc_snippets.py'` and compiled by the `TurboHTTP.DocSnippets` project in the shadow
    build of `check.sh`; a snippet that does not compile fails the gate.
*   **Unity tests do not run in PR CI** (no license, #188). Run only the tests your change can affect with
    `'./Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter <regex>`; the full suites run
    periodically in a host editor. Details: `Development~/docs/agents/tooling.md` and
    `Development~/docs/agents/testing.md`.
