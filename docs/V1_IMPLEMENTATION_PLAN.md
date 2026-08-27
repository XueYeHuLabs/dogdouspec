# DogdouSpec v1 Implementation Plan

Status: Legacy plan requiring iteration-first rewrite

Do not execute this plan as written. Its project catalog, independent Evidence,
claim/lease, event, policy-digest, and broad workflow tasks predate the
normative iteration-first contracts in `V1_XML_SCHEMA_CONTRACT.md`,
`V1_CLI_CONTRACT.md`, and `V1_SKILL_WORKFLOW.md`. A reduced replacement plan is
defined in `V1_IMPLEMENTATION_PLAN_ITERATION_FIRST.md`.

Depends on: `docs/V1_DESIGN.md`

Execution mode: Traditional repository development until the self-hosting gate

## 1. Objective

Build the first usable DogdouSpec CLI, verify it through fixtures and end-to-end tests, then migrate the DogdouSpec repository into a DogdouSpec-managed workspace. Subsequent releases will be planned and executed through the new workflow.

This document defines scope, dependencies, deliverables, and acceptance. It is not a mutable execution ledger. Before self-hosting, progress is represented by reviewed repository changes and verification evidence. After self-hosting, remaining work status moves to `.dogdouspec` XML state.

## 2. Delivery definition

Version 1 is usable when a clean local project can:

1. Initialize a valid `.dogdouspec` workspace without global installation.
2. Register multiple repositories and create Iteration and Research Work Items.
3. Query documents with XPath and validate schemas, links, and semantic rules.
4. Manage task lifecycle, dependencies, leases, evidence, and completion gates.
5. Record a Finding, dispose its Issue, and handle task expansion, Research, Change, successor Iteration, and backlog deferral.
6. Generate a scoped implementation context and validated `AGENTS.md` policy digest.
7. Detect revision conflicts and recover interrupted multi-file transactions.
8. Produce deterministic JSON suitable for multiple coding-agent environments.
9. Manage the next DogdouSpec iteration using its own workflow.

## 3. Execution rules

- Documentation, schemas, code documentation, and commit messages are written in English.
- Each task is implemented in dependency order and verified against its acceptance criteria.
- New requirements discovered during implementation follow the Change and Surprise Protocol in `V1_DESIGN.md`.
- Product implementation must not begin by silently refining this plan beyond its approved design boundary.
- No MCP server, graphical interface, arbitrary command runner, or autonomous agent scheduler is added to v1.
- The self-hosting migration begins only after the complete bootstrap gate passes.
- Existing unrelated user work is preserved and excluded from task commits.

## 4. Milestone map

```text
B0 Design baseline
  -> B1 Domain contracts and schemas
  -> B2 Secure workspace and transaction core
  -> B3 Read and validation CLI
  -> B4 Work execution CLI
  -> B5 Discovery, change, and backlog CLI
  -> B6 Context, policy digest, packaging, and end-to-end verification
  -> B7 DogdouSpec self-hosting migration
  -> B8 First managed iteration
```

## 5. B0 - Design baseline

### B0-T1 Review and freeze the v1 product boundary

Deliverables:

- Approved `docs/V1_DESIGN.md`.
- Approved `docs/V1_IMPLEMENTATION_PLAN.md`.
- Recorded decisions for CLI-first delivery, no MCP in v1, and the control-plane boundary.

Acceptance:

- The document ownership model has no duplicate authoritative state.
- Surprise, replanning, successor, deferral, and completion rules are explicit.
- The traditional bootstrap and self-hosting boundary is explicit.
- Deferred decisions are named rather than silently assumed.

Dependencies: None.

### B0-T2 Establish repository development conventions

Deliverables:

- Root `AGENTS.md` containing repository bootstrap, documentation language, editing, build, test, and change-control rules.
- `.editorconfig`, `.gitignore`, root `README.md`, and a single build entry point.

Acceptance:

- A new Agent can discover the build and verification entry points from the repository root.
- Generated Dogdou policy content is not introduced before the policy engine exists.
- Repository rules do not claim that DogdouSpec is already self-hosted.
- The task does not prematurely choose the implementation technology or source layout.

Dependencies: B0-T1.

## 6. B1 - Domain contracts and schemas

### B1-T1 Freeze the domain glossary and identity rules

Deliverables:

- Normative definitions for Project, Repository, Work Item, Iteration, Research, Requirement, Design Element, Task, Finding, Issue, Change, Evidence, Policy, Knowledge, and Backlog Item.
- Stable ID grammar and case rules.
- Relative document reference and fragment grammar.
- Revision and timestamp conventions.

