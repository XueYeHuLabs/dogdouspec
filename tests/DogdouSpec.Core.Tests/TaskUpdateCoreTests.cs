using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskUpdateCoreTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_TaskUpdateTests_" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // Ignore cleanup errors
            }
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

    [TestMethod]
    public void TaskUpdate_StartTransition_PendingToInProgress_SetsTimestampsAndRevision()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-history-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Starting task history work.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: requestXml);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(env);
        Assert.IsFalse(env.AlreadyApplied);
        Assert.AreEqual(1, env.Documents.Count);
        Assert.AreEqual(10, env.Documents[0].Revision);

        // Verify tasks.xml on disk
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        Assert.AreEqual("10", xdoc.Root!.Attribute("revision")?.Value);

        var taskElem = xdoc.Root.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-task-history");
        Assert.AreEqual("in-progress", taskElem.Attribute("status")?.Value);
        Assert.AreEqual("2026-08-23T05:00:00Z", taskElem.Attribute("started_at")?.Value);
        Assert.AreEqual("2026-08-23T05:00:00Z", taskElem.Attribute("updated_at")?.Value);

        var recElem = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T050000Z-record-history-start");
        Assert.AreEqual("20260823T050000Z-update-start-history", recElem.Attribute("operation_id")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_BlockAndResumeTransitions_WorkAsExpected()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Block an in-progress task (20260823-task-xpath-projection is in-progress in demo)
        var blockReq = """
<task-update
  id="20260823T051000Z-update-block"
  transition="block"
  actor="codex"
  occurred_at="2026-08-23T05:10:00Z">
  <records>
    <record
      id="20260823T051000Z-record-blocker"
      kind="finding"
      status="active"
      created_at="2026-08-23T05:10:00Z"
      actor="codex">
      <summary>Blocked on external dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (blockSuccess, _, blockDiags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: blockReq);

        Assert.IsTrue(blockSuccess, string.Join("; ", blockDiags.Select(d => d.Message)));

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        Assert.AreEqual("blocked", taskElem.Attribute("status")?.Value);

        // 2. Resume the blocked task
        var resumeReq = """
<task-update
  id="20260823T052000Z-update-resume"
  transition="resume"
  actor="codex"
  occurred_at="2026-08-23T05:20:00Z">
  <records>
    <record
      id="20260823T052000Z-record-resume"
      kind="handoff"
      status="informational"
      created_at="2026-08-23T05:20:00Z"
      actor="codex">
      <summary>Resuming task.</summary>
    </record>
  </records>
</task-update>
""";

        var (resumeSuccess, _, resumeDiags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 10,
            requestXml: resumeReq);

        Assert.IsTrue(resumeSuccess, string.Join("; ", resumeDiags.Select(d => d.Message)));

        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        Assert.AreEqual("in-progress", taskElem.Attribute("status")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_VerifyTransition_InProgressToVerification()
    {
        var workspace = CreateWorkspaceCopy();
        var verifyReq = """
<task-update
  id="20260823T053000Z-update-verify"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:30:00Z">
  <records>
    <record
      id="20260823T053000Z-record-verify-start"
      kind="verification"
      status="informational"
      created_at="2026-08-23T05:30:00Z"
      actor="codex">
      <summary>Ready for verification.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, _, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: verifyReq);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        Assert.AreEqual("verification", taskElem.Attribute("status")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_TerminalTransitions_TransferSupersedeCancel()
    {
        var workspace = CreateWorkspaceCopy();

        // Transfer
        var transferReq = """
<task-update
  id="20260823T054000Z-update-transfer"
  transition="transfer"
  actor="codex"
  occurred_at="2026-08-23T05:40:00Z">
  <records>
    <record
      id="20260823T054000Z-record-transfer"
      kind="handoff"
      status="informational"
      created_at="2026-08-23T05:40:00Z"
      actor="codex">
      <summary>Transferring task to next iteration.</summary>
    </record>
  </records>
</task-update>
""";

        var (tSuccess, _, tDiags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: transferReq);

        Assert.IsTrue(tSuccess, string.Join("; ", tDiags.Select(d => d.Message)));

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-task-history");
        Assert.AreEqual("transferred", taskElem.Attribute("status")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_IllegalTransitions_RejectedWithTaskTransitionConflict()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Cannot start an in-progress task (20260823-task-xpath-projection is in-progress)
        var startReq = """
<task-update
  id="20260823T055000Z-update-bad-start"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:50:00Z">
  <records>
    <record
      id="20260823T055000Z-record-bad-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:50:00Z"
      actor="codex">
      <summary>Illegal start.</summary>
    </record>
  </records>
</task-update>
""";

        var (success1, _, diags1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: startReq);

        Assert.IsFalse(success1);
        Assert.IsTrue(diags1.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict));

        // 2. Cannot complete a pending task directly
        var completeReq = """
<task-update
  id="20260823T055100Z-update-bad-complete"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-23T05:51:00Z">
  <records>
    <record
      id="20260823T055100Z-record-bad-complete"
      kind="completion"
      status="informational"
      created_at="2026-08-23T05:51:00Z"
      actor="codex">
      <summary>Illegal direct complete.</summary>
    </record>
  </records>
</task-update>
""";

        var (success2, _, diags2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: completeReq);

        Assert.IsFalse(success2);
        Assert.IsTrue(diags2.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict));
    }

    [TestMethod]
    public void TaskUpdate_CombinedMutations_AppliesAllChangesAtomically()
    {
        var workspace = CreateWorkspaceCopy();
        var combinedReq = """
<task-update
  id="20260823T060000Z-update-combined"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T06:00:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filterout-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-composition" result="passed"/>
    <criterion target="20260823-taskaccept-result-limit" result="passed"/>
  </acceptance>
  <resolve-records>
    <record target="20260823T033000Z-record-projection-decision"/>
    <record target="20260823T040000Z-record-projection-attempt"/>
  </resolve-records>
  <context_update>
    <summary>Updated context summary for XPath projection.</summary>
    <design_snapshot>Updated design snapshot with composable filters.</design_snapshot>
  </context_update>
  <records>
    <record
      id="20260823T060000Z-record-projection-verification"
      kind="verification"
      status="informational"
      created_at="2026-08-23T06:00:00Z"
      actor="codex">
      <summary>Verification executed and all criteria verified.</summary>
      <covers>
        <ref scope="document" target="20260823-taskaccept-filter-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filterout-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filter-composition" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-result-limit" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: combinedReq);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));
        Assert.AreEqual(10, env!.Documents[0].Revision);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");

        // Status
        Assert.AreEqual("verification", taskElem.Attribute("status")?.Value);

        // Acceptance
        foreach (var crit in taskElem.Element("acceptance")!.Elements("criterion"))
        {
            Assert.AreEqual("passed", crit.Attribute("status")?.Value);
        }

        // Resolved records
        var decRec = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T033000Z-record-projection-decision");
        Assert.AreEqual("resolved", decRec.Attribute("status")?.Value);
        var attRec = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T040000Z-record-projection-attempt");
        Assert.AreEqual("resolved", attRec.Attribute("status")?.Value);

        // Context
        Assert.AreEqual("Updated context summary for XPath projection.", taskElem.Element("context")!.Element("summary")?.Value);
        Assert.AreEqual("Updated design snapshot with composable filters.", taskElem.Element("context")!.Element("design_snapshot")?.Value);

        // Appended record stamped with operation_id
        var verRec = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T060000Z-record-projection-verification");
        Assert.AreEqual("20260823T060000Z-update-combined", verRec.Attribute("operation_id")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_CompleteTransition_TerminalPredicatesEnforced()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. First move task to verification with criteria passed and resolved records
        var verifyReq = """
<task-update
  id="20260823T061000Z-update-verify"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T06:10:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filterout-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-composition" result="passed"/>
    <criterion target="20260823-taskaccept-result-limit" result="passed"/>
  </acceptance>
  <resolve-records>
    <record target="20260823T033000Z-record-projection-decision"/>
    <record target="20260823T040000Z-record-projection-attempt"/>
  </resolve-records>
  <records>
    <record
      id="20260823T061000Z-record-verify"
      kind="verification"
      status="informational"
      created_at="2026-08-23T06:10:00Z"
      actor="codex">
      <summary>Verification stage complete.</summary>
      <covers>
        <ref scope="document" target="20260823-taskaccept-filter-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filterout-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filter-composition" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-result-limit" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";

        var (vSuccess, _, vDiags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: verifyReq);
        Assert.IsTrue(vSuccess, string.Join("; ", vDiags.Select(d => d.Message)));

        // 2. Now complete the task with completion record
        var completeReq = """
<task-update
  id="20260823T062000Z-update-complete"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-23T06:20:00Z">
  <records>
    <record
      id="20260823T062000Z-record-complete"
      kind="completion"
      status="informational"
      created_at="2026-08-23T06:20:00Z"
      actor="codex">
      <summary>Completed XPath projection task.</summary>
      <covers>
        <ref scope="document" target="20260823-taskaccept-filter-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filterout-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filter-composition" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-result-limit" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";

        var (cSuccess, cEnv, cDiags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 10,
            requestXml: completeReq);

        Assert.IsTrue(cSuccess, string.Join("; ", cDiags.Select(d => d.Message)));
        Assert.AreEqual(11, cEnv!.Documents[0].Revision);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        Assert.AreEqual("done", taskElem.Attribute("status")?.Value);
        Assert.AreEqual("2026-08-23T06:20:00Z", taskElem.Attribute("completed_at")?.Value);
    }

    [TestMethod]
    public void TaskUpdate_DurableIdempotency_OriginalAndCurrentRevisionRetrySucceeds()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T063000Z-update-idempotent"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T06:30:00Z">
  <records>
    <record
      id="20260823T063000Z-record-idempotent"
      kind="start"
      status="informational"
      created_at="2026-08-23T06:30:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";

        // 1. Initial application (expectedRevision: 9 -> actual becomes 10)
        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: requestXml);

        Assert.IsTrue(s1, string.Join("; ", d1.Select(d => d.Message)));
        Assert.IsFalse(env1!.AlreadyApplied);
        Assert.AreEqual(10, env1.Documents[0].Revision);

        // 2. Retry with pre-commit expected revision (9)
        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: requestXml);

        Assert.IsTrue(s2, string.Join("; ", d2.Select(d => d.Message)));
        Assert.IsTrue(env2!.AlreadyApplied);
        Assert.AreEqual(10, env2.Documents[0].Revision);

        // 3. Retry with current revision (10)
        var (s3, env3, d3) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 10,
            requestXml: requestXml);

        Assert.IsTrue(s3, string.Join("; ", d3.Select(d => d.Message)));
        Assert.IsTrue(env3!.AlreadyApplied);
        Assert.AreEqual(10, env3.Documents[0].Revision);

        // 4. Retry with stale revision (e.g. 5) -> fails
        var (s4, _, d4) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 5,
            requestXml: requestXml);

        Assert.IsFalse(s4);
        Assert.IsTrue(d4.Any(d => d.Code == DiagnosticCodes.RevisionConflict));

        // 5. Retry with same operation ID but different record content -> fails
        var differentReqXml = """
<task-update
  id="20260823T063000Z-update-idempotent"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T06:30:00Z">
  <records>
    <record
      id="20260823T063000Z-record-idempotent"
      kind="start"
      status="informational"
      created_at="2026-08-23T06:30:00Z"
      actor="codex">
      <summary>DIFFERENT CONTENT</summary>
    </record>
  </records>
</task-update>
""";

        var (s5, _, d5) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 10,
            requestXml: differentReqXml);

        Assert.IsFalse(s5);
        Assert.IsTrue(d5.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void TaskUpdate_AntiSpoofing_GenericAppendRejectsOperationId()
    {
        var workspace = CreateWorkspaceCopy();
        var spoofFragment = """
<record
  id="20260823T064000Z-record-spoofed"
  kind="decision"
  status="informational"
  created_at="2026-08-23T06:40:00Z"
  actor="codex"
  operation_id="20260823T064000Z-update-spoofed">
  <summary>Attempting to spoof operation receipt via append.</summary>
</record>
""";

        var (success, _, diags) = GenericAppender.Append(
            workspace,
            "20260823-xpath-core/tasks.xml",
            "/tasks/task[@id='20260823-task-task-history']/records",
            expectedRevision: 9,
            fragmentXml: spoofFragment);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("operation_id")));
    }

    [TestMethod]
    public void TaskUpdate_CrossTaskOperationIdCollision_Rejected()
    {
        var workspace = CreateWorkspaceCopy();
        var req1 = """
<task-update
  id="20260823T065000Z-update-shared"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T06:50:00Z">
  <records>
    <record
      id="20260823T065000Z-record-t1"
      kind="start"
      status="informational"
      created_at="2026-08-23T06:50:00Z"
      actor="codex">
      <summary>Starting task history.</summary>
    </record>
  </records>
</task-update>
""";

        var (s1, _, _) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: req1);
        Assert.IsTrue(s1);

        // Now try using the exact same update ID on a different task
        var req2 = """
<task-update
  id="20260823T065000Z-update-shared"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T06:50:00Z">
  <records>
    <record
      id="20260823T065000Z-record-t2"
      kind="start"
      status="informational"
      created_at="2026-08-23T06:50:00Z"
      actor="codex">
      <summary>Starting atomic update task.</summary>
    </record>
  </records>
</task-update>
""";

        var (s2, _, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-atomic-update",
            expectedRevision: 10,
            requestXml: req2);

        Assert.IsFalse(s2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void TaskUpdate_SpecXmlByteIdenticalAfterTaskUpdate()
    {
        var workspace = CreateWorkspaceCopy();
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var beforeHash = SHA256.HashData(File.ReadAllBytes(specPath));

        var reqXml = """
<task-update
  id="20260823T070000Z-update-hash-check"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T07:00:00Z">
  <records>
    <record
      id="20260823T070000Z-record-hash-check"
      kind="start"
      status="informational"
      created_at="2026-08-23T07:00:00Z"
      actor="codex">
      <summary>Hash check update.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, _, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));

        var afterHash = SHA256.HashData(File.ReadAllBytes(specPath));
        CollectionAssert.AreEqual(beforeHash, afterHash, "spec.xml must be byte-identical after task update.");
    }

    [TestMethod]
    public void TaskUpdate_CrashRecovery_RecoversCleanlyOnInterruptedTransaction()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<task-update
  id="20260823T071000Z-update-fault"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T07:10:00Z">
  <records>
    <record
      id="20260823T071000Z-record-fault"
      kind="start"
      status="informational"
      created_at="2026-08-23T07:10:00Z"
      actor="codex">
      <summary>Fault injection record.</summary>
    </record>
  </records>
