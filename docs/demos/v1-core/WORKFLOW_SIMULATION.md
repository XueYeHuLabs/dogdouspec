# DogdouSpec v1 Iteration Lifecycle Simulation

Status: Review Simulation

This document simulates one complete Iteration lifecycle using the iteration-first demo. The CLI names and transaction XML are proposed v1 contracts intended for usability review; no executable currently implements them.

The simulation separates:

- DogdouSpec state discovery and mutation.
- Human or Agent discussion and implementation work.
- Repository-native build and test commands.
- Durable semantic records written back to `tasks.xml`.

## 1. Proposed minimal CLI surface

The simulation uses only:

```text
dogdouspec workspace discover
dogdouspec workspace init
dogdouspec iteration list
dogdouspec iteration create
dogdouspec query
dogdouspec search
dogdouspec validate
dogdouspec template show
dogdouspec append --stdin
dogdouspec task update --stdin
dogdouspec iteration readiness
dogdouspec iteration confirm --stdin
dogdouspec transaction apply --stdin
```

`iteration create` is a schema-aware convenience operation because it creates a
fixed directory and document skeleton. Routine history uses a filled template
and `append`; stateful Task changes use one `task update` helper. Low-level
transactions remain available for draft multi-document planning and unsupported
combinations, but are not the normal Skill path.

`iteration readiness` is read-only. It reports technical facts and unresolved
product decisions without changing either document. `iteration confirm` is the
only write path for protected product decisions such as Iteration activation,
material design acceptance, Iteration acceptance dispositions, and Iteration
completion. A generic transaction that targets those fields fails with
`OWNER_DECISION_REQUIRED`.

### 1.1 Two independent state lines

| State line | Authoritative document | Examples | Automatic Agent transition |
|---|---|---|---|
| Technical execution | `tasks.xml` | Task `pending`, `in-progress`, `blocked`, `verification`, `done`; Task criterion `pending`, `passed`, `failed`, `not-applicable` | Yes, subject to assertions, verification, and Skill stop rules |
| Product decision | `spec.xml` | Requirement `proposed`, `approved`, `superseded`, `withdrawn`; design `proposed`, `accepted`, `rejected`; product criterion `pending`, `accepted`, `rejected`, `waived`; Iteration `draft`, `active`, `replanning`, `completed` | No; only an explicit owner confirmation may change decision state |

Task coverage is an input to product review. It never aliases or synchronizes a
product criterion automatically. Likewise, “all Tasks terminal” means only that
the Iteration is ready to be reviewed; it does not mean the Iteration is
complete.

A Requirement is never `completed`. Its lifecycle describes whether the
product definition is proposed, approved, superseded, or withdrawn. Whether the
delivered result satisfies that approved definition is captured separately by
the owner-confirmed product acceptance decisions.

For v1, the protected SPEC mutation surface includes:

- `/iteration/@status`.
- `/iteration/product/requirements/requirement/@status` after draft planning.
- `/iteration/product/acceptance/criterion/@decision`.
- Accepted or rejected material design decisions.
- Appending an authoritative product confirmation.

The transaction engine checks the selected nodes after XPath resolution; it is
not sufficient to disguise a protected write behind a different XPath. Draft
planning may write `proposed` content. Crossing from proposal to an
authoritative product decision requires `iteration confirm`.

## 2. Simulated revision timeline

The current demo is the checkpoint after the XPath projection Task started and
the owner confirmed structured reasoning records and template-first mutation.
Earlier and later revisions are reconstructed to show the complete lifecycle.

