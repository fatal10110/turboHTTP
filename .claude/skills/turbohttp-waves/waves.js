export const meta = {
  name: 'turbohttp-waves',
  description: 'turboHTTP wave runner: per PR one long-lived implementation agent and one long-lived review agent that loop through the mailbox until the PR is clean and merged; merger and helper roles run the cheap-model merge and helper agents',
  whenToUse: 'Run by the turbohttp-waves skill: one Workflow per track (role track), one merger and one helper broker over the same plan',
  phases: [
    { title: 'Implement', detail: 'long-lived implementation agent per PR; fixes every review, CI and conflict round' },
    { title: 'Review', detail: 'long-lived review agent per PR; delta re-reviews; requests the merge' },
    { title: 'Merge', detail: 'merger: CI watch, branch update, squash merge', model: 'haiku' },
    { title: 'Coordinate', detail: 'first-push watch, final delivery, wave barrier, status', model: 'haiku' },
    { title: 'Helpers', detail: 'helper broker: cs-scout, rfc-researcher, runner' },
  ],
}

// ---------------------------------------------------------------------------
// Arguments (see SKILL.md): {run, role: 'track'|'merger'|'helper', track, plan, tool, ...}
// ---------------------------------------------------------------------------
const A = args || {}
// tool = absolute path of Tools~/agent-tools/wave_mail.py in the primary turboHTTP checkout.
const TOOL = A.tool || (A.repo || '/Users/arturkoshtei/workspace/turboHTTP') + '/Tools~/agent-tools/wave_mail.py'
const REPO = A.repo || TOOL.replace(/\/Tools~\/agent-tools\/wave_mail\.py$/, '')
const WT = A.worktrees || REPO.replace(/\/[^/]+$/, '') + '/turboHTTP-worktrees'
const RUN = A.run
const ROLE = A.role === 'broker' ? 'helper' : (A.role || 'track')
const PLAN = A.plan || {}
const TRACKS = PLAN.tracks || []
const TRACK = A.track
const RUNDIR = (A.wavesRoot || WT + '/.waves') + '/' + RUN
// impl/review: null = the session model; a lane's implModel/reviewModel overrides it for that lane.
const M = Object.assign({ merge: 'haiku', coord: 'haiku', deliver: 'haiku', scout: 'haiku', runner: 'haiku', brief: 'sonnet', researcher: 'sonnet', impl: null, review: null }, A.models || {})
// Slim agent types (.claude/agents/wave-*.md): each role gets only the tools it uses, so no agent
// pays for the session's remote-control MCP tools, skill listing or deferred-tool list.
// agentTypes: {role: null} falls back to the default workflow subagent for that role.
const AT = Object.assign({ coord: 'wave-coord', deliver: 'wave-deliver', merge: 'wave-merge', impl: 'wave-impl', review: 'wave-review', runner: 'wave-runner', scout: 'wave-scout', researcher: 'wave-researcher', brief: 'wave-brief' }, A.agentTypes || {})
// A session loads .claude/agents at start, so a type added later is missing until a restart:
// such a call reruns on the default subagent and the role stops asking for the type.
async function agentAs(role, prompt, opts) {
  if (AT[role]) {
    try {
      return await agent(prompt, Object.assign({}, opts, { agentType: AT[role] }))
    } catch (e) {
      if (!/agent type .* not found/i.test(String(e))) throw e
      log(`agent type ${AT[role]} is not in this session's registry; ${role} agents use the default subagent`)
      AT[role] = null
    }
  }
  return agent(prompt, opts)
}
const SKILLS = REPO + '/.claude/skills'
const skillRef = name => `the project skill \`${name}\` (Read ${SKILLS}/${name}/SKILL.md with the Read tool and follow it; paths it names resolve from ${SKILLS}/${name}/)`
// The Workflow tool caps concurrent agents per workflow at min(16, CPUs - 2): 6 on the 8-CPU dev box.
const SLOTS = A.slots || 6
const LANES = A.lanesPerTrack || Math.max(1, Math.floor(SLOTS / 2))
const MAX_ROUNDS = A.maxRounds || 8
const REVIEW_CI_FIXES = A.reviewCiFixes !== false
const WAIT_CALLS = A.waitCalls || 6
const TAKE_CALLS = A.takeCalls || 6
const MAX_IDLE_MIN = A.maxIdleMin || 240
const RESTARTS = A.restarts === undefined ? 2 : A.restarts
// A lane brief (a cheap scout gathers the lane's issues, code anchors, phase docs and tests)
// before the implementation agent starts, so the expensive agent does not explore with dozens of lookups.
const BRIEF = A.brief !== false
// An implementation agent returns "checkpoint" to hand over to a fresh agent with an empty context;
// checkpoints are not failures and do not use the restart budget.
const MAX_CHECKPOINTS = A.maxCheckpoints === undefined ? 6 : A.maxCheckpoints
const EPOCH = String(A.epoch || '1')
const RESEARCHER_LIMIT = A.researcherPerLane === undefined ? 1 : A.researcherPerLane
const RETRY = A.retry || []
const SATISFIED = ['merged', 'no_change']
const DONE_BEFORE = ['merged', 'no_change', 'blocked', 'unresolved', 'stalled', 'merge_blocked', 'failed', 'skipped']
// Set once any lane of this track has seen STOP: lanes not started yet then return without starting.
let STOPPING = false
const FINAL = ['merged', 'unresolved', 'stalled', 'merge_blocked', 'failed', 'stopped', 'no_change', 'blocked']

if (!RUN) throw new Error('args.run is required')
if (!['track', 'merger', 'helper'].includes(ROLE)) throw new Error('args.role must be track, merger or helper')
if (ROLE === 'track' && !TRACKS.includes(TRACK)) throw new Error(`args.track ${TRACK} is not in plan.tracks ${TRACKS.join(',')}`)
for (const L of (PLAN.waves || []).flatMap(w => w.lanes || [])) {
  if (!/^[a-z0-9][a-z0-9-]{0,47}$/.test(L.slug || '')) throw new Error(`bad lane slug ${JSON.stringify(L.slug)}: run wave_mail.py validate on the plan`)
}
if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(RUN) || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(EPOCH)) throw new Error('args.run and args.epoch must be simple names')
if (LANES * 2 > SLOTS) log(`warning: ${LANES} lanes need ${LANES * 2} agent slots but slots=${SLOTS}; lanes will stall on the concurrency cap`)