Acceptance:

- Every persisted object has one owner and globally unambiguous identity within a project.
- References can be resolved without loading arbitrary external files.
- Identity never depends on a mutable title or filesystem slug.

Dependencies: B0-T1.

### B1-T2 Define lifecycle and transition contracts

Deliverables:

- Work-item, task, Issue, Change, Evidence, Policy, Knowledge, and Backlog lifecycle tables.
- Legal transition matrix with required fields, authority, and side effects.
- Terminal disposition rules.
- Iteration and Research completion predicates.

Acceptance:

- `Done`, `Transferred`, `Superseded`, `Cancelled`, `DeferredToBacklog`, and `AcceptedRisk` cannot be confused.
- A task required by current acceptance cannot be deferred without an approved Change.
- Every transition can be evaluated deterministically by the core.

Dependencies: B1-T1.

### B1-T3 Design versioned XML schemas

Deliverables:

- XSDs for project, knowledge, policies, backlog, specification, goal, findings, changes, evidence, and events.
- `schema-lock.xml` contract.
- Minimal valid fixtures for every document type.
- Invalid fixtures for required structural failures.

Acceptance:

- All valid fixtures pass XSD validation.
- All invalid fixtures fail for the intended reason.
- The schemas do not duplicate authoritative state across documents.
- V1 managed schemas have no target namespace and require an explicit root `schema_version` attribute.
- Schema versioning permits an explicit future migration path.

Dependencies: B1-T1, B1-T2.

### B1-T4 Define semantic validation rules

Deliverables:

- Cross-document uniqueness and reference rules.
- Completion, evidence revision, successor, backlog, accepted-risk, and policy-digest checks.
- Validation diagnostic code catalog with severity and remediation guidance.

Acceptance:

- Every invariant from `V1_DESIGN.md` maps to a validation rule or an explicitly documented non-enforceable owner decision.
- Diagnostics include document reference, object ID, rule code, and actionable explanation.

Dependencies: B1-T2, B1-T3.

## 7. B2 - Secure workspace and transaction core

### B2-T1 Scaffold the confirmed implementation baseline

Deliverables:

- `global.json` pinning .NET SDK `10.0.*` (`10.0.100` base with `latestFeature` roll-forward).
- `DogdouSpec.slnx`, Core and CLI projects, and three test projects defined in Section 16.1 of `V1_DESIGN.md`.
- Central build, analyzer, formatting, and package-version properties.
- Pinned `System.CommandLine` 2.0.11 and MSTest 4.3.3 dependencies with committed NuGet lock files.
- `agent.build.cmd` Debug and Release entry points.
- Minimal Native AOT-compatible command that reports the v1 version contract.

Acceptance:

- Debug locked restore, build, and test pass with warnings as errors.
- Release publishes and executes a self-contained `win-x64` Native AOT `dogdouspec.exe`.
- AOT, trimming, and single-file analyzers report no warnings.
- Project references preserve the Core-to-CLI dependency direction; Core has no CLI dependency.
- No unapproved runtime dependency or alternate technology is introduced.

Dependencies: B0-T2.

### B2-T2 Implement workspace discovery and document catalog

Deliverables:

- Ancestor-based `.dogdouspec/project.xml` discovery.
- Explicit `--project-root` support.
- Typed document references and catalog resolution.
- Workspace containment and cataloged read-only repository-path checks.

Acceptance:

- Discovery is deterministic from nested repository paths.
- Traversal outside the workspace is rejected unless permitted as a cataloged read-only repository path.
- Symlink, junction, case, and normalized-path behavior has Windows tests.

Dependencies: B2-T1.

### B2-T3 Implement secure XML load, XPath query, and deterministic output

Deliverables:

- Secure XML reader with DTD and external entity processing disabled.
- V1 document size, nesting, XPath length, and result-count limits from Section 16.3 of `V1_DESIGN.md`.
- XPath 1.0 evaluation for node-set, Boolean, number, and string results.
- Single-document query and deterministic per-document project search APIs.
- Typed node results with document reference, node type, generated absolute XPath, value, and authoritative XML fragment where applicable.
- Deterministic JSON result envelope including document reference and revision.

Acceptance:

- Malformed, oversized, external-entity, and invalid XPath inputs fail closed with stable diagnostics.
- XPath 2.0/3.x, XQuery, XSLT, variables, extension functions, and cross-document XPath are rejected or unavailable.
- Query results preserve enough XML identity to construct subsequent domain commands.
- Node-set query results use document order; search groups use canonical document-reference order.
- XPath `NaN` and infinity results use the documented non-finite JSON representation.
- Result truncation is explicit in human and JSON output.

