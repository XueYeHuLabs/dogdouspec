# DogdouSpec Usability, Iteration-Owned Results, VCS Checkpoint, and Handoff Proposal

- **Status:** Proposed / Non-normative
- **Date:** 2026-09-01
- **Supersedes:** `WORKSPACE_VCS_CHECKPOINT_AND_HANDOFF_PROPOSAL.md`
- **Target:** Immediate documentation and Skill convergence, followed by separately approved CLI and schema work
- **Normative foundations:** [`V1_XML_SCHEMA_CONTRACT.md`](V1_XML_SCHEMA_CONTRACT.md), [`V1_CLI_CONTRACT.md`](V1_CLI_CONTRACT.md), [`V1_SKILL_WORKFLOW.md`](V1_SKILL_WORKFLOW.md), and [`V1_DESIGN.md`](V1_DESIGN.md)

---

## 1. Executive Summary

Dogfooding exposed two related gaps:

1. agent-produced implementation and review results had no explicit persisted owner, which encouraged durable Markdown or JSON reports under `.agents/work-results/`; and
2. locally durable `.dogdouspec/` XML could remain untracked when product commits were created, leaving a handoff dependent on one worktree.

Both gaps are ownership problems. DogdouSpec already defines `tasks.xml` records as the owner of attempts, findings, discussion, verification, completion, and handoff facts. It must not create a second semantic ledger in an agent-output directory. Likewise, an atomic DogdouSpec document commit is not a Git checkpoint: a Git-backed repository needs explicit checkpoint boundaries so authoritative XML can travel across machines and sessions.

This proposal therefore establishes one coherent model:

- semantic agent results are iteration state and are recorded in Task records;
- temporary worker response files are transport only and may be deleted at any time;
- only inherently large raw evidence may remain external, referenced from the relevant Task record;
- `.agents/work-results/` is not a canonical hierarchy, persistence boundary, or recovery dependency;
- Git-backed workspaces checkpoint authoritative `.dogdouspec/` documents at material lifecycle and handoff boundaries, without DogdouSpec automatically staging or committing;
- usability porcelain remains schema-aware and preserves owner authority.

---

## 2. Dogfood Observations

### 2.1 Result ownership was ambiguous

Agents naturally wrote detailed reports outside `.dogdouspec/`. Those reports mixed two different classes of information:

- compact semantic facts needed to resume the iteration, such as changed files, commit identities, commands, exit codes, findings, risks, review disposition, blockers, and next actions; and
- bulky or operational material, such as full test logs, traces, dumps, screenshots, packages, provider logs, prompts, and mutation request files.

Treating both classes as durable external files duplicates Task state and makes the iteration incomplete without an undocumented directory. Treating neither as durable loses important handoff context. The correct boundary is semantic ownership, not file format.

### 2.2 Local document durability was mistaken for transport durability

The XML transaction layer provides locks, revision checks, atomic replacement, and crash recovery. It does not make an untracked workspace available to another clone, worktree, host, or later checkout. During dogfooding, valid product commits existed while active `spec.xml` and `tasks.xml` remained untracked. The implementation was committed, but the governing iteration was not transport-ready.

### 2.3 Usability still exposes plumbing

Several common activities require raw XPath or XML request payloads:

- explaining effective Task scope;
- reviewing and revising Tasks;
- listing and summarizing Task state;
- distinguishing execution terminality from product confirmation;
- obtaining a bounded view across explicitly named workspaces.

These remain valid proposal targets, but they must not introduce another authority store.

---

## 3. Goals and Non-Goals

### 3.1 Goals

1. Give every durable semantic execution fact exactly one persisted owner in `tasks.xml`.
2. Define a narrow external-evidence boundary for data that is unsuitable for XML embedding.
3. Remove any dependency on `.agents/work-results/` for iteration recovery, review, or handoff.
4. Define Git checkpoint and handoff expectations for authoritative `.dogdouspec/` state.
5. Improve scope, review, revise, visibility, readiness, and aggregation UX without weakening schema or authority gates.
6. Keep documentation, generated guidance, Skill content, installation instructions, and CLI help consistent with implemented behavior.

### 3.2 Non-Goals

1. No automatic `git add`, `git commit`, push, tag, or branch mutation.
2. No detached evidence registry, report database, or managed work-results tree.
3. No retention of raw prompts, chat transcripts, provider telemetry, mutation envelopes, or hidden chain-of-thought by default.
4. No requirement to embed large binary or high-volume payloads in XML.
5. No automatic product acceptance or iteration confirmation by an agent.
6. No shared cross-workspace transaction, lock, or central catalog.
7. No advertisement of proposed commands in current CLI help before implementation.