| Stage | `spec.xml` | `tasks.xml` |
|---|---:|---:|
| Iteration created | 1 | 1 |
| Product, design, and initial Tasks approved | 2 | 2 |
| Iteration activated | 3 | 2 |
| Layout Task completed through several checkpoints | 3 | 7 |
| XPath projection Task started | 3 | 8 |
| Record, template, and schema contract reconciled; current demo checkpoint | 4 | 9 |
| Implementation attempt recorded | 4 | 10 |
| Unexpected problem blocks Task | 4 | 11 |
| Material projection resolution confirmed; Task reconciled | 5 | 12 |
| Task enters verification | 5 | 13 |
| Task verification and completion committed | 5 | 14 |
| Remaining Tasks completed | 5 | 21 |
| Owner confirms product acceptance and Iteration completion | 6 | 21 |

Revision increments represent committed document changes, not individual XML operations. Multiple operations against one document in one transaction increment that document once.

## 3. Step 0 - Discover or initialize the workspace

From a repository subdirectory:

```powershell
dogdouspec workspace discover --format xml
```

Expected result:

```xml
<workspace root="L:/dogdou/dogdouspec/docs/demos/v1-core/.dogdouspec"/>
```

If no ancestor `.dogdouspec` exists:

```powershell
dogdouspec workspace init
```

Initialization creates only the fixed project-level structure:

```text
.dogdouspec/
|-- _schema/
|-- _skill/
|-- knowledge.xml
`-- backlog.xml
```

It does not create `project.xml` or an Iteration.

## 4. Step 1 - Create a new Iteration

The user asks to implement the XPath query and projection core.

```powershell
dogdouspec iteration create `
  --date 2026-08-23 `
  --name xpath-core `
  --kind feature `
  --title "XPath core"
```

The command atomically creates:

```text
.dogdouspec/20260823-xpath-core/
|-- spec.xml
`-- tasks.xml
```

Initial state:

```xml
<iteration
  id="20260823-xpath-core"
  schema_version="1.0"
  revision="1"
  kind="feature"
  status="draft"
  created_at="2026-08-23T02:00:00Z">
  <index>
    <summary>XPath core</summary>
    <term key="kind" value="feature"/>
  </index>
  <product/>
  <design/>
</iteration>
```

```xml
<tasks
  id="20260823-xpath-core-tasks"
  iteration="20260823-xpath-core"
  schema_version="1.0"
  revision="1">
  <index>
    <summary>Implementation Tasks for XPath core.</summary>
    <term key="iteration" value="20260823-xpath-core"/>
  </index>
</tasks>
```

Creation fails without mutation when the target directory already exists. The CLI does not silently choose a suffix.

## 5. Step 2 - Discuss and update product and design content

### 5.1 Read only the discussion surface

```powershell
dogdouspec query `
  --document 20260823-xpath-core/spec.xml `
  --format xml `
  --xpath "ds:filter(/iteration, '@id', '@status', 'index', 'product', 'design')"