Dependencies: B2-T2.

### B2-T4 Implement schema and semantic validation engine

Deliverables:

- XSD validation by document type and schema lock.
- Cross-document index and semantic rule evaluator.
- Whole-project and scoped validation APIs.

Acceptance:

- All B1 fixtures produce the expected diagnostic set.
- Broken links, duplicate IDs, illegal terminal dispositions, stale evidence, and invalid completion claims are detected.
- Validation performs no writes.

Dependencies: B1-T4, B2-T3.

### B2-T5 Implement revisioned atomic transactions and recovery

Deliverables:

- Expected-revision checks.
- Shared-reader/exclusive-writer project-level interprocess lock.
- Staged per-file replacement.
- Prepared/Committed multi-file transaction journal with hashes, retained originals, and startup recovery.
- Event generation for committed mutations.

Acceptance:

- Stale writers fail without changing files.
- A simulated crash at each commit phase recovers to either the complete old state or complete new state.
- Readers never observe a partial multi-document transaction.
- Post-`Prepared` recovery rolls forward idempotently when staged hashes are valid and restores originals otherwise.
- Partial multi-file success is impossible.
- Validation runs before commit and committed files remain valid after recovery.

Dependencies: B2-T4.

## 8. B3 - Read and validation CLI

### B3-T1 Implement CLI command and result conventions

Deliverables:

- Root command, help, version, exit-code, diagnostic, JSON, and human-output conventions.
- Lowercase case-sensitive commands, global options, stdout/stderr separation, and exit codes defined in Section 16.5 of `V1_DESIGN.md`.
- Source-generated JSON envelope defined in Section 16.6 of `V1_DESIGN.md`.
- Command invocation test harness.

Acceptance:

- Machines can rely on stable JSON envelopes and exit codes.
- Human output remains concise and points to the same diagnostic codes.
- JSON mode writes only the versioned JSON envelope to stdout.
- Missing required input fails without an interactive prompt.
- Response files, parser directives, automatic abbreviations, and undeclared aliases are rejected.
- Approval-gated commands cannot be bypassed with a confirmation flag.
- No command writes unless its help and metadata identify it as mutating.

Dependencies: B2-T1.

### B3-T2 Implement initialization and project inspection

Commands:

- `dogdouspec init`
- `dogdouspec repo add`
- `dogdouspec repo list`
- `dogdouspec repo remove`
- `dogdouspec work list`
- `dogdouspec work show`
- `dogdouspec policy list`
- `dogdouspec backlog list`

Acceptance:

- Initialization creates a complete minimal workspace in one transaction.
- Re-running initialization is idempotent or fails with an explicit non-destructive diagnostic.
- Repository registration enforces stable identity, normalized paths, containment rules, and safe removal references.
- Inspection commands do not require direct file knowledge.

Dependencies: B2-T5, B3-T1.

### B3-T3 Implement query and validation commands

Commands:

- `dogdouspec query`
- `dogdouspec search`
- `dogdouspec validate`

Acceptance:

- Query supports typed document references and XPath 1.0 result types.
- Search evaluates independently per selected cataloged document and never provides cross-document XPath semantics.
- Validation supports whole-project, Work Item, and individual-document scope.
- Invalid state returns a non-zero exit code and complete structured diagnostics.

Dependencies: B2-T4, B3-T1.

## 9. B4 - Work execution CLI

### B4-T1 Implement Work Item creation and transition

Commands:

- `dogdouspec work create`
- `dogdouspec work transition`
- `dogdouspec spec show`
- `dogdouspec spec import`

Acceptance:

- Iteration and Research creation produce all required documents and catalog links atomically.
- A complete candidate specification can be imported while the Work Item is `Draft`.
- Leaving `Draft` freezes direct specification import; later material revisions require an approved Change.
- Illegal lifecycle transitions fail without mutation.
- Technical completion checks report readiness without changing product
  acceptance or Iteration state. Product acceptance and Iteration completion
  require a separate explicit owner confirmation command.

Dependencies: B3-T2, B2-T5.

### B4-T2 Implement task graph management

Commands:

- `dogdouspec task add`
- `dogdouspec task split`
- `dogdouspec task claim`
- `dogdouspec task release`
- `dogdouspec task transition`

Acceptance:

- Dependency cycles, invalid references, duplicate claims, expired revisions, and invalid terminal dispositions are rejected.
- Task splitting preserves requirement and acceptance traceability.
- A lease does not grant authority outside the task scope.

Dependencies: B4-T1.

