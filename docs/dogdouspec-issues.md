# DogDouSpec CLI Issue Report

**Date:** 2026-09-25
**Reported by:** Iteration orchestrator session (Copilot), rpacu v2
**CLI version observed:** `1.0.3+f58934c4147b61067e2ae9a0a92b3cf5f2e1f819`
**Context:** Iteration `20260922-mcp-substrate` — 34 tasks, 33 completed, tasks.xml grew from r136 to r474 with 513 records (735 KB) across multiple orchestrator sessions; ~27 governed commits; zero direct XML edits; workspace validation passed after every mutation.

Findings come from first-hand operation of the CLI during the r136–r274 range (~140 governed mutations, ~60 hand-authored mutation-request files) plus verification of the r274–r474 range produced by later sessions. Severity reflects actual observed impact on a real governed iteration, not hypothetical risk.

---

## Issue summary

| # | Severity | Title |
|---|----------|-------|
| 1 | High | Timestamp monotonicity trap between porcelain and low-level commands |
| 2 | High | No porcelain path for structured records; all governance load falls on hand-written XML |
| 3 | Medium | Acceptance-criteria coverage validated at `complete`, not at `verify` |
| 4 | Medium | Poor discoverability of request structures (no `template list`; errors lack expected shapes) |
| 5 | Medium | XML-escaping hazards in prose record text |
| 6 | Medium | No archival strategy for record growth in long iterations |
| 7 | Low | Document-scoped operation/record IDs produce misleading cross-task conflict errors |
| 8 | Low | Iteration index summary goes stale after lifecycle transitions |
| 9 | Low | Blocking is binary: no record-only blocker that preserves task status |
| 10 | Low | Miscellaneous ergonomics: `ds:filter` member grammar, mandatory `--expected-revision` for low-level updates |

---

## Issue 1 — Timestamp monotonicity trap between porcelain and low-level commands

**Severity:** High · **Category:** Correctness / ergonomics

**Problem.** Low-level `task update` accepts an explicit `occurred_at`; porcelain commands (`task verify`, `task finish`, `task review approve`, …) auto-generate `occurred_at` from the wall clock. Both enforce `occurred_at >= task/@updated_at`. Once any record is stamped ahead of the wall clock (e.g. an orchestrator writing a batch of records with estimated timestamps), **every porcelain mutation on that task fails permanently**:

```
INVALID_ARGUMENT: task-update @occurred_at '2026-09-23T17:00:45Z' cannot be
earlier than current task updated_at '2026-09-23T17:15:00Z'.
```

**Evidence.** Hit at least four times in one session (T01 verify, T17 complete, a blocker record, and one more). After the first occurrence the orchestrator was forced to keep writing artificially increasing future timestamps (17:17Z → 23:35Z) for the rest of the session, decoupling the whole record timeline from real time, and porcelain commands remained unusable for those tasks.

**Impact.**
- Porcelain commands become unusable for any task whose records ever ran ahead of the clock.
- Record timelines silently drift from wall-clock truth, weakening the audit value of `created_at`/`occurred_at`.
- The failure mode is surprising and recururs; recovery requires guessing the "minimal acceptable timestamp".

**Required improvement.**
1. Add `--occurred-at <ts>` to all porcelain mutating commands (opt-in override).
2. Or: when the auto-clock would violate monotonicity, either accept the task's `updated_at` as the effective timestamp with a warning, or fail with a message that states the **minimal acceptable timestamp** explicitly.
3. Consider accepting equal timestamps (`occurred_at == updated_at`) for record-only appends.

**Expected outcome.** Porcelain and low-level commands become freely interchangeable on the same task; record timestamps stay truthful; the monotonicity invariant is still enforced but no longer bricks a task's porcelain path.

---

## Issue 2 — No porcelain path for structured records; all governance load falls on hand-written XML

**Severity:** High · **Category:** Ergonomics / adoption

**Problem.** Structured task records — `kind="verification|finding|resolution|completion"`, `<covers>` references, `<resolve-records>` — can only be authored through low-level `task update` request files (XML). Porcelain commands offer a single free-text `--summary` and nothing else. In a multi-agent workflow where child agents must not touch governed state (by design), the orchestrator must transcribe every dispatch, delivery, review finding, correction, recheck, verification and completion into hand-built XML with correct escaping, IDs, revisions and timestamps.

**Evidence.** 565 files accumulated under `.dogdouspec/_tmp/dispatch/` in one iteration, a large fraction of them `task-update` request XML authored solely to append structured records. A typical task closure (delivery record → findings → correction → recheck → verify with covers → review → complete → commit-ref) cost 6–8 hand-written XML files.

**Impact.**
- Governance throughput is bound by orchestrator XML authoring, not by the work itself.
- Every XML file is an opportunity for the escaping and ID pitfalls of Issues 5 and 7.
- Discourages exactly the behavior DogDouSpec wants (rich semantic records) because the cheapest path is the `--summary` string, which loses structure.

