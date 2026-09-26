# External Dogfood Field Report

Findings from an external consumer driving one full governed iteration to completion in an
unrelated repository, then re-verifying every finding against the current `dev/optimize` HEAD.

| Field | Value |
|---|---|
| Reporter | External consumer repository (release orchestration, 3 submodules) |
| Usage basis | Iteration `20260901-v091-privileged-evidence-vmtest`: 12 tasks, `tasks.xml` to revision 110, `spec.xml` to revision 2, `backlog.xml` to revision 14 |
| Originally observed on | `1.0.0+123fc4dba73888692fe2cde3834576fb6bb87701` (WinGet `Vixasol.DogdouSpec`) |
| Re-verified against | `1.0.0+540b63740b3a5e12a03256d7c4110e6e48f47884` (built from source) |
| Method | Every status below was re-measured against a freshly built HEAD binary in a scratch workspace. Nothing is carried over on memory. |

---

## 1. Status Summary

| ID | Finding | Severity | Status vs `540b637` |
|---|---|---|---|
| F1 | Mutations cannot be checked before they are made | High | Unchanged |
| F2 | An undefined acceptance criterion still passes the gate | High | Unchanged; broader than first assessed |
| F3 | The knowledge store has no first-class write path | High | Partially mitigated |
| F4 | `task add` rejects its own canonical request document | Medium | Unchanged; root cause identified |
| F5 | Gates are discovered by tripping them | Medium | Unchanged |
| F6 | Managed documents gain one trailing newline per mutation | Medium | Unchanged; root cause identified |
| F7 | Committed state is unreviewable | Medium | Unchanged |
| F8 | Addressing and concurrency differ per command | Low | Partially fixed |
| F9 | Workspaces had no transport story | Low | Largely fixed |
| **F10** | **`workspace vcs-status` reports transport-ready with no repository** | **High** | **New defect, introduced by the F9 fix** |

---

## 2. Confirmed Improvements Since `123fc4d`

These are stated first because they are real and should not be undone by anything proposed below.

1. **Read-only porcelain.** `task list`, `task show`, `task summary`, and the top-level `summary`
   command remove a large amount of hand-written XPath from routine navigation.
2. **Revision and iteration auto-resolution.** `task quick`, `revise`, `start`, `verify`, and
   `finish` now resolve the current revision and the active iteration when those are omitted.
   This is the single largest ergonomic gain in the release.
3. **Readiness dimensions.** `iteration readiness` now reports named dimensions alongside raw
   technical checks, which makes the output legible without cross-referencing the schema.
4. **Workspace persistence tooling.** `workspace vcs-status`, `workspace checkpoint-plan`, and
   `workspace unlock` answer questions that previously had no CLI surface at all — subject to F10.
5. **The transport question is now decided.** `workspace init` writes a `.gitignore` that ignores
   only `/.dogdouspec/_tmp/`, and `templates/v1/AGENTS.md` states the rule. This was previously
   left to each consumer to guess.

---

## 3. Findings

### F1 — Mutations cannot be checked before they are made

**Severity:** High &nbsp;·&nbsp; **Status:** Unchanged

Anything richer than a state transition is submitted as a hand-authored XML request through
`--file` or `--stdin`. There is no pre-flight path for that document.

Measured on `540b637`, `--dry-run` is present on exactly one of twelve mutating commands:

| Command | `--dry-run` |
|---|---|
| `task quick` | yes |
| `task update`, `add`, `revise`, `split`, `review`, `finish` | no |
| `backlog add` | no |
| `iteration create`, `iteration confirm` | no |
| `requirement propose`, `append` | no |

`validate` still accepts only `--iteration` and `--document`, both resolved inside `.dogdouspec`,
so a request file cannot be schema-checked before it is applied. Every authoring mistake is
discovered by a failed mutation.

**Proposed change.** Add `--dry-run` to every mutating command, returning the parsed request and
the revision it would write — the shape `task quick --dry-run --format xml` already produces.
Failing that, extend `validate` with `--request <file>` so a request document can be checked
against `requests.xsd` and the current workspace state before submission.

