# 2026-11 — waves brokers: re-serve requests a poller dropped (`take --ack`)

## What

In run w1110 the merger's Haiku poller ran `wave_mail.py take`, got the merge request for PR #398,
kept polling against its instructions, and returned "no events". The request stayed in
`requests/taken/` with no answer. `take` re-served such requests only on a broker's first call
(`--requeue`), so nothing recovered it. The lane's review and implementation agents waited on it
for about three hours, holding two of the session's agent slots. That kept PR #399 from getting a
review agent, and the other track waited at the wave barrier. Moving the request back to `new/` by
hand let it merge within minutes.

`take` now keeps a per-broker delivery ledger (`requests/delivered-<broker>.json`). `waves.js`
passes the ids it received from the previous poller as `--ack` on every later `take` (`-` for
none). Each delivered id that is not acknowledged and has no answer goes back to `new/`, and that
same `take` serves it again.

## Files

- `Tools~/agent-tools/wave_mail.py`: `delivery_ledger`, `requeue_unacked`; `take --ack`. `--requeue`
  clears the ledger (it re-serves every unanswered request anyway).
- `.claude/skills/turbohttp-waves/waves.js`: `takePrompt(broker, first, once, acked)`. The merger
  and the helper broker track the ids of the last take that returned events, and the stop
  confirmation passes the same ids.
- `.claude/skills/turbohttp-waves/SKILL.md`: one paragraph on the brokers' poller and `--ack`.
- Tests: `Tools~/agent-tools/test_wave_mail.py` (4 cases) and `waves_sim.mjs`. The merger's
  prompts carry `--requeue`, then `--ack m1` on the next take and on the stop confirmation. After a
  failed take, an empty report or a fake stop, the merger and the helper broker keep acking the last
  received ids. New events replace them.

## Decisions

- **Acknowledge delivery, not completion.** A helper request can run for 40+ minutes while the
  helper broker keeps polling, so a time-based lease on `taken/` would re-serve live work. The script
  knows exactly which ids it received, and that is the fact the poller can lose.
- **Only a take that returned events changes `acked`.** A poller that fails, reports no events or
  reports a stop may never have run `take`. The ledger then still holds the previous ids, and acking
  none would re-serve requests that are in flight. In the helper broker that means a second runner
  or Unity run in the same worktree. So `acked` stays as it was, and the stop confirmation passes it
  too (review round 1 on PR #400). A repeated ack does no harm, because every take that does run
  resets the ledger. If the poller did run `take` and dropped what it received, those ids are not
  in the ack list and are served again.
- **A disobedient poller is covered too.** If it runs `take` again after receiving events, that run
  re-serves them at once, so the run it reports carries them.
- **No `--ack`, old behaviour.** A take without `--ack` neither re-serves nor fails, so a run
  started with the old script keeps working.

## How verified

- `python3 -m unittest discover -s 'Tools~/agent-tools' -p 'test_*.py'`: the new `test_wave_mail`
  cases pass. On `origin/main`'s `wave_mail.py` all four fail (no `--ack`).
- `node .claude/skills/turbohttp-waves/waves_sim.mjs`: all scenarios pass. Against `origin/main`'s
  `waves.js` the new assertion fails (`--ack m1` missing from the second take).
- `'./Tools~/ci/check.sh'`: see the PR.
- Not run: a live waves run with the change (run w1110 still uses the `main` copy).