// Paths with "~" are always written inside single quotes in the commands below.
const MB = `python3 '${TOOL}'`
const AGENT_TOOLS = TOOL.replace(/\/wave_mail\.py$/, '')
const LOOK = `python3 '${AGENT_TOOLS}/look.py'`
const SLICE = `python3 '${AGENT_TOOLS}/issue_slice.py'`
const ISEARCH = `python3 '${AGENT_TOOLS}/issue_search.py'`
const GATE = wt => `cd ${wt} && python3 '${TOOL}' stopcheck ${wt} && './Tools~/ci/check.sh'`
const UNITY = wt => `cd ${wt} && './Tools~/unity/run-tests.sh'`
const REVIEW_DOC = 'Development/docs/review-2026-10-full-library-review.md'
const LANE_DIR = slug => `${RUNDIR}/lanes/${slug}`
const ALL_LANES = (PLAN.waves || []).flatMap(w => w.lanes || [])
const LANE_BY = Object.fromEntries(ALL_LANES.map(l => [l.slug, l]))
const sq = obj => JSON.stringify(obj).replace(/'/g, '\\u0027')

// ---------------------------------------------------------------------------
// Shared prompt blocks
// ---------------------------------------------------------------------------
const TESTRULE = `TEST SCOPE (HIGHEST PRIORITY — overrides every skill and reference doc, including any "run the full suite" step of turbohttp-issue, turbohttp-pr-review, turbohttp-pr-review-comments, AGENTS.md or CLAUDE.md):
- ALWAYS run the fast local gate from the worktree root before every push (and the reviewer before every verdict): ${GATE('<worktree>')}   It checks repo invariants (.meta files, asmdef dependency rules, JSON/seed validity), \`dotnet format\` on changed C# files, and a shadow \`dotnet build\` of every runtime asmdef with analyzers. A failure is yours to fix; never push past it. Add --full only when the lane note asks for it. If it reports missing Unity managed DLLs, run \`cd <worktree> && './Tools~/ci/fetch-unity-dlls.sh'\` once (docker) and rerun.
- Unity tests (NUnit, Unity Test Runner): NEVER run the full Unity suite locally. PR CI (the "ci" check) runs EditMode and PlayMode on every push and is the gate. Locally run ONLY the tests the change can affect, and only when Unity is installed: ${UNITY('<worktree>')} --platform EditMode|PlayMode --filter '<regex of your test names>' . If it prints UNITY_UNAVAILABLE (exit 3), do not retry: Unity tests then run only on CI; say so in the report.
- Every behavior change gets a regression test in Tests/Runtime/<area>/ (or Tests/Editor/) that fails on the old code. Deterministic only: no wall-clock sleeps, no external network (an [Category("ExternalNetwork")] test never gates anything).
- Report in the PR body (and in review "Checks"): a "Local gates" section (the exact check.sh command and result at the pushed head; scoped Unity runs with filter and result, or "Unity unavailable locally"), and the line "Unity tests: CI".
`
const SPECIALISTS = `
Specialist rubrics (mandatory, CLAUDE.md section 7): you cannot spawn the specialist subagents, so Read ${REPO}/.claude/agents/unity-infrastructure-architect.md and ${REPO}/.claude/agents/unity-network-architect.md ONCE (round 1) and apply both checklists yourself to the diff: module boundaries and asmdef rules, API surface, memory/pooling ownership, thread safety and the ARM64/IL2CPP memory model (Volatile/Interlocked), disposal on every path, IL2CPP/AOT hazards (reflection, Expression.Compile, MakeGenericMethod, stripping), platform compatibility (iOS/Android/ATS/IPv6), protocol correctness against the RFCs, TLS/security, cancellation, and missing tests. A rubric failure is a finding like any other.
`
const HELPERS = (slug, role) => `
Cheap helper agents: you cannot spawn subagents, so a broker workflow runs them for you on cheaper models. One Bash call (timeout 600000):
  ${MB} ask --run ${RUN} --from ${slug}/${role} --type <type> --worktree <your worktree or ''> --question - <<'EOF_ASK'
  <self-contained question: what you need, the leads you already have (paths, symbols), the answer shape you want>
  EOF_ASK
Types:
- cs-scout: locate C# code in a given worktree (read-only): declarations, callers, implementations, asmdef membership; returns file:line with one line each.
- rfc-researcher: one batched protocol/spec investigation (RFC 9110/9111/9112/9113/7541/6455/7692/8441/6265, TLS RFCs, Unity/IL2CPP platform docs) combined with the repo's phase docs under Development/docs/phases and ${REVIEW_DOC}. At most ${RESEARCHER_LIMIT} per lane: put the whole question in one request.
- runner: run the commands you list, in the given worktree (the local gate, scoped Unity runs, dotnet builds), and return a compressed result (pass/fail, failing tests, first error lines with file:line). Use it for long runs whose raw output you do not need.
The call prints {"answer": ...}, or {"pending": true, "id": ...} (keep waiting with \`${MB} answer-wait --run ${RUN} --id <id> --wait 570\`, or work on and check later with --wait 0), or {"error": ...} (broker down: do it inline), or {"stop": true} (the run is stopping: wrap up as your stop rule says). Add --nowait to fire a request and keep working. Answer inline when rg or a targeted read settles it in a few calls. Verify a helper's evidence paths before relying on them.
`
const ENV = `
Environment rules (all agents):
- Primary checkout: ${REPO} (GitHub fatal10110/turboHTTP, default branch main). It is a Unity UPM package root (Runtime/, Editor/, Tests/, Samples~/, Documentation~/, Tools~/). Always use absolute paths; your inherited cwd may be unrelated: ignore it.
- Never write in the primary ${REPO} checkout; edits happen only in linked worktrees under ${WT}/ (lane worktree wf-<slug> on branch wf/<slug>, review worktree review-<slug>). Base every worktree on origin/main.
- Working directory: start every repo shell call with \`cd <worktree> &&\` (the worktree ROOT) and write every path root-relative (Runtime/..., Tests/..., Development/...), or use \`git -C <worktree>\` / \`--root <worktree>\`. Agent tools are always called by the absolute paths given in this prompt.
- Paths containing "~" (Tools~, Samples~, Documentation~) go inside single quotes in shell commands.
- Never guess a path. When you do not know a file's exact location, find it in the same call: \`rg --files <worktree>/Runtime -g '<Name>*.cs'\` (or ${LOOK} rgl:...). A "No such file" result means look it up, not try a variant.
- Path map (relative to the worktree root):
  * Core/client/pipeline: Runtime/Core/ (Internal/ pooling+buffers, Pipeline/ interceptors); transport: Runtime/Transport/{Http1,Http2,Tcp,Tls,Connection,Internal}/ and RawSocketTransport.cs; BouncyCastle glue Runtime/Transport/BouncyCastle/*.cs (Lib/ is vendored: never edit it); modules Runtime/{JSON,Middleware,Auth,Retry,Cache,RateLimit,Observability,Files,Testing,Unity,UniTask}/; WebSocket Runtime/{WebSocket,WebSocket.Transport,Unity.WebSocket}/; editor Editor/; native Plugins/{iOS,Android}/.
  * Tests: Tests/Runtime/<Area>/ (TurboHTTP.Tests.Runtime.asmdef), Tests/Editor/; helpers Tests/Runtime/TestHelpers.cs, AssertAsync.cs; mocks in Runtime/Testing (MockTransport, RecordReplayTransport).
  * Plans: Development/docs/phases/ (phase docs and sub-phase folders), the roadmap Development/docs/00-overview.md, the review ${REVIEW_DOC}, the implementation journal Development/docs/implementation-journal/.
  * Repo rules: CLAUDE.md (always) and the scoped guides it lists in section 9 (architecture.md, platform.md, testing.md): read the ones your change touches.
- Module rules (CLAUDE.md section 4, Development/docs/agents/architecture.md): optional modules depend only on TurboHTTP.Core except the documented exceptions; no new cross-module asmdef references; Core never references a module; Transport stays free of UnityEngine; unsafe code only in Transport; string asmdef references. A change that needs a new reference is a [blocker] unless the lane note allows it.
- GitHub: use the GitHub MCP tools (mcp__github__*), not the gh CLI. Load schemas with ToolSearch "select:mcp__github__pull_request_read,mcp__github__issue_read,mcp__github__search_issues,mcp__github__issue_write,mcp__github__add_issue_comment,mcp__github__create_pull_request,mcp__github__update_pull_request,mcp__github__pull_request_review_write,mcp__github__add_comment_to_pending_review,mcp__github__add_reply_to_pull_request_comment,mcp__github__merge_pull_request,mcp__github__update_pull_request_branch". Translate the skills' gh steps: issue view -> issue_read (get, get_comments); search -> search_issues ("repo:fatal10110/turboHTTP <terms>"); issue create/close/edit -> issue_write; comments -> add_issue_comment; pr create/edit -> create_pull_request/update_pull_request; pr view/diff/files/checks -> pull_request_read (get, get_diff, get_files, get_review_comments, get_reviews, get_check_runs); inline review -> pull_request_review_write create, add_comment_to_pending_review, submit_pending; thread reply -> add_reply_to_pull_request_comment; merge -> merge_pull_request. gh is allowed only for resolving review threads (gh api graphql resolveReviewThread), re-running failed CI jobs (gh run rerun --failed) and reading failed job logs (gh run view --log-failed), with a 300000 ms Bash timeout and up to two retries on HTTP 401.
- Cost model: every tool call re-sends your WHOLE context (often 150k+ tokens), so the number of calls matters far more than their size. Put every lookup you need right now into ONE call, and never spend a call on something another call can carry.
- Batched lookups: ${LOOK} <spec>... (run from the worktree, or pass --root <worktree>) does several in one call with capped output:
  FILE:A-B (a range) | cs:PATH:Name,Type.Member (C# declarations with doc comments and attributes; PATH a file or directory) | rg:PATH:PATTERN | rgl:PATH:PATTERN (files + counts) | rgg:GLOB:PATH:PATTERN.
  Example: ${LOOK} --root <worktree> cs:Runtime/Transport/Http2:Http2Connection.HandleHeadersFrame,HpackEncoder.Encode rgl:Tests/Runtime:Http2Connection Runtime/Transport/RawSocketTransport.cs:290-320
- Code navigation (every tool output stays in your context for the rest of the lane, so keep each one small):
  * rg: always bound by path and glob (-g '*.cs'), start with -l or -c to find files, then -n with a small -m or -C 3; never an unbounded rg over the whole repo, and never over Runtime/Transport/BouncyCastle/Lib.
  * Compact output: prefer \`rtk rg ...\` over plain rg, and \`rtk git diff|log|show ...\` over plain git for anything longer than a few lines.
  * Reading: a known range with the Read tool (offset + limit) or \`sed -n 'A,Bp'\` of at most 80 lines; never \`cat\` a whole source file over 80 lines, never re-read a range you already have in context.
  * Output caps: rg always with \`-m 5\` and piped through \`| head -60\`; git diff of named files or \`--stat\` first; gate output through \`| tail -40\`.
  * Batching: before you look anything up, list every lookup the next step needs and fetch them all in ONE call. Two single-lookup calls in a row is a mistake; so is a lookup the lane brief already answers.
- Issues: read each issue with ${SLICE} <n> (body, newest comments, referenced issues, the repository paths it names; --section <name> for one section, --full for everything). It is cached, so the other agent of your lane reads it for free. Find related issues with ${ISEARCH} <terms> [--state open] [--milestone v1.0]. Read each issue once per agent and keep what you need in your notes/worklog.
- Editing: the Edit tool needs a Read of that file first (a sed/cat/look view does not count): Read the range, then Edit. For several mechanical edits at once a python heredoc in one call is fine. New C# files need a matching .meta file (copy the shape of a sibling .cs.meta with a fresh 32-hex guid: python3 -c "import uuid;print(uuid.uuid4().hex)"); the local gate fails without it.
- Implementation journal (CLAUDE.md section 7): every lane that changes code adds ONE new file Development/docs/implementation-journal/YYYY-MM-<slug>.md (what, files, decisions, deferred items). Never edit another lane's journal file. Do not edit CLAUDE.md or AGENTS.md unless the lane note says so (they are shared hot files).
- Waiting: never use sleep loops; wait only through the ${MB} commands named in your instructions, each in one Bash call with timeout 600000.
- Follow-up issues: dedupe with ${ISEARCH} / search_issues first and amend a matching issue instead of duplicating; use the issue template (Summary, Location, Problem, Spec reference, Suggested fix, Acceptance criteria, Context) and the milestone that owns the work (v1.0-beta for protocol/security defects, v1.1 otherwise unless the parent issue says more).
- Where an issue offers several options, choose the one matching the RFC, the phase doc or CLAUDE.md, else the conservative one, stated in a "Decisions" section of the PR body.
- Merged PRs close their issues: one "Closes #n" line per fully resolved issue, "Part of #n" for partial ones. Every acceptance-criteria checkbox of a closed issue maps to a hunk or a test in the PR body.
- Commit messages end with a blank line then: Co-Authored-By: Claude <noreply@anthropic.com>
- PR bodies end with: 🤖 Generated with [Claude Code](https://claude.com/claude-code)
- Never put model names or IDs in commits, PR text or code.
- Put new tests in new or small test files to avoid conflicts between lanes.
${PLAN.exclusions ? '- Never touch (other sessions / owner decisions): ' + PLAN.exclusions + '\n' : ''}${PLAN.env ? PLAN.env + '\n' : ''}- Other lanes run concurrently or later; stay within your lane and do not implement what other lanes own: ${ALL_LANES.map(l => l.slug + '=' + l.issues.join('/')).join('; ')}.
`
const issuesText = L => L.issues.map(i => '#' + i).join(', ')

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const IMPL_SCHEMA = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['merged', 'no_change', 'blocked', 'unresolved', 'stalled', 'merge_blocked', 'failed', 'stopped', 'idle_exit', 'checkpoint'] },
    pr_number: { type: 'integer' },
    closes: { type: 'array', items: { type: 'integer' } },
    summary: { type: 'string' },
    followups_filed: { type: 'array', items: { type: 'string' } },
  },
  required: ['status', 'summary'],
}
const REVIEWER_SCHEMA = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['merged', 'unresolved', 'stalled', 'merge_blocked', 'failed', 'stopped', 'idle_exit', 'no_change', 'blocked'] },
    pr_number: { type: 'integer' },
    rounds: { type: 'integer' },
    merge_sha: { type: 'string' },
    summary: { type: 'string', description: 'one line per round: verdict and item count; merge attempts and outcomes' },
  },
  required: ['status', 'summary'],
}
const WAIT_SCHEMA = {
  type: 'object',
  properties: {
    messages: { type: 'array', items: { type: 'object', properties: { seq: { type: 'integer' }, kind: { type: 'string' }, data: { type: 'string', description: 'the message data object, as a JSON string' } }, required: ['seq', 'kind', 'data'] } },
    timeout: { type: 'boolean' },
    stop: { type: 'boolean' },
  },
  required: ['messages', 'timeout'],
}
const MERGE_SCHEMA = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['merged', 'conflict', 'ci_failed', 'head_moved', 'threads_open', 'merge_blocked', 'retry'] },
    answered: { type: 'boolean', description: 'true once the answer file was written' },
    merge_sha: { type: 'string' },
    notes: { type: 'string' },
  },
  required: ['status', 'answered', 'notes'],
}
const LINES_SCHEMA = {
  type: 'object',
  properties: { passed: { type: 'boolean' }, stop: { type: 'boolean' }, lines: { type: 'array', items: { type: 'string' } } },
  required: ['lines'],
}
const TAKE_SCHEMA = {
  type: 'object',
  properties: {
    events: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' }, type: { type: 'string' }, from: { type: 'string' } }, required: ['id', 'type', 'from'] } },
    timeout: { type: 'boolean' },
    stop: { type: 'boolean' },
  },
  required: ['events', 'stop'],
}