---

### F2 — An undefined acceptance criterion still passes the gate

**Severity:** High &nbsp;·&nbsp; **Status:** Unchanged, and broader than first assessed

`IterationCreator.cs:262` (feature) and `:316` (research) seed the product criterion with
placeholder text. Nothing downstream requires it to be replaced.

Three separate observations make this worse than an unfilled default:

1. **`iteration create` cannot accept a criterion.** Its only options are `--id`, `--kind`,
   `--activate`. There is no first-class command anywhere in the CLI that sets criterion text, so
   the placeholder is not a default the caller declined — it is the only reachable state.
2. **`--activate` ships the placeholder into an active iteration** with its initial requirement
   already approved. Verified on a scratch workspace: the resulting `spec.xml` carries
   `<objective>Objective pending definition…`, `<statement>Requirement statement pending
   definition.</statement>`, and `<criterion … decision="pending">Product criterion pending
   definition.</criterion>`.
3. **Readiness cannot see it.** `iteration readiness --phase completion` reports
   `Acceptance criteria pending: 1` — a count, with no check on whether the criterion has content.

This is already load-bearing in this repository's own governance record. Three iterations carry
the literal placeholder string and were confirmed against it:

```
.dogdouspec/20260827-porcelain-commands/spec.xml:35        decision="accepted"
.dogdouspec/20260827-winget-distribution/spec.xml:35       decision="accepted"
.dogdouspec/20260902-dogfood-usability-evidence/spec.xml   decision="accepted"
```

Each reads `Product criterion pending definition.` An iteration signed off `accepted` against
undefined text asserts that a bar was cleared that nobody wrote. This is the only finding in this
report where the tool can certify something untrue.

**Proposed change.** Two parts, in order:

1. Give the criterion a write path — either options on `iteration create`, or a
   `requirement`-style command that sets criterion text under owner authority.
2. Make `iteration activate` refuse a criterion whose text still equals the seeded placeholder, so
   the bar must exist before work starts rather than at the moment the work is judged. At minimum,
   add a completion-phase readiness check that fails on placeholder text.

---

### F3 — The knowledge store has no first-class write path

**Severity:** High &nbsp;·&nbsp; **Status:** Partially mitigated

`knowledge.xml` is a first-class managed document with its own schema, and
`template show --name knowledge.entry` emits a correct entry skeleton — so the plumbing path
(`template show` then `append`) does exist, and the original report understated this.

What is still missing is porcelain. There is no `knowledge` command among the fifteen top-level
commands, and nothing in any workflow prompts for an entry. The consequence is visible in the
consuming repository: after a full iteration that produced exactly the kind of durable
cross-cutting facts the store exists to hold, `knowledge.xml` remains at `revision="1"` with zero
entries. Every one of those facts lives in task records scoped to a now-closed iteration.

**Proposed change.** Add `knowledge add` with the same option shape as `backlog add`, and surface
it at completion — `iteration confirm` is the natural moment to ask what outlived the iteration.

---

### F4 — `task add` rejects its own canonical request document

**Severity:** Medium &nbsp;·&nbsp; **Status:** Unchanged; root cause identified

Reproducible in two commands against a scratch workspace:

```bash
dogdouspec task quick --iteration <ITER> --title "Origin probe" --scope "src/" \
  --why "..." --done-when "..." --dry-run --format xml > req.xml
dogdouspec task add --iteration <ITER> --file req.xml --expected-revision <N>
```

```
[ERROR] INVALID_REFERENCE_TARGET_TYPE (<ITER>/tasks.xml): Operational origin for task
'<TASK_ID>' must be exactly one iteration supports reference to '<ITER>'.
```

The rejected document is the CLI's own canonical output, unmodified, and its origin is exactly
one `<ref scope="iteration" target="<ITER>" relation="supports"/>` — precisely what the message
demands.

**Root cause.** `src/DogdouSpec.Core/Tasks/TaskAdder.cs:320`:

```csharp
if (!string.Equals(commandName, "task quick", StringComparison.Ordinal) ||
    originRefs.Count != 1 ||
    !string.Equals(originRefs[0].Attribute("scope")?.Value, "iteration", …) ||
    !string.Equals(originRefs[0].Attribute("relation")?.Value, "supports", …) ||
    !string.Equals(originRefs[0].Attribute("target")?.Value, normIterId, …))
```

Operational origins are gated on the **command name**, not on the document. The first clause is
the only one that fails, and it is the one clause the error message never mentions — the message
enumerates the four structural conditions that all passed. That is why the failure cannot be
bisected: every stated requirement is already satisfied.

**Proposed change.** Decide which behaviour is intended, then make the code and the message agree:

- If `task add` should accept operational tasks, drop the `commandName` clause.
- If it should not, say so: *"Operational origins are only accepted via `task quick`. Use
  `task quick`, or supply an `implements` origin referencing an approved requirement."*

In either case, error text for structural rejections should print the parsed-actual next to the
expected rather than restating the rule.

---

### F5 — Gates are discovered by tripping them

**Severity:** Medium &nbsp;·&nbsp; **Status:** Unchanged

Real constraints are learned only by violating them. Each is individually defensible; the cost is
that none are announced before the mutation fails, and there is no `--explain` anywhere in the CLI.

Encountered while writing this report, on `540b637`, all from `--help` output that marks the
relevant options as optional:

```
backlog add  →  INVALID_ARGUMENT: At least one --source-iteration or --source-task is required.
backlog add  →  INVALID_ARGUMENT: Specify exactly one of --target-iteration or --review-condition.
task quick   →  TASK_REVIEW_IMPLEMENTER_UNKNOWN: --review-required must be paired with --agent.
task review  →  INVALID_ARGUMENT: --expected-revision must be positive.
```

Three round trips to add one backlog item. The last is a straightforward help defect:
`task review --help` does not mark `--expected-revision` as REQUIRED, but the runtime rejects its
absence.

Encountered during the original iteration and still present: `WAIVER_RATIONALE_MISSING` surfaces
only at `iteration confirm`; a record's `occurred_at` must not precede the task's `updated_at`;
active findings and questions block `task finish`. **The last of these should be kept exactly as
it is** — it stopped a task being closed over two genuinely unresolved findings, and is the single
most valuable thing the tool did during the iteration. The problem is only that it is met as a
failure rather than as a listed precondition.

**Proposed change.** Express conditional requirements in `--help`, and add `--explain` to mutating
commands to list what the operation will require against current state before anything is written.

---

### F6 — Managed documents gain one trailing newline per mutation

**Severity:** Medium &nbsp;·&nbsp; **Status:** Unchanged; root cause identified

Deterministic and reproducible on `540b637`. In a fresh workspace, `backlog.xml` trailing newline
bytes track the revision exactly:

| After | Revision | Trailing `\n` bytes |
|---|---|---|
| `workspace init` | 1 | 1 |
| `backlog add` ×1 | 2 | 2 |
| `backlog add` ×2 | 3 | 3 |
| `backlog add` ×3 | 4 | 4 |
| `backlog add` ×4 | 5 | 5 |
| `backlog complete` | 6 | 6 |

Normalizing the file to a single trailing newline and running one further mutation yields two —
so the existing trailing whitespace survives the load/save round trip, and the serializer appends
another on top of it.

**Root cause.** The `+ "\n"` idiom appears at nineteen call sites, most of which format stdout and
are harmless. Four write managed documents, and they split cleanly by load option:

| Write path | Load option | Leaks |
|---|---|---|
| `Backlog/BacklogLifecycle.cs:506` | `LoadOptions.SetLineInfo` | yes |
| `Tasks/TaskReviewer.cs:348` | `LoadOptions.SetLineInfo` | yes |
| `Iterations/IterationConfirmer.cs:1327` | `PreserveWhitespace \| SetLineInfo` | yes |
| `Tasks/TaskQuick.cs:182` | *(none)* | no |

