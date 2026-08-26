# Coding Agent Guidelines for DogdouSpec

Welcome! This repository provides **DogdouSpec**, an iteration-first specification and task governance system designed to keep multi-step, complex engineering tasks structured, token-efficient, and resilient across AI coding sessions.

## 1. When to Use DogdouSpec (Applicable Scenarios & Benefits)

DogdouSpec is a powerful tool for complex project management, not a rigid constraint for every interaction. Agents should choose the appropriate mode based on task complexity:

### ⚡ Mode A: Direct Execution (Routine & Lightweight Tasks — Default)
- **Applicable Scenarios**:
  - Standalone bug fixes, typos, single-file refactorings, or script tweaks.
  - Ad-hoc investigations, test additions, or general Q&A.
  - Direct, bounded requests that can be completed and understood in a single interaction.
- **Benefits**:
  - Zero overhead, maximum speed, minimal tool calls.
- **Workflow**:
  - Do **NOT** query, create, or modify `.dogdouspec/` iterations or tasks.
  - Directly make code changes and verify with `.\build.cmd`.
  - Record the full rationale directly in the [Git Commit Guidelines](#3-git-commit-guidelines) (Title + numbered details).

### 🛡️ Mode B: Governed Iteration Workflow (Complex & Long-Cycle Tasks — Recommended)
- **Applicable Scenarios**:
  - Multi-step features or roadmap items spanning multiple tasks or sessions.
  - Architectural overhauls or cross-component refactoring requiring dependency tracking.
  - Ambiguous requirements requiring formal specification, owner review gates, or research spikes.
  - Multi-agent collaboration or handoffs where durable state persistence is needed.
- **Benefits**:
  - **Context Persistence**: Prevents LLM context drift across long sessions through schema-validated XML artifacts.
  - **Token Efficiency**: Two-phase XPath queries (`ds:filter`) load only the active task rather than entire task graphs.
  - **Authority Governance**: Strict gates prevent technical agents from auto-approving product scope or design decisions.
  - **Zero External Dependencies**: Self-contained repo-local CLI execution without global packages, background daemons, or MCP servers.
- **Workflow Trigger**:
  - When a complex requirement is presented, the Agent should **recommend** using DogdouSpec to the user.
  - When the user explicitly requests DogdouSpec, or when executing tasks inside an existing active iteration, follow the **DogdouSpec Workflow** below.

## 2. DogdouSpec Workflow (When Active / Selected)

This repository uses **DogdouSpec** to manage iterations, specifications, and tasks through authoritative XML documents in `.dogdouspec/`.

1. **Use Repo-Local CLI**:
   - Windows: `.\dogdouspec.cmd <command>`
   - Cross-platform: `dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- <command>`
   - Do not install global tools or configure MCP servers.
2. **Never Directly Edit `.dogdouspec/*.xml`**:
   - Do not use file editors, `Set-Content`, or scripts to modify files inside `.dogdouspec/`.
   - All managed mutations must be executed through the public CLI (`task update`, `append`, `transaction apply`, `iteration confirm`).
3. **Discover & Select Actionable Work (Two-Phase Query)**:
   - Validate workspace: `.\dogdouspec.cmd validate --format xml`
   - Find active iteration: `.\dogdouspec.cmd iteration list --format xml`
   - Phase 1a (Resume in-progress or verification task):
     ```powershell
     .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress' or @status='verification'][1], '@id', '@status', '@agent', 'index')" --format xml
     ```
   - Phase 1b (If no task is in-progress, select first ready pending task):
     ```powershell
     # Recommended (resolves cross-document and cross-iteration dependencies):
     .\dogdouspec.cmd task next --iteration "<ITERATION_ID>" --format xml

     # Alternative raw XPath (document-local dependencies only):
     .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='pending' and not(dependencies/ref[@relation='depends-on']/@target = /tasks/task[@status!='done' and @status!='transferred' and @status!='superseded' and @status!='cancelled']/@id)][1], '@id', '@status', '@agent', 'index')" --format xml
     ```
   - Phase 2 (Load full selected task):
     ```powershell
     .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']" --format xml
     ```
4. **Follow the Checked-In Skill**:
   - Read [`.agents/skills/dogdouspec/SKILL.md`](.agents/skills/dogdouspec/SKILL.md) and its references for complete guidelines on XPath projections, mutation semantics, and authority rules.
5. **Code Changes & Verification**:
   - Run `.\build.cmd` before and after changes. Ensure all test suites pass with 0 errors and 0 warnings.
6. **Task Updates & State Transitions**:
   - Transition task: `pending` -> `start` (`in-progress`) -> `verify` (`verification`) -> `complete` (`done`).
   - Pass exact expected revisions (`--expected-revision <N>`).
   - After each write, run `.\dogdouspec.cmd validate --format xml` and re-query.
7. **Respect Product Authority Gates**:
   - Technical agents cannot auto-complete requirements, design decisions, or iterations.
   - Run `.\dogdouspec.cmd iteration readiness` to check gating status.
   - Only execute `iteration confirm` when explicitly instructed by the human owner in the current interaction.
8. **Preserve User Work**:
   - Do not commit or push to git unless explicitly requested by the user.

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
