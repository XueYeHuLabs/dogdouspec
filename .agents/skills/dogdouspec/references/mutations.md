# Mutation Operations Reference

DogdouSpec enforces single-source-of-truth document integrity through structured, schema-validated mutations. Direct file editing of `.dogdouspec/*.xml` is prohibited.

## Mutation Decision Matrix

| Operation | Command | Primary Use Case | Concurrency & Idempotency |
| :--- | :--- | :--- | :--- |
| **Task Update** | `dogdouspec task update` | Task state machine transitions (`start`, `verify`, `complete`, etc.), acceptance criteria updates, context snapshots, active record resolution, and appending execution records. Supports `--revision latest` opt-in. | Single-document atomic commit. Persists durable `operation_id` receipts. Replays are deeply verified for idempotent success. Execution transitions fail closed when iteration is `replanning`. |
| **Task Record** | `dogdouspec task record` | Appending structured records (`discussion`, `finding`, `verification`, `completion`, `decision`) directly to a task without manual XML files. | Single-document atomic commit to `tasks.xml`. Auto-resolves revision. Monotonic timestamp verification. |
| **Task Review** | `dogdouspec task review` | Structured approval or changes-requested submission for a Task with `review required="true"`. | Approval actor must differ from immutable Task `@agent` attribution. Changes requested creates an active finding and returns the Task to `in-progress`. Separation is provenance, not authenticated identity. |
| **Task Add** | `dogdouspec task add` | Appending a new pending task referencing an existing requirement in `spec.xml`. Supports `--revision latest` opt-in. | Single-document atomic commit. Revision-checked. Verified origin reference. Durable `operation_id` stamping. |
| **Task Quick** | `dogdouspec task quick` | Compact bounded work intended to execute now. Inputs expand to a normal Task; no second task type or file exists. | `--start` creates the final in-progress Task, start history, and receipt in exactly one `tasks.xml` revision. `--dry-run` writes nothing. |
| **Task Revise** | `dogdouspec task revise` | Elaborating constraints, dependencies, acceptance criteria, or scope on active/pending tasks. A started task cannot replace rationale and may only expand scope. Rejects terminal tasks (`TASK_IMMUTABLE`). | Single-document atomic commit. Revision-checked. Durable `operation_id` stamping. |
| **Task Split** | `dogdouspec task split` | Transitioning a parent task to a terminal disposition (`superseded`/`transferred`/`cancelled`) and atomically adding 2+ pending subtasks. Supports `--revision latest` opt-in. | Single-document atomic commit. Revision-checked. Durable `operation_id` stamping. |
| **Task Block** | `dogdouspec task block` | Transitioning an active task to `blocked` (or recording a blocker finding without state change via `--record-only`) and recording kind, owner, and target recheck timestamp. | Single-document atomic commit to `tasks.xml`. Revision-checked. Appends finding record with `blocker-*` index terms. |
| **Task Resume** | `dogdouspec task resume` | Resolving active blocker findings and transitioning a blocked task back to `in-progress`. | Single-document atomic commit to `tasks.xml`. Revision-checked. Fails closed if other active blockers remain, upstream dependencies are unmet, origin requirements are not approved, or iteration is in `replanning`. |
| **Requirement Propose** | `dogdouspec requirement propose` | Proposing a new requirement with `status="proposed"`. Rejects non-proposed statuses (`OWNER_DECISION_REQUIRED`). | Single-document atomic commit to `spec.xml`. Revision-checked. Durable `operation_id` stamping. |
| **Change Propose** | `dogdouspec change propose` | Attaching one or more active finding receipts to tasks, freezing target tasks to `blocked`, and proposing requirements across documents. | 2-document atomic commit (`spec.xml` + `tasks.xml`). Requires `active` iteration status. Immediate identical replay is durable; later revision drift is rejected. |
| **Change Apply** | `dogdouspec change apply` | Resolving active findings, setting terminal task dispositions, and adding successor tasks during `status="replanning"`. | Recovery-backed commit to `tasks.xml`; a deterministic informational receipt is appended to the first impacted task. No-op application is rejected; immediate identical replay is durable. |
| **Generic Append** | `dogdouspec append` | Appending a single valid child element to a managed document container (e.g. adding knowledge entries or backlog items). | Single-document atomic commit. Revision-checked. Performs identity-based deduplication for elements with unique `@id`. |
| **Transaction Apply** | `dogdouspec transaction apply` | Low-level multi-document escape hatch, structural replacements, batch initialization. Supports `--revision latest` opt-in. | Multi-document staged publish with crash recovery. Correlated by `operation_id`. Non-durable (no separate business receipt container); stale retry conflicts if revision advanced. |
| **Iteration Confirm** | `dogdouspec iteration confirm` | Owner-gated product decisions: iteration activation, design change acceptance, replanning, continuation, completion, cancellation, or supersession. | Protected single-document write to `spec.xml`. Reads `tasks.xml` for consistency/readiness verification without modifying it. Requires current-interaction human owner instruction. |

