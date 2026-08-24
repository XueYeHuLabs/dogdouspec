using System.Xml.Linq;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskChangeWorkflowTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;
    private string _workspace = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                RepoRoot = current.FullName;
                break;
            }
            current = current.Parent;
        }

        Assert.IsNotNull(RepoRoot, "Repository root could not be located.");
    }

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_WorkflowTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch { }
        }
    }

    private string CreateWorkspaceCopy()
    {
        var srcDemo = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destDir = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(srcDemo, destDir);
        return destDir;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }

    private void InitWorkspaceWithFeatureIteration(string iterId = "20260824-test-feature")
    {
        _workspace = CreateWorkspaceCopy();
        var (iSuccess, _, iDiags) = IterationCreator.Create(_workspace, iterId, "feature");
        Assert.IsTrue(iSuccess, $"Iteration create failed: {string.Join(", ", iDiags.Select(d => d.Message))}");
    }

    [TestMethod]
    public void TaskQuick_OperationalPendingAndDryRun_UseNormalTaskRepresentation()
    {
        var iterId = "20260824-quick-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var input = new QuickTaskInput("Quick maintenance", new List<string> { "src/**" }, "maintenance is verified", "bounded operational work",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, false, false,
            "20260824-task-quick-maintenance", "20260824T120000Z-quick-maintenance");
        var (success, result, envelope, diagnostics) = TaskQuick.Create(_workspace, input);
        Assert.IsTrue(success, string.Join(", ", diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(envelope);
        Assert.AreEqual(2, envelope.Documents.Single().Revision);
        var task = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml")).Root!.Elements("task").Single();
        Assert.AreEqual("pending", (string?)task.Attribute("status"));
        Assert.AreEqual(iterId, (string?)task.Element("origin")?.Element("ref")?.Attribute("target"));
        Assert.AreEqual("supports", (string?)task.Element("origin")?.Element("ref")?.Attribute("relation"));

        var before = File.ReadAllBytes(Path.Combine(_workspace, iterId, "tasks.xml"));
        var dry = input with { DryRun = true, ExpectedRevision = null, TaskId = "20260824-task-quick-preview", OperationId = "20260824T120001Z-quick-preview" };
        var (drySuccess, dryResult, dryEnvelope, dryDiags) = TaskQuick.Create(_workspace, dry);
        Assert.IsTrue(drySuccess, string.Join(", ", dryDiags.Select(d => d.Message)));
        Assert.IsNotNull(dryEnvelope);
        Assert.IsNotNull(dryResult);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_workspace, iterId, "tasks.xml")));

        var badOperation = input with { DryRun = true, TaskId = "20260824-task-quick-bad-op", OperationId = "20260824-quick-bad-op" };
        var (badSuccess, _, _, badDiags) = TaskQuick.Create(_workspace, badOperation);
        Assert.IsFalse(badSuccess);
        Assert.IsTrue(badDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void TaskQuick_DryRunDuplicateTaskMatchesWriteAndLeavesWorkspaceUntouched()
    {
        var iterId = "20260824-quick-duplicate";
        InitWorkspaceWithFeatureIteration(iterId);
        var first = new QuickTaskInput("First quick", new List<string> { "src/**" }, "done", "reason", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), iterId, 1, false, false, "20260824-task-quick-duplicate", "20260824T140000Z-quick-first");
        Assert.IsTrue(TaskQuick.Create(_workspace, first).Success);
        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var before = File.ReadAllBytes(tasksPath);
        var tempPath = Path.Combine(_workspace, "_tmp");
        var tempBefore = Directory.Exists(tempPath) ? Directory.GetFileSystemEntries(tempPath, "*", SearchOption.AllDirectories).Order().ToArray() : Array.Empty<string>();
        var conflicting = first with { Title = "Conflicting quick", ExpectedRevision = null, OperationId = "20260824T140001Z-quick-conflict", DryRun = true };
        var preview = TaskQuick.Create(_workspace, conflicting);
        Assert.IsFalse(preview.Success);
        Assert.AreEqual(DiagnosticCodes.DuplicateId, preview.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(tasksPath));
        var tempAfterPreview = Directory.Exists(tempPath) ? Directory.GetFileSystemEntries(tempPath, "*", SearchOption.AllDirectories).Order().ToArray() : Array.Empty<string>();
        CollectionAssert.AreEqual(tempBefore, tempAfterPreview);
        var write = TaskQuick.Create(_workspace, conflicting with { DryRun = false });
        Assert.IsFalse(write.Success);
        Assert.AreEqual(preview.Diagnostics.Single().Code, write.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(tasksPath));
    }

    [TestMethod]
    public void ProtectedStateGuard_LowLevelNewInProgressTaskIsRejected()
    {
        var before = XDocument.Parse("<tasks id='20260824-guard' iteration='20260824-guard' schema_version='1.0' revision='1'><index><summary>x</summary></index></tasks>");
        var after = new XDocument(before);
        after.Root!.Add(new XElement("task", new XAttribute("id", "20260824-task-guard"), new XAttribute("status", "in-progress")));
        var diagnostic = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", before, after);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(DiagnosticCodes.OwnerDecisionRequired, diagnostic.Code);
        after.Root!.Element("task")!.SetAttributeValue("status", "pending");
        Assert.IsNull(ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", before, after));

        var existingBefore = new XDocument(after);
        after.Root!.Element("task")!.SetAttributeValue("status", "in-progress");
        var transition = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, after);
        Assert.IsNotNull(transition);
        Assert.AreEqual(DiagnosticCodes.TaskTransitionConflict, transition.Code);
        after.Root!.Element("task")!.SetAttributeValue("status", "pending");
        after.Root!.Element("task")!.Add(new XElement("records", new XElement("record", new XAttribute("id", "20260824-record-guard"))));
        Assert.IsNull(ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, after));
        var removed = new XDocument(existingBefore);
        removed.Root!.Element("task")!.Remove();
        var removal = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, removed);
        Assert.IsNotNull(removal);
        Assert.AreEqual(DiagnosticCodes.TaskTransitionConflict, removal.Code);
    }

    [TestMethod]
    public void NewHelpers_RejectOversizedInputAndBackdatedOrUnstampedTasks()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var oversized = new string('x', XPathQueryLimits.MaxDocumentBytes + 1);
        var oversizedResult = TaskAdder.Add(_workspace, iterId, 1, oversized);
        Assert.IsFalse(oversizedResult.Success);
        Assert.IsTrue(oversizedResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.LimitExceeded));

        var badStamp = """
<task-add id="20260824T085900Z-taskadd-bad-stamp" actor="codex" occurred_at="2026-08-24T08:59:00Z"><task id="20260824-task-bad-stamp" status="pending" created_at="2026-08-24T08:58:59Z" updated_at="2026-08-24T08:59:00Z"><index><summary>Bad stamp.</summary></index><title>Bad stamp</title><objective>Reject timestamp mismatch.</objective><rationale>Test.</rationale><scope><repository path="src/test.cs"/></scope><origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/><acceptance><criterion id="20260824-crit-bad-stamp" status="pending">No.</criterion></acceptance><context><summary>Test.</summary></context><records/></task></task-add>
""";
        var badStampResult = TaskAdder.Add(_workspace, iterId, 1, badStamp);
        Assert.IsFalse(badStampResult.Success);
        Assert.IsTrue(badStampResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var validStamp = badStamp.Replace("2026-08-24T08:58:59Z", "2026-08-24T08:59:00Z", StringComparison.Ordinal);
        var added = TaskAdder.Add(_workspace, iterId, 1, validStamp);
        Assert.IsTrue(added.Success, string.Join(", ", added.Diagnostics.Select(d => d.Message)));

        var addedTask = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"))
            .Root!
            .Elements("task")
            .Single(t => (string?)t.Attribute("id") == "20260824-task-bad-stamp");
        Assert.AreEqual("2026-08-24T08:59:00Z", (string?)addedTask.Attribute("created_at"));
        Assert.AreEqual("2026-08-24T08:59:00Z", (string?)addedTask.Attribute("updated_at"));
        Assert.IsNull(addedTask.Attribute("started_at"));
        Assert.IsNull(addedTask.Attribute("completed_at"));

        var backdatedRevise = """
<task-revise id="20260824T085859Z-taskrevise-backdated" actor="codex" occurred_at="2026-08-24T08:58:59Z"><records><record id="20260824T085859Z-rec-backdated" kind="discussion" status="informational" created_at="2026-08-24T08:58:59Z" actor="codex"><summary>Backdated.</summary></record></records></task-revise>
""";
        var backdatedResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-bad-stamp", 2, backdatedRevise);
        Assert.IsFalse(backdatedResult.Success);
        Assert.IsTrue(backdatedResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        File.AppendAllText(tasksPath, new string(' ', XPathQueryLimits.MaxDocumentBytes + 1));
        var revise = """
<task-revise id="20260824T090000Z-taskrevise-limit" actor="codex" occurred_at="2026-08-24T09:00:00Z"><records><record id="20260824T090000Z-rec-limit" kind="discussion" status="informational" created_at="2026-08-24T09:00:00Z" actor="codex"><summary>Limit.</summary></record></records></task-revise>
""";
        var oversizedDocumentResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-missing", 1, revise);
        Assert.IsFalse(oversizedDocumentResult.Success);
        Assert.IsTrue(oversizedDocumentResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.LimitExceeded));
    }

    [TestMethod]
    public void TaskAdd_HappyPath_AddsPendingTaskAndIncrementsRevision()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T090000Z-taskadd-01"
  actor="codex"
  occurred_at="2026-08-24T09:00:00Z">
  <task
    id="20260824-task-feature-impl"
    status="pending"
    created_at="2026-08-24T09:00:00Z"
    updated_at="2026-08-24T09:00:00Z">
    <index>
      <summary>Feature Implementation Task Summary.</summary>
    </index>
    <title>Feature Implementation Task</title>
    <objective>Implement feature completely.</objective>
    <rationale>Implements feature req.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-impl" status="pending">
        Implementation passes tests.
      </criterion>
    </acceptance>
    <context>
      <summary>Initial technical context.</summary>
    </context>
    <records>
      <record
        id="20260824T090000Z-rec-add-initial"
        kind="discussion"
        status="informational"
        created_at="2026-08-24T09:00:00Z"
        actor="codex">
        <summary>Task created.</summary>
      </record>
    </records>
  </task>
</task-add>
""";

        var (success, env, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(success, $"Add failed: {string.Join(", ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.IsFalse(env.AlreadyApplied);

        // Verify task exists in tasks.xml
        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var addedTask = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-feature-impl");
        Assert.IsNotNull(addedTask);
        Assert.AreEqual("pending", (string?)addedTask.Attribute("status"));

        // Idempotent retry
        var (retrySuccess, retryEnv, retryDiags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(retrySuccess, $"Retry failed: {string.Join(", ", retryDiags.Select(d => d.Message))}");
        Assert.IsNotNull(retryEnv);
        Assert.IsTrue(retryEnv.AlreadyApplied);
    }

    [TestMethod]
    public void TaskAdd_NonPendingStatus_FailsClosed()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T090100Z-taskadd-bad-status"
  actor="codex"
  occurred_at="2026-08-24T09:01:00Z">
  <task
    id="20260824-task-inprog-attempt"
    status="in-progress"
    created_at="2026-08-24T09:01:00Z"
    updated_at="2026-08-24T09:01:00Z">
    <index>
      <summary>Invalid In-Progress Task.</summary>
    </index>
    <title>Invalid In-Progress Task</title>
    <objective>Invalid objective.</objective>
    <rationale>Attempting to add active task directly.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-inprog" status="pending">
        Implementation passes tests.
      </criterion>
    </acceptance>
    <context>
      <summary>Context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument || d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void TaskRevise_HappyPath_ElaboratesConstraintsAndAcceptance()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // First add a pending task
        var addXml = $"""
<task-add
  id="20260824T090200Z-taskadd-base"
  actor="codex"
  occurred_at="2026-08-24T09:02:00Z">
  <task
    id="20260824-task-to-revise"
    status="pending"
    created_at="2026-08-24T09:02:00Z"
    updated_at="2026-08-24T09:02:00Z">
    <index>
      <summary>Base Task Summary.</summary>
    </index>
    <title>Base Task</title>
    <objective>Base objective.</objective>
    <rationale>Initial base rationale.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-base" status="pending">
        Base criterion.
      </criterion>
    </acceptance>
    <context>
      <summary>Base context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Now revise it
        var reviseXml = """
<task-revise
  id="20260824T090300Z-taskrevise-01"
  actor="codex"
  occurred_at="2026-08-24T09:03:00Z">
  <rationale>Elaborated technical rationale.</rationale>
  <add_constraints>
    <constraint id="20260824-const-perf">
      Must execute under 100ms.
    </constraint>
  </add_constraints>
  <add_acceptance>
    <criterion id="20260824-crit-perf" status="pending">
      Benchmark verified.
    </criterion>
  </add_acceptance>
  <records>
    <record
      id="20260824T090300Z-rec-revise-01"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:03:00Z"
      actor="codex">
      <summary>Added performance constraint and criterion.</summary>
    </record>
  </records>
</task-revise>
""";

        var (revOk, revEnv, revDiags) = TaskReviser.Revise(_workspace, iterId, "20260824-task-to-revise", 2, reviseXml);
        Assert.IsTrue(revOk, $"Revise failed: {string.Join(", ", revDiags.Select(d => d.Message))}");
        Assert.IsNotNull(revEnv);
        Assert.IsFalse(revEnv.AlreadyApplied);

        // Verify task in document
        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var task = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-to-revise");
        Assert.AreEqual("Elaborated technical rationale.", task.Element("rationale")?.Value);
        Assert.IsNotNull(task.Element("constraints")?.Elements("constraint").FirstOrDefault(c => (string?)c.Attribute("id") == "20260824-const-perf"));
        Assert.IsNotNull(task.Element("acceptance")?.Elements("criterion").FirstOrDefault(c => (string?)c.Attribute("id") == "20260824-crit-perf"));
    }

    [TestMethod]
    public void TaskExecution_RejectsProposedOriginRequirement()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var add = """
<task-add id="20260824T092700Z-taskadd-proposed-origin" actor="codex" occurred_at="2026-08-24T09:27:00Z"><task id="20260824-task-proposed-origin" status="pending" created_at="2026-08-24T09:27:00Z" updated_at="2026-08-24T09:27:00Z"><index><summary>Proposed origin task.</summary></index><title>Proposed origin</title><objective>Must not execute.</objective><rationale>Await owner.</rationale><scope><repository path="src/proposed.cs"/></scope><origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/><acceptance><criterion id="20260824-crit-proposed-origin" status="pending">Works.</criterion></acceptance><context><summary>Context.</summary></context><records/></task></task-add>
""";
        Assert.IsTrue(TaskAdder.Add(_workspace, iterId, 1, add).Success);
        var start = """
<task-update id="20260824T092800Z-taskstart-proposed-origin" transition="start" actor="codex" occurred_at="2026-08-24T09:28:00Z"><records><record id="20260824T092800Z-rec-proposed-origin" kind="start" status="informational" created_at="2026-08-24T09:28:00Z" actor="codex"><summary>Attempt start.</summary></record></records></task-update>
""";
        var result = TaskUpdater.Update(_workspace, iterId, "20260824-task-proposed-origin", 2, start);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void TaskRevise_StartedTaskOnlyAllowsAdditiveScopeExpansion()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var activate = $"""
<iteration-confirmation id="20260824T093000Z-conf-activate-started-revise" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="1" actor="owner" decided_at="2026-08-24T09:30:00Z">
  <summary>Approve baseline requirement.</summary>
  <requirements><requirement target="20260824-req-test-feature" decision="approved"/></requirements>
  <acceptance><criterion target="20260824-crit-test-feature" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        Assert.IsTrue(IterationConfirmer.Confirm(_workspace, activate).Success);

        var add = """
<task-add id="20260824T093100Z-taskadd-started-revise" actor="codex" occurred_at="2026-08-24T09:31:00Z">
  <task id="20260824-task-started-revise" status="pending" created_at="2026-08-24T09:31:00Z" updated_at="2026-08-24T09:31:00Z">
    <index><summary>Started revise task.</summary></index><title>Started revise task</title><objective>Verify revision boundary.</objective><rationale>Original rationale.</rationale>
    <scope><repository path="src/original.cs"><include path="src/original.cs"/></repository></scope>
    <origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/>
    <acceptance><criterion id="20260824-crit-started-revise" status="pending">Works.</criterion></acceptance><context><summary>Context.</summary></context><records/>
  </task>
</task-add>
""";
        Assert.IsTrue(TaskAdder.Add(_workspace, iterId, 1, add).Success);
        var start = """
<task-update id="20260824T093200Z-taskstart-started-revise" transition="start" actor="codex" occurred_at="2026-08-24T09:32:00Z"><records><record id="20260824T093200Z-rec-started-revise" kind="start" status="informational" created_at="2026-08-24T09:32:00Z" actor="codex"><summary>Start.</summary></record></records></task-update>
""";
        Assert.IsTrue(TaskUpdater.Update(_workspace, iterId, "20260824-task-started-revise", 2, start).Success);

        var rationaleRewrite = """
<task-revise id="20260824T093300Z-taskrevise-rationale" actor="codex" occurred_at="2026-08-24T09:33:00Z"><rationale>Rewritten rationale.</rationale><records><record id="20260824T093300Z-rec-rationale" kind="discussion" status="informational" created_at="2026-08-24T09:33:00Z" actor="codex"><summary>Attempt rationale rewrite.</summary></record></records></task-revise>
""";
        var rationaleResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, rationaleRewrite);
        Assert.IsFalse(rationaleResult.Success);
        Assert.IsTrue(rationaleResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskRevisionNotAllowed));

        var narrowedScope = """
<task-revise id="20260824T093400Z-taskrevise-narrow" actor="codex" occurred_at="2026-08-24T09:34:00Z"><scope><repository path="src/other.cs"/></scope><records><record id="20260824T093400Z-rec-narrow" kind="discussion" status="informational" created_at="2026-08-24T09:34:00Z" actor="codex"><summary>Attempt narrowing.</summary></record></records></task-revise>
""";
        var narrowResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, narrowedScope);
        Assert.IsFalse(narrowResult.Success);
        Assert.IsTrue(narrowResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskRevisionNotAllowed));

        var expandedScope = """
<task-revise id="20260824T093500Z-taskrevise-expand" actor="codex" occurred_at="2026-08-24T09:35:00Z"><scope><repository path="src/original.cs"><include path="src/original.cs"/></repository><repository path="src/added.cs"/></scope><records><record id="20260824T093500Z-rec-expand" kind="discussion" status="informational" created_at="2026-08-24T09:35:00Z" actor="codex"><summary>Expand scope.</summary></record></records></task-revise>
""";
        var expandResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, expandedScope);
        Assert.IsTrue(expandResult.Success, string.Join(", ", expandResult.Diagnostics.Select(d => d.Message)));
    }

    [TestMethod]
    public void TaskSplit_HappyPath_SetsParentDispositionAndAddsSubtasks()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add parent task
        var addXml = $"""
<task-add
  id="20260824T090400Z-taskadd-parent"
  actor="codex"
  occurred_at="2026-08-24T09:04:00Z">
  <task
    id="20260824-task-parent-split"
    status="pending"
    created_at="2026-08-24T09:04:00Z"
    updated_at="2026-08-24T09:04:00Z">
    <index>
      <summary>Large Parent Task Summary.</summary>
    </index>
    <title>Large Parent Task</title>
    <objective>Parent objective.</objective>
    <rationale>Too big, needs split.</rationale>
    <scope>
      <repository path="src/big.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-parent" status="pending">
        Parent criterion.
      </criterion>
    </acceptance>
    <context>
      <summary>Parent context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        var splitXml = $"""
<task-split
  id="20260824T090500Z-tasksplit-01"
  actor="codex"
  occurred_at="2026-08-24T09:05:00Z">
  <parent_disposition
    transition="supersede"
    rationale="Split into part A and part B.">
    <record
      id="20260824T090500Z-rec-split-disp"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:05:00Z"
      actor="codex">
      <summary>Superseded by subtasks.</summary>
    </record>
  </parent_disposition>
  <subtasks>
    <task
      id="20260824-task-sub-1"
      status="pending"
      created_at="2026-08-24T09:05:00Z"
      updated_at="2026-08-24T09:05:00Z">
      <index>
        <summary>Subtask 1 summary.</summary>
      </index>
      <title>Subtask 1</title>
      <objective>Subtask 1 objective.</objective>
      <rationale>Part 1</rationale>
      <scope>
        <repository path="src/part1.cs"/>
      </scope>
      <origin>
        <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
      </origin>
      <constraints/>
      <acceptance>
        <criterion id="20260824-crit-sub1" status="pending">
          Subtask 1 works.
        </criterion>
      </acceptance>
      <context>
        <summary>Subtask 1 context.</summary>
      </context>
      <records/>
    </task>
    <task
      id="20260824-task-sub-2"
      status="pending"
      created_at="2026-08-24T09:05:00Z"
      updated_at="2026-08-24T09:05:00Z">
      <index>
        <summary>Subtask 2 summary.</summary>
      </index>
      <title>Subtask 2</title>
      <objective>Subtask 2 objective.</objective>
      <rationale>Part 2</rationale>
      <scope>
        <repository path="src/part2.cs"/>
      </scope>
      <origin>
        <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
      </origin>
      <constraints/>
      <acceptance>
        <criterion id="20260824-crit-sub2" status="pending">
          Subtask 2 works.
        </criterion>
      </acceptance>
      <context>
        <summary>Subtask 2 context.</summary>
      </context>
      <records/>
    </task>
  </subtasks>
</task-split>
""";

        var (splitOk, splitEnv, splitDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2, splitXml);
        Assert.IsTrue(splitOk, $"Split failed: {string.Join(", ", splitDiags.Select(d => d.Message))}");
        Assert.IsNotNull(splitEnv);
        Assert.IsFalse(splitEnv.AlreadyApplied);

        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var parent = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-parent-split");
        Assert.AreEqual("superseded", (string?)parent.Attribute("status"));
        Assert.IsTrue(parent.Element("records")!.Elements("record").Any(r =>
            (string?)r.Attribute("id") == "20260824T090500Z-tasksplit-01-receipt" &&
            r.Element("impact")?.Value == "Split into part A and part B."), "Split rationale must be queryable from its generated receipt.");
        var (splitReplayOk, splitReplayEnv, splitReplayDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2, splitXml);
        Assert.IsTrue(splitReplayOk, string.Join(", ", splitReplayDiags.Select(d => d.Message)));
        Assert.IsTrue(splitReplayEnv!.AlreadyApplied);
        var (splitDivergentOk, _, splitDivergentDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2,
            splitXml.Replace("Split into part A and part B.", "Different split rationale.", StringComparison.Ordinal));
        Assert.IsFalse(splitDivergentOk);
        Assert.IsTrue(splitDivergentDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        var sub1 = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-sub-1");
        var sub2 = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-sub-2");
        Assert.IsNotNull(sub1);
        Assert.IsNotNull(sub2);
    }

    [TestMethod]
    public void RequirementPropose_ProposedStatus_Succeeds()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var proposeXml = """
<requirement-propose
  id="20260824T091000Z-reqprop-01"
  actor="codex"
  occurred_at="2026-08-24T09:10:00Z">
  <requirement id="20260824-req-technical-extension" status="proposed">
    <index>
      <summary>Technical Extension Requirement Summary.</summary>
    </index>
    <statement>The system must support technical extension.</statement>
    <rationale>Discovered during implementation.</rationale>
  </requirement>
</requirement-propose>
""";

        var (propOk, propEnv, propDiags) = RequirementProposer.Propose(_workspace, iterId, 1, proposeXml);
        Assert.IsTrue(propOk, $"Propose failed: {string.Join(", ", propDiags.Select(d => d.Message))}");
        Assert.IsNotNull(propEnv);

        var specXmlPath = Path.Combine(_workspace, iterId, "spec.xml");
        var specDoc = XDocument.Load(specXmlPath);
        var req = specDoc.Descendants("requirement").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824-req-technical-extension");
        Assert.IsNotNull(req);
        Assert.AreEqual("proposed", (string?)req.Attribute("status"));
    }

    [TestMethod]
    public void RequirementPropose_NonProposedStatus_FailsWithOwnerDecisionRequired()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var proposeXml = """
<requirement-propose
  id="20260824T091100Z-reqprop-approved-attempt"
  actor="codex"
  occurred_at="2026-08-24T09:11:00Z">
  <requirement id="20260824-req-auto-approved" status="approved">
    <index>
      <summary>Auto Approved Requirement Summary.</summary>
    </index>
    <statement>Illegal self-approval statement.</statement>
    <rationale>Trying to bypass owner authority.</rationale>
  </requirement>
</requirement-propose>
""";

        var (propOk, _, propDiags) = RequirementProposer.Propose(_workspace, iterId, 1, proposeXml);
        Assert.IsFalse(propOk);
        Assert.IsTrue(propDiags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void ChangePropose_HappyPath_AttachesFindingFreezesTaskAndProposesRequirement()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task to freeze
        var addXml = $"""
<task-add
  id="20260824T091500Z-taskadd-target"
  actor="codex"
  occurred_at="2026-08-24T09:15:00Z">
  <task
    id="20260824-task-to-freeze"
    status="pending"
    created_at="2026-08-24T09:15:00Z"
    updated_at="2026-08-24T09:15:00Z">
    <index>
      <summary>Task to Freeze Summary.</summary>
    </index>
    <title>Task to Freeze</title>
    <objective>Freeze objective.</objective>
    <rationale>Initial rationale</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-tf" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Initial context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        var activateXml = $"""
<iteration-confirmation id="20260824T091530Z-conf-activate" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="2" actor="owner" decided_at="2026-08-24T09:15:30Z">
  <summary>Owner activated the baseline requirement.</summary>
  <requirements><requirement target="20260824-req-test-feature" decision="approved"/></requirements>
  <acceptance><criterion target="20260824-crit-test-feature" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        var (activateOk, _, activateDiags) = IterationConfirmer.Confirm(_workspace, activateXml);
        Assert.IsTrue(activateOk, $"Activation failed: {string.Join(", ", activateDiags.Select(d => d.Message))}");

        var changeProposeXml = """
<change-propose
  id="20260824T091600Z-changeprop-01"
  actor="codex"
  occurred_at="2026-08-24T09:16:00Z">
  <summary>Discovered requirement scope gap during execution.</summary>
  <finding_record task="20260824-task-to-freeze">
    <record
      id="20260824T091600Z-rec-finding-scope-gap"
      kind="finding"
      status="active"
      created_at="2026-08-24T09:16:00Z"
      actor="codex">
      <summary>Scope gap requires new requirement.</summary>
    </record>
  </finding_record>
  <freeze_tasks>
    <task target="20260824-task-to-freeze" reason="Blocked pending owner decision on requirement."/>
  </freeze_tasks>
  <proposed_requirements>
    <requirement id="20260824-req-scope-gap-fix" status="proposed">
      <index>
        <summary>Scope Gap Fix Requirement Summary.</summary>
      </index>
      <statement>The system must fix the scope gap.</statement>
      <rationale>Required for proper execution.</rationale>
    </requirement>
  </proposed_requirements>
</change-propose>
""";

        var (cpOk, cpEnv, cpDiags) = ChangeProposer.Propose(_workspace, iterId, 2, 2, changeProposeXml);
        Assert.IsTrue(cpOk, $"Change propose failed: {string.Join(", ", cpDiags.Select(d => d.Message))}");
        Assert.IsNotNull(cpEnv);

        // Verify tasks.xml: task is blocked and has active finding
        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var task = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-to-freeze");
        Assert.AreEqual("blocked", (string?)task.Attribute("status"));
        var activeFinding = task.Element("records")?.Elements("record").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824T091600Z-rec-finding-scope-gap");
        Assert.IsNotNull(activeFinding);
        Assert.AreEqual("active", (string?)activeFinding.Attribute("status"));

        // Verify spec.xml: proposed requirement exists
        var specDoc = XDocument.Load(Path.Combine(_workspace, iterId, "spec.xml"));
        var proposedReq = specDoc.Descendants("requirement").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824-req-scope-gap-fix");
        Assert.IsNotNull(proposedReq);
        Assert.AreEqual("proposed", (string?)proposedReq.Attribute("status"));

        // change propose owns a two-document transaction. An interruption after
        // the first publish must recover to the complete new state rather than
        // leaving a split specification/task view for the next writer.
        var interruptedXml = changeProposeXml
            .Replace("20260824T091600Z-changeprop-01", "20260824T091700Z-changeprop-fault", StringComparison.Ordinal)
            .Replace("20260824T091600Z-rec-finding-scope-gap", "20260824T091700Z-rec-finding-fault", StringComparison.Ordinal)
            .Replace("20260824-req-scope-gap-fix", "20260824-req-scope-gap-fault", StringComparison.Ordinal)
            .Replace("2026-08-24T09:16:00Z", "2026-08-24T09:17:00Z", StringComparison.Ordinal);
        var (faultSuccess, _, _) = ChangeProposer.Propose(
            _workspace, iterId, 2, 3, interruptedXml,
            faultInjector: new TestFaultInjector(FaultPhase.DuringMultiFileCommitAfterFirstFile));
        Assert.IsFalse(faultSuccess);
        var (recovered, recoveryError) = StartupRecovery.Run(_workspace);
        Assert.IsTrue(recovered, recoveryError?.Message);
        var recoveredValidation = SchemaValidator.Validate(_workspace);
        Assert.IsTrue(recoveredValidation.IsValid, string.Join("; ", recoveredValidation.Diagnostics.Select(d => d.Message)));
        var recoveredSpec = XDocument.Load(Path.Combine(_workspace, iterId, "spec.xml"));
        var recoveredTasks = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var recoveredRequirement = recoveredSpec.Descendants("requirement").Any(r => (string?)r.Attribute("id") == "20260824-req-scope-gap-fault");
        var recoveredFinding = recoveredTasks.Descendants("record").Any(r => (string?)r.Attribute("id") == "20260824T091700Z-rec-finding-fault");
        Assert.AreEqual(recoveredRequirement, recoveredFinding, "Recovery must converge change-propose documents to one complete old or new state.");
    }

    [TestMethod]
    public void ChangeApply_OutsideReplanningStatus_FailsWithChangeApplicationInvalid()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var changeApplyXml = """
<change-apply
  id="20260824T092000Z-changeapply-bad-status"
  actor="codex"
  occurred_at="2026-08-24T09:20:00Z">
  <summary>Attempting change apply while iteration is draft/active.</summary>
</change-apply>
""";

        // spec.xml is currently in status="draft"
        var (caOk, _, caDiags) = ChangeApplier.Apply(_workspace, iterId, 1, 1, changeApplyXml);
        Assert.IsFalse(caOk);
        Assert.IsTrue(caDiags.Any(d => d.Code == DiagnosticCodes.ChangeApplicationInvalid));
    }

    [TestMethod]
    public void TaskUpdater_ReplanningFreeze_RejectsExecutionTransitions()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task
        var addXml = $"""
<task-add
  id="20260824T092500Z-taskadd-replanning"
  actor="codex"
  occurred_at="2026-08-24T09:25:00Z">
  <task
    id="20260824-task-freeze-test"
    status="pending"
    created_at="2026-08-24T09:25:00Z"
    updated_at="2026-08-24T09:25:00Z">
    <index>
      <summary>Task Freeze Test Summary.</summary>
    </index>
    <title>Task Freeze Test</title>
    <objective>Freeze objective.</objective>
    <rationale>Testing replanning freeze.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-ft" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Freeze context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Transition iteration to replanning
        var specXmlPath = Path.Combine(_workspace, iterId, "spec.xml");
        var specDoc = XDocument.Load(specXmlPath);
        specDoc.Root!.SetAttributeValue("status", "replanning");
        var confs = specDoc.Root!.Element("confirmations");
        if (confs == null)
        {
            confs = new XElement("confirmations");
            specDoc.Root.Add(confs);
        }
        confs.Add(new XElement("confirmation",
            new XAttribute("id", "20260824T092530Z-conf-replan"),
            new XAttribute("action", "replan"),
            new XAttribute("decision", "accepted"),
            new XAttribute("actor", "owner"),
            new XAttribute("decided_at", "2026-08-24T09:25:30Z"),
            new XElement("summary", "Owner confirmed replanning.")));
        specDoc.Save(specXmlPath);

        // Attempt execution transition 'start' on task
        var startXml = """
<task-update
  id="20260824T092600Z-update-start-during-replanning"
  transition="start"
  actor="codex"
  occurred_at="2026-08-24T09:26:00Z">
  <records>
    <record
      id="20260824T092600Z-rec-start-rep"
      kind="start"
      status="informational"
      created_at="2026-08-24T09:26:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";

        var (startOk, _, startDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-freeze-test", 2, startXml);
        Assert.IsFalse(startOk);
        Assert.IsTrue(startDiags.Any(d => d.Code == DiagnosticCodes.IterationReplanningExecutionFrozen));

        // Terminal transition 'supersede' should NOT be frozen
        var supersedeXml = """
<task-update
  id="20260824T092700Z-update-supersede-during-replanning"
  transition="supersede"
  actor="codex"
  occurred_at="2026-08-24T09:27:00Z">
  <records>
    <record
      id="20260824T092700Z-rec-supersede-rep"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:27:00Z"
      actor="codex">
      <summary>Superseded task.</summary>
    </record>
  </records>
</task-update>
""";

        var (supOk, supEnv, supDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-freeze-test", 2, supersedeXml);
        Assert.IsTrue(supOk, $"Supersede should succeed during replanning: {string.Join(", ", supDiags.Select(d => d.Message))}");
        Assert.IsNotNull(supEnv);
    }

    [TestMethod]
    public void TaskUpdater_TerminalTaskImmutability_RejectsMutations()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task
        var addXml = $"""
<task-add
  id="20260824T093000Z-taskadd-term"
  actor="codex"
  occurred_at="2026-08-24T09:30:00Z">
  <task
    id="20260824-task-terminal-immutability"
    status="pending"
    created_at="2026-08-24T09:30:00Z"
    updated_at="2026-08-24T09:30:00Z">
    <index>
      <summary>Terminal Immutability Task Summary.</summary>
    </index>
    <title>Terminal Immutability Task</title>
    <objective>Terminal objective.</objective>
    <rationale>Testing terminal immutability.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-term" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Terminal context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Supersede the task
        var cancelXml = """
<task-update
  id="20260824T093100Z-update-cancel-term"
  transition="cancel"
  actor="codex"
  occurred_at="2026-08-24T09:31:00Z">
  <records>
    <record
      id="20260824T093100Z-rec-cancel-term"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:31:00Z"
      actor="codex">
      <summary>Cancelled task.</summary>
    </record>
  </records>
</task-update>
""";
        var (cancelOk, _, _) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 2, cancelXml);
        Assert.IsTrue(cancelOk);

        // Try to transition cancelled task back to in-progress
        var restartXml = """
<task-update
  id="20260824T093200Z-update-restart-term"
  transition="start"
  actor="codex"
  occurred_at="2026-08-24T09:32:00Z">
  <records>
    <record
      id="20260824T093200Z-rec-restart-term"
      kind="start"
      status="informational"
      created_at="2026-08-24T09:32:00Z"
      actor="codex">
      <summary>Restarting.</summary>
    </record>
  </records>
</task-update>
""";
        var (resOk, _, resDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, restartXml);
        Assert.IsFalse(resOk);
        Assert.IsTrue(resDiags.Any(d => d.Code == DiagnosticCodes.TaskImmutable));

        // Try to append non-informational (completion) record to cancelled task
        var compRecXml = """
<task-update
  id="20260824T093300Z-update-comp-term"
  actor="codex"
  occurred_at="2026-08-24T09:33:00Z">
  <records>
    <record
      id="20260824T093300Z-rec-comp-term"
      kind="completion"
      status="informational"
      created_at="2026-08-24T09:33:00Z"
      actor="codex">
      <summary>Completed.</summary>
    </record>
  </records>
</task-update>
""";
        var (compOk, _, compDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, compRecXml);
        Assert.IsFalse(compOk);
        Assert.IsTrue(compDiags.Any(d => d.Code == DiagnosticCodes.TaskImmutable));

        // Appending informational record to cancelled task should SUCCEED
        var infoRecXml = """
<task-update
  id="20260824T093400Z-update-info-term"
  actor="codex"
  occurred_at="2026-08-24T09:34:00Z">
  <records>
    <record
      id="20260824T093400Z-rec-info-term"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:34:00Z"
      actor="codex">
      <summary>Post-cancellation informational discussion.</summary>
    </record>
  </records>
</task-update>
""";
        var (infoOk, infoEnv, infoDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, infoRecXml);
        Assert.IsTrue(infoOk, $"Informational record append to terminal task must succeed: {string.Join(", ", infoDiags.Select(d => d.Message))}");
        Assert.IsNotNull(infoEnv);
    }
}