// ---------------------------------------------------------------------------
// Prompts: the two long-lived agents of a lane
// ---------------------------------------------------------------------------
function implPrompt(L, resume, brief, canCheckpoint) {
  const D = LANE_DIR(L.slug)
  const W = `${WT}/wf-${L.slug}`
  const checkpointRules = canCheckpoint
    ? `CHECKPOINTS (your cost is turns x context, so hand over instead of growing):
- Right after you send the first ready (reason initial): bring ${D}/worklog.md fully up to date (state, PR number, decisions and why, files changed, the exact gate and test commands, open questions) and return status checkpoint. A fresh agent handles the review, CI and conflict rounds from the worklog.
- Any other time your conversation has grown long (about 60 tool calls since you started, or a context warning) and you are at a clean point (edits saved, worklog listing the exact remaining steps), or you are about to wait on recv after a long round: update the worklog and return status checkpoint. Never checkpoint in the middle of an edit, with uncommitted work the worklog does not describe, or with a message read but not handled.`
    : `CHECKPOINTS: none are left for this lane. Never return status checkpoint; keep working until the final message or another return the protocol names.`
  return `${TESTRULE}
You are the long-lived implementation agent for lane "${L.slug}" (wave ${L.wave}, track ${TRACK}) of fatal10110/turboHTTP: issues ${issuesText(L)}${L.milestone ? ' (' + L.milestone + ')' : ''}.
${L.note || ''}
You own this PR's code: you implement it and fix every review round, CI failure and merge conflict, handing over to a fresh implementation agent at the CHECKPOINTS below so no agent carries a huge context. One long-lived review agent reviews every push and routes merge results back through the lane mailbox. You never merge.
${brief ? `\nLANE BRIEF (a scout prepared it from origin/main; it is also in ${D}/brief.md). Rely on it instead of repeating its searches; verify only what you change or what looks wrong:\n${brief}\n` : resume ? `\nThe lane brief is in ${D}/brief.md: Read it once if the lane is still at stage impl; review, CI and conflict rounds rarely need it.\n` : ''}

PHASE 1 — implement. Load and follow ${skillRef('turbohttp-issue')}: ${brief
    ? 'the LANE BRIEF above already holds the issue slice, the code anchors, the phase-doc and RFC pointers and the tests to copy, so start from it: read live only what it lacks or marks open (issue comments newer than the brief included)'
    : 'read each issue live (' + SLICE + ' <n>), verify each defect still exists on origin/main, read the phase doc and RFC sections the issue names'} (through the helpers below where they fit), implement the smallest complete change for this lane with a failing regression test first (NUnit under Tests/Runtime/<area>/ or Tests/Editor/), run the local gate, add the journal entry, then open ONE PR, ready for review (not draft), with "Closes #n" per fully resolved issue, the acceptance-criteria mapping and the evidence the skill requires.
- Worktree: ${MB} worktree --run ${RUN} --lane ${L.slug} (one call: creates or reuses ${W} on branch wf/${L.slug} from origin/main, records it in the lane state, and prints head, dirty files and how far it is behind main). Push with git -C ${W} push -u origin wf/${L.slug}.
${(L.deps || []).length ? `- This lane builds on lane(s) ${L.deps.join(', ')}, already merged to origin/main: read their merged PRs and reuse their primitives.` : ''}
- An issue already fixed on main or stale: no code for it; comment with evidence (main SHA, file:line, merging PR), close it, leave it out of "Closes".
- Keep ${D}/worklog.md current: decisions and why, RFC/phase-doc evidence, what each round changed, open questions. A replacement agent resumes from it if you stop.
- STOP: once the run is stopping, every ${MB} call prints "stop": true, ${LOOK} prints "### STOP", and the gate command (which runs \`wave_mail.py stopcheck\` first) prints "### STOP" and refuses to run. When you see any of them: update the worklog (where you are, what is left), push nothing, and return status stopped. Never spend a call of its own checking for it.

MAILBOX (each command in its own Bash call, timeout 600000):
- To the review agent: ${MB} send --run ${RUN} --lane ${L.slug} --box review --kind <kind> --data '<json object>' [--set key=value ...]
  * ready {"reason":"initial|review-fix|ci-fix|conflict","pr":<n>,"head":"<pushed sha>","semantic":<bool>} after every push that should move on; add --set stage=review --set pr=<n> --set head=<sha>.
  * no_change {"summary":"..."} with --set status=no_change, or blocked {"reason":"..."} with --set status=blocked (after commenting the blocker on the issue): only before a PR exists; then return at once.
- Wait for the review agent: ${MB} recv --run ${RUN} --lane ${L.slug} --box impl --wait 570 ; run it again while it prints "timeout": true. Before working on a review, ci or conflict message, run git -C ${W} pull --no-rebase origin wf/${L.slug} : the merge agent may have pushed a branch-update merge commit, and your push must stay a fast-forward (not for final: a merged branch is already deleted). Messages:
  * review {"round":N,"file":"..."}: read the file (and the PR's unresolved threads), then follow ${skillRef('turbohttp-pr-review-comments')}: verify each item against the code, the RFC and the phase doc, fix every confirmed in-scope finding with tests, reply on every thread with evidence (fixed-in SHA, or rebuttal with code/RFC evidence), resolve fixed threads, file follow-ups for valid out-of-scope items, run the local gate, push (never force-push), send ready reason review-fix. Items that carry a thread id (often CodeQL alerts by github-advanced-security that the reviewer triaged into the list) are findings like any other: after the push, ${MB} resolve-thread --id <id> --reply 'Fixed in <sha>: <one line>' ; for a rebuttal add --keep-open (the reviewer accepts or rejects it). Never skip a listed thread because a bot opened it, and never leave one without a reply. You already know the code: re-read only what the findings point at.
  * ci {"file":"...","head":"..."}: CI failed for a real reason; read the file, fix, verify with the local gate (and the failing Unity tests scoped with --filter when Unity is available), push, send ready reason ci-fix.
  * conflict {"notes":"..."}: the branch cannot be updated automatically. git fetch origin; merge origin/main into your branch (never rebase, never force-push); resolve (journal files never conflict: keep both); run the local gate; push; send ready reason conflict with "semantic": true when the resolution changed the PR's logic, else false.
  * final {"reason":"merged|unresolved|stalled|merge_blocked|failed|stopped"}: if merged, run ${MB} worktree --run ${RUN} --lane ${L.slug} --remove (worktree and local branch); otherwise keep both. Update the worklog and return your result with status = the final reason.
  * A message identical to one you already handled (same kind and round/file): ignore it.
  * recv printing "stop": true: the run is stopping; update the worklog and return status stopped.
- If recv times out for ${MAX_IDLE_MIN} consecutive minutes (since the last message you received), update the worklog and return status idle_exit.
${checkpointRules}
- Skills: Read each SKILL.md once per agent and keep what you need in your notes; never re-read or \`cat\` it again.
${HELPERS(L.slug, 'impl')}
${ENV}
${resume ? '\nRESUME: ' + resume : ''}
Return: status, pr_number, closes, summary (what changed, key files, decisions), followups_filed.`
}

