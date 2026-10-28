# DogdouSpec

DogdouSpec is an iteration-first structured XML/XPath specification and technical execution engine designed for human and AI pairing.

## Publisher and Support

DogdouSpec is published by Hangzhou Xueyehu Technology Co., Ltd. The company
hosts the project's source code and releases under its GitHub organization,
[XueYeHuLabs](https://github.com/XueYeHuLabs). The `Vixasol` namespace in the
WinGet package identifier `Vixasol.DogdouSpec` comes from the
[vixasol.com](https://vixasol.com) domain; it is not a separate publisher. The
[official publisher identity page](https://vixasol.com/publisher/) documents
the relationship between the legal publisher, the namespace, and the GitHub
organization.

- Project source and releases: [XueYeHuLabs/dogdouspec](https://github.com/XueYeHuLabs/dogdouspec).
- Support and issue reports: [GitHub Issues](https://github.com/XueYeHuLabs/dogdouspec/issues).
- Privacy: [DogdouSpec Privacy Policy](https://vixasol.com/privacy/dogdouspec/).
- License: [MIT](LICENSE).

## 1. Quick Start & Adoption Guide

### Standard Global Installation (Recommended for Most Projects)

For standard development environments with a global package manager:

1. **Install DogdouSpec globally on Windows**:
   ```powershell
   winget install Vixasol.DogdouSpec
   ```
2. **Initialize workspace in your repository**:
   Navigate to your project root and run:
   ```powershell
   dogdouspec workspace init
   ```
   This single command performs all mechanical initialization:
   - Creates `.dogdouspec/` with authoritative XSD schemas, `backlog.xml`, and `knowledge.xml`.
   - Copies the agent skill to `.agents/skills/dogdouspec/` (SKILL.md + references). Skips files that already exist.
   - Appends `/.dogdouspec/_tmp/` to `.gitignore` (idempotent; creates the file if not present).
3. **Read the setup guide and configure your project**:
   ```powershell
   dogdouspec skill guide
   ```
   The guide describes what to add to `AGENTS.md`, what to commit to Git, and how to tailor DogdouSpec to your project's build tools. These are **agent + owner decisions** — DogdouSpec never writes `AGENTS.md` automatically.
4. **Checkpoint the initialized governed state in Git-backed repositories**:
   - Managed `.dogdouspec/` documents and `.agents/skills/dogdouspec/` are authoritative project state. Review and commit them explicitly.
   - DogdouSpec does not run `git add`, `git commit`, or `git push`. Inspect `git status --short -- .dogdouspec` and create a checkpoint only with repository-write authority.

### Upgrading DogdouSpec

After installing a newer version:

```powershell
winget upgrade Vixasol.DogdouSpec

# Inspect changes and read the focused upgrade workflow:
dogdouspec skill status --format xml
dogdouspec skill guide --file references/upgrade.md
```

The embedded [upgrade contract](.agents/skills/dogdouspec/references/upgrade.md)
offers read-only workspace, Skill, schema, diff and optional customization-history
discovery before explicit synchronization. The calling agent reconciles `AGENTS.md`,
local guidance, wrappers, CI, and other repository-specific content. DogdouSpec
never makes those judgment-based edits, stages files, commits, or pushes. Upgrade
advice does not block `skill sync --force`; logging customization is optional.

---

### Air-Gapped & Source-Based Deployment (Repo-Local Vendoring)

For isolated, air-gapped, or hermetic environments where global tools, PATH modifications, or external package managers are prohibited:

- Follow the authoritative [Air-Gapped / Vendored Deployment Guide](docs/INSTALL_IN_OTHER_REPOSITORY.md) to compile a self-contained ~9.5 MB `dogdouspec.exe` into `<TARGET_REPO>/tools/dogdouspec/` with a root `dogdouspec.cmd` wrapper.

---

## 2. Developer Build and Test (From Source)

- .NET SDK `10.0.*` (`10.0.100` or compatible), target framework `net10.0`, C# 14.
- `System.CommandLine` 2.0.11 for CLI parsing.
- Built-in `System.Xml`, `System.Xml.XPath`, and `XmlSchemaSet`.
- MSTest 4.0.2 for unit and integration testing.

Run the repository build and test script:
```cmd
build.cmd
```
Or using the .NET CLI:
```powershell
dotnet build DogdouSpec.slnx -c Debug
dotnet test DogdouSpec.slnx -c Debug --no-build
```

This repository also uses the checked-in Skill that it embeds in the CLI. After
editing Skill sources, build them before self-hosted synchronization; use the
current source-built CLI for unreleased commands:

```powershell
dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- skill status
dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- skill diff
dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- skill sync --force
```

The `.skill-state.xml` baseline/history travels with this checkout but is not
embedded or distributed as another repository's history. An older global binary
contains its own older Skill; use it only when those are the intended contents.

---

## 3. CLI Commands Reference

All commands run directly through `dogdouspec <command> [options]`:

### 1. Workspace Commands
- **Workspace Discovery**
  ```powershell
  dogdouspec workspace discover [--workspace-root PATH] [--format xml|human]
  ```
- **Workspace Initialization**
  ```powershell
  dogdouspec workspace init [--workspace-root PATH] [--format xml|human]
  ```
  Initialization creates authoritative managed state. In Git-backed governed work, validate and checkpoint that state explicitly; the CLI never stages or commits it.
- **Workspace Unlock** (release stale writer locks and run startup recovery)
  ```powershell
  dogdouspec workspace unlock [--force] [--workspace-root PATH] [--format xml|human]
  ```

### 2. Skill and Schema Upgrade Primitives
- **Skill Guide** (authoritative guidance embedded in the current binary)
  ```powershell
  dogdouspec skill guide [--all | --file RELATIVE_PATH] [--format markdown|human|xml]
  ```
- **Skill Status** (read-only embedded-versus-repository comparison)
  ```powershell
  dogdouspec skill status [--output-dir PATH] [--format xml|human]
  ```
  Exit code `1` means differences were reported for caller review. It is not a command failure.
  `.skill-state.xml` stores a portable sync baseline and optional history alongside the Skill.
  Status adds per-file provenance and advisory `automatic_upgrade` (`ready`, `review-needed`,
  `unknown`, `not-needed`). Content hashes normalize CRLF to LF; existing customization may remain
  after upgrading. History never decides whether a file is currently customized.
- **Skill Diff and Optional History**
  ```powershell
  dogdouspec skill diff [--file RELATIVE_PATH] [--output-dir PATH] [--format unified|xml]
  dogdouspec skill record --summary "Why and what changed" [--output-dir PATH] [--format xml|human]
  dogdouspec skill records [--last 5] [--output-dir PATH] [--format xml|human]
  ```
  Users/agents edit Markdown directly. Recording intent is optional and never resets the baseline.
  Diff and records are read-only and require no Git. Diff exits `1` when differences exist.
- **Skill Sync** (synchronize skill files with this CLI's embedded version; pass `--force` to overwrite upon upgrade)
  ```powershell
  dogdouspec skill sync [--force] [--output-dir PATH] [--format xml|human]
  ```
  Requires `--force` to overwrite existing files. Never reads or modifies `AGENTS.md`. Agent and owner decide how to update `AGENTS.md` based on `skill guide` output.
  Sync preserves history, updates the baseline, and allows explicitly requested overwrites even
  with local changes or unknown provenance. Export has the same metadata behavior.
- **Skill Export**
  ```powershell
  dogdouspec skill export --output-dir PATH [--format xml|human]
  ```
- **Schema Status and Sync** (inspect or mechanically refresh optional readable XSD copies)
  ```powershell
  dogdouspec schema status [--version 1.0] [--workspace-root PATH] [--format xml|human]
  dogdouspec schema diff [--name NAME] [--version 1.0] [--workspace-root PATH] [--format unified|xml]
  dogdouspec schema sync --expected-version 1.0 [--workspace-root PATH] [--format xml|human]
  ```
  `schema sync` uses the workspace writer lock and crash recovery. It never migrates or edits managed XML documents.

### 3. Iteration Commands
- **Iteration Listing**
  ```powershell
  dogdouspec iteration list [--workspace-root PATH] [--format xml|human]
  ```
- **Iteration Creation** (supports `--activate` for immediate active state)
  ```powershell
  dogdouspec iteration create --id YYYYMMDD-name --kind feature|research [--file iteration-content.xml] [--constraint "kind: text" ...] [--activate] [--criterion "..." ...] [--workspace-root PATH] [--format xml|human]
  ```
  Export `template show --name iteration.create`, fill the product/research intent, and pass it through `--file` to create complete content in one validated operation. Use pending criteria and proposed requirements/design. Acceptance comes from the file or `--criterion`, never both. `--activate` requires explicit owner authorization and leaves product criteria pending.
- **Iteration Constraints** (apply to every Task in the iteration)
  ```powershell
  dogdouspec iteration constraint add --iteration ID --text "..." [--kind TOKEN] [--constraint-id ID] [--expected-spec-revision N] [--format xml|human]
  dogdouspec iteration constraint list [--iteration ID] [--format xml|human]
  dogdouspec iteration constraint remove --iteration ID --constraint-id ID [--expected-spec-revision N] [--format xml|human]
  ```
  Constraints are stored in `spec.xml`. Add/remove use its revision through `--expected-spec-revision`; the CLI resolves the current revision when omitted. Creation accepts repeatable `--constraint`. Its text, and add's text when `--kind` is omitted, recognize `kind: text` or `[kind] text` and remove that prefix. Constraint kinds are open tokens (`[a-z][a-z0-9-]*`), such as `environment`, `configuration`, or a project-specific kind. Explicit add `--kind` preserves the supplied text apart from outer whitespace. See the [CLI contract](docs/V1_CLI_CONTRACT.md#44-iteration-constraints) for the full behavior and authority boundaries.
- **Iteration Activation** (Porcelain command)
  ```powershell
  dogdouspec iteration activate [--iteration ID] [--auto-approve] [--summary "..."] [--format xml|human]
  ```
- **Iteration Completion** (Porcelain command)
  ```powershell
  dogdouspec iteration complete [--iteration ID] [--accept-all] [--summary "..."] [--format xml|human]
  ```
- **Iteration Readiness**
  ```powershell
  dogdouspec iteration readiness --iteration ID --phase activation|completion [--workspace-root PATH] [--format xml|human]
  ```
- **Iteration Confirmation** (Plumbing raw XML confirmation)
  ```powershell
  dogdouspec iteration confirm (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
  ```

### 4. Validation & Query
- **Validation**
  ```powershell
  dogdouspec validate [--workspace-root PATH] [--iteration ID] [--document RELATIVE_PATH] [--format xml|human]
  ```
- **XPath Query**
  ```powershell
  dogdouspec query --document REF (--xpath EXPR|--xpath-file PATH) [--var name=value ...] [--warn-empty] [--workspace-root PATH] [--format xml|human]
  ```
- **Scoped Search**
  ```powershell
  dogdouspec search --scope project|iteration [--iteration ID] (--xpath EXPR|--xpath-file PATH) [--var name=value ...] [--workspace-root PATH] [--format xml|human]
  ```
  `--xpath-file` avoids shell quoting and Git Bash/MSYS argument rewriting. For empty node-set diagnosis, `query --warn-empty` reports the root/direct children on stderr while preserving successful output and exit status. Backlog items are at `/backlog/items/item`.

### 5. Task & Requirement Mutations
- **High-Level Task Operations**:
  - `task start --task <TASK_ID> [--iteration <ID>] [--summary "..."]` (Transition to `in-progress`)
  - `task verify --task <TASK_ID> [--iteration <ID>] [--covers <CRITERION>] [--summary "..."]` (Transition to `verification`)
  - `task finish --task <TASK_ID> [--iteration <ID>] [--covers <CRITERION>] [--summary "..."]` (Sequential transitions to `done`, reporting every revision and any partial progress)
  - `task block --task <TASK_ID> --summary "..." [--blocker-kind <KIND>] [--blocker-owner <OWNER>] [--blocker-review-at <TIMESTAMP>] [--condition "..."] [--next-action "..."]` (Transition to `blocked` with structured finding)
  - `task resume --task <TASK_ID> (--finding <FINDING_ID> | --all) --summary "..."` (Resolve blocker findings and resume to `in-progress`)
  - `task quick --title ... --scope ... --done-when ... --why ... [--start]` (Quick-create task)
  - `task next [--iteration <ID>]` (Discover next actionable task with progression assessment facts)
  - `task blockers [--iteration <ID>] [--task <ID>] [--owner <OWNER>] [--due-only]` (Query active blockers and recheck queue)
  - `task context --task <TASK_ID> [--iteration <ID>] [--max-bytes <N>] [--no-history]` (Read bounded context with effective iteration and Task constraints)
  - `task records --task <TASK_ID> [--last 5] [--kind verification] [--fields id,created_at,summary,checks] [--max-summary N] [--max-bytes N]` (Bounded persisted history with explicit omission/truncation metadata)
  - `task record --task <TASK_ID> --kind verification --summary "..." --check-command "dotnet test" --check-result passed --check-exit-code 0` (Append structured evidence to existing checks without changing acceptance)
  - `task review record --task <TASK_ID> --summary "..." [--actor <ACTOR>] [--result approved|changes-requested]` (Save an actual review as an informational note across task states)
  - `task review status --task <TASK_ID>` and `task review records --task <TASK_ID> --last 5` (Review facts and bounded history, with XML/human output)

  Review notes preserve the caller's explanation and optional declared result;
  callers choose whether to change, block or finish work. Task context includes
  compact review facts and a status command. `review status` distinguishes notes
  from existing formal submissions and provides useful commands. Existing
  `review approve/request-changes` remain formal submission actions for tasks
  explicitly configured with review requirements; unsupported submissions explain
  how to save a note. Notes use existing XML records, including after completion.

  Before starting or resuming a selected Task, read `task context --no-history`. Its essential constraints retain both `scope="iteration"` and `scope="task"`, including optional `kind`; both scopes apply. Raw Task queries and `task show` do not include iteration constraints. Re-read context after a constraint change, and resolve conflicts before execution.

- **Plumbing & Governance Operations**:
  - `task update` (Raw XML state machine update), `task review` (Review gate submission), `task add`, `task revise`, `task split`
  - `requirement propose`, `change propose`, `change apply`
  - `backlog add`, `backlog list`, `backlog record`, `backlog records`, `backlog schedule`, `backlog complete`, `backlog cancel`
  - `append`, `transaction apply`

  `task revise --add-scope` preserves repositories/exclusions and deduplicates includes; `--add-dependency` resolves document or cross-iteration project scope. `transaction apply --dry-run` validates without publishing and labels its preview. `task quick --dry-run --format xml` returns a labeled canonical task-add request for inspection/replay. Finish can leave earlier transitions committed if a later step fails; its partial receipt and nonzero exit identify that state, and review gates still apply.

  Backlog add can generate omitted item ID, operation ID and time, with actor from `DOGDOUSPEC_ACTOR` or `agent`. Its receipt returns exact replay parameters in `backlog-identity`; reuse those for retries. An explicit operation ID without `--occurred-at` must have a valid UTC timestamp prefix. Kinds are open tokens. Keep product proposals as research backlog with an owner review condition until a design is chosen.

  Record partial progress without changing status, or explain a lifecycle transition:
  ```powershell
  dogdouspec backlog record --id <ITEM_ID> --summary "Research is complete; compatibility verification remains."
  dogdouspec backlog records --id <ITEM_ID> --last 5 --format xml
  dogdouspec backlog complete --id <ITEM_ID> --summary "Implemented and verified." --resolving-task <TASK_ID>
  dogdouspec backlog cancel --id <ITEM_ID> --summary "An existing feature already covers this need."
  ```
  `record` also accepts follow-up notes on completed/cancelled items. Summaries,
  task links and `--check-command/--check-result/--check-exit-code/--check-kind/--check-summary`
  are optional for transitions; a check without an exit code may omit the command.
  Reported checks do not decide closure. Existing invocations and replay identities
  remain valid. `records` reads recent entries in append order without writing.

### 6. Diagnostics & Reporting
- **Iteration Summary** (instant progress card and task breakdown with progression assessment)
  ```powershell
  dogdouspec summary [--iteration ID] [--workspace-root PATH] [--format markdown|json|xml|human]
  ```
- **VCS Working Tree Status** (read-only inspection of managed document cleanliness and checkpoint readiness)
  ```powershell
  dogdouspec workspace vcs-status [--workspace-root PATH] [--format xml|human]
  ```

---

## 4. Exit Codes

| Code | Meaning |
|---:|---|
| 0 | Success, including a verified idempotent retry |
| 1 | Read-only scope verification completed with one or more out-of-scope paths |
| 2 | Command, XML request, XPath, or argument error |
| 3 | Schema or semantic validation failure |
| 4 | Revision, lock, cardinality, or idempotency conflict |
| 5 | Protected product decision or authority gate |
| 6 | Filesystem commit or recovery failure |
| 7 | Input, query, projection, or output limit exceeded |

---

## 5. Documentation & References

- **Air-Gapped & Vendored Deployment Guide**: See [docs/INSTALL_IN_OTHER_REPOSITORY.md](docs/INSTALL_IN_OTHER_REPOSITORY.md) for self-contained, source-based repo-local deployment in isolated environments.
- **Cross-Platform Build & Packaging**: See [docs/CROSS_PLATFORM.md](docs/CROSS_PLATFORM.md) for Linux and macOS compilation and packaging guidance.

- **Agent Guidelines & Skills**: See [`AGENTS.md`](AGENTS.md) and [`.agents/skills/dogdouspec/SKILL.md`](.agents/skills/dogdouspec/SKILL.md) for workflow integration rules and compact two-phase query patterns.
- **Usability, Results, and Checkpoint Proposal**: See [`docs/DOGFOOD_USABILITY_AND_EVIDENCE_PROPOSAL.md`](docs/DOGFOOD_USABILITY_AND_EVIDENCE_PROPOSAL.md) for the merged non-normative roadmap. Proposed commands described there are not current CLI commands.

---

## 6. Architectural Boundaries & Workflow

- **Repository-Local State**: Authoritative specification and task state is stored entirely within `.dogdouspec/` XML documents and validated against embedded XSD v1 schemas.
- **Iteration-Owned Results**: Implementation summaries, commits, checks, findings, reviews, risks, blockers, and handoff instructions are persisted in `tasks.xml` records. Raw agent reports, prompts, and provider logs are transient by default; only inherently bulky raw evidence remains external when repository policy requires it.
- **VCS Checkpoints**: An atomic DogdouSpec document commit is locally durable but is not a Git checkpoint. Git-backed Mode B work should version managed `.dogdouspec/` state at material boundaries, ignore only `_tmp/`, and never infer repository-write authority.
- **Authority Boundaries**: Technical agents manage task lifecycles, execution records, and code changes autonomously. Product requirements, design decisions, and iteration completions require explicit human owner confirmation via `iteration confirm`.