---

## 4. Ownership Model

### 4.1 Authoritative iteration state

In a v1 workspace, the authoritative state is:

```text
.dogdouspec/
  backlog.xml
  knowledge.xml
  <iteration-id>/
    spec.xml
    tasks.xml
```

`tasks.xml` owns the semantic result of agent work. A Task record should preserve, when relevant:

- concise implementation or investigation summary;
- changed file paths and immutable source commit identities;
- executed commands, exit codes, and summarized verification outcomes;
- acceptance criteria or requirement coverage;
- findings, defects, risks, and their disposition;
- reviewer identity, review separation, and approval or changes-requested outcome;
- actor, tool/model, and session correlation when operationally useful;
- blocker, handoff state, next permitted action, and prohibited actions.

Existing v1 `<summary>`, `<details>`, `<checks>`, `<covers>`, findings, discussion, verification, completion, and handoff record forms remain the current representation. A future schema revision may add structured fields only when repeated dogfood evidence proves that the existing record vocabulary is insufficient.

### 4.2 Transient worker transport

An orchestrator may ask a worker to return JSON, Markdown, XML, or plain text. That response is a transport envelope, not durable project state. Before ending or handing off, the orchestrator extracts material facts into the appropriate Task record.

The workflow must remain recoverable after all temporary worker output is deleted. A repository may use any scratch location permitted by its own tooling, but DogdouSpec does not reserve or standardize `.agents/work-results/`.

### 4.3 External raw evidence

Some evidence is inherently too large or binary for managed XML, including:

- ETL or execution traces;
- crash dumps;
- packages and installer payloads;
- screenshots or recordings;
- complete test logs, coverage bundles, or benchmark datasets.

Such payloads may be stored in a repository-approved artifact system or path. The owning Task record must contain the semantic outcome and, when needed, a stable locator, digest, media kind, size, producer, and coverage relationship. Losing the payload may reduce audit depth, but must not erase the iteration's semantic outcome.

This proposal does not prescribe a workspace-local artifact root. Local, CI, release, or object-storage locations are repository policy. Raw prompts, provider rate-limit logs, OAuth material, and mutation request XML are not evidence artifacts merely because they are files.

### 4.4 One fact, one persisted owner

The same semantic report must not be maintained in both `tasks.xml` and a durable Markdown/JSON report. External payload metadata belongs in the Task record; the payload owns only its raw bytes. Git commit messages may summarize product changes, but they do not replace Task history.

---

## 5. Workspace File and VCS Classification

### 5.1 Files to checkpoint

Git-backed repositories should version authoritative managed documents and, when vendored, tool-owned support material:

```text
.dogdouspec/
  backlog.xml
  knowledge.xml
  <iteration-id>/spec.xml
  <iteration-id>/tasks.xml
  _schema/
  _skill/
```

Completed, cancelled, or superseded iterations remain governance history unless an explicit archival policy moves them to another durable content-addressed store.

### 5.2 Runtime-only state

`.dogdouspec/_tmp/` contains locks, staging files, transaction journals, and recovery scratch state. It must not be versioned. Initialization guidance should recommend ignoring exactly this runtime subtree, not all of `.dogdouspec/`.

### 5.3 Document commit versus VCS checkpoint

- A **document commit** is a successful DogdouSpec mutation that advances managed-document revisions.
- A **VCS checkpoint** is a repository commit that captures validated managed documents.
- A **governance checkpoint** primarily contains `.dogdouspec/` state and closely related policy material.
- **Transport-ready** means another authorized agent can resume from versioned repository state without local reports or chat history.

Git is an outer persistence and review layer, not part of XML transaction success.

---

## 6. Checkpoint and Handoff Contract

### 6.1 Material checkpoint boundaries

A Git-backed Mode B workspace should checkpoint validated authoritative state before:

1. iteration activation or owner adoption;
2. a material requirement, acceptance, waiver, scope, dependency, or replanning decision;
3. Task transfer, split, supersession, cancellation, completion, or review disposition;
4. handoff to another agent, worktree, host, or coordinator;
5. pausing for credentials, rate limits, hardware, VMware, signing, or another external blocker;
6. release, package, deployment, signing, or externally meaningful verification gates;
7. iteration confirmation or closure.