function implResume(L, why) {
  const D = LANE_DIR(L.slug)
  return `${why} First run ${MB} state --run ${RUN} --lane ${L.slug} --set status=active ; the state it prints (stage, pr, round) is authoritative. Rebuild your context from ${D}/worklog.md, the PR if there is one (pull_request_read get and its unresolved review threads; for the code, \`git -C ${WT}/wf-${L.slug} diff --stat origin/main...HEAD\`, then only the hunks a review item or the worklog points at; never get_diff of the whole PR), ${D}/reviews/ and ${D}/ci/. Reuse worktree ${WT}/wf-${L.slug} (recreate it from origin/wf/${L.slug} if missing). Then by stage:
- impl: finish the implementation and continue with the protocol.
- review or merge: the review agent is working; go straight to recv.
- fix: the newest review in ${D}/reviews/ may be partly addressed; compare what is pushed with what was asked, finish it, push, send ready reason review-fix.
- ci-fix or conflict: the triggering message may already be consumed; read the newest file in ${D}/ci/ or merge origin/main as the protocol says, fix, push, send ready with the matching reason.`
}

function briefPrompt(L) {
  const D = LANE_DIR(L.slug)
  const W = `${WT}/wf-${L.slug}`
  return `You prepare the LANE BRIEF for lane "${L.slug}" of fatal10110/turboHTTP (issues ${issuesText(L)}${L.milestone ? ', ' + L.milestone : ''}). An expensive implementation agent starts right after you with your brief in its prompt; every fact you hand it saves it a lookup. Gather facts; do not design the change, do not write code, do not run tests.
Lane note: ${L.note || '(none)'}
Steps (batch: several lookups per Bash call):
1. ${MB} worktree --run ${RUN} --lane ${L.slug}   (creates ${W} on origin/main; all paths below are relative to it).
2. Issues: ${SLICE} <n> for each lane issue (prints body, comments, referenced issues and the repository paths named). Check every blocker or dependency the issue or note names with gh issue view <b> -R fatal10110/turboHTTP --json state,title (timeout 300000).
3. Code anchors: for every file:line the issues cite, confirm it still exists on origin/main with ${LOOK} --root ${W} (ranges and cs:PATH:Type.Member); give the current line numbers, the enclosing type/method, the callers of each method the fix will change (rg -n -w '<Method>' -g '*.cs' Runtime | head -20), the asmdef that owns each file, and any lock/Volatile/Interlocked or pooling ownership comment on the touched state.
4. Spec and plan: the RFC sections the issues name (section numbers and the one-sentence rule each states; do not paste RFC text), the phase doc sections under Development/docs/phases that cover the code (path + heading), and the newest implementation-journal entries for these files (rg -l '<FileName>' Development/docs/implementation-journal | tail -5).
5. Tests: the existing test files that cover the code (rg -l '<TypeName>' Tests/Runtime Tests/Editor -g '*.cs'), the closest test to copy as a template (path + test name + the fixture/mock it uses: MockTransport, a loopback server helper, AssertAsync), and the test asmdef it belongs to.
6. Blockers: an issue already fixed on main, a dependency issue still open, or a fix that needs a new asmdef reference the module rules forbid, goes first, under BLOCKER, with evidence.
7. Commands: the local gate (${GATE(W)}), and the scoped Unity runs with the --filter regexes for the tests to add or touch (${UNITY(W)} --platform EditMode|PlayMode --filter '<regex>').
Write the brief as markdown, at most 250 lines: sections BLOCKER (only if any), Issues (requirements, acceptance criteria, decisions already made in comments), Code anchors, Spec and plan, Tests, Commands, Open questions. Paths with line numbers; short excerpts (at most 15 lines) only where the exact text matters. Save it with one Bash call: mkdir -p ${D} && cat > ${D}/brief.md <<'EOF_BRIEF' ... EOF_BRIEF . Your final message is the brief itself, verbatim.
You have no GitHub MCP tools and no ToolSearch: the gh commands in the steps above override the GitHub rule below.\nSTOP: once the run is stopping (or draining), ${MB} calls print "stop": true and ${LOOK} prints "### STOP". When you see either, do nothing more and make your final message exactly STOP.
${ENV}`
}

// Code-scanning (CodeQL) alerts arrive as review threads by github-advanced-security a few minutes
// after each push. The reviewer owns every unresolved thread: none may be left unanswered at merge.
const THREAD_TRIAGE = (pr, head) => `THREAD TRIAGE (every round, before you write the verdict; you own every unresolved thread on the PR, including the CodeQL alerts that github-advanced-security posts as threads):
- List them, waiting for this head's code scanning first: ${MB} threads --pr ${pr} --head ${head} --wait 570 (Bash timeout 600000; run it again while "code_scanning" is "pending", at most 3 times). Output: unresolved, unanswered, threads [{id, path, line, outdated, author, bot, replies, title, url}].
- Decide each thread, then act on it (one Bash call per thread: ${MB} resolve-thread --id <id> --reply '<text>' replies and resolves):
  * A real defect (a null dereference that can happen, a resource leak or missing Dispose on a reachable path, a catch-all that swallows errors in Runtime/ code without a stated reason, Path.Combine with a later argument that can be rooted, a race): an action item in r<N>.md with its tag and the thread id, e.g. "[should-fix] path:line — what to do (thread PRRT_...)". Leave the thread open; the implementation agent fixes, replies and resolves it.
  * A cheap quality note on lines this PR adds (missed using, useless assignment, missed Where/Select, missed readonly): a non-optional [nit] action item with the thread id when the fix is a one-line mechanical change; otherwise resolve-thread with a one-line reason.
  * Intended or a false positive (a test teardown or a peer loop that must swallow every exception, a value CodeQL thinks can be null but cannot, with the file:line that proves it): resolve-thread with that evidence.
  * On lines this PR did not touch: resolve-thread --reply 'Pre-existing code outside this PR's diff.'
  * An implementation agent's rebuttal (replies > 0, still open): accept it with resolve-thread, or keep the item in the action list and say why the evidence does not hold. An outdated thread whose item is fixed: resolve it.
- A round is clean only when the action list is empty AND a final ${MB} threads --pr ${pr} prints "unresolved": 0. Never ask for the merge with an open thread.
`

