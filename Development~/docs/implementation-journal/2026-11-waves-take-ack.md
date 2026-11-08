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
  and the helper broker track the ids of the last take that returned. The stop confirmation acks
  none, because it follows a take without events.
- `.claude/skills/turbohttp-waves/SKILL.md`: one paragraph on the brokers' poller and `--ack`.
- Tests: `Tools~/agent-tools/test_wave_mail.py` (4 cases) and `waves_sim.mjs` (the prompts carry
  `--requeue`, then `--ack m1`, then `--ack -`).

## Decisions

- **Acknowledge delivery, not completion.** A helper request can run for 40+ minutes while the
  helper broker keeps polling, so a time-based lease on `taken/` would re-serve live work. The script
  knows exactly which ids it received, and that is the fact the poller can lose.
- **A failed take agent leaves `acked` unchanged.** If it never ran `take`, the ledger still holds
  the previous ids and they are acknowledged again. If it ran `take` and then died, whatever it
  received is not in the ack list and is served again.
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