```

The Agent and owner discuss objectives, deliverables, scope, acceptance, boundaries, alternatives, and implementation shape outside DogdouSpec. Raw conversation is not persisted by default.

The durable proposal produced from discussion is:

- Proposed product and design content.
- Proposed decisions and relevant rejected alternatives with rationale.
- Initial Tasks containing enough local implementation context.

### 5.2 Persist the normalized proposal

While the Iteration is still `draft`, the Skill sends one multi-document
transaction. The payload below abbreviates the discussed proposal but does not
claim that the product owner has approved it.

```text
dogdouspec transaction apply --stdin
```

```xml
<transaction operation_id="20260823T024500Z-operation-plan-iteration">
  <document path="20260823-xpath-core/spec.xml" expected_revision="1">
    <assert test="count(/iteration[@status='draft']) = 1"/>
    <replace-node select="/iteration/product" expect="1">
      <product>
        <objective>Deliver the local XPath query and projection core.</objective>
        <deliverables>
          <deliverable id="20260823-delivery-xpath-query">
            <description>Composable XPath variables and projection.</description>
          </deliverable>
        </deliverables>
        <requirements>
          <requirement
            id="20260823-req-structural-projection"
            status="proposed">
            <statement>
              Projection functions return composable XPath node sets.
            </statement>
            <rationale>
              An Agent must project indexes before loading large Task bodies.
            </rationale>
          </requirement>
        </requirements>
        <acceptance>
          <criterion
            id="20260823-accept-resume-task"
            decision="pending">
            A new Agent can resume one Task without loading unrelated bodies.
          </criterion>
        </acceptance>
      </product>
    </replace-node>
    <replace-node select="/iteration/design" expect="1">
      <design>
        <overview>Use XPath 1.0 with a custom XsltContext.</overview>
        <decisions>
          <decision
            id="20260823-design-node-projection"
            status="proposed">
            <rationale>
              Extension functions fill the XPath 1.0 structural projection gap.
            </rationale>
          </decision>
        </decisions>
      </design>
    </replace-node>
    <set-attribute
      select="/iteration"
      expect="1"
      name="updated_at"
      value="2026-08-23T02:45:00Z"/>
  </document>
  <document path="20260823-xpath-core/tasks.xml" expected_revision="1">
    <append-child select="/tasks" expect="1">
      <task
        id="20260823-task-xpath-projection"
        status="pending"
        created_at="2026-08-23T02:45:00Z"
        updated_at="2026-08-23T02:45:00Z">
        <index>
          <summary>Implement composable XPath projection.</summary>
          <term key="topic" value="xpath-projection"/>
          <term key="priority" value="p0"/>
        </index>
        <title>Implement XPath projection</title>
        <objective>Implement variables, filter, and filter-out.</objective>
        <origin>
          <ref
            scope="iteration"
            target="20260823-req-structural-projection"
            relation="implements"/>
        </origin>
        <constraints/>
        <acceptance>
          <criterion
            id="20260823-taskaccept-filter-composition"
            status="pending">
            Projection output remains XPath-composable.
          </criterion>
        </acceptance>
        <context>
          <summary>
            Use IXsltContextFunction and return XPathNodeIterator.
          </summary>
        </context>
        <records/>
      </task>
    </append-child>
  </document>
</transaction>
```

The transaction validates both resulting documents and commits both or neither. Successful revisions are `spec.xml=2` and `tasks.xml=2`.

### 5.3 Review and activate the Iteration

```powershell
dogdouspec validate --iteration 20260823-xpath-core
dogdouspec iteration readiness `
  --iteration 20260823-xpath-core `
  --phase activation `
  --format xml
```

The readiness result may say that the Iteration is structurally ready, but it
does not approve the product scope. The Agent presents the proposed scope,
acceptance, and design to the owner and stops. After review, the owner performs
an explicit confirmation:

```text
dogdouspec iteration confirm --stdin
```

```xml
<iteration-confirmation
  id="20260823T025000Z-confirmation-activation"
  iteration="20260823-xpath-core"
  action="activate"
  expected_spec_revision="2"
  actor="owner"
  decided_at="2026-08-23T02:50:00Z">
  <summary>
    Product scope, acceptance criteria, and design are approved for
    implementation.
  </summary>
  <requirements>
    <requirement
      target="20260823-req-structural-projection"
      decision="approved"/>
  </requirements>
  <design>
    <decision
      target="20260823-design-node-projection"
      decision="accepted"/>
  </design>
</iteration-confirmation>
```

The dedicated command validates the activation prerequisites, appends the
confirmation, and changes `status="draft"` to `status="active"` atomically.
After activation, ordinary Task records and status changes do not modify
`spec.xml`. Material product or design changes require another explicit product
confirmation.

`actor` records attribution; it is not a cryptographic identity proof. In v1,
the Skill must never synthesize or invoke a confirmation as part of its
automatic loop. It presents the readiness result and waits for an explicit
owner instruction. Strong authenticated approvals would be a separate feature,
not something the local XML format can honestly claim to provide.

## 6. Step 3 - Select actionable work

First obtain a compact overview of all unfinished Tasks:

```xpath
ds:filter(
  //task[not(
    @status='done'
    or @status='transferred'
    or @status='superseded'
    or @status='cancelled'
  )],
  '@id',
  '@status',
  '@agent',
  'index'
)
```