function reviewerPrompt(L, resume, roundLimit) {
  const D = LANE_DIR(L.slug)
  const RW = `${WT}/review-${L.slug}`
  return `${TESTRULE}
You are the long-lived review agent for lane "${L.slug}" (issues ${issuesText(L)}) of fatal10110/turboHTTP. You stay alive for the PR's whole life: you review the first push in full, then wait for the implementation agent's fixes and re-review ONLY what changed, request the merge when the PR is clean, and route CI failures and conflicts back to the implementation agent. You keep your context between rounds: never re-read a file, issue, thread or spec section you already read unless the new delta touches it.
${L.note || ''}
${SPECIALISTS}
Review worktree: ${RW}, detached. At the start of every round run ${MB} worktree --run ${RUN} --lane ${L.slug} --review in a Bash call of its own (creates it the first time, then moves it to the current origin/wf/${L.slug}, and prints the head). Remove it when you return: the same command with --remove.
Cost rules: Read the review skill's SKILL.md and the two rubric files ONCE (round 1) with the Read tool and keep their checklists in your notes; never re-read or \`cat\` them again, and never chain them to another command. Review from the diff: \`git -C ${RW} diff --stat <base>..<head>\`, then the hunks of the files you review, then surrounding code in ranges of at most 80 lines (${LOOK} cs:... for whole declarations). Do not print whole files.

MAILBOX (each command in its own Bash call, timeout 600000):
- Wait for the implementation agent: ${MB} recv --run ${RUN} --lane ${L.slug} --box review --wait 570 ; run it again while it prints "timeout": true. You get ready {"reason","pr","head","semantic"}, or rarely no_change/blocked (then return that status).
- To the implementation agent: ${MB} send --run ${RUN} --lane ${L.slug} --box impl --kind <review|ci|conflict|final> --data '<json object>' [--set key=value ...]
- Merge requests go to the merge agent (cheap model) through the merger broker:
  ${MB} ask --run ${RUN} --from ${L.slug}/review --type merge --question '{"pr":<n>,"head":"<sha>"}' --wait 570
  The merge takes a while (CI): while it prints "pending": true, run ${MB} answer-wait --run ${RUN} --id <id> --wait 570 again, always with the same id (a pending request is still being served; never ask again while one is pending). The answer is a JSON object {"status":"merged|conflict|ci_failed|head_moved|threads_open|merge_blocked|retry","merge_sha","file","head","notes"}. "stop": true: return status stopped. An "error" (the merge broker is not running): ${MB} pause --secs 300, then ask again; after 6 such errors, send final {"reason":"failed"} --set status=failed and return status failed.

LOOP:
1. Round 1 (the first ready): load ${skillRef('turbohttp-pr-review')} and follow it end to end: review against the acceptance criteria of every issue the PR closes or is part of, the RFC sections and phase docs they name, and both specialist rubrics; run the local gate in the review worktree (${GATE(RW)}) and, when Unity is available, the PR's new/changed tests scoped with --filter; submit one GitHub review with inline comments; decide the Bottom line. Only [blocker]/[must-fix]/[should-fix]/non-optional [nit] items count as action required.
   Test coverage is part of every round: every behavior change needs a deterministic regression test that would fail on the old code, and the PR body's "Local gates" section must match what you can reproduce. A missing regression test, a failing local gate, or a test that depends on wall-clock sleeps or the external network is a [must-fix]. Never run the full Unity suite: CI does.
2. Every round N: run the THREAD TRIAGE below for the head you reviewed, then write ${D}/reviews/r<N>.md (verdict, the numbered action list '[tag] path:line — what to do (thread id when there is one)' or "none", the thread triage outcome (counts: listed as items, resolved with a reason), and for N > 1 the status of each earlier item), then ${MB} state --run ${RUN} --lane ${L.slug} --set round=<N> .
   - Action required and N < ${roundLimit}: send review {"round":N,"file":"${D}/reviews/r<N>.md"} --set stage=fix, then wait.
   - Action required and N >= ${roundLimit}: send final {"reason":"unresolved"} --set status=unresolved and return status unresolved.
   - Clean (no action item and "unresolved": 0): go to step 4.
3. On ready (round N+1), by reason:
   - review-fix${REVIEW_CI_FIXES ? ', ci-fix' : ''}, or conflict with "semantic": true: a DELTA re-review. Diff your last reviewed head to the new head in the review worktree; verify each earlier item is fixed; review the delta in full and other code only where the delta reaches. Read the author's thread replies; do not re-raise a finding rebutted with concrete code/RFC evidence unless you can show the rebuttal wrong with better evidence, and do not add nits you could have raised earlier unless they are real defects. Submit the re-review on GitHub, then step 2.
   - ${REVIEW_CI_FIXES ? '' : 'ci-fix, or '}conflict with "semantic": false: no review; go to step 4.
4. Merge: ${MB} state --run ${RUN} --lane ${L.slug} --set stage=merge , then ask for the merge with head = the head of the latest ready you accepted (the head you reviewed, or for a ready you took to merge without review, the head it names), and act on the answer:
   - merged: send final {"reason":"merged","merge_sha":"<sha>"} --set status=merged; return status merged.
   - ci_failed: send ci {"file":"<file>","head":"<head>"} --set stage=ci-fix; wait (step 3).
   - conflict: send conflict {"notes":"<notes>"} --set stage=conflict; wait (step 3).
   - head_moved: the branch changed without a ready (an unexpected push). Fetch it and do a delta re-review of the new head as the next round (step 3 rules, then step 2).
   - threads_open: review threads appeared or stayed open after your verdict (CodeQL alerts on the newest push, a human reviewer). Treat it as the next round on the same head: THREAD TRIAGE, then step 2.
   - merge_blocked: send final {"reason":"merge_blocked"} --set status=merge_blocked; return status merge_blocked.
   - retry: ask again; after 6 merge requests in total, send final {"reason":"failed"} --set status=failed and return status failed.
- If recv times out for ${MAX_IDLE_MIN} consecutive minutes (since the last message you received): send final {"reason":"stalled"} --set status=stalled; return status stalled.
- recv or answer-wait printing "stop": true: return status stopped (no final message).
${THREAD_TRIAGE('<pr>', '<head>')}
${HELPERS(L.slug, 'review')}
${ENV}
${resume ? '\nRESUME: ' + resume : ''}
Return: status, pr_number, rounds, merge_sha, summary.`
}

function reviewerResume(L, why) {
  const D = LANE_DIR(L.slug)
  return `${why} You have no memory of earlier rounds: rebuild it once. First run ${MB} state --run ${RUN} --lane ${L.slug} ; the state it prints (stage, pr, round) is authoritative. Read every review in ${D}/reviews/ and the PR (pull_request_read get, get_reviews, get_review_comments). Your next round number is the highest r<N>.md number + 1, and the last reviewed head is the one in the newest review file. Then by stage:
- review (or impl with a PR open): a ready message may already be consumed. If the PR head differs from your last reviewed head (or there is no review yet), review it now (round 1 in full if there is none, otherwise a delta re-review); else wait.
- fix, ci-fix or conflict: wait for the implementation agent's ready.
- merge: check the PR first. MERGED: send final {"reason":"merged","merge_sha":"<sha>"} --set status=merged and return status merged. Otherwise look for an earlier merge request of this lane still pending (ls ${RUNDIR}/requests/taken/ | rg '^${L.slug}-review-' , newest first, with no matching ${RUNDIR}/answers/<id>.md): if one exists, keep waiting on it with answer-wait; else ask for the merge again.`
}

// ---------------------------------------------------------------------------
// Prompts: small coordination agents
// ---------------------------------------------------------------------------
function watchPrompt(L, drain) {
  return `You are a mailbox watcher. Run only ${drain ? 'these commands, each' : 'this command'} in one Bash call with timeout 600000, copied exactly; do nothing else:
${drain ? `1. ${MB} drain --run ${RUN} --lane ${L.slug} --box review --kinds no_change,blocked   (discards answers left from an earlier run)
2. ` : ''}${MB} recv --run ${RUN} --lane ${L.slug} --box review --peek --wait 570
While its output has "timeout": true and no "stop", run it again: at most ${WAIT_CALLS} recv runs in total.
Return the last output: messages (seq, kind, and data re-encoded as a JSON string), timeout, stop.`
}

function deliverFinalPrompt(L, data) {
  return `Run only this command in one Bash call with timeout 600000, copied exactly, and do nothing else; then return messages [], timeout true:
${MB} send --run ${RUN} --lane ${L.slug} --box impl --kind final --data '${sq(data)}' --set status=${data.reason}`
}

function statusPrompt() {
  return `Run exactly this Bash command (timeout 600000) and return its "lines" array verbatim: ${MB} status --run ${RUN}`
}

function barrierPrompt(wi, finish) {
  return `You are a wave barrier. Run only these commands, each in its own Bash call with timeout 600000, copied exactly:
1. ${MB} barrier --run ${RUN} --epoch ${EPOCH} --wave ${wi} --track ${TRACK} --tracks ${TRACKS.join(',')} --wait 570
   While it prints "passed": false without "stop", run it again: at most 30 runs.
${finish ? `2. If it passed: ${MB} finish-track --run ${RUN} --epoch ${EPOCH} --track ${TRACK}\n` : ''}Return passed, stop and lines from the last barrier output (lines verbatim; empty if absent).`
}