Verified directly: in one scratch iteration, `task quick` and `task verify` left `tasks.xml` at one
trailing byte across revisions 4→6, and a single `task review` took it to two at revision 7. The
consuming repository shows the same signature — its `tasks.xml` at revision 110 carries two
trailing bytes, and exactly one `task review` was run against it.

**Why this now matters.** `.gitattributes` currently suppresses the symptom:

```
**/.dogdouspec/** whitespace=-blank-at-eol,-blank-at-eof
```

That rule protects this repository but not consumers. In the consuming repository — which requires
`git diff --check` to be clean before commit — `.dogdouspec/` was checkpointed as the new
`AGENTS.md` guidance directs, and the check immediately failed on three managed files. The caller
could not fix it either, because hand-editing managed XML is forbidden by the same guidance.

**Proposed change.** Normalize in one place: trim trailing whitespace, then append exactly one
newline. Add a round-trip test asserting that a no-op rewrite is byte-identical. Consider
extracting the duplicated `XmlWriterSettings` block — it appears 28 times across 27 files — into a
single canonical serializer, which would prevent this class of divergence rather than fixing one
instance of it.

---

### F7 — Committed state is unreviewable

**Severity:** Medium &nbsp;·&nbsp; **Status:** Unchanged

Prose-bearing elements are serialized without wrapping. Measured on a fresh workspace holding four
backlog items, `backlog.xml` is 13 lines with a longest line of 3,941 characters. In the consuming
repository the same document reached 16 lines with a longest line of 25,661; `tasks.xml` reached
1,551 lines with a longest line of 5,719.

Every edit to a record therefore lands as a whole-line replacement. Diffs show nothing useful,
`git blame` resolves to the line rather than the record, and a conflict would be unmergeable by
hand. This was tolerable while workspace state was local-only. Now that `AGENTS.md` directs
consumers to version `.dogdouspec/`, it undercuts the audit trail the documents exist to produce.

**Proposed change.** Serialize with stable pretty-printing — one element per line, and hard-wrap
prose content at a fixed column. The format is already canonical and machine-written, so a stable
line policy costs nothing and makes review possible.

---

### F8 — Addressing and concurrency differ per command

**Severity:** Low &nbsp;·&nbsp; **Status:** Partially fixed

Genuine progress: `--expected-revision` is now optional on `task revise`, `review`, `finish`, and
`start`, and `--iteration` auto-discovers the single active iteration. That removes most of the
read-then-write round trips the original report described.

What remains inconsistent:

| Surface | Current state |
|---|---|
| `task update`, `task add`, `backlog add` | `--expected-revision` still REQUIRED |
| `task review` | help says optional; runtime rejects absence (see F5) |
| `query`, `validate` | address documents by on-disk path (`<ITER>/tasks.xml`) |
| all mutating commands | address documents by `--iteration` |
| `iteration confirm` | revisions carried as `expected_spec_revision` / `expected_tasks_revision` XML attributes, with no flag equivalent |

**Proposed change.** Accept `--iteration` plus a document name on `query` and `validate`, and
accept `--expected-revision` as a flag on every mutating command including `iteration confirm`,
with the request document's attributes taking precedence when both are supplied.

---

### F9 — Workspaces had no transport story

**Severity:** Low &nbsp;·&nbsp; **Status:** Largely fixed — see F10

The original finding was that workspace state existed on exactly one disk with no backup, no sync,
and no stated policy. `540b637` answers this: `workspace init` writes a `.gitignore` ignoring only
`/.dogdouspec/_tmp/`, `templates/v1/AGENTS.md` states the checkpoint rule, and `workspace
vcs-status`, `checkpoint-plan`, and `unlock` give it CLI surface. The stale `_tmp/writer.lock`
left behind in older workspaces is now addressed by `workspace unlock`.

The risk this finding described was concrete rather than theoretical: removing two Git worktrees in
the consuming repository would have permanently destroyed two submodule-local workspaces, which
survived only because ignored content was checked before `git worktree remove` ran. The new
guidance prevents a repeat.

One residual: each workspace still carries its own copy of all six XSDs under `_schema/`. All three
workspaces in the consuming repository were checksummed and are byte-identical today, so there is
no drift to report — but nothing pins them, and a schema change would leave every existing
workspace to be reconciled independently.