Intermediate progress records may be batched during one uninterrupted execution interval. The workspace must not be called transport-ready while intended managed files remain untracked or dirty.

### 6.2 Product and governance commits

The default history shape is:

1. commit reviewed product changes from exact allowlists;
2. record immutable product commit identities and summarized verification in the Task;
3. run `dogdouspec validate --format xml`;
4. inspect `git status --short -- .dogdouspec`;
5. create a separate governance checkpoint when commit authority exists.

A repository may combine product and governance changes when explicit policy requires it. Excluding `.dogdouspec/` from a product commit does not make the managed documents disposable.

DogdouSpec and its Skill must never infer Git mutation authority. If authority is unavailable, report the workspace as **locally durable but not transport-ready** and name the exact uncheckpointed managed files.

### 6.3 Multi-repository ordering

For a coordinating iteration:

1. checkpoint each component repository from its reviewed allowlist;
2. record exact component commits and verification outcomes;
3. validate the coordinated candidate;
4. update and validate the coordinating Task;
5. checkpoint the coordinating governance state.

This records coordination across immutable states; it does not create a cross-repository transaction.

### 6.4 Stop and handoff sequence

Before stopping at a user-defined gate or handing off:

```powershell
dogdouspec validate --format xml
dogdouspec query --document "<iteration-id>/tasks.xml" --xpath "/tasks/task[@id='<task-id>']" --format xml
git status --short -- .dogdouspec
```

The Task must state completed commits and checks, unresolved findings and risks, the exact blocker or authority gate, and the next permitted action. Raw prompts, provider logs, temporary request files, and agent reports must not be prerequisites for resumption.

---

## 7. Proposed Usability Porcelain

All commands in this section are proposals until separately implemented and tested. They must not appear in current CLI help as available commands.

### 7.1 Scope explanation and worktree verification

Propose `task scope explain` to show effective include/exclude rules, the rule that matched each path, and relevant component roots. Propose an explicit `--worktree` verification mode that considers committed, staged, unstaged, and untracked paths within bounded repository roots.

Any cross-session worktree baseline is non-authoritative execution input. Material mismatches and the final verified outcome belong in Task records; no hidden baseline file may become required state.

### 7.2 Review and revise

Propose high-level `task review approve`, `task review request-changes`, and additive `task revise` forms that compile to the existing schema-validated mutation engine. They must preserve expected revisions, reviewer separation, immutable terminal states, and raw XML fallback.

Review attribution is provenance, not authentication. Workspace policy may bind actor, model, session, source commits, and external evidence digests, but DogdouSpec does not prove that a human or provider identity controls those strings.

### 7.3 Task visibility

Propose schema-neutral `task list`, `task show`, and `task summary` views. They should default to compact indexes and expose opt-in record detail, preserving the two-phase context discipline.

### 7.4 Readiness dimensions

Readiness output should separate:

- execution terminality;
- verification completeness;
- unresolved blocking findings;
- product confirmation state;
- VCS checkpoint state and transport readiness.

No aggregate Boolean may imply product acceptance when owner confirmation is still pending.

### 7.5 Read-only workspace aggregation

Propose bounded aggregation over explicitly named workspace roots or an external member manifest. It takes no writer locks, performs no recursive discovery, mutates no child workspace, and reports each member independently. A coordinator may summarize member commit identities in its own Task records.

### 7.6 VCS diagnostics

Propose read-only `workspace vcs-status` and `workspace checkpoint-plan` diagnostics. They may classify managed files as tracked, untracked, modified, staged, or ignored and report whether a checkpoint boundary is satisfied. They must not stage, commit, push, or change ignore rules.

---

## 8. Current Guidance Convergence

Before new porcelain or schema work, current user-facing surfaces should agree on implemented behavior:

1. `SKILL.md` and generated `AGENTS.md` state that semantic results belong in Task records and that no external report directory is authoritative.
2. README and installation guidance state that managed `.dogdouspec/` documents should be versioned in Git-backed Mode B repositories while `_tmp/` remains runtime-only.
3. initialized `_skill/README.md` explains result ownership, checkpoint responsibility, and Git authority boundaries.
4. existing root, `workspace`, `workspace init`, `skill`, and `skill sync` help describes these responsibilities without listing proposed commands.
5. normative workflow documentation includes the stop/handoff checkpoint check.