### B4-T3 Implement evidence recording and coverage checks

Commands:

- `dogdouspec evidence record`
- `dogdouspec evidence list`

Acceptance:

- Evidence records producer, time, environment, artifact provenance, specification revision, and coverage references.
- Evidence for an obsolete specification revision is reported as stale unless explicitly revalidated.
- Recording evidence does not automatically mark a task or acceptance case complete.

Dependencies: B4-T2.

## 10. B5 - Discovery, change, and backlog CLI

### B5-T1 Implement Finding and Issue creation

Commands:

- `dogdouspec finding record`
- `dogdouspec issue open`
- `dogdouspec issue disposition`

Acceptance:

- A Finding records provenance without silently changing scope.
- Every Issue accepts exactly one valid disposition.
- Dispositions requiring another object create or link that object atomically.

Dependencies: B4-T1.

### B5-T2 Implement planning elaboration and Research extraction

Deliverables:

- Guarded `ExpandedToTask` operation.
- Child Research creation and originating-task blocker links.
- Research completion output-to-disposition checks.

Acceptance:

- Local elaboration is rejected when it crosses a material-change boundary.
- Research cannot complete with unanswered questions lacking explicit disposition.
- Completing Research does not automatically approve its proposed design.

Dependencies: B5-T1, B4-T2.

### B5-T3 Implement Change and replanning workflow

Commands:

- `dogdouspec change propose`
- `dogdouspec change assess`
- `dogdouspec change approve`
- `dogdouspec change reject`

Acceptance:

- Proposing a material Change places the owning Work Item in `Replanning` and freezes affected tasks.
- Impact analysis covers requirements, tasks, repositories, policies, and evidence.
- Approval creates a new specification revision and explicit task/evidence dispositions.
- An Agent cannot satisfy an owner approval gate merely by naming itself as approver.

Dependencies: B5-T1, B4-T3.

### B5-T4 Implement successor and backlog transfer workflows

Commands:

- Successor creation through `dogdouspec work create --successor-of`.
- `dogdouspec backlog add`
- `dogdouspec backlog schedule`

Acceptance:

- Superseding an Iteration preserves the previous Work Item and maps every non-terminal task.
- Deferral fails when the Issue still blocks required current acceptance.
- Backlog entries require source, reason, risk, acceptance impact, priority, and target or review condition.

Dependencies: B5-T3.

### B5-T5 Implement Knowledge and Policy lifecycle workflows

Commands:

- `dogdouspec knowledge add`
- `dogdouspec knowledge promote`
- `dogdouspec knowledge retire`
- `dogdouspec policy propose`
- `dogdouspec policy approve`
- `dogdouspec policy retire`
- `dogdouspec policy check`

Acceptance:

- Unverified Findings cannot silently become verified Knowledge.
- Knowledge cannot become mandatory merely by being recorded or promoted.
- Policy approval enforces the configured owner authority and creates source links.
- Policy exception and retirement preserve affected work and historical provenance.
- Context and validation distinguish mandatory, default, and guideline policy levels.

Dependencies: B5-T1, B5-T3.

## 11. B6 - Context, policy digest, packaging, and verification

### B6-T1 Implement scoped context generation

Command:

- `dogdouspec context`

Acceptance:

- Context selection is based on repository, Work Item, task, intent, scope, and verified links.
- Output includes source document revisions and stop conditions.
- Unrelated knowledge and policies are excluded.
- Context generation performs no writes.

Dependencies: B3-T3, B4-T2, B5-T4, B5-T5.

### B6-T2 Implement `AGENTS.md` policy digest synchronization

Deliverables:

- Marker-delimited generated digest.
- Source policy revision and content hash.
- Sync and drift-validation operations.

Acceptance:

- Only the generated block is modified.
- Human-authored `AGENTS.md` content is byte-preserved outside the block.
- Drift or conflicting mandatory instructions is a validation failure.

Dependencies: B6-T1.

### B6-T3 Package the repository-local command

Deliverables:

- Self-contained Native AOT `win-x64` executable named `dogdouspec.exe`.
- `dogdouspec.cmd` workspace-local wrapper.
- Version reporting and pinned-tool update policy.
- Installation-free usage documentation.

Acceptance:

- A clean supported Windows machine can use the wrapper without global registration or editor configuration.
- The wrapper discovers the project from the root and nested repository directories.
- Missing or mismatched binaries fail with actionable diagnostics.

Dependencies: B3-T1, B6-T2.

### B6-T4 Build the end-to-end bootstrap acceptance suite

Scenarios:

1. Initialize an empty multi-repository project.
2. Create and complete a simple Iteration.
3. Create and complete a Research Work Item.
4. Expand a local task.
5. Extract Research from an unexpected Issue.
6. Approve a material Change and invalidate stale evidence.
7. Supersede an Iteration and transfer unfinished tasks.
8. Defer a non-blocking Issue to backlog.
9. Reject an illegal deferral and an unauthorized accepted risk.
10. Detect a stale revision and recover simulated transaction crashes.
11. Generate scoped context and synchronize the policy digest.

Acceptance:

- Every scenario runs through the public CLI without direct XML editing.
- All resulting workspaces pass whole-project validation.
- Negative scenarios leave the workspace unchanged.
- Output is verified in both human and JSON modes.

Dependencies: B2-T5, B3-T3, B4-T3, B5-T4, B5-T5, B6-T2, B6-T3.

### B6-T5 Pass the first-usable-version gate

Acceptance:

- All conditions in Section 18 of `V1_DESIGN.md` pass from a clean workspace.
- All automated tests pass using the documented repository build entry point.
- No v1 non-goal was introduced implicitly.
- Known non-blocking issues have explicit recorded disposition outside the self-hosted workflow.
- The owner explicitly authorizes the self-hosting migration.

Dependencies: B6-T4.

## 12. B7 - DogdouSpec self-hosting migration

### B7-T1 Initialize the Dogdou project workspace for DogdouSpec

Deliverables:

- An owner-selected Dogdou workspace root with explicit Git ownership.
- `.dogdouspec/project.xml` with the DogdouSpec repository cataloged.
- Initial project policies and verified knowledge.
- Repository-local wrapper bound to the accepted v1 binary.

Acceptance:

- Initialization uses only the accepted public CLI.
- The new workspace passes whole-project validation.
- No implementation status is invented during import.

Dependencies: B6-T5 and explicit migration authorization.

### B7-T2 Import the v1 design and reconcile actual implementation

Deliverables:

- An approved bootstrap Iteration specification derived from `V1_DESIGN.md` and this plan.
- Tasks and evidence reconciled against the actual repository state.
- Findings for any difference between planned and delivered v1 behavior.

Acceptance:

- Imported XML links back to these bootstrap documents as provenance.
- Completed status is assigned only where current evidence proves acceptance.
- Unimplemented, changed, or deferred behavior has an explicit disposition.

Dependencies: B7-T1.

### B7-T3 Switch repository guidance to the managed workflow

Deliverables:

- Updated root `AGENTS.md` bootstrap instructions.
- Validated mandatory-policy digest.
- Checked-in project Skill for DogdouSpec iteration management.
- Traditional bootstrap plan marked historical without deleting it.

Acceptance:

- A new Agent can load scoped context and resume the next managed task using only repository instructions and the local CLI.
- The Skill has no hidden state and performs no direct XML writes.
- The repository passes validation after a fresh checkout.

Dependencies: B7-T2.

## 13. B8 - First managed iteration

### B8-T1 Create the first post-bootstrap improvement Iteration

Candidate scope must be selected from evidence-backed v1 limitations. Possible candidates include usability improvements, schema migration ergonomics, stronger evidence integrity, performance, or an optional adapter.

Acceptance:

- The Iteration is created, planned, executed, changed if necessary, evidenced, and completed entirely through the managed workflow.
- Any proposed MCP adapter remains optional and does not change the CLI or XML authority model.
- Retrospective findings are promoted to knowledge or policy only through explicit disposition.

Dependencies: B7-T3.

## 14. Verification strategy

Verification should be layered:

1. MSTest 4 unit tests on Microsoft Testing Platform for identity, lifecycle, policy, completion, and diagnostic rules.
2. XML parser and schema security tests.
3. Property or generated tests for transition and reference invariants.
4. Filesystem tests for discovery, containment, locking, atomic replacement, and recovery.
5. CLI contract tests for exit codes and deterministic JSON.
6. Golden-workspace tests for valid and invalid project states.
7. End-to-end tests using only the public CLI.
8. A clean-machine packaging smoke test before self-hosting.

No test may claim completion by bypassing the public domain rules through direct fixture mutation, except narrowly scoped parser or validator unit tests.

## 15. Review gates

Implementation stops for owner review at these gates:

- G1: Domain identities, lifecycle, and schemas are ready to freeze.
- G2: Transaction and recovery semantics are ready to freeze.
- G3: Change, successor, and backlog semantics pass end-to-end tests.
- G4: The complete first-usable-version gate passes.
- G5: Self-hosting migration is authorized.

Passing an earlier gate does not authorize later external actions or the self-hosting migration.
