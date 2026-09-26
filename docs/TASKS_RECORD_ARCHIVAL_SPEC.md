# DogdouSpec tasks.xml Record Archival and Compaction Specification

Status: Approved Design Specification
Version: 1.0
Date: 2026-09-25
Related Issues: Issue 6 (tasks.xml document growth in long iterations)

---

## 1. Executive Summary

In long-running or large-scale iterations, `tasks.xml` grows monotonically as execution records (progress reports, discussion logs, blocker findings, resolution receipts, and verification results) accumulate. In real-world multi-day dogfooding iterations, 500+ records can drive `tasks.xml` to exceed 700 KB, degrading parser performance, increasing agent token context consumption, and imposing high overhead on whole-document validation.

This specification defines the **DogdouSpec Task Record Archival and Compaction Mechanism**, an architectural solution that separates verbose execution history of terminal tasks (`done`, `cancelled`, `superseded`) into partition archive documents while preserving:
1. **Append-only provenance and cryptographic auditability**.
2. **Deterministic revision monotonicity**.
3. **Transparent two-phase XPath queries (`ds:filter`)**.
4. **Sub-second workspace validation and low agent token footprints**.

---

## 2. Design Principles & Invariants

1. **Active Working Set Boundedness**:
   `tasks.xml` must only contain active tasks (`pending`, `in-progress`, `verification`, `blocked`) with their full record histories, plus compact index representations of terminal tasks.
2. **Zero Audit Loss (Append-Only Provenance)**:
   No record is ever deleted or mutated. Archival is an atomic relocation of immutable record graphs from `tasks.xml` to partition archive documents (`tasks.archive.xml` or `tasks.archive.<shard>.xml`).
3. **Cryptographic Tamper-Evidence**:
   Compacted tasks retain SHA-256 digests and record counts of their archived record sets.
4. **Transparent Two-Phase XPath Queries**:
   Existing XPath projections (e.g. `ds:filter(/tasks/task[@status='in-progress'], ...)` and `dogdouspec task next`) evaluate exclusively against the lightweight `tasks.xml` working set without disk I/O to archive files. When a query explicitly requests the deep record history of a terminal task, the query engine resolves archive references on demand.
5. **Atomic Transactional Migration**:
   Archival is executed as an atomic two-document transaction (or multi-document transaction across shards) using the existing DogdouSpec ACID transaction and revision lock mechanism.

---

## 3. Architecture & Document Layout

### 3.1 Partition Layout

Within an iteration directory `.dogdouspec/<iteration-id>/`:

```
.dogdouspec/20260925-cli-ergonomics/
├── spec.xml
├── tasks.xml                 # Active working set & compacted terminal task stubs
├── tasks.archive.xml         # Consolidated archive document for terminal task records
└── tasks.archive/            # (Optional shard directory when exceeding MaxArchiveBytes)
    ├── 20260925-part01.xml
    └── 20260925-part02.xml
```

### 3.2 Live tasks.xml Working Set (Post-Archival)

When a task transitions to `done`, `cancelled`, or `superseded` and is archived:
- Its `<records>` node is compacted into a stub.
- The terminal record (e.g. `completion`, `superseded`, or `resolution`) is retained inline for immediate status inspection without loading archives.
- The stub records the archive location, count, and SHA-256 digest:

```xml
<task id="20260925-task-01-task-record" status="done" created_at="2026-09-25T02:40:02Z" updated_at="2026-09-25T03:00:00Z">
  <title>Add TaskRecord helper and record subcommand</title>
  <objective>Provide porcelain subcommand to append structured records.</objective>
  <acceptance>
    <criterion id="20260925-taskcrit-01-1" status="passed">dogdouspec task record appends valid record</criterion>
  </acceptance>
  <records total_count="8" archived="true" archive_path="20260925-cli-ergonomics/tasks.archive.xml" archive_sha256="a1b2c3d4...">
    <summary>7 historical records archived. Terminal completion record retained inline.</summary>
    <record id="20260925T030000Z-rec-complete" kind="completion" status="passed" created_at="2026-09-25T03:00:00Z" actor="agent">
      <summary>Completed Task 01: Implemented TaskRecord helper and dogdouspec task record subcommand.</summary>
      <covers>
        <ref scope="document" target="20260925-taskcrit-01-1" relation="covers" />
      </covers>
    </record>
  </records>
</task>
```

