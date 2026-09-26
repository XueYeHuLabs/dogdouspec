# Technical Contract: Unified Progression Decisions, Blocker Recovery, and VCS Readiness

Iteration: `20260908-progression-recovery`
Governing Specifications:
- [spec.xml](../../../.dogdouspec/20260908-progression-recovery/spec.xml) (Requirements req-pr-01..08, Decisions design-pr-01..04, Acceptance criteria accept-pr-01..09)
- [PLAN.md](PLAN.md) (Sequencing, Verification Matrix, Completion Boundaries)

---

## 1. Shared Progression Fact and Action Assessment Model

### 1.1 Action Categories and Semantics
Core provides a unified, read-only progression assessment engine reused by `task next`, `iteration summary`, `task summary`, and `iteration readiness`.

| Action Category | Preconditions | Recommended Next Command | Allowed Roles |
|---|---|---|---|
| `start-work` | First pending task with all dependencies satisfied. | `dogdouspec task start --task <ID>` | Implementer (`@agent`) / Any (if unassigned) |
| `continue-work` | Task in `in-progress`, no blocking findings, not in verification. | `dogdouspec task verify` (upon completion of work) | Implementer (`@agent`) |
| `verify-work` | Task in `verification`, running checks, or `in-progress` transitioning to verification. | `dogdouspec task verify` or `task finish` | Implementer / Reviewer |
| `review-required` | Task in `verification`, `<review required="true"/>`, no approved submission yet. | `dogdouspec task review` | Independent Reviewer (`actor != task.@agent`) |
| `resolve-findings`| Task has active finding records (`record kind="finding" status="active"`). | Address issues, append `record kind="resolution" status="resolved"` | Implementer |
| `resume-task` | Task is `blocked`, all active blockers resolved, dependencies satisfied, iteration unfrozen. | `dogdouspec task resume` | Implementer |
| `wait-dependency` | Pending task whose upstream `depends-on` tasks are not yet terminal/done. | Wait for upstream tasks or select alternative ready task | Any |
| `wait-external` | Task in `blocked` status waiting for external condition / blocker owner. | Inspect with `dogdouspec task blockers` | Blocker Owner / Team |
| `owner-decision` | Iteration is `draft`, `replanning`, unapproved requirements exist, or product acceptance pending. | `dogdouspec iteration confirm` | Human Owner |
| `no-tasks` | Iteration has zero tasks in `tasks.xml`. | `dogdouspec task quick` or `task add` | Any |
| `execution-terminal`| All tasks in iteration are terminal (`done`, `cancelled`, `transferred`, `superseded`). | Inspect readiness with `dogdouspec iteration readiness` | Owner / Lead |

> **Critical Invariant**: Cancellation, transfer, and supersession are execution-terminal states but do NOT constitute successful delivery. If all tasks are terminal but include non-done states, completion percentage and delivery reporting must explicitly distinguish terminal completion from successful delivery.

### 1.2 Progression Reason Codes

| Reason Code | Category | Definition |
|---|---|---|
| `ACTIVE_TASK_IN_PROGRESS` | `continue-work` | An active task is currently in-progress. |
| `ACTIVE_TASK_VERIFICATION` | `verify-work` | Active task is currently in verification. |
| `ACTIVE_TASK_REVIEW_REQUIRED` | `review-required` | Active task in verification requires independent review before finish. |
| `ACTIVE_TASK_REVIEW_CHANGES_REQUESTED` | `resolve-findings` | Review submission requested changes; findings must be resolved. |
| `ACTIVE_TASK_ACTIVE_FINDINGS` | `resolve-findings` | Active finding records exist on the task. |
| `PENDING_TASK_READY` | `start-work` | First pending task with all dependencies satisfied is ready to execute. |
| `PENDING_TASK_DEPENDENCIES_UNSATISFIED` | `wait-dependency` | Pending task has unmet upstream dependencies. |
| `DEPENDENCY_CYCLE_DETECTED` | `diagnostic` | Structural defect: cycle detected in task dependency graph. |
| `DEPENDENCY_MISSING_TARGET` | `diagnostic` | Structural defect: dependency target task or document does not exist. |
| `REQUIREMENT_NOT_APPROVED` | `owner-decision` | Origin requirement is not in approved status. |
| `TASKS_BLOCKED` | `wait-external` | All non-terminal tasks are currently blocked. |
| `TASKS_TERMINAL_INCOMPLETE` | `execution-terminal` | All tasks terminal, but non-done (cancelled/transferred/superseded) exist. |
| `ALL_TASKS_DONE` | `execution-terminal` | All tasks in iteration are done with verified criteria. |
| `ITERATION_DRAFT` | `owner-decision` | Iteration is draft; activation required before execution. |
| `ITERATION_REPLANNING` | `owner-decision` | Iteration is replanning; execution transitions frozen. |
| `ITERATION_COMPLETED` | `execution-terminal` | Iteration is completed. |
| `ITERATION_SUPERSEDED` | `execution-terminal` | Iteration has been superseded. |
| `ITERATION_CANCELLED` | `execution-terminal` | Iteration is cancelled. |
| `NO_TASKS` | `no-tasks` | No tasks exist in iteration. |