## Terminal Task Immutability

Tasks in `done`, `transferred`, `superseded`, or `cancelled` statuses represent historical facts:
- Attempts to transition terminal tasks or modify their acceptance criteria, constraints, or scope return `TASK_IMMUTABLE` (exit code `4`).
- Attempts to append non-informational records (`completion`, `start`, or active `finding` records) return `TASK_IMMUTABLE`.
- Appending informational discussion or handoff records to terminal tasks remains permitted.

## Blocker Storage and Recovery Contracts

Blockers are stored within the owning `<task>` under `<records>` as an active finding record (`<record kind="finding" status="active">`):
- **Index Terms**: Store `blocker-kind` (e.g. `external`, `dependency`, `environment`, `owner`, `review`), `blocker-owner` (responsible entity, e.g. `owner`, `agent`), and `blocker-review-at` formatted as a compact UTC timestamp `yyyyMMddTHHmmssZ` conforming to `TokenValueType`.
- **Context & Outcome**: Record the unblocking condition in `<context>` and the follow-up execution action in `<outcome>`.
- **Resolution**: Resolving a blocker appends a resolution record (`<record kind="resolution">`) targeting the finding via `<covers><ref relation="resolves" target="<FINDING_ID>"/></covers>`, or sets the finding status to `resolved`.
- **Unblocking Gate**: `task resume` transitions the task to `in-progress` only when all active blocker findings are resolved, dependencies are satisfied, origins are approved, and the iteration is not in `replanning`.
- **Record-Only Mode**: `task block --record-only` records an active blocker finding without transitioning the task's state to `blocked`.
- **Blocker Queue & Modes**: `task blockers` aggregates active blockers, unmet dependencies, and active findings, deterministically ordered by review due dates. Filter with `--mode all` (default), `--mode status-blocked`, or `--mode record-only`.

## Bounded Task Recovery Context (`task context`)

`task context` provides a structured, fail-closed view for newly spawned coding sessions to resume work without loading transient external reports:
- **Essential Context (Unconditionally Preserved)**: Title, objective, rationale, scope, constraints, origin requirements, acceptance criteria, active blockers, latest status records, and progression facts (action category, reason code, recommended action, permitted actions, prohibited actions, and authority boundaries).
- **Fail-Closed Size Enforcement**: If Essential Context alone exceeds `--max-bytes` (default: 32 KB), the query fails closed with `LIMIT_EXCEEDED` (exit code `7`).
- **Greedy Supplemental Budgeting**: Historical records are budgeted into the remaining byte allocation; omitted records produce `truncated="true"`, `omitted_records="<N>"`, and a query locator.
- **Traceability**: Output reports exact `tasks_revision`, `spec_revision`, and all source document paths.

## Replanning Execution Freeze

