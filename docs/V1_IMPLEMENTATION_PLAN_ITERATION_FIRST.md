# DogdouSpec v1 Iteration-First Implementation Plan

Status: Proposed execution plan

This plan replaces the executable scope of the legacy
`V1_IMPLEMENTATION_PLAN.md`. Implementation remains traditional repository
development until the first usable CLI passes its bootstrap acceptance gate.

Normative dependencies:

- `V1_XML_SCHEMA_CONTRACT.md`
- `V1_CLI_CONTRACT.md`
- `V1_SKILL_WORKFLOW.md`
- `schemas/v1`
- `templates/v1`
- `docs/demos/v1-core`

## 1. First usable version

V1 is usable when a clean Windows project can:

1. Use a repository-local executable without MCP or global installation.
2. Initialize an iteration-first `.dogdouspec` workspace.
3. Create feature and Research Work directories.
4. Validate managed XML against the shipped v1 schemas and semantic rules.
5. Query one document and search a declared boundary with XPath 1.0,
   variables, `ds:filter`, and `ds:filter-out`.
6. Obtain templates and append structured records safely.
7. Atomically update independent Task state with revision and idempotency
   checks.
8. Report technical readiness without making product decisions.
9. Apply an explicit owner confirmation to protected SPEC state.
10. Resume the next actionable Task using the checked-in Skill workflow.

## 2. Fixed implementation baseline

- .NET 10, C# 14, nullable enabled.
- Windows 11 x64 for v1 acceptance.
- Debug JIT build for development.
- Self-contained Native AOT `win-x64` release executable.
- `System.CommandLine` for CLI parsing.
- Built-in `System.Xml`, `System.Xml.XPath`, and `XmlSchemaSet` APIs.
- MSTest for unit and integration tests.
- No XML namespace in managed documents.
- XPath 1.0 with one custom `XsltContext`.
- XML and human output; no required v1 JSON contract.
- One project-level interprocess writer lock.

## 3. M0 - Freeze the executable contract

### M0-T1 Validate schemas and fixtures

Deliverables:

- Compilable v1 XSD set.
- Valid feature, Research, tasks, knowledge, backlog, and request fixtures.
- Invalid fixtures for missing rationale, illegal state, malformed ID,
  unresolved reference, and protected mutation.

Acceptance:

- Every valid fixture passes the intended XSD.
- Every invalid fixture fails for its intended diagnostic category.
- The current iteration-first demo validates without compatibility exceptions.

### M0-T2 Freeze templates and record semantics

Deliverables:

- Discussion, Finding, verification, Task update, confirmation, knowledge, and
  backlog templates.
- Append-oriented correction and supersession rules.
- Index key and normalization rules.

Acceptance:

- Templates are schema-valid before semantic placeholder checks.
- A structured discussion can preserve trigger, options, reasons, and outcome.
- A conclusion cannot mutate protected product state merely by being recorded.

### M0-T3 Approve CLI contract and reduced scope

Acceptance:

- Generic transaction is an escape hatch rather than the normal Skill path.
- No old project catalog, Evidence, lease, policy digest, or event-log
  requirement remains in the executable v1 plan.
- Product completion remains a separate owner decision.

Dependencies: M0-T1, M0-T2.

## 4. M1 - Repository and secure XML foundation

### M1-T1 Bootstrap solution and build entry point

Deliverables:

- Pinned SDK configuration.
- Core, CLI, and test projects.
- Root `build.cmd` for restore, build, test, and release publish.
- Repository-local `dogdouspec.cmd` wrapper.

Acceptance:

- A new checkout builds with the documented root command.
- Native AOT analysis is enabled for release code.
- Schemas and templates are embedded resources with versioned lookup.

### M1-T2 Implement secure XML loading and deterministic serialization

Acceptance:

- DTD, external entity, XInclude, and external schema resolution fail closed.
- Document and structural limits are enforced.
- UTF-8, indentation, final newline, and stable output tests pass.
- Untouched narrative text is preserved.

Dependencies: M1-T1.

### M1-T3 Implement workspace discovery and document enumeration

Acceptance:

- Nearest-ancestor `.dogdouspec` discovery is deterministic.
- Explicit root override is contained and normalized.
- Directory enumeration recognizes Work candidates and special documents.
- Traversal, symlink, junction, case, and alternate-stream tests pass.

Dependencies: M1-T2.

## 5. M2 - XPath read surface

### M2-T1 Implement variables and projection functions

Acceptance:

- String variable grammar and duplicate/unbound errors match the contract.
- `filter` and `filter-out` support only attributes and direct children.
- Missing members are ignored and duplicates coalesced.
- Projected results remain composable node-sets with deterministic order.
- Extension functions have no side effects or external access.

### M2-T2 Implement query and search output

Acceptance:

- Element node-sets use the compact wrapper.
- Scalar and mixed node results preserve type without excessive wrapping.
- Search visits managed documents in deterministic path order.
- Limits fail without partial successful output.

Dependencies: M1-T3, M2-T1.

## 6. M3 - Schema and semantic validation

### M3-T1 Implement versioned XSD validation

Acceptance:

- Document type and schema version select embedded schemas deterministically.
- Workspace schema copies are readable but cannot override the executable.
- Diagnostics identify file, line when available, object ID, and rule code.

### M3-T2 Implement project indexes and references

Acceptance:

- Project IDs are unique and time-first.
- Document, iteration, and project references resolve at the narrowest declared
  boundary.
