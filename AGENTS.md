# Coding Agent Guidelines for DogdouSpec

Welcome! This repository provides **DogdouSpec**, an iteration-first specification and task governance system designed to keep multi-step, complex engineering tasks structured, token-efficient, and resilient across AI coding sessions.

## 1. Design Philosophy & When to Use DogdouSpec

### The Core Problem DogdouSpec Solves
Traditional monolithic markdown documents (e.g. `TODO.md`, `PLAN.md`, `TASKS.md`) impose a heavy cognitive and token burden on AI coding agents. In long sessions, repeatedly reading, updating, and parsing whole markdown graphs leads to **context drift**, **lost constraints**, **hallucinated checkboxes**, and **severe token waste**.

DogdouSpec replaces unstructured markdown tracking with **schema-validated XML artifacts, deterministic revision locks, and two-phase XPath queries (`ds:filter`)**, loading only the active task's minimal context (saving ~90% tokens per interaction) and preserving durable state across multi-session or multi-agent handoffs.

### Two Explicit Modes (Avoid False Friction & Redundancy)

Agents must choose the appropriate mode based on task complexity:

#### ⚡ Mode A: Direct Single-Commit Execution (Routine & Lightweight Tasks — Default)
- **Applicable Scenarios**:
  - Standalone bug fixes, typos, single-file refactorings, script tweaks, or test additions.
  - Direct, bounded requests that can be completed and understood in a single Git commit.
- **Workflow**:
  - **Zero DogdouSpec Overhead**: Do **NOT** query, create, or modify `.dogdouspec/` iterations or tasks.
  - Make code changes directly and verify with `.\build.cmd`.
  - Record the full rationale directly in the [Git Commit Guidelines](#3-git-commit-guidelines) (Title + numbered details).
- **Rule**: Single-commit tasks already explain their full intent in the commit message. Avoid using DogdouSpec for trivial cases to prevent creating a false impression of friction or redundancy.

#### 🛡️ Mode B: Governed Iteration Workflow (Complex & Long-Cycle Tasks — Recommended)
- **Applicable Scenarios**:
  - Multi-step features or roadmap items spanning multiple tasks, sessions, or agents.
  - Architectural overhauls or cross-component refactoring requiring dependency tracking.
  - Ambiguous requirements requiring formal specification, owner review gates, or research spikes.
  - Long-term state persistence where markdown files would cause context window degradation.
- **Workflow Trigger**:
  - When a complex requirement is presented, the Agent should **recommend** using DogdouSpec to the user.
  - When the user explicitly requests DogdouSpec, or when executing tasks inside an existing active iteration, follow the **DogdouSpec Workflow** below.

## 2. DogdouSpec Workflow (When Active / Selected)

This repository uses **DogdouSpec** to manage iterations, specifications, and tasks through authoritative XML documents in `.dogdouspec/`.

1. **Environment Pre-flight & CLI Execution**:
   - Verify `dogdouspec --version`.
   - If missing, fail closed and instruct the user: `winget install Vixasol.DogdouSpec`.
   - Execute commands directly via `dogdouspec <command>` (or `dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- <command>` during source development).
2. **Never Directly Edit `.dogdouspec/*.xml`**:
   - Do not use file editors, `Set-Content`, or scripts to modify files inside `.dogdouspec/`.
   - All managed mutations must be executed through the public CLI (`task update`, `task quick`, `task add`, `append`, `transaction apply`, `iteration confirm`).
3. **Discover & Select Actionable Work (Two-Phase Query)**:
   - Validate workspace: `dogdouspec validate --format xml`
   - Find active iteration: `dogdouspec iteration list --format xml`
   - Phase 1a (Resume in-progress or verification task):
     ```powershell
     dogdouspec query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress' or @status='verification'][1], '@id', '@status', '@agent', 'index')" --format xml
     ```
   - Phase 1b (If no task is in-progress, select first ready pending task):
     ```powershell
     dogdouspec task next --iteration "<ITERATION_ID>" --format xml
     ```
   - Phase 2 (Load full selected task):
     ```powershell
     dogdouspec query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']" --format xml
     ```
4. **Follow the Checked-In Skill**:
   - Read [`.agents/skills/dogdouspec/SKILL.md`](.agents/skills/dogdouspec/SKILL.md) and its references for complete guidelines on XPath projections, mutation semantics, and authority rules.
5. **Code Changes & Verification**:
   - Run `.\build.cmd` before and after changes. Ensure all test suites pass with 0 errors and 0 warnings.
6. **Task Updates & State Transitions**:
   - Transition task: `pending` -> `start` (`in-progress`) -> `verify` (`verification`) -> `complete` (`done`).
   - Pass exact expected revisions (`--expected-revision <N>`).
   - After each write, run `dogdouspec validate --format xml` and re-query.
   - Persist semantic agent results—implementation summary, source commits, checks, findings, risks, review outcome, blockers, and handoff instructions—in the relevant `tasks.xml` Task records. Do not rely on `.agents/work-results/` or another report folder for recovery.
7. **Respect Product Authority Gates**:
   - Technical agents cannot auto-complete requirements, design decisions, or iterations.
   - Run `dogdouspec iteration readiness` to check gating status.
   - Only execute `iteration confirm` when explicitly instructed by the human owner in the current interaction.
8. **Preserve User Work**:
   - Do not commit or push to git unless explicitly requested by the user.
9. **Checkpoint Governed State**:
   - In Git-backed Mode B work, inspect `git status --short -- .dogdouspec` at material lifecycle, review, handoff, external-blocker, and release boundaries.
   - Version managed `.dogdouspec/` documents and ignore only `.dogdouspec/_tmp/`. If Git-write authority is absent, report the workspace as locally durable but not transport-ready and list the exact uncheckpointed files.
   - Raw worker reports, prompts, mutation envelopes, and provider logs are transient by default. Only bulky raw evidence may remain external; summarize its outcome in the owning Task record.

## 3. Git Commit Guidelines

Before committing code, agents MUST run `git diff --check` to ensure that the commit contains no whitespace errors. All whitespace errors MUST be fixed before committing.

All git commit messages MUST be written entirely in English and strictly follow the `Title[ + blank line + details]` format:

```
Title

1. somethings 1.
2. somethings 2.
```

* **Title (Mandatory):**
  * Imperative mood describing the theme (e.g. `Add ...`, `Update ...`, `Remove ...`, `Standardize ...`).
  * First letter capitalized.
  * No conventional prefixes (do NOT use `feat:`, `fix:`, `chore:`, `feat/`, `fix(scope):`, etc.).
* **Details (Optional):**
  * Separated from the title by a blank line.
  * Numbered list starting with `1. `, `2. `, etc.
  * Each line MUST be in all lowercase (except technical proper nouns, contains the first letter of a sentence).
  * Each line MUST end with a period (`.`).
  * Describes the detailed changes and rationale.
  * Use LF line endings only, no CRLF.