### 1.3 Candidate Ordering and Agent Filtering
- **Stable Document Order**: Candidate tasks are evaluated and returned in exact document order as defined in `tasks.xml`.
- **Default Selection**: First actionable candidate in document order.
- **Agent Filtering (`--agent <name>`)**:
  - Filters tasks where `@agent` equals the requested name, OR tasks where `@agent` is omitted (unassigned).
  - For tasks requiring review (`review-required`), an agent cannot review their own task (`actor != task.@agent`).

---

## 2. Checkpoint Readiness Dimension (`vcs_checkpoint`)

### 2.1 VCS Dimension States
Integrated into `dogdouspec iteration readiness`:

| Status | Condition | Message / Output Detail |
|---|---|---|
| `passed` | Workspace is Git repo, `git status` succeeded, zero uncheckpointed authoritative documents (`uncheckpointed.Count == 0`). | "Authoritative documents are clean and checkpointed" |
| `failed` | Workspace is Git repo, `git status` succeeded, but uncheckpointed authoritative documents exist (`uncheckpointed.Count > 0`). | "Uncheckpointed authoritative documents exist: [file list with status: untracked, modified]" |
| `unknown` | Git is installed but `git status` failed with an error, or inspection failed. | "VCS inspection failed: [error details]. Unknown status cannot be treated as passed." |
| `not-applicable` | Workspace is not inside a Git repository. | "Non-Git workspace: VCS checkpoint dimension not applicable." |

### 2.2 Invariants
1. `unknown` MUST NOT be treated as `passed`.
2. Read-only guarantee: VCS readiness inspection MUST NOT run `git commit`, `git add`, `git reset`, or modify `.gitignore`, working tree, index, or HEAD.
3. VCS readiness is an independent diagnostic dimension; it does not replace human owner confirmation and does not introduce automatic git commits.

---

## 3. Blocker Workflow and Recheck Queues

### 3.1 V1 Schema-Compliant Blocker Storage
Blockers are stored within the owning `<task>` under `<records>` as a `<record kind="finding" status="active">`:

```xml
<record id="20260908T100000Z-finding-blocker" kind="finding" status="active" created_at="2026-09-08T10:00:00Z" actor="codex">
  <index>
    <summary>Blocked waiting for external API key</summary>
    <term key="blocker-kind" value="external"/>
    <term key="blocker-owner" value="owner"/>
    <term key="blocker-review-at" value="20260909T120000Z"/>
  </index>
  <summary>External API key missing</summary>
  <context>Resolution condition: owner provisions credentials in vault</context>
  <outcome>Resume task and run integration tests</outcome>
</record>
```

- **Index Keys**:
  - `blocker-kind`: `TokenValueType` (`external`, `dependency`, `environment`, `owner`, `review`, etc.).
  - `blocker-owner`: `TokenValueType` (responsible entity, e.g. `owner`, `agent`, `infrastructure`).
  - `blocker-review-at`: `TokenValueType` in compact UTC ISO format `yyyyMMddTHHmmssZ` (no colons, conforming to `TokenValueType` pattern `[a-zA-Z0-9][a-zA-Z0-9.-]*`).
