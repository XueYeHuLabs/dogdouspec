# Coding Agent Guidelines for DogdouSpec

Welcome! This repository uses **DogdouSpec** to manage iterations, specifications, and tasks through authoritative XML documents in `.dogdouspec/`.

## Mandatory Agent Workflow

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
     .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='pending' and not(dependencies/ref[@relation='depends-on']/@target = /tasks/task[@status!='done' and @status!='transferred' and @status!='superseded' and @status!='cancelled']/@id)][1], '@id', '@status', '@agent', 'index')" --format xml
     ```
   - Phase 2 (Load full selected task):
     ```powershell
     .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']" --format xml
     ```
4. **Follow the Checked-In Skill**:
   - Read [`skills/dogdouspec/SKILL.md`](skills/dogdouspec/SKILL.md) and its references for complete guidelines on XPath projections, mutation semantics, and authority rules.
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

## Git Commit Guidelines

Before committing code, agents MUST run `git diff --check` to ensure that the commit not contains whitespace errors. All whitespace errors MUST be fixed before committing.

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