- Duplicate and dangling targets fail.
- Reverse lookup requires no persisted backlink.

### M3-T3 Implement lifecycle and readiness rules

Acceptance:

- Task transitions, completion predicates, and dependency cycles are checked.
- Requirement, acceptance, design, and Iteration protected states are checked.
- Readiness reports technical facts and pending product decisions separately.
- A technically ready Iteration remains non-terminal until confirmation.

Dependencies: M3-T1, M3-T2.

## 7. M4 - Atomic write surface

### M4-T1 Implement writer lock, revision, commit, and recovery

Acceptance:

- Stale revisions change no file.
- One changed document becomes wholly old or wholly new after interruption.
- Iteration creation and multi-document transactions recover completely.
- Temporary requests and recovery files are not retained as project history.

### M4-T2 Implement schema and template discovery

Acceptance:

- `schema show` and `template show` return exact versioned resources.
- Unsupported names and versions fail clearly.
- All shipped templates pass schema validation tests.

### M4-T3 Implement append and idempotent retry

Acceptance:

- Parent XPath selects exactly one element.
- The resulting document validates before commit.
- An identical existing appended ID returns already-applied success.
- Different content under the same ID fails as an idempotency conflict.
- Protected product nodes cannot be appended or altered through this path.

### M4-T4 Implement Task update

Acceptance:

- Task selection is by stable ID and expected tasks revision.
- Transition, acceptance criterion results, active record resolution, context
  update, and record appends commit atomically into `tasks.xml`.
- Appended records are stamped with request `operation_id`.
- Completion predicates and terminal checks run prospectively before `done`.
- Retry with pre-commit or current revision returns already-applied success.
- Generic append rejects fragments with `record/@operation_id` (anti-spoofing).
- The helper cannot modify `spec.xml` or product confirmation state.

### M4-T5 Implement low-level transaction apply

Acceptance:

- XPath 1.0 assertions, variables, exact cardinality, and sequential
  append/replace/set/remove operations are deterministic and bounded.
- Only real managed-document nodes can be mutation targets; projection clones,
  document roots, root revision, and Task operation receipts are not mutable.
- Every changed document increments once and all changed documents enter one
  recovery-backed commit; semantic no-ops write nothing.
- Resolved before/after authority checks prevent alternate XPath spellings from
  bypassing product confirmation rules.
- Prospective XSD and whole-project semantic validation happen before publish;
  negative cases preserve every target byte.

### M4-T6 Implement readiness and owner confirmation

Acceptance:

- Readiness performs no write.
- Confirmation requires explicit decisions and exact source revisions.
- Waiver requires rationale.
- Confirmation writes protected SPEC state and provenance atomically.
- No Task or validation result automatically invokes confirmation.

Dependencies: M3-T3, M4-T1, M4-T5.

## 8. M5 - Workspace creation, Skill, and acceptance

### M5-T1 Implement workspace and Work creation

Acceptance:

- Init creates special files and readable schema/Skill content atomically.
- Feature and Research creation produce schema-valid `spec.xml` and
  `tasks.xml`.
- Existing target state is never silently renamed or overwritten.

### M5-T2 Check in the environment-neutral workflow Skill

Acceptance:

- The Skill performs index-first reads and one-Task full reads.
- It uses templates/helpers before raw transactions.
- It records structured reasoning rather than transcripts or conclusions only.
- It handles surprise disposition and stops at every product authority gate.

### M5-T3 End-to-end bootstrap suite

Scenarios:

1. Initialize and discover from a nested repository path.
2. Create and activate a feature Iteration with explicit owner confirmation.
3. Query unfinished indexes and resume one complete Task.
4. Append a structured discussion from a template.
5. Retry the append without duplication.
6. Block a Task with a Finding and reject an invalid revision retry.
7. Create Research for an unknown blocking question.
8. Reconcile an owner-confirmed material design change.
9. Verify and technically complete all Tasks.
10. Produce readiness while product criteria remain pending.
11. Reject a generic transaction that attempts product completion.
12. Complete only through explicit owner confirmation.

Acceptance:

- Scenarios use only the public CLI and repository-native build entry point.
- Resulting workspaces pass whole-project validation.
- Negative cases leave managed state unchanged.

Dependencies: M2-T2, M3-T3, M4-T5, M5-T1, M5-T2.

## 9. M6 - First usable release and self-hosting gate

### M6-T1 Package and smoke test

Acceptance:

- Clean supported Windows machine uses `dogdouspec.cmd` without global setup.
- Native AOT executable reports version and finds embedded schemas/templates.
- Nested-path discovery, query, append, Task update, validation, and readiness
  pass smoke tests.

### M6-T2 Owner authorizes DogdouSpec migration

The repository is not considered self-hosted merely because XML demo files
exist. Migration begins only after the full public-CLI acceptance suite passes
and the owner explicitly authorizes it.

### M6-T3 Create the first managed improvement Iteration

Acceptance:

- Planning, structured discussion, Task execution, surprise handling,
  verification, readiness, and product completion all use the accepted CLI and
  Skill.
- Bootstrap documents remain historical provenance.

Dependencies: M6-T1 and explicit owner authorization.

## 10. Deferred after v1

- MCP or editor adapters.
- GUI and autonomous scheduling.
- Cryptographic approval identity.
- Independent Evidence or event stores.
- Claim/lease protocols and Task sharding.
- Generated `AGENTS.md` policy digest.
- Arbitrary user-defined schema execution.
- JSON compatibility contract.