// One take agent polls up to TAKE_CALLS times (~9.5 min each) before it returns, so an idle
// broker pays one agent's context per ~TAKE_CALLS × 9.5 min instead of one per poll.
function takePrompt(broker, first) {
  const cmd = `${MB} take --run ${RUN} --broker ${broker} --epoch ${EPOCH} --tracks ${TRACKS.join(',')} --wait 570`
  return `You are a request poller. Run only this Bash command (timeout 600000), copied exactly:
${cmd}${first ? '\nOn the FIRST run only, append " --requeue" to it.' : ''}
While it prints "events": [] with "timeout": true and no "stop": true, run it again: at most ${TAKE_CALLS} runs in total. Do nothing else between runs.
Return the events (id, type, from only), timeout and stop fields of the last run.`
}

function helperPrompt(ev) {
  const file = `${RUNDIR}/requests/taken/${ev.id}.json`
  const body = {
    'cs-scout': `Locate the C# code the question asks about, in the worktree it names (read-only): rg -n -w with -g '*.cs' bounded to Runtime/, Tests/ or Editor/ (never Runtime/Transport/BouncyCastle/Lib), and ${LOOK} --root <worktree> cs:PATH:Type.Member for declarations. Report file:line locations with one line each on what is there; no file dumps.`,
    'rfc-researcher': `Investigate the protocol or platform question as one batched report. Sources, in order: the RFC/spec text (fetch https://www.rfc-editor.org/rfc/rfcNNNN.txt with WebFetch, or the official spec page), the repo's phase docs under <worktree>/Development/docs/phases, ${REVIEW_DOC}, and the code the question names. Cite RFC section numbers and quote at most one sentence per rule; cite file:line for code. Say MUST/SHOULD/MAY precisely and what the client must do on violation (connection vs stream error, close code, retry rule).`,
    'runner': `Run the commands the question lists, in the worktree it names: the local gate as ${GATE('<worktree>')}, and Unity tests only as ${UNITY('<worktree>')} with a --filter. Refuse a Unity run without --filter (answer that CI runs the full suite and ask for a filter). UNITY_UNAVAILABLE (exit 3) is a result, not an error: report it. Do not edit, commit or push anything. Report per command: pass/fail, the failing check or test names, and the first error lines with file:line (at most 60 lines in total).`,
  }[ev.type]
  return `Helper request ${ev.id} from ${ev.from}. Read the request file ${file} with the Read tool; its "question" field is your task and its "worktree" field is the requester's worktree. ${body}
Your final message is delivered verbatim to the requester: lead with the answer, cite file:line or RFC-section evidence, say what is confirmed versus inferred.`
}

function deliverPrompt(id, text) {
  const path = `${RUNDIR}/answers/${id}.md`
  return `Deliver a helper answer. Use the Write tool to create ${path}.tmp with exactly the text between the markers (not the markers), then run (Bash) mv ${path}.tmp ${path} . Do nothing else.
<<<ANSWER
${text}
ANSWER>>>`
}

function mergePrompt(ev, L) {
  const file = `${RUNDIR}/requests/taken/${ev.id}.json`
  const slug = L.slug
  const D = LANE_DIR(slug)
  return `You are the CI and merge agent for lane "${slug}" (branch wf/${slug}, issues ${issuesText(L)}) in fatal10110/turboHTTP. Read the request file ${file} with the Read tool: its "question" field is a JSON object {"pr": <number>, "head": "<sha the reviewer approved>"}. The PR passed review with no action required, and the user explicitly authorized merging PRs that passed this review loop. Be mechanical: you never edit code, never resolve conflicts and never run tests locally (CI runs the full Unity suite and the gates).
Every Bash call to ${MB} uses timeout 600000. You answer exactly once, at the end, with:
  ${MB} answer --run ${RUN} --id ${ev.id} --text '<json object>'
where the object is {"status":"merged|conflict|ci_failed|head_moved|threads_open|merge_blocked|retry","merge_sha":"","file":"","head":"","notes":""} (no single quotes inside).
PR state and CI come from two compact commands (use them instead of pull_request_read polling):
  ${MB} pr-state --pr <n>   -> {state, merged, merge_sha, draft, head, mergeable_state, behind_by}
  ${MB} ci-wait --run ${RUN} --broker merge --head <sha> --wait 570   -> waits in-process; {state: success|failure|pending|error, failed: [{name, conclusion, run_id, job_id}], pending, missing}
1. Head check: ${MB} pr-state --pr <n>. If the PR is already MERGED (an earlier merge agent merged it, then stopped before answering), do step 5's checks and answer merged with its merge SHA. If the PR head is the requested head, continue. Otherwise git -C ${REPO} fetch origin wf/${slug} main and list the extra commits: git -C ${REPO} log --format='%H %P %s' <requested head>..origin/wf/${slug} . Continue only if the requested head is an ancestor (git -C ${REPO} merge-base --is-ancestor <requested head> origin/wf/${slug}) and every listed commit is a merge commit (two parent hashes) whose subject starts with "Merge branch 'main'": a branch update an earlier merge attempt made. Anything else is a push the reviewer has not seen: answer head_moved with head = the PR head.
2. Update: only when pr-state shows behind_by > 0: mcp__github__update_pull_request_branch (owner fatal10110, repo turboHTTP), then pr-state again for the new head. A merge conflict: answer conflict with the conflicting files in notes. behind_by 0: continue.
3. CI: ${MB} ci-wait --run ${RUN} --broker merge --head <head> --wait 570 ; while it prints state pending, run it again (at most 10 runs, about 90 minutes; longer: answer retry). It returns success only when every check run on the head finished green and the aggregate "ci" check is present (CodeQL alone is not enough). state error: run it once more, then fall back to pull_request_read get_check_runs.
   On a failure: list the failing jobs and read their logs (gh run view <run-id> --log-failed | tail -n 200). It is a flake only if the failing test has an open flaky-test issue (search_issues "repo:fatal10110/turboHTTP Flaky <TestName>") or it fails the same way on the latest main CI run; then rerun the failed jobs once (gh run rerun <run-id> --failed), comment the test name on that issue (or file one in "v1.1: Post-release hardening" with labels type:test, area:testing, priority:P2), and go back to waiting. A Unity license or activation error is infrastructure, not a code failure: answer retry with notes "unity license". Otherwise write ${D}/ci/c<next number>.md (create the directory if needed) with the head sha, each failing check, the failing test names and about 40 log lines per failure, and answer ci_failed with file = that path and head = the sha.
   Checks that never start or fail for GitHub Actions infrastructure reasons (cancelled, queued > 30 min, runner errors) twice in a row: answer retry with notes "ci outage".
3b. Threads: ${MB} threads --pr <n> --head <head verified green> --wait 570 (it waits for code scanning on that head). "unresolved" > 0: do not merge; answer threads_open with head = that head and notes = the open threads as "id path:line title", at most 10. "error": run it once more, then answer retry.
4. Merge: never edit the PR title or body (its Closes/Part of lines are the implementation agent's decision). If the PR is a draft, mark it ready (update_pull_request draft=false). mcp__github__merge_pull_request (merge_method squash, expectedHeadSha = the head verified green). "Head is not up to date" or "head changed": back to step 2 (at most 3 times). If the permission system or a repository ruleset denies the merge, do not try another route: answer merge_blocked.
5. After the merge: git -C ${REPO} push origin --delete wf/${slug}; confirm every "Closes" issue is closed (close any that did not auto-close, commenting the merge SHA); ${MB} state --run ${RUN} --lane ${slug} --set status=merged --set merge_sha=<sha> ; answer merged with merge_sha.
GitHub: use the mcp__github__* tools (load with ToolSearch "select:mcp__github__pull_request_read,mcp__github__update_pull_request_branch,mcp__github__merge_pull_request,mcp__github__update_pull_request,mcp__github__issue_read,mcp__github__issue_write,mcp__github__add_issue_comment,mcp__github__search_issues"); gh only for the run logs and reruns above, with a 300000 ms timeout.
Return: status, answered (true once the answer command printed "written": true), merge_sha, notes.`
}

// ---------------------------------------------------------------------------
// Concurrency helpers
// ---------------------------------------------------------------------------
function sem(n) {
  let active = 0
  const q = []
  return async fn => {
    if (active < n) active++
    else await new Promise(r => q.push(r))
    try { return await fn() } finally {
      const nx = q.shift()
      if (nx) nx(); else active--
    }
  }
}
const hotLocks = {}
for (const L of ALL_LANES) for (const h of (L.hot || [])) if (!hotLocks[h]) hotLocks[h] = sem(1)
function withHot(groups, fn) {
  const gs = [...groups].sort()
  const step = i => i >= gs.length ? fn() : hotLocks[gs[i]](() => step(i + 1))
  return step(0)
}
function parseLines(lines) {
  const st = {}
  for (const ln of (lines || [])) {
    const [slug, status, stage, pr, round] = String(ln).split('|')
    if (slug) st[slug] = { status: status || 'active', stage: stage || '', pr: pr ? Number(pr) : undefined, round: round ? Number(round) : 0 }
  }
  return st
}
function parseData(s) {
  if (s && typeof s === 'object') return s
  try { return JSON.parse(s || '{}') } catch (e) { return {} }
}