</task-update>
""";

        // Inject fault before publish
        var injector = new TestFaultInjector(FaultPhase.AfterStagingBeforeValidation);

        var (success, env, _) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: reqXml,
            faultInjector: injector);

        Assert.IsFalse(success, "Update must fail when fault is injected");
        Assert.IsNull(env);

        // Run recovery and verify workspace is healthy
        var (recSuccess, recErr) = StartupRecovery.Run(workspace);
        Assert.IsTrue(recSuccess, $"Recovery must succeed: {recErr?.Message}");
        Assert.IsNull(recErr);

        // Can apply normally afterwards
        var (s, finalEnv, _) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsTrue(s);
        Assert.AreEqual(10, finalEnv!.Documents[0].Revision);
    }

    [TestMethod]
    public void TaskUpdate_DuplicateAcceptanceTarget_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        var reqXml = """
<task-update
  id="20260823T072000Z-update-dup-accept"
  actor="codex"
  occurred_at="2026-08-23T07:20:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
  </acceptance>
  <records>
    <record
      id="20260823T072000Z-record-dup-accept"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:20:00Z"
      actor="codex">
      <summary>Testing duplicate acceptance target.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict && d.Message.Contains("Duplicate acceptance criterion target")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes, "Document must remain byte-identical on validation failure.");
    }

    [TestMethod]
    public void TaskUpdate_DuplicateResolveTarget_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        var reqXml = """
<task-update
  id="20260823T072100Z-update-dup-resolve"
  actor="codex"
  occurred_at="2026-08-23T07:21:00Z">
  <resolve-records>
    <record target="20260823T040000Z-record-projection-attempt"/>
    <record target="20260823T040000Z-record-projection-attempt"/>
  </resolve-records>
  <records>
    <record
      id="20260823T072100Z-record-dup-resolve"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:21:00Z"
      actor="codex">
      <summary>Testing duplicate resolve target.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict && d.Message.Contains("Duplicate resolve record target")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes, "Document must remain byte-identical on validation failure.");
    }

    [TestMethod]
    public void TaskUpdate_ResolveNonActiveRecord_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        // 20260823T031500Z-record-projection-start is informational in demo tasks.xml
        var reqXml = """
