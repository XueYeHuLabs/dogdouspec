# DogdouSpec Next-Stage Agent Usability Requirements

- **Status:** Partially implemented. USAB-002 (Compact Recovery Context) and USAB-005 (Limit Overflow Diagnostics) implemented in iteration `20261014-agent-usability` (DogdouSpec 1.0.4). Remaining items (USAB-001, USAB-003, USAB-004) remain proposed.
- **Date:** 2026-10-08.
- **Baseline:** The locally installed WinGet CLI reports version 1.0.3. Its embedded guidance matches the installed repository Skill in the inspected consumer workspace.
- **Purpose:** Reduce coordination overhead and make remaining delivery work immediately visible during long engineering iterations.
- **Scope:** Four primary improvements, with supporting diagnostic and guidance requirements.

## 1. Context and Responsibility

These requirements come from practical use of DogdouSpec during the `20260930-vnic` iteration, followed by a read-only inspection of the current CLI, embedded guidance, and implementation.

The historical session accumulated 559 task records containing 382,978 summary characters. At the user-requested stop, all 17 tasks remained `in-progress`, which made implementation progress, review results, outstanding tests, and actual blockers difficult to distinguish. These figures describe the historical session window, not the current iteration state, execution time, or token cost.

Much of that overhead resulted from agent misuse: repeated long summaries, unnecessary wrapper scripts, and excessive frozen-catalog and hash-verification activity. DogdouSpec should make the useful path easier, but agent-generated overhead must not be presented as a missing product capability.

The optimization objective is concrete: a coordinator should quickly identify unfinished functionality, perform authorized implementation and review work, record the material result, and stop at the correct owner boundary.

## 2. Existing Capabilities: Do Not Reimplement

The following capabilities were verified through current help, embedded guidance, or successful read-only CLI calls. They are the starting point for this work.

| Capability | Current support | Next-stage treatment |
| --- | --- | --- |
| Iteration progress card | `summary` provides task breakdown, progress, blockers, pending product gates, and a recommended action; Markdown, JSON, XML, and human formats are available. | Extend delivery visibility; do not add another summary command. |
| Bounded task recovery | `task context` separates `essential` and `supplemental`, accepts `--max-bytes`, and reports omitted history with a query locator. | Improve selection and remove duplicate serialization. |
| Structured records without request XML | `task record` escapes prose and supports record kinds, coverage, resolution, and automatic revision resolution. | Use and improve the native command; do not create another record wrapper. |
| Additive task changes | `task revise` provides `--add-constraint`, `--add-criterion`, `--add-scope`, and `--add-dependency`, with automatic revision resolution. | Preserve these commands and reconcile guidance. |
| Scope explanations | `task scope explain` and working-tree scope checks already exist. | No new scope subsystem is requested. |
| Progression and blockers | `task next`, `task blockers`, `task block --record-only`, and `task resume` already exist. | Reuse their assessments and authority boundaries. |
| Template discovery | Missing-template diagnostics list available templates; `template list` and `template show` exist. | Improve remaining diagnostics without reopening template discovery as missing. |
| Lightweight work and transient output guidance | The Skill defines Mode A with zero governance overhead, recommends high-level commands, prohibits a durable external agent-report ledger, and treats raw output as transient by default. | Apply these rules and make the normal governed path shorter. |

Older field reports may describe the absence of capabilities that now exist. Their historical observations remain useful, but those absence claims must not become new implementation tasks.

## 3. Requirements Overview

| ID | Priority | Requirement | Existing foundation |
| --- | --- | --- | --- |
| USAB-001 | P0 | Show implementation, review, and validation progress separately. | Existing summary and progression views. |
| USAB-002 | P0 | Produce a compact recovery context without repeated records. | Implemented in iteration `20261014-agent-usability` (DogdouSpec 1.0.4). |
| USAB-003 | P0 | Return a useful committed-operation result and simplify caller guidance. | Existing mutation engine, receipts, and revision-aware commands. |
| USAB-004 | P1 | Expose effective owner directives and explicit supersession. | Existing constraints, records, and authority boundaries. |
| USAB-005 | P1, supporting | Make remaining diagnostics actionable. | Context limit overflow breakdown diagnostics implemented in iteration `20261014-agent-usability` (DogdouSpec 1.0.4). |

