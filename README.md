# DogdouSpec

DogdouSpec is an iteration-first structured XML/XPath specification and technical execution engine designed for human and AI pairing.

## 1. Quick Start & Adoption Guide

### Standard Global Installation (Recommended for Most Projects)

For standard development environments with a global package manager:

1. **Install DogdouSpec globally on Windows**:
   ```powershell
   winget install Vixasol.DogdouSpec
   ```
2. **Initialize workspace & skill in your repository**:
   Navigate to your project root and run:
   ```powershell
   # 1. Initialize XML workspace (.dogdouspec/ schemas, backlog, knowledge)
   dogdouspec workspace init

   # 2. Synchronize agent skill (.agents/skills/dogdouspec/)
   dogdouspec skill sync
   ```
3. **Configure Agent Guidelines (`AGENTS.md`)**:
   - `AGENTS.md` establishes governance boundaries, mode selection (Mode A direct commit vs Mode B governed iteration), and authority rules for AI coding agents.
   - Maintainers or coding agents should configure `AGENTS.md` tailored to the target project's specific tech stack, build scripts (e.g. `npm test`, `cargo build`, `dotnet test`), and verification commands, referencing [`templates/v1/AGENTS.md`](templates/v1/AGENTS.md) as a structural baseline.

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

### 2. Skill Management
- **Skill Sync**
  ```powershell
  dogdouspec skill sync [--output-dir PATH] [--format xml|human]
  ```
- **Skill Export**
  ```powershell
  dogdouspec skill export --output-dir PATH [--format xml|human]
  ```

### 3. Iteration Commands
- **Iteration Listing**
  ```powershell
  dogdouspec iteration list [--workspace-root PATH] [--format xml|human]
  ```
- **Iteration Creation** (supports `--activate` for immediate active state)
  ```powershell
  dogdouspec iteration create --id YYYYMMDD-name --kind feature|research [--activate] [--workspace-root PATH] [--format xml|human]
  ```
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
  dogdouspec query --document REF --xpath EXPR [--var name=value ...] [--workspace-root PATH] [--format xml|human]
  ```
- **Scoped Search**
  ```powershell
  dogdouspec search --scope project|iteration [--iteration ID] --xpath EXPR [--var name=value ...] [--workspace-root PATH] [--format xml|human]
  ```

### 5. Task & Requirement Mutations
- **High-Level Task Operations**:
  - `task start --task <TASK_ID> [--iteration <ID>] [--summary "..."]` (Transition to `in-progress`)
  - `task verify --task <TASK_ID> [--iteration <ID>] [--covers <CRITERION>] [--summary "..."]` (Transition to `verification`)
  - `task finish --task <TASK_ID> [--iteration <ID>] [--covers <CRITERION>] [--summary "..."]` (Atomic completion to `done`)
  - `task quick --title ... --scope ... --done-when ... --why ... [--start]` (Quick-create task)
  - `task next [--iteration <ID>]` (Discover next ready actionable task)
- **Plumbing & Governance Operations**:
  - `task update` (Raw XML state machine update), `task review` (Review gate submission), `task add`, `task revise`, `task split`
  - `requirement propose`, `change propose`, `change apply`
  - `backlog add`, `backlog list`, `backlog schedule`, `backlog complete`, `backlog cancel`
  - `append`, `transaction apply`

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
- **Dogfood Remediation Evidence**: See [docs/DOGDOUCLIX_DOGFOOD_REMEDIATION.md](docs/DOGDOUCLIX_DOGFOOD_REMEDIATION.md) for background analysis and remediation evidence.
- **Agent Guidelines & Skills**: See [`AGENTS.md`](AGENTS.md) and [`.agents/skills/dogdouspec/SKILL.md`](.agents/skills/dogdouspec/SKILL.md) for workflow integration rules and compact two-phase query patterns.

---

## 6. Architectural Boundaries & Workflow

- **Repository-Local State**: Authoritative specification and task state is stored entirely within `.dogdouspec/` XML documents and validated against embedded XSD v1 schemas.
- **Authority Boundaries**: Technical agents manage task lifecycles, execution records, and code changes autonomously. Product requirements, design decisions, and iteration completions require explicit human owner confirmation via `iteration confirm`.