**Required improvement.** Add porcelain record commands, for example:

```
dogdouspec task record --task <ID> --kind verification \
  --covers crit-1 --covers crit-2 \
  --resolve <finding-record-id> \
  --summary "..." [--status active|resolved|informational]
```

using the current wall clock (or `--occurred-at` per Issue 1) and auto-resolving the current revision in single-writer situations. Markdown/plain text passed via parameter should be escaped by the CLI.

**Expected outcome.** The structured-record workflow drops from "author an XML file with exact IDs, revisions, timestamps and escaping" to "one command with flags". The 6–8 XML files per task closure collapse to commands; record quality and density improve because the structured path becomes the path of least resistance.

---

## Issue 3 — Acceptance-criteria coverage validated at `complete`, not at `verify`

**Severity:** Medium · **Category:** Fail-fast / diagnostics

**Problem.** `TASK_CRITERION_NOT_COVERED` is raised when transitioning to `done`, not when entering `verification`. Additionally, an `<acceptance>` block with criterion results inside a low-level `verify` update does **not** by itself establish coverage — coverage requires `<covers>` references inside a task-local record.

**Evidence.** T17: the `verify` transition succeeded, the review gate was approved, and only then `complete` was rejected with `TASK_CRITERION_NOT_COVERED` for both criteria, requiring an extra corrective record and a re-run of the completion.

**Impact.** A task can pass its entire verification phase and review gate while missing the evidence linkage that completion requires; the failure surfaces at the worst possible moment (after review approval), forcing extra mutations on an already-reviewed state.

**Required improvement.** Validate criteria coverage at the `verify` transition (fail fast), and make the error message include the expected `<covers>` fragment shape. Optionally treat `<acceptance>` results in the verify update as implicit coverage candidates.

**Expected outcome.** Coverage gaps are caught when verification starts, before review effort is spent; the error is self-explanatory; no post-review corrective mutations.

---

## Issue 4 — Poor discoverability of request structures

**Severity:** Medium · **Category:** Onboarding / diagnostics

**Problem.** The exact XML shapes for `iteration confirm` (e.g. `<requirements><requirement target=… decision=…/>` rather than `<criterion>`), `task review`, and `task update` records must currently be discovered by reading the XSDs (`requests.xsd` → `spec.xsd` `ConfirmationTargetsType`). `dogdouspec template` has `show --name` but no `list`, so template names themselves must be guessed. Error messages rarely embed the expected structure.

**Evidence.** Activation required three dry-run attempts: first `OWNER_DECISION_REQUIRED` (no hint that `<requirements>` was needed), then a schema rejection for using `<criterion>` inside `<requirements>` — the error did not name the correct element. The `template show iteration.confirmation` name had to be found in help text examples.

**Impact.** Slow, trial-and-error onboarding for the most safety-critical operations (owner confirmations); agents burn turns reading schema files; risk of wrong-shape requests being interpreted as intent errors rather than syntax errors.

**Required improvement.**
1. `dogdouspec template list` (names + one-line purpose).
2. For structure violations, embed the expected fragment in the diagnostic (the schema already knows it).
3. Reference template names from each command's help.

**Expected outcome.** First-attempt success rate for confirmation/review/update requests rises materially; agents stop reading XSDs; schema-validation errors become self-serviceable.

---

## Issue 5 — XML-escaping hazards in prose record text

**Severity:** Medium · **Category:** Ergonomics / correctness of authoring

**Problem.** Task records are prose; authoring them as raw XML means ordinary technical text can break parsing. Observed failures: `minimum <= current <= maximum` (rejected: `Name cannot begin with the '=' character`) and a literal `]]>` (rejected: `']]>' is not allowed in character data`).