The overview includes blocked Tasks because they remain unfinished. It is not itself the actionable selection query.

Resume an already active Task first:

```xpath
(//task[@status='in-progress' or @status='verification'])[1]
```

When no active Task exists, select the first pending Task whose dependencies are done:

```xpath
(//task[
  @status='pending'
  and not(dependencies/ref[
    not(@target = /tasks/task[@status='done']/@id)
  ])
])[1]
```

Blocked Tasks are inspected separately:

```xpath
//task[@status='blocked']
```

This avoids a global current-Task pointer while preventing a blocked Task from hiding ready work.

## 7. Step 4 - Start and load a Task

Starting is a schema-aware Task update, not a claim file or lease. The Skill
obtains the `task.update` template and submits:

```powershell
dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 7 `
  --stdin
```

```xml
<task-update
  id="20260823T031500Z-update-start-projection"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T03:15:00Z">
  <records>
      <record
        id="20260823T031500Z-record-projection-start"
        kind="start"
        status="informational"
        created_at="2026-08-23T03:15:00Z"
        actor="codex">
        <summary>Started XPath projection implementation.</summary>
      </record>
  </records>
</task-update>
```

The helper checks pending state and dependency readiness. If another writer
changed revision 7, it fails without mutation and the Agent repeats selection.

After success, load the complete Task:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --format xml `
  --var task_id=20260823-task-xpath-projection `
  --xpath '//task[@id=$task_id]'
```

## 8. Step 5 - Implement and record a meaningful checkpoint

DogdouSpec does not run repository commands. The Agent works through repository-native entry points:

```powershell
agent.build.cmd Debug
```

The Agent does not append a record for every file edit or command. It records a meaningful checkpoint when the information will help another Agent resume or avoid repeating work.

```powershell
dogdouspec append `
  --document 20260823-xpath-core/tasks.xml `
  --parent-xpath "//task[@id=$task_id]/records" `
  --var task_id=20260823-task-xpath-projection `
  --expected-revision 9 `
  --stdin
```

```xml
<record
  id="20260823T040000Z-record-projection-attempt"
  kind="attempt"
  status="active"
  created_at="2026-08-23T04:00:00Z"
  actor="codex">
  <summary>
    The extension function composes correctly for one projected root. Multiple
    projected roots still need deterministic navigation tests.
  </summary>
</record>
```

The resulting `tasks.xml` revision is 10.

## 9. Step 6 - Encounter and record an unexpected problem

Implementation reveals that materializing each projected root in a separate temporary XML document produces undefined cross-document ordering when the XPath engine composes the result.

This invalidates part of the accepted design, so the Task must not silently continue.

```powershell
dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 10 `
  --stdin
```

```xml
<task-update
  id="20260823T043000Z-update-block-projection"
  transition="block"
  actor="codex"
  occurred_at="2026-08-23T04:30:00Z">
  <records>
      <record
        id="20260823T043000Z-record-projection-ordering"
        kind="finding"
        status="active"
        created_at="2026-08-23T04:30:00Z"
        actor="codex">
        <summary>
          Separate temporary documents do not provide a deterministic shared
          document order for composed projection results.
        </summary>
        <impact>
          The materialized-per-root design cannot satisfy deterministic result
          ordering and blocks the current Task.
        </impact>
      </record>
  </records>
</task-update>
```

The resulting `tasks.xml` revision is 11. The finding is Task-local because it was discovered and must be understood in that implementation context.

## 10. Step 7 - Discuss and resolve the problem

The Agent queries only the blocked Task and relevant design:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --var task_id=20260823-task-xpath-projection `
  --xpath '//task[@id=$task_id]'
```

```powershell
dogdouspec query `
  --document 20260823-xpath-core/spec.xml `
  --xpath "/iteration/design/decisions"
```

