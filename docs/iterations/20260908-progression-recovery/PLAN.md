# Unified Progression Decisions and Blocker Recovery: Implementation Plan

Iteration: `20260908-progression-recovery`. This document explains implementation sequencing and verification methods; it does not track task status.

- [spec.xml](../../../.dogdouspec/20260908-progression-recovery/spec.xml) is authoritative for requirements, scope, product acceptance, and design decisions.
- [tasks.xml](../../../.dogdouspec/20260908-progression-recovery/tasks.xml) is authoritative for task scope, dependencies, technical acceptance, execution records, and reviews.
- Command prototypes in this document are proposed interfaces for this iteration. Do not advertise them as existing capabilities in CLI help or skills before implementation.

## 1. Objective and Current State

Enable callers to reliably determine which work to continue, why progress is blocked, which conditions permit recovery, and which facts a new session needs to resume work.

The current source already provides task dependencies, state transitions, review gates, task inspection, scope checks, and Git diagnostics. This iteration builds on those capabilities to complete the progression workflow. Static inspection identified two priority regression scenarios:

1. `TaskNext.SelectNext` can report all tasks as terminal when no pending or active tasks remain but blocked tasks still exist.
2. The `vcs_checkpoint` dimension in `IterationReadiness` is fixed to passed and is not yet connected to actual `WorkspaceVcsStatus` results.

These are baseline findings for implementation tasks to reproduce. This planning work does not claim that the corresponding regression tests have run or passed.

## 2. Delivery Boundaries

Deliver a shared progression assessment model, integration with existing outputs, an actual VCS dimension, blocker registration and recovery commands, a read-only blocker queue, bounded recovery context, and the corresponding contracts, tests, and guidance.

This iteration excludes task leases and heartbeats, automatic execution or scheduled notifications, evidence-to-source-version binding, cross-workspace scheduling, duration forecasting, graphical interfaces, and automatic Git writes. Preserve existing cross-iteration dependencies. Retain the v1 managed schema by default; propose a change if structural extensions are necessary rather than silently changing how older CLIs read the workspace.

## 3. Proposed Design

### 3.1 Shared Facts and Progression Decisions

Provide a shared read-only assessment entry point in Core that combines lifecycle, task state, dependency satisfaction, review gates, unresolved findings, and checkpoint facts. `task next`, top-level `summary`, `task summary`, and `iteration readiness` reuse these facts and rules while retaining their own presentation granularity and product-confirmation responsibilities.

The proposed result includes at least the iteration, spec/tasks revisions read, task ID, action category, reason code, blocking object, required role, and follow-up query locator. Return candidates in stable document order with an explicit, explained default selection. This iteration introduces neither priority scheduling nor automatic claiming.

Distinguish at least continue-work, verify-work, review-required, resolve-findings, resume-task, wait-dependency, wait-external, owner-decision, no-tasks, and execution-terminal. Execution terminality does not mean product acceptance. Lifecycle states such as draft/replanning/completed must participate in assessment; do not recommend execution operations prohibited by the current lifecycle. Apply the same gate rules when a task or agent is explicitly selected.

Cross-document reads do not promise an atomic snapshot outside a lock. Report dependency-document revisions; if revisions change during reading, retry within a bound or return an explicit inconsistency diagnostic. Do not describe mixed versions as a consistent snapshot. Derived views must not become authoritative storage.

### 3.2 Checkpoint Dimension

Reuse existing read-only Git diagnostics. Distinguish passed, failed, unknown, and not-applicable, with field and exit-code compatibility policy defined in T01. Untracked, modified, staged-but-uncommitted, and ignored managed files must not be described as checkpointed. Report not-applicable for non-Git workspaces and unknown, with a reason, when Git is unavailable or inspection fails.

Keep checkpoint state separate from execution terminality, verification completeness, and product confirmation. This iteration neither commits automatically in response to VCS diagnostics nor silently makes checkpoint failure a new product-confirmation gate. Preserve existing write authority and confirmation rules.

### 3.3 Blocker Workflow

Proposed interfaces: `task block`, `task resume`, and `task blockers`. T01 defines their arguments and output fields in the CLI contract.

Blocker facts belong to Task records, reusing finding/resolution, active/resolved, and index/summary/context/outcome. Define stable blocker-kind, blocker-owner, and blocker-review-at index keys; use a compact UTC time format expressible by the existing TokenValueType. Store resolution conditions in context and the next action after recovery in outcome. Historical records with missing fields must show unknown/unspecified rather than fabricated owners or times.

Define identifiers, registration, updates, individual resolution, and history retention for multiple blockers on one task. Recovery requires an explicit caller action and resolution evidence, checking dependencies, requirement authorization, active findings, and iteration freezes. Reaching a recheck time only creates an item to inspect; it does not change state automatically. Derive dependency blockers from the existing dependency graph without persisting a duplicate set of facts.

Preserve existing legal block/resume transitions: unmet dependencies of pending tasks are derived waiting conditions, not a reason to silently extend the state machine for presentation convenience. When verification is blocked, recovery follows the existing in-progress flow and requires subsequent verification again.

### 3.4 Recovery Context

Proposed interface: `task context --task <ID>`. Default output includes the task objective, effective scope and constraints, source requirements, acceptance criteria, dependency results, latest verification/review, unresolved findings, next actions, and authority boundaries. Load full history only through explicit queries.