USAB-005 supports the four primary improvements. It is not a separate platform or workflow.

## 4. USAB-001: Delivery Stage Visibility

### Problem

A lifecycle status such as `in-progress` does not explain whether implementation is complete, review has requested changes, or testing is waiting for an explicit owner instruction. The current summary represents task status and progression, but it does not expose these delivery dimensions per task.

### Required Behavior

1. Extend the existing summary with compact implementation, review, and validation assessments for each relevant task.
2. Associate each assessment with its supporting record or criterion reference and a short explanation.
3. Distinguish missing information, pending work, successful results, failures, requested changes, and an explicit wait for owner authorization.
4. Show the remaining action and who must perform or authorize it.
5. Preserve the difference between task completion and product acceptance. Completed tasks must not imply an accepted or closed iteration.
6. Reuse governed task facts. Do not introduce an independently maintained delivery state machine or external status database.
7. Do not infer implementation completion from a build result, review approval from a dispatched review, or test success from entering `verification`.

If existing records cannot express a required fact reliably, define the smallest necessary structured representation through the normal public-contract process. Do not guess phase results from arbitrary prose.

### Acceptance Criteria

- Given implementation completion, an actual approved review, and a directive requiring explicit permission before VM tests, the summary shows implementation complete, review approved, validation awaiting owner authorization, and waiting as the next action.
- Given a review request for changes, the summary shows the unresolved finding and correction as the next action, regardless of an earlier successful build.
- Given absent review or validation facts, the corresponding assessment is unknown or pending rather than successful.
- Given all tasks done and a pending product criterion, the existing pending acceptance gate remains visible.
- Human and machine-readable output convey the same assessments and references.

## 5. USAB-002: Compact Recovery Context

### Problem

Bounded recovery already exists, but it can repeat the same verification, review, and completion records in both essential and supplemental sections. In the inspected implementation, supplemental candidates are taken from all task records without excluding the records already selected for essential context. Filling the remaining byte budget with history can also produce a much larger response than the next action needs.

### Required Behavior

1. Serialize each record ID at most once within a recovery response. Essential records take precedence over supplemental history.
2. Keep current objectives, effective scope, constraints, criteria, active blockers, and authority boundaries intact.
3. Provide a compact recovery presentation focused on current delivery facts and the next authorized action. Make additional history an explicit, documented option.
4. Preserve references and query locators for details omitted from the compact presentation.
5. Define record-count semantics clearly: essential records, supplemental candidates, included history, and omitted history must not be conflated.
6. Keep record selection deterministic for unchanged source revisions and options.
7. When essential context exceeds the byte limit, identify the largest contributing sections and provide a supported way to inspect them. Never silently remove constraints or acceptance criteria to fit.
8. Encourage concise material-result records. Any repeated-content or excessive-length guidance must be advisory and must not automatically merge, delete, or rewrite history.

The existing record archival specification is a separate concern. Improving a recovery view must not require a storage migration, a new report ledger, or manual archival work by the caller.

### Acceptance Criteria

- A record selected as the latest verification result is absent from supplemental history in the same response.
- A task with extensive historical discussion can be resumed from its compact current facts and locators without loading the full history.
- Every successful response respects its documented byte limit and reports consistent inclusion and omission counts.
- A deliberately undersized limit produces an actionable error with section sizes; it does not produce a partial successful response that drops mandatory facts.
- Changing presentation options leaves governed documents, record IDs, and revisions unchanged.

## 6. USAB-003: Useful Operation Results and Consistent Guidance

### Problem

