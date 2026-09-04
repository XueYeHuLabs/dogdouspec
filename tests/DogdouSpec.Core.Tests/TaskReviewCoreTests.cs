using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskReviewCoreTests
{
    private const string IterationId = "20260823-xpath-core";
    private const string TaskId = "20260823-task-xpath-projection";
    private const string Implementer = "implementation-agent";
    private static string RepoRoot = null!;
    private string _workspace = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")))
                {
                    RepoRoot = current.FullName;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(RepoRoot)) break;
        }
        Assert.IsFalse(string.IsNullOrEmpty(RepoRoot));
    }

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "dogdouspec-review-core-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec"), _workspace);
        PrepareReviewTask();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    [TestMethod]
    public void ReviewRequired_MissingOrSameActorApprovalBlocks_IndependentApprovalAllowsCompletion()
    {
        var revision = CurrentRevision();
        var (missingSuccess, _, missingDiagnostics) = TaskUpdater.Update(
            _workspace, IterationId, TaskId, revision, CompletionRequest("20260825T100000Z-complete-without-review"));
        Assert.IsFalse(missingSuccess);
        Assert.IsTrue(missingDiagnostics.Any(d => d.Code == DiagnosticCodes.TaskReviewRequired), Join(missingDiagnostics));

        var sameActor = ApprovalRequest("20260825T100100Z-review-same-actor", Implementer);
        var (sameSuccess, _, sameDiagnostics) = TaskReviewer.Submit(_workspace, IterationId, TaskId, revision, sameActor);
        Assert.IsFalse(sameSuccess);
        Assert.IsTrue(sameDiagnostics.Any(d => d.Code == DiagnosticCodes.TaskReviewActorConflict));

        var approval = ApprovalRequest("20260825T100200Z-review-approved", "independent-reviewer");
        var (approved, approvalEnvelope, approvalDiagnostics) = TaskReviewer.Submit(_workspace, IterationId, TaskId, revision, approval);
        Assert.IsTrue(approved, Join(approvalDiagnostics));
        Assert.AreEqual(revision + 1, approvalEnvelope!.Documents.Single().Revision);

        var (replayed, replayEnvelope, replayDiagnostics) = TaskReviewer.Submit(_workspace, IterationId, TaskId, revision, approval);
        Assert.IsTrue(replayed, Join(replayDiagnostics));
        Assert.IsTrue(replayEnvelope!.AlreadyApplied);

        var reorderedApproval = approval
            .Replace("id=\"20260825T100200Z-review-approved\" actor=\"independent-reviewer\" occurred_at=\"2026-08-25T10:02:00Z\"",
                "occurred_at=\"2026-08-25T10:02:00Z\" actor=\"independent-reviewer\" id=\"20260825T100200Z-review-approved\"", StringComparison.Ordinal)
            .Replace("id=\"20260825T100200Z-review-approved-submission\" disposition=\"approved\"",
                "disposition=\"approved\" id=\"20260825T100200Z-review-approved-submission\"", StringComparison.Ordinal);
        var (canonicalReplay, canonicalEnvelope, canonicalDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, revision, reorderedApproval);
        Assert.IsTrue(canonicalReplay, Join(canonicalDiagnostics));
        Assert.IsTrue(canonicalEnvelope!.AlreadyApplied);

        var reviewTasksPath = TasksPath();
        var reviewTasksBefore = File.ReadAllBytes(reviewTasksPath);
        var pendingRoot = Path.Combine(_workspace, "_tmp", "tx_pending_task_review_replay");
        var pendingDirectory = Path.Combine(pendingRoot, "staged");
        Directory.CreateDirectory(pendingDirectory);
        var (blockedReplay, _, blockedDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, revision, approval, dryRun: true);
        Assert.IsFalse(blockedReplay);
        Assert.IsTrue(blockedDiagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed), Join(blockedDiagnostics));
        Assert.IsTrue(Directory.Exists(pendingDirectory), "Task-review dry-run must preserve pending recovery artifacts.");
        CollectionAssert.AreEqual(reviewTasksBefore, File.ReadAllBytes(reviewTasksPath));
        Directory.Delete(pendingRoot, recursive: true);

        var (completed, _, completeDiagnostics) = TaskUpdater.Update(
            _workspace, IterationId, TaskId, revision + 1, CompletionRequest("20260825T100300Z-complete-after-review"));
        Assert.IsTrue(completed, Join(completeDiagnostics));
        Assert.AreEqual("done", (string?)LoadTask().Attribute("status"));
    }

    [TestMethod]
    public void ChangesRequested_CreatesBlockingFindingAndRequiresFreshApproval()
    {
        var revision = CurrentRevision();
        var changes = ChangesRequest("20260825T101000Z-review-changes", "20260825T101000Z-finding-review");
        var (changed, _, changeDiagnostics) = TaskReviewer.Submit(_workspace, IterationId, TaskId, revision, changes);
        Assert.IsTrue(changed, Join(changeDiagnostics));
        var task = LoadTask();
        Assert.AreEqual("in-progress", (string?)task.Attribute("status"));
        Assert.IsTrue(task.Element("records")!.Elements("record").Any(r =>
            (string?)r.Attribute("id") == "20260825T101000Z-finding-review" &&
            (string?)r.Attribute("status") == "active"));

        var correction = $"""
<task-update id="20260825T101100Z-correct-review" transition="verify" actor="implementation-agent" occurred_at="2026-08-25T10:11:00Z">
  <resolve-records><record target="20260825T101000Z-finding-review"/></resolve-records>
  <records>
    <record id="20260825T101100Z-record-correct-review" kind="verification" status="informational" created_at="2026-08-25T10:11:00Z" actor="implementation-agent" operation_id="20260825T101100Z-correct-review">
      <summary>Requested review correction was applied.</summary>
    </record>
  </records>
</task-update>
""";
        var (corrected, _, correctionDiagnostics) = TaskUpdater.Update(
            _workspace, IterationId, TaskId, revision + 1, correction);
        Assert.IsTrue(corrected, Join(correctionDiagnostics));

        var approval = ApprovalRequest("20260825T101200Z-review-approved-fresh", "independent-reviewer")
            .Replace("2026-08-25T10:02:00Z", "2026-08-25T10:12:00Z", StringComparison.Ordinal);
        var (approved, _, approvalDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, revision + 2, approval);
        Assert.IsTrue(approved, Join(approvalDiagnostics));
        Assert.IsTrue(TaskReviewGate.Evaluate(LoadTask()).Satisfied);
    }

    [TestMethod]
    public void CompletionReadiness_ReportsMissingReviewGate()
    {
        var path = TasksPath();
        var document = XDocument.Load(path);
        var task = FindTask(document);
        task.SetAttributeValue("status", "done");
        task.SetAttributeValue("completed_at", "2026-08-25T10:20:00Z");
        task.Element("index")!.Elements("term")
            .Single(t => (string?)t.Attribute("key") == "status")
            .SetAttributeValue("value", "done");
        var covers = new XElement("covers",
            task.Element("acceptance")!.Elements("criterion").Select(c => new XElement("ref",
                new XAttribute("scope", "document"),
                new XAttribute("target", c.Attribute("id")!.Value),
                new XAttribute("relation", "covers"))));
        task.Element("records")!.Add(new XElement("record",
            new XAttribute("id", "20260825T102000Z-record-readiness-completion"),
            new XAttribute("kind", "completion"), new XAttribute("status", "informational"),
            new XAttribute("created_at", "2026-08-25T10:20:00Z"), new XAttribute("actor", Implementer),
            new XElement("summary", "Readiness review gate fixture."), covers));
        document.Save(path);

        var (success, result, diagnostics) = IterationReadiness.Assess(_workspace, IterationId, "completion");
        Assert.IsTrue(success, Join(diagnostics));
        var gate = result!.TechnicalChecks.Single(c => c.Name == "review_gates");
        Assert.AreEqual("failed", gate.Result);
        StringAssert.Contains(gate.Message, "Latest structured review submission is not approved");
    }

    [TestMethod]
    public void LegacyTaskWithoutReview_RemainsSatisfied()
    {
        var task = LoadTask();
        task.Element("review")!.Remove();
        var evaluation = TaskReviewGate.Evaluate(task);
        Assert.IsFalse(evaluation.Required);
        Assert.IsTrue(evaluation.Satisfied);
    }

    [TestMethod]
    public void ReviewRequiredWithoutAgent_IsSemanticallyInvalid()
    {
        var document = XDocument.Load(TasksPath());
        FindTask(document).Attribute("agent")!.Remove();
        document.Save(TasksPath());

        var result = SchemaValidator.Validate(_workspace, iterationId: IterationId);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskReviewImplementerUnknown), Join(result.Diagnostics));
    }

    [TestMethod]
    public void LowLevelMutation_CannotChangeImplementerAttribution()
    {
        var before = XDocument.Load(TasksPath());
        var after = new XDocument(before);
        FindTask(after).SetAttributeValue("agent", "different-agent");

        var diagnostic = ProtectedStateGuard.CheckProtectedState($"{IterationId}/tasks.xml", before, after);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(DiagnosticCodes.OwnerDecisionRequired, diagnostic.Code);
    }

    [TestMethod]
    public void LowLevelMutation_CannotForgeStructuredReviewState()
    {
        var before = XDocument.Load(TasksPath());
        FindTask(before).Element("review")!.Remove();
        var after = new XDocument(before);
        FindTask(after).Element("records")!.AddBeforeSelf(new XElement("review", new XAttribute("required", "true")));

        var diagnostic = ProtectedStateGuard.CheckProtectedState($"{IterationId}/tasks.xml", before, after);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(DiagnosticCodes.TaskReviewStateInvalid, diagnostic.Code);
    }

    [TestMethod]
    public void ReviewRejectsBackdatedTimeAndProjectWideElementIdCollision()
    {
        const string backdated = """
<task-review id="20260825T095900Z-review-backdated" actor="independent-reviewer" occurred_at="2026-08-25T09:59:00Z">
  <submission id="20260825T095900Z-review-backdated-submission" disposition="approved">
    <summary>Backdated approval must fail.</summary>
  </submission>
</task-review>
""";
        var (backdatedSuccess, _, backdatedDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, CurrentRevision(), backdated);
        Assert.IsFalse(backdatedSuccess);
        Assert.IsTrue(backdatedDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument), Join(backdatedDiagnostics));

        var existingId = XDocument.Load(TasksPath()).Root!.Elements("task")
            .First(t => (string?)t.Attribute("id") != TaskId)
            .Element("records")!.Elements("record").First().Attribute("id")!.Value;
        var collision = $"""
<task-review id="20260825T103200Z-review-id-collision" actor="independent-reviewer" occurred_at="2026-08-25T10:32:00Z">
  <submission id="{existingId}" disposition="approved">
    <summary>Colliding submission ID must fail deterministically.</summary>
  </submission>
</task-review>
""";
        var (collisionSuccess, _, collisionDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, CurrentRevision(), collision);
        Assert.IsFalse(collisionSuccess);
        Assert.IsTrue(collisionDiagnostics.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict), Join(collisionDiagnostics));
    }

    [TestMethod]
    public void ReviewOperation_CannotReuseProjectWideOperationId_AndApprovalRejectsImpact()
    {
        var document = XDocument.Load(TasksPath());
        var otherRecord = document.Root!.Elements("task")
            .First(t => (string?)t.Attribute("id") != TaskId)
            .Element("records")!.Elements("record").First();
        otherRecord.SetAttributeValue("operation_id", "20260825T103000Z-review-global-collision");
        document.Save(TasksPath());

        var (collisionSuccess, _, collisionDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, CurrentRevision(),
            ApprovalRequest("20260825T103000Z-review-global-collision", "independent-reviewer"));
        Assert.IsFalse(collisionSuccess);
        Assert.IsTrue(collisionDiagnostics.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict), Join(collisionDiagnostics));

        const string approvalWithImpact = """
<task-review id="20260825T103100Z-review-impact" actor="independent-reviewer" occurred_at="2026-08-25T10:31:00Z">
  <submission id="20260825T103100Z-review-impact-submission" disposition="approved">
    <summary>Approval must not carry changes-requested impact.</summary>
    <impact>This field is invalid for approval.</impact>
  </submission>
</task-review>
""";
        var (impactSuccess, _, impactDiagnostics) = TaskReviewer.Submit(
            _workspace, IterationId, TaskId, CurrentRevision(), approvalWithImpact);
        Assert.IsFalse(impactSuccess);
        Assert.IsTrue(impactDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument), Join(impactDiagnostics));
    }

    private void PrepareReviewTask()
    {
        var document = XDocument.Load(TasksPath());
        var task = FindTask(document);
        task.SetAttributeValue("status", "verification");
        task.SetAttributeValue("agent", Implementer);
        task.SetAttributeValue("updated_at", "2026-08-25T10:00:00Z");
        var statusTerm = task.Element("index")!.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        if (statusTerm == null) task.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "verification")));
        else statusTerm.SetAttributeValue("value", "verification");
        foreach (var criterion in task.Element("acceptance")!.Elements("criterion")) criterion.SetAttributeValue("status", "passed");
        task.Element("records")!.Elements("record")
            .Where(r => (string?)r.Attribute("kind") == "finding" && (string?)r.Attribute("status") == "active")
            .ToList().ForEach(r => r.SetAttributeValue("status", "resolved"));
        if (task.Element("review") == null) task.Element("records")!.AddBeforeSelf(new XElement("review", new XAttribute("required", "true")));
        document.Save(TasksPath());
    }

    private string CompletionRequest(string operationId)
    {
        var refs = string.Join(Environment.NewLine, LoadTask().Element("acceptance")!.Elements("criterion")
            .Select(c => $"        <ref scope=\"document\" target=\"{c.Attribute("id")!.Value}\" relation=\"covers\"/>"));
        return $"""
<task-update id="{operationId}" transition="complete" actor="implementation-agent" occurred_at="2026-08-25T10:30:00Z">
  <records>
    <record id="{operationId}-record" kind="completion" status="informational" created_at="2026-08-25T10:30:00Z" actor="implementation-agent" operation_id="{operationId}">
      <summary>Review-gated task completed.</summary>
      <covers>
{refs}
      </covers>
    </record>
  </records>
</task-update>
""";
    }

    private static string ApprovalRequest(string operationId, string actor) => $"""
<task-review id="{operationId}" actor="{actor}" occurred_at="2026-08-25T10:02:00Z">
  <submission id="{operationId}-submission" disposition="approved">
    <summary>Independent review approved the implementation.</summary>
  </submission>
</task-review>
""";

    private static string ChangesRequest(string operationId, string findingId) => $"""
<task-review id="{operationId}" actor="independent-reviewer" occurred_at="2026-08-25T10:10:00Z">
  <submission id="{operationId}-submission" disposition="changes-requested" finding_id="{findingId}">
    <summary>Review found a correction that blocks completion.</summary>
    <impact>The implementation must be corrected before approval.</impact>
  </submission>
</task-review>
""";

    private int CurrentRevision()
    {
        var document = XDocument.Load(TasksPath());
        return int.Parse(document.Root!.Attribute("revision")!.Value, System.Globalization.CultureInfo.InvariantCulture);
    }
    private XElement LoadTask() => FindTask(XDocument.Load(TasksPath()));
    private string TasksPath() => Path.Combine(_workspace, IterationId, "tasks.xml");
    private static XElement FindTask(XDocument document) => document.Root!.Elements("task").Single(t => (string?)t.Attribute("id") == TaskId);
    private static string Join(IReadOnlyList<DogdouSpec.Core.Diagnostics.Diagnostic> diagnostics) => string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