Provide item/byte limits, truncation markers, omitted counts, and follow-up query locators. Objectives, constraints, unresolved blockers, and prohibited actions are essential information. If essential information exceeds the limit, return an explicit diagnostic rather than silently omitting it while claiming complete context. Report document revisions and identify missing persisted facts. Recovery remains possible after temporary work reports are deleted, without reading chat history or depending on a new report directory.

## 4. Implementation Sequence and Dependencies

T01-T08 in this table refer to `20260908-task-pr-01` through `20260908-task-pr-08`. This table explains phases only; tasks.xml is authoritative for actual dependencies.

| Phase | Task | Prerequisites | Deliverable and exit condition |
|---|---|---|---|
| A: Define technical contracts | T01 Progression decisions and compatibility | None | Executable state/action matrix, blocker fields, output and exit-code policy, context limits, and negative scenarios, without changing owner-approved scope |
| B: Unify facts | T02 Shared progression model | T01 | Joint assessment of lifecycle, dependencies, reviews, and blockers, with determinism and boundary tests |
| C: Integrate existing views | T03 Consistent command outputs | T02 | next/summary/task summary/readiness agree, including completion-rate denominators |
| C: Integrate actual diagnostics | T04 VCS readiness dimension | T02 | Actual read-only results for Git states and inspection failures |
| D: Complete the blocker workflow | T05 Blocker commands and queues | T02, T03 | Registration, individual resolution, recovery, recheck queues, and historical compatibility pass |
| E: Enable resumable handoff | T06 Minimal recovery context | T03, T04, T05 | Bounded output with explainable sources, revisions, missing information, and truncation |
| F: Align guidance | T07 Contracts and guidance | T03, T04, T05, T06 | README, Skill, templates, help, and older proposal notes agree with implementation |
| G: Technical acceptance | T08 Scenario regression and handoff | T07 | Full build and scenario exercises pass, independent technical review is complete, and product acceptance is handed to the owner |

T03 and T04 can be implemented independently after their prerequisite completes. This describes dependencies and does not require multiple agents. T01 first runs `build.cmd` to establish the baseline; subsequent code changes follow repository requirements for verification before and after changes.

## 5. Verification Matrix

| Category | Required scenarios | Product acceptance references |
|---|---|---|
| Lifecycle and terminality | Empty task set, blocked only, blocked+done, all done, cancellation/transfer/supersession, draft, replanning, completed; views agree with the state machine | AC01, AC02 |
| Dependencies and reviews | Same-document/cross-iteration dependencies, missing references, cycles, active findings, review awaiting submission, changes-requested, independent approval; recommended actions are legal | AC02, AC03 |
| Git diagnostics | Clean, untracked, modified, staged, ignored, non-Git, Git unavailable/error; HEAD/index/working-file contents remain unchanged | AC04 |
| Blocker workflow | Multiple blockers, missing historical fields, due rechecks, resolved external conditions, unmet dependencies, unapproved requirements, replanning; failed resolution leaves no partial writes | AC05, AC06 |
| Recovery view | Deleted temporary reports, long history, essential constraints exceeding limits, nonessential-history truncation, revision drift while reading, missing dependencies, terminal tasks | AC07 |
| Compatibility and documentation | Historical v1 XML remains readable; existing valid requests remain usable; existing output-field semantics are preserved and new fields/error codes are documented; help advertises no unimplemented capabilities | AC08 |
| Complete exercise | Create draft, activate with owner authorization, execute, block, explicitly recover, verify, independently review, inspect checkpoint status, await owner acceptance | AC09 |

The full IDs for AC01-AC09 are `20260908-accept-pr-01` through `20260908-accept-pr-09`. Tests use isolated temporary workspaces and test authorization inputs. They must not modify actual historical iterations or confirm this iteration on the owner's behalf.

## 6. Risks and Controls

- **Presentation changes affect callers:** T01 distinguishes corrections to erroneous behavior from additional fields. Preserve existing success paths and valid XML requests, and add CLI contract tests.
- **Queries expand into an execution scheduler:** Keep views read-only and recommendations separate from execution commands; do not automatically claim, recover, accept, or commit.
- **Generic v1 records cannot express blockers adequately:** First validate stable indexes and record fields. If schema changes are required, follow the requirement/design change process and provide an upgrade plan.
- **Incomplete history creates false conclusions:** Report unknown explicitly and provide a way to supply missing facts or query further.
- **Assessment logic diverges again across entry points:** Share the Core model and check fact consistency across views using the same scenarios.
- **Context grows too large or presents an inaccurate snapshot:** Bound output by default, retain critical constraints, report source revisions, and handle read drift within a bound.

## 7. Execution Records and Completion Boundaries

During execution, persist implementation results, source commits, verification commands and exit codes, findings, risks, reviews, and next actions in the corresponding Task records. Large raw logs may be stored in repository-approved locations, but semantic results must not exist only in external reports.

Each implementation task proceeds to independent review after technical verification. Implementer validation does not replace review, and this plan does not presume a passing review. Once technical tasks are complete, run workspace validation and iteration readiness. The owner decides product requirements, design, acceptance, and iteration completion.

At handoff boundaries, inspect `git status --short -- .dogdouspec`. Without Git-write authorization, list authoritative files that have not been checkpointed and report that they are locally durable but not yet captured in a transport-ready Git checkpoint. Do not commit or push automatically.