Native commands already resolve revisions automatically. Nevertheless, the current Skill says to always pass exact revisions and to run a separate workspace validation and re-query after every mutation. The historical coordinator implemented a four-step wrapper: query revision, append record, validate, and query the record again.

The missing improvement is a complete normal-operation contract and matching guidance, not another automatic-revision feature.

### Required Behavior

1. Return a compact result from a successful high-level mutation containing the operation identity, affected task, committed document revisions, resulting task status, relevant changed facts, and any applicable next action or warning.
2. Derive the result from the committed state associated with that operation. Do not attach a later unrelated revision as if it were the operation's result.
3. State which validation checks the operation completed. Distinguish target or transaction validation from a separate whole-workspace validation; do not imply unrelated documents were checked when they were not.
4. Preserve explicit expected-revision behavior, conflict handling, idempotency, schema checks, lifecycle protection, and owner gates.
5. Never hide a revision conflict by retrying a stale planned change against a newer revision. The caller must reassess affected assumptions when required.
6. A reporting failure after a committed write must not be described as an uncommitted operation. Report commit success and any unavailable supplementary result distinctly.
7. Update the Skill, generated guidance, workflow documentation, and help together. Explain when native revision resolution is appropriate and when a planned multi-document operation needs explicit expected revisions.
8. Once the result contract is sufficient, document a normal single-command record workflow. Reserve separate whole-workspace validation and re-query for material boundaries, recovery, migration, or a specific unresolved concern.

Guidance should emphasize recording implementation results, actual review outcomes, unresolved findings, and handoff conditions. It should not require a record for each prompt, poll, unchanged status, or transport event.

### Acceptance Criteria

- One successful native record command provides enough information to confirm the appended record and resulting task status without a preliminary revision query or a confirmation query solely for that record.
- An explicitly stale revision fails without mutating state or silently substituting a newer revision.
- Replaying an operation preserves the existing idempotency contract and does not create another semantic result.
- Mutation results accurately describe validation coverage and do not claim whole-workspace health without such validation.
- Updated examples use actual implemented commands and no longer simultaneously require explicit revisions for every command while presenting automatic resolution as the normal path.

## 7. USAB-004: Effective Owner Directives

### Problem

The owner initially allowed VM testing without further authorization, then instructed the coordinator to complete implementation and review and wait for a later request before starting VM tests. Old handoff material retained the earlier authorization. Constraints and record statuses exist, but current public guidance does not provide a complete view of directive supersession and effective permissions.

### Required Behavior

1. Provide a compact effective-directive view in recovery and relevant summary output.
2. For each directive, expose its scope, source reference, current effect, and any explicitly superseded directive.
3. Store or reference iteration-wide directives once rather than copying the same text into every task.
4. Require an explicit supersession relation or an equally deterministic public representation. A later timestamp alone must not imply greater owner authority.
5. Accept directive facts recorded by an authorized caller from explicit owner instructions. Do not automatically interpret chat history or authenticate an owner from an actor label.
6. Report conflicting or ambiguous directives as unresolved; do not silently select the more permissive action.
7. Treat implementation authorization and test authorization separately. A waiting-for-tests directive must allow already-authorized implementation work while showing tests as waiting.
8. Represent explicit local governance preferences, such as excluding `.dogdouspec` from commits, without causing automatic Git writes or silently weakening existing acceptance gates. Report any resulting persistence or readiness condition accurately.

Start with existing governed records and references where they are sufficient. Any new persistent directive structure requires an explicit schema and public-contract change.

### Acceptance Criteria

- Explicitly superseding automatic VM-test authorization with a wait instruction causes recovery output to show waiting for owner authorization as the effective test condition.
- Earlier authorization remains available as history and is clearly marked as superseded.
- An ordinary progress record cannot grant test authorization or override an owner directive.
- Conflicting directives produce a visible unresolved condition and an actionable clarification requirement.
- A task-scoped directive does not silently modify permissions for unrelated tasks or iterations.
- Producing the view never launches tests, invokes providers, or modifies Git state.

