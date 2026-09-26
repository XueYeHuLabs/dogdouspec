using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class ProgressionEngineCoreTests
{
    private static readonly string[] TestCriteria = new[] { "Substantive criterion for progression core test." };
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private const string TestIterationId = "20260908-progression-test";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ProgressionCore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _workspaceRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_workspaceRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);
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
    public void EmptyTaskSet_ReturnsNoTasksAction()
    {
        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(0, res.Facts.TotalTasks);
        Assert.AreEqual(ProgressionActionCategories.NoTasks, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.NoTasks, res.RecommendedAction.ReasonCode);
        Assert.IsFalse(res.IsExecutionTerminal);
        Assert.IsFalse(res.IsProductConfirmed);
    }

    [TestMethod]
    public void BlockedOnlyTasks_ReturnsWaitExternal_NotTerminal()
    {
        AddTaskToIteration("task-blocked", "blocked", hasActiveFinding: true);

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(1, res.Facts.TotalTasks);
        Assert.AreEqual(1, res.Facts.BlockedTasks);
        Assert.IsFalse(res.Facts.IsAllTerminal);
        Assert.IsFalse(res.Facts.IsTerminalIncomplete);
        Assert.AreEqual(ProgressionActionCategories.WaitExternal, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.TasksBlocked, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void BlockedTask_AllBlockersResolved_ReturnsResumeTask()
    {
        AddTaskToIteration("task-resumable", "blocked", hasActiveFinding: false, hasResolvedRecord: true);

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.ResumeTask, res.RecommendedAction.ActionCategory);
        Assert.AreEqual("task-resumable", res.RecommendedAction.TargetTaskId);
    }

    [TestMethod]
    public void BlockedAndDoneTasks_ReturnsWaitExternal_AndCalculatesCompletionPercentage()
    {
        AddTaskToIteration("task-done", "done");
        AddTaskToIteration("task-blocked", "blocked", hasActiveFinding: true);

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(2, res.Facts.TotalTasks);
        Assert.AreEqual(1, res.Facts.DoneTasks);
        Assert.AreEqual(1, res.Facts.BlockedTasks);
        Assert.AreEqual(50.0, res.Facts.CompletionPercentage);
        Assert.IsFalse(res.Facts.IsAllTerminal);
        Assert.AreEqual(ProgressionActionCategories.WaitExternal, res.RecommendedAction.ActionCategory);
    }

    [TestMethod]
    public void AllDoneTasks_ReturnsExecutionTerminal_AllTasksDone()
    {
        AddTaskToIteration("task-done-1", "done");
        AddTaskToIteration("task-done-2", "done");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(2, res.Facts.TotalTasks);
        Assert.AreEqual(2, res.Facts.DoneTasks);
        Assert.AreEqual(100.0, res.Facts.CompletionPercentage);
        Assert.IsTrue(res.Facts.IsAllTerminal);
        Assert.IsTrue(res.Facts.IsDeliverySuccessful);
        Assert.IsFalse(res.Facts.IsTerminalIncomplete);
        Assert.AreEqual(ProgressionActionCategories.ExecutionTerminal, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.AllTasksDone, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void TerminalIncomplete_OnlyCancelledTransferredSuperseded_DistinguishedFromSuccess()
    {
        AddTaskToIteration("task-cancelled", "cancelled");
        AddTaskToIteration("task-superseded", "superseded");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(2, res.Facts.TotalTasks);
        Assert.AreEqual(0, res.Facts.DoneTasks);
        Assert.AreEqual(2, res.Facts.TerminalTasks);
        Assert.AreEqual(0.0, res.Facts.CompletionPercentage);
        Assert.IsTrue(res.Facts.IsAllTerminal);
        Assert.IsFalse(res.Facts.IsDeliverySuccessful);
        Assert.IsTrue(res.Facts.IsTerminalIncomplete);
        Assert.AreEqual(ProgressionActionCategories.ExecutionTerminal, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.TasksTerminalIncomplete, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Lifecycle_Draft_OverridesActionToOwnerDecision()
    {
        // Set iteration status to draft
        SetIterationStatus("draft");
        AddTaskToIteration("task-pending", "pending");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual("draft", res.IterationStatus);
        Assert.AreEqual(ProgressionActionCategories.OwnerDecision, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.IterationDraft, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Lifecycle_Replanning_OverridesActionToOwnerDecision()
    {
        SetIterationStatus("replanning");
        AddTaskToIteration("task-in-progress", "in-progress");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual("replanning", res.IterationStatus);
        Assert.AreEqual(ProgressionActionCategories.OwnerDecision, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.IterationReplanning, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Lifecycle_Completed_OverridesActionToExecutionTerminal()
    {
        SetIterationStatus("completed");
        AddTaskToIteration("task-done", "done");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.ExecutionTerminal, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.IterationCompleted, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Lifecycle_Superseded_OverridesActionToExecutionTerminal()
    {
        SetIterationStatus("superseded");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.ExecutionTerminal, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.IterationSuperseded, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void ActiveTask_InProgress_ReturnsContinueWork()
    {
        AddTaskToIteration("task-in-progress", "in-progress");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.ContinueWork, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskInProgress, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void ActiveTask_Verification_ReturnsVerifyWork()
    {
        AddTaskToIteration("task-verification", "verification");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.VerifyWork, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskVerification, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void ReviewRequired_AwaitingSubmission_ReturnsReviewRequired()
    {
        AddTaskToIteration("task-review", "verification", reviewRequired: true, agent: "alice");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.ReviewRequired, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskReviewRequired, res.RecommendedAction.ReasonCode);
        Assert.AreEqual("reviewer", res.RecommendedAction.RequiredRole);
    }

    [TestMethod]
    public void ActiveFindings_ReturnsResolveFindings()
    {
        AddTaskToIteration("task-finding", "in-progress", hasActiveFinding: true);

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(1, res.Facts.ActiveFindingsCount);
        Assert.AreEqual(ProgressionActionCategories.ResolveFindings, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskActiveFindings, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void UnapprovedRequirement_ReturnsOwnerDecision()
    {
        // Add an unapproved (proposed) requirement to spec.xml
        var specPath = Path.Combine(_workspaceRoot, TestIterationId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        var reqsEl = specDoc.Root!.Element("product")!.Element("requirements")!;
        reqsEl.Add(new XElement("requirement",
            new XAttribute("id", "20260908-req-unapproved"),
            new XAttribute("status", "proposed"),
            new XElement("index", new XElement("summary", "Unapproved requirement")),
            new XElement("statement", "Unapproved requirement statement"),
            new XElement("rationale", "Testing unapproved req")));
        specDoc.Save(specPath);

        AddTaskToIteration("task-pending-unapproved", "pending", originReqId: "20260908-req-unapproved");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.OwnerDecision, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.RequirementNotApproved, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Dependencies_Satisfied_ReturnsStartWork()
    {
        AddTaskToIteration("task-pending-ready", "pending");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(res);
        Assert.AreEqual(ProgressionActionCategories.StartWork, res.RecommendedAction.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.PendingTaskReady, res.RecommendedAction.ReasonCode);
    }

    [TestMethod]
    public void Dependencies_StructuralError_Dangling_FailsClosed()
    {
        AddTaskToIteration("task-dangling", "pending", dependsOnTaskId: "20260908-task-nonexistent");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsFalse(ok);
        Assert.IsNull(res);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.DanglingReference));
    }

    [TestMethod]
    public void AgentFilter_FiltersCandidatesAndEnforcesIndependentReviewer()
    {
        AddTaskToIteration("task-alice", "in-progress", agent: "alice");
        AddTaskToIteration("task-bob", "verification", reviewRequired: true, agent: "bob");

        // Filter for alice:
        var (aliceOk, aliceRes, _) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId, agentFilter: "alice");
        Assert.IsTrue(aliceOk);
        Assert.IsNotNull(aliceRes);
        // Alice can work on task-alice and review task-bob (since bob != alice)
        Assert.IsTrue(aliceRes.ActionableCandidates.Any(c => c.TaskId == "task-alice"));
        Assert.IsTrue(aliceRes.ActionableCandidates.Any(c => c.TaskId == "task-bob"));

        // Filter for bob:
        var (bobOk, bobRes, _) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId, agentFilter: "bob");
        Assert.IsTrue(bobOk);
        Assert.IsNotNull(bobRes);
        // Bob cannot review his own task-bob!
        Assert.IsFalse(bobRes.ActionableCandidates.Any(c => c.TaskId == "task-bob"));
    }

    [TestMethod]
    public void Revisions_IncludesSpecAndTasksRevisions()
    {
        AddTaskToIteration("task-1", "pending");

        var (ok, res, diags) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);

        Assert.IsTrue(ok);
        Assert.IsNotNull(res);
        Assert.IsTrue(res.DocumentRevisions.Count >= 2);
        Assert.IsTrue(res.DocumentRevisions.Any(r => r.DocumentPath.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(res.DocumentRevisions.Any(r => r.DocumentPath.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ReadOnly_Guarantee_ManagedDocumentsUnchanged()
    {
        AddTaskToIteration("task-1", "pending");

        var tasksPath = Path.Combine(_workspaceRoot, TestIterationId, "tasks.xml");
        var beforeBytes = File.ReadAllBytes(tasksPath);

        var (ok, res, _) = ProgressionEngine.Assess(_workspaceRoot, TestIterationId);
        Assert.IsTrue(ok);

        var afterBytes = File.ReadAllBytes(tasksPath);
        CollectionAssert.AreEqual(beforeBytes, afterBytes);
    }

    private void AddTaskToIteration(
        string taskId,
        string status,
        bool reviewRequired = false,
        string? agent = null,
        bool hasActiveFinding = false,
        string? originReqId = null,
        string? dependsOnTaskId = null,
        bool hasResolvedRecord = false)
    {
        var tasksPath = Path.Combine(_workspaceRoot, TestIterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);

        var taskEl = new XElement("task",
            new XAttribute("id", taskId),
            new XAttribute("status", status),
            new XAttribute("created_at", "2026-09-08T00:00:00Z"),
            new XAttribute("updated_at", "2026-09-08T00:00:00Z"),
            new XElement("index",
                new XElement("summary", $"Task {taskId}"),
                new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "task")),
                new XElement("term", new XAttribute("key", "status"), new XAttribute("value", status))),
            new XElement("title", $"Task {taskId}"),
            new XElement("objective", $"Objective for {taskId}"),
            new XElement("rationale", "Rationale"),
            new XElement("scope", new XElement("repository", new XAttribute("path", "."))),
            new XElement("origin",
                new XElement("ref",
                    new XAttribute("scope", "iteration"),
                    new XAttribute("target", originReqId ?? "20260908-req-progression-test"),
                    new XAttribute("relation", "implements"))),
            new XElement("constraints"),
            new XElement("acceptance",
                new XElement("criterion", new XAttribute("id", $"crit-{taskId}"), new XAttribute("status", "pending"), "Criterion")),
            new XElement("context", new XElement("summary", "Context")));

        if (!string.IsNullOrEmpty(agent))
        {
            taskEl.SetAttributeValue("agent", agent);
        }

        if (!string.IsNullOrEmpty(dependsOnTaskId))
        {
            taskEl.Add(new XElement("dependencies",
                new XElement("ref",
                    new XAttribute("scope", "document"),
                    new XAttribute("target", dependsOnTaskId),
                    new XAttribute("relation", "depends-on"))));
        }

        if (reviewRequired)
        {
            taskEl.Add(new XElement("review", new XAttribute("required", "true")));
        }

        var recordsEl = new XElement("records");
        if (hasActiveFinding)
        {
            recordsEl.Add(new XElement("record",
                new XAttribute("id", $"{taskId}-finding-1"),
                new XAttribute("kind", "finding"),
                new XAttribute("status", "active"),
                new XAttribute("created_at", "2026-09-08T00:00:00Z"),
                new XAttribute("actor", "tester"),
                new XElement("summary", "Active test finding")));
        }
        if (hasResolvedRecord)
        {
            recordsEl.Add(new XElement("record",
                new XAttribute("id", $"{taskId}-res-1"),
                new XAttribute("kind", "resolution"),
                new XAttribute("status", "resolved"),
                new XAttribute("created_at", "2026-09-08T00:00:00Z"),
                new XAttribute("actor", "tester"),
                new XElement("summary", "Resolved test blocker")));
        }
        taskEl.Add(recordsEl);

        tasksDoc.Root!.Add(taskEl);
        tasksDoc.Save(tasksPath);
    }

    private void SetIterationStatus(string status)
    {
        var specPath = Path.Combine(_workspaceRoot, TestIterationId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root!.SetAttributeValue("status", status);
        specDoc.Save(specPath);
    }
}