<task-update
  id="20260823T072200Z-update-bad-resolve"
  actor="codex"
  occurred_at="2026-08-23T07:22:00Z">
  <resolve-records>
    <record target="20260823T031500Z-record-projection-start"/>
  </resolve-records>
  <records>
    <record
      id="20260823T072200Z-record-bad-resolve"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:22:00Z"
      actor="codex">
      <summary>Attempting to resolve informational record.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict && d.Message.Contains("only 'active' records can be resolved")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes, "Document must remain byte-identical on validation failure.");
    }

    [TestMethod]
    public void TaskUpdate_ResolveActiveRecord_AndIdempotentRetry_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();

        // 20260823T040000Z-record-projection-attempt has status="active" in demo tasks.xml
        var reqXml = """
<task-update
  id="20260823T072300Z-update-resolve-active"
  actor="codex"
  occurred_at="2026-08-23T07:23:00Z">
  <resolve-records>
    <record target="20260823T040000Z-record-projection-attempt"/>
  </resolve-records>
  <records>
    <record
      id="20260823T072300Z-record-resolution"
      kind="resolution"
      status="informational"
      created_at="2026-08-23T07:23:00Z"
      actor="codex">
      <summary>Resolved attempt issue.</summary>
    </record>
  </records>
</task-update>
""";

        // 1. Initial execution
        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: reqXml);

        Assert.IsTrue(s1, string.Join("; ", d1.Select(d => d.Message)));
        Assert.IsFalse(env1!.AlreadyApplied);
        Assert.AreEqual(10, env1.Documents[0].Revision);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var targetTask = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        var resolvedRec = targetTask.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T040000Z-record-projection-attempt");
        Assert.AreEqual("resolved", resolvedRec.Attribute("status")?.Value);

        // 2. Retry of exact completed operation succeeds with already_applied="true" even though record is now resolved
        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 10,
            requestXml: reqXml);

        Assert.IsTrue(s2, string.Join("; ", d2.Select(d => d.Message)));
        Assert.IsTrue(env2!.AlreadyApplied);
    }

    [TestMethod]
    public void TaskUpdate_NonUtcOffsetOrNoZoneOccurredAt_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        // 1. Offset timezone +08:00
        var offsetReq = """
<task-update
  id="20260823T072400Z-update-offset"
  actor="codex"
  occurred_at="2026-08-23T07:24:00+08:00">
  <records>
    <record
      id="20260823T072400Z-record-offset"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:24:00Z"
      actor="codex">
      <summary>Non-UTC offset occurred_at.</summary>
    </record>
  </records>
</task-update>
""";

        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: offsetReq);

        Assert.IsFalse(s1);
        Assert.IsNull(env1);
        Assert.IsTrue(d1.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("ending with 'Z'")));

        // 2. Unzoned timestamp without Z
        var unzonedReq = """
<task-update
  id="20260823T072400Z-update-unzoned"
  actor="codex"
  occurred_at="2026-08-23T07:24:00">
  <records>
    <record
      id="20260823T072400Z-record-unzoned"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:24:00Z"
      actor="codex">
      <summary>Unzoned occurred_at.</summary>
    </record>
  </records>
</task-update>
""";

        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: unzonedReq);

        Assert.IsFalse(s2);
        Assert.IsNull(env2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("ending with 'Z'")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    [TestMethod]
    public void TaskUpdate_NonUtcRecordCreatedAt_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        var badRecordReq = """
<task-update
  id="20260823T072500Z-update-bad-rec-time"
  actor="codex"
  occurred_at="2026-08-23T07:25:00Z">
  <records>
    <record
      id="20260823T072500Z-record-bad-time"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:25:00+02:00"
      actor="codex">
      <summary>Record with non-UTC created_at.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: badRecordReq);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("@created_at")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    [TestMethod]
    public void TaskUpdate_BackdatedOccurredAt_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        // 20260823-task-task-history has created_at="2026-08-23T03:10:00Z", updated_at="2026-08-23T04:20:00Z"
        // Attempt update with occurred_at earlier than updated_at (04:00:00Z)
        var backdatedReq = """
<task-update
  id="20260823T040000Z-update-backdated"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T04:00:00Z">
  <records>
    <record
      id="20260823T040000Z-record-backdated"
      kind="start"
      status="informational"
      created_at="2026-08-23T04:00:00Z"
      actor="codex">
      <summary>Backdated start.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, env, diags) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: backdatedReq);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("cannot be earlier than")));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    [TestMethod]
    public void TaskUpdate_TamperedStartedAt_RetryFailsWithIdempotencyConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var startReq = """
<task-update
  id="20260823T072600Z-update-start-tamper"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T07:26:00Z">
  <records>
    <record
      id="20260823T072600Z-record-start-tamper"
      kind="start"
      status="informational"
      created_at="2026-08-23T07:26:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";

        // 1. Initial start succeeds
        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 9,
            requestXml: startReq);
        Assert.IsTrue(s1, string.Join("; ", d1.Select(d => d.Message)));

        // 2. Tamper started_at attribute in tasks.xml
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-task-history");
        taskElem.SetAttributeValue("started_at", "2026-08-23T01:00:00Z");
        xdoc.Save(tasksPath);

        // 3. Retry same operation -> fails with IdempotencyConflict because started_at was tampered
        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            expectedRevision: 10,
            requestXml: startReq);

        Assert.IsFalse(s2);
        Assert.IsNull(env2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict && d.Message.Contains("started_at")));
    }

    [TestMethod]
    public void TaskUpdate_MalformedXmlAndDtd_FailsBeforeMutation_BytePreserving()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        // 1. Malformed XML syntax
        var malformedXml = "<task-update id=\"20260823T072700Z-update-bad\" occurred_at=\"2026-08-23T07:27:00Z\"><unclosed>";
        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: malformedXml);

        Assert.IsFalse(s1);
        Assert.IsNull(env1);
        Assert.IsTrue(d1.Any(d => d.Code == DiagnosticCodes.XmlParseError));

        // 2. DTD prohibited
        var dtdXml = """