// ---------------------------------------------------------------------------
// Track role: one lane = one long-lived implementation agent + one long-lived review agent
// ---------------------------------------------------------------------------
async function lifecycle(L, prev, retried) {
  const slug = L.slug
  const history = []
  const resuming = prev && !DONE_BEFORE.includes(prev.status) && prev.stage ? prev : null
  // A retried lane gets a fresh round budget on top of the rounds it already used.
  const roundLimit = (retried && prev && prev.round ? prev.round : 0) + MAX_ROUNDS
  const runs = { impl: 0, review: 0 }
  const fails = { impl: 0, review: 0 } // early exits that are not checkpoints
  let checkpoints = 0
  let brief = null
  // Set once the PR exists; agents started after it get a pointer to the brief, not the brief.
  let pushed = !!(resuming && resuming.stage !== 'impl')
  const live = { impl: null, review: null }
  const done = { impl: null, review: null }
  const seen = { impl: 0, review: 0 } // the run number whose exit the loop below has handled

  const start = (role, resume) => {
    const n = ++runs[role]
    done[role] = null
    const model = role === 'impl' ? (L.implModel || M.impl) : (L.reviewModel || M.review)
    const opts = role === 'impl'
      ? { label: `impl:${slug}${n > 1 ? '#' + n : ''}`, phase: 'Implement', schema: IMPL_SCHEMA }
      : { label: `review:${slug}${n > 1 ? '#' + n : ''}`, phase: 'Review', schema: REVIEWER_SCHEMA }
    if (model) opts.model = model
    const effort = role === 'impl' ? L.implEffort : L.reviewEffort
    if (effort) opts.effort = effort
    const prompt = role === 'impl' ? implPrompt(L, resume, pushed ? null : brief, checkpoints < MAX_CHECKPOINTS) : reviewerPrompt(L, resume, roundLimit)
    live[role] = agentAs(role, prompt, opts).then(r => {
      if (r && r.pr_number) pushed = true
      done[role] = r || { status: 'died', summary: `${role} agent died` }
      return { role, n, r: done[role] }
    })
  }
  const why = (role, r) => `The previous ${role === 'impl' ? 'implementation' : 'review'} agent of this lane exited early (${r.status}: ${(r.summary || '').slice(0, 300)}).`
  const sendFinal = async data => {
    await agentAs('coord', deliverFinalPrompt(L, data), { label: `final:${slug}`, phase: 'Coordinate', model: M.coord, effort: 'low' })
  }

  if (BRIEF && !resuming) {
    const b = await agentAs('brief', briefPrompt(L), { label: `brief:${slug}`, phase: 'Implement', model: M.brief })
    if (typeof b === 'string' && b.trim() === 'STOP') { STOPPING = true; return { slug, status: 'stopped', history } }
    if (typeof b === 'string' && b.trim()) brief = b.trim().slice(0, 24000)
    else if (b && typeof b === 'object' && b.brief) brief = String(b.brief).slice(0, 24000)
  }
  start('impl', resuming ? implResume(L, 'A previous implementation agent for this lane stopped (run restart).') : null)
  // A checkpoint hands the lane to a fresh implementation agent; anything else counts as an early exit.
  const checkpointed = r => r.status === 'checkpoint' && checkpoints < MAX_CHECKPOINTS && ++checkpoints
  const ckptWhy = 'The previous implementation agent of this lane checkpointed to keep its context small; you continue its work with an empty context.'

  // Until the first push only the implementation agent runs; a cheap watcher holds the second slot.
  let reviewing = pushed
  let idleMin = 0
  let watchFails = 0
  let firstWatch = true
  // An implementation agent's exit (a checkpoint above all) posts no mailbox message, so the watcher
  // races it instead of being waited out; a watcher still in flight is reused on the next pass.
  let watching = null
  const settle = async res => { if (watching) await watching; return res }
  while (!reviewing) {
    if (!watching) watching = agentAs('coord', watchPrompt(L, firstWatch), { label: `watch:${slug}`, phase: 'Coordinate', model: M.coord, effort: 'low', schema: WAIT_SCHEMA }).then(w => ({ w }))
    const ev = await Promise.race([watching, live.impl.then(() => null)])
    const w = ev && ev.w
    if (ev) watching = null
    if (w) firstWatch = false
    if (w && w.stop) { STOPPING = true; await live.impl; return { slug, status: 'stopped', history, impl: done.impl } }
    const msgs = w ? (w.messages || []).map(m => ({ kind: m.kind, data: parseData(m.data) })) : []
    const term = msgs.find(m => m.kind === 'no_change' || m.kind === 'blocked')
    if (term) {
      await live.impl
      log(`${slug}: ${term.kind} — ${(term.data.summary || term.data.reason || '').slice(0, 160)}`)
      return { slug, status: term.kind, history, impl: done.impl }
    }
    if (msgs.some(m => m.kind === 'ready')) { reviewing = pushed = true; break }
    if (done.impl) {
      seen.impl = runs.impl
      if (done.impl.status === 'stopped') STOPPING = true
      if (['no_change', 'blocked', 'stopped'].includes(done.impl.status)) return settle({ slug, status: done.impl.status, history, impl: done.impl })
      if (checkpointed(done.impl)) {
        history.push(`impl#${runs.impl} checkpoint`)
        start('impl', implResume(L, ckptWhy))
        idleMin = 0
        continue
      }
      if (++fails.impl > RESTARTS) {
        // The last exit may follow a first push whose ready the watcher has not reported yet: then
        // the review side still finishes the lane, as it does for an exit after the push.
        const late = watching ? (await watching).w : null
        watching = null
        if (!(late && (late.messages || []).some(m => m.kind === 'ready'))) return { slug, status: 'failed', notes: 'implementation agent exited too often', history, impl: done.impl }
        history.push(`impl gave up after the first push: ${done.impl.status}`)
        reviewing = pushed = true
        break
      }
      history.push(`impl#${runs.impl} exited before the first push: ${done.impl.status}`)
      log(`${slug}: implementation agent exited (${done.impl.status}); starting a replacement`)
      start('impl', implResume(L, why('impl', done.impl)))
      idleMin = 0
      continue
    }
    if (!w) {
      if (++watchFails > 3) {
        await sendFinal({ reason: 'failed' })
        await live.impl
        return { slug, status: 'failed', notes: 'mailbox watcher failed repeatedly', history, impl: done.impl }
      }
      continue
    }
    watchFails = 0
    idleMin += WAIT_CALLS * 9.5
    if (idleMin > MAX_IDLE_MIN) {
      await sendFinal({ reason: 'stalled' })
      await live.impl
      return { slug, status: 'stalled', history, impl: done.impl }
    }
  }

  log(`${slug}: first push ready; review agent starting`)
  start('review', resuming && resuming.stage !== 'impl' ? reviewerResume(L, 'A previous review agent for this lane stopped (run restart).') : null)

  for (let guard = 0; guard < 50; guard++) {
    // Handle an exit the loop has not seen yet before waiting on anything: an agent can finish
    // while the other one is still being started, or both in the same tick.
    const exitOf = role => (done[role] && seen[role] < runs[role]) ? { role, n: runs[role], r: done[role] } : null
    let ev = exitOf('review') || exitOf('impl')
    if (!ev) {
      const pending = ['impl', 'review'].filter(role => !done[role]).map(role => live[role])
      if (!pending.length) break
      ev = await Promise.race(pending)
    }
    const { role, r } = ev
    seen[role] = ev.n
    if (role === 'review') {
      if (FINAL.includes(r.status)) {
        history.push(`review: ${r.status} — ${(r.summary || '').slice(0, 300)}`)
        if (!done.impl) {
          if (r.status !== 'stopped') await sendFinal({ reason: r.status, merge_sha: r.merge_sha || '' })
          await live.impl
        }
        log(`${slug}: ${r.status}${r.pr_number ? ' (PR #' + r.pr_number + ')' : ''}${r.merge_sha ? ' ' + r.merge_sha : ''}`)
        return { slug, status: r.status, pr: r.pr_number, rounds: r.rounds, merge_sha: r.merge_sha, history, impl: done.impl, review: r }
      }
      const implGone = done.impl && seen.impl === runs.impl && (FINAL.includes(done.impl.status) || fails.impl > RESTARTS)
      if (++fails.review > RESTARTS || implGone) {
        if (!done.impl) {
          await sendFinal({ reason: 'failed' })
          await live.impl
        }
        return { slug, status: 'failed', notes: 'review agent exited too often', history, impl: done.impl, review: r }
      }
      history.push(`review#${runs.review} exited: ${r.status}`)
      log(`${slug}: review agent exited (${r.status}); starting a replacement`)
      start('review', reviewerResume(L, why('review', r)))
      continue
    }
    // The implementation agent returned.
    if (FINAL.includes(r.status)) continue // it got its final message; the review agent returns next
    if (checkpointed(r)) {
      history.push(`impl#${runs.impl} checkpoint`)
      start('impl', implResume(L, ckptWhy))
      continue
    }
    if (++fails.impl > RESTARTS) {
      history.push(`impl gave up: ${r.status}`)
      if (!done.review) continue // the review agent ends the lane (stalled at worst)
      return { slug, status: 'failed', notes: 'implementation agent exited too often', history, impl: r, review: done.review }
    }
    history.push(`impl#${runs.impl} exited: ${r.status}`)
    log(`${slug}: implementation agent exited (${r.status}); starting a replacement`)
    start('impl', implResume(L, why('impl', r)))
  }
  return { slug, status: (done.review && done.review.status) || 'failed', history, impl: done.impl, review: done.review }
}