Discussion concludes that one shared projected document or a conformant virtual
Navigator is required. This is a material change to accepted design. The Agent
may explain the proposal, but it must not accept the proposal or unblock the
Task by itself. The owner first confirms the design change:

```text
dogdouspec iteration confirm --stdin
```

```xml
<iteration-confirmation
  id="20260823T050000Z-confirmation-projection-order"
  iteration="20260823-xpath-core"
  action="accept-design-change"
  expected_spec_revision="4"
  actor="owner"
  decided_at="2026-08-23T05:00:00Z">
  <summary>The material design resolution is approved.</summary>
  <new_design_decision
    id="20260823-design-shared-projection-order"
    status="proposed">
    <index>
      <summary>Use one deterministic projected XPath tree.</summary>
      <term key="topic" value="projection-order"/>
    </index>
    <rationale>
      All projected roots must share one deterministic XPath document order,
      or a virtual Navigator must preserve the source order.
    </rationale>
    <sources>
      <ref
        scope="iteration"
        target="20260823T043000Z-record-projection-ordering"
        relation="triggered-by"/>
    </sources>
  </new_design_decision>
  <design>
    <decision target="20260823-design-shared-projection-order" decision="accepted"/>
  </design>
</iteration-confirmation>
```

The confirmation command changes only `spec.xml`, producing revision 5. It
appends both the accepted decision and its confirmation provenance. If the
owner rejects or postpones the proposal, the Task remains blocked.

After the accepted decision is visible, the Agent reconciles the technical Task
state with the Task update helper:

```powershell
dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 11 `
  --stdin
```

```xml
<task-update
  id="20260823T050100Z-update-resume-projection"
  transition="resume"
  actor="codex"
  occurred_at="2026-08-23T05:01:00Z">
  <resolve-records>
    <record target="20260823T043000Z-record-projection-ordering"/>
  </resolve-records>
  <context_update>
    <design_snapshot>
      All projected roots share one deterministic projected tree or use a
      virtual Navigator that preserves source order.
    </design_snapshot>
  </context_update>
  <records>
      <record
        id="20260823T050100Z-record-projection-resolution"
        kind="decision"
        status="informational"
        created_at="2026-08-23T05:01:00Z"
        actor="codex">
        <summary>
          Reconciled the Task-local design snapshot with the owner-confirmed
          projection decision. Implementation may resume.
        </summary>
      </record>
  </records>
</task-update>
```

Successful revisions are `spec.xml=5` and `tasks.xml=12`. Only the new design
decision points to the Finding. A duplicate reverse relationship is not
persisted; reverse lookup derives it.

If the resolution had not changed product or accepted design, only `tasks.xml` would need a resolution record and status update.

## 11. Step 8 - Enter verification

After implementation and focused checks are ready, mark the Task as being verified before running the final verification matrix:

```powershell
dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 12 `
  --stdin
```

```xml
<task-update
  id="20260823T053000Z-update-begin-verification"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:30:00Z">
  <records>
    <record
      id="20260823T053000Z-record-begin-verification"
      kind="verification"
      status="active"
      created_at="2026-08-23T05:30:00Z"
      actor="codex">
      <summary>Entered final focused verification.</summary>
    </record>
  </records>
</task-update>
```

The resulting `tasks.xml` revision is 13.

Repository verification then runs outside DogdouSpec:

```powershell
agent.build.cmd Debug
```

If verification fails, append an attempt or Finding and return the Task to
`in-progress` or `blocked` in one Task update.

## 12. Step 9 - Record verification and complete the Task

After verification succeeds, one Task update records the durable proof and
terminal technical state:

```powershell
dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 13 `
  --stdin
