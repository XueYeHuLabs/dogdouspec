# Mutation Operations Reference

DogdouSpec enforces single-source-of-truth document integrity through structured, schema-validated mutations. Direct file editing of `.dogdouspec/*.xml` is prohibited.

## Mutation Decision Matrix

| Operation | Command | Primary Use Case | Concurrency & Idempotency |
| :--- | :--- | :--- | :--- |
| **Task Update** | `dogdouspec task update` | Task state machine transitions (`start`, `verify`, `complete`, etc.), acceptance criteria updates, context snapshots, and appending execution records. | Single-document atomic commit. Persists durable `operation_id` receipts. Replays are deeply verified for idempotent success. |
| **Generic Append** | `dogdouspec append` | Appending a single valid child element to a managed document container (e.g. adding knowledge entries or backlog items). | Single-document atomic commit. Revision-checked. Performs identity-based deduplication for elements with unique `@id`. |
| **Transaction Apply** | `dogdouspec transaction apply` | Multi-document atomic transactions, structural replacements, batch initialization, and low-level task scope adjustments. | Multi-document staged publish with crash recovery. Correlated by `operation_id`. Non-durable (no separate business receipt container); stale retry conflicts if revision advanced. |
| **Iteration Confirm** | `dogdouspec iteration confirm` | Owner-gated product decisions: iteration activation, design change acceptance, replanning, continuation, completion, cancellation, or supersession. | Protected single-document write to `spec.xml`. Reads `tasks.xml` for consistency/readiness verification without modifying it. Requires current-interaction human owner instruction. |

## Document Revisions

Every managed document root contains an authoritative `revision="N"` attribute owned by the engine.

- All mutating CLI commands require the caller to provide `--expected-revision <N>` or `<document expected_revision="N">`.
- If the document has been modified concurrently (`actualRevision != expectedRevision`), the command fails with `REVISION_CONFLICT` (exit code `4`).
- Callers must re-query the current document state and recalculate the mutation.

## Multi-Document Transaction Visibility & Filesystem Semantics

As specified in `docs/V1_CLI_CONTRACT.md` (Section 11):

1. **Project Locking**: Writers acquire a single `.dogdouspec` project lock. Readers do not acquire the writer lock.
2. **Individual Whole-File Atomic Replacements**: Writers stage complete replacement files in a workspace-local temporary directory, flush them, and replace target files individually via atomic rename operations.
3. **Unlocked Reader Visibility During Publish**: During a multi-document publish across multiple files, concurrent readers may temporarily observe a mix of complete old and complete new revisions across different files. Readers always observe a complete valid document (never a partial or corrupted file). Simultaneous multi-file visibility across the entire filesystem is not claimed.
4. **Crash Recovery Convergence**: If a process terminates mid-publish, a minimal recovery marker in the CLI temporary area allows startup recovery to complete or roll back the prepared transaction to a single valid set before serving any subsequent write.
5. **No Success on Partial Commit**: The engine never returns success on a partial commit.

## Durable vs Non-Durable Idempotency & Retry Protocols

### 1. Task Update (`task update`) - Durable Receipt
- Every `<task-update>` request requires a unique `id` (e.g. `20260823T054000Z-update-task-start`).
- When applied, the engine stamps `operation_id` on all created `<record>` elements.
- **Idempotent Replay**: If retried with the same `operation_id` and the live document matches all requested final states, timestamps, and criteria results, the command returns `already_applied="true"` with exit code `0`.
- If live state has drifted, the command returns `IDEMPOTENCY_CONFLICT` (exit code `4`).

### 2. Generic Append (`append`) - Identity-Based Deduplication
- For container elements that require unique child identifiers (e.g. `<entry id="...">` in `knowledge.xml` or `<item id="...">` in `backlog.xml`), appending an item whose `@id` already exists with identical content succeeds idempotently (`already_applied="true"`).

### 3. Transaction Apply (`transaction apply`) - Non-Durable Transaction
- Low-level transactions use `operation_id` as a correlation token during commit and staging.
- They do not create durable business receipts on generic elements.
- Stale retries against documents whose revision advanced will return `REVISION_CONFLICT`.
- **Do not assume a failed transaction can be retried simply by incrementing `expected_revision`.** Re-query the live target documents first to verify if the intended changes were already committed before constructing a new request.

## Request XML Templates

Use `dogdouspec template show --name <NAME>` to view exact XML templates for public requests:

- `task.update`: Task update request template.
- `transaction.apply`: Multi-document transaction request template.
- `iteration.confirmation`: Iteration confirmation request template.
- `record.discussion`: Discussion record template for task records.
- `record.finding`: Finding record template for task records.
- `record.verification`: Verification record template for task records.
- `knowledge.entry`: Knowledge entry template for `knowledge.xml`.
- `backlog.item`: Backlog item template for `backlog.xml`.

Example:
```powershell
.\dogdouspec.cmd template show --name task.update
.\dogdouspec.cmd template show --name iteration.confirmation
```