async function trackMain() {
  phase('Coordinate')
  const st0 = await agentAs('coord', statusPrompt(), { label: 'status', phase: 'Coordinate', model: M.coord, effort: 'low', schema: LINES_SCHEMA })
  let STATE = parseLines(st0 && st0.lines)
  const laneSem = sem(LANES)
  const results = []
  const waves = PLAN.waves || []
  log(`track ${TRACK}: ${waves.length} waves, ${LANES} lane(s) at a time, ${Object.keys(STATE).length} lanes with saved state`)
  for (let wi = 0; wi < waves.length; wi++) {
    const mine = (waves[wi].lanes || []).filter(L => L.track === TRACK).map(L => Object.assign({ wave: waves[wi].name || String(wi + 1) }, L))
    const settled = {}
    for (const L of mine) { let res; settled[L.slug] = { p: new Promise(r => { res = r }), res: s => res(s) } }
    const runOne = async L => {
      const prev = STATE[L.slug]
      if (prev && DONE_BEFORE.includes(prev.status) && !RETRY.includes(L.slug)) return { slug: L.slug, status: prev.status, pr: prev.pr, resumed: true }
      const same = (L.deps || []).filter(d => settled[d])
      const sameSt = await Promise.all(same.map(d => settled[d].p))
      const bad = (L.deps || []).filter(d => {
        const s = settled[d] ? sameSt[same.indexOf(d)] : (STATE[d] && STATE[d].status)
        return !SATISFIED.includes(s)
      })
      if (bad.length) {
        log(`${L.slug}: skipped, deps not merged: ${bad.join(', ')}`)
        return { slug: L.slug, status: 'skipped', notes: 'deps not merged: ' + bad.join(', ') }
      }
      const retryPrev = prev && RETRY.includes(L.slug) ? Object.assign({}, prev, { status: 'active' }) : prev
      const notStarted = { slug: L.slug, status: 'stopped', notes: 'run stopping: lane not started' }
      if (STOPPING) return notStarted
      return withHot(L.hot || [], () => laneSem(() => STOPPING ? notStarted : lifecycle(L, retryPrev, RETRY.includes(L.slug))))
    }
    const rs = await parallel(mine.map(L => async () => {
      let r
      try { r = await runOne(L) } catch (e) { r = { slug: L.slug, status: 'failed', notes: String(e) } }
      if (r.status === 'stopped') STOPPING = true
      settled[L.slug].res(r.status)
      STATE[L.slug] = Object.assign({}, STATE[L.slug], { status: r.status, pr: r.pr })
      return r
    }))
    results.push(...rs.filter(Boolean))
    log(`track ${TRACK}: wave ${wi + 1} done (${rs.filter(r => r && r.status === 'merged').length}/${mine.length} merged); waiting for the other tracks`)
    let b = null
    for (let tries = 0; tries < 20; tries++) {
      b = await agentAs('coord', barrierPrompt(wi, wi === waves.length - 1), { label: `barrier:w${wi + 1}`, phase: 'Coordinate', model: M.coord, effort: 'low', schema: LINES_SCHEMA })
      if (b && (b.passed || b.stop)) break
    }
    if (!b || b.stop || !b.passed) { log(`track ${TRACK}: stopping after wave ${wi + 1} (${b && b.stop ? 'STOP' : 'barrier did not pass'})`); break }
    STATE = Object.assign(STATE, parseLines(b.lines))
  }
  return results.map(r => ({ slug: r.slug, status: r.status, pr: r.pr, rounds: r.rounds, resumed: r.resumed, merge_sha: r.merge_sha, notes: r.notes, history: r.history,
    summary: r.impl ? r.impl.summary : undefined, closes: r.impl ? r.impl.closes : undefined, followups: r.impl ? r.impl.followups_filed : undefined,
    review: r.review ? r.review.summary : undefined }))
}

// ---------------------------------------------------------------------------
// Merger role: one merge agent at a time, for every track
// ---------------------------------------------------------------------------
async function mergerMain() {
  phase('Merge')
  const handled = []
  let requeued = false
  log(`merger: serving merge requests for tracks ${TRACKS.join(',')}`)
  for (let i = 0; i < 900; i++) {
    const t = await agentAs('coord', takePrompt('merge', !requeued), { label: 'take', phase: 'Merge', model: M.coord, effort: 'low', schema: TAKE_SCHEMA })
    if (!t) continue
    requeued = true // only a take that actually ran has re-served the orphans
    if (t.stop) break
    for (const ev of (t.events || [])) {
      const slug = String(ev.from).split('/')[0]
      const L = LANE_BY[slug] || { slug, issues: [] }
      const m = await agentAs('merge', mergePrompt(ev, L), { label: `merge:${slug}`, phase: 'Merge', model: M.merge, schema: MERGE_SCHEMA })
      if (!m || !m.answered) {
        await agentAs('deliver', deliverPrompt(ev.id, JSON.stringify({ status: 'retry', notes: 'merge agent ' + (m ? 'did not answer' : 'failed') })), { label: `deliver:${ev.id}`, phase: 'Merge', model: M.deliver, effort: 'low' })
      }
      handled.push(`${slug}: ${m ? m.status : 'failed'}`)
      log(`merger: ${slug} ${m ? m.status : 'failed'}${m && m.merge_sha ? ' ' + m.merge_sha : ''}`)
    }
  }
  return { handled }
}

// ---------------------------------------------------------------------------
// Helper role: serve helper requests from every track
// ---------------------------------------------------------------------------
async function helperMain() {
  phase('Helpers')
  const pending = []
  const perLane = {}
  const served = []
  let requeued = false
  const deliver = (id, text) => agentAs('deliver', deliverPrompt(id, text), { label: `deliver:${id}`, phase: 'Helpers', model: M.deliver, effort: 'low' })
  const HELPER_ROLE = { 'cs-scout': 'scout', 'rfc-researcher': 'researcher', 'runner': 'runner' }
  const helperOpts = ev => {
    const base = { label: `${ev.type}:${ev.from}`, phase: 'Helpers' }
    if (ev.type === 'cs-scout') return Object.assign(base, { model: M.scout })
    if (ev.type === 'rfc-researcher') return Object.assign(base, { model: M.researcher })
    return Object.assign(base, { model: M.runner, effort: 'low' })
  }
  const helperRun = ev => HELPER_ROLE[ev.type]
    ? agentAs(HELPER_ROLE[ev.type], helperPrompt(ev), helperOpts(ev))
    : Promise.resolve(`ERROR: unknown helper type ${ev.type}. Use cs-scout, rfc-researcher or runner.`)
  log(`helper broker: serving helper requests for tracks ${TRACKS.join(',')}`)
  for (let i = 0; i < 900; i++) {
    const t = await agentAs('coord', takePrompt('helper', !requeued), { label: 'take', phase: 'Helpers', model: M.coord, effort: 'low', schema: TAKE_SCHEMA })
    if (!t) continue
    requeued = true // only a take that actually ran has re-served the orphans
    if (t.stop) break
    for (const ev of (t.events || [])) {
      const lane = String(ev.from).split('/')[0]
      if (ev.type === 'rfc-researcher') {
        perLane[lane] = (perLane[lane] || 0) + 1
        if (perLane[lane] > RESEARCHER_LIMIT) {
          pending.push(deliver(ev.id, `DENIED: the rfc-researcher limit (${RESEARCHER_LIMIT} per lane) is used up for ${lane}. Ask cs-scout for code locations or research inline.`))
          continue
        }
      }
      served.push(`${ev.type} ${ev.from}`)
      pending.push(helperRun(ev).then(ans =>
        deliver(ev.id, ans ? String(ans) : `ERROR: the ${ev.type} helper failed. Research inline.`)))
    }
  }
  await Promise.all(pending)
  log(`helper broker: served ${served.length} requests`)
  return { served }
}

if (ROLE === 'merger') return await mergerMain()
if (ROLE === 'helper') return await helperMain()
return await trackMain()