```

```xml
<task-update
  id="20260823T060000Z-update-complete-projection"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-23T06:00:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filterout-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-composition" result="passed"/>
    <criterion target="20260823-taskaccept-result-limit" result="passed"/>
  </acceptance>
  <resolve-records>
    <record target="20260823T053000Z-record-begin-verification"/>
  </resolve-records>
  <records>
      <record
        id="20260823T055500Z-record-projection-verification"
        kind="verification"
        status="informational"
        created_at="2026-08-23T05:55:00Z"
        actor="codex">
        <summary>XPath projection implementation passed focused verification.</summary>
        <checks>
          <check
            kind="command"
            command="agent.build.cmd Debug"
            result="passed"
            exit_code="0">
            <summary>
              Build and focused projection tests completed successfully.
            </summary>
          </check>
        </checks>
        <covers>
          <ref
            scope="document"
            target="20260823-taskaccept-filter-members"
            relation="covers"/>
          <ref
            scope="document"
            target="20260823-taskaccept-filterout-members"
            relation="covers"/>
          <ref
            scope="document"
            target="20260823-taskaccept-filter-composition"
            relation="covers"/>
          <ref
            scope="document"
            target="20260823-taskaccept-result-limit"
            relation="covers"/>
        </covers>
      </record>
      <record
        id="20260823T060000Z-record-projection-completion"
        kind="completion"
        status="informational"
        created_at="2026-08-23T06:00:00Z"
        actor="codex">
        <summary>
          XPath variables, filter, filter-out, composition, deterministic order,
          and result-limit behavior are complete.
        </summary>
      </record>
  </records>
</task-update>
```

The resulting `tasks.xml` revision is 14. No Evidence document or completion transaction file is created.

## 13. Step 10 - Continue to the next Task

The Agent repeats the actionable selection sequence:

1. Resume `in-progress` or `verification` Task.
2. Otherwise select the first ready `pending` Task.
3. Report blocked Tasks separately.
4. Stop when no actionable Task remains.

This loop belongs to the Skill. The CLI does not persist or calculate a global next Task.

## 14. Step 11 - Establish technical readiness for product review

After all implementation Tasks are terminal, a final verification Task records checks covering the Iteration-level acceptance IDs in `spec.xml` with `scope="iteration"` references.

Run semantic validation:

```powershell
dogdouspec validate --iteration 20260823-xpath-core --format xml
```

Explicit Task gate:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --xpath "count(//task[not(@status='done' or @status='transferred' or @status='superseded' or @status='cancelled')])"
```

Expected value: `0`.

Active problem gate:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --xpath "count(//record[(@kind='finding' or @kind='blocker') and @status='active'])"
```

Expected value: `0`.

Product acceptance state before owner review:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/spec.xml `
  --xpath "count(/iteration/product/acceptance/criterion[@decision='pending'])"
```

In this demo the expected value is `6`. The final verification Task already
contains technical coverage for these criteria, but coverage is evidence for a
product decision, not the decision itself.

The Agent now asks for a non-mutating readiness assessment:

```powershell
dogdouspec iteration readiness `
  --iteration 20260823-xpath-core `
  --phase completion `
  --format xml
```

Illustrative result:

```xml
<readiness
  iteration="20260823-xpath-core"
  phase="completion"
  spec_revision="5"
  tasks_revision="21"
  technically_ready="true"
  owner_confirmation_required="true">
  <technical_checks>
    <check name="tasks_terminal" passed="true" />
    <check name="done_tasks_predicates" passed="true" />
    <check name="task_criteria_terminal_and_covered" passed="true" />
    <check name="no_active_findings" passed="true" />
  </technical_checks>
  <pending_product_decisions requirements="0" questions="0" design="0" acceptance="6" total="6" />
  <required_action action="complete" requires_owner_confirmation="true" />
</readiness>
```

`technically_ready="true"` with `owner_confirmation_required="true"` deliberately does not mean “Iteration complete.” At this point the Agent stops automatic advancement and presents the deliverables, product criteria, technical records, and unresolved risks to the owner.

## 15. Step 12 - Owner confirms the product outcome

If the owner concludes that the delivered result meets the intended product
outcome, the owner submits every criterion disposition and the Iteration
decision explicitly:

