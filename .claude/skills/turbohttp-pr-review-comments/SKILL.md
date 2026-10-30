---
name: turbohttp-pr-review-comments
description: >
  Address review comments on an open fatal10110/turboHTTP pull request (human reviewer, the
  turbohttp-waves review agent, or an automated reviewer): fetch every unresolved inline thread
  and top-level review body, verify each claim against the code, the RFC and the phase docs before
  touching anything, fix only confirmed in-scope findings with regression tests, reply with
  evidence to the rest, file follow-ups for valid out-of-scope items, and resolve threads. Use when
  the user says "address the review on PR N", "fix the comments", or when a turbohttp-waves
  implementation prompt hands over a review round. Do not use to produce a review
  (turbohttp-pr-review).
---

# turboHTTP review-comment handling

Verify first, fix second. A reviewer's prose is a claim, not a fact. A turbohttp-waves prompt
overrides this file on test scope, worktrees and GitHub tooling (a waves review round arrives as a
review file plus the PR's threads).

## 0. What comments are ranked against

Protocol correctness (RFCs), security (validation, credential scope, bounded input), IL2CPP/mobile
safety, concurrency and ownership outrank everything else (see turbohttp-issue "Authority"). A
comment that names one of them is never downgraded to style. While you are in the file verifying a
comment, check those properties on the surrounding changed lines too; a defect the reviewer missed
is still yours.

## 1. Fetch threads and reviews

```bash
gh pr view <N> -R fatal10110/turboHTTP --json number,headRefName,headRefOid,baseRefName
gh api graphql --paginate -F n=<N> -f query='
  query($n: Int!, $endCursor: String) { repository(owner:"fatal10110", name:"turboHTTP") {
    pullRequest(number: $n) { reviewThreads(first: 100, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes { id isResolved isOutdated
        comments(first: 100) { totalCount nodes { id body path line author { login } } } } } } } }' \
  > <scratchpad>/pr<N>-threads.json
gh api graphql --paginate -F n=<N> -f query='
  query($n: Int!, $endCursor: String) { repository(owner:"fatal10110", name:"turboHTTP") {
    pullRequest(number: $n) { reviews(first: 100, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes { id databaseId state body author { login } } } } } }' \
  > <scratchpad>/pr<N>-reviews.json
```

Only unresolved threads on the current head are work items (re-locate `isOutdated` ones before
dismissing them). Every non-empty review body with actionable content is also a work item.

## 2. Verify every comment before classifying it

- Open the file:line the comment cites and confirm the code does what it claims.
- A protocol claim: open the RFC section yourself; never carry a citation you have not read into a
  commit, reply or PR text.
- A race/ordering/lost-wakeup/use-after-return claim: write out the concrete interleaving (which
  thread holds what at each step, and the resulting bytes/state) or reproduce it with a failing
  test. Rejecting it needs the same rigor: name the lock, the single-threaded owner, or the barrier
  that makes the interleaving impossible, at every call site.
- A security or limits claim: state reachability concretely (what a hostile peer sends, how often,
  what it gains or what the client exposes).
- An IL2CPP/platform claim: check the API against .NET Standard 2.1 / Unity 2021.3 and the stripping
  setup; if it needs a device to settle, say so and track it (`needs-device-validation`).

Classify only after verification:

- **Valid, in-scope** — fix it. A protocol break, race, leak or security hole the PR introduces or
  widens is always in scope.
- **Valid, out-of-scope** — pre-existing, not worsened by the PR: reply with evidence and the issue
  filed/amended in §4a; do not fix here.
- **Invalid** — citation wrong, behavior already correct, or fixed by a later commit: reply with the
  refuting evidence; do not fix.
- **Valid, latent** — real defect nothing reaches today: file it (§4a) and reply.
- **Question** — answer it.

## 3. Fix confirmed in-scope findings

Work in the PR branch's worktree (never the primary checkout). Smallest complete fix per finding; a
regression test that fails on the old code for every behavior fix, in the hostile direction for
concurrency/protocol/security fixes. Then:

```bash
cd <worktree> && './Tools~/ci/check.sh'
cd <worktree> && './Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter '<affected tests>'   # when UNITY_PATH is set
```

Re-apply the specialist rubric checklist that covers the finding's area. Update the PR's journal
file if a decision changed. Commit messages name the behavior fixed (not "address review"); push
(never force-push).

## 4. Reply and resolve

### 4a. Track out-of-scope and latent findings

Dedupe with `'Tools~/agent-tools/issue_search.py' <terms>` (or search_issues), amend a matching
issue instead of duplicating, otherwise file one with the repository template and the owning
milestone (see turbohttp-issue "Follow-up issues"). Link back to the thread.

### 4b. Reply on every thread, then resolve

- Fixed: reply with what changed and the commit SHA, then resolve
  (`gh api graphql -f query='mutation { resolveReviewThread(input:{threadId:"<id>"}) { thread { isResolved } } }'`).
- Out-of-scope or latent: reply with evidence and the issue number, then resolve.
- Invalid: reply with the specific evidence (file:line, RFC section, the later commit), then resolve.
  Never resolve without a reply.
- A top-level review body: one PR comment answering it point by point.
- Push before resolving threads that reference a fix.

## 5. Report back

Which comments were fixed (commit refs), rejected (evidence), or tracked (issue numbers); the gates
run and their results; and the three answers (wire/behavior, concurrency, abuse/limits) for the
branch as it now stands.