**Proposed change (residual only).** Either version-stamp `_schema/` and have `validate` warn on a
mismatch against the CLI's embedded schemas, or resolve schemas from the CLI and treat the
workspace copy as a cache.

---

### F10 — `workspace vcs-status` reports transport-ready with no repository

**Severity:** High &nbsp;·&nbsp; **Status:** New defect, introduced by the F9 fix

In a directory that is **not a Git repository at all**, both new commands report success:

```
$ dogdouspec workspace vcs-status --format human
  Git Repository:    No
  Transport Ready:   YES (All authoritative files checkpointed)
  Managed Files:     12
  Uncheckpointed:    0

$ dogdouspec workspace checkpoint-plan --format human
  Status:            SATISFIED (Workspace is transport-ready)
  No uncheckpointed managed documents. Governance state is up to date.
```

Nothing has been checkpointed. There is nowhere for it to have been checkpointed to. The command
reports the governance property as satisfied because it never had evidence either way.

**Root cause.** `src/DogdouSpec.Core/Workspace/WorkspaceVcsStatus.cs` fails open in three places:

- `:83` — `string status = "clean";` initialises to the safe-looking value before any evidence is
  gathered.
- `:84` — `if (isGit)` guards the entire status determination, so when `isGit` is false every file
  keeps that default and `uncheckpointed` stays empty.
- `:104` — `bool isTransportReady = uncheckpointed.Count == 0;` therefore evaluates true.

The same path is taken when `isGit` is true but `git status` fails: `statSuccess && statExit == 0`
is checked at `:55`, and on failure `gitStatusMap` is simply left empty with no diagnostic raised.
A Git invocation failure is reported as a clean, transport-ready workspace.

This directly contradicts the rule the feature exists to enforce, stated in `templates/v1/AGENTS.md`:
*"If Git-write authority is absent, report the workspace as locally durable but not transport-ready
and list the exact uncheckpointed files."*

**Proposed change.** Fail closed. Default `status` to `"unknown"`; compute
`isTransportReady = isGit && gitStatusSucceeded && uncheckpointed.Count == 0`; raise a diagnostic
when `git status` fails; and render the no-repository case as *"NOT TRANSPORT-READY (no Git
repository — workspace is locally durable only)"*. Add tests for both the non-repository case and
the `git status` failure case.

---

## 4. Suggested Order of Work

1. **F10**, then **F2**. These are the two findings where the tool reports a governance property it
   has not established. F10 is a regression in new code and is a small, contained fix; F2 has been
   silently accepted three times in this repository's own record.
2. **F1**. The largest time sink, and the pattern already exists in `task quick`. Extending it
   would also blunt F4 and F5, since most of what those errors fail to explain becomes visible
   before submission.
3. **F6 and F7 together.** One serializer change. Now that consumers are directed to version
   `.dogdouspec/`, an audit trail that cannot be diffed or blamed is not doing the job it was
   committed for — and the `.gitattributes` suppression does not travel to consuming repositories.
4. **F4, F5, F8, F3** as ordinary ergonomics work.
5. **F9 residual** last; there is no drift today.

---

## 5. Method and Limits

Findings originate from driving iteration `20260901-v091-privileged-evidence-vmtest` to completion
against the WinGet build `1.0.0+123fc4d`. Every status in this report was then re-measured against
`1.0.0+540b637`, built from source with `dotnet publish -p:PublishAot=false`, in a disposable
scratch workspace — command surfaces, `--dry-run` availability, seeded spec content, readiness
output, error text, trailing-byte counts, and line lengths. The schema comparison across three
workspaces was done by checksum.

F4's rejection message is reproduced with identifiers replaced by placeholders. The F6 load-option
correlation is proven across four write paths but the underlying .NET whitespace-retention
behaviour was not traced into the framework; the correlation is the evidence offered, not a claim
about `XDocument` internals. The Native AOT publish path could not be exercised in this environment
(`vswhere.exe` was not resolvable), so all measurements come from the framework-dependent build.