```text
dogdouspec iteration confirm --stdin
```

```xml
<iteration-confirmation
  id="20260823T090000Z-confirmation-completion"
  iteration="20260823-xpath-core"
  action="complete"
  expected_spec_revision="5"
  expected_tasks_revision="21"
  actor="owner"
  decided_at="2026-08-23T09:00:00Z">
  <summary>
    Product review confirms that the delivered behavior satisfies the intended
    Iteration outcome.
  </summary>
  <acceptance>
    <criterion
      target="20260823-accept-directory-overview"
      decision="accepted"/>
    <criterion
      target="20260823-accept-resume-task"
      decision="accepted"/>
    <criterion
      target="20260823-accept-integrated-verification"
      decision="accepted"/>
    <criterion
      target="20260823-accept-no-truncation"
      decision="accepted"/>
    <criterion
      target="20260823-accept-structured-reasoning"
      decision="accepted"/>
    <criterion
      target="20260823-accept-template-append"
      decision="accepted"/>
  </acceptance>
</iteration-confirmation>
```

The dedicated helper rechecks the exact `tasks.xml` revision and technical
readiness, requires an explicit disposition for every product criterion,
appends the confirmation, and changes the Iteration to `completed` in one
`spec.xml` commit. It does not infer any acceptance decision from Task state.
The resulting revisions are `spec.xml=6` and `tasks.xml=21`.

If the owner rejects a criterion, the submitted action must be `continue` or
`replan`; the Iteration remains non-terminal and follow-up Tasks or a material
change are recorded. A waiver requires an explicit rationale and configured
owner authority. The Agent cannot select any of these product dispositions on
the owner's behalf.

Validate again after commit:

```powershell
dogdouspec validate --iteration 20260823-xpath-core
```

Git review then shows:

- Product and design evolution in `spec.xml`.
- Task execution, problems, decisions, verification, and completion in `tasks.xml`.
- No generated project catalog, Evidence manifest, lease, or transaction history file.

## 16. Friction exposed by the simulation

### 16.1 Template-first mutation resolves routine transaction verbosity

Raw transaction XML remains coherent as an atomic kernel, but it is too verbose
and error-prone for routine use. The v1 surface therefore exposes:

- `template show` for valid example content.
- `append` for one append-oriented record.
- `task update` for record append plus technical Task-state changes.

Helpers compile to the same revision-checked atomic core and introduce no second
source of state. Dedicated `task.claim` and `task.complete` persisted objects
remain unnecessary.

### 16.2 `set-attribute` semantics must be explicit

The low-level escape hatch defines `set-attribute` as create-when-absent and
replace-when-present. `expect` controls selected-element cardinality. Routine
Skill updates do not need to reproduce these mechanics.

### 16.3 Technical readiness and product confirmation are separate contracts

Readiness is a read-only derived view. Confirmation is an explicit product
decision that cites expected revisions. Technical success may open the review
gate but cannot cross it. The generic transaction engine must reject writes to
protected SPEC decision fields with `OWNER_DECISION_REQUIRED`.

### 16.4 Cross-document acceptance coverage belongs to validation

XPath remains single-document. Coverage from Task records to Iteration acceptance uses stable references and is checked by `validate --iteration`, not by pretending one XPath expression crosses files.

### 16.5 Whole-file revisions serialize concurrent Task writers

One `tasks.xml` produces simple Git history and discovery but serializes concurrent writers through one revision. This is acceptable for v1. If real multi-Agent contention becomes common, Task sharding can be evaluated from evidence rather than introduced preemptively.

### 16.6 Discussion records should contain decisions, not transcripts

Raw conversation is normally transient. DogdouSpec persists the trigger,
material alternatives, rejection reasons, outcome, remaining uncertainty,
Findings, and Task-local handoff context. Product acceptance still requires a
separate confirmation. This preserves reasoning without creating an unbounded
chat log.