<?xml version="1.0"?>
<!DOCTYPE task-update [
  <!ENTITY xxe "test">
]>
<task-update id="20260823T072700Z-update-dtd" actor="codex" occurred_at="2026-08-23T07:27:00Z">
  <records>
    <record id="20260823T072700Z-record-dtd" kind="discussion" status="informational" created_at="2026-08-23T07:27:00Z" actor="codex">
      <summary>&xxe;</summary>
    </record>
  </records>
</task-update>
""";

        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: dtdXml);

        Assert.IsFalse(s2);
        Assert.IsNull(env2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.DtdProhibited || d.Code == DiagnosticCodes.XmlParseError));

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    [TestMethod]
    public void TaskUpdate_PartialReceiptConflict_FailsWithIdempotencyConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var multiRecReq = """
<task-update
  id="20260823T072800Z-update-partial"
  actor="codex"
  occurred_at="2026-08-23T07:28:00Z">
  <records>
    <record
      id="20260823T072800Z-record-p1"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:28:00Z"
      actor="codex">
      <summary>Record 1.</summary>
    </record>
    <record
      id="20260823T072800Z-record-p2"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T07:28:00Z"
      actor="codex">
      <summary>Record 2.</summary>
    </record>
  </records>
</task-update>
""";

        // 1. Initial application with 2 records
        var (s1, env1, d1) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 9,
            requestXml: multiRecReq);
        Assert.IsTrue(s1, string.Join("; ", d1.Select(d => d.Message)));

        // 2. Tamper tasks.xml by deleting one of the two stamped records (simulating partial receipt)
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        var p2Rec = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == "20260823T072800Z-record-p2");
        p2Rec.Remove();
        xdoc.Save(tasksPath);

        // 3. Retry -> detects record count mismatch / missing record
        var (s2, env2, d2) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-xpath-projection",
            expectedRevision: 10,
            requestXml: multiRecReq);

        Assert.IsFalse(s2);
        Assert.IsNull(env2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void TaskSemanticValidation_PendingTaskWithStartedAt_FailsSemanticValidation()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var pendingTask = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("status") == "pending");
        pendingTask.SetAttributeValue("started_at", "2026-08-23T03:10:00Z");
        xdoc.Save(tasksPath);

        var result = SchemaValidator.Validate(workspace);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskPendingHasStartedAt), "Pending task with started_at must fail semantic validation.");
    }

    [TestMethod]
    public void TaskUpdate_StatusTermSynchronization_AllTransitionsSynchronizeIndexTerm()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-task-history"; // Pending task

        // 1. Add status term "pending" to task
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        taskElem.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "pending")));
        xdoc.Save(tasksPath);

        // 2. Start transition: pending -> in-progress
        var startXml = """
