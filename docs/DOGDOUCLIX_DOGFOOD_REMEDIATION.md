# DogdouClix Dogfood Remediation Matrix

This document records the upstream disposition of every finding in
`L:\dogdouclix\docs\dogdouspec_issues.md` as of 2026-08-25. A disposition may
implement the suggested behavior, retain a stricter contract deliberately, or
use an existing DogdouSpec entity instead of adding a duplicate model.

| Finding | Disposition | Executable or documented evidence |
| --- | --- | --- |
| ISSUE-001 | Implemented. `.agents/skills/dogdouspec` is the default checked-in and installed Skill path. | `docs/INSTALL_IN_OTHER_REPOSITORY.md`, `AGENTS.md`, and `SkillDeploymentTests`; upgrade preserves modified legacy `skills/dogdouspec` content instead of silently deleting it. |
| ISSUE-002 | Implemented. Time-sensitive creation and mutation paths accept an injected clock, and date-drift tests derive timestamps from controlled time. | `IClock`/`SystemClock` use in Core services and the iteration, change, requirement, and Task regression suites. |
| ISSUE-003 | Retained strict coverage, repaired ergonomics. Completion still requires terminal criterion results and verification/completion records that explicitly cover them; the shipped request template is now valid and the dual obligation is documented. | `templates/v1/task.update.xml`, Task completion semantic tests, `docs/V1_CLI_CONTRACT.md`, and the Skill workflow. DogdouSpec does not invent evidence links automatically. |
| ISSUE-004 | Retained domain-specific vocabulary with clearer diagnostics. Requirement decisions use `approved`; product acceptance uses `accepted`; invalid diagnostics state the action-specific allowed values. | `IterationConfirmer`, confirmation templates, and iteration confirmation Core/CLI tests. The tokens are not aliased because approval and acceptance are distinct product decisions. |
| ISSUE-005 | Retained strict optimistic concurrency. Each changed document advances exactly one revision, and responses return the exact revision or revisions to use next; receipts are part of the same atomic write. | Mutation envelopes, idempotency tests, `docs/V1_CLI_CONTRACT.md`, and `.agents/skills/dogdouspec/references/mutations.md`. Callers re-query after each write and never guess revisions. |
| ISSUE-006 | Implemented. Task and iteration lifecycle helpers synchronize `index/term[@key='status']` with authoritative `@status`, and semantic validation rejects stale status terms. | `StatusTermHelper`, transition/change paths, and status synchronization tests. |
| ISSUE-007 | Implemented as governed guidance and public helpers, without forcing every exclusion into backlog or every completion into knowledge. | Skill decision table for backlog, knowledge, and `design_snapshot`; `backlog add|list|schedule|complete|cancel`; backlog Core/CLI tests. An exclusion is a boundary unless a credible future obligation exists. |
| ISSUE-008 | Implemented. Project-scoped cross-iteration Task dependencies are resolved, checked before start/resume, and considered by actionable Task selection. | `TaskDependencyGate`, `task next`, `TaskDependencyAndScopeCoreTests`, and the public dependency contract. |
| ISSUE-009 | Implemented as an explicit read-only repository check rather than pretending XML validation observes Git state. | `task scope --path|--git-ref|--git-range`, `TaskScopeVerifier`, Core/CLI scope tests, and documented glob/exclude semantics. |
| ISSUE-010 | Retained as conditional context, not mandatory boilerplate. | The Skill decision table and `docs/V1_SKILL_WORKFLOW.md` require `design_snapshot` when material technical context is needed to resume or verify safely, and recommend omission when it would only repeat routine status. |
| ISSUE-011 | Implemented. Time-first IDs support both date and UTC timestamp prefixes; iteration list exposes `created_at` and orders chronologically with ID as the deterministic tie-breaker. | iteration creation/listing Core/CLI tests and `docs/V1_CLI_CONTRACT.md`. |
| ISSUE-012 | Implemented and exercised. Replan freezes execution, change application resolves findings and creates/disposes Tasks, and owner continuation reopens execution. | `TaskChangeWorkflowTests`, `TaskChangeWorkflowCliTests`, the Skill decision tree, and authority/mutation references. |
| ISSUE-013 | Implemented as a decision heuristic rather than mandatory ADR production. | Skill guidance distinguishes Task rationale, `design_snapshot`, knowledge, and material design decisions; accepted design changes remain owner-confirmed through `iteration confirm`. |
| ISSUE-014 | Implemented through the existing backlog model; a second `issues.xml` authority was intentionally rejected. | `backlog kind="defect"`, severity/source/resolution fields, public lifecycle commands, query support, and backlog tests. Resolving-Task links are evidence and do not rewrite Task origin. |
| ISSUE-015 | Implemented as an optional fail-closed structured review gate. | `task review`, `<review required="true">`, completion/readiness gates, active findings on `changes-requested`, immutable implementer attribution for gated Tasks, and Task review Core/CLI tests. Legacy Tasks remain compatible. |

## Evidence and version-control boundary

`dogdouspec validate` proves that managed XML is schema-valid, internally
consistent, and compliant with DogdouSpec's local semantic rules. Mutation
receipts and actor fields preserve structured provenance. They do **not**
authenticate an actor, cryptographically verify a reviewer, or independently
prove that an external command, test, build, or review actually ran. Repositories
that need those guarantees must bind DogdouSpec to authenticated Git, CI,
signing, or identity-provider evidence.

The authoritative `.dogdouspec/` state should be versioned with the repository
so lifecycle decisions, Task records, backlog state, and schema ownership are
reviewable with the code. Runtime `.dogdouspec/_tmp/` staging and recovery files
must remain ignored. Managed XML must still be mutated only through the
repository-local CLI; version control is the durable review surface, not an
alternate write path.