When an iteration enters `status="replanning"`:
- Technical execution progress commands (`task update` with `transition="start"`, `"resume"`, `"verify"`, or `"complete"`) fail closed with `ITERATION_REPLANNING_EXECUTION_FROZEN` (exit code `5`).
- Planning and disposition operations (`task add`, `task split`, `task update` with `transition="supersede"|"transfer"|"cancel"`, `change apply`) remain enabled so agents can construct the new plan.

Generated operation receipts store a canonical-XML `request-sha256` value and readable reasons. This is only an idempotency fingerprint: it is not a signature, evidence hash, or authentication mechanism.

## Time and Input Bounds

The task/change/requirement helper requests are rejected before parsing when their UTF-8 payload exceeds the configured XML document limit; any managed document they read is checked against the same bound before it is opened. A write request cannot backdate a modified task or document: `occurred_at` must be at least its current `updated_at` and `created_at`. When backdating is attempted, the engine rejects the request with an `INVALID_ARGUMENT` diagnostic clearly reporting the minimal acceptable timestamp. New tasks are pending except `task quick --start`, which atomically creates a normal in-progress task with `created_at=updated_at=started_at` and a start record.

## Document Revisions

Every managed document root contains an authoritative `revision="N"` attribute owned by the engine.

- Mutating CLI commands require revision control: pass `--expected-revision <N>`, `--expected-spec-revision <N>`, `--expected-tasks-revision <N>`, or opt in to auto-resolution with `--revision latest` (supported on `task update`, `task add`, `task split`, and `transaction apply`).
- If explicit expected revisions do not match the current document revision (`actualRevision != expectedRevision`), the command fails with `REVISION_CONFLICT` (exit code `4`).
- Callers must re-query the current document state and recalculate the mutation when revision conflict occurs.

## Multi-Document Transaction Visibility & Filesystem Semantics

As specified in `docs/V1_CLI_CONTRACT.md` (Section 11):

1. **Project Locking**: Writers acquire a single `.dogdouspec` project lock. Readers do not acquire the writer lock.
2. **Individual Whole-File Atomic Replacements**: Writers stage complete replacement files in a workspace-local temporary directory, flush them, and replace target files individually via atomic rename operations.
3. **Unlocked Reader Visibility During Publish**: During a multi-document publish across multiple files, concurrent readers may temporarily observe a mix of complete old and complete new revisions across different files. Readers always observe a complete valid document (never a partial or corrupted file). Simultaneous multi-file visibility across the entire filesystem is not claimed.
4. **Crash Recovery Convergence**: If a process terminates mid-publish, a minimal recovery marker in the CLI temporary area allows startup recovery to complete or roll back the prepared transaction to a single valid set before serving any subsequent write.
5. **No Success on Partial Commit**: The engine never returns success on a partial commit.

## Request XML Templates

Use `dogdouspec template list` to discover all available built-in request templates, their categories, descriptions, and target documents. Use `dogdouspec template show --name <NAME>` to view exact XML templates for public requests:

- `task.update`: Task update request template.
- `task.review`: Structured Task review request template.
- `task.add`: Task add request template.
- `task.revise`: Task revise request template.
- `task.split`: Task split request template.
- `requirement.propose`: Requirement propose request template.
- `change.propose`: Mid-flight change propose request template.
- `change.apply`: Replanned change apply request template.
- `transaction.apply`: Multi-document transaction request template.
- `iteration.confirmation`: Iteration confirmation request template.
- `record.discussion`: Discussion record template for task records.
- `record.finding`: Finding record template for task records.
- `record.verification`: Verification record template for task records.
- `knowledge.entry`: Knowledge entry template for `knowledge.xml`.
- `backlog.item`: Backlog item template for `backlog.xml`.

Example:
```powershell
dogdouspec template list
dogdouspec template show --name task.add
dogdouspec template show --name change.propose
dogdouspec template show --name change.apply
```