<task-update
  id="20260823T050100Z-update-start-sync"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:01:00Z">
  <records>
    <record
      id="20260823T050100Z-rec-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:01:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";
        var (s1, env1, d1) = TaskUpdater.Update(workspace, iterId, taskId, 9, startXml);
        Assert.IsTrue(s1, string.Join("; ", d1.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        var statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.AreEqual("in-progress", (string?)statusTerm?.Attribute("value"));

        // 3. Block transition: in-progress -> blocked
        var blockXml = """
<task-update
  id="20260823T050200Z-update-block-sync"
  transition="block"
  actor="codex"
  occurred_at="2026-08-23T05:02:00Z">
  <records>
    <record
      id="20260823T050200Z-rec-block"
      kind="finding"
      status="active"
      created_at="2026-08-23T05:02:00Z"
      actor="codex">
      <summary>Blocked on external issue.</summary>
    </record>
  </records>
</task-update>
""";
        var (s2, env2, d2) = TaskUpdater.Update(workspace, iterId, taskId, 10, blockXml);
        Assert.IsTrue(s2, string.Join("; ", d2.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.AreEqual("blocked", (string?)statusTerm?.Attribute("value"));

        // 4. Resume transition: blocked -> in-progress
        var resumeXml = """
<task-update
  id="20260823T050300Z-update-resume-sync"
  transition="resume"
  actor="codex"
  occurred_at="2026-08-23T05:03:00Z">
  <resolve-records>
    <record target="20260823T050200Z-rec-block"/>
  </resolve-records>
  <records>
    <record
      id="20260823T050300Z-rec-resume"
      kind="resolution"
      status="informational"
      created_at="2026-08-23T05:03:00Z"
      actor="codex">
      <summary>Resuming task.</summary>
    </record>
  </records>
</task-update>
""";
        var (s3, env3, d3) = TaskUpdater.Update(workspace, iterId, taskId, 11, resumeXml);
        Assert.IsTrue(s3, string.Join("; ", d3.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.AreEqual("in-progress", (string?)statusTerm?.Attribute("value"));

        // 5. Verify transition: in-progress -> verification
        var verifyXml = """
<task-update
  id="20260823T050400Z-update-verify-sync"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:04:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-history-integrated" result="passed"/>
    <criterion target="20260823-taskaccept-history-reasoning" result="passed"/>
  </acceptance>
  <records>
    <record
      id="20260823T050400Z-rec-verify"
      kind="verification"
      status="informational"
      created_at="2026-08-23T05:04:00Z"
      actor="codex">
      <summary>Verification complete.</summary>
      <checks>
        <check kind="test" result="passed">
          <summary>Tests passed.</summary>
        </check>
      </checks>
      <covers>
        <ref scope="document" target="20260823-taskaccept-history-integrated" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-history-reasoning" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";
        var (s4, env4, d4) = TaskUpdater.Update(workspace, iterId, taskId, 12, verifyXml);
        Assert.IsTrue(s4, string.Join("; ", d4.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.AreEqual("verification", (string?)statusTerm?.Attribute("value"));

        // 6. Complete transition: verification -> done
        var completeXml = """
<task-update
  id="20260823T050500Z-update-complete-sync"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-23T05:05:00Z">
  <records>
    <record
      id="20260823T050500Z-rec-complete"
      kind="completion"
      status="informational"
      created_at="2026-08-23T05:05:00Z"
      actor="codex">
      <summary>Task completed.</summary>
      <covers>
        <ref scope="document" target="20260823-taskaccept-history-integrated" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-history-reasoning" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";
        var (s5, env5, d5) = TaskUpdater.Update(workspace, iterId, taskId, 13, completeXml);
        Assert.IsTrue(s5, string.Join("; ", d5.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.AreEqual("done", (string?)statusTerm?.Attribute("value"));
    }

    [TestMethod]
    public void TaskUpdate_StatusTermSynchronization_TransferTransition_SynchronizesIndexTerm()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-task-history";

        // Add status term "pending"
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        taskElem.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "pending")));
        xdoc.Save(tasksPath);

        var transferXml = """
<task-update
  id="20260823T050600Z-update-transfer-sync"
  transition="transfer"
  actor="codex"
  occurred_at="2026-08-23T05:06:00Z">
  <records>
    <record
      id="20260823T050600Z-rec-transfer"
      kind="handoff"
      status="informational"
      created_at="2026-08-23T05:06:00Z"
      actor="codex">
      <summary>Transferring task to another iteration.</summary>
    </record>
  </records>
</task-update>
""";
        var (s, env, d) = TaskUpdater.Update(workspace, iterId, taskId, 9, transferXml);
        Assert.IsTrue(s, string.Join("; ", d.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("transferred", (string?)taskElem.Attribute("status"));
        var statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.IsNotNull(statusTerm);
        Assert.AreEqual("transferred", (string?)statusTerm.Attribute("value"));

        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(diag => $"{diag.Code}: {diag.Message}")));
    }

    [TestMethod]
    public void TaskUpdate_StatusTermSynchronization_SupersedeTransition_SynchronizesIndexTerm()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-task-history";

        // Add status term "pending"
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        taskElem.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "pending")));
        xdoc.Save(tasksPath);

        var supersedeXml = """
<task-update
  id="20260823T050700Z-update-supersede-sync"
  transition="supersede"
  actor="codex"
  occurred_at="2026-08-23T05:07:00Z">
  <records>
    <record
      id="20260823T050700Z-rec-supersede"
      kind="decision"
      status="informational"
      created_at="2026-08-23T05:07:00Z"
      actor="codex">
      <summary>Task superseded by updated design.</summary>
    </record>
  </records>
</task-update>
""";
        var (s, env, d) = TaskUpdater.Update(workspace, iterId, taskId, 9, supersedeXml);
        Assert.IsTrue(s, string.Join("; ", d.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("superseded", (string?)taskElem.Attribute("status"));
        var statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.IsNotNull(statusTerm);
        Assert.AreEqual("superseded", (string?)statusTerm.Attribute("value"));

        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(diag => $"{diag.Code}: {diag.Message}")));
    }

    [TestMethod]
    public void TaskUpdate_StatusTermSynchronization_CancelTransition_SynchronizesIndexTerm()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-task-history";

        // Add status term "pending"
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        taskElem.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "pending")));
        xdoc.Save(tasksPath);

        var cancelXml = """
<task-update
  id="20260823T050800Z-update-cancel-sync"
  transition="cancel"
  actor="codex"
  occurred_at="2026-08-23T05:08:00Z">
  <records>
    <record
      id="20260823T050800Z-rec-cancel"
      kind="decision"
      status="informational"
      created_at="2026-08-23T05:08:00Z"
      actor="codex">
      <summary>Cancelling task work.</summary>
    </record>
  </records>
</task-update>
""";
        var (s, env, d) = TaskUpdater.Update(workspace, iterId, taskId, 9, cancelXml);
        Assert.IsTrue(s, string.Join("; ", d.Select(d => d.Message)));
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("cancelled", (string?)taskElem.Attribute("status"));
        var statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.IsNotNull(statusTerm);
        Assert.AreEqual("cancelled", (string?)statusTerm.Attribute("value"));

        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(diag => $"{diag.Code}: {diag.Message}")));
    }

    [TestMethod]
    public void TaskUpdate_ZeroStatusTerm_DoesNotInventStatusTerm()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-task-history";

        // Task initially has NO status term in demo
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        var statusTerm = taskElem.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        statusTerm?.Remove();
        xdoc.Save(tasksPath);

        // Start task
        var startXml = """
<task-update
  id="20260823T051000Z-update-nostatus"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:10:00Z">
  <records>
    <record
      id="20260823T051000Z-rec-nostatus"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:10:00Z"
      actor="codex">
      <summary>Starting task without status term.</summary>
    </record>
  </records>
</task-update>
""";
        var (s, env, d) = TaskUpdater.Update(workspace, iterId, taskId, 9, startXml);
        Assert.IsTrue(s, string.Join("; ", d.Select(d => d.Message)));

        // Verify task status is in-progress and NO status term was invented
        xdoc = XDocument.Load(tasksPath);
        taskElem = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
        var terms = taskElem.Element("index")?.Elements("term").Where(t => (string?)t.Attribute("key") == "status").ToList();
        Assert.IsNotNull(terms);
        Assert.AreEqual(0, terms.Count);
    }

    [TestMethod]
    public void TaskSemanticValidation_DuplicateStatusTerms_FailsWithAmbiguousReference()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Root!.Elements("task").First();
        var indexElem = taskElem.Element("index")!;
        indexElem.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "done")));
        indexElem.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "done")));
        xdoc.Save(tasksPath);

        var result = SchemaValidator.Validate(workspace);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.AmbiguousReference), "Duplicate status terms must fail with AmbiguousReference.");
    }

    [TestMethod]
    public void TaskSemanticValidation_StaleStatusTerm_FailsWithTaskTransitionConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var inProgressTask = xdoc.Root!.Elements("task").First(t => (string?)t.Attribute("status") == "in-progress");
        var indexElem = inProgressTask.Element("index")!;
        indexElem.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "pending"))); // Stale value
        xdoc.Save(tasksPath);

        var result = SchemaValidator.Validate(workspace);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict), "Stale status term on task must fail with TaskTransitionConflict.");
    }

    [TestMethod]
    public void TaskUpdate_TemplateTaskUpdateExecution_WithCovers_SucceedsAndPassesValidation()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection"; // in-progress task

        // 1. Move to verification first
        var verifyXml = """
<task-update
  id="20260823T052000Z-update-verify-tpl"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-23T05:20:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filterout-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-composition" result="passed"/>
    <criterion target="20260823-taskaccept-result-limit" result="passed"/>
  </acceptance>
  <records>
    <record
      id="20260823T052000Z-rec-verify-tpl"
      kind="verification"
      status="informational"
      created_at="2026-08-23T05:20:00Z"
      actor="codex">
      <summary>Verified implementation.</summary>
      <checks>
        <check kind="test" result="passed"><summary>Tests pass</summary></check>
      </checks>
      <covers>
        <ref scope="document" target="20260823-taskaccept-filter-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filterout-members" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-filter-composition" relation="covers"/>
        <ref scope="document" target="20260823-taskaccept-result-limit" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";
        var (vOk, _, vDiags) = TaskUpdater.Update(workspace, iterId, taskId, 9, verifyXml);
        Assert.IsTrue(vOk, string.Join("; ", vDiags.Select(d => d.Message)));

        // 2. Read template file from templates/v1/task.update.xml
        var tplPath = Path.Combine(RepoRoot, "templates", "v1", "task.update.xml");
        var tplText = File.ReadAllText(tplPath);

        // 3. Perform placeholder substitution
        var nowTimestamp = "2026-08-23T05:21:00Z";
        var requestXml = tplText
            .Replace("20000101T000000Z-update-replace-task", "20260823T052100Z-update-tpl-complete")
            .Replace("replace-actor", "codex")
            .Replace("2000-01-01T00:00:00Z", nowTimestamp)
            .Replace("20000101T000000Z-record-replace-completion", "20260823T052100Z-record-tpl-complete")
            .Replace("20000101-taskaccept-replace", "20260823-taskaccept-filter-members")
            .Replace("Replace with the completed technical outcome.", "Implemented projection functions.");

        // Add covers refs for all remaining criteria
        requestXml = requestXml.Replace(
            "<ref scope=\"document\" target=\"20260823-taskaccept-filter-members\" relation=\"covers\"/>",
            "<ref scope=\"document\" target=\"20260823-taskaccept-filter-members\" relation=\"covers\"/>\n        <ref scope=\"document\" target=\"20260823-taskaccept-filterout-members\" relation=\"covers\"/>\n        <ref scope=\"document\" target=\"20260823-taskaccept-filter-composition\" relation=\"covers\"/>\n        <ref scope=\"document\" target=\"20260823-taskaccept-result-limit\" relation=\"covers\"/>");

        // Remove the optional acceptance block since criteria were passed in verify
        var requestDoc = XDocument.Parse(requestXml);
        requestDoc.Root!.Element("acceptance")?.Remove();
        requestXml = requestDoc.ToString();

        // 4. Execute TaskUpdater.Update
        var (cOk, cEnv, cDiags) = TaskUpdater.Update(workspace, iterId, taskId, 10, requestXml);
        Assert.IsTrue(cOk, string.Join("; ", cDiags.Select(d => d.Message)));
        Assert.IsNotNull(cEnv);

        // 5. Full workspace validation must pass
        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }
}