### 3.3 Archive Document Structure (tasks.archive.xml)

Archive files are governed by `archive.xsd` and reuse the strict record definitions from `tasks.xsd`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<task-archive iteration="20260925-cli-ergonomics" shard="0" archived_at="2026-09-25T03:05:00Z" schema_version="1.0">
  <archived-task id="20260925-task-01-task-record" record_count="7" sha256="a1b2c3d4...">
    <records>
      <record id="20260925T024002Z-rec-task-01-init" kind="discussion" ...>
        ...
      </record>
      ...
    </records>
  </archived-task>
</task-archive>
```

---

## 4. Two-Phase XPath Query Compatibility

### Phase 1: Filter and Discovery Queries
The vast majority of agent queries (~95%) discover actionable work, check status, or list ready tasks:
```powershell
dogdouspec query --document "<ITERATION>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress'], '@id', 'title')"
dogdouspec task next --iteration "<ITERATION>"
dogdouspec task blockers --iteration "<ITERATION>"
```
These execute exclusively against `tasks.xml`. Because compacted terminal tasks only occupy ~1 KB each instead of 50-100 KB, the entire `tasks.xml` remains within 20-50 KB throughout multi-week iterations.

### Phase 2: Full Task and Deep Record Queries
When an agent or audit tool requests the full task details:
1. If the task is active: `tasks.xml` already contains the complete record set. No archive lookup occurs.
2. If the task is terminal and `archived="true"`:
   - Command `dogdouspec task context --task <TASK_ID>` returns the compacted task with retained terminal records.
   - If `--include-records` or `--full-history` is requested, the query engine transparently reads `archive_path`, extracts the `<archived-task>` records, and splices them into the returned node-set.

---

## 5. Compaction Lifecycle & CLI Workflow

### Command: `dogdouspec task compact` (or `task archive`)

```powershell
dogdouspec task compact --iteration <ITERATION_ID> [--dry-run] [--format human|xml]
```

#### Workflow Steps:
1. **Selection**: Find all terminal tasks (`done`, `cancelled`, `superseded`) whose records have not yet been archived (`@archived != 'true'`).
2. **Record Extraction**: For each selected task, collect all non-terminal records. Compute canonical SHA-256 hash over the extracted XML snippet.
3. **Archive Generation / Append**:
   - If `tasks.archive.xml` exists, load and append new `<archived-task>` blocks.
   - If not, initialize a new `tasks.archive.xml`.
4. **Working-Set Compaction**:
   - Replace `<records>` in `tasks.xml` with the compacted stub, retaining the single terminal completion record.
   - Set attributes `@archived="true"`, `@archive_path="..."`, `@archive_sha256="..."`, `@total_count="..."`.
5. **Atomic Commit**:
   - Atomically commit both `tasks.archive.xml` and `tasks.xml` via `TransactionApplier`.
   - Increment `tasks.xml` revision monotonically.

---

## 6. Verification and Integrity Guarantees

1. **Audit Check**: `dogdouspec validate` verifies:
   - Existence and containment of referenced `archive_path`.
   - SHA-256 hash match between `archive_sha256` attribute in `tasks.xml` and the computed hash of the archived node block in `tasks.archive.xml`.
   - Sum of inline records + archived records equals `total_count`.
2. **Rollback Safety**:
   - If compaction encounters an error or revision mismatch, the atomic transaction rolls back, leaving `tasks.xml` in its original unmodified state.