## 8. USAB-005: Supporting Diagnostic Improvements

Existing template discovery and byte-limit diagnostics should be retained. Remaining improvements must be specific to the failing operation.

1. Unsupported options should identify the relevant command, its supported forms, and a usable help or template locator. Do not imply that all commands support the same output formats.
2. XML structure errors should provide a valid expected fragment or the exact applicable template, where the schema can determine it reliably.
3. Attempted replacement of a started task's rationale should explain the immutable boundary and the supported discussion or additive-revision action.
4. Scope conflicts should point to the existing explanation command and the offending path or rule.
5. Context-limit errors must meet USAB-002's size-breakdown requirement.
6. Documented diagnostic codes and exit-code mappings must agree with the observed CLI behavior. Tests must assert the intended contract rather than accepting any nonzero result.

Acceptance requires representative invalid-option, invalid-structure, immutable-field, scope-conflict, and context-overflow cases to provide a direct corrective action without requiring source-code inspection.

## 9. Scope Boundaries

- Keep semantic execution facts in the existing governed iteration model.
- Preserve actual independent review requirements. A declared reviewer or provider name is provenance, not proof that a review ran.
- Do not add mandatory hash verification, frozen catalogs, external agent ledgers, or evidence-qualification workflows to normal task delivery.
- Preserve the engine's existing concurrency and idempotency safeguards; reducing caller overhead does not require removing them.
- Do not add provider orchestration, VM scheduling, automatic tests, automatic Git operations, or multi-workspace aggregation to this optimization stage.
- Do not change test standards, skip real checks, or infer product acceptance to improve displayed completion percentages.
- This document requests product optimization work; it does not activate a consumer iteration, authorize its tests, or claim any proposed feature is already implemented.

## 10. Suggested Delivery Order

1. **First delivery:** USAB-002 record deduplication and actionable overflow diagnostics, with corresponding contracts and semantic tests.
2. **Second delivery:** USAB-001 compact delivery assessments, reusing current summary and progression machinery.
3. **Third delivery:** USAB-003 operation results and synchronized caller guidance; remove redundant workflow examples after the result contract is implemented.
4. **Fourth delivery:** USAB-004 effective directives, starting with the test-authorization supersession scenario.
5. Apply USAB-005 diagnostics within each affected delivery rather than building a separate diagnostic workflow.

Each delivery must synchronize implementation, CLI help, relevant contracts, generated guidance, and tests. Validation should demonstrate the stated input, result, and remaining authority boundary. Additional audit artifacts are not a delivery requirement.

## 11. References and Evidence

- [Current CLI contract](V1_CLI_CONTRACT.md).
- [Current Skill workflow](V1_SKILL_WORKFLOW.md).
- [Progression and recovery contract](iterations/20260908-progression-recovery/CONTRACT.md).
- [Earlier usability proposal and implementation-status note](DOGFOOD_USABILITY_AND_EVIDENCE_PROPOSAL.md).
- [Historical CLI issue report](dogdouspec-issues.md).
- [Separate record archival design](TASKS_RECORD_ARCHIVAL_SPEC.md).
- [Current iteration summary model](../src/DogdouSpec.Core/Reporting/IterationSummary.cs).
- [Current recovery-context record selection](../src/DogdouSpec.Core/Tasks/TaskContextQuery.cs).

Read-only consumer checks performed on 2026-10-08 included `--version`, `skill guide --all`, `skill status`, `summary` in Markdown and JSON, `task context` with successful and insufficient byte budgets, native-command help, and missing-template diagnostics. A successful context response repeated its latest verification, review, and completion records in supplemental history. The source implementation independently confirms that supplemental candidates currently include all task records.

No consumer iteration state was changed and no VM tests were started during this requirements review. The inspection did not run a new write-path test; proposed operation-result improvements are therefore based on the current public interface and guidance, not a claimed mutation failure.