- **Historical Compatibility**: If a historical finding record lacks `blocker-*` index terms, it is displayed with `blocker-kind="unknown"`, `blocker-owner="unspecified"`, and `blocker-review-at="none"`.

### 3.2 Blocker Resolution and Task Recovery
- **Resolution**:
  - Adding a resolution record `<record kind="resolution" status="resolved">` with:
    ```xml
    <covers>
      <ref scope="document" target="20260908T100000Z-finding-blocker" relation="resolves"/>
    </covers>
    ```
  - Or updating the active finding's status from `active` to `resolved`.
- **Multiple Blockers**: A task may have multiple active blocker findings. The task remains `blocked` as long as at least one active blocker remains.
- **Recovery Conditions**:
  - Task can only transition `blocked` -> `in-progress` when ALL active blockers on the task are resolved, upstream dependencies remain satisfied, origin requirement remains approved, and iteration is not in `replanning`.
  - Reaching a `blocker-review-at` timestamp does NOT automatically unblock the task. It only flags the item as due in `task blockers`.

### 3.3 Blocker Queue View (`task blockers`)
- Public command: `dogdouspec task blockers [--iteration <ID>] [--task <ID>] [--owner <OWNER>] [--due-only] [--format <xml|human>]`
- Read-only: does not modify state. Combines:
  1. Explicit task blocker findings.
  2. Derived dependency blockers (unmet upstream tasks).
  3. General active findings.
- Sorted deterministically: overdue items first, followed by upcoming due items, followed by undated items, in stable document order.

---

## 4. Bounded Task Recovery Context (`task context`)

### 4.1 Specification and Interface
- Public command: `dogdouspec task context --task <ID> [--iteration <ID>] [--max-bytes <N>] [--format <xml|human>]`
- Default size bound: 32,768 bytes (32 KB).

### 4.2 Content Layers and Truncation Rules
1. **Essential Context (Non-Truncatable)**:
   - Task Objective, Scope (included/excluded), and Constraints.
   - Origin Requirement statement and key points.
   - Acceptance Criteria.
   - Active Blockers and Findings.
   - Permitted Next Actions and Prohibited Actions.
   - *Rule*: Essential information must NEVER be silently omitted or truncated. If essential information alone exceeds `--max-bytes`, fail closed with diagnostic error `LIMIT_EXCEEDED` and instruct caller to increase limit or narrow scope.
2. **Supplemental Context (Bounded / Truncatable)**:
   - Historical records (discussion, attempts, old verifications).
   - Upstream completed dependency summaries.
   - If historical records exceed the limit, they are truncated with explicit metadata:
     `truncated="true"`, `omitted_records="<N>"`, and query locators for loading full history via `dogdouspec query`.
3. **Traceability**:
   - Every context response includes source document revisions: `tasks_revision`, `spec_revision`, and referenced dependency revisions.
   - Recovery must be fully functional from authoritative XML alone, with zero reliance on transient agent reports (e.g. `.agents/work-results/`).

---

## 5. Compatibility and Governance Guarantees

1. **Schema Integrity**: All structures remain 100% compliant with XSD v1 schemas (`common.xsd`, `tasks.xsd`, `spec.xsd`, `requests.xsd`). No schema evolution is required.
2. **Exact Revision Locks**: All mutating commands require exact expected revisions (`--expected-revision`) and fail closed on revision conflict (`REVISION_CONFLICT`).
3. **Exit Codes**:
   - `0`: Success.
   - `2`: Invalid argument, XML parse error, or target document/iteration/task not found.
   - `3`: Schema validation error, dependency cycle, or reference violation.
   - `4`: Revision conflict, lock conflict, idempotency conflict, or task state transition conflict.
   - `5`: Owner decision required or iteration replanning execution frozen.
   - `6`: Filesystem, initialization, commit, or recovery failure.
   - `7`: Document or context limit exceeded.
4. **Authority Boundaries**: No command automatically completes iterations, accepts product criteria, claims tasks, or executes Git commits. Product acceptance and final iteration completion remain exclusive human owner decisions.