**Evidence.** Two `XML_PARSE_ERROR` failures in one session, both from legitimate technical content (a provisional decision's invariant statement; a CDATA rule description).

**Impact.** Silent authoring traps; depending on escape choice, semantics can be accidentally altered while fixing (`<=` vs `&lt;=` in displayed text); wasted mutations.

**Required improvement.** Accept record text through a channel the CLI escapes itself (command parameter or a plain-text/markdown file the CLI wraps), as per Issue 2's `task record`. For the raw-XML path, make `XML_PARSE_ERROR` report the offending character position.

**Expected outcome.** Prose records can carry arbitrary technical content without XML expertise; parse errors, when they still occur, are immediately locatable.

---

## Issue 6 — No archival strategy for record growth in long iterations

**Severity:** Medium (structural) · **Category:** Scalability

**Problem.** 513 records drove `tasks.xml` to 735 KB in a single iteration. Terminal tasks are immutable and their records cannot be migrated, compressed or split out. The two-phase query pattern (`ds:filter` index first) is currently mandatory rather than an optimization, and whole-document operations (validation, any non-projected query) pay the full-document cost.

**Evidence.** Full task loads already exceeded 250 KB for the reconciliation task in the design phase; the current 735 KB is a single iteration's accumulation.

**Impact.** Long-running or multi-iteration workspaces will see monotonically growing documents with no designed relief; context-budget pressure on agents grows with history rather than with active scope.

**Required improvement.** Design an archival/snapshot mechanism — e.g. separating records of terminal tasks into per-task or per-archive documents with an index contract, or a documented compaction path that preserves the append-only audit guarantee. Keep the two-phase query working transparently across the split.

**Expected outcome.** Working-set document size tracks active scope rather than iteration age; validation and queries stay fast; the append-only audit trail is preserved.

---

## Issue 7 — Document-scoped operation/record IDs produce misleading cross-task conflicts

**Severity:** Low · **Category:** Diagnostics

**Problem.** Operation and record IDs are unique per document, not per task. Applying the same request file to a second task fails with `IDEMPOTENCY_CONFLICT: … already exists under task 'X'` — naming the *first* task, not the task being updated.

**Evidence.** Applying a combined two-task commit-reference record to T04 and T09: the first application succeeded, the second failed pointing at T04 while the operator was acting on T09.

**Impact.** Confusing diagnostics that appear to indicate cross-task contamination when the real rule is "one request file per task with distinct IDs".

**Required improvement.** Extend the error to name both tasks and state the remedy ("request IDs are document-scoped; author a separate request per task"). Document the scoping rule in the mutation reference.

**Expected outcome.** Self-explanatory failure; no misdiagnosis of state corruption.

---

## Issue 8 — Iteration index summary goes stale after lifecycle transitions

**Severity:** Low · **Category:** Reporting hygiene

**Problem.** `<iteration><index><summary>` still reads "Draft rpacu v2 design … no iteration activation" even though the iteration activated, reached technical completion of all implementation tasks, and is pending owner acceptance. `dogdouspec summary` renders fresh progress, but `iteration list` surfaces the stale text.

**Evidence.** Current `iteration list` output vs. actual r474 state (33/34 tasks done).

**Impact.** Quick-read surfaces contradict each other; a stale summary can mislead a human scanning iteration state without running the summary card.

**Required improvement.** Refresh (or invalidate with a placeholder) the index summary on lifecycle transitions such as activation and completion; or document the index as static bootstrap text and exclude it from `iteration list` output.

**Expected outcome.** All status surfaces agree; no manual correction needed.

---

## Issue 9 — Blocking is binary: no record-only blocker that preserves task status

**Severity:** Low · **Category:** Workflow modeling

**Problem.** `task block` transitions the task to `blocked`. There is no way to record a structured environment blocker (kind/owner/unblock condition) while the task remains `in-progress` — which is exactly the state wanted when work stops on one front but the record stream should continue.

**Evidence.** During a multi-hour network outage, the orchestrator recorded blocker information as hand-shaped active findings instead of using `task block`, deliberately to avoid the status change — at the cost of bypassing the `task blockers` recheck queue.

**Impact. `task blockers` under-reports real blockers in mixed situations; operators choose between accurate status and structured blocker tracking.

**Required improvement.** `task block --record-only` (or equivalent): append the structured blocker finding without the status transition, with `task blockers` listing both forms.

**Expected outcome.** Environment blockers get first-class tracking (recheck due dates, owners, unblock conditions) without forcing an inaccurate task status.

---

## Issue 10 — Miscellaneous ergonomics

**Severity:** Low · **Category:** Ergonomics

1. **`ds:filter` member grammar**: `name()` is rejected as a member ("Members must be exactly '@attribute-name' or 'direct-child-name'"), and multi-expression XPath (e.g. `count(a) , count(b)`) is an "invalid token" — each count needs a separate query. *Improvement:* allow read-only common idioms or document the per-query workaround. *Outcome:* fewer round-trips for simple inventories.
2. **Mandatory `--expected-revision` on low-level updates**: safe by design, but in verified single-writer sessions the orchestrator must parse the new revision out of every output (automated here with regex loops). *Improvement:* an explicit opt-in `--revision latest` for single-writer contexts, keeping the strict default. *Outcome:* simpler automation without weakening the default safety.

---

## Unverified areas

For completeness, these DogDouSpec capabilities were **not** exercised by this iteration and are therefore not covered by the findings above: `change propose` / replanning flow, `transaction apply`, schema upgrade path (`skill sync` / `schema sync`), multi-workspace operation, and concurrent multi-writer mutation beyond the observed idempotency guards.

---

## Priorities

1. **Issue 2** (`task record` porcelain) — largest leverage; also mitigates 1, 5 and 7.
2. **Issue 1** (timestamp override / minimal-timestamp diagnostics).
3. **Issue 3** (coverage validation at verify) and **Issue 4** (`template list` + embedded expected shapes).
4. Issues 5–9 as targeted follow-ups; Issue 6 as a design investigation for long-iteration scalability.
