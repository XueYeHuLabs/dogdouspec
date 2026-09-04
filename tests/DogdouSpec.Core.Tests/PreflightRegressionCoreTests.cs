using System.Security.Cryptography;
using System.Xml.Linq;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class PreflightRegressionCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_PreflightRegression_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void TaskMutationPreflight_UsesOutOfBandTargetInsteadOfOperationId()
    {
        var workspace = CreateWorkspaceCopy();
        const string iterationId = "20260823-xpath-core";
        const string taskId = "20260823-task-task-history";

        var requests = new Dictionary<string, string>
        {
            ["task-update"] = """
<task-update id="20260904T150000Z-preflight-update" transition="start" actor="test" occurred_at="2026-09-04T15:00:00Z">
  <records><record id="20260904T150000Z-record-update" kind="start" status="informational" created_at="2026-09-04T15:00:00Z" actor="test"><summary>Start through preflight.</summary></record></records>
</task-update>
""",
            ["task-revise"] = """
<task-revise id="20260904T150100Z-preflight-revise" actor="test" occurred_at="2026-09-04T15:01:00Z">
  <add_constraints><constraint id="20260904-constraint-preflight">Exercise target resolution.</constraint></add_constraints>
  <records><record id="20260904T150100Z-record-revise" kind="discussion" status="informational" created_at="2026-09-04T15:01:00Z" actor="test"><summary>Revise through preflight.</summary></record></records>
</task-revise>
""",
            ["task-review"] = """
<task-review id="20260904T150200Z-preflight-review" actor="independent-reviewer" occurred_at="2026-09-04T15:02:00Z">
  <submission id="20260904T150200Z-submission-review" disposition="approved"><summary>Review through preflight.</summary></submission>
</task-review>
""",
            ["task-split"] = BuildSplitRequest(workspace, taskId)
        };

        foreach (var (requestType, requestXml) in requests)
        {
            var (missingSuccess, _, missingDiagnostics) = MutationPreflight.Preflight(
                workspace,
                requestXml,
                iterationId,
                taskId: null,
                expectedRevision: 9);
            Assert.IsFalse(missingSuccess, $"{requestType} must require its out-of-band target task.");
            Assert.IsTrue(
                missingDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("--task argument is required", StringComparison.Ordinal)),
                $"{requestType} missing-target diagnostics: {string.Join("; ", missingDiagnostics.Select(d => d.Message))}");

            var (_, _, targetedDiagnostics) = MutationPreflight.Preflight(
                workspace,
                requestXml,
                iterationId,
                taskId,
                expectedRevision: 9);
            Assert.IsFalse(
                targetedDiagnostics.Any(d => d.Message.Contains("disagrees with request attribute", StringComparison.Ordinal)),
                $"{requestType} incorrectly compared operation ID with task ID: {string.Join("; ", targetedDiagnostics.Select(d => d.Message))}");
            Assert.IsFalse(
                targetedDiagnostics.Any(d => d.Message.Contains("--task argument is required", StringComparison.Ordinal)),
                $"{requestType} ignored the supplied target task.");
        }
    }

    [TestMethod]
    public void RevisionAndConfirmationResolution_FillsOrAssertsAndFailsClosed()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var originalTasks = File.ReadAllText(tasksPath);

        File.WriteAllText(tasksPath, originalTasks.Replace("revision=\"9\"", "revision=\"malformed\"", StringComparison.Ordinal));
        var (explicitSuccess, explicitRevision, explicitError) = DocumentRevisionResolver.ResolveExpectedRevision(
            workspace,
            "20260823-xpath-core/tasks.xml",
            42);
        Assert.IsTrue(explicitSuccess, explicitError?.Message);
        Assert.AreEqual(42, explicitRevision);

        var (automaticSuccess, _, automaticError) = DocumentRevisionResolver.ResolveExpectedRevision(
            workspace,
            "20260823-xpath-core/tasks.xml",
            null);
        Assert.IsFalse(automaticSuccess);
        Assert.AreEqual(DiagnosticCodes.XmlParseError, automaticError?.Code);

        File.WriteAllText(tasksPath, originalTasks);
        var request = XDocument.Parse("""
<iteration-confirmation id="20260904T151000Z-confirm-resolve" action="complete" actor="owner" decided_at="2026-09-04T15:10:00Z">
  <summary>Resolve omitted confirmation addressing.</summary>
</iteration-confirmation>
""");
        var (resolved, resolvedXml, resolveError) = IterationConfirmationRequestResolver.Reconcile(workspace, request);
        Assert.IsTrue(resolved, resolveError?.Message);
        var resolvedRequest = XDocument.Parse(resolvedXml!);
        Assert.AreEqual("20260823-xpath-core", resolvedRequest.Root!.Attribute("iteration")?.Value);
        Assert.AreEqual("4", resolvedRequest.Root.Attribute("expected_spec_revision")?.Value);
        Assert.AreEqual("9", resolvedRequest.Root.Attribute("expected_tasks_revision")?.Value);

        var mismatchRequest = XDocument.Parse(resolvedXml!);
        var (mismatchSuccess, _, mismatchError) = IterationConfirmationRequestResolver.Reconcile(
            workspace,
            mismatchRequest,
            explicitSpecRevision: 99);
        Assert.IsFalse(mismatchSuccess);
        StringAssert.Contains(mismatchError!.Message, "disagrees with request attribute");
    }

    [TestMethod]
    public void TaskPreflight_WithoutIteration_FindsUniqueTaskInNonActiveIteration()
    {
        var workspace = CreateWorkspaceCopy();
        const string iterationId = "20260904-draft-preflight-target";
        const string taskId = "20260904-task-draft-preflight-target";

        var (createSuccess, _, createDiagnostics) = IterationCreator.Create(workspace, iterationId, "feature");
        Assert.IsTrue(createSuccess, string.Join("; ", createDiagnostics.Select(d => d.Message)));

        var quickInput = new QuickTaskInput(
            "Draft preflight target",
            new List<string> { "src/**" },
            "draft target is uniquely located",
            "verify omitted iteration addressing",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            iterationId,
            1,
            false,
            false,
            taskId,
            "20260904T152000Z-quick-draft-preflight-target");
        var (quickSuccess, _, _, quickDiagnostics) = TaskQuick.Create(workspace, quickInput);
        Assert.IsTrue(quickSuccess, string.Join("; ", quickDiagnostics.Select(d => d.Message)));

        var reviseRequest = """
<task-revise id="20260904T152100Z-revise-draft-preflight-target" actor="test" occurred_at="2026-09-04T15:21:00Z">
  <add_constraints><constraint id="20260904-constraint-draft-preflight-target">Keep auto-location unique.</constraint></add_constraints>
  <records>
    <record id="20260904T152100Z-record-draft-preflight-target" kind="discussion" status="informational" created_at="2026-09-04T15:21:00Z" actor="test">
      <summary>Exercise omitted iteration auto-location.</summary>
    </record>
  </records>
</task-revise>
""";
        var (success, result, diagnostics) = MutationPreflight.Preflight(
            workspace,
            reviseRequest,
            iterationId: null,
            taskId: taskId);

        Assert.IsTrue(success, string.Join("; ", diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(result);
        Assert.AreEqual(iterationId, result.IterationId);
        Assert.AreEqual(taskId, result.TaskId);
    }

    [TestMethod]
    public void ChangePreflight_PreservesExplicitStaleTasksRevision()
    {
        var workspace = CreateWorkspaceCopy();
        const string iterationId = "20260823-xpath-core";
        var proposeRequest = """
<change-propose id="20260904T161000Z-change-preflight-stale" actor="test" occurred_at="2026-09-04T16:10:00Z">
  <summary>Verify explicit tasks revision authority.</summary>
  <finding_record task="20260823-task-task-history">
    <record id="20260904T161000Z-finding-preflight-stale" kind="finding" status="active" created_at="2026-09-04T16:10:00Z" actor="test">
      <summary>Revision preflight finding.</summary>
    </record>
  </finding_record>
  <freeze_tasks>
    <task target="20260823-task-task-history" reason="Exercise explicit stale tasks revision."/>
  </freeze_tasks>
  <proposed_requirements>
    <requirement id="20260904-req-preflight-stale" status="proposed">
      <index><summary>Explicit stale revision requirement.</summary></index>
      <statement>Preflight must preserve an explicit tasks revision.</statement>
      <rationale>Silent replacement hides caller conflicts.</rationale>
    </requirement>
  </proposed_requirements>
</change-propose>
""";

        var (proposeSuccess, _, proposeDiagnostics) = MutationPreflight.Preflight(
            workspace,
            proposeRequest,
            iterationId,
            expectedRevision: 4,
            expectedTasksRevision: 999);
        Assert.IsFalse(proposeSuccess);
        Assert.IsTrue(proposeDiagnostics.Any(d =>
            d.Code == DiagnosticCodes.RevisionConflict &&
            d.ExpectedRevision == 999 &&
            d.ActualRevision == 9));

        var specPath = Path.Combine(workspace, iterationId, "spec.xml");
        var spec = XDocument.Load(specPath);
        spec.Root!.SetAttributeValue("status", "replanning");
        spec.Save(specPath);
        var applyRequest = """
<change-apply id="20260904T161100Z-change-apply-preflight-stale" actor="test" occurred_at="2026-09-04T16:11:00Z">
  <summary>Verify explicit apply tasks revision authority.</summary>
  <task_dispositions>
    <task target="20260823-task-task-history" transition="cancel" rationale="Exercise explicit stale tasks revision."/>
  </task_dispositions>
</change-apply>
""";

        var (applySuccess, _, applyDiagnostics) = MutationPreflight.Preflight(
            workspace,
            applyRequest,
            iterationId,
            expectedRevision: 4,
            expectedTasksRevision: 999);
        Assert.IsFalse(applySuccess);
        Assert.IsTrue(applyDiagnostics.Any(d =>
            d.Code == DiagnosticCodes.RevisionConflict &&
            d.ExpectedRevision == 999 &&
            d.ActualRevision == 9));
    }

    [TestMethod]
    public void IdempotentTaskDryRun_RejectsPendingRecoveryState()
    {
        var workspace = CreateWorkspaceCopy();
        var request = """
<task-update id="20260904T153000Z-update-recovery-replay" transition="start" actor="test" occurred_at="2026-09-04T15:30:00Z">
  <records>
    <record id="20260904T153000Z-record-recovery-replay" kind="start" status="informational" created_at="2026-09-04T15:30:00Z" actor="test">
      <summary>Exercise dry-run replay recovery gate.</summary>
    </record>
  </records>
</task-update>
""";

        var (initialSuccess, _, initialDiagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            request);
        Assert.IsTrue(initialSuccess, string.Join("; ", initialDiagnostics.Select(d => d.Message)));

        var pendingDirectory = Path.Combine(workspace, "_tmp", "tx_pending_replay", "staged");
        Directory.CreateDirectory(pendingDirectory);
        var (replaySuccess, _, replayDiagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            request,
            dryRun: true);

        Assert.IsFalse(replaySuccess);
        Assert.IsTrue(replayDiagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed));
        Assert.IsTrue(Directory.Exists(pendingDirectory), "Dry-run recovery inspection must not mutate pending artifacts.");
    }

    [TestMethod]
    public void AllDryRunMutationFamilies_RejectPendingRecoveryBeforeSemanticShortcuts()
    {
        var workspace = CreateWorkspaceCopy();
        var pendingDirectory = Path.Combine(workspace, "_tmp", "tx_pending_all_families", "staged");
        Directory.CreateDirectory(pendingDirectory);

        static void AssertRecoveryBlocked(
            (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) result,
            string family)
        {
            Assert.IsFalse(result.Success, $"{family} dry-run must fail closed while recovery is pending.");
            Assert.IsTrue(
                result.Diagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed),
                $"{family} diagnostics: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        }

        AssertRecoveryBlocked(
            BacklogLifecycle.Schedule(
                workspace,
                1,
                new BacklogTransitionInput(
                    "20260904-backlog-recovery-gate",
                    "20260904T155000Z-backlog-recovery-gate",
                    "test",
                    new DateTimeOffset(2026, 9, 4, 15, 50, 0, TimeSpan.Zero),
                    null),
                dryRun: true),
            "backlog");
        AssertRecoveryBlocked(
            RequirementProposer.Propose(
                workspace,
                "20260823-xpath-core",
                4,
                "<invalid/>",
                dryRun: true),
            "requirement propose");
        AssertRecoveryBlocked(
            ChangeProposer.Propose(
                workspace,
                "20260823-xpath-core",
                4,
                9,
                "<invalid/>",
                dryRun: true),
            "change propose");
        AssertRecoveryBlocked(
            ChangeApplier.Apply(
                workspace,
                "20260823-xpath-core",
                4,
                9,
                "<invalid/>",
                dryRun: true),
            "change apply");
        AssertRecoveryBlocked(
            IterationConfirmer.Confirm(workspace, "<invalid/>", dryRun: true),
            "iteration confirm");

        Assert.IsTrue(Directory.Exists(pendingDirectory), "Recovery inspection must remain read-only.");
    }

    [TestMethod]
    public void CommitDryRun_IsZeroWriteAndRejectsPendingRecoveryState()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksRelativePath = "20260823-xpath-core/tasks.xml";
        var specRelativePath = "20260823-xpath-core/spec.xml";
        var tasksPath = Path.Combine(workspace, tasksRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalTasks = File.ReadAllText(tasksPath);
        var replacement = originalTasks.Replace("revision=\"9\"", "revision=\"10\"", StringComparison.Ordinal);
        var beforeHashes = HashManagedDocuments(workspace);
        var tmpDir = Path.Combine(workspace, "_tmp");
        if (Directory.Exists(tmpDir))
        {
            Directory.Delete(tmpDir, true);
        }

        var operation = new TransactionDocumentOperation(tasksRelativePath, replacement, 9, 10);
        var (success, envelope, diagnostics) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "dry-run regression",
            new[] { operation },
            readPreconditions: new[] { new TransactionReadPrecondition(specRelativePath, 4) },
            dryRun: true);
        Assert.IsTrue(success, string.Join("; ", diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(envelope);
        CollectionAssert.AreEquivalent(beforeHashes, HashManagedDocuments(workspace));
        Assert.IsFalse(File.Exists(Path.Combine(tmpDir, "writer.lock")));
        Assert.IsFalse(Directory.Exists(tmpDir), "Dry-run must not create _tmp or staging state.");

        var (staleSuccess, _, staleDiagnostics) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "dry-run stale read",
            new[] { operation },
            readPreconditions: new[] { new TransactionReadPrecondition(specRelativePath, 999) },
            dryRun: true);
        Assert.IsFalse(staleSuccess);
        Assert.IsTrue(staleDiagnostics.Any(d => d.Code == DiagnosticCodes.RevisionConflict));

        var pendingDirectory = Path.Combine(tmpDir, "tx_pending_preview", "staged");
        Directory.CreateDirectory(pendingDirectory);
        var (pendingSuccess, _, pendingDiagnostics) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "dry-run pending recovery",
            new[] { operation },
            dryRun: true);
        Assert.IsFalse(pendingSuccess);
        Assert.IsTrue(pendingDiagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed));
        Assert.IsTrue(Directory.Exists(pendingDirectory), "Pending-state inspection must be read-only.");

        var noOpRequest = """
<transaction operation_id="20260904T154000Z-transaction-noop-recovery">
  <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
    <assert test="count(/tasks/task) &gt; 0"/>
  </document>
</transaction>
""";
        var (noOpSuccess, _, noOpDiagnostics) = TransactionApplier.Apply(
            workspace,
            noOpRequest,
            dryRun: true);
        Assert.IsFalse(noOpSuccess, "A semantic no-op must not bypass the dry-run recovery gate.");
        Assert.IsTrue(noOpDiagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed));

        CollectionAssert.AreEquivalent(beforeHashes, HashManagedDocuments(workspace));
    }

    private string CreateWorkspaceCopy()
    {
        var source = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destination = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(source, destination);
        return destination;
    }

    private static string BuildSplitRequest(string workspace, string taskId)
    {
        var tasks = XDocument.Load(Path.Combine(workspace, "20260823-xpath-core", "tasks.xml"));
        var source = tasks.Root!.Elements("task").Single(t => (string?)t.Attribute("id") == taskId);

        XElement CreateSubtask(string id, string criterionId)
        {
            var subtask = new XElement(source);
            subtask.SetAttributeValue("id", id);
            subtask.SetAttributeValue("status", "pending");
            subtask.SetAttributeValue("created_at", "2026-09-04T15:03:00Z");
            subtask.SetAttributeValue("updated_at", "2026-09-04T15:03:00Z");
            subtask.Attribute("started_at")?.Remove();
            subtask.Attribute("completed_at")?.Remove();
            subtask.Element("records")?.ReplaceWith(new XElement("records"));
            var criterion = subtask.Element("acceptance")!.Elements("criterion").First();
            criterion.SetAttributeValue("id", criterionId);
            criterion.SetAttributeValue("status", "pending");
            return subtask;
        }

        var request = new XElement(
            "task-split",
            new XAttribute("id", "20260904T150300Z-preflight-split"),
            new XAttribute("actor", "test"),
            new XAttribute("occurred_at", "2026-09-04T15:03:00Z"),
            new XElement(
                "parent_disposition",
                new XAttribute("transition", "supersede"),
                new XAttribute("rationale", "Split for preflight target resolution."),
                new XElement(
                    "record",
                    new XAttribute("id", "20260904T150300Z-record-split"),
                    new XAttribute("kind", "discussion"),
                    new XAttribute("status", "informational"),
                    new XAttribute("created_at", "2026-09-04T15:03:00Z"),
                    new XAttribute("actor", "test"),
                    new XElement("summary", "Split through preflight."))),
            new XElement(
                "subtasks",
                CreateSubtask("20260904-task-preflight-split-a", "20260904-criterion-preflight-split-a"),
                CreateSubtask("20260904-task-preflight-split-b", "20260904-criterion-preflight-split-b")));
        return request.ToString(SaveOptions.DisableFormatting);
    }

    private static string[] HashManagedDocuments(string workspace)
    {
        return Directory.GetFiles(workspace, "*.xml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "_tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(workspace, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }
}