This convergence is documentation and help correction, not implementation of the commands proposed in Section 7.

---

## 9. Security and Privacy

| Risk | Required boundary |
|---|---|
| Secret leakage through reports | Persist concise redacted semantic facts; do not retain raw prompts, authentication material, environment dumps, or provider logs by default. |
| XML bloat | Keep bulky raw payloads external and store only bounded metadata plus semantic outcomes in Task records. |
| Path traversal | Any future local artifact locator must be normalized, bounded by repository policy, and checked against traversal and reparse escapes. |
| Tampered external evidence | Bind audit-critical payloads to a cryptographic digest in the owning Task record. |
| False handoff confidence | Report untracked or dirty authoritative XML as locally durable but not transport-ready. |
| Unauthorized Git changes | Diagnostics remain read-only; staging, committing, and pushing require explicit authority. |
| Duplicate truth | Reject durable semantic reports that compete with Task records. |

---

## 10. Phased Roadmap

### Phase 0: Guidance convergence

- merge the two proposals;
- remove the canonical work-results concept;
- update Skill, README, installation guide, generated guidance, workflow documentation, and existing CLI help;
- add tests for generated and displayed guidance.

### Phase 1: Schema-neutral visibility

- Task list/show/summary;
- scope explanation and bounded worktree verification;
- readiness dimensions;
- read-only VCS status and checkpoint planning.

### Phase 2: Mutation porcelain

- review approve/request-changes;
- additive revise;
- consistent expected-revision and diagnostic behavior.

### Phase 3: Evidence references, only if dogfood demand remains

- define the smallest schema evolution for external raw-payload references;
- keep semantic outcomes in Task records;
- provide no managed output directory or general report store;
- require explicit workspace upgrade and old-CLI fail-closed behavior.

### Phase 4: Read-only aggregation

- bounded explicit members;
- per-workspace diagnostics and readiness;
- no shared authority or cross-workspace mutation.

---

## 11. Acceptance Criteria

1. Deleting every `.agents/work-results/` directory does not affect validation, recovery, review, readiness, or handoff.
2. A Task can be resumed from `tasks.xml` with its implementation summary, source commits, checks, findings, review state, risks, blocker, and next action intact.
3. Raw prompts, chats, mutation envelopes, and provider telemetry are absent from default persisted state.
4. Large external evidence can be audited through a Task-owned locator and digest without duplicating its bytes in XML.
5. Git-backed guidance distinguishes document commits from VCS checkpoints and ignores only `.dogdouspec/_tmp/` by default.
6. Handoff reports fail closed when authoritative documents are untracked or dirty relative to the intended checkpoint.
7. DogdouSpec never performs a Git write without explicit user or repository authority.
8. Current CLI help documents only implemented commands; proposed porcelain remains labeled as proposed in this document.
9. Skill sync and workspace initialization generate guidance consistent with these rules.
10. Owner confirmation remains required for product acceptance and iteration closure.

---

## 12. Rejected Alternatives

| Alternative | Reason rejected |
|---|---|
| Canonical `.agents/work-results/<iteration>/<task>/` hierarchy | Creates a second persistence model and makes handoff depend on non-authoritative files. |
| Durable per-agent Markdown or JSON reports | Duplicates Task semantics and permits conflicting truth. |
| Embed complete logs, traces, or binaries in XML | Bloats managed documents and harms bounded XPath access. |
| Store resumable state in `.dogdouspec/_tmp/` | Confuses crash-recovery scratch with durable iteration authority. |
| Reconstruct iteration state from Git messages | Git history cannot replace schema-validated requirements, Tasks, records, revisions, and authority gates. |
| Automatically commit every DogdouSpec mutation | Couples XML correctness to Git and violates repository-write authority. |
| Ignore all of `.dogdouspec/` | Makes governed state local-only and prevents reliable handoff. |
| Autonomous agent product confirmation | Violates the human owner authority boundary. |
| Managed cross-workspace catalog | Introduces coupling and lock coordination that read-only aggregation avoids. |

---

## 13. Decision Summary

The iteration is the durable unit of agent work. Its semantic results live in Task records, not in a separate agent-report folder. External files are limited to raw payloads that are intrinsically unsuitable for XML and are referenced from the owning record. In Git-backed workflows, validated authoritative documents are checkpointed at material boundaries so local durability becomes transport durability. Future CLI improvements must make these rules easier to follow without silently expanding authority or introducing a second source of truth